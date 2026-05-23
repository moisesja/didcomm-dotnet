namespace DidComm.Messages;

/// <summary>
/// Supplies the <c>id</c> value placed on every outbound <see cref="Message"/> when one is
/// not provided explicitly. Implementations carry a uniqueness obligation per FR-MSG-14:
/// each returned <c>id</c> MUST be unique across all messages the sender ever sends, and at
/// minimum unique across all interactions visible to the parties involved. UUID v4 (the
/// default; see <see cref="UuidV4MessageIdGenerator"/>) satisfies this in normal operation;
/// a custom generator MUST honor the contract or thread-correlation, deduplication, and
/// problem-report routing will break for downstream peers.
/// </summary>
internal interface IMessageIdGenerator
{
    /// <summary>Generate a new message identifier. MUST be ≤ 32 bytes of unreserved URI characters per FR-MSG-02 and MUST be unique per FR-MSG-14.</summary>
    string NewId();
}
