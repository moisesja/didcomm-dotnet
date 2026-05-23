using DidComm.Jose;

namespace DidComm.Secrets;

/// <summary>
/// Minimal internal contract that the envelope layer uses to fetch private keys for
/// decrypt / sign-then-encrypt-inner-sign / authcrypt-sender operations. Phase 2 ships
/// this strictly as an internal API; Phase 3 introduces the public
/// <c>ISecretsResolver</c> documented in FR-SEC-01 and adapts.
/// </summary>
/// <remarks>
/// Splitting the lookup from the public resolver keeps Phase 2 testable (an in-memory
/// stub seeded from Appendix A satisfies the contract trivially) without locking in
/// the public secrets API before the Phase 3 facade has stress-tested it.
/// </remarks>
internal interface IInternalSecretsLookup
{
    /// <summary>
    /// Look up the private JWK matching <paramref name="kid"/>. Returns <c>null</c> when no
    /// matching secret is held; the envelope layer treats null as "skip this recipient" rather
    /// than as an error (next recipient may match).
    /// </summary>
    /// <param name="kid">Key identifier — typically a DID URL with a fragment.</param>
    Jwk? TryGet(string kid);

    /// <summary>
    /// Return the subset of <paramref name="kids"/> for which a matching private key is held.
    /// Used by the unpack pipeline to find candidate recipient entries in a multi-recipient
    /// JWE without doing N independent lookups (FR-SEC-01 "find present subset").
    /// </summary>
    /// <param name="kids">Candidate kids to test.</param>
    IReadOnlyList<string> FindPresent(IEnumerable<string> kids);
}
