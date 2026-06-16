namespace DidComm.Crypto.KeyAgreement;

/// <summary>
/// The small set of JOSE curve/algorithm string mappings the DIDComm message layer still needs
/// after the envelope crypto moved to DataProofsDotnet.Jose: the JWS <c>alg</c> for a signer
/// curve (used by from_prior), and curve-eligibility predicates the resolver adapter uses to
/// filter resolved DID-document keys per verification relationship. All raw key handling lives in
/// DataProofsDotnet.Jose / NetCrypto, so this no longer references any key-type enum.
/// </summary>
internal static class KeyTypeMapper
{
    /// <summary>The JOSE signing algorithm matched to a JWK curve (Ed25519, P-256/384/521, secp256k1).</summary>
    /// <param name="crv">JWK <c>crv</c> value.</param>
    /// <exception cref="NotSupportedException">When the curve has no JWS algorithm mapping.</exception>
    public static string ToJwsAlgorithm(string crv) => crv switch
    {
        Jose.JoseAlgorithms.CrvEd25519 => Jose.JoseAlgorithms.EdDSA,
        Jose.JoseAlgorithms.CrvP256 => Jose.JoseAlgorithms.ES256,
        Jose.JoseAlgorithms.CrvP384 => Jose.JoseAlgorithms.ES384,
        Jose.JoseAlgorithms.CrvP521 => Jose.JoseAlgorithms.ES512,
        Jose.JoseAlgorithms.CrvSecp256k1 => Jose.JoseAlgorithms.ES256K,
        _ => throw new NotSupportedException($"Curve '{crv}' has no JWS algorithm mapping."),
    };

    /// <summary>True when <paramref name="crv"/> is one of the FR-ENC-01/02 key-agreement curves.</summary>
    /// <param name="crv">JWK <c>crv</c> value.</param>
    public static bool IsKeyAgreementCurve(string crv) =>
        crv is Jose.JoseAlgorithms.CrvX25519 or Jose.JoseAlgorithms.CrvP256
            or Jose.JoseAlgorithms.CrvP384 or Jose.JoseAlgorithms.CrvP521;

    /// <summary>True when <paramref name="crv"/> is a curve DIDComm can sign/verify with (FR-SIG-01).</summary>
    /// <param name="crv">JWK <c>crv</c> value.</param>
    public static bool IsSigningCurve(string crv) =>
        crv is Jose.JoseAlgorithms.CrvEd25519 or Jose.JoseAlgorithms.CrvP256
            or Jose.JoseAlgorithms.CrvP384 or Jose.JoseAlgorithms.CrvP521
            or Jose.JoseAlgorithms.CrvSecp256k1;
}
