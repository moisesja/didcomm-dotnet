namespace DidComm.Protocols.Rotation;

/// <summary>
/// The validated JWT claims carried by a DIDComm <c>from_prior</c> header per DIDComm v2.1
/// §5.6 / FR-ROT-01..04. Surfaced on the unpack-side metadata after the rotation JWT has been
/// verified against the prior DID's <c>authentication</c> relationship.
/// </summary>
/// <param name="Sub">New DID — MUST equal the message <c>from</c> (FR-ROT-02).</param>
/// <param name="Iss">Prior DID — issuer of the rotation JWT.</param>
/// <param name="Iat">Issued-at, UTC epoch seconds. Together with the local clock the relying party detects out-of-order pre-rotation messages (FR-ROT-05).</param>
/// <param name="Exp">OPTIONAL expiry, UTC epoch seconds. When present the unpack pipeline rejects an expired rotation JWT (FR-ROT-05 freshness).</param>
/// <param name="Nbf">OPTIONAL not-before, UTC epoch seconds. When present the unpack pipeline rejects a not-yet-valid rotation JWT.</param>
public sealed record FromPriorClaims(string Sub, string Iss, long Iat, long? Exp = null, long? Nbf = null);
