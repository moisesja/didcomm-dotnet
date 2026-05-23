using System.Text.Json;
using DidComm.Crypto;
using DidComm.Crypto.KeyAgreement;
using DidComm.Exceptions;
using DidComm.Json;
using DidComm.Messages;
using NetDidJwkConverter = NetDid.Core.Jwk.JwkConverter;

namespace DidComm.Jose.Signing;

/// <summary>
/// Builds a DIDComm signed envelope (JWS, JSON serialization) per FR-SIG-01..06. The
/// payload is the base64url-encoded plaintext JWM bytes (FR-SIG-04); the protected header
/// carries <c>alg</c> (per signer key), <c>kid</c> (signer DID URL), and <c>typ</c>.
/// </summary>
/// <remarks>
/// <para>
/// Emits **Flattened** JSON serialization when there is exactly one signer (the FR-SIG-02
/// "Flattened is sufficient" rule) and **General** JSON when there are two or more.
/// </para>
/// <para>
/// Caller supplies the inner <see cref="Message"/> already validated (the builder runs
/// <see cref="Message.Validate"/> defensively) plus the signer's private key as a JWK whose
/// <c>kid</c> + <c>crv</c> select the signing algorithm via <see cref="KeyTypeMapper.ToJwsAlgorithm"/>.
/// </para>
/// </remarks>
internal static class JwsBuilder
{
    /// <summary>Build the packed JWS string from a plaintext message and one or more signer keys.</summary>
    /// <param name="message">Plaintext JWM. Will be canonically serialized and base64url-encoded as the JWS payload.</param>
    /// <param name="signerPrivateJwks">Signer key(s). Each must have <c>kid</c>, <c>crv</c>, and <c>d</c> populated.</param>
    /// <param name="cryptoProvider">Crypto provider used to compute signatures (delegated to net-did).</param>
    /// <param name="requireInnerToHeader">If true, refuse to build when the message has no <c>to</c> header — the sign-then-encrypt anti-surreptitious-forwarding rule (FR-SIG-06).</param>
    /// <exception cref="MalformedMessageException">Inputs are missing required fields or violate FR-SIG-06.</exception>
    public static string Build(
        Message message,
        IReadOnlyList<Jwk> signerPrivateJwks,
        ICryptoProvider cryptoProvider,
        bool requireInnerToHeader = false)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(signerPrivateJwks);
        ArgumentNullException.ThrowIfNull(cryptoProvider);
        if (signerPrivateJwks.Count == 0)
            throw new ArgumentException("At least one signer key is required.", nameof(signerPrivateJwks));

        message.Validate();
        if (requireInnerToHeader && (message.To is null || message.To.Count == 0))
            throw new MalformedMessageException(
                "Sign-then-encrypt requires the inner signed JWM to carry a 'to' header (FR-SIG-06).");

        var payloadBytes = SerializeMessage(message);
        var payloadB64u = Base64Url.Encode(payloadBytes);

        var signatures = new List<SignatureEntry>(signerPrivateJwks.Count);
        foreach (var signer in signerPrivateJwks)
        {
            ValidateSignerJwk(signer);
            var header = BuildProtectedHeader(signer);
            var protectedB64u = header.EncodeBase64Url();
            var signingInput = Encoding.ASCII.GetBytes(protectedB64u + "." + payloadB64u);
            var privateScalar = DecodePrivateScalar(signer);
            var signature = cryptoProvider.Sign(header.Alg, privateScalar, signingInput);
            CryptographicOperations.ZeroMemory(privateScalar);
            signatures.Add(new SignatureEntry(protectedB64u, signer.Kid!, signature));
        }

        return signatures.Count == 1
            ? RenderFlattened(payloadB64u, signatures[0])
            : RenderGeneral(payloadB64u, signatures);
    }

    private static byte[] SerializeMessage(Message message)
    {
        var node = JsonSerializer.SerializeToNode(message, DidCommJson.Default);
        return DeterministicJsonWriter.WriteUtf8(node);
    }

    private static JwsProtectedHeader BuildProtectedHeader(Jwk signer)
    {
        var alg = KeyTypeMapper.ToJwsAlgorithm(signer.Crv!);
        return new JwsProtectedHeader
        {
            Alg = alg,
            Kid = signer.Kid!,
            Typ = MediaTypes.Signed,
        };
    }

    private static void ValidateSignerJwk(Jwk signer)
    {
        if (signer is null) throw new ArgumentException("Signer JWK cannot be null.", nameof(signer));
        if (string.IsNullOrEmpty(signer.Kid))
            throw new MalformedMessageException("Signer JWK is missing 'kid'.");
        if (string.IsNullOrEmpty(signer.Crv))
            throw new MalformedMessageException("Signer JWK is missing 'crv'.");
        if (string.IsNullOrEmpty(signer.D))
            throw new MalformedMessageException("Signer JWK is missing private-key material ('d').");
    }

    private static byte[] DecodePrivateScalar(Jwk signer) => Base64Url.Decode(signer.D!);

    private static string RenderFlattened(string payloadB64u, SignatureEntry entry)
    {
        // Flattened JSON form: { payload, protected, header, signature }
        return JsonSerializer.Serialize(new
        {
            payload = payloadB64u,
            @protected = entry.ProtectedB64u,
            header = new { kid = entry.Kid },
            signature = Base64Url.Encode(entry.Signature),
        });
    }

    private static string RenderGeneral(string payloadB64u, IReadOnlyList<SignatureEntry> signatures)
    {
        // General JSON form: { payload, signatures: [{ protected, header, signature }, …] }
        var entries = signatures.Select(s => new
        {
            @protected = s.ProtectedB64u,
            header = new { kid = s.Kid },
            signature = Base64Url.Encode(s.Signature),
        }).ToArray();

        return JsonSerializer.Serialize(new { payload = payloadB64u, signatures = entries });
    }

    private readonly record struct SignatureEntry(string ProtectedB64u, string Kid, byte[] Signature);
}
