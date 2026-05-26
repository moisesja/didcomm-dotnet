using DidComm.Facade;
using DidComm.Messages;
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

    /// <summary>Construct a dispatcher bound to a registry and thread-state store.</summary>
    /// <param name="registry">Resolved handlers per FR-PROTO-03.</param>
    /// <param name="threads">Per-thread state store (FR-I18N-02, FR-PROTO-10).</param>
    /// <param name="logger">Optional structured logger; warnings are emitted for FR-THR-04 rule 3 drops.</param>
    public ProtocolDispatcher(
        ProtocolHandlerRegistry registry,
        IThreadStateStore threads,
        ILogger<ProtocolDispatcher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(threads);
        _registry = registry;
        _threads = threads;
        _logger = logger;
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

        // FR-THR-04 rule 3 (defensive enforcement of a peer's rule-2 violation):
        // if the inbound is a pure ACK that ALSO requests an ACK, dropping it here prevents
        // an infinite ACK loop. We never invoke the handler — log and move on.
        if (AckLoopGuard.IsPureAck(received.Message) && AckLoopGuard.RequestsAck(received.Message))
        {
            _logger?.LogWarning(
                "Dropped inbound pure-ACK that also requests an ACK (FR-THR-04 rule 3). Message id: {MessageId}",
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
        var context = new ProtocolContext(received, thread, _threads, client, options);

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

        return new DispatchOutcome(DispatchResult.ReplyProduced, reply, handler);
    }
}
