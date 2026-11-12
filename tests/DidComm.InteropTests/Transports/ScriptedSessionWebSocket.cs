using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using DidComm.Transports.ReturnRoute;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// An in-memory <see cref="WebSocket"/> for the #92 session-socket tests (G8): the test scripts what
/// the peer sends (<see cref="PushMessage"/>, <see cref="PushFragments"/>, <see cref="PushClose"/>),
/// can hold the transport's sends at a gate (<see cref="HoldSends"/> / <see cref="ReleaseSends"/>),
/// make the next send fail, and inspect what was sent, how many sends overlapped, the close-output
/// status and how many bytes had been read when it was sent. Like a real socket it allows one
/// receive and one send at a time, refuses sends after its close output, wakes a pending receive on
/// dispose, and (like <c>ManagedWebSocket</c>) aborts when a receive is cancelled.
/// </summary>
internal sealed class ScriptedSessionWebSocket : WebSocket
{
    private readonly Channel<Inbound> _inbound = Channel.CreateUnbounded<Inbound>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _sync = new();
    private readonly ConcurrentQueue<byte[]> _sent = new();
    private WebSocketState _state = WebSocketState.Open;
    private Inbound? _current;
    private int _currentOffset;
    private long _bytesDelivered;
    private int _largestRead;
    private int _sendsInFlight;
    private int _maxSendsInFlight;
    private TaskCompletionSource? _sendGate;
    private TaskCompletionSource _sendEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _failNextSend;
    private WebSocketCloseStatus? _closeOutputStatus;
    private string? _closeOutputDescription;
    private long _bytesAtCloseOutput = -1;
    private WebSocketCloseStatus? _closeStatus;
    private string? _closeStatusDescription;

    /// <summary>The order the factory created this socket in (0-based).</summary>
    public int Index { get; init; }

    /// <summary>When <c>true</c> (the default) the peer answers our close output with its own close frame.</summary>
    public bool AnswerCloseOutput { get; set; } = true;

    /// <summary>When <c>true</c> our close output never completes until its token is cancelled (a peer that stopped reading).</summary>
    public bool CloseOutputHangs { get; set; }

    /// <summary>Every payload whose send completed, in order.</summary>
    public IReadOnlyList<byte[]> Sent => _sent.ToArray();

    /// <summary>The most sends that were in progress at the same time.</summary>
    public int MaxSendsInFlight => Volatile.Read(ref _maxSendsInFlight);

    /// <summary>Sends in progress now.</summary>
    public int SendsInFlight => Volatile.Read(ref _sendsInFlight);

    /// <summary>Completes when a send entered while sends are held (re-armed by <see cref="HoldSends"/>).</summary>
    public Task SendEntered
    {
        get { lock (_sync) return _sendEntered.Task; }
    }

    /// <summary>The status of our close output, once sent.</summary>
    public WebSocketCloseStatus? CloseOutputStatus
    {
        get { lock (_sync) return _closeOutputStatus; }
    }

    /// <summary>The description of our close output, once sent.</summary>
    public string? CloseOutputDescription
    {
        get { lock (_sync) return _closeOutputDescription; }
    }

    /// <summary>Bytes the transport had read when it sent its close output; -1 before that.</summary>
    public long BytesReadAtCloseOutput => Interlocked.Read(ref _bytesAtCloseOutput);

    /// <summary>Bytes the transport has read so far.</summary>
    public long BytesRead => Interlocked.Read(ref _bytesDelivered);

    /// <summary>The largest single read the transport made (its receive buffer, at most).</summary>
    public int LargestRead => Volatile.Read(ref _largestRead);

    /// <summary>Whether <see cref="Dispose"/> ran.</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc />
    public override WebSocketCloseStatus? CloseStatus
    {
        get { lock (_sync) return _closeStatus; }
    }

    /// <inheritdoc />
    public override string? CloseStatusDescription
    {
        get { lock (_sync) return _closeStatusDescription; }
    }

    /// <inheritdoc />
    public override WebSocketState State
    {
        get { lock (_sync) return _state; }
    }

    /// <inheritdoc />
    public override string? SubProtocol => null;

    /// <summary>The peer sends one whole message.</summary>
    public void PushMessage(byte[] bytes) => _inbound.Writer.TryWrite(new Inbound(bytes, EndOfMessage: true, Close: null));

    /// <summary>The peer sends one message as fragments of <paramref name="fragmentSize"/> bytes.</summary>
    public void PushFragments(byte[] bytes, int fragmentSize)
    {
        for (var offset = 0; offset < bytes.Length; offset += fragmentSize)
        {
            var length = Math.Min(fragmentSize, bytes.Length - offset);
            _inbound.Writer.TryWrite(new Inbound(bytes.AsSpan(offset, length).ToArray(), EndOfMessage: offset + length >= bytes.Length, Close: null));
        }
    }

    /// <summary>The peer sends its close frame.</summary>
    public void PushClose(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure, string? description = "peer closed")
        => _inbound.Writer.TryWrite(new Inbound(Array.Empty<byte>(), EndOfMessage: true, Close: (status, description)));

    /// <summary>Park every send until <see cref="ReleaseSends"/>; <see cref="SendEntered"/> completes when one arrives.</summary>
    public void HoldSends()
    {
        lock (_sync)
        {
            _sendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _sendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Let held sends complete; later sends are not held.</summary>
    public void ReleaseSends()
    {
        TaskCompletionSource? gate;
        lock (_sync)
        {
            gate = _sendGate;
            _sendGate = null;
        }
        gate?.TrySetResult();
    }

    /// <summary>The next send throws a <see cref="WebSocketException"/> (a dead connection).</summary>
    public void FailNextSend() => Volatile.Write(ref _failNextSend, 1);

    /// <inheritdoc />
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_state is WebSocketState.Closed or WebSocketState.Aborted, this);
            if (_state == WebSocketState.CloseReceived)
                throw new InvalidOperationException("The peer's close frame was already received.");
        }

        Inbound item;
        if (_current is { } partial)
        {
            item = partial;
        }
        else
        {
            try
            {
                item = await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ManagedWebSocket aborts the connection when a pending receive is cancelled.
                Abort();
                throw;
            }
            catch (ChannelClosedException)
            {
                throw new ObjectDisposedException(nameof(ScriptedSessionWebSocket));
            }
            _current = item;
            _currentOffset = 0;
        }

        if (item.Close is { } close)
        {
            _current = null;
            lock (_sync)
            {
                _closeStatus = close.Status;
                _closeStatusDescription = close.Description;
                if (_state == WebSocketState.Open)
                    _state = WebSocketState.CloseReceived;
                else if (_state == WebSocketState.CloseSent)
                    _state = WebSocketState.Closed;
            }
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, close.Status, close.Description);
        }

        var count = Math.Min(buffer.Count, item.Bytes.Length - _currentOffset);
        Array.Copy(item.Bytes, _currentOffset, buffer.Array!, buffer.Offset, count);
        _currentOffset += count;
        Interlocked.Add(ref _bytesDelivered, count);
        int seen;
        while ((seen = Volatile.Read(ref _largestRead)) < count && Interlocked.CompareExchange(ref _largestRead, count, seen) != seen)
        {
        }
        var done = _currentOffset >= item.Bytes.Length;
        if (done)
            _current = null;
        return new WebSocketReceiveResult(count, WebSocketMessageType.Binary, done && item.EndOfMessage);
    }

    /// <inheritdoc />
    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        TaskCompletionSource? gate;
        TaskCompletionSource entered;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_state is WebSocketState.Closed or WebSocketState.Aborted, this);
            if (_state == WebSocketState.CloseSent)
                throw new InvalidOperationException("The close output was already sent.");
            gate = _sendGate;
            entered = _sendEntered;
        }

        var inFlight = Interlocked.Increment(ref _sendsInFlight);
        int seen;
        while ((seen = Volatile.Read(ref _maxSendsInFlight)) < inFlight && Interlocked.CompareExchange(ref _maxSendsInFlight, inFlight, seen) != seen)
        {
        }
        try
        {
            if (Interlocked.Exchange(ref _failNextSend, 0) == 1)
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "scripted send failure");
            var copy = buffer.AsSpan().ToArray();
            if (gate is not null)
            {
                entered.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            lock (_sync)
                ObjectDisposedException.ThrowIf(_state is WebSocketState.Closed or WebSocketState.Aborted, this);
            _sent.Enqueue(copy);
        }
        finally
        {
            Interlocked.Decrement(ref _sendsInFlight);
        }
    }

    /// <inheritdoc />
    public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_state is WebSocketState.Closed or WebSocketState.Aborted, this);
            if (_state == WebSocketState.CloseSent)
                throw new InvalidOperationException("The close output was already sent.");
            _closeOutputStatus = closeStatus;
            _closeOutputDescription = statusDescription;
            Interlocked.Exchange(ref _bytesAtCloseOutput, Interlocked.Read(ref _bytesDelivered));
        }
        if (CloseOutputHangs)
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            if (_state == WebSocketState.Open)
                _state = WebSocketState.CloseSent;
            else if (_state == WebSocketState.CloseReceived)
                _state = WebSocketState.Closed;
        }
        if (AnswerCloseOutput)
            PushClose(closeStatus, "answer");
    }

    /// <inheritdoc />
    public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => await CloseOutputAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public override void Abort()
    {
        lock (_sync)
        {
            if (_state is WebSocketState.Closed or WebSocketState.Aborted)
                return;
            _state = WebSocketState.Aborted;
        }
        _inbound.Writer.TryComplete();
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        lock (_sync)
        {
            Disposed = true;
            if (_state is not (WebSocketState.Closed or WebSocketState.Aborted))
                _state = WebSocketState.Closed;
        }
        _inbound.Writer.TryComplete();
    }

    private sealed record Inbound(byte[] Bytes, bool EndOfMessage, (WebSocketCloseStatus Status, string? Description)? Close);
}

/// <summary>Creates <see cref="ScriptedSessionWebSocket"/>s for a transport and remembers them in order.</summary>
internal sealed class ScriptedSocketFactory
{
    private readonly ConcurrentQueue<ScriptedSessionWebSocket> _created = new();
    private int _count;

    /// <summary>Runs on every new socket before the transport sees it (configure it, record state).</summary>
    public Action<ScriptedSessionWebSocket>? OnCreate { get; set; }

    /// <summary>Wraps each creation (e.g. a rendezvous); <c>null</c> creates directly.</summary>
    public Func<Func<ScriptedSessionWebSocket>, ScriptedSessionWebSocket>? Around { get; set; }

    /// <summary>Every socket created, in order.</summary>
    public IReadOnlyList<ScriptedSessionWebSocket> Sockets => _created.OrderBy(s => s.Index).ToArray();

    /// <summary>How many sockets were created.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>The delegate for <c>WebSocketTransportOptions.WebSocketFactory</c>.</summary>
    public WebSocket Create()
    {
        ScriptedSessionWebSocket Make()
        {
            var socket = new ScriptedSessionWebSocket { Index = Interlocked.Increment(ref _count) - 1 };
            OnCreate?.Invoke(socket);
            _created.Enqueue(socket);
            return socket;
        }
        return Around is null ? Make() : Around(Make);
    }
}

/// <summary>An <see cref="IInboundFrameConsumer"/> that records frames and runs an optional per-frame behaviour.</summary>
internal sealed class RecordingFrameConsumer : IInboundFrameConsumer
{
    private readonly ConcurrentQueue<InboundFrame> _frames = new();
    private int _calls;

    /// <summary>Runs on every frame after it was recorded; may throw or park.</summary>
    public Func<InboundFrame, int, CancellationToken, ValueTask>? Behavior { get; set; }

    /// <summary>Every frame received, in order.</summary>
    public IReadOnlyList<InboundFrame> Frames => _frames.ToArray();

    /// <summary>Calls so far.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <inheritdoc />
    public ValueTask ConsumeAsync(InboundFrame frame, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _calls);
        _frames.Enqueue(frame);
        return Behavior is null ? ValueTask.CompletedTask : Behavior(frame, n, ct);
    }
}
