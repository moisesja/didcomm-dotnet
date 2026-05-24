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

    /// <summary>Resolved clock helper.</summary>
    internal DateTimeOffset Now() => Clock?.Invoke() ?? DateTimeOffset.UtcNow;
}
