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
/// unwraps nested compositions (anoncrypt(authcrypt), anoncrypt(sign), …), runs the addressing-
/// consistency checks FR-CONSIST-01/02/03/05 as each layer reveals enough information
/// (FR-CONSIST-04 is an advisory SHOULD and FR-CONSIST-06's resolver-backed authorization is
/// wired in Phase 3), and returns an <see cref="UnpackResult"/> carrying both the inner plaintext
/// and the FR-API-04 metadata.
/// </summary>
internal static class EnvelopeReader
{
    /// <summary>Unpack <paramref name="packed"/> into its inner plaintext + metadata.</summary>
    /// <param name="packed">A packed DIDComm message: plaintext JWM, signed JWS, or encrypted JWE.</param>
    /// <param name="secretsLookup">Internal lookup for recipient private keys (decrypt path).</param>
    /// <param name="senderLookup">Internal lookup for sender public keys (authcrypt path); MAY be null when no authcrypt is expected.</param>
    /// <param name="signerLookup">Function returning the public JWK of a signer kid (verify path); MAY be null when no signed layers are expected.</param>
    /// <param name="cryptoProvider">DidComm crypto provider.</param>
    /// <param name="resolverCheck">
    /// FR-CONSIST-06 resolver-backed authorization predicate <c>(assertedDid, kid, relationship) =&gt; isAuthorized</c>.
    /// When non-null, the unpack pipeline asserts the inner plaintext's sender / recipient / signer kids are present
    /// under the resolved DID Document's matching relationship. Pass <c>null</c> to short-circuit the check (Phase 2 default).
    /// </param>
    /// <exception cref="MalformedMessageException">When the input is not well-formed.</exception>
    /// <exception cref="CryptoException">When decryption / verification fails.</exception>
    /// <exception cref="ConsistencyException">When an addressing-consistency rule (FR-CONSIST-*) is violated.</exception>
    public static UnpackResult Unpack(
        string packed,
        IInternalSecretsLookup secretsLookup,
        IInternalSenderKeyLookup? senderLookup,
        Func<string, Jwk?>? signerLookup,
        DidCommDefaultCryptoProvider cryptoProvider,
        Func<string, string, string, bool>? resolverCheck = null)
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

                    // FR-CONSIST-01 — an authcrypt layer (skid present) MUST agree with the
                    // plaintext 'from' DID subject; otherwise the envelope claims a sender it
                    // did not cryptographically authenticate.
                    if (senderKid is not null)
                        AddressingConsistency.CheckAuthcryptFromMatchesSkid(message.From, senderKid);

                    // FR-CONSIST-06 — the kids surfaced by the cryptographic layers must be
                    // genuinely authorized in their asserted DID Documents. The predicate is
                    // supplied by the caller (the Phase 3 facade backs it with IDidKeyService);
                    // when null the check is skipped (Phase 2 default).
                    if (resolverCheck is not null)
                    {
                        if (senderKid is not null && message.From is not null)
                            AddressingConsistency.CheckResolverAuthorization(message.From, senderKid, "keyAgreement", resolverCheck);

                        if (encrypted && recipientKid is not null)
                        {
                            var recipientDid = DidSubject.DidSubjectOf(recipientKid);
                            if (recipientDid is not null)
                                AddressingConsistency.CheckResolverAuthorization(recipientDid, recipientKid, "keyAgreement", resolverCheck);
                        }

                        if (signerKid is not null && message.From is not null)
                            AddressingConsistency.CheckResolverAuthorization(message.From, signerKid, "authentication", resolverCheck);
                    }

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

                    // FR-CONSIST-03 (signer kid ↔ from) already enforced inside JwsParser.
                    // FR-CONSIST-05 — when this signature sits inside an authcrypt layer
                    // (authcrypt(sign(…))), the inner signer MUST be the same DID subject as the
                    // outer authcrypt sender (skid).
                    if (senderKid is not null)
                        AddressingConsistency.CheckAuthcryptInnerSignerMatchesSkid(jwsResult.SignerKid, senderKid);

                    current = Encoding.UTF8.GetString(jwsResult.PayloadBytes);
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

                default:
                    throw new MalformedMessageException($"Unrecognized envelope kind '{kind}'.");
            }
        }

        throw new MalformedMessageException("Envelope nesting exceeded the legal depth of 4.");
    }
}
