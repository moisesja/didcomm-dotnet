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
public interface IMessageIdGenerator
{
    /// <summary>Generate a new message identifier: unreserved URI characters per FR-MSG-02 (the spec describes "a short (&lt;=32 bytes) string" and recommends UUIDs, which the default emits as 36 characters), unique per FR-MSG-14. A message whose id is longer than 512 characters is refused when it is built or packed.</summary>
    string NewId();
}
