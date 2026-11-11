using System.Net.WebSockets;
using System.Threading.Channels;
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
/// <see cref="Mode"/>, <see cref="Thread"/> and the closed state are read from other threads
/// (handlers using <see cref="IReturnRouteSessionRegistry.Find"/>), so they are volatile; the
/// closed state is set once, atomically. A closed session never reopens: <see cref="ApplyHeader"/>
/// is a no-op on it and <see cref="Mode"/> reports <see cref="ReturnRouteMode.None"/> (PA-3).
/// </para>
/// <para>
/// On close for any reason queued pushes are discarded — <c>true</c> from
/// <see cref="TrySendAsync"/> meant queued, not delivered. A push dequeued while the session is
/// downgraded (<c>none</c> / <c>thread</c>) is not sent either, so nothing is written after the
/// receive loop processed the downgrade. On eviction the writer sends a normal close frame and arms
/// a backstop that cancels the connection after <see cref="CloseGrace"/> if the peer never answers
/// (JC-21).
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
    private readonly CancellationTokenSource _closedSource = new();
    private readonly CancellationToken _closedToken;
    private volatile int _mode = (int)ReturnRouteMode.All;
    private volatile string? _thread;
    private int _closeState;   // 0 = open; otherwise 1 + (int)SessionCloseReason, set once (Interlocked)
    private volatile Task? _writer;

    /// <summary>Create the session for one connection; call <see cref="Start"/> once it is registered.</summary>
    /// <param name="socket">The connection's socket. Only this session's writer sends on it while the session runs.</param>
    /// <param name="client">The facade that packs pushes.</param>
    /// <param name="peerDid">The authcrypt sender's DID subject, fixed for the session's lifetime.</param>
    /// <param name="localDid">The DID subject of the key that decrypted the opening inbound.</param>
    /// <param name="maxPendingPushes">The writer queue's depth (<see cref="ReturnRouteSessionOptions.MaxPendingPushes"/>).</param>
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
        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(maxPendingPushes)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait, // only TryWrite is used: full → false, never blocks
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
    {
        ArgumentNullException.ThrowIfNull(message);
        ct.ThrowIfCancellationRequested();

        // A downgraded or closed session costs no snapshot and no crypto.
        if (Mode != ReturnRouteMode.All)
            return false;

        var snapshot = SessionOutbound.TrySnapshotAndCheck(message, PeerDid, LocalDid, _logger);
        if (snapshot is null)
            return false;

        var frame = await SessionOutbound.TryPackAsync(_client, snapshot, PeerDid, LocalDid, _logger, ct).ConfigureAwait(false);
        if (frame is null || Mode != ReturnRouteMode.All)
            return false;

        // Never awaits the socket: a full (or completed) queue answers false at once (JC-9).
        if (_queue.Writer.TryWrite(frame))
            return true;
        SafeLog.Write(_logger, LogLevel.Debug, "Return-route push {MessageId} not queued: the session's writer queue is full or closed.", snapshot.Id);
        return false;
    }

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

    /// <summary>
    /// Close the session (idempotent; the first reason wins): pushes stop, queued pushes are
    /// discarded, <see cref="Closed"/> is cancelled. For <see cref="SessionCloseReason.Evicted"/>
    /// the writer then sends a normal close frame (FR-RR-03: the peer observes the eviction).
    /// Safe from any thread.
    /// </summary>
    /// <param name="reason">Why the session closes.</param>
    internal void RequestClose(SessionCloseReason reason)
    {
        if (Interlocked.CompareExchange(ref _closeState, 1 + (int)reason, 0) != 0)
            return;
        _mode = (int)ReturnRouteMode.None;
        _queue.Writer.TryComplete();

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
                // Closed: discard what is still queued. Downgraded: nothing is written after the
                // receive loop processed the `none` / `thread`.
                if (IsClosed || _mode != (int)ReturnRouteMode.All)
                    continue;
                await _socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, _connectionToken).ConfigureAwait(false);
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

        // Backstop (JC-21): the peer answers with its close frame, which ends the receive loop; if it
        // never does, the connection is cancelled after the grace.
        try
        {
            _connection.CancelAfter(CloseGrace);
        }
        catch (ObjectDisposedException)
        {
            // The connection already ended.
        }
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
