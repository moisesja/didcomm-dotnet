using System.Collections.Concurrent;
using DidComm.Consistency;
using DidComm.Facade;
using DidComm.Messages;
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
/// Correlation rides the <see cref="IProtocolObserver"/> seam (FR-PROTO-12): this class
/// observes inbound <c>discover-features/2.0</c> traffic only, matches a <c>disclose</c> to a
/// pending query by <c>thid</c> == the query's <c>id</c>, and completes the awaiting caller.
/// The responder-side handler is untouched — an inbound <c>disclose</c> remains a terminal
/// leaf in dispatch, whether or not anyone is awaiting it.
/// </para>
/// <para>
/// <strong>Spoofing defense.</strong> A <c>disclose</c> completes a pending query only when the
/// envelope authenticated its sender (authcrypt or a verified signature) AND the message's
/// <c>from</c> is exactly the DID the query was sent to. An anoncrypt/plaintext disclosure, or
/// one from a third party that guessed the query id, is logged and ignored — and deliberately
/// does NOT cancel the pending query, so a forgery cannot deny the legitimate response either.
/// </para>
/// <para>Thread-safe; intended as a singleton (registered by <c>AddBuiltInProtocols()</c>).</para>
/// </remarks>
public sealed class DiscoverFeaturesClient : IProtocolObserver
{
    private readonly Func<Message, SendOptions, CancellationToken, Task> _send;
    private readonly ILogger<DiscoverFeaturesClient>? _logger;
    private readonly ConcurrentDictionary<string, PendingQuery> _pending = new(StringComparer.Ordinal);

    private sealed record PendingQuery(
        string ResponderDid,
        TaskCompletionSource<IReadOnlyList<FeatureDisclosure>> Completion);

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
    {
        ArgumentNullException.ThrowIfNull(send);
        _send = send;
        _logger = logger;
    }

    private static Func<Message, SendOptions, CancellationToken, Task> SendVia(DidCommClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return (message, options, ct) => client.SendAsync(message, options, ct);
    }

    /// <summary>Observe only the Discover Features 2.x family (least privilege, FR-PROTO-12).</summary>
    string? IProtocolObserver.ProtocolUriFilter => DiscoverFeatures.ProtocolUri;

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
        var completion = new TaskCompletionSource<IReadOnlyList<FeatureDisclosure>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Register BEFORE sending: on a fast same-socket transport the disclose can arrive
        // before SendAsync even returns.
        if (!_pending.TryAdd(message.Id, new PendingQuery(to, completion)))
            throw new InvalidOperationException($"Duplicate pending Discover Features query id '{message.Id}'.");

        // One deadline over BOTH the send and the wait. A linked token carries the timeout into the
        // transport too, so a slow send counts against the budget instead of starting the clock only
        // after it returns. Only OUR timeout is translated to TimeoutException — a transport failure
        // (or a caller cancellation) propagates unchanged rather than being mislabeled "no disclose".
        using var timeoutCts = timeout == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(timeout);
        using var linked = timeoutCts is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var effectiveCt = linked?.Token ?? ct;
        try
        {
            await _send(
                message,
                new SendOptions(
                    Recipients: new[] { to },
                    From: from,
                    ServiceEndpointOverride: serviceEndpointOverride),
                effectiveCt).ConfigureAwait(false);

            return await completion.Task.WaitAsync(effectiveCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Discover Features query '{message.Id}' to '{to}' did not complete (send + correlated 'disclose') within {timeout} (FR-PROTO-05a).");
        }
        finally
        {
            _pending.TryRemove(message.Id, out _);
        }
    }

    /// <inheritdoc />
    Task IProtocolObserver.OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var message = observation.Message;

        if (!string.Equals(message.Type, DiscoverFeatures.DiscloseType, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;
        if (message.Thid is not { Length: > 0 } thid || !_pending.TryGetValue(thid, out var pending))
            return Task.CompletedTask; // unsolicited disclose — stays a terminal leaf (FR-PROTO-05)

        // Spoofing defense: never complete a waiter off an unauthenticated envelope or a sender
        // other than the DID we queried. The pending entry is left intact so a forgery cannot
        // deny the legitimate response.
        if (!observation.Authenticated)
        {
            _logger?.LogWarning(
                "Ignored a 'disclose' for pending query {Thid}: envelope does not authenticate the sender (anoncrypt/plaintext). Message id: {MessageId}.",
                thid, message.Id);
            return Task.CompletedTask;
        }
        // Defense in depth: `Authenticated` implies the sender is bound to an authenticating key id
        // (authcrypt skid or a verified JWS signer kid) — that binding is what makes `from`
        // trustworthy (FR-CONSIST-01/03). Require the key id to actually be present before we trust
        // `from`, so a hypothetical envelope-layer regression that set `Authenticated` without a
        // skid/signer kid cannot let a forged `from` complete the query. Fail closed.
        if (string.IsNullOrEmpty(observation.SenderKid) && string.IsNullOrEmpty(observation.SignerKid))
        {
            _logger?.LogWarning(
                "Ignored a 'disclose' for pending query {Thid}: envelope reports authenticated but carries no sender/signer key id. Message id: {MessageId}.",
                thid, message.Id);
            return Task.CompletedTask;
        }
        // Compare DID subjects, not raw strings (PRD §4.3): `from` and the queried `to` are DIDs or
        // DID URLs without a fragment, so a subject-wise match avoids dropping a legitimate reply
        // whose `from` differs only in DID-URL form. Still fails closed — an unparseable or
        // different-subject `from` does not complete the waiter.
        if (!DidSubject.SameDidSubject(message.From, pending.ResponderDid))
        {
            _logger?.LogWarning(
                "Ignored a 'disclose' for pending query {Thid}: sender '{From}' is not the queried responder. Message id: {MessageId}.",
                thid, message.From, message.Id);
            return Task.CompletedTask;
        }

        pending.Completion.TrySetResult(DiscoverFeatures.ReadDisclosures(message));
        return Task.CompletedTask;
    }
}
