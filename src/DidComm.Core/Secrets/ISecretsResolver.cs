using DidComm.Jose;

namespace DidComm.Secrets;

/// <summary>
/// Consumer-supplied private-key resolver (FR-SEC-01). The library never stores keys itself
/// (DD-02); applications register an implementation backed by an HSM, cloud KMS, encrypted
/// store, or — for tests — an in-memory map. The contract is asynchronous because production
/// backings are I/O bound.
/// </summary>
/// <remarks>
/// <para>
/// The facade pre-resolves all secrets it needs before invoking the envelope layer
/// (see PRD §7 — the JOSE composition layer remains synchronous). Lookups occur at pack time
/// (to sign / encrypt) and at unpack time (to choose the recipient entry that the local agent
/// can decrypt).
/// </para>
/// <para>
/// A null return from <see cref="FindAsync"/> on an unpack path is non-fatal — the multi-
/// recipient JWE has another candidate. A null return on a pack path raises
/// <see cref="Exceptions.SecretNotFoundException"/> via the facade.
/// </para>
/// </remarks>
public interface ISecretsResolver
{
    /// <summary>
    /// Look up the private JWK matching <paramref name="kid"/>. Returns <c>null</c> when no
    /// matching secret is held.
    /// </summary>
    /// <param name="kid">Key identifier — typically a DID URL with a fragment.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Jwk?> FindAsync(string kid, CancellationToken ct = default);

    /// <summary>
    /// Return the subset of <paramref name="kids"/> for which a matching private key is held.
    /// Used by the facade on unpack to identify which recipient entries in a multi-recipient
    /// JWE the local agent can decrypt without performing <c>N</c> individual lookups.
    /// </summary>
    /// <param name="kids">Candidate kids to test.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default);
}
