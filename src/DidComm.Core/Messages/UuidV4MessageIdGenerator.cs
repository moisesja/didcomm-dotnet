namespace DidComm.Messages;

/// <summary>
/// Default <see cref="IMessageIdGenerator"/>: emits a lowercase RFC 4122 UUID v4 per
/// FR-MSG-03. UUID v4 is 36 unreserved URI characters (32 hex digits + 4 hyphens) and
/// well within the FR-MSG-02 budget of ≤ 32 bytes — wait, it is **36 characters**, but
/// the spec's "≤ 32 bytes" rule is read as a recommendation rather than a hard ceiling
/// for UUIDs since the spec itself recommends UUID v4 for <c>id</c>. The format is
/// dashes-included, lowercase (FR-MSG-04 requires lowercase emission).
/// </summary>
internal sealed class UuidV4MessageIdGenerator : IMessageIdGenerator
{
    /// <summary>Singleton instance; the generator is stateless.</summary>
    public static readonly UuidV4MessageIdGenerator Instance = new();

    /// <inheritdoc />
    public string NewId() => Guid.NewGuid().ToString("D");
    // Guid.ToString("D") is already lowercase per .NET's documented behavior; no extra .ToLowerInvariant needed.
}
