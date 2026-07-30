namespace DidComm.Facade;

/// <summary>
/// Outcome of the FR-CONSIST-04 recipient-addressing check (#59): when an unpacked message
/// carries a <c>to</c> header, was one of this agent's own identifiers named in it? Per the
/// spec this is advisory — a <see cref="NotAddressed"/> outcome never rejects the message,
/// it is surfaced so the application can apply its own policy (log, alert, drop).
/// </summary>
/// <remarks>
/// <para>
/// The identifiers checked are the union of the decrypting recipient kid's DID subject (when
/// the envelope was encrypted) and the DIDs the application declared in
/// <see cref="DidCommOptions.OwnIdentifiers"/>. All comparisons are DID-subject comparisons,
/// so a DID URL identifier matches its bare DID in <c>to</c>. Note that for encrypted
/// envelopes with a present <c>to</c>, FR-CONSIST-02 (a MUST) already rejects a message whose
/// <c>to</c> omits the decrypting kid's DID subject — so on that path a delivered message is
/// always <see cref="Addressed"/> unless only <see cref="DidCommOptions.OwnIdentifiers"/>
/// entries were checked. The warning becomes reachable on signed-only and plaintext messages,
/// which have no decrypting identity and are otherwise unchecked.
/// </para>
/// <para>
/// <strong>Trust boundary — this is a warning channel, not a trust signal.</strong> The
/// <c>to</c> header is sender-authored, so on a plaintext or anoncrypt envelope any party can
/// produce <see cref="Addressed"/> simply by naming you: it proves the sender typed your DID,
/// nothing more. Only when the message is <c>Authenticated</c> or carries a verified signature
/// (<c>NonRepudiation</c>) did an identified sender commit to that addressing. Likewise a sender
/// can reach <see cref="NotEvaluated"/> just by omitting <c>to</c> — absence of a warning is not
/// evidence of correct delivery. Act on <see cref="NotAddressed"/>; do not treat
/// <see cref="Addressed"/> as authorization.
/// </para>
/// </remarks>
public enum RecipientAddressing
{
    /// <summary>
    /// The check did not run: the message carried no <c>to</c> header, or the library had no
    /// own identifier to check against (not encrypted and
    /// <see cref="DidCommOptions.OwnIdentifiers"/> empty or unparseable). The default for
    /// synthetically constructed results.
    /// </summary>
    NotEvaluated = 0,

    /// <summary>An own identifier appears in the message's <c>to</c> list (as DID subjects).</summary>
    Addressed,

    /// <summary>
    /// The FR-CONSIST-04 warning: the message carries a <c>to</c> header, at least one own
    /// identifier was known, and none appear in the list. The message is still delivered —
    /// legitimate causes exist (mediated routing, re-wrapped envelopes) — but it can indicate
    /// surreptitious forwarding or misdelivery.
    /// </summary>
    NotAddressed,
}
