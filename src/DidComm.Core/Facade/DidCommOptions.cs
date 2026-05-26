using DidComm.Transports;

namespace DidComm.Facade;

/// <summary>
/// Process-wide configuration knobs for <c>DidCommClient</c>. Registered as a singleton via
/// <c>AddDidComm(...).Configure(...)</c>.
/// </summary>
public sealed class DidCommOptions
{
    /// <summary>
    /// Per-message receive ceiling enforced by <c>UnpackAsync</c>; oversized inputs throw
    /// <see cref="Exceptions.MalformedMessageException"/> before any cryptographic work begins
    /// (FR-API-06 / problem code <c>me.res.storage.message_too_big</c> — surfaced by the
    /// transport layer in Phase 5). Defaults to 1 MiB.
    /// </summary>
    public int MaxReceiveBytes { get; set; } = 1 * 1024 * 1024;

    /// <summary>
    /// Tolerance applied when checking <see cref="Messages.Message.ExpiresTime"/> against the
    /// current clock (FR-API-05). Defaults to <see cref="TimeSpan.Zero"/> — strict expiry.
    /// </summary>
    public TimeSpan ExpiresClockSkew { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Override the clock used for FR-API-05 expiry checks and FR-ROT-* claims validation.
    /// <c>null</c> ⇒ <see cref="DateTimeOffset.UtcNow"/>. Set in tests to make time deterministic.
    /// </summary>
    public Func<DateTimeOffset>? Clock { get; set; }

    /// <summary>
    /// Opt-in tolerance (DD-10) for a bare-string <c>serviceEndpoint</c> in a resolved DID
    /// Document. The DIDComm v2.1 conformance shape is an object (or array of objects) inside
    /// <c>serviceEndpoint</c>; a bare string is non-canonical. Off by default so callers receive
    /// stricter parsing automatically. Set to <c>true</c> only when interoperating with peers
    /// that still emit the legacy shape — and document why in the host application.
    /// </summary>
    public bool AllowBareStringServiceEndpoint { get; set; } = false;

    /// <summary>
    /// SSRF-defense policy applied by <c>SendAsync</c> to endpoints resolved from a recipient's DID
    /// document (FR-SEC). Defaults to blocking private / loopback / link-local / metadata
    /// destinations. A caller-supplied <c>SendOptions.ServiceEndpointOverride</c> is trusted and
    /// bypasses this policy.
    /// </summary>
    public OutboundEndpointPolicy OutboundEndpointPolicy { get; set; } = new();

    /// <summary>Resolved clock helper.</summary>
    internal DateTimeOffset Now() => Clock?.Invoke() ?? DateTimeOffset.UtcNow;
}
