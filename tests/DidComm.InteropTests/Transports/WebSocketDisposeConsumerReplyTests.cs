using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using DidComm.Messages;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Xunit;
using Kind = DidComm.InteropTests.Transports.WebSocketDisposeCloseWaitTests.Kind;
using Live = DidComm.InteropTests.Transports.WebSocketDisposeCloseWaitTests.Live;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// #100 OD-7: a reply the inbound consumer is sending when dispose begins is drained, not cut off.
/// The consumer replies with the token it was given (as a handler under <c>UseInboundDispatch()</c>
/// does), and that token is linked into the reply's send. Dispose stops new messages reaching the
/// consumer at once, but cancels a consumer call in progress, on active and retiring session
/// sockets alike, only once every endpoint has drained, whichever endpoint the reply goes to. So
/// the reply reaches the peer whole and the socket it went on is closed politely. Real
/// Kestrel peer writing raw frames; the transport's sockets are real <see cref="ClientWebSocket"/>s
/// behind a wrapper that holds the reply's send at a gate, and aborts the socket, as a cancelled
/// <see cref="ClientWebSocket"/> send does, if the send's token is cancelled while it is held.
/// </summary>
public sealed class WebSocketDisposeConsumerReplyTests
{
    private static readonly TimeSpan Guard = WebSocketDisposeCloseWaitTests.Guard;
    private const string Reply = "the reply";

    /// <summary>Where the consumer's reply goes.</summary>
    public enum ReplyRoute
    {
        /// <summary>The message came on an active session socket; the header-less reply goes back on it.</summary>
        OnTheActiveSession,

        /// <summary>The message came on a retiring session socket; the header-less reply goes on a pooled socket.</summary>
        OnThePooledSocketFromARetiringSession,
    }

    public static TheoryData<ReplyRoute> Routes => new() { ReplyRoute.OnTheActiveSession, ReplyRoute.OnThePooledSocketFromARetiringSession };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_reply_the_consumer_is_sending_when_dispose_begins_reaches_the_peer_whole_and_its_socket_is_closed_politely(ReplyRoute route)
    {
        var held = new HeldReply();
        await using var t = await Live.StartAsync(o =>
        {
            var factory = o.WebSocketFactory!;
            o.WebSocketFactory = () => new HeldReplyWebSocket((ClientWebSocket)factory(), held);
            o.Connect = (socket, uri, ct) => ((HeldReplyWebSocket)socket).Inner.ConnectAsync(uri, ct);
        });
        var session = await t.OpenAsync(Kind.Session);
        if (route == ReplyRoute.OnThePooledSocketFromARetiringSession)
        {
            (await t.Transport.SendAsync(Request(t, "retire", ReturnRouteMode.None), default).WaitAsync(Guard)).Accepted.Should().BeTrue();
            Encoding.UTF8.GetString(await session.NextMessageAsync(Guard)).Should().Be("retire");
            (await WebSocketDisposeCloseWaitTests.NextCloseAsync(session)).CloseReason.Should().Be("return-route session ended");
            t.Transport.ActiveSessionSocketCount.Should().Be(0, "precondition: the session socket is retiring");
            t.Transport.LiveSessionSocketCount.Should().Be(1, "precondition: it is still read");
        }

        // The peer pushes a message; the consumer replies with its own token, and the reply's send is
        // held: it is in flight, holding its socket's send gate, when dispose begins.
        Task<TransportResult>? replying = null;
        t.Consumer.Behavior = (frame, _, ct) =>
        {
            if (Encoding.UTF8.GetString(frame.Payload.Span) != "request")
                return ValueTask.CompletedTask;
            replying = t.Transport.SendAsync(Request(t, Reply, mode: null), ct);
            return new ValueTask(replying);
        };
        await session.SendTextAsync(Encoding.UTF8.GetBytes("request"), endOfMessage: true);
        var replyPeer = route == ReplyRoute.OnTheActiveSession ? session : await t.Peer.NextConnectionAsync(Guard);
        await held.Entered.WaitAsync(Guard);

        // DisposeAsync runs to its drain before it returns this task: whatever it does to the
        // consumer's token at its start has been done when the reply is let go.
        var dispose = t.Transport.DisposeAsync().AsTask();
        held.Release();

        Encoding.UTF8.GetString(await replyPeer.NextMessageAsync(Guard)).Should().Be(Reply, "the reply was drained, not cut off");
        var close = await WebSocketDisposeCloseWaitTests.NextCloseAsync(replyPeer);
        close.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure, "the socket the reply went on is closed politely, not aborted");
        close.CloseReason.Should().Be("transport disposed");
        await replyPeer.SendCloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
        replyPeer.End();
        if (!ReferenceEquals(replyPeer, session))
            session.End(); // the retiring socket's peer never answered the retire; the sweep ends it

        await dispose.WaitAsync(Guard);
        (await replying!.WaitAsync(Guard)).Accepted.Should().BeTrue("the consumer's reply send completed");
        held.CancelledWhileHeld.Should().BeFalse();
        t.Consumer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_reply_to_another_endpoint_that_is_still_draining_when_dispose_begins_reaches_it_whole_and_every_close_is_polite()
    {
        // The consumer of session socket A replies to another endpoint, B (a pooled socket of its
        // own), with its own token. The reply is on a slow link: it holds B's send gate for
        // SlowLink. A's drain has nothing to wait for, B's waits for the reply: no consumer token may
        // be cancelled until every endpoint has drained, or this reply, still in flight on B, is cut
        // off. The only real time is the link's speed; the correct order does not depend on it.
        var held = new HeldReply(SlowLink);
        await using var t = await Live.StartAsync(o =>
        {
            var factory = o.WebSocketFactory!;
            o.WebSocketFactory = () => new HeldReplyWebSocket((ClientWebSocket)factory(), held);
            o.Connect = (socket, uri, ct) => ((HeldReplyWebSocket)socket).Inner.ConnectAsync(uri, ct);
        });
        var a = await t.OpenAsync(Kind.Session);
        var endpointB = new Uri(t.Peer.Endpoint + "?endpoint=b");

        Task<TransportResult>? replying = null;
        t.Consumer.Behavior = (frame, _, ct) =>
        {
            if (Encoding.UTF8.GetString(frame.Payload.Span) != "request")
                return ValueTask.CompletedTask;
            replying = t.Transport.SendAsync(new TransportRequest(endpointB, Encoding.UTF8.GetBytes(Reply), MediaTypes.Encrypted), ct);
            return new ValueTask(replying);
        };
        await a.SendTextAsync(Encoding.UTF8.GetBytes("request"), endOfMessage: true);
        var b = await t.Peer.NextConnectionAsync(Guard);
        await held.Entered.WaitAsync(Guard);

        var dispose = t.Transport.DisposeAsync().AsTask();

        Encoding.UTF8.GetString(await b.NextMessageAsync(Guard)).Should().Be(Reply, "B's drain let the reply finish: nothing cancelled it while it was in flight");
        var closeB = await WebSocketDisposeCloseWaitTests.NextCloseAsync(b);
        closeB.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure, "B is closed politely, not aborted");
        var closeA = await WebSocketDisposeCloseWaitTests.NextCloseAsync(a);
        closeA.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure, "A is closed politely");
        foreach (var peer in new[] { a, b })
        {
            await peer.SendCloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
            peer.End();
        }

        await dispose.WaitAsync(Guard);
        (await replying!.WaitAsync(Guard)).Accepted.Should().BeTrue("the consumer's reply send completed");
        held.CancelledWhileHeld.Should().BeFalse();
        t.Sockets.Should().HaveCount(2).And.OnlyContain(s => s.State == WebSocketState.Closed, "both close handshakes completed");
        t.Events.Count(e => e == DidComm.Transports.WebSocket.WebSocketLifecycleEventKind.Disconnected).Should().Be(2, "one Disconnected per socket");
    }

    // The reply's link speed in the cross-endpoint test: the reply holds B's gate this long.
    private static readonly TimeSpan SlowLink = TimeSpan.FromMilliseconds(500);

    // To the peer, between the DIDs the session socket was opened for (Live.OpenAsync's).
    private static TransportRequest Request(Live t, string text, ReturnRouteMode? mode)
        => new(t.Peer.Endpoint, Encoding.UTF8.GetBytes(text), MediaTypes.Encrypted)
        {
            ReturnRoute = mode,
            LocalDid = "did:example:alice",
            PeerDid = "did:example:bob",
        };

    /// <summary>The held reply's gate, shared by every socket the transport opens.</summary>
    private sealed class HeldReply(TimeSpan? slowLink = null)
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _cancelled;

        /// <summary>The reply's send reached the socket and is held.</summary>
        public Task Entered => _entered.Task;

        public bool CancelledWhileHeld => _cancelled;

        public void Release() => _release.TrySetResult();

        public async Task HoldAsync(CancellationToken ct)
        {
            _entered.TrySetResult();
            try
            {
                if (slowLink is { } delay)
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                else
                    await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _cancelled = true;
                throw;
            }
        }
    }

    /// <summary>
    /// A real <see cref="ClientWebSocket"/> whose send of the reply waits at <see cref="HeldReply"/>;
    /// a cancellation while it waits aborts the socket, as a cancelled send of a real one does.
    /// </summary>
    private sealed class HeldReplyWebSocket(ClientWebSocket inner, HeldReply held) : WebSocket
    {
        public ClientWebSocket Inner { get; } = inner;

        public override WebSocketCloseStatus? CloseStatus => Inner.CloseStatus;
        public override string? CloseStatusDescription => Inner.CloseStatusDescription;
        public override WebSocketState State => Inner.State;
        public override string? SubProtocol => Inner.SubProtocol;

        public override void Abort() => Inner.Abort();
        public override void Dispose() => Inner.Dispose();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Inner.CloseAsync(closeStatus, statusDescription, cancellationToken);
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => Inner.ReceiveAsync(buffer, cancellationToken);
        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) => Inner.ReceiveAsync(buffer, cancellationToken);

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => SendAsync(buffer.AsMemory(), messageType, endOfMessage, cancellationToken).AsTask();

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, WebSocketMessageFlags messageFlags, CancellationToken cancellationToken)
            => SendAsync(buffer, messageType, messageFlags.HasFlag(WebSocketMessageFlags.EndOfMessage), cancellationToken);

        public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (messageType == WebSocketMessageType.Text && buffer.Span.SequenceEqual(Encoding.UTF8.GetBytes(Reply)))
            {
                try
                {
                    await held.HoldAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Inner.Abort();
                    throw;
                }
            }
            await Inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
        }
    }
}
