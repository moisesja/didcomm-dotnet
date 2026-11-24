using System.Threading.Channels;
using DidComm.Facade;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.Logging;

namespace DidComm.Samples.MediatorAgent;

/// <summary>How the relay ended one forward.</summary>
public enum RelayOutcome
{
    /// <summary>Delivered to the recipient's own inbox with <see cref="DidCommClient.SendPackedAsync"/>.</summary>
    Relayed,

    /// <summary>The recipient is registered with the mediator, but this host knows no inbox for it: handed to <see cref="NullForwardSink"/>.</summary>
    NoInbox,

    /// <summary>The forward's own <c>expires_time</c> passed while it waited: not relayed.</summary>
    Expired,

    /// <summary>
    /// The relay did not finish: the inbox could not be reached, answered with an error, or took
    /// longer than <see cref="InboxRelaySink.RelayTimeout"/>. The exception goes back to the
    /// handler, which logs the message id and the exception type.
    /// </summary>
    Failed,
}

/// <summary>What the relay did with one forward, for the host to read (and the sample to narrate).</summary>
/// <param name="NextHop">The DID subject the onward payload was meant for.</param>
/// <param name="Outcome">How the relay ended it.</param>
/// <param name="AcknowledgedFirst">Whether the transport had already acknowledged the forward (the 202 was on the wire) when the sink started.</param>
/// <param name="ExpiresTime">The forward's own <c>expires_time</c> (epoch seconds), or <c>null</c>.</param>
/// <param name="Delay">The hold the sender asked for (<c>delay_milli</c>, capped by the mediator), or <c>null</c>.</param>
/// <param name="NotBefore">When a held forward becomes due, or <c>null</c> when no hold was asked for.</param>
/// <param name="HttpStatus">The inbox's HTTP status, when the forward was relayed.</param>
/// <param name="ResponseDiscarded">Whether the transport dropped a response body it did not read.</param>
/// <param name="ResponseMediaType">The media type of a response body the transport read, or <c>null</c>.</param>
/// <param name="ResponseBytes">The size of a response body the transport read (0 when none).</param>
/// <param name="Error">The exception type name when the relay failed, or <c>null</c>.</param>
public sealed record RelayRecord(
    string NextHop,
    RelayOutcome Outcome,
    bool AcknowledgedFirst,
    long? ExpiresTime,
    TimeSpan? Delay,
    DateTimeOffset? NotBefore,
    int? HttpStatus = null,
    bool ResponseDiscarded = false,
    string? ResponseMediaType = null,
    int ResponseBytes = 0,
    string? Error = null);

/// <summary>
/// The host's own relay for the mediator's routing role: an <see cref="IForwardSink"/> that delivers
/// each forward's onward payload to the recipient's own HTTP inbox.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddRoutingMediator()</c> receives forwards, checks them, answers the sender with a 202 and
/// only then hands each one to the registered <see cref="IForwardSink"/>, on a background queue.
/// The library's default sink, <see cref="NullForwardSink"/>, drops everything; a complete
/// mediator (<c>AddMediator()</c> in <c>DidComm.Mediator</c>) keeps each message for pickup, pushes
/// it to a live connection and, with <c>MediatorOptions.DirectPush</c>, to the inbox the host
/// registered for the recipient (section 9 of this sample). This sink is the option for a host
/// that keeps the routing role only: it keeps its own directory of recipient inboxes and relays to
/// them itself.
/// </para>
/// <para>
/// The directory is bounded (<see cref="MaxInboxes"/> entries) and keyed by DID subject — the DID
/// without any <c>#fragment</c>, <c>?query</c> or <c>/path</c> — because a forward's <c>next</c> may
/// name one of the recipient's keys rather than the bare DID (PRD §4.3). Only the host's operator
/// fills it (<see cref="TryAddInbox"/>), never from a peer's message or a DID document: otherwise a
/// stranger could point the relay at an address of its choosing.
/// </para>
/// <para>
/// Dependency rule: a sink is built inside the protocol handler registry, so it must never take
/// <see cref="ProtocolHandlerRegistry"/>, <see cref="ProtocolDispatcher"/> or a protocol handler
/// in its constructor (<c>AddRoutingMediator()</c> refuses such a sink at registration). The
/// <see cref="DidCommClient"/> is safe to take: the library's own <see cref="ForwardProcessor"/>
/// takes it too. A sink is a singleton and must be thread-safe.
/// </para>
/// <para>
/// A sink runs on the forward handler's background pump, one forward at a time, so it must never
/// wait on an endpoint a peer controls without a time limit: one inbox that never answers would
/// stop every relay behind it. Each relay here gets at most <see cref="RelayTimeout"/>. That bound
/// does not remove the problem, it only caps it: because this sample relays one forward at a time,
/// a slow inbox still delays each forward queued behind it by up to the bound (a known limit). A
/// production relay hands each forward to a bounded queue per recipient, served by its own
/// workers, so a slow recipient only delays its own messages. That protects other recipients from a
/// slow one, not from a failing one: the HTTP transport has one circuit breaker for every
/// destination, so repeated failed attempts at one inbox (retries included) can open it, and while
/// it is open every recipient's relay fails. (The complete mediator's push avoids this: it sends
/// through its own copy of the transport, with no retry and a circuit breaker that never opens.)
/// </para>
/// </remarks>
public sealed class InboxRelaySink : IForwardSink
{
    /// <summary>The most inboxes this host's directory keeps.</summary>
    public const int MaxInboxes = 16;

    /// <summary>The most outcome records kept for the host to read; older ones are dropped first.</summary>
    public const int MaxRecords = 32;

    /// <summary>
    /// The longest one relay may take, from the first connection attempt to the inbox's answer.
    /// After that the relay is given up and recorded as <see cref="RelayOutcome.Failed"/>.
    /// </summary>
    public static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(5);

    private readonly DidCommClient _relay;
    private readonly NullForwardSink _noInbox;
    private readonly object _gate = new();
    private readonly Dictionary<string, Uri> _inboxes = new(StringComparer.Ordinal);
    private readonly Channel<RelayRecord> _records = Channel.CreateBounded<RelayRecord>(
        new BoundedChannelOptions(MaxRecords) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>Create the relay.</summary>
    /// <param name="relay">The mediator's facade: it sends the onward bytes under the mediator's own outbound-endpoint (SSRF) policy.</param>
    /// <param name="loggerFactory">Optional; for the fallback <see cref="NullForwardSink"/>, which logs message ids only, never a DID. The relay itself logs nothing: a relay that fails goes back to the forward handler, which logs it.</param>
    public InboxRelaySink(DidCommClient relay, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(relay);
        _relay = relay;
        _noInbox = new NullForwardSink(loggerFactory?.CreateLogger<NullForwardSink>());
    }

    /// <summary>What the relay did, oldest first. Read one record per forward you are waiting for.</summary>
    public ChannelReader<RelayRecord> Records => _records.Reader;

    /// <summary>The number of inboxes in the directory.</summary>
    public int InboxCount
    {
        get
        {
            lock (_gate)
                return _inboxes.Count;
        }
    }

    /// <summary>
    /// Record where a recipient's own inbox is. Returns <c>false</c> when the value is not a DID or
    /// DID URL, or when the directory is full and the DID is not already in it.
    /// </summary>
    /// <param name="didOrDidUrl">The recipient's DID (a DID URL is reduced to its DID).</param>
    /// <param name="inbox">The recipient's HTTP(S) inbox.</param>
    public bool TryAddInbox(string didOrDidUrl, Uri inbox)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        var subject = DidSubjectOf(didOrDidUrl);
        if (subject is null || !inbox.IsAbsoluteUri)
            return false;

        lock (_gate)
        {
            if (_inboxes.Count >= MaxInboxes && !_inboxes.ContainsKey(subject))
                return false;
            _inboxes[subject] = inbox;
            return true;
        }
    }

    /// <inheritdoc />
    public async Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);
        var nextHop = DidSubjectOf(result.NextHop) ?? result.NextHop;

        // The handler hands a forward to its sink only after the transport acknowledged it (over
        // HTTP: once the mediator's 202 is on the wire), so this has already completed: reading it
        // shows the order. Awaiting it keeps this sink correct if it is ever called another way.
        var acknowledgedFirst = context.Acknowledged.IsCompletedSuccessfully;
        await context.Acknowledged.ConfigureAwait(false);

        // The handler judged expiry when the forward arrived; judge it again for the time it spent
        // queued, on the same clock and with the same skew allowance (FR-ROUTE-09).
        var now = context.Options.Clock?.Invoke() ?? DateTimeOffset.UtcNow;
        if (result.IsExpired(now, context.Options.ExpiresClockSkew))
        {
            Write(Base(RelayOutcome.Expired));
            return;
        }

        // A sender may ask the mediator to hold a forward (delay_milli, FR-ROUTE-10); NotBefore is
        // when it becomes due, capped by ForwardProcessorOptions.MaxDelay. This relay keeps no
        // delay queue, so it relays at once. A mediator that honours the hold stores the message
        // with its NotBefore instead (AddMediator()'s pickup queue does).
        Uri? inbox;
        lock (_gate)
            _inboxes.TryGetValue(nextHop, out inbox);

        if (inbox is null)
        {
            // Registered with the mediator, but this host has no inbox for it (the recipient
            // collects its messages some other way). Hand it to the library's NullForwardSink,
            // which drops it with a log line that names the message id only.
            await _noInbox.AcceptAsync(result, context, ct).ConfigureAwait(false);
            Write(Base(RelayOutcome.NoInbox));
            return;
        }

        // The inbox is an endpoint a peer controls: give the relay a time limit of its own (see
        // the class remarks), linked to the shutdown token the handler passes in.
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(RelayTimeout);
        try
        {
            // The onward bytes are already packed for the recipient: send them as they are, under
            // the media type of their own envelope (RelayMediaType.cs). Usually that is encrypted,
            // but a sender may have wrapped a signed or plaintext message, and the receiver checks
            // the declared type against the envelope (transports.md): labelled wrongly, it would be
            // refused with 415. The facade applies the mediator's outbound-endpoint (SSRF) policy to
            // the inbox URL and never reads a response body on this path.
            var sent = await _relay.SendPackedAsync(result.OnwardPacked, inbox, RelayMediaType.Of(result.OnwardPacked), bounded.Token)
                .ConfigureAwait(false);
            Write(Base(RelayOutcome.Relayed) with
            {
                HttpStatus = sent.HttpStatusCode,
                ResponseDiscarded = sent.ResponseDiscarded,
                ResponseMediaType = sent.ResponseMediaType,
                ResponseBytes = sent.ResponsePayload?.Length ?? 0,
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The host is shutting down: not a failed relay. The handler counts the forward as
            // dropped at shutdown.
            throw;
        }
        catch (OperationCanceledException ex) when (bounded.IsCancellationRequested)
        {
            // Our own time limit fired: the inbox was too slow. Record it, and hand the handler a
            // TimeoutException so it is logged as a failure, not as a shutdown.
            Write(Base(RelayOutcome.Failed) with { Error = nameof(TimeoutException) });
            throw new TimeoutException($"The relay did not finish within {RelayTimeout.TotalSeconds} seconds.", ex);
        }
        catch (Exception ex)
        {
            // Any other failure (the inbox refused, the guard refused its address, a fault): record
            // it, then let the handler see it too; it logs the message id and the exception type.
            Write(Base(RelayOutcome.Failed) with { Error = ex.GetType().Name });
            throw;
        }

        RelayRecord Base(RelayOutcome outcome) => new(
            nextHop, outcome, acknowledgedFirst, result.ExpiresTime, result.Delay, result.NotBefore);
    }

    private void Write(RelayRecord record) => _records.Writer.TryWrite(record);

    /// <summary>
    /// The DID of a DID or DID URL: everything before the first <c>/</c>, <c>?</c> or <c>#</c>
    /// (none of them can appear inside a DID), or <c>null</c> when the value is not a DID.
    /// </summary>
    internal static string? DidSubjectOf(string? didOrDidUrl)
    {
        if (string.IsNullOrEmpty(didOrDidUrl) || !didOrDidUrl.StartsWith("did:", StringComparison.Ordinal))
            return null;
        var end = didOrDidUrl.IndexOfAny(['/', '?', '#']);
        return end < 0 ? didOrDidUrl : didOrDidUrl[..end];
    }
}
