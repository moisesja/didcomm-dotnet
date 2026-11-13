using System.Net.WebSockets;
using System.Threading.Channels;
using DidComm.Diagnostics;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.Logging;

namespace DidComm.AspNetCore;

/// <summary>
/// The return-route session of one WebSocket connection (FR-RR-05): opened at the connection's
/// first eligible message, registered in the <see cref="IReturnRouteSessionRegistry"/>, and alive
/// until the connection ends or the registry evicts it. Pushes are packed by the caller's task and
/// queued to ONE writer task, the only code that ever sends on (or close-outputs) the socket, so
/// frames never interleave and the receive loop never overlaps a send (JC-9).
/// </summary>
/// <remarks>
/// <para>
/// Push capacity is reserved before any work: a send
/// (<see cref="TrySendAsync(Message, int, CancellationToken)"/>, which the two-argument overload
/// calls) takes one of <see cref="ReturnRouteSessionOptions.MaxPendingPushes"/> slots (a lock-free
/// decrement that never goes below zero) before the snapshot, and is refused at once when none is
/// free — no snapshot, no key resolution, no encryption. The slot is given back on every path that
/// does not queue a frame (refused addressing, failed snapshot or pack, a packed frame over the
/// caller's size bound, a downgrade or close that landed during the pack, a refused write,
/// cancellation or an exception); a queued frame keeps it until the
/// writer takes the frame off the queue, whether it then sends or discards it. So at any moment the
/// pushes being snapshotted or packed plus the frames queued number at most
/// <see cref="ReturnRouteSessionOptions.MaxPendingPushes"/>; the one frame the writer is sending is
/// not counted. The bounded channel stays as a second limit with the same depth.
/// </para>
/// <para>
/// <see cref="Mode"/>, <see cref="Thread"/> and the closed state are read from other threads
/// (handlers using <see cref="IReturnRouteSessionRegistry.Find"/>), so they are volatile; the
/// closed state is set once, atomically. A closed session never reopens: <see cref="ApplyHeader"/>
/// is a no-op on it and <see cref="Mode"/> reports <see cref="ReturnRouteMode.None"/> (PA-3).
/// </para>
/// <para>
/// On close for any reason queued pushes are discarded — <c>true</c> from
/// <see cref="TrySendAsync(Message, CancellationToken)"/> (<see cref="ReturnRouteSendResult.Sent"/>
/// from the size-bounded overload) meant queued, not delivered. A push dequeued while the session is
/// downgraded (<c>none</c> / <c>thread</c>) is not sent either. The writer's "may I send?" check and
/// the start of the send happen under one send gate that <see cref="ApplyHeader"/> and
/// <see cref="RequestClose"/> also take, so once either has returned no send begins: nothing is
/// written after the receive loop processed a downgrade (a send already in progress completes). On
/// eviction a backstop is armed at once that cancels the connection after <see cref="CloseGrace"/>,
/// and the writer — once its current send, if any, is done — sends a normal close frame; the peer's
/// answering close ends the connection sooner (JC-21). The backstop also covers a writer stuck in a
/// send to a peer that stopped reading.
/// </para>
/// </remarks>
internal sealed class WebSocketReturnRouteSession : IReturnRouteSession
{
    /// <summary>The default bound on the eviction close handshake and on stopping the writer.</summary>
    internal static readonly TimeSpan DefaultCloseGrace = TimeSpan.FromSeconds(5);

    /// <summary>The close-frame description sent to an evicted peer.</summary>
    internal const string EvictedDescription = "return-route session evicted";

    private readonly WebSocket _socket;
    private readonly DidCommClient _client;
    private readonly CancellationTokenSource _connection;
    private readonly CancellationToken _connectionToken;
    private readonly ILogger? _logger;
    private readonly Channel<byte[]> _queue;
    private int _freePushSlots; // MaxPendingPushes minus pushes reserved (snapshot / pack) or queued; never negative
    private readonly CancellationTokenSource _closedSource = new();
    private readonly CancellationToken _closedToken;
    private volatile int _mode = (int)ReturnRouteMode.All;
    private volatile string? _thread;
    private int _closeState;   // 0 = open; otherwise 1 + (int)SessionCloseReason, set once (Interlocked)
    private readonly object _sendGate = new(); // the writer's check + send start vs. ApplyHeader / RequestClose
    private volatile Task? _writer;

    /// <summary>Create the session for one connection; call <see cref="Start"/> once it is registered.</summary>
    /// <param name="socket">The connection's socket. Only this session's writer sends on it while the session runs.</param>
    /// <param name="client">The facade that packs pushes.</param>
    /// <param name="peerDid">The authcrypt sender's DID subject, fixed for the session's lifetime.</param>
    /// <param name="localDid">The DID subject of the key that decrypted the opening inbound.</param>
    /// <param name="maxPendingPushes">Pushes being packed or queued at once, and the writer queue's depth (<see cref="ReturnRouteSessionOptions.MaxPendingPushes"/>).</param>
    /// <param name="connection">The connection's token source; cancelled to end the connection on a writer fault or when an evicted peer never answers the close frame.</param>
    /// <param name="logger">Optional logger (ids and type / enum names only).</param>
    /// <param name="closeGrace">Override of <see cref="DefaultCloseGrace"/> (tests).</param>
    internal WebSocketReturnRouteSession(
        WebSocket socket,
        DidCommClient client,
        string peerDid,
        string localDid,
        int maxPendingPushes,
        CancellationTokenSource connection,
        ILogger? logger,
        TimeSpan? closeGrace = null)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(peerDid);
        ArgumentException.ThrowIfNullOrEmpty(localDid);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPendingPushes, 1);
        ArgumentNullException.ThrowIfNull(connection);
        _socket = socket;
        _client = client;
        PeerDid = peerDid;
        LocalDid = localDid;
        _connection = connection;
        _connectionToken = connection.Token;
        _logger = logger;
        CloseGrace = closeGrace ?? DefaultCloseGrace;
        _closedToken = _closedSource.Token;
        _freePushSlots = maxPendingPushes;
        // Defense in depth: a frame is queued only under a reserved slot, so the channel is never
        // fuller than the reservations allow. Only TryWrite is used: full → false, never blocks.
        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(maxPendingPushes)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <inheritdoc />
    public string PeerDid { get; }

    /// <inheritdoc />
    public string LocalDid { get; }

    /// <inheritdoc />
    public ReturnRouteMode Mode => IsClosed ? ReturnRouteMode.None : (ReturnRouteMode)_mode;

    /// <inheritdoc />
    public string? Thread => _thread;

    /// <inheritdoc />
    public ReturnRouteTransport Transport => ReturnRouteTransport.WebSocket;

    /// <inheritdoc />
    public bool IsLive => true;

    /// <inheritdoc />
    public CancellationToken Closed => _closedToken;

    /// <summary>The bound on the eviction close handshake and on stopping the writer.</summary>
    internal TimeSpan CloseGrace { get; }

    /// <summary><c>true</c> once the session closed, for any reason.</summary>
    internal bool IsClosed => Volatile.Read(ref _closeState) != 0;

    /// <summary><c>true</c> when the writer task never started or has run to completion (diagnostics and tests).</summary>
    internal bool WriterStopped => _writer is not { IsCompleted: false };

    /// <summary>Why the session closed, or <c>null</c> while it is open.</summary>
    internal SessionCloseReason? CloseReason
    {
        get
        {
            var state = Volatile.Read(ref _closeState);
            return state == 0 ? null : (SessionCloseReason)(state - 1);
        }
    }

    /// <summary>Start the writer task. Idempotent; called once the registry accepted the session.</summary>
    internal void Start()
    {
        if (_writer is not null)
            return;
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <inheritdoc />
    public async ValueTask<bool> TrySendAsync(Message message, CancellationToken ct)
        => await TrySendAsync(message, int.MaxValue, ct).ConfigureAwait(false) == ReturnRouteSendResult.Sent;

    /// <inheritdoc />
    public async ValueTask<ReturnRouteSendResult> TrySendAsync(Message message, int maxPackedBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPackedBytes, 1);
        ct.ThrowIfCancellationRequested();

        // A downgraded or closed session costs nothing; a full one costs no snapshot and no crypto.
        if (Mode != ReturnRouteMode.All)
            return ReturnRouteSendResult.NotSent;
        if (!TryReservePushSlot())
        {
            SafeLog.Write(_logger, LogLevel.Debug, "Return-route push {MessageId} not queued: the session has no free push slot.", LogValues.Id(message.Id));
            return ReturnRouteSendResult.NotSent;
        }

        var slotHeld = true;
        try
        {
            var snapshot = SessionOutbound.TrySnapshotAndCheck(message, PeerDid, LocalDid, _logger);
            if (snapshot is null)
                return ReturnRouteSendResult.NotSent;

            var frame = await SessionOutbound.TryPackAsync(_client, snapshot, PeerDid, LocalDid, _logger, ct).ConfigureAwait(false);
            if (frame is null)
                return ReturnRouteSendResult.NotSent;

            // FR-PICK-03: the bound is on the packed envelope, so it is checked on the very bytes
            // the writer would send. Nothing is queued; the finally below gives the slot back.
            // Checked before the mode is read again: a frame over its bound is TooLarge even when
            // a downgrade or close landed during the pack (the caller's next send is then NotSent).
            if (frame.Length > maxPackedBytes)
            {
                SafeLog.Write(_logger, LogLevel.Debug, "Return-route push {MessageId} not queued: its packed envelope is {PackedBytes} bytes, over the caller's bound of {MaxPackedBytes}.", LogValues.Id(snapshot.Id), frame.Length, maxPackedBytes);
                return ReturnRouteSendResult.TooLarge;
            }

            if (Mode != ReturnRouteMode.All)
                return ReturnRouteSendResult.NotSent;

            // Never awaits the socket (JC-9). On success the slot passes to the queued frame, and the
            // writer gives it back when it dequeues the frame; a completed (closed) queue refuses.
            if (_queue.Writer.TryWrite(frame))
            {
                slotHeld = false;
                return ReturnRouteSendResult.Sent;
            }
            SafeLog.Write(_logger, LogLevel.Debug, "Return-route push {MessageId} not queued: the session's writer queue is full or closed.", LogValues.Id(snapshot.Id));
            return ReturnRouteSendResult.NotSent;
        }
        finally
        {
            if (slotHeld)
                ReleasePushSlot();
        }
    }

    /// <summary>Take one free push slot; <c>false</c> (nothing taken) when none is free.</summary>
    private bool TryReservePushSlot()
    {
        var free = Volatile.Read(ref _freePushSlots);
        while (free > 0)
        {
            var seen = Interlocked.CompareExchange(ref _freePushSlots, free - 1, free);
            if (seen == free)
                return true;
            free = seen;
        }
        return false;
    }

    /// <summary>Give back one push slot taken by <see cref="TryReservePushSlot"/>.</summary>
    private void ReleasePushSlot() => Interlocked.Increment(ref _freePushSlots);

    /// <summary>
    /// Apply the <c>return_route</c> header of a later message authenticated by this session's own
    /// (peer, local) pair (FR-RR-05): <c>all</c> resumes pushes; <c>none</c> — and a malformed value,
    /// which FR-RR-01 treats as <c>none</c> — downgrades; <c>thread</c> downgrades and surfaces the
    /// thread (JC-20). The endpoint does not call this for a header-less message (the mode is then
    /// unchanged). A no-op once the session closed (PA-3).
    /// </summary>
    /// <param name="header">The parsed header of the later message.</param>
    internal void ApplyHeader(ReturnRouteHeader header)
    {
        // Under the send gate: once this returns, the writer cannot start a send it checked against
        // the previous mode.
        lock (_sendGate)
        {
            if (IsClosed)
                return;
            switch (header.Mode)
            {
                case ReturnRouteMode.All:
                    _thread = null;
                    _mode = (int)ReturnRouteMode.All;
                    break;
                case ReturnRouteMode.Thread:
                    _thread = header.Thread;
                    _mode = (int)ReturnRouteMode.None;
                    break;
                default:
                    _mode = (int)ReturnRouteMode.None;
                    break;
            }
        }
    }

    /// <summary>
    /// Close the session (idempotent; the first reason wins): pushes stop, queued pushes are
    /// discarded, <see cref="Closed"/> is cancelled. For <see cref="SessionCloseReason.Evicted"/>
    /// the connection backstop is armed (<see cref="CloseGrace"/>) and the writer then sends a
    /// normal close frame (FR-RR-03: the peer observes the eviction). Safe from any thread.
    /// </summary>
    /// <param name="reason">Why the session closes.</param>
    internal void RequestClose(SessionCloseReason reason)
    {
        // Under the send gate, like ApplyHeader: once this returns no new send begins.
        lock (_sendGate)
        {
            if (Interlocked.CompareExchange(ref _closeState, 1 + (int)reason, 0) != 0)
                return;
            _mode = (int)ReturnRouteMode.None;
        }
        _queue.Writer.TryComplete();

        // JC-21 backstop, armed at eviction rather than after the close frame went out: the writer
        // reaches the close frame only after its current send, which never completes when the peer
        // stopped reading.
        if (reason == SessionCloseReason.Evicted)
        {
            try
            {
                _connection.CancelAfter(CloseGrace);
            }
            catch (ObjectDisposedException)
            {
                // The connection already ended.
            }
        }

        // Closed callbacks are handler code: never run them on the caller's thread (which may be
        // another connection's receive loop evicting this session).
        var cancelled = _closedSource.CancelAsync();
        if (!cancelled.IsCompletedSuccessfully)
            _ = ObserveAsync(cancelled, _logger);
    }

    /// <summary>
    /// Close the session (as <see cref="SessionCloseReason.Unregistered"/> unless already closed)
    /// and wait for the writer to finish, bounded by <see cref="CloseGrace"/>; a writer still
    /// inside a send by then has its send cancelled through the connection token. Returns
    /// <c>true</c> when the writer is stopped (or never started), so the caller may close the
    /// socket without overlapping a send; <c>false</c> when it could not be stopped, in which case
    /// the caller must abort the socket instead.
    /// </summary>
    internal async Task<bool> StopAsync()
    {
        RequestClose(SessionCloseReason.Unregistered);
        var writer = _writer;
        if (writer is null)
            return true;

        try
        {
            await writer.WaitAsync(CloseGrace).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
        }

        // Stuck in a send to a peer that is not reading: cancel the send (the connection ends anyway).
        CancelConnection();
        try
        {
            await writer.WaitAsync(CloseGrace).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            SafeLog.Write(_logger, LogLevel.Warning, "Return-route session writer did not stop within the close grace; aborting the connection.");
            return false;
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var frame in _queue.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                // The frame left the queue: its slot is free again, whether it is sent or discarded
                // below (the frame being sent is not counted against MaxPendingPushes).
                ReleasePushSlot();

                // Closed: discard what is still queued. Downgraded: nothing is written after the
                // receive loop processed the `none` / `thread`. The check and the start of the send
                // are one step under the send gate (ApplyHeader / RequestClose take it too); the send
                // itself is awaited outside it.
                ValueTask send;
                lock (_sendGate)
                {
                    if (IsClosed || _mode != (int)ReturnRouteMode.All)
                        continue;
                    send = _socket.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, endOfMessage: true, _connectionToken);
                }
                await send.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_connectionToken.IsCancellationRequested)
        {
            // The connection is ending (client abort, eviction backstop, StopAsync): nothing to report.
            RequestClose(SessionCloseReason.Unregistered);
            return;
        }
        catch (Exception ex)
        {
            // The socket is broken: end the session and the connection; the receive loop unregisters.
            SafeLog.Write(_logger, LogLevel.Warning, "Return-route session writer failed with {ExceptionType}; ending the connection.", ex.GetType().Name);
            RequestClose(SessionCloseReason.Unregistered);
            CancelConnection();
            return;
        }

        if (CloseReason == SessionCloseReason.Evicted)
            await SendEvictionCloseAsync().ConfigureAwait(false);
    }

    private async Task SendEvictionCloseAsync()
    {
        if (_socket.State != WebSocketState.Open)
            return;
        try
        {
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, EvictedDescription, _connectionToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SafeLog.Write(_logger, LogLevel.Debug, "Return-route eviction close failed with {ExceptionType}; ending the connection.", ex.GetType().Name);
            CancelConnection();
            return;
        }

        // The peer answers with its close frame, which ends the receive loop; if it never does, the
        // backstop RequestClose(Evicted) armed cancels the connection after the grace (JC-21).
    }

    private void CancelConnection()
    {
        try
        {
            _connection.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The connection already ended.
        }
        catch (AggregateException ex)
        {
            SafeLog.Write(_logger, LogLevel.Debug, "Connection cancellation callback threw {ExceptionType}.", ex.GetType().Name);
        }
    }

    private static async Task ObserveAsync(Task cancellation, ILogger? logger)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SafeLog.Write(logger, LogLevel.Warning, "A return-route session Closed callback threw {ExceptionType}; isolated.", ex.GetType().Name);
        }
    }
}
