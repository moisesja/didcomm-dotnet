using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.Rotation;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Threading;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;

namespace DidComm.Samples.EnvelopesAndMessages;

/// <summary>
/// The envelope tour (PRD §14.3 sample 03, tasks C–N): every envelope composition the spec
/// defines, each content-encryption algorithm on the compositions that allow it,
/// multi-recipient packing, the three attachment shapes, threading + ACKs, and DID rotation
/// via <c>from_prior</c> — printing each packed wire form (truncated) and the unpacked
/// metadata so you can see exactly what each shape proves.
/// <see cref="Main"/> is the CLI; <see cref="RunAsync"/> is the testable seam invoked by the
/// InteropTests smoke test (FR-DX-02, no process spawn).
/// </summary>
public static class Program
{
    /// <summary>CLI entry point — writes to <see cref="Console.Out"/> and exits 0 on success.</summary>
    public static async Task<int> Main()
    {
        try
        {
            await RunAsync(Console.Out).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"EnvelopesAndMessages failed: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// Run the whole tour, writing the narration to <paramref name="output"/>. Sections keep
    /// the PRD §14.2 task letters (C–N) so the output can be read next to the spec map.
    /// </summary>
    /// <param name="output">Destination for narrator output. <c>null</c> uses <see cref="Console.Out"/>.</param>
    public static async Task RunAsync(TextWriter? output = null)
    {
        var narrator = output is null ? new Narrator() : new Narrator(output);

        // One offline setup for the whole tour: an in-memory secrets resolver stands in for
        // your KMS, and did:peer:2 identities resolve locally (the DID document is encoded in
        // the DID string itself) so nothing here touches a network (FR-DX-02).
        var secrets = new InMemorySecretsResolver();
        var services = new ServiceCollection();
        services.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(secrets));
        await using var sp = services.BuildServiceProvider();

        var manager = sp.GetRequiredService<IDidManager>();
        var keyGen = sp.GetRequiredService<IKeyGenerator>();
        var crypto = sp.GetRequiredService<ICryptoProvider>();

        var alice = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        var bob = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        var carol = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        var alice2 = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto); // the DID Alice rotates to in section N
        foreach (var key in alice.Privates.Concat(bob.Privates).Concat(carol.Privates).Concat(alice2.Privates))
            secrets.Add(key);

        var client = sp.GetRequiredService<DidCommClient>();

        narrator.Step($"Minted alice  = {Trunc(alice.Did, 64)}");
        narrator.Step($"Minted bob    = {Trunc(bob.Did, 64)}");
        narrator.Step($"Minted carol  = {Trunc(carol.Did, 64)}");
        narrator.Step($"Minted alice2 = {Trunc(alice2.Did, 64)} (rotation target)");

        await PlaintextAsync(narrator, client, alice, bob);
        await SignedAsync(narrator, client, alice, bob);
        await AnoncryptAsync(narrator, client, bob);
        await AuthcryptAsync(narrator, client, alice, bob);
        await SignThenEncryptAsync(narrator, client, alice, bob);
        await ProtectSenderAsync(narrator, client, alice, bob);
        await ContentEncryptionAsync(narrator, client, alice, bob);
        await MultiRecipientAsync(narrator, client, alice, bob, carol);
        await UnpackMetadataAsync(narrator, client, alice, bob);
        await AttachmentsAsync(narrator, client, alice, bob);
        await ThreadingAndAcksAsync(narrator, client, alice, bob);
        await RotationAsync(narrator, client, alice, alice2, bob);
    }

    // ── C ────────────────────────────────────────────────────────────────────────────────

    private static async Task PlaintextAsync(Narrator n, DidCommClient client, PeerIdentity alice, PeerIdentity bob)
    {
        n.Section("C", "Plaintext (debug/inspection only)");

        // Plaintext is the bare JWM — the inner payload every other shape protects. Nothing
        // about it is confidential, authenticated, or tamper-evident.
        var message = Basic(alice.Did, bob.Did, "Readable by anyone on the path.");
        var packed = await client.PackPlaintextAsync(message);
        n.Value("Packed (truncated)", Trunc(packed, 120));

        var unpacked = await client.UnpackAsync(packed);
        n.Value("Encrypted", unpacked.Encrypted);
        n.Value("Authenticated", unpacked.Authenticated);
        n.Value("NonRepudiation", unpacked.NonRepudiation);
        n.Note("All three flags are false: 'from' is just an unverified claim here. Use plaintext for debugging, never on the wire.");
    }

    // ── D ────────────────────────────────────────────────────────────────────────────────

    private static async Task SignedAsync(Narrator n, DidCommClient client, PeerIdentity alice, PeerIdentity bob)
    {
        n.Section("D", "Signed (non-repudiable, no confidentiality)");

        // A signed envelope is a JWS over the plaintext: everyone can read it, and everyone
        // can prove Alice produced exactly this content. The 'to' header is included on
        // purpose — without it a signed message could be forwarded to an audience the signer
        // never addressed (the client logs a warning when it is missing, FR-SIG-05).
        var message = Basic(alice.Did, bob.Did, "Alice provably said this.");
        var packed = await client.PackSignedAsync(message, signFrom: alice.Did);
        n.Value("Packed (truncated)", Trunc(packed, 120));

        var unpacked = await client.UnpackAsync(packed);
        n.Value("NonRepudiation", unpacked.NonRepudiation);
        n.Value("Encrypted", unpacked.Encrypted);
        n.Value("SignatureAlgorithm", unpacked.SignatureAlgorithm);
        n.Value("SignerKid", Trunc(unpacked.SignerKid, 64));
        n.Note("Signed-only means public. For secret AND provable, sign inside encryption (section G).");
    }

    // ── E ────────────────────────────────────────────────────────────────────────────────

    private static async Task AnoncryptAsync(Narrator n, DidCommClient client, PeerIdentity bob)
    {
        n.Section("E", "Anoncrypt (confidential, anonymous sender)");

        // Omitting From — on the message AND the pack options — is the whole selection:
        // the library derives the anonymous ECDH-ES key agreement instead of the
        // authenticated one (FR-MSG-08).
        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithTo(bob.Did)
            .WithBody(new JsonObject { ["content"] = "For Bob's eyes, from nobody in particular." })
            .Build();
        var packed = await client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { bob.Did }));
        n.Value("Packed (truncated)", Trunc(packed.Message, 120));

        var unpacked = await client.UnpackAsync(packed.Message);
        n.Value("Encrypted", unpacked.Encrypted);
        n.Value("AnonymousSender", unpacked.AnonymousSender);
        n.Value("Authenticated", unpacked.Authenticated);
        n.Value("KeyWrap", unpacked.KeyWrap); // ECDH-ES = the anonymous derivation
        n.Note("Use anoncrypt for first contact or when the sender must stay anonymous — any origin claimed inside the body is unverified.");
    }

    // ── F ────────────────────────────────────────────────────────────────────────────────

    private static async Task AuthcryptAsync(Narrator n, DidCommClient client, PeerIdentity alice, PeerIdentity bob)
    {
        n.Section("F", "Authcrypt (confidential + sender authenticated — the default)");

        // The default posture: only Bob reads it, and a successful decrypt itself proves
        // Alice sent it (ECDH-1PU mixes her static key into the derivation).
        var message = Basic(alice.Did, bob.Did, "Only Bob reads this, and Bob knows it's Alice.");
        var packed = await client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { bob.Did },
            From: alice.Did));
        n.Value("Packed (truncated)", Trunc(packed.Message, 120));

        var unpacked = await client.UnpackAsync(packed.Message);
        n.Value("Encrypted", unpacked.Encrypted);
        n.Value("Authenticated", unpacked.Authenticated);
        n.Value("KeyWrap", unpacked.KeyWrap); // ECDH-1PU = the authenticated derivation
        n.Value("SenderKid", Trunc(unpacked.SenderKid, 64));
        n.Note("Authcrypt authenticates Alice to Bob only — deniable by design. Transferable proof is section G.");
    }

    // ── G ────────────────────────────────────────────────────────────────────────────────

    private static async Task SignThenEncryptAsync(Narrator n, DidCommClient client, PeerIdentity alice, PeerIdentity bob)
    {
        n.Section("G", "Sign-then-encrypt (add non-repudiation)");

        // SignFrom on an encrypted pack signs the plaintext FIRST, then encrypts the signed
        // form — the only composition order the spec allows (FR-SIG-06). One call gets it right.
        var message = Basic(alice.Did, bob.Did, "Secret, authenticated, and provable.");
        var packed = await client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { bob.Did },
            From: alice.Did,
            SignFrom: alice.Did));
        n.Value("Packed (truncated)", Trunc(packed.Message, 120));

        var unpacked = await client.UnpackAsync(packed.Message);
        n.Value("Encrypted", unpacked.Encrypted);
        n.Value("Authenticated", unpacked.Authenticated);
        n.Value("NonRepudiation", unpacked.NonRepudiation);
        n.Value("Stack", string.Join(" ⊃ ", unpacked.Stack)); // Encrypted ⊃ Signed ⊃ Plaintext
        n.Note("Reach for SignFrom when the recipient may need third-party-verifiable proof. Signatures are forever — skip it when deniability matters.");
    }

    // ── H ────────────────────────────────────────────────────────────────────────────────

    private static async Task ProtectSenderAsync(Narrator n, DidCommClient client, PeerIdentity alice, PeerIdentity bob)
    {
        n.Section("H", "Protect the sender (anoncrypt wraps authcrypt)");

        // Plain authcrypt names the sender's key id (skid) in the OUTER, unencrypted JOSE
        // header — every mediator on the path learns which Alice key is talking. ProtectSender
        // wraps the authcrypt envelope in an outer anoncrypt layer, moving skid inside the
        // ciphertext where only Bob can see it.
        var message = Basic(alice.Did, bob.Did, "Mediators shouldn't learn who is talking.");

        var plain = await client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { bob.Did }, From: alice.Did));
        var plainHeader = DecodeOuterProtectedHeader(plain.Message);
        n.Step("Plain authcrypt — the outer header any mediator can read:");
        n.Value("Outer alg", plainHeader["alg"]?.GetValue<string>());
        n.Value("Outer skid", Trunc(plainHeader["skid"]?.GetValue<string>(), 64));

        var hidden = await client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { bob.Did }, From: alice.Did, ProtectSender: true));
        var hiddenHeader = DecodeOuterProtectedHeader(hidden.Message);
        n.Step("ProtectSender = true — the outer header is now anonymous:");
        n.Value("Outer alg", hiddenHeader["alg"]?.GetValue<string>()); // ECDH-ES
        n.Value("Outer skid", hiddenHeader["skid"]?.GetValue<string>()); // <null> — moved inside

        var unpacked = await client.UnpackAsync(hidden.Message);
        n.Value("Authenticated (after peeling)", unpacked.Authenticated);
        n.Value("Stack", string.Join(" ⊃ ", unpacked.Stack)); // two Encrypted layers
        n.Note("The parties being blinded are the ones in the middle — Bob still authenticates Alice after peeling the outer layer.");
    }

    // ── I ────────────────────────────────────────────────────────────────────────────────

    private static async Task ContentEncryptionAsync(Narrator n, DidCommClient client, PeerIdentity alice, PeerIdentity bob)
    {
        n.Section("I", "Content encryption — each algorithm on the composition that allows it");

        // Anoncrypt accepts all three ContentEncryptionAlgorithm values; pack with each and
        // read the negotiated cipher back off the unpack metadata.
        var anon = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithTo(bob.Did)
            .WithBody(new JsonObject { ["content"] = "Same payload, three ciphers." })
            .Build();
        foreach (var enc in new[]
                 {
                     ContentEncryptionAlgorithm.A256CbcHs512,
                     ContentEncryptionAlgorithm.A256Gcm,
                     ContentEncryptionAlgorithm.XC20P,
                 })
        {
            var packed = await client.PackEncryptedAsync(anon, new PackEncryptedOptions(
                Recipients: new[] { bob.Did }, Enc: enc));
            var unpacked = await client.UnpackAsync(packed.Message);
            n.Value($"anoncrypt {enc}", unpacked.ContentEncryption);
        }

        // Authcrypt allows exactly one cipher — A256CBC-HS512, which is also the default, so
        // leaving Enc alone is always correct.
        var auth = Basic(alice.Did, bob.Did, "Default cipher.");
        var authPacked = await client.PackEncryptedAsync(auth, new PackEncryptedOptions(
            Recipients: new[] { bob.Did }, From: alice.Did));
        n.Value("authcrypt (default)", (await client.UnpackAsync(authPacked.Message)).ContentEncryption);

        // The guard rail: authcrypt's ECDH-1PU key agreement is only specified over the
        // CBC-with-HMAC family, so GCM/XC20P with authcrypt is refused before any crypto runs
        // (FR-ENC-09).
        n.Step("Authcrypt + A256GCM is a forbidden combination — the pack call refuses it.");
        try
        {
            await client.PackEncryptedAsync(auth, new PackEncryptedOptions(
                Recipients: new[] { bob.Did }, From: alice.Did, Enc: ContentEncryptionAlgorithm.A256Gcm));
            n.Note("UNEXPECTED: the forbidden combination was not refused.");
        }
        catch (InvalidOperationException ex)
        {
            n.Note($"Refused as designed: {ex.Message}");
        }
    }

    // ── J ────────────────────────────────────────────────────────────────────────────────

    private static async Task MultiRecipientAsync(Narrator n, DidCommClient client, PeerIdentity alice, PeerIdentity bob, PeerIdentity carol)
    {
        n.Section("J", "Multi-recipient (one envelope, several readers)");

        // The body is encrypted once; the content-encryption key is wrapped once per
        // recipient. Bob and Carol each decrypt with their own key.
        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(alice.Did)
            .WithTo(bob.Did, carol.Did)
            .WithBody(new JsonObject { ["content"] = "One envelope, two readers." })
            .Build();
        var packed = await client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { bob.Did, carol.Did },
            From: alice.Did));
        n.Value("Packed (truncated)", Trunc(packed.Message, 120));

        var wireRecipients = JsonNode.Parse(packed.Message)!["recipients"]!.AsArray();
        n.Value("Recipients on the wire", wireRecipients.Count);

        var unpacked = await client.UnpackAsync(packed.Message);
        n.Value("RecipientKid (decrypted with)", Trunc(unpacked.RecipientKid, 64));
        n.Value("AllRecipientKids.Count", unpacked.AllRecipientKids.Count);
        n.Note("Recipient DIDs are visible in the envelope — multi-recipient saves bytes, it does not hide the audience from each other.");
    }

    // ── K ────────────────────────────────────────────────────────────────────────────────

    private static async Task UnpackMetadataAsync(Narrator n, DidCommClient client, PeerIdentity alice, PeerIdentity bob)
    {
        n.Section("K", "Unpack metadata — every field, one envelope");

        // Pack the richest shape (sign-then-encrypt) and read back the full UnpackResult so
        // the metadata surface is visible in one place (FR-API-04).
        var message = Basic(alice.Did, bob.Did, "Inspect me.");
        var packed = await client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { bob.Did }, From: alice.Did, SignFrom: alice.Did));
        var r = await client.UnpackAsync(packed.Message);

        n.Value("Message.Id", r.Message.Id);
        n.Value("Message.From", Trunc(r.Message.From, 64));
        n.Value("Stack", string.Join(" ⊃ ", r.Stack));
        n.Value("Encrypted", r.Encrypted);
        n.Value("Authenticated", r.Authenticated);
        n.Value("NonRepudiation", r.NonRepudiation);
        n.Value("AnonymousSender", r.AnonymousSender);
        n.Value("ContentEncryption", r.ContentEncryption);
        n.Value("KeyWrap", r.KeyWrap);
        n.Value("SignatureAlgorithm", r.SignatureAlgorithm);
        n.Value("SignerKid", Trunc(r.SignerKid, 64));
        n.Value("SenderKid", Trunc(r.SenderKid, 64));
        n.Value("RecipientKid", Trunc(r.RecipientKid, 64));
        n.Value("RecipientAddressing", r.RecipientAddressing);
        n.Note("Message.From is trustworthy only when Authenticated or NonRepudiation is true — check the flags before acting on the sender identity.");
    }

    // ── L ────────────────────────────────────────────────────────────────────────────────

    private static async Task AttachmentsAsync(Narrator n, DidCommClient client, PeerIdentity alice, PeerIdentity bob)
    {
        n.Section("L", "Attachments (inline json / base64 / linked-with-hash)");

        // Shape 1 — inline JSON: small structured data travels as-is inside the message.
        var report = new Attachment
        {
            Id = "report",
            MediaType = "application/json",
            Data = new AttachmentData { Json = JsonNode.Parse("""{"total":42}""") },
        };

        // Shape 2 — inline base64: small binary payloads, base64url-encoded.
        var logoBytes = Encoding.UTF8.GetBytes("pretend-this-is-a-png");
        var logo = new Attachment
        {
            Id = "logo",
            MediaType = "image/png",
            ByteCount = logoBytes.Length,
            Data = new AttachmentData { Base64 = Base64UrlEncode(logoBytes) },
        };

        // Shape 3 — linked with hash: big content stays at a URL; the message pins its digest
        // so the recipient can verify whatever it later fetches.
        var videoBytes = Encoding.UTF8.GetBytes("pretend-this-is-a-large-mp4");
        var video = new Attachment
        {
            Id = "video",
            MediaType = "video/mp4",
            Data = new AttachmentData
            {
                Links = new List<string> { "https://cdn.example/x.mp4" },
                Hash = MultibaseSha256Multihash(videoBytes),
            },
        };

        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(alice.Did)
            .WithTo(bob.Did)
            .WithBody(new JsonObject { ["content"] = "Three attachments enclosed." })
            .WithAttachment(report)
            .WithAttachment(logo)
            .WithAttachment(video)
            .Build();

        var packed = await client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { bob.Did }, From: alice.Did));
        var unpacked = await client.UnpackAsync(packed.Message);

        var received = unpacked.Message.Attachments!;
        n.Value("Attachments.Count", received.Count);
        n.Value("report (inline json)", received[0].Data.Json?.ToJsonString());
        n.Value("logo (base64, decoded)", Encoding.UTF8.GetString(Base64UrlDecode(received[1].Data.Base64!)));
        n.Value("video (link)", received[2].Data.Links![0]);
        n.Value("video (hash)", Trunc(received[2].Data.Hash, 64));

        // A link without a digest invites content substitution, so the builder refuses it
        // (FR-ATT-03).
        n.Step("A linked attachment without a hash is refused at Build() time.");
        try
        {
            new MessageBuilder()
                .WithType("https://didcomm.org/basicmessage/2.0/message")
                .WithAttachment(new Attachment
                {
                    Id = "unpinned",
                    Data = new AttachmentData { Links = new List<string> { "https://cdn.example/y.bin" } },
                })
                .Build();
            n.Note("UNEXPECTED: the hash-less link was not refused.");
        }
        catch (MalformedMessageException ex)
        {
            n.Note($"Refused as designed: {ex.Message}");
        }
    }

    // ── M ────────────────────────────────────────────────────────────────────────────────

    private static async Task ThreadingAndAcksAsync(Narrator n, DidCommClient client, PeerIdentity alice, PeerIdentity bob)
    {
        n.Section("M", "Threading & ACKs (thid / pthid / please_ack / ack)");

        // Alice opens a thread and asks Bob to acknowledge receipt.
        var opening = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(alice.Did)
            .WithTo(bob.Did)
            .WithBody(new JsonObject { ["content"] = "Bob, did you get this?" })
            .WithPleaseAck() // "ack the current message"
            .Build();
        var packedOpening = (await client.PackEncryptedAsync(opening, new PackEncryptedOptions(
            Recipients: new[] { bob.Did }, From: alice.Did))).Message;
        var bobReceives = await client.UnpackAsync(packedOpening);
        // please_ack's "ack the current message" convention is [""] on the wire, so the
        // readable check is the predicate, not the raw list.
        n.Value("Bob sees a please_ack request", AckLoopGuard.RequestsAck(bobReceives.Message));

        // Bob's substantive reply continues the thread: thid = the opener's id, and the ack
        // header confirms receipt in the same message.
        var reply = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(bob.Did)
            .WithTo(alice.Did)
            .WithThid(bobReceives.Message.Id)
            .WithAck(bobReceives.Message.Id)
            .WithBody(new JsonObject { ["content"] = "Got it." })
            .Build();
        var packedReply = (await client.PackEncryptedAsync(reply, new PackEncryptedOptions(
            Recipients: new[] { alice.Did }, From: bob.Did))).Message;
        var aliceReceives = await client.UnpackAsync(packedReply);
        n.Value("Reply thid == opening id", aliceReceives.Message.Thid == opening.Id);
        n.Value("Alice sees ack[]", string.Join(",", aliceReceives.Message.Ack ?? new List<string>()));

        // pthid links a NEW thread to the one it grew out of — the child cites the parent
        // thread's id, the way a response to an out-of-band invitation cites the invitation.
        var sideThread = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(alice.Did)
            .WithTo(bob.Did)
            .WithPthid(opening.Id)
            .WithBody(new JsonObject { ["content"] = "Spinning off a related conversation." })
            .Build();
        n.Value("Side-thread pthid == parent id", sideThread.Pthid == opening.Id);

        // When only an ACK is needed (no content), Empty 1.0 is the canonical wire shape:
        // Message.Empty() pre-seeds the builder with the Empty 1.0 type (FR-PROTO-06).
        var pureAck = Message.Empty()
            .WithFrom(bob.Did)
            .WithTo(alice.Did)
            .WithThid(opening.Id)
            .WithAck(opening.Id)
            .Build();
        n.Value("Empty ACK type", pureAck.Type);
        n.Value("AckLoopGuard.IsPureAck", AckLoopGuard.IsPureAck(pureAck));
        n.Value("AckLoopGuard.IsSafeToSend", AckLoopGuard.IsSafeToSend(pureAck));

        // The loop trap: a pure ACK that ALSO asks for an ACK would ping-pong forever; the
        // guard flags it before transmission (FR-THR-04).
        var loopTrap = Message.Empty()
            .WithFrom(bob.Did)
            .WithTo(alice.Did)
            .WithAck(opening.Id)
            .WithPleaseAck()
            .Build();
        n.Value("IsSafeToSend (ack that asks for an ack)", AckLoopGuard.IsSafeToSend(loopTrap));
    }

    // ── N ────────────────────────────────────────────────────────────────────────────────

    private static async Task RotationAsync(Narrator n, DidCommClient client, PeerIdentity alice, PeerIdentity alice2, PeerIdentity bob)
    {
        n.Section("N", "DID rotation via from_prior");

        // Bob trusts alice; he has never heard of alice2. The from_prior JWT is Alice's
        // continuity proof: signed with a key her OLD DID advertised under 'authentication',
        // its claims say "the party you knew as `iss` is now `sub`".
        var priorAuthKey = alice.Privates.First(k => string.Equals(k.Crv, "Ed25519", StringComparison.Ordinal));
        var claims = new FromPriorClaims(
            Sub: alice2.Did,
            Iss: alice.Did,
            Iat: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // The lifetime overload stamps exp = iat + lifetime so a captured token cannot be
        // replayed past the window (FR-ROT-05).
        var jwt = await FromPriorBuilder.BuildAsync(claims, priorAuthKey, lifetime: TimeSpan.FromMinutes(5));
        n.Value("from_prior JWT (truncated)", Trunc(jwt, 60));

        var rotation = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(alice2.Did) // the message travels under the NEW DID
            .WithTo(bob.Did)
            .WithFromPrior(jwt)
            .WithBody(new JsonObject { ["content"] = "Same Alice, new DID." })
            .Build();

        // Rotation messages must travel encrypted; the pack is authcrypt from the new DID.
        var packed = (await client.PackEncryptedAsync(rotation, new PackEncryptedOptions(
            Recipients: new[] { bob.Did }, From: alice2.Did))).Message;
        n.Value("Packed (truncated)", Trunc(packed, 120));

        // Bob's unpack verifies the JWT against the OLD DID's document and surfaces the
        // validated claims — his cue to re-bind the conversation to the new DID.
        var result = await client.UnpackAsync(packed);
        n.Value("FromPrior.Iss (old DID)", Trunc(result.FromPrior?.Iss, 64));
        n.Value("FromPrior.Sub (new DID)", Trunc(result.FromPrior?.Sub, 64));
        n.Value("Sub == message.From", result.FromPrior?.Sub == result.Message.From);
        n.Value("IsTermination", result.FromPrior?.IsTermination);

        // The termination form (FR-ROT-06): a from_prior whose claims OMIT sub announces
        // "this relationship ends, with no successor DID". It rides on a message WITHOUT
        // 'from' — packed anoncrypt, since there is no successor identity to authenticate.
        n.Step("Relationship termination: from_prior without sub, on a from-less anoncrypt message.");
        var terminationClaims = FromPriorClaims.ForTermination(
            Iss: alice.Did,
            Iat: DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var terminationJwt = await FromPriorBuilder.BuildAsync(terminationClaims, priorAuthKey, lifetime: TimeSpan.FromMinutes(5));

        var goodbye = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithTo(bob.Did)
            .WithFromPrior(terminationJwt)
            .WithBody(new JsonObject { ["content"] = "Goodbye." })
            .Build();
        var packedGoodbye = (await client.PackEncryptedAsync(goodbye, new PackEncryptedOptions(
            Recipients: new[] { bob.Did }))).Message;

        var termination = await client.UnpackAsync(packedGoodbye);
        n.Value("Termination FromPrior.IsTermination", termination.FromPrior?.IsTermination);
        n.Value("Termination FromPrior.Sub", termination.FromPrior?.Sub);
        n.Value("Termination message.From", termination.Message.From);
        n.Note("Check IsTermination before treating from_prior claims as a rotation — a termination has no successor to re-bind to.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────

    private static Message Basic(string from, string to, string content) => new MessageBuilder()
        .WithType("https://didcomm.org/basicmessage/2.0/message")
        .WithFrom(from)
        .WithTo(to)
        .WithBody(new JsonObject { ["content"] = content })
        .Build();

    private static string Trunc(string? value, int max)
        => value is null ? "<null>" : value.Length <= max ? value : value[..(max - 1)] + "…";

    /// <summary>Parse a packed JWE (general JSON serialization) and decode its base64url 'protected' header.</summary>
    private static JsonObject DecodeOuterProtectedHeader(string packedJwe)
    {
        var envelope = JsonNode.Parse(packedJwe)!.AsObject();
        var protectedB64 = envelope["protected"]!.GetValue<string>();
        return JsonNode.Parse(Base64UrlDecode(protectedB64))!.AsObject();
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string base64Url)
    {
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(padded);
    }

    /// <summary>Digest bytes with SHA-256 and wrap the result as a multibase(base64url) multihash (0x12 0x20 prefix).</summary>
    private static string MultibaseSha256Multihash(byte[] content)
    {
        var digest = SHA256.HashData(content);
        var multihash = new byte[2 + digest.Length];
        multihash[0] = 0x12; // multihash code: sha2-256
        multihash[1] = (byte)digest.Length;
        digest.CopyTo(multihash, 2);
        return "u" + Base64UrlEncode(multihash);
    }
}
