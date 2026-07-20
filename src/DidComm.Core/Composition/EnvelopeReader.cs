using System.Text.Json;
using DidComm.Consistency;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Json;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Secrets;
using DpEnc = DataProofsDotnet.Jose.Encryption;
using DpSig = DataProofsDotnet.Jose.Signing;
using JoseAlgorithms = DataProofsDotnet.Jose.JoseAlgorithms;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Composition;

/// <summary>
/// High-level unpack orchestrator. Auto-detects the envelope structure (FR-API-03), recursively
/// unwraps nested compositions (anoncrypt(authcrypt), anoncrypt(sign), …) by delegating each JWE/
/// JWS layer to DataProofsDotnet.Jose, runs the addressing-consistency checks FR-CONSIST-01/02/03/05
/// as each layer reveals enough information (FR-CONSIST-04 is an advisory SHOULD;
/// FR-CONSIST-06's resolver-backed authorization is supplied by the facade), and returns an
/// <see cref="UnpackResult"/> carrying both the inner plaintext and the FR-API-04 metadata.
/// </summary>
/// <remarks>
/// <para>
/// The JOSE layer (DataProofsDotnet.Jose) verifies signatures and decrypts but knows nothing of the
/// DIDComm plaintext <c>from</c>/<c>to</c> headers, so it returns the signer/sender/recipient kids
/// (and the verified payload bytes) and this reader binds them to the message-layer addressing
/// rules. In particular FR-CONSIST-03 (signed <c>from</c> ↔ signer kid) and FR-SIG-06 (an inner
/// signed JWM under encryption MUST carry <c>to</c>) are enforced here, against the deserialized
/// inner message, since the JWS parser returns raw payload bytes rather than a DIDComm message.
/// </para>
/// <para>
/// <strong>Opaque key agreement (FR-SEC-06).</strong> Each encrypt layer is decrypted through the
/// async <c>JweParser.ParseAsync</c> with an <c>IEcdhKey</c> the <see cref="KeyOperationResolver"/>
/// resolves for the held recipient kid — opaque (keystore/HSM) or extractable. The recipient private
/// scalar never enters this layer on the opaque path. The reader always invokes <c>ParseAsync</c>
/// (with a throwaway decoy handle when no recipient key is held), so from <c>ParseAsync</c> inward the
/// ECDH/unwrap cost and the uniform decryption failure are constant-work — independent of which (or
/// whether) recipient key the agent holds (dataproofs #12). Any opaque-handle fault is folded into that
/// same uniform failure so exception type/shape leaks nothing either. The held path does perform one
/// extra backing-store lookup to fetch the key before <c>ParseAsync</c> (the unheld path goes straight
/// to the in-process decoy); on a slow store that prologue is the consumer resolver's responsibility,
/// outside this layer's constant-work guarantee (as <c>JweParser</c> documents) and covered at the
/// transport boundary by the receive rejection floor (issue #35).
/// </para>
/// </remarks>
internal static class EnvelopeReader
{
    /// <summary>Unpack <paramref name="packed"/> into its inner plaintext + metadata.</summary>
    /// <param name="packed">A packed DIDComm message: plaintext JWM, signed JWS, or encrypted JWE.</param>
    /// <param name="recipientKeys">Resolves the recipient ECDH key-agreement handle (opaque or extractable) for the decrypt path, and answers held-ness for recipient selection.</param>
    /// <param name="senderLookup">Internal lookup for sender public keys (authcrypt path); MAY be null when no authcrypt is expected.</param>
    /// <param name="signerLookup">Function returning the public JWK of a signer kid (verify path); MAY be null when no signed layers are expected.</param>
    /// <param name="cryptoProvider">JOSE crypto provider (NetCrypto-backed).</param>
    /// <param name="resolverCheck">
    /// FR-CONSIST-06 resolver-backed authorization predicate <c>(assertedDid, kid, relationship, ct) =&gt; isAuthorized</c>.
    /// When non-null, the unpack pipeline asserts the inner plaintext's sender / recipient / signer kids are present
    /// under the resolved DID Document's matching relationship. Pass <c>null</c> to short-circuit the check.
    /// </param>
    /// <param name="ct">Cancellation token for the (possibly I/O-bound) key agreement and DID resolution.</param>
    /// <exception cref="MalformedMessageException">When the input is not well-formed.</exception>
    /// <exception cref="CryptoException">When decryption / verification fails.</exception>
    /// <exception cref="ConsistencyException">When an addressing-consistency rule (FR-CONSIST-*) is violated.</exception>
    public static async Task<UnpackResult> UnpackAsync(
        string packed,
        KeyOperationResolver recipientKeys,
        IInternalSenderKeyLookup? senderLookup,
        Func<string, Jwk?>? signerLookup,
        JoseCryptoProvider cryptoProvider,
        Func<string, string, string, CancellationToken, Task<bool>>? resolverCheck = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packed);
        ArgumentNullException.ThrowIfNull(recipientKeys);
        ArgumentNullException.ThrowIfNull(cryptoProvider);

        var stack = new List<EnvelopeKind>();
        // Per-layer composition trace (outer→inner) carrying the authenticated/anonymous distinction
        // the EnvelopeKind stack can't (anoncrypt and authcrypt are both EnvelopeKind.Encrypted). Used
        // by AssertLegalComposition to reject illegal layer orderings — e.g. the illegal
        // anoncrypt(anoncrypt) vs the legal anoncrypt(authcrypt) (FR-ENV-02/04, issue #17).
        var shape = new List<LayerShape>();
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
                    // Reject illegal envelope compositions before any content/consistency work: the legal
                    // receive grammar is anoncrypt? authcrypt? sign? plaintext (DIDComm v2.1 Appendix C).
                    // The auth/anon flag on each encrypt layer is what separates the legal anoncrypt(authcrypt)
                    // from the illegal anoncrypt(anoncrypt). (Issue #17.)
                    shape.Add(LayerShape.Plaintext);
                    AssertLegalComposition(shape);

                    Message? deserialized;
                    try
                    {
                        deserialized = JsonSerializer.Deserialize<Message>(current, DidCommJson.Default);
                    }
                    catch (JsonException ex)
                    {
                        // Covers malformed members and converter rejections (e.g. an out-of-range
                        // epoch-seconds value) so they surface as the standard malformed-message error
                        // rather than an undocumented JsonException at the public unpack boundary.
                        throw new MalformedMessageException("Plaintext payload is not a valid DIDComm message.", ex);
                    }
                    var message = deserialized
                        ?? throw new MalformedMessageException("Plaintext payload deserialized to null.");
                    message.Validate();

                    if (encrypted && recipientKid is not null)
                        AddressingConsistency.CheckRecipientKidInTo(message.To, recipientKid);

                    // FR-CONSIST-01 — an authcrypt layer (skid present) MUST agree with the
                    // plaintext 'from' DID subject; otherwise the envelope claims a sender it
                    // did not cryptographically authenticate.
                    if (senderKid is not null)
                        AddressingConsistency.CheckAuthcryptFromMatchesSkid(message.From, senderKid);

                    // FR-CONSIST-03 — a signed layer's signer kid MUST agree with the plaintext
                    // 'from' DID subject. The JOSE layer verified the signature and surfaced the
                    // signer kid; the from↔signer binding is a message-layer rule enforced here
                    // (DataProofsDotnet.Jose's JwsParser returns bytes, not a DIDComm message).
                    if (signerKid is not null)
                        AddressingConsistency.CheckSignedFromMatchesSignerKid(message.From, signerKid);

                    // FR-SIG-06 — a signed JWM nested inside an encrypt layer MUST carry 'to' (the
                    // anti-surreptitious-forwarding rule). Previously enforced inside the JWS parser
                    // against the deserialized payload; re-homed here for the same reason.
                    if (encrypted && nonRepudiation && (message.To is null || message.To.Count == 0))
                        throw new ConsistencyException(
                            "Sign-then-encrypt composition: the inner signed JWM MUST carry 'to' (FR-SIG-06).");

                    // FR-CONSIST-06 — the kids surfaced by the cryptographic layers must be
                    // genuinely authorized in their asserted DID Documents. The predicate is
                    // supplied by the facade (backed by IDidKeyService); when null the check is skipped.
                    if (resolverCheck is not null)
                    {
                        if (senderKid is not null && message.From is not null)
                            await AddressingConsistency.CheckResolverAuthorizationAsync(message.From, senderKid, "keyAgreement", resolverCheck, ct).ConfigureAwait(false);

                        if (encrypted && recipientKid is not null)
                        {
                            var recipientDid = DidSubject.DidSubjectOf(recipientKid);
                            if (recipientDid is not null)
                                await AddressingConsistency.CheckResolverAuthorizationAsync(recipientDid, recipientKid, "keyAgreement", resolverCheck, ct).ConfigureAwait(false);
                        }

                        if (signerKid is not null && message.From is not null)
                            await AddressingConsistency.CheckResolverAuthorizationAsync(message.From, signerKid, "authentication", resolverCheck, ct).ConfigureAwait(false);
                    }

                    // Preserve the exact verified plaintext and the trust fields established above
                    // before the mutable public Message leaves the unpack boundary. Protocol
                    // correlators and observers consume this immutable sidecar, so later caller or
                    // handler mutation cannot rewrite the identity/body they act on.
                    InboundMessageSnapshot.RegisterVerified(
                        message,
                        current,
                        encrypted,
                        authenticated,
                        nonRepudiation,
                        anonymous,
                        senderKid,
                        signerKid,
                        recipientKid);

                    return new UnpackResult(
                        Message: message,
                        Stack: stack,
                        Encrypted: encrypted,
                        Authenticated: authenticated,
                        NonRepudiation: nonRepudiation,
                        // The two flags are independent (matches SICPA reference impl):
                        // anonymous is derived strictly from the *outermost* encrypt layer's alg —
                        // anoncrypt (true) or authcrypt (false), set once when that layer is unwrapped
                        // (#23) — while authenticated reflects whether any layer (signed or authcrypt)
                        // bound a sender identity. anoncrypt-encrypt + inner-sign sets both flags.
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
                    shape.Add(LayerShape.Sign);
                    if (signerLookup is null)
                        throw new CryptoException("Signed envelope encountered but no signer-key lookup was supplied.");

                    DpSig.JwsParseResult jwsResult;
                    try
                    {
                        jwsResult = DpSig.JwsParser.Parse(current, signerLookup, cryptoProvider);
                    }
                    catch (DataProofsDotnet.Jose.MalformedJoseException ex)
                    {
                        throw new MalformedMessageException(ex.Message, ex);
                    }
                    catch (DataProofsDotnet.Jose.JoseCryptoException ex)
                    {
                        throw new CryptoException(ex.Message, ex);
                    }
                    catch (ArgumentException ex)
                    {
                        // Defensive boundary guard (#22, FR-API-07): map any ArgumentException surfacing
                        // from the delegated JWS parse (a non-canonical field length, or a throwing
                        // consumer signer lookup) to the documented unpack contract, so a raw
                        // ArgumentException never escapes UnpackAsync. InnerException is preserved.
                        throw new MalformedMessageException("Malformed JWS.", ex);
                    }

                    // Fail closed: a verified JWS MUST surface a signer kid. Otherwise FR-CONSIST-03
                    // (signed 'from' ↔ signer) and FR-CONSIST-05 (inner signer ↔ skid) would silently
                    // no-op below while the message is still reported authenticated — letting 'from'
                    // assert an identity the signature never bound. Assert it here so the identity
                    // binding never depends on the delegated parser always populating the kid.
                    if (string.IsNullOrEmpty(jwsResult.SignerKid))
                        throw new CryptoException(
                            "Verified JWS did not surface a signer kid; cannot bind the message 'from' to the signer (FR-CONSIST-03).");

                    nonRepudiation = true;
                    // A verified JWS authenticates the signer (the FR-API-04 metadata semantics
                    // treat 'authenticated' as "sender identity cryptographically confirmed").
                    // For an outer JWS with no encrypt layer the signer == sender.
                    authenticated = true;
                    sigAlg = jwsResult.SignatureAlgorithm;
                    signerKid = jwsResult.SignerKid;

                    // FR-CONSIST-05 — when this signature sits inside an authcrypt layer
                    // (authcrypt(sign(…))), the inner signer MUST be the same DID subject as the
                    // outer authcrypt sender (skid). FR-CONSIST-03 and FR-SIG-06 are deferred to
                    // the Plaintext branch, which has the deserialized inner message.
                    if (senderKid is not null)
                        AddressingConsistency.CheckAuthcryptInnerSignerMatchesSkid(jwsResult.SignerKid, senderKid);

                    current = Encoding.UTF8.GetString(jwsResult.PayloadBytes);
                    break;
                }

                case EnvelopeKind.Encrypted:
                {
                    DpEnc.JweParseResult jweResult;
                    try
                    {
                        // Discover the recipient kids without any private key, select the held one, and
                        // resolve its opaque-or-extractable ECDH handle. When none is held we still drive
                        // a full ParseAsync with a decoy handle — the parser's constant-work path
                        // (dataproofs #12) makes the ECDH/unwrap cost and the uniform failure identical
                        // to the held path, closing the held-vs-unheld recipient-enumeration oracle.
                        var peek = DpEnc.JweParser.PeekRecipients(current);
                        var recipientKey = await ResolveRecipientKeyOrDecoyAsync(recipientKeys, peek.RecipientKids, cryptoProvider, ct).ConfigureAwait(false);
                        jweResult = await DpEnc.JweParser.ParseAsync(current, recipientKey, senderLookup, cryptoProvider, ct).ConfigureAwait(false);
                    }
                    catch (DataProofsDotnet.Jose.MalformedJoseException ex)
                    {
                        throw new MalformedMessageException(ex.Message, ex);
                    }
                    catch (DataProofsDotnet.Jose.JoseCryptoException ex)
                    {
                        throw new CryptoException(ex.Message, ex);
                    }
                    catch (ArgumentException ex)
                    {
                        // Defensive boundary guard (#22, FR-API-07). The delegated DataProofsDotnet.Jose
                        // parser currently wraps the AEAD's wrong-length-iv/tag ArgumentException as
                        // MalformedJoseException (caught above), but EnvelopeReader's contract promises
                        // only MalformedMessageException/CryptoException — so map ANY ArgumentException
                        // surfacing from the delegated parse to the contract type rather than let a raw
                        // one escape UnpackAsync. Neutral message (the cause may be a field length or a
                        // throwing consumer lookup); the original is preserved as InnerException.
                        throw new MalformedMessageException("Malformed JWE.", ex);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException
                        and not MalformedMessageException and not CryptoException and not ConsistencyException)
                    {
                        // Constant-work + FR-API-07: an opaque (keystore/HSM) recipient handle can fault in
                        // ways the in-process path never does — a KeyNotFoundException when a kid is rotated
                        // out between selection and derive, a CryptographicException from the enclave, or any
                        // backing-store error. Fold ALL of them into the SAME uniform decryption failure as a
                        // wrong-key / tampered-ciphertext failure, so (a) no raw exception escapes the unpack
                        // contract and (b) the opaque path cannot reintroduce a recipient-possession oracle by
                        // exception type/shape (the held path would otherwise throw a distinct type the decoy
                        // path — an in-process RawEcdhKey — never can). The cause is preserved as
                        // InnerException for diagnosis; cancellation is allowed to propagate.
                        throw new CryptoException("JWE could not be decrypted.", ex);
                    }

                    // The loop unwraps outermost→inner, so the first encrypt layer is the outermost one.
                    // AnonymousSender is documented as "the outermost encrypt layer was anoncrypt", so
                    // derive it from THIS layer's alg only when it's the outermost encrypt — not by
                    // OR-accumulating across layers (#23). For legal shapes the #17 gate already
                    // guarantees anoncrypt-if-present is outermost; deriving here keeps the flag correct
                    // by construction regardless.
                    var isOutermostEncryptLayer = !encrypted;
                    encrypted = true;
                    contentEnc = jweResult.ContentEncryption;
                    keyWrap = jweResult.Algorithm;
                    recipientKid = jweResult.RecipientKid;
                    allRecipientKids = jweResult.AllRecipientKids;

                    if (isOutermostEncryptLayer)
                        anonymous = !jweResult.IsAuthenticated;

                    if (jweResult.IsAuthenticated)
                    {
                        // Fail closed: an authenticated decrypt MUST surface the sender skid,
                        // else the FR-CONSIST-01 from↔skid check in the Plaintext branch would
                        // silently no-op while the message is still reported authenticated —
                        // letting 'from' assert an identity the envelope never bound. Guaranteed
                        // by DataProofs' IsAuthenticated ⟺ non-empty-skid contract today; assert
                        // it so the identity binding never depends on the delegated parser
                        // upholding it (issue #52; parity with the JWS signer-kid guard above).
                        AddressingConsistency.CheckAuthcryptSkidSurfaced(jweResult.SenderKid);
                        authenticated = true;
                        senderKid = jweResult.SenderKid;
                        shape.Add(LayerShape.AuthEncrypt);
                    }
                    else
                    {
                        shape.Add(LayerShape.AnonEncrypt);
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

    /// <summary>
    /// Resolve the ECDH key-agreement handle the encrypt layer is decrypted with: the first held
    /// recipient kid's handle (opaque or extractable), or — when none is held or usable — a throwaway
    /// decoy on a supported curve. The decoy keeps the per-layer work constant whether or not the
    /// agent holds a recipient key (dataproofs #12); the parser additionally swaps in its own
    /// work-curve decoy when this handle's curve doesn't match the envelope, so the decoy here only
    /// needs to exist, not to match.
    /// </summary>
    private static async Task<DpEnc.IEcdhKey> ResolveRecipientKeyOrDecoyAsync(
        KeyOperationResolver recipientKeys,
        IReadOnlyList<string> recipientKids,
        JoseCryptoProvider cryptoProvider,
        CancellationToken ct)
    {
        var heldKids = await recipientKeys.FindPresentAsync(recipientKids, ct).ConfigureAwait(false);
        foreach (var kid in heldKids)
        {
            var handle = await recipientKeys.ResolveKeyAgreementAsync(kid, ct).ConfigureAwait(false);
            if (handle is not null)
                return handle;
        }

        var scalar = new byte[32];
        cryptoProvider.Fill(scalar);
        try
        {
            return new DpEnc.RawEcdhKey(JoseAlgorithms.CrvX25519, scalar, cryptoProvider);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }
    }

    /// <summary>A single envelope layer, outer→inner, with the auth/anon distinction the composition gate needs.</summary>
    private enum LayerShape
    {
        Sign,
        AnonEncrypt,
        AuthEncrypt,
        Plaintext,
    }

    /// <summary>
    /// Assert the unwrapped layer sequence (outer→inner) is a legal DIDComm v2.1 composition. The
    /// receive grammar is <c>anoncrypt? authcrypt? sign? plaintext</c>: at most one anoncrypt
    /// (outermost), then at most one authcrypt, then at most one signature (innermost), then the
    /// plaintext. This admits every shape in spec Appendix C — including the FR-ENV-02 emit set, the
    /// receive-only <c>authcrypt(sign)</c> (FR-ENV-03), and the protect-sender-plus-sign
    /// <c>anoncrypt(authcrypt(sign))</c> (Appendix C.3) — and rejects sign-outside-encrypt
    /// (FR-ENV-05 ordering), authcrypt-outside-anoncrypt, double anoncrypt/authcrypt, and more than
    /// one signature. (Issue #17.)
    /// </summary>
    private static void AssertLegalComposition(IReadOnlyList<LayerShape> shape)
    {
        var i = 0;
        if (i < shape.Count && shape[i] == LayerShape.AnonEncrypt) i++; // at most one anoncrypt, outermost
        if (i < shape.Count && shape[i] == LayerShape.AuthEncrypt) i++; // then at most one authcrypt
        if (i < shape.Count && shape[i] == LayerShape.Sign) i++;        // then at most one signature, innermost

        // Whatever remains must be exactly the terminal plaintext; anything else is an illegal nesting.
        var legal = i == shape.Count - 1 && shape[i] == LayerShape.Plaintext;
        if (!legal)
        {
            throw new MalformedMessageException(
                $"Illegal DIDComm envelope composition [{string.Join(", ", shape)}]; the legal receive grammar " +
                "is anoncrypt? authcrypt? sign? plaintext (DIDComm v2.1, Appendix C).");
        }
    }
}
