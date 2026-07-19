using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.Trace;
using DidComm.Threading;
using Microsoft.Extensions.Logging;

namespace DidComm.Protocols;

/// <summary>
/// Orchestrates dispatching an unpacked inbound message to its <see cref="IProtocolHandler"/>:
/// resolves the handler from <see cref="ProtocolHandlerRegistry"/>, applies the FR-THR-04
/// loop-guard pre-filter, calls the handler, validates any reply against
/// <see cref="AckLoopGuard.IsSafeToSend"/> (FR-THR-04 rule 2), and returns a
/// <see cref="DispatchOutcome"/> describing what the transport layer should do next.
/// After the outcome is determined, registered <see cref="IProtocolObserver"/>s are notified
/// on a read-only side channel (FR-PROTO-12) — see <see cref="IProtocolObserver"/> for the
/// trust model.
/// </summary>
/// <remarks>
/// The dispatcher does NOT itself send replies — that decision is the transport's, because
/// HTTP receive is one-way per FR-TRN-10 (replies travel out of band) while WebSocket can
/// optionally reply on the same socket when the operator opts in. Keeping the dispatcher
/// transport-agnostic also makes it directly testable from unit tests without spinning up an
/// ASP.NET Core host.
/// </remarks>
public sealed class ProtocolDispatcher
{
    private readonly ProtocolHandlerRegistry _registry;
    private readonly IThreadStateStore _threads;
    private readonly ILogger<ProtocolDispatcher>? _logger;
    private readonly TraceOptions? _traceOptions;
    private readonly IProtocolObserver[] _observers;

    /// <summary>Construct a dispatcher bound to a registry and thread-state store.</summary>
    /// <param name="registry">Resolved handlers per FR-PROTO-03.</param>
    /// <param name="threads">Per-thread state store (FR-I18N-02, FR-PROTO-10).</param>
    /// <param name="logger">Optional structured logger; warnings are emitted for FR-THR-04 rule 3 drops.</param>
    /// <param name="traceOptions">Optional Trace 2.0 options (FR-PROTO-11). Registered via <c>DidCommBuilder.EnableTracing(...)</c>; when supplied, every inbound message is checked against <see cref="TraceObserver.ShouldReport"/> and an authorised trace-report intent is logged at <c>Information</c>. HTTP POST integration is deferred.</param>
    /// <param name="observers">Optional read-only inbound observers (FR-PROTO-12). Fixed at construction — there is deliberately no runtime add/remove — and enumerated in an Information log line so the registration set is operator-auditable.</param>
    public ProtocolDispatcher(
        ProtocolHandlerRegistry registry,
        IThreadStateStore threads,
        ILogger<ProtocolDispatcher>? logger = null,
        TraceOptions? traceOptions = null,
        IEnumerable<IProtocolObserver>? observers = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(threads);
        _registry = registry;
        _threads = threads;
        _logger = logger;
        _traceOptions = traceOptions;
        _observers = observers?.ToArray() ?? Array.Empty<IProtocolObserver>();
        if (_observers.Length > 0)
        {
            // FR-PROTO-12 audit trail: observers see decrypted inbound plaintext, so the set of
            // registered observer types must be visible to an operator reading startup logs — a
            // stowaway registration from a compromised dependency should not be silent.
            _logger?.LogInformation(
                "Inbound protocol observers registered (read-only side channel, FR-PROTO-12): {Observers}.",
                string.Join("; ", _observers.Select(o => $"{o.GetType().FullName} (filter: {o.ProtocolUriFilter ?? "ALL"})")));
        }
    }

    /// <summary>
    /// Dispatch <paramref name="received"/> to its handler. Returns the resulting
    /// <see cref="DispatchOutcome"/> for the caller (typically the AspNetCore endpoint layer)
    /// to act on.
    /// </summary>
    /// <param name="received">The unpack result for the inbound envelope.</param>
    /// <param name="client">The facade — passed through to the handler via <see cref="ProtocolContext.Client"/>. Nullable for ergonomic unit tests; production code always supplies it.</param>
    /// <param name="options">The active <see cref="DidCommOptions"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DispatchOutcome> DispatchAsync(
        UnpackResult received,
        DidCommClient? client,
        DidCommOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(received);
        ArgumentNullException.ThrowIfNull(options);

        var outcome = await DispatchCoreAsync(received, client, options, ct).ConfigureAwait(false);

        // FR-PROTO-12: observers are notified exactly once per completed dispatch, AFTER the
        // outcome is determined and for every outcome path (handled, NoReply, NoHandler, and
        // the loop-guard drops) — so observer state or timing can never influence handling,
        // and an initiator with no handler registered for a PIURI can still observe its traffic.
        await NotifyObserversAsync(received, ct).ConfigureAwait(false);
        return outcome;
    }

    private async Task<DispatchOutcome> DispatchCoreAsync(
        UnpackResult received,
        DidCommClient? client,
        DidCommOptions options,
        CancellationToken ct)
    {
        // FR-PROTO-11: when Trace 2.0 is opted in via EnableTracing, exercise the decision logic
        // on every inbound. Today the observable side effect is a structured Information log
        // line — HTTP POST integration is deferred to the runtime hook in a later phase.
        if (_traceOptions is not null && TraceObserver.ShouldReport(received.Message, _traceOptions, out var traceUri))
        {
            _logger?.LogInformation(
                "Trace 2.0: inbound message {MessageId} authorizes trace-report to {TraceUri}; POST integration deferred (FR-PROTO-11).",
                received.Message.Id, traceUri);
        }

        // FR-THR-04 rule 3: a pure ACK that answers an ACK THIS agent requested on the thread must be
        // consumed, not dispatched — honoring it (e.g. a handler replying) is what closes an ACK loop.
        // Only ACK requests the dispatcher itself emitted are tracked (see the reply path below); a
        // request sent via the facade directly is the application's responsibility. Fetch the thread
        // only for a pure ACK so unrelated traffic doesn't allocate state here.
        if (AckLoopGuard.IsPureAck(received.Message))
        {
            var ackThread = _threads.GetOrCreate(received.Message.Thid ?? received.Message.Id);
            if (ackThread.AckRequested)
            {
                ackThread.AckRequested = false;
                _logger?.LogInformation(
                    "Consumed inbound pure-ACK answering our ACK request (FR-THR-04 rule 3); not dispatched. Message id: {MessageId}",
                    received.Message.Id);
                return new DispatchOutcome(DispatchResult.DroppedAsAckLoop, Reply: null, Handler: null);
            }
        }

        // FR-THR-04 rule 2 (defensive enforcement of a peer's violation): an inbound pure ACK that ALSO
        // requests an ACK would loop both peers forever. Drop it without invoking a handler.
        if (AckLoopGuard.IsPureAck(received.Message) && AckLoopGuard.RequestsAck(received.Message))
        {
            _logger?.LogWarning(
                "Dropped inbound pure-ACK that also requests an ACK (peer's FR-THR-04 rule 2 violation). Message id: {MessageId}",
                received.Message.Id);
            return new DispatchOutcome(DispatchResult.DroppedAsAckLoop, Reply: null, Handler: null);
        }

        if (!_registry.TryResolve(received.Message.Type, out var handler))
        {
            _logger?.LogInformation(
                "No protocol handler registered for inbound message type '{Type}' (FR-PROTO-03).",
                received.Message.Type);
            return new DispatchOutcome(DispatchResult.NoHandler, Reply: null, Handler: null);
        }

        var thread = _threads.GetOrCreate(received.Message.Thid ?? received.Message.Id);
        var context = new ProtocolContext(received, thread, client, options, _threads);

        var reply = await handler.HandleAsync(received.Message, context, ct).ConfigureAwait(false);
        if (reply is null)
            return new DispatchOutcome(DispatchResult.NoReply, Reply: null, Handler: handler);

        // FR-THR-04 rule 2: pure-ACK that also requests an ACK is a handler bug. We surface it
        // as InvalidOperationException so the misconfiguration is loud and the bad reply never
        // reaches the wire.
        if (!AckLoopGuard.IsSafeToSend(reply))
        {
            throw new InvalidOperationException(
                $"Protocol handler '{handler.ProtocolUri}' returned a reply that violates FR-THR-04 rule 2 (a pure ACK MUST NOT also request an ACK). Reply id: '{reply.Id}'.");
        }

        // FR-THR-04 rule-3 bookkeeping: if this reply requests an ACK, remember it on the thread so the
        // answering pure-ACK is consumed (above) rather than re-dispatched into a loop.
        if (AckLoopGuard.RequestsAck(reply))
            thread.AckRequested = true;

        return new DispatchOutcome(DispatchResult.ReplyProduced, reply, handler);
    }

    private async Task NotifyObserversAsync(UnpackResult received, CancellationToken ct)
    {
        if (_observers.Length == 0)
            return;

        foreach (var observer in _observers)
        {
            // Everything per-observer — the filter check, the defensive clone, and the callback —
            // runs inside one try/catch. FR-PROTO-12 guarantees observation can NEVER change the
            // dispatch outcome: the outcome is already computed by the caller, so even a
            // pathological message that trips ObserverMatches or the clone must be swallowed here
            // rather than propagate out of DispatchAsync and clobber it.
            try
            {
                if (!ObserverMatches(observer.ProtocolUriFilter, received.Message.Type))
                    continue;

                // Each observer gets its OWN deep clone: mutation by one observer can reach neither
                // the pipeline's live message nor another observer's view. Observer counts are
                // small, so per-observer cloning costs less than the isolation is worth.
                var observation = InboundObservation.FromUnpackResult(received);
                await observer.OnMessageReceivedAsync(observation, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "IProtocolObserver '{ObserverType}' threw while observing inbound message {MessageId}; the dispatch outcome is unaffected (FR-PROTO-12).",
                    observer.GetType().FullName, received.Message.Id);
            }
        }
    }

    // A filter observes its whole protocol family: same protocol name and major version, any
    // minor (a 2.0 filter sees 2.1 traffic). This reuses the registry's FR-PROTO-01/02 matching
    // rules minus the older-minor tie-break, which only exists to pick a single handler.
    private static bool ObserverMatches(string? filter, string? messageType)
    {
        if (filter is null)
            return true;
        if (!MessageTypeUri.TryParse(messageType, out var mturi))
            return false;
        if (!ProtocolIdentifier.TryParse(filter, out var filterPiuri))
            return false;
        // TryParse, not Parse: the MTURI docUri group (`.+?`) tolerates a trailing '/' that the
        // stricter PIURI group (`.+?[^/]`) rejects, so a crafted double-slash `type` parses as an
        // MTURI but its derived PIURI does not. Fail closed to "no match" rather than throw.
        if (!ProtocolIdentifier.TryParse(mturi!.ProtocolIdentifier, out var inboundPiuri))
            return false;
        return filterPiuri.MatchesProtocolAndMajor(inboundPiuri);
    }
}
