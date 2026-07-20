using System.Collections.Concurrent;
using DidComm.Consistency;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports;
using Microsoft.Extensions.Logging;

namespace DidComm.Protocols.DiscoverFeatures;

/// <summary>
/// The initiator (requester) side of Discover Features 2.0 (FR-PROTO-05a): sends a
/// <c>queries</c> message and awaits the peer's correlated <c>disclose</c>, so an application
/// can programmatically learn which protocols/versions/features a peer supports before
/// starting an exchange. Complements <see cref="DiscoverFeaturesHandler"/>, which implements
/// the responder side.
/// </summary>
/// <remarks>
/// <para>
/// Correlation runs as a small synchronous inbound correlator
/// (<see cref="IInboundCorrelator"/>), NOT through the best-effort <see cref="IProtocolObserver"/>
/// queue. The dispatcher hands each dispatched inbound message to this correlator inline; it matches
/// a <c>disclose</c> to a pending query by <c>thid</c> == the query's <c>id</c> and completes the
/// awaiting caller. The inline phase performs only bounded header/trust checks and an atomic claim;
/// typed body deserialization runs later on the requester's continuation. It is not subject to
/// observer-queue drops, while an inbound <c>disclose</c> remains a terminal leaf in dispatch whether
/// or not anyone is awaiting it.
/// </para>
/// <para>
/// <strong>Spoofing defense.</strong> A <c>disclose</c> completes a pending query only when the
/// envelope authenticated its sender (authcrypt or a verified signature) AND the message's
/// <c>from</c> is the DID the query was sent to (compared as a DID subject). An anoncrypt/plaintext
/// disclosure, or one from a third party that guessed the query id, is logged and ignored — and
/// deliberately does NOT cancel the pending query, so a forgery cannot deny the legitimate response.
/// </para>
/// <para>Thread-safe; intended as a singleton (registered by <c>AddBuiltInProtocols()</c>).</para>
/// </remarks>
public sealed class DiscoverFeaturesClient : IInboundCorrelator
{
    private const long RejectedLogEvery = 1000;

    private readonly Func<Message, SendOptions, CancellationToken, Task> _send;
    private readonly Func<InboundMessageSnapshot, IReadOnlyList<FeatureDisclosure>> _parseDisclosures;
    private readonly ILogger<DiscoverFeaturesClient>? _logger;
    private readonly ConcurrentDictionary<string, PendingQuery> _pending = new(StringComparer.Ordinal);

    private sealed class PendingQuery
    {
        private const int Waiting = 0;
        private const int Completed = 1;
        private const int Abandoned = 2;

        private int _terminalState;
        private long _rejected;

        public PendingQuery(string requesterDid, string responderDid)
        {
            RequesterDid = requesterDid;
            ResponderDid = responderDid;
            Completion = new TaskCompletionSource<InboundMessageSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public string RequesterDid { get; }
        public string ResponderDid { get; }
        public TaskCompletionSource<InboundMessageSnapshot> Completion { get; }
        public bool IsWaiting => Volatile.Read(ref _terminalState) == Waiting;

        public bool TryComplete(InboundMessageSnapshot snapshot)
        {
            if (Interlocked.CompareExchange(ref _terminalState, Completed, Waiting) != Waiting)
                return false;
            Completion.TrySetResult(snapshot);
            return true;
        }

        public bool TryAbandon(CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _terminalState, Abandoned, Waiting) != Waiting)
                return false;
            if (cancellationToken.CanBeCanceled)
                Completion.TrySetCanceled(cancellationToken);
            return true;
        }

        public bool ShouldLogRejection(out long rejected)
        {
            rejected = Interlocked.Increment(ref _rejected);
            return rejected == 1 || rejected % RejectedLogEvery == 0;
        }
    }

    /// <summary>Construct the initiator client over the DIDComm facade.</summary>
    /// <param name="client">The facade used to send the <c>queries</c> message (requires a transport router, like any <see cref="DidCommClient.SendAsync"/> caller).</param>
    /// <param name="logger">Optional structured logger; rejected (spoofed/unauthenticated) disclosures are logged as warnings.</param>
    public DiscoverFeaturesClient(DidCommClient client, ILogger<DiscoverFeaturesClient>? logger = null)
        : this(SendVia(client), logger)
    {
    }

    /// <summary>Test seam: construct over a raw send step instead of the full facade.</summary>
    /// <param name="send">Delegate that delivers the packed <c>queries</c> message.</param>
    /// <param name="logger">Optional structured logger.</param>
    internal DiscoverFeaturesClient(
        Func<Message, SendOptions, CancellationToken, Task> send,
        ILogger<DiscoverFeaturesClient>? logger = null)
        : this(
            send,
            static snapshot => DiscoverFeatures.ReadDisclosures(snapshot.DeserializeMessage()),
            logger)
    {
    }

    /// <summary>Test seam that makes off-receive-path disclosure parsing directly observable.</summary>
    internal DiscoverFeaturesClient(
        Func<Message, SendOptions, CancellationToken, Task> send,
        Func<InboundMessageSnapshot, IReadOnlyList<FeatureDisclosure>> parseDisclosures,
        ILogger<DiscoverFeaturesClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(parseDisclosures);
        _send = send;
        _parseDisclosures = parseDisclosures;
        _logger = logger;
    }

    private static Func<Message, SendOptions, CancellationToken, Task> SendVia(DidCommClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return (message, options, ct) => client.SendAsync(message, options, ct);
    }

    /// <summary>
    /// Send a Discover Features 2.0 <c>queries</c> to <paramref name="to"/> and await the
    /// correlated <c>disclose</c> (FR-PROTO-05a).
    /// </summary>
    /// <param name="from">Initiator DID (authcrypt sender — the peer must be able to tell who is asking, and the reply comes back addressed to this DID).</param>
    /// <param name="to">Responder DID. Only an authenticated <c>disclose</c> from exactly this DID (compared as a DID subject) can complete the query.</param>
    /// <param name="queries">One or more feature queries (see <see cref="FeatureQuery"/>).</param>
    /// <param name="timeout">Deadline for the <strong>whole operation</strong> — the send and the wait for the disclosure together. If it elapses before a correlated <c>disclose</c> arrives, the call fails with <see cref="TimeoutException"/>. Pass <see cref="Timeout.InfiniteTimeSpan"/> to bound only by <paramref name="ct"/>.</param>
    /// <param name="serviceEndpointOverride">Optional explicit endpoint URI, forwarded to <see cref="SendOptions.ServiceEndpointOverride"/> (skips DID-document service resolution).</param>
    /// <param name="ct">Cancellation token; cancelling abandons the pending query.</param>
    /// <returns>The disclosures the peer returned. An empty list is meaningful per FR-PROTO-05: it asserts "no matches", not "Discover Features unsupported".</returns>
    /// <exception cref="TimeoutException">The operation did not complete (send + correlated <c>disclose</c>) within <paramref name="timeout"/>.</exception>
    public async Task<IReadOnlyList<FeatureDisclosure>> QueryFeaturesAsync(
        string from,
        string to,
        IReadOnlyList<FeatureQuery> queries,
        TimeSpan timeout,
        Uri? serviceEndpointOverride = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentException.ThrowIfNullOrEmpty(to);
        ArgumentNullException.ThrowIfNull(queries);
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive (or Timeout.InfiniteTimeSpan).");

        var message = DiscoverFeatures.CreateQuery(from, to, queries.ToArray());
        var pending = new PendingQuery(from, to);

        // Register BEFORE sending: on a fast same-socket transport the disclose can arrive
        // before SendAsync even returns.
        if (!_pending.TryAdd(message.Id, pending))
            throw new InvalidOperationException($"Duplicate pending Discover Features query id '{message.Id}'.");

        // One deadline over BOTH the send and the wait. A linked token carries the timeout into the
        // transport too, so a slow send counts against the budget instead of starting the clock only
        // after it returns. Only OUR timeout is translated to TimeoutException — a transport failure
        // (or a caller cancellation) propagates unchanged rather than being mislabeled "no disclose".
        using var timeoutCts = timeout == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(timeout);
        using var linked = timeoutCts is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var effectiveCt = linked?.Token ?? ct;
        using var cancellationRegistration = effectiveCt.Register(() =>
        {
            // Cancellation and a valid response compete on PendingQuery's single terminal CAS.
            // The winner removes this exact dictionary entry and settles the TCS; there is no
            // remove-before-complete window in which a response can be lost or parsed twice.
            if (pending.TryAbandon(effectiveCt))
                RemovePending(message.Id, pending);
        });
        try
        {
            await _send(
                message,
                new SendOptions(
                    Recipients: new[] { to },
                    From: from,
                    ServiceEndpointOverride: serviceEndpointOverride),
                effectiveCt).ConfigureAwait(false);

            var disclose = await pending.Completion.Task.ConfigureAwait(false);
            // The TCS is settled only by PendingQuery's terminal CAS. Once a response wins that CAS,
            // a later cancellation must not retroactively replace it after body parsing has begun.
            // Conversely, when cancellation wins, the await above throws and parsing never runs.
            // Force an asynchronous thread-pool continuation even when a reentrant/loopback send
            // completed the TCS before `_send` returned, so parsing never extends the inbound
            // dispatcher/correlator call stack.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            return _parseDisclosures(disclose);
        }
        catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Discover Features query '{message.Id}' to '{to}' did not complete (send + correlated 'disclose') within {timeout} (FR-PROTO-05a).");
        }
        finally
        {
            pending.TryAbandon();
            RemovePending(message.Id, pending);
        }
    }

    /// <summary>
    /// Correlate an immutable inbound snapshot to a pending query (FR-PROTO-05a). Invoked inline by
    /// the dispatcher before application handlers and guarded per <see cref="IInboundCorrelator"/>.
    /// The bounded hook only validates fixed headers/trust metadata and atomically transfers the
    /// snapshot; disclosure parsing runs on the awaiting requester's continuation.
    /// </summary>
    /// <param name="received">The immutable plaintext/trust snapshot created during unpack.</param>
    void IInboundCorrelator.OnInbound(InboundMessageSnapshot received)
    {
        if (!string.Equals(received.Type, DiscoverFeatures.DiscloseType, StringComparison.OrdinalIgnoreCase))
            return;
        if (received.Thid is not { Length: > 0 } thid || !_pending.TryGetValue(thid, out var pending) || !pending.IsWaiting)
            return; // unsolicited disclose — stays a terminal leaf (FR-PROTO-05)

        // Spoofing defense: never complete a waiter off an unauthenticated envelope or a sender
        // other than the DID we queried. The pending entry is left intact so a forgery cannot
        // deny the legitimate response.
        if (!received.Authenticated)
        {
            LogRejected(pending, thid, received,
                "envelope does not authenticate the sender (anoncrypt/plaintext)");
            return;
        }
        // Defense in depth: `Authenticated` implies the sender is bound to an authenticating key id
        // (authcrypt skid or a verified JWS signer kid) — that binding is what makes `from`
        // trustworthy (FR-CONSIST-01/03). Require the key id to actually be present before we trust
        // `from`, so a hypothetical envelope-layer regression that set `Authenticated` without a
        // skid/signer kid cannot let a forged `from` complete the query. Fail closed.
        if (string.IsNullOrEmpty(received.SenderKid) && string.IsNullOrEmpty(received.SignerKid))
        {
            LogRejected(pending, thid, received,
                "envelope reports authenticated but carries no sender/signer key id");
            return;
        }
        if ((!string.IsNullOrEmpty(received.SenderKid)
                && !DidSubject.SameDidSubject(received.SenderKid, received.From))
            || (!string.IsNullOrEmpty(received.SignerKid)
                && !DidSubject.SameDidSubject(received.SignerKid, received.From)))
        {
            LogRejected(pending, thid, received,
                "plaintext sender does not match its authenticating key DID subject");
            return;
        }
        // Compare DID subjects, not raw strings (PRD §4.3): `from` and the queried `to` are DIDs or
        // DID URLs without a fragment, so a subject-wise match avoids dropping a legitimate reply
        // whose `from` differs only in DID-URL form. Still fails closed — an unparseable or
        // different-subject `from` does not complete the waiter.
        if (!DidSubject.SameDidSubject(received.From, pending.ResponderDid))
        {
            LogRejected(pending, thid, received,
                "sender is not the queried responder");
            return;
        }

        // Bind the response to the local identity that originated this exact query. `to` is covered by
        // the authenticated envelope/signature; on encrypted messages RecipientKid additionally proves
        // which local DID actually decrypted it in a multi-tenant secrets store.
        if (!received.To.Any(to => DidSubject.SameDidSubject(to, pending.RequesterDid)))
        {
            LogRejected(pending, thid, received,
                "message is not addressed to the requester");
            return;
        }
        if (received.Encrypted && !DidSubject.SameDidSubject(received.RecipientKid, pending.RequesterDid))
        {
            LogRejected(pending, thid, received,
                "envelope was not decrypted by the requester");
            return;
        }

        // One atomic terminal transition wins against duplicates and timeout/cancellation cleanup.
        // RunContinuationsAsynchronously prevents typed body parsing from running on this receive path.
        if (pending.TryComplete(received))
            RemovePending(thid, pending);
    }

    private void LogRejected(
        PendingQuery pending,
        string thid,
        InboundMessageSnapshot received,
        string reason)
    {
        if (_logger is null || !pending.ShouldLogRejection(out var rejected))
            return;

        _logger.LogWarning(
            "Ignored a 'disclose' for pending query {Thid}: {Reason}. Message id: {MessageId}; from: {From}; to: {To}; recipient kid: {RecipientKid}; requester: {Requester}; responder: {Responder}. Rejected known-thread disclosures: {Rejected} (first, then every {LogEvery}).",
            thid, reason, received.Id, received.From, received.To, received.RecipientKid,
            pending.RequesterDid, pending.ResponderDid, rejected, RejectedLogEvery);
    }

    private void RemovePending(string thid, PendingQuery pending)
        => _pending.TryRemove(new KeyValuePair<string, PendingQuery>(thid, pending));
}
