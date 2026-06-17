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

    /// <summary>Construct a dispatcher bound to a registry and thread-state store.</summary>
    /// <param name="registry">Resolved handlers per FR-PROTO-03.</param>
    /// <param name="threads">Per-thread state store (FR-I18N-02, FR-PROTO-10).</param>
    /// <param name="logger">Optional structured logger; warnings are emitted for FR-THR-04 rule 3 drops.</param>
    /// <param name="traceOptions">Optional Trace 2.0 options (FR-PROTO-11). Registered via <c>DidCommBuilder.EnableTracing(...)</c>; when supplied, every inbound message is checked against <see cref="TraceObserver.ShouldReport"/> and an authorised trace-report intent is logged at <c>Information</c>. HTTP POST integration is deferred.</param>
    public ProtocolDispatcher(
        ProtocolHandlerRegistry registry,
        IThreadStateStore threads,
        ILogger<ProtocolDispatcher>? logger = null,
        TraceOptions? traceOptions = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(threads);
        _registry = registry;
        _threads = threads;
        _logger = logger;
        _traceOptions = traceOptions;
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
}
