using DidComm.Jose.Signing;
using DataProofsDotnet.Jose.Encryption;
using DpBase64Url = DataProofsDotnet.Jose.Base64Url;
using JwsSigner = DataProofsDotnet.Jose.Signing.JwsSigner;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Composition;

/// <summary>
/// Builds DataProofs operation handles from <em>extractable</em> private JWKs for the envelope-layer
/// tests that drive <c>EnvelopeWriter</c> / its parameter records directly. In production the facade
/// resolves these handles from the registered resolver — opaque (keystore) or extractable — and never
/// needs the raw scalar (FR-SEC-06); these helpers exist only so the low-level composition tests keep
/// using in-memory test keys.
/// </summary>
public static class TestHandles
{
    /// <summary>An in-process <see cref="IEcdhKey"/> over the private scalar (the extractable path).</summary>
    public static IEcdhKey ToEcdhKey(this Jwk privateJwk, JoseCryptoProvider crypto)
        => new RawEcdhKey(privateJwk.Crv!, DpBase64Url.Decode(privateJwk.D!), crypto);

    /// <summary>A <see cref="JwsSigner"/> built from the private signer JWK (the extractable path).</summary>
    public static JwsSigner ToJwsSigner(this Jwk privateJwk)
        => JwsSignerFactory.FromPrivateJwk(privateJwk);
}
