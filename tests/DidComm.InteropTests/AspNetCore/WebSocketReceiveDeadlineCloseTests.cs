using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.Transports.Stomp;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// The close handshakes the WebSocket endpoints start — the 1008 of an expired message, the 1009 of an
/// oversized one, and a STOMP protocol-error close — against a raw peer that reads the server's close
/// frame and never answers it. Each must end the connection within the close grace (5 s): the server
/// aborts it, so the peer sees the TCP connection end. Without the bound the connection would stay
/// open for as long as the peer likes, holding the request and its buffers.
/// </summary>
/// <remarks>
/// The grace is real time (the product's own), so each test takes about 5 s; the 30 s read is only a
/// hang guard on an event that is certain with a correct product.
/// </remarks>
[Collection(QaSerialTiming.Name)]
public sealed class WebSocketReceiveDeadlineCloseTests
{
    private static readonly TimeSpan EndGuard = TimeSpan.FromSeconds(30);

    public static TheoryData<DeadlineEndpoint> Endpoints => new()
    {
        DeadlineEndpoint.Dispatch,
        DeadlineEndpoint.Callback,
        DeadlineEndpoint.Mediator,
    };

    public static TheoryData<DeadlineEndpoint> StompEndpoints => new()
    {
        DeadlineEndpoint.Dispatch,
        DeadlineEndpoint.Callback,
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_1008_close_the_peer_never_answers_ends_the_connection_within_the_close_grace(DeadlineEndpoint endpoint)
    {
        await using var host = await DeadlineKestrelHost.StartAsync();
        var mark = host.Time.Mark();
        await using var peer = await host.ConnectRawAsync(endpoint);
        await host.Time.WaitForTimerAsync(mark, DeadlineKestrelHost.Limit, DeadlineKestrelHost.Guard);
        host.Time.Advance(DeadlineKestrelHost.Limit);

        var close = await peer.ReadCloseAsync(DeadlineKestrelHost.Guard);
        close.Should().NotBeNull("the server sends its close frame first");
        close!.CloseCode.Should().Be((int)WebSocketCloseStatus.PolicyViolation);

        // The peer never answers: the server gives up after the grace and drops the connection.
        await peer.ReadToEndAsync(EndGuard);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_1009_close_the_peer_never_answers_ends_the_connection_within_the_close_grace(DeadlineEndpoint endpoint)
    {
        await using var host = await DeadlineKestrelHost.StartAsync(); // MaxReceiveBytes 64 KiB
        await using var peer = await host.ConnectRawAsync(endpoint);
        await peer.SendFrameAsync(RawFrame.Text, fin: true, Encoding.ASCII.GetBytes(new string('a', 80 * 1024)));

        var close = await peer.ReadCloseAsync(DeadlineKestrelHost.Guard);
        close.Should().NotBeNull();
        close!.CloseCode.Should().Be((int)WebSocketCloseStatus.MessageTooBig);

        await peer.ReadToEndAsync(EndGuard);
    }

    [Theory]
    [MemberData(nameof(StompEndpoints))]
    public async Task A_STOMP_protocol_error_close_the_peer_never_answers_ends_the_connection_within_the_close_grace(DeadlineEndpoint endpoint)
    {
        await using var host = await DeadlineKestrelHost.StartAsync(o => o.Receive = r =>
        {
            r.MessageReceiveTimeout = DeadlineKestrelHost.Limit;
            r.UseStomp = true;
        });
        await using var peer = await host.ConnectRawAsync(endpoint);
        var subscribe = new StompFrame("SUBSCRIBE", new[] { KeyValuePair.Create("destination", "/x"), KeyValuePair.Create("id", "0") }, ReadOnlyMemory<byte>.Empty);
        await peer.SendFrameAsync(RawFrame.Binary, fin: true, StompFrameCodec.Encode(subscribe));

        var close = await peer.ReadCloseAsync(DeadlineKestrelHost.Guard);
        close.Should().NotBeNull();
        close!.CloseCode.Should().Be((int)WebSocketCloseStatus.ProtocolError, "a broker verb is a STOMP protocol error");

        await peer.ReadToEndAsync(EndGuard);
    }
}

/// <summary>
/// The last step of both receive loops (<c>WebSocketClosing.EndAsync</c>) against a scripted socket:
/// the receive buffer goes back to its pool only once no receive into it can still be running, the
/// close is sent with <c>CloseOutputAsync</c> (never <c>CloseAsync</c>, which would start a second
/// receive), and a peer that does not answer within the grace is aborted.
/// </summary>
public sealed class WebSocketClosingTests
{
    private static readonly TimeSpan ShortGrace = TimeSpan.FromMilliseconds(50);

    // Real-time hang guard: each call ends within its grace with a correct product.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task The_buffer_is_not_returned_to_its_pool_while_a_receive_into_it_never_completes()
    {
        var socket = new ScriptedSocket(completeReceivesOnAbort: false, answerClose: false);
        var pool = new RecordingPool();
        var buffer = new byte[8192];
        var outstanding = socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

        var returned = await WebSocketClosing.EndAsync(socket, WebSocketCloseStatus.PolicyViolation, "message receive timeout", outstanding, buffer, pool, ShortGrace, CancellationToken.None, logger: null).WaitAsync(HangGuard);

        returned.Should().BeFalse();
        pool.Returned.Should().BeEmpty("the receive may still write into the buffer");
        outstanding.IsCompleted.Should().BeFalse();
        socket.SentClose.Should().Be(WebSocketCloseStatus.PolicyViolation, "the close frame goes out while the receive is outstanding");
        socket.CloseAsyncCalls.Should().Be(0);
        socket.ReceiveCalls.Should().Be(1, "the outstanding receive is awaited, not joined by a second one");
        socket.Aborted.Should().BeTrue("a peer that never answers within the grace is aborted");
    }

    [Fact]
    public async Task The_buffer_goes_back_to_its_pool_once_the_outstanding_receive_has_completed()
    {
        var socket = new ScriptedSocket(completeReceivesOnAbort: true, answerClose: false);
        var pool = new RecordingPool();
        var buffer = new byte[8192];
        var outstanding = socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

        var returned = await WebSocketClosing.EndAsync(socket, WebSocketCloseStatus.PolicyViolation, "message receive timeout", outstanding, buffer, pool, ShortGrace, CancellationToken.None, logger: null).WaitAsync(HangGuard);

        returned.Should().BeTrue();
        pool.Returned.Should().ContainSingle().Which.Should().BeSameAs(buffer);
        socket.Aborted.Should().BeTrue();
    }

    [Fact]
    public async Task A_close_the_peer_answers_completes_the_handshake_without_an_abort_and_returns_the_buffer()
    {
        var socket = new ScriptedSocket(completeReceivesOnAbort: false, answerClose: true);
        var pool = new RecordingPool();
        var buffer = new byte[8192];

        var returned = await WebSocketClosing.EndAsync(socket, WebSocketCloseStatus.MessageTooBig, "payload too large", outstanding: null, buffer, pool, TimeSpan.FromSeconds(30), CancellationToken.None, logger: null).WaitAsync(HangGuard);

        returned.Should().BeTrue();
        socket.SentClose.Should().Be(WebSocketCloseStatus.MessageTooBig);
        socket.State.Should().Be(WebSocketState.Closed);
        socket.Aborted.Should().BeFalse();
        pool.Returned.Should().ContainSingle().Which.Should().BeSameAs(buffer);
    }

    [Fact]
    public async Task With_no_close_to_send_and_no_receive_outstanding_the_buffer_is_returned_and_the_socket_untouched()
    {
        var socket = new ScriptedSocket(completeReceivesOnAbort: false, answerClose: false);
        var pool = new RecordingPool();
        var buffer = new byte[8192];

        var returned = await WebSocketClosing.EndAsync(socket, status: null, description: null, outstanding: null, buffer, pool, ShortGrace, CancellationToken.None, logger: null).WaitAsync(HangGuard);

        returned.Should().BeTrue();
        pool.Returned.Should().ContainSingle();
        socket.SentClose.Should().BeNull();
        socket.ReceiveCalls.Should().Be(0);
        socket.Aborted.Should().BeFalse();
    }

    private sealed class RecordingPool : ArrayPool<byte>
    {
        public List<byte[]> Returned { get; } = new();

        public override byte[] Rent(int minimumLength) => new byte[minimumLength];

        public override void Return(byte[] array, bool clearArray = false) => Returned.Add(array);
    }

    /// <summary>
    /// A socket whose receives never complete on their own; <c>CloseOutputAsync</c> moves it to
    /// CloseSent, and with <c>answerClose</c> the next receive returns the peer's close frame.
    /// </summary>
    private sealed class ScriptedSocket : WebSocket
    {
        private readonly bool _completeReceivesOnAbort;
        private readonly bool _answerClose;
        private readonly List<TaskCompletionSource<WebSocketReceiveResult>> _pending = new();
        private WebSocketState _state = WebSocketState.Open;

        public ScriptedSocket(bool completeReceivesOnAbort, bool answerClose)
        {
            _completeReceivesOnAbort = completeReceivesOnAbort;
            _answerClose = answerClose;
        }

        public WebSocketCloseStatus? SentClose { get; private set; }

        public bool Aborted { get; private set; }

        public int CloseAsyncCalls { get; private set; }

        public int ReceiveCalls { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override void Abort()
        {
            Aborted = true;
            _state = WebSocketState.Aborted;
            if (_completeReceivesOnAbort)
            {
                foreach (var pending in _pending)
                    pending.TrySetException(new OperationCanceledException("aborted"));
            }
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            CloseAsyncCalls++;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            SentClose = closeStatus;
            _state = _state == WebSocketState.CloseReceived ? WebSocketState.Closed : WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            ReceiveCalls++;
            if (_answerClose && _state == WebSocketState.CloseSent)
            {
                _state = WebSocketState.Closed;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "bye"));
            }
            var pending = new TaskCompletionSource<WebSocketReceiveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(pending);
            return pending.Task;
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
