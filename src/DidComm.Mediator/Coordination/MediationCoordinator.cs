using DidComm.Consistency;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Coordination;

/// <summary>What the mediator decided about a <c>mediate-request</c>.</summary>
internal enum MediateOutcome
{
    /// <summary>Nothing is sent: a store, policy or identity fault (logged).</summary>
    NoReply,

    /// <summary>Answer <c>mediate-deny</c>.</summary>
    Denied,

    /// <summary>Answer <c>mediate-grant</c> with the routing DIDs.</summary>
    Granted,
}

/// <summary>
/// The mediator role of Coordinate Mediation without the wire format: who the peer is, what a
/// mediate request, a recipient update and a recipient query do to the store, and what comes back.
/// <see cref="CoordinateMediationHandler"/> (3.0) puts message names and bodies around it; a 2.0
/// handler can do the same over the same store (FR-MED-10).
/// </summary>
/// <remarks>
/// Nothing here throws for a store, policy or identity fault: the fault is logged by exception type
/// name and turned into the outcome the protocol allows (<c>server_error</c> entries, or no reply).
/// Cancellation of the dispatch token is the exception and propagates. Log lines carry the message
/// id (shortened when over <see cref="MaxLoggedIdLength"/> characters) and type or enum names only.
/// </remarks>
internal sealed class MediationCoordinator
{
    internal const int MaxLoggedIdLength = 128;
    private const long DropLogEvery = 1000;

    private readonly IMediationStore _store;
    private readonly IMediationPolicy _policy;
    private readonly IMediatorIdentity _identity;
    private readonly CoordinateMediationOptions _options;
    private readonly ILogger? _logger;
    private long _unauthenticated;
    private long _capacityDenials;

    public MediationCoordinator(
        IMediationStore store,
        IMediationPolicy policy,
        IMediatorIdentity identity,
        CoordinateMediationOptions options,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _store = store;
        _policy = policy;
        _identity = identity;
        _options = options;
        _logger = logger;
    }

    /// <summary>The largest <c>updates</c> list a request may carry.</summary>
    public int MaxUpdatesPerMessage => _options.MaxUpdatesPerMessage;

    /// <summary>
    /// The peer of a request (FR-MED-05): the DID subject of the authcrypt sender key, with the DID
    /// subject of the key that decrypted it as the local side. The same rule that lets an inbound
    /// open a return-route session (FR-RR-03), so "may be answered on the session" and "is a peer"
    /// can never disagree. <c>false</c> for plaintext, anoncrypt, signed-only and
    /// <c>anoncrypt(sign)</c>: the caller drops the message without looking at its body.
    /// </summary>
    public bool TryGetPeer(UnpackResult received, string messageId, out string peerDid, out string localDid)
    {
        if (ReturnRouteEligibility.TryGetPeer(received, out peerDid, out localDid, out _))
            return true;

        // Anyone can send these, so the line is rate-limited: the first and every 1000th at Warning.
        var n = Interlocked.Increment(ref _unauthenticated);
        var level = n == 1 || n % DropLogEvery == 0 ? LogLevel.Warning : LogLevel.Debug;
        Log(level, "Coordinate-mediation message {MessageId} dropped: no authcrypt sender (FR-MED-05); not applied, not answered. {Count} such drops.", LogId(messageId), n);
        return false;
    }

    /// <summary>Handle a <c>mediate-request</c> from <paramref name="peerDid"/>.</summary>
    public async Task<(MediateOutcome Outcome, IReadOnlyList<string> RoutingDids)> RequestAsync(
        string peerDid, string localDid, string messageId, ProtocolContext context, CancellationToken ct)
    {
        try
        {
            // A peer that already holds a grant gets it again, unchanged, with its registrations
            // (FR-MED-05: idempotent). The policy is for new grants only.
            var existing = await _store.GetGrantAsync(peerDid, ct).ConfigureAwait(false);
            if (existing is not null)
                return (MediateOutcome.Granted, existing.RoutingDids);

            var decision = await _policy.OnMediateRequestAsync(peerDid, context, ct).ConfigureAwait(false);
            if (decision != MediationDecision.Grant)
                return (MediateOutcome.Denied, Array.Empty<string>());

            // No configured routing DID: the DID the recipient addressed this request to.
            var configured = _identity.RoutingDids;
            IReadOnlyList<string> routingDids = configured is { Count: > 0 } ? configured.ToArray() : new[] { localDid };
            foreach (var did in routingDids)
            {
                if (!BareDid.IsBareDid(did))
                {
                    Log(LogLevel.Error, "Coordinate-mediation request {MessageId} not granted: a configured routing DID is not a bare DID within the length bound (FR-MED-07).", LogId(messageId));
                    return (MediateOutcome.NoReply, Array.Empty<string>());
                }
            }

            var granted = await _store.GrantAsync(new MediationGrant(peerDid, routingDids), ct).ConfigureAwait(false);
            if (granted is null)
            {
                // Any authenticated stranger can cause this once the store is full: rate-limited.
                var n = Interlocked.Increment(ref _capacityDenials);
                var level = n == 1 || n % DropLogEvery == 0 ? LogLevel.Warning : LogLevel.Debug;
                Log(level, "Coordinate-mediation request {MessageId} denied: the mediation store holds its maximum number of grants (FR-MED-08). {Count} such denials.", LogId(messageId), n);
                return (MediateOutcome.Denied, Array.Empty<string>());
            }
            return (MediateOutcome.Granted, granted.RoutingDids);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log(LogLevel.Error, "Coordinate-mediation request {MessageId} not answered: {ExceptionType} from the store, policy or identity; nothing granted by this request.", LogId(messageId), ex.GetType().Name);
            return (MediateOutcome.NoReply, Array.Empty<string>());
        }
    }

    /// <summary>
    /// Handle the entries of a <c>recipient-update</c> from <paramref name="peerDid"/>. One result
    /// per update, in input order; never throws for a store or policy fault.
    /// </summary>
    public async Task<IReadOnlyList<RecipientUpdateResult>> UpdateAsync(
        string peerDid, string localDid, IReadOnlyList<RecipientUpdate> updates, string messageId, ProtocolContext context, CancellationToken ct)
    {
        var results = new RecipientUpdateResult[updates.Count];
        if (updates.Count == 0)
            return results;

        // The grant first, so a peer without one costs no DID parse and no policy call.
        MediationGrant? grant;
        try
        {
            grant = await _store.GetGrantAsync(peerDid, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log(LogLevel.Error, "Coordinate-mediation update {MessageId}: {ExceptionType} from the store; every entry answered server_error.", LogId(messageId), ex.GetType().Name);
            return Fill(results, updates, RecipientUpdateOutcome.ServerError);
        }
        if (grant is null)
            return Fill(results, updates, RecipientUpdateOutcome.ClientError);

        var own = OwnDids(localDid, grant, context.Options);
        var pending = new List<RecipientUpdate>(updates.Count);
        var pendingIndex = new List<int>(updates.Count);
        var policyFaults = 0;
        string? policyFaultType = null;
        for (var i = 0; i < updates.Count; i++)
        {
            var update = updates[i];

            // A DID URL (or anything else that is not a bare DID) is refused, never normalized
            // into its DID (FR-MED-05).
            if (!BareDid.IsBareDid(update.RecipientDid))
            {
                results[i] = Result(update, RecipientUpdateOutcome.ClientError);
                continue;
            }

            if (update.Action == RecipientUpdateAction.Add)
            {
                // The mediator's own DIDs are not recipients.
                if (IsOneOf(own, update.RecipientDid))
                {
                    results[i] = Result(update, RecipientUpdateOutcome.ClientError);
                    continue;
                }

                bool allowed;
                try
                {
                    allowed = await _policy.MayRegisterAsync(peerDid, update.RecipientDid, context, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    policyFaults++;
                    policyFaultType ??= ex.GetType().Name;
                    results[i] = Result(update, RecipientUpdateOutcome.ServerError);
                    continue;
                }
                if (!allowed)
                {
                    results[i] = Result(update, RecipientUpdateOutcome.ClientError);
                    continue;
                }
            }

            pending.Add(update);
            pendingIndex.Add(i);
        }

        if (policyFaults > 0)
            Log(LogLevel.Error, "Coordinate-mediation update {MessageId}: the policy threw {ExceptionType} for {Count} entries; those answered server_error.", LogId(messageId), policyFaultType, policyFaults);

        if (pending.Count == 0)
            return results;

        // One store call for the whole batch: the store applies it atomically and in order, and
        // decides ownership itself (one grant per DID, D5).
        IReadOnlyList<RecipientStoreOutcome>? outcomes = null;
        try
        {
            outcomes = await _store.UpdateRecipientsAsync(peerDid, pending, ct).ConfigureAwait(false);
            if (outcomes is null || outcomes.Count != pending.Count)
            {
                Log(LogLevel.Error, "Coordinate-mediation update {MessageId}: the store returned a result list of the wrong length; affected entries answered server_error.", LogId(messageId));
                outcomes = null;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log(LogLevel.Error, "Coordinate-mediation update {MessageId}: {ExceptionType} from the store; affected entries answered server_error.", LogId(messageId), ex.GetType().Name);
        }

        for (var j = 0; j < pending.Count; j++)
        {
            var outcome = outcomes is null ? RecipientUpdateOutcome.ServerError : ToWire(outcomes[j]);
            results[pendingIndex[j]] = Result(pending[j], outcome);
        }
        return results;
    }

    /// <summary>
    /// Handle a <c>recipient-query</c> from <paramref name="peerDid"/> (FR-MED-06). <c>null</c>
    /// means no reply: the store faulted and the protocol has no error shape for a query.
    /// </summary>
    public async Task<(IReadOnlyList<string> Dids, RecipientPagination Pagination)?> QueryAsync(
        string peerDid, RecipientQueryPaging? paginate, string messageId, CancellationToken ct)
    {
        // No paginate: the first page at the maximum size. limit <= 0: an empty page.
        var limit = paginate is null ? _options.MaxPageSize : Math.Clamp(paginate.Limit, 0, _options.MaxPageSize);
        var offset = Math.Max(0, paginate?.Offset ?? 0);

        RecipientPage page;
        try
        {
            page = await _store.ListRecipientsAsync(peerDid, offset, limit, ct).ConfigureAwait(false);
            if (page?.Dids is null || page.Dids.Count > limit || page.Total < 0)
            {
                Log(LogLevel.Error, "Coordinate-mediation query {MessageId} not answered: the store returned a page outside the requested bounds.", LogId(messageId));
                return null;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log(LogLevel.Error, "Coordinate-mediation query {MessageId} not answered: {ExceptionType} from the store.", LogId(messageId), ex.GetType().Name);
            return null;
        }

        var count = page.Dids.Count;
        var remaining = (int)Math.Max(0L, (long)page.Total - offset - count);
        return (page.Dids, new RecipientPagination(count, offset, remaining));
    }

    /// <summary>Write a log line; a throwing logging provider changes nothing.</summary>
    public void Log(LogLevel level, string message, params object?[] args)
    {
        if (_logger is null)
            return;
        try
        {
            _logger.Log(level, message, args);
        }
        catch
        {
            // Logging providers are host code; they must not change dispatch.
        }
    }

    /// <summary>
    /// A message id for a log line. Ids are attacker-chosen and not length-bounded by the message
    /// model, so a long one is replaced, not written.
    /// </summary>
    public static string LogId(string? messageId)
        => messageId is null ? "(none)"
            : messageId.Length <= MaxLoggedIdLength ? messageId
            : $"(id of {messageId.Length} characters)";

    private static RecipientUpdateOutcome ToWire(RecipientStoreOutcome outcome) => outcome switch
    {
        RecipientStoreOutcome.Added or RecipientStoreOutcome.Removed => RecipientUpdateOutcome.Success,
        RecipientStoreOutcome.AlreadyRegistered => RecipientUpdateOutcome.NoChange,
        RecipientStoreOutcome.OwnedByAnotherGrant
            or RecipientStoreOutcome.NotRegistered
            or RecipientStoreOutcome.GrantLimitReached
            or RecipientStoreOutcome.NoGrant => RecipientUpdateOutcome.ClientError,
        // StoreLimitReached, and any value a store invents: the mediator's side.
        _ => RecipientUpdateOutcome.ServerError,
    };

    private static RecipientUpdateResult Result(RecipientUpdate update, RecipientUpdateOutcome outcome)
        => new(update.RecipientDid, update.Action, outcome);

    private static RecipientUpdateResult[] Fill(RecipientUpdateResult[] results, IReadOnlyList<RecipientUpdate> updates, RecipientUpdateOutcome outcome)
    {
        for (var i = 0; i < results.Length; i++)
            results[i] = Result(updates[i], outcome);
        return results;
    }

    // The DIDs that are this mediator: the one that decrypted the request, the routing DIDs it
    // grants (configured, and those in the caller's grant) and the host's own identifiers.
    private List<string> OwnDids(string localDid, MediationGrant grant, DidCommOptions options)
    {
        var own = new List<string> { localDid };
        AddSubjects(own, grant.RoutingDids);
        try
        {
            AddSubjects(own, _identity.RoutingDids);
        }
        catch
        {
            // A faulting identity provider adds nothing here; a mediate-request reports it.
        }
        AddSubjects(own, options.OwnIdentifiers);
        return own;
    }

    private static void AddSubjects(List<string> own, IEnumerable<string>? values)
    {
        if (values is null)
            return;
        foreach (var value in values)
        {
            var subject = DidSubject.DidSubjectOf(value);
            if (subject is not null)
                own.Add(subject);
        }
    }

    // Walks the whole list every time: the answer never depends on which entry matched (D7).
    private static bool IsOneOf(List<string> own, string did)
    {
        var match = false;
        foreach (var candidate in own)
            match |= string.Equals(candidate, did, StringComparison.Ordinal);
        return match;
    }
}
