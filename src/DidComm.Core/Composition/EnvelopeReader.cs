using System.Text.Json;
using DidComm.Consistency;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Jose.Encryption;
using DidComm.Jose.Signing;
using DidComm.Json;
using DidComm.Messages;
using DidComm.Secrets;
using DidCommDefaultCryptoProvider = DidComm.Crypto.DefaultCryptoProvider;

namespace DidComm.Composition;

/// <summary>
/// High-level unpack orchestrator. Auto-detects the envelope structure (FR-API-03), recursively
/// unwraps nested compositions (anoncrypt(authcrypt), anoncrypt(sign), …), runs FR-CONSIST-01..05
/// checks as each layer reveals enough information, and returns an <see cref="UnpackResult"/>
/// carrying both the inner plaintext and the FR-API-04 metadata.
/// </summary>
internal static class EnvelopeReader
{
    /// <summary>Unpack <paramref name="packed"/> into its inner plaintext + metadata.</summary>
    /// <param name="packed">A packed DIDComm message: plaintext JWM, signed JWS, or encrypted JWE.</param>
    /// <param name="secretsLookup">Internal lookup for recipient private keys (decrypt path).</param>
    /// <param name="senderLookup">Internal lookup for sender public keys (authcrypt path); MAY be null when no authcrypt is expected.</param>
    /// <param name="signerLookup">Function returning the public JWK of a signer kid (verify path); MAY be null when no signed layers are expected.</param>
    /// <param name="cryptoProvider">DidComm crypto provider.</param>
    /// <exception cref="MalformedMessageException">When the input is not well-formed.</exception>
    /// <exception cref="CryptoException">When decryption / verification fails.</exception>
    /// <exception cref="ConsistencyException">When an addressing-consistency rule (FR-CONSIST-*) is violated.</exception>
    public static UnpackResult Unpack(
        string packed,
        IInternalSecretsLookup secretsLookup,
        IInternalSenderKeyLookup? senderLookup,
        Func<string, Jwk?>? signerLookup,
        DidCommDefaultCryptoProvider cryptoProvider)
    {
        ArgumentException.ThrowIfNullOrEmpty(packed);
        ArgumentNullException.ThrowIfNull(secretsLookup);
        ArgumentNullException.ThrowIfNull(cryptoProvider);

        var stack = new List<EnvelopeKind>();
        bool authenticated = false, nonRepudiation = false, anonymous = false, encrypted = false;
        string? contentEnc = null, keyWrap = null, sigAlg = null, signerKid = null, senderKid = null, recipientKid = null;
        IReadOnlyList<string> allRecipientKids = Array.Empty<string>();

        var current = packed;
        for (var depth = 0; depth < 4; depth++) // hard upper bound — no legal composition nests deeper
        {
            var kind = EnvelopeDetector.Detect(current);
            stack.Add(kind);

            switch (kind)
            {
                case EnvelopeKind.Plaintext:
                {
                    var message = JsonSerializer.Deserialize<Message>(current, DidCommJson.Default)
                        ?? throw new MalformedMessageException("Plaintext payload deserialized to null.");
                    message.Validate();

                    if (encrypted && recipientKid is not null)
                        AddressingConsistency.CheckRecipientKidInTo(message.To, recipientKid);

                    return new UnpackResult(
                        Message: message,
                        Stack: stack,
                        Encrypted: encrypted,
                        Authenticated: authenticated,
                        NonRepudiation: nonRepudiation,
                        // The two flags are independent (matches SICPA reference impl):
                        // anonymous reflects whether the *outermost* encrypt layer was
                        // anoncrypt (true) or authcrypt (false); authenticated reflects
                        // whether any layer (signed or authcrypt) bound a sender identity.
                        // anon-encrypt + inner-sign sets both flags.
                        AnonymousSender: anonymous,
                        ContentEncryption: contentEnc,
                        KeyWrap: keyWrap,
                        SignatureAlgorithm: sigAlg,
                        SignerKid: signerKid,
                        SenderKid: senderKid,
                        RecipientKid: recipientKid,
                        AllRecipientKids: allRecipientKids);
                }

                case EnvelopeKind.Signed:
                {
                    if (signerLookup is null)
                        throw new CryptoException("Signed envelope encountered but no signer-key lookup was supplied.");

                    var jwsResult = JwsParser.Parse(current, signerLookup, cryptoProvider);
                    nonRepudiation = true;
                    // A verified JWS authenticates the signer (the FR-API-04 metadata semantics
                    // treat 'authenticated' as "sender identity cryptographically confirmed").
                    // For an outer JWS with no encrypt layer the signer == sender.
                    authenticated = true;
                    sigAlg = jwsResult.SignatureAlgorithm;
                    signerKid = jwsResult.SignerKid;

                    if (encrypted && jwsResult.Message.To is null)
                        throw new ConsistencyException(
                            "Sign-then-encrypt composition: the inner signed JWM MUST carry 'to' (FR-SIG-06).");

                    // FR-CONSIST-03 already enforced inside JwsParser.

                    current = Encoding.UTF8.GetString(jwsResult.PayloadBytes);
                    // Inner JWS payload is the canonical plaintext JWM — return on next loop.
                    break;
                }

                case EnvelopeKind.Encrypted:
                {
                    var jweResult = JweParser.Parse(current, secretsLookup, senderLookup, cryptoProvider);
                    encrypted = true;
                    contentEnc = jweResult.ContentEncryption;
                    keyWrap = jweResult.Algorithm;
                    recipientKid = jweResult.RecipientKid;
                    allRecipientKids = jweResult.AllRecipientKids;

                    if (jweResult.IsAuthenticated)
                    {
                        authenticated = true;
                        senderKid = jweResult.SenderKid;
                    }
                    else
                    {
                        anonymous = true;
                    }

                    current = Encoding.UTF8.GetString(jweResult.Plaintext);
                    break;
                }
            }
        }

        throw new MalformedMessageException("Envelope nesting exceeded the legal depth of 4.");
    }
}
