namespace DidComm.Messages;

/// <summary>
/// Default <see cref="IMessageIdGenerator"/>: emits an RFC 4122 UUID v4 per FR-MSG-03, rendered
/// dashes-included and lowercase (FR-MSG-04 requires lowercase emission). A UUID v4 is 36
/// characters of unreserved URI grammar (32 hex digits + 4 hyphens); the spec recommends UUID v4
/// for <c>id</c>, so its 36-character length is treated as conformant rather than bounded by the
/// FR-MSG-02 ≤ 32-byte recommendation.
/// </summary>
public sealed class UuidV4MessageIdGenerator : IMessageIdGenerator
{
    /// <summary>Singleton instance; the generator is stateless.</summary>
    public static readonly UuidV4MessageIdGenerator Instance = new();

    /// <inheritdoc />
    // Guid.ToString("D") is lowercase in practice, but FR-MSG-04 mandates lowercase, so normalize
    // explicitly rather than depend on undocumented formatting behavior.
    public string NewId() => Guid.NewGuid().ToString("D").ToLowerInvariant();
}
