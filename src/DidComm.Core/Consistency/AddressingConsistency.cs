using DidComm.Exceptions;

namespace DidComm.Consistency;

/// <summary>
/// Pure check functions for the message-layer addressing-consistency rules in PRD §4.3
/// (FR-CONSIST-01..06). They are deliberately stateless and free of resolver / crypto
/// dependencies so Phase 1 can land them ahead of Phase 2's envelope construction and
/// Phase 3's resolver wiring.
/// </summary>
/// <remarks>
/// <para>
/// Every comparison goes through <see cref="DidSubject.DidSubjectOf(string?)"/> per the §4.3
/// normative paragraph: never raw-string-compare a DID URL against a possibly-DID-URL
/// <c>from</c> / <c>to</c>.
/// </para>
/// <para>
/// The resolver-backed authorization check (FR-CONSIST-06) lives in
/// <see cref="CheckResolverAuthorization"/> and is intentionally a hook in Phase 1 — Phase 3
/// supplies the real implementation through <c>IDidKeyService</c>. Until then callers may
/// supply a <c>null</c> resolver and the hook short-circuits to "authorized".
/// </para>
/// </remarks>
internal static class AddressingConsistency
{
    /// <summary>
    /// FR-CONSIST-01 — authcrypt <c>from</c> ↔ <c>skid</c>. When plaintext <c>from</c> is
    /// present, the DID subject of the authcrypt sender key (<c>skid</c>) MUST equal the
    /// DID subject of <c>from</c>.
    /// </summary>
    /// <param name="from">The plaintext message <c>from</c> header (may be a DID or DID URL).</param>
    /// <param name="skid">The authcrypt sender key identifier (DID URL with fragment).</param>
    /// <exception cref="ConsistencyException">When the DID subjects differ.</exception>
    public static void CheckAuthcryptFromMatchesSkid(string? from, string skid)
    {
        ArgumentException.ThrowIfNullOrEmpty(skid);
        if (from is null) return;
        if (!DidSubject.SameDidSubject(from, skid))
            throw new ConsistencyException(
                $"Authcrypt 'skid' DID subject does not match plaintext 'from' (FR-CONSIST-01). from='{from}', skid='{skid}'.");
    }

    /// <summary>
    /// FR-CONSIST-02 — recipient <c>to</c> ↔ <c>kid</c>. When plaintext <c>to</c> is present,
    /// the DID subject of the recipient key actually used to decrypt MUST be a member of
    /// the <c>to</c> list (compared as DID subjects).
    /// </summary>
    /// <param name="to">The plaintext message <c>to</c> array; may be null/empty.</param>
    /// <param name="recipientKid">The recipient key identifier used to decrypt (DID URL).</param>
    /// <exception cref="ConsistencyException">When <paramref name="recipientKid"/>'s DID subject is not in <paramref name="to"/>.</exception>
    public static void CheckRecipientKidInTo(IEnumerable<string>? to, string recipientKid)
    {
        ArgumentException.ThrowIfNullOrEmpty(recipientKid);
        if (to is null) return;

        var recipientSubject = DidSubject.DidSubjectOf(recipientKid)
            ?? throw new ConsistencyException(
                $"Recipient 'kid' is not a parseable DID URL (FR-CONSIST-02). kid='{recipientKid}'.");

        foreach (var t in to)
        {
            var toSubject = DidSubject.DidSubjectOf(t);
            if (toSubject is not null && string.Equals(toSubject, recipientSubject, StringComparison.Ordinal))
                return;
        }

        throw new ConsistencyException(
            $"Recipient 'kid' DID subject is not present in plaintext 'to' (FR-CONSIST-02). kid='{recipientKid}'.");
    }

    /// <summary>
    /// FR-CONSIST-03 — signed <c>from</c> ↔ signer <c>kid</c>. When plaintext <c>from</c> is
    /// present in a signed message, the DID subject of the signer <c>kid</c> MUST equal the
    /// DID subject of <c>from</c>.
    /// </summary>
    /// <param name="from">The plaintext message <c>from</c> header.</param>
    /// <param name="signerKid">The JWS signer key identifier (DID URL with fragment).</param>
    /// <exception cref="ConsistencyException">When the DID subjects differ.</exception>
    public static void CheckSignedFromMatchesSignerKid(string? from, string signerKid)
    {
        ArgumentException.ThrowIfNullOrEmpty(signerKid);
        if (from is null) return;
        if (!DidSubject.SameDidSubject(from, signerKid))
            throw new ConsistencyException(
                $"Signer 'kid' DID subject does not match plaintext 'from' (FR-CONSIST-03). from='{from}', signerKid='{signerKid}'.");
    }

    /// <summary>
    /// FR-CONSIST-04 — recipient self-presence in <c>to</c>. Returns true (no warning) when
    /// <paramref name="recipientDid"/>'s DID subject appears in <paramref name="to"/>; returns
    /// false (caller should emit a warning) when it does not. Per SHOULD, we do not throw.
    /// </summary>
    /// <param name="to">The plaintext <c>to</c> array; may be null/empty.</param>
    /// <param name="recipientDid">The local recipient's DID.</param>
    public static bool IsRecipientInTo(IEnumerable<string>? to, string recipientDid)
    {
        if (to is null) return false;
        var subject = DidSubject.DidSubjectOf(recipientDid);
        if (subject is null) return false;
        foreach (var t in to)
        {
            var toSubject = DidSubject.DidSubjectOf(t);
            if (toSubject is not null && string.Equals(toSubject, subject, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// FR-CONSIST-05 — <c>authcrypt(sign(plaintext))</c> inner signer ↔ outer sender. When
    /// the spec's discouraged sign-inside-authcrypt composition is received, the inner JWS
    /// signer <c>kid</c> DID subject MUST equal the outer authcrypt <c>skid</c> DID subject.
    /// </summary>
    /// <param name="signerKid">Inner JWS signer key identifier.</param>
    /// <param name="skid">Outer authcrypt sender key identifier.</param>
    /// <exception cref="ConsistencyException">When the DID subjects differ.</exception>
    public static void CheckAuthcryptInnerSignerMatchesSkid(string signerKid, string skid)
    {
        ArgumentException.ThrowIfNullOrEmpty(signerKid);
        ArgumentException.ThrowIfNullOrEmpty(skid);
        if (!DidSubject.SameDidSubject(signerKid, skid))
            throw new ConsistencyException(
                $"Inner signed kid does not match outer authcrypt 'skid' (FR-CONSIST-05). signerKid='{signerKid}', skid='{skid}'.");
    }

    /// <summary>
    /// FR-CONSIST-06 — resolver-backed authorization check. This is the hook Phase 3 will
    /// flesh out via <c>IDidKeyService</c>: the supplied <paramref name="kid"/> must appear in
    /// the resolved DID document of <paramref name="assertedDid"/> under the required
    /// verification relationship (<c>keyAgreement</c> or <c>authentication</c>). The Phase 1
    /// implementation accepts a caller-supplied <paramref name="resolverCheck"/> delegate;
    /// when none is supplied the check is a no-op (logged via XML doc only — the wiring
    /// belongs to Phase 3).
    /// </summary>
    /// <param name="assertedDid">The DID asserted to control the key (the <c>from</c> or matched <c>to</c>).</param>
    /// <param name="kid">The key identifier whose presence in the DID document is being verified.</param>
    /// <param name="relationship">Either <c>"keyAgreement"</c> (for encrypt-side kids) or <c>"authentication"</c> (for signer kids).</param>
    /// <param name="resolverCheck">Pluggable predicate: returns true when the key is authorized. Phase 3 supplies the real implementation; pass <c>null</c> to short-circuit to "authorized".</param>
    /// <exception cref="ConsistencyException">When the resolver indicates the kid is not authorized.</exception>
    public static void CheckResolverAuthorization(
        string assertedDid,
        string kid,
        string relationship,
        Func<string, string, string, bool>? resolverCheck)
    {
        ArgumentException.ThrowIfNullOrEmpty(assertedDid);
        ArgumentException.ThrowIfNullOrEmpty(kid);
        ArgumentException.ThrowIfNullOrEmpty(relationship);

        if (resolverCheck is null) return;

        if (!resolverCheck(assertedDid, kid, relationship))
            throw new ConsistencyException(
                $"Key '{kid}' is not present under '{relationship}' in the resolved DID Document of '{assertedDid}' (FR-CONSIST-06).");
    }
}
