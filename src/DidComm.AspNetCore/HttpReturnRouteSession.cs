using DidComm.Facade;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.Logging;

namespace DidComm.AspNetCore;

/// <summary>
/// The return-route session of one HTTP request (FR-RR-04): at most one message, which becomes the
/// <c>200</c> body of the request that opened the session. Request-scoped and never registered in
/// the <see cref="IReturnRouteSessionRegistry"/> (JC-16), so it raises no session events.
/// </summary>
/// <remarks>
/// <para>
/// One slot, guarded by one lock. A sender claims the slot BEFORE packing, so two concurrent
/// callers pack at most once; if the winner's pack fails the slot is released, so the handler's
/// returned reply can still use it.
/// </para>
/// <para>
/// The endpoint's last step, <see cref="TakeResponseAndClose"/>, closes the session and takes the
/// response in one state transition (PA-2). A sender still packing at that moment (a handler's
/// background task holding <c>context.Session</c>) finds the session closed when its pack
/// completes and returns <c>false</c>: its bytes are never reported as sent and never reach the
/// response.
/// </para>
/// </remarks>
internal sealed class HttpReturnRouteSession : IReturnRouteSession
{
    private const int SlotFree = 0;
    private const int SlotClaimed = 1;
    private const int SlotFilled = 2;

    private readonly DidCommClient _client;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _closedSource = new();
    private readonly CancellationToken _closedToken;
    private int _slot;               // guarded by _gate
    private byte[]? _response;       // guarded by _gate
    private volatile bool _closed;   // written under _gate; read lock-free for the cheap pre-checks

    /// <summary>Create the session for one admitted, eligible HTTP inbound.</summary>
    /// <param name="client">The facade that packs the returned message.</param>
    /// <param name="peerDid">The authcrypt sender's DID subject.</param>
    /// <param name="localDid">The DID subject of the key that decrypted the inbound.</param>
    /// <param name="logger">Optional logger (ids and type / enum names only).</param>
    internal HttpReturnRouteSession(DidCommClient client, string peerDid, string localDid, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(peerDid);
        ArgumentException.ThrowIfNullOrEmpty(localDid);
        _client = client;
        PeerDid = peerDid;
        LocalDid = localDid;
        _logger = logger;
        _closedToken = _closedSource.Token;
    }

    /// <inheritdoc />
    public string PeerDid { get; }

    /// <inheritdoc />
    public string LocalDid { get; }

    /// <inheritdoc />
    public ReturnRouteMode Mode => _closed ? ReturnRouteMode.None : ReturnRouteMode.All;

    /// <inheritdoc />
    public string? Thread => null;

    /// <inheritdoc />
    public ReturnRouteTransport Transport => ReturnRouteTransport.Http;

    /// <inheritdoc />
    public bool IsLive => false;

    /// <inheritdoc />
    public CancellationToken Closed => _closedToken;

    /// <summary><c>true</c> while the session holds a packed message (the one the response will carry).</summary>
    internal bool HasResponse
    {
        get
        {
            lock (_gate)
                return _slot == SlotFilled;
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> TrySendAsync(Message message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ct.ThrowIfCancellationRequested();

        // Cheap pre-check: a closed or already-filled session costs no snapshot and no crypto.
        lock (_gate)
        {
            if (_closed || _slot != SlotFree)
                return false;
        }

        var snapshot = SessionOutbound.TrySnapshotAndCheck(message, PeerDid, LocalDid, _logger);
        if (snapshot is null)
            return false;

        // Claim the one slot before packing: only the winner packs.
        lock (_gate)
        {
            if (_closed || _slot != SlotFree)
                return false;
            _slot = SlotClaimed;
        }

        var committed = false;
        try
        {
            var packed = await SessionOutbound.TryPackAsync(_client, snapshot, PeerDid, LocalDid, _logger, ct).ConfigureAwait(false);
            if (packed is null)
                return false;

            lock (_gate)
            {
                // PA-2: the endpoint already closed the session and took its (empty) response; these
                // bytes would never be sent, so they are not reported as sent.
                if (_closed)
                    return false;
                _response = packed;
                _slot = SlotFilled;
                committed = true;
            }
            return true;
        }
        finally
        {
            if (!committed)
            {
                // Pack failed, was cancelled, or lost to the close: release the claim so a later
                // sender (the handler's returned reply) can still use the slot while the session is open.
                lock (_gate)
                {
                    if (_slot == SlotClaimed)
                        _slot = SlotFree;
                }
            }
        }
    }

    /// <summary>
    /// Close the session and take its response in one state transition (PA-2): returns the packed
    /// message for the <c>200</c> body, or <c>null</c> for a <c>202</c>. After this call every
    /// <see cref="TrySendAsync"/> — including one already packing — returns <c>false</c>, and
    /// <see cref="Closed"/> is cancelled. Idempotent: a second call returns <c>null</c>.
    /// </summary>
    internal byte[]? TakeResponseAndClose()
    {
        byte[]? taken;
        lock (_gate)
        {
            if (_closed)
                return null;
            _closed = true;
            taken = _slot == SlotFilled ? _response : null;
            _response = null;
        }

        // Callbacks registered on Closed are handler code: run them off this thread (the response
        // must not wait for them) and observe their faults by type name.
        var cancelled = _closedSource.CancelAsync();
        if (!cancelled.IsCompletedSuccessfully)
            _ = ObserveAsync(cancelled, _logger);
        return taken;
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
