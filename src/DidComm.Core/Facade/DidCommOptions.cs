using DidComm.Transports;

namespace DidComm.Facade;

/// <summary>
/// Process-wide configuration knobs for <c>DidCommClient</c>. Registered as a singleton via
/// <c>AddDidComm(...).Configure(...)</c>.
/// </summary>
/// <example>
/// Configured in <c>samples/02-Cookbook</c> (section A — dependency-injection setup).
/// </example>
public sealed class DidCommOptions
{
    /// <summary>
    /// Per-message receive ceiling enforced by <c>UnpackAsync</c>; oversized inputs throw
    /// <see cref="Exceptions.MalformedMessageException"/> before any cryptographic work begins
    /// (FR-API-06 / problem code <c>me.res.storage.message_too_big</c> — surfaced by the
    /// transport layer in Phase 5). Defaults to 1 MiB. The return-route client side applies the same
    /// ceiling to an in-band HTTP response it reads (FR-RR-07; a larger body is never buffered) and to
    /// each message pushed on a WebSocket session socket (a larger one closes the socket with 1009).
    /// </summary>
    public int MaxReceiveBytes { get; set; } = 1 * 1024 * 1024;

    /// <summary>
    /// Tolerance applied when checking <see cref="Messages.Message.ExpiresTime"/> against the
    /// current clock (FR-API-05), in whole seconds (a sub-second value counts as zero). Defaults to
    /// <see cref="TimeSpan.Zero"/> — strict expiry. The same allowance judges a <c>from_prior</c>
    /// JWT's <c>exp</c> / <c>nbf</c> (FR-ROT-05) and a forward's or queued payload's expiry
    /// (FR-ROUTE-09), so the checks cannot drift apart.
    /// </summary>
    /// <remarks>
    /// It does not affect FR-RR-08 replay admission, which has its own tolerance for a peer whose
    /// clock runs ahead: <see cref="Transports.ReturnRoute.ReplayProtectionOptions.ClockSkew"/>
    /// (default 60 seconds). Raising this value only accepts expired messages for longer.
    /// </remarks>
    public TimeSpan ExpiresClockSkew { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Override the clock used for FR-API-05 expiry checks, FR-ROT-* claims validation, FR-RR-08
    /// replay admission and the FR-RR-09 <c>created_time</c> stamping of <c>SendAsync</c>.
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

    /// <summary>
    /// The DIDs (or DID URLs) this agent considers its own identity, used by the FR-CONSIST-04
    /// recipient-addressing check (#59): when an unpacked message carries a <c>to</c> header,
    /// <c>UnpackAsync</c> reports on <see cref="UnpackResult.RecipientAddressing"/> whether one
    /// of these identifiers — or the decrypting recipient kid's DID subject — appears in the
    /// list. Comparison is by DID subject, so a DID URL entry matches its bare DID. Empty by
    /// default: unconfigured agents are still covered on encrypted envelopes via the decrypting
    /// kid, while signed-only and plaintext messages report
    /// <see cref="RecipientAddressing.NotEvaluated"/>. Advisory only — never affects delivery.
    /// </summary>
    public IReadOnlyCollection<string> OwnIdentifiers { get; set; } = Array.Empty<string>();

    /// <summary>Resolved clock helper.</summary>
    internal DateTimeOffset Now() => Clock?.Invoke() ?? DateTimeOffset.UtcNow;
}
