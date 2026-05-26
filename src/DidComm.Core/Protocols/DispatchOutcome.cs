using DidComm.Messages;

namespace DidComm.Protocols;

/// <summary>The high-level disposition of a single dispatcher invocation.</summary>
public enum DispatchResult
{
    /// <summary>No handler was registered for the inbound <c>type</c>; the message was ignored.</summary>
    NoHandler,
    /// <summary>A handler ran and returned <c>null</c>, meaning no reply is warranted (e.g. Empty 1.0).</summary>
    NoReply,
    /// <summary>A handler returned a reply that passed the FR-THR-04 loop-guard; the transport layer SHOULD deliver it.</summary>
    ReplyProduced,
    /// <summary>
    /// The inbound message was a pure ACK that also requested an ACK — FR-THR-04 rule 3
    /// defensive enforcement against a peer's rule-2 violation. The handler is NOT invoked
    /// and no reply is produced.
    /// </summary>
    DroppedAsAckLoop,
}

/// <summary>
/// What the dispatcher decided about an inbound message. The transport layer reads this to
/// decide whether to deliver a reply (HTTP receive only logs it per FR-TRN-10; WebSocket
/// optionally sends it on the same socket when explicitly opted-in).
/// </summary>
/// <param name="Result">High-level outcome category.</param>
/// <param name="Reply">The reply message the handler produced, when <see cref="Result"/> is <see cref="DispatchResult.ReplyProduced"/>.</param>
/// <param name="Handler">The handler that ran, when one was found; <c>null</c> for <see cref="DispatchResult.NoHandler"/>.</param>
public sealed record DispatchOutcome(
    DispatchResult Result,
    Message? Reply,
    IProtocolHandler? Handler);
