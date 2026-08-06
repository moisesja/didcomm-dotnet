namespace DidComm.Protocols.Rotation;

/// <summary>
/// The validated JWT claims carried by a DIDComm <c>from_prior</c> header per DIDComm v2.1
/// §5.6 / FR-ROT-01..04. Surfaced on the unpack-side metadata after the rotation JWT has been
/// verified against the prior DID's <c>authentication</c> relationship.
/// </summary>
/// <remarks>
/// A <c>from_prior</c> whose JWT omits <c>sub</c> is the <b>relationship-termination</b> form
/// (FR-ROT-06): the prior DID announces there is no successor identity, and the carrying
/// message MUST be sent without <c>from</c>. <see cref="IsTermination"/> distinguishes the two
/// forms; consumers of <c>UnpackResult.FromPrior</c> check it before treating the claims as a
/// rotation.
/// </remarks>
/// <param name="Sub">New DID — MUST equal the message <c>from</c> (FR-ROT-02). <c>null</c> for the relationship-termination form (FR-ROT-06), which MUST ride on a message without <c>from</c>.</param>
/// <param name="Iss">Prior DID — issuer of the rotation JWT.</param>
/// <param name="Iat">Issued-at, UTC epoch seconds. Together with the local clock the relying party detects out-of-order pre-rotation messages (FR-ROT-05).</param>
/// <param name="Exp">OPTIONAL expiry, UTC epoch seconds. When present the unpack pipeline rejects an expired rotation JWT (FR-ROT-05 freshness).</param>
/// <param name="Nbf">OPTIONAL not-before, UTC epoch seconds. When present the unpack pipeline rejects a not-yet-valid rotation JWT.</param>
/// <example>
/// <code>
/// var rotation = new FromPriorClaims(Sub: "did:example:new", Iss: "did:example:prior", Iat: 1700000000);
/// var termination = FromPriorClaims.ForTermination(Iss: "did:example:prior", Iat: 1700000000);
/// // termination.IsTermination == true; termination.Sub == null
/// </code>
/// </example>
public sealed record FromPriorClaims(string? Sub, string Iss, long Iat, long? Exp = null, long? Nbf = null)
{
    /// <summary>
    /// <c>true</c> when the JWT omitted <c>sub</c> — the relationship-termination form
    /// (FR-ROT-06). The prior DID (<see cref="Iss"/>) is ending the relationship rather than
    /// rotating to a successor, and the carrying message has no <c>from</c>.
    /// </summary>
    public bool IsTermination => Sub is null;

    /// <summary>
    /// Construct the relationship-termination claims (FR-ROT-06): <c>sub</c> omitted, so the
    /// prior DID announces the relationship ends with no successor identity.
    /// </summary>
    /// <param name="Iss">Prior DID — issuer of the termination JWT.</param>
    /// <param name="Iat">Issued-at, UTC epoch seconds.</param>
    /// <param name="Exp">OPTIONAL expiry, UTC epoch seconds — bounds replay of the captured termination JWT.</param>
    /// <param name="Nbf">OPTIONAL not-before, UTC epoch seconds.</param>
    public static FromPriorClaims ForTermination(string Iss, long Iat, long? Exp = null, long? Nbf = null)
        => new(Sub: null, Iss: Iss, Iat: Iat, Exp: Exp, Nbf: Nbf);
}
