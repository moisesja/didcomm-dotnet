using System.Text.Json.Nodes;
using DidComm.Composition;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Jose.Signing;
using DidComm.Messages;
using DidComm.Secrets;
using AwesomeAssertions;
using NetCrypto;
using Xunit;
using DpEnc = DataProofsDotnet.Jose.Encryption;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;
using MalformedJoseException = DataProofsDotnet.Jose.MalformedJoseException;

namespace DidComm.Tests.Envelopes.Composition;

/// <summary>
/// Pins what the stricter JOSE parser means for callers of didcomm's unpack. The dependency refresh
/// in issue #77 moved DataProofsDotnet.Jose from 1.3.0 to 1.5.0. Its 1.4.0 release changed how the
/// parser treats two inputs a remote peer can send without holding any key:
/// <list type="bullet">
///   <item>A JSON escape for a lone UTF-16 surrogate (<c>\uD800</c>) in a string value anywhere in a
///         JWS or JWE. The parser used to accept one in a member it never read. It now rejects the
///         whole envelope as malformed. (The same escape in a member NAME never reaches this parser:
///         didcomm's envelope detector rejects it first. See <see cref="SurrogateEscapeBoundaryTests"/>.)</item>
///   <item>An X25519 public key of small order, used as the ephemeral <c>epk</c> or as an authcrypt
///         sender key. Key agreement against it produces the all-zero shared secret, which the crypto
///         layer refuses. That refusal used to leave the parser as a raw
///         <see cref="CryptographicException"/>. The parser now reports it as its uniform decrypt
///         failure.</item>
/// </list>
/// Each test starts from a real envelope and makes one hostile change to it.
/// </summary>
public sealed class JoseInputHardeningTests
{
    private static readonly JoseCryptoProvider _crypto = new();

    // The JSON escape for a lone high surrogate: six ASCII characters on the wire, so the bytes are
    // valid UTF-8 and valid JSON grammar. The string they denote is not Unicode text, which I-JSON
    // (RFC 7493 §2.1) forbids. The verbatim literal keeps the backslash, so the escape itself reaches
    // the JSON text rather than the character it stands for.
    private const string UnpairedSurrogateEscape = @"\uD800";

    // A well-formed control for the escape above: a surrogate PAIR (U+1F600), escaped the same way.
    private const string SurrogatePairEscape = @"\uD83D\uDE00";

    // The all-zero u-coordinate, a point of small order on Curve25519. X25519 against it gives the
    // all-zero shared secret whatever the private scalar, which RFC 7748 §6.1 lets a party reject. It
    // is 32 bytes, the right length, so no length or encoding check stops it before key agreement.
    private static readonly string SmallOrderX25519 = DidComm.Jose.Base64Url.Encode(new byte[32]);

    // ---- Unpaired-surrogate escapes in a member no parser reads ----

    [Fact]
    public async Task Unpaired_surrogate_escape_in_an_unread_JWE_member_is_rejected_as_malformed()
    {
        // The hostile member sits at the top level of the JWE's General JSON serialization. That is
        // outside the protected header and outside the AAD, so it does not break decryption: with
        // DataProofsDotnet.Jose 1.3.0 this envelope decrypted and unpacked. From 1.4.0 the parser
        // checks the whole JSON text before reading it and throws MalformedJoseException, which
        // EnvelopeReader's JWE branch maps to MalformedMessageException.
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(EmptyMessage(), new[] { bob.PublicJwk }, "A256GCM"), _crypto);
        var secrets = new DictionarySecretsLookup(new[] { bob.PrivateJwk });

        // Control: the same member carrying a well-formed escape still unpacks. So the rejection below
        // comes from the unpaired surrogate, not from an unknown member being present.
        var control = EnvelopeReaderTestRunner.Unpack(WithUnreadTopLevelMember(packed, SurrogatePairEscape),
            secrets, senderLookup: null, signerLookup: null, _crypto);
        control.Encrypted.Should().BeTrue();

        Action act = () => EnvelopeReaderTestRunner.Unpack(WithUnreadTopLevelMember(packed, UnpairedSurrogateEscape),
            secrets, senderLookup: null, signerLookup: null, _crypto);

        act.Should().Throw<MalformedMessageException>()
            .WithMessage("*surrogate*")
            .WithInnerException<MalformedJoseException>();
    }

    [Fact]
    public async Task Unpaired_surrogate_escape_in_an_unread_JWS_member_is_rejected_as_malformed()
    {
        // The signed counterpart. A top-level member of the JWS's General JSON serialization is not
        // part of the signing input (the protected header and the payload), so the signature still
        // verifies: with DataProofsDotnet.Jose 1.3.0 this envelope unpacked. From 1.4.0 the parser
        // rejects it as MalformedJoseException, which EnvelopeReader's JWS branch maps to
        // MalformedMessageException.
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var packed = await EnvelopeWriter.PackSignedAsync(
            new PackSignedParameters(EmptyMessage(), new[] { signer.PrivateJwk.ToJwsSigner() }));
        Func<string, Jwk?> signerLookup = kid => kid == signer.PublicJwk.Kid ? signer.PublicJwk : null;
        var noSecrets = new DictionarySecretsLookup(Array.Empty<Jwk>());

        // Control, as above: a well-formed escape in the same member still verifies and unpacks.
        var control = EnvelopeReaderTestRunner.Unpack(WithUnreadTopLevelMember(packed, SurrogatePairEscape),
            noSecrets, senderLookup: null, signerLookup, _crypto);
        control.SignerKid.Should().Be(signer.PublicJwk.Kid);

        Action act = () => EnvelopeReaderTestRunner.Unpack(WithUnreadTopLevelMember(packed, UnpairedSurrogateEscape),
            noSecrets, senderLookup: null, signerLookup, _crypto);

        act.Should().Throw<MalformedMessageException>()
            .WithMessage("*surrogate*")
            .WithInnerException<MalformedJoseException>();
    }

    // ---- Small-order X25519 keys in key agreement ----

    [Fact]
    public async Task Small_order_epk_fails_identically_whether_or_not_the_recipient_key_is_held()
    {
        // Anyone can put a small-order epk in an envelope; no key is needed. It must fail as the same
        // uniform decrypt failure for an agent that holds the recipient key as for one that does not
        // (the decoy path). A difference in exception type, message or inner-exception type would tell
        // the sender which agents hold the key, the oracle the decoy path exists to close.
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(EmptyMessage(), new[] { bob.PublicJwk }, "A256GCM"), _crypto);
        var hostile = WithEpkX(packed, SmallOrderX25519);

        var holderFailure = UnpackFailure(hostile, new DictionarySecretsLookup(new[] { bob.PrivateJwk }), senderLookup: null);
        var strangerFailure = UnpackFailure(hostile, new DictionarySecretsLookup(Array.Empty<Jwk>()), senderLookup: null);

        holderFailure.GetType().Should().Be(strangerFailure.GetType());
        holderFailure.Message.Should().Be(strangerFailure.Message);
        (holderFailure.InnerException?.GetType()).Should().Be(strangerFailure.InnerException?.GetType());
    }

    [Fact]
    public async Task Small_order_epk_reaches_key_agreement_before_the_AEAD_check()
    {
        // Guards the test above against passing for the wrong reason. Changing epk changes the
        // protected header, and the protected header is the AAD, so the AEAD tag no longer matches.
        // A CryptoException alone could therefore come from the tag check. A held key that records
        // each agreement it performs shows the derive ran against the small-order point and that the
        // key agreement itself refused it, before any unwrap or tag check.
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(EmptyMessage(), new[] { bob.PublicJwk }, "A256GCM"), _crypto);
        var hostile = WithEpkX(packed, SmallOrderX25519);
        var recording = new RecordingKeyResolver(bob);

        Action act = () => EnvelopeReaderTestRunner.Unpack(hostile, recording, senderLookup: null, signerLookup: null, _crypto);

        act.Should().Throw<CryptoException>().WithMessage("JWE could not be decrypted.");
        recording.Key.PeerPublicKeys.Should().ContainSingle().Which.Should().Equal(new byte[32]);
        recording.Key.Faults.Should().ContainSingle().Which.Should().BeOfType<CryptographicException>();
    }

    [Fact]
    public async Task Small_order_authcrypt_sender_key_fails_identically_whether_or_not_the_recipient_key_is_held()
    {
        // ECDH-1PU (authcrypt) runs two key agreements: one with the epk and one with the sender's
        // static key, which the receiver looks up by skid. In DIDComm that key comes from the sender's
        // own DID document, so the sender chooses it. Here the lookup returns the small-order point.
        // The envelope itself is untouched, so nothing can fail before key agreement. The failure must
        // be the same for the key holder as for the decoy path.
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                EmptyMessage(), new[] { bob.PublicJwk }, "A256CBC-HS512",
                SenderKey: alice.PrivateJwk.ToEcdhKey(_crypto),
                Skid: alice.PublicJwk.Kid),
            _crypto);
        var smallOrderSender = new DictionarySenderKeyLookup(new[]
        {
            new Jwk { Kty = "OKP", Crv = "X25519", X = SmallOrderX25519, Kid = alice.PublicJwk.Kid },
        });

        var holderFailure = UnpackFailure(packed, new DictionarySecretsLookup(new[] { bob.PrivateJwk }), smallOrderSender);
        var strangerFailure = UnpackFailure(packed, new DictionarySecretsLookup(Array.Empty<Jwk>()), smallOrderSender);

        // The point is a well-formed 32-byte key, so the parser's separate "sender public key is
        // malformed" error does not apply. Only the key agreement rejects it, as the uniform failure.
        holderFailure.Message.Should().Be("JWE could not be decrypted.");
        holderFailure.GetType().Should().Be(strangerFailure.GetType());
        holderFailure.Message.Should().Be(strangerFailure.Message);
        (holderFailure.InnerException?.GetType()).Should().Be(strangerFailure.InnerException?.GetType());
    }

    // ---- Helpers ----

    private static Message EmptyMessage() => new MessageBuilder()
        .WithType("https://didcomm.org/empty/1.0/empty")
        .WithFrom("did:example:alice")
        .WithTo("did:example:bob")
        .Build();

    private static CryptoException UnpackFailure(string packed, ISecretsResolver secrets, IInternalSenderKeyLookup? senderLookup)
    {
        Action act = () => EnvelopeReaderTestRunner.Unpack(packed, secrets, senderLookup, signerLookup: null, _crypto);
        return act.Should().Throw<CryptoException>().Which;
    }

    /// <summary>
    /// Add a top-level member that no JOSE parser reads, holding <paramref name="rawJsonEscape"/> as
    /// raw JSON text. This edits the string rather than going through JsonNode, because
    /// System.Text.Json writes a lone surrogate as U+FFFD, so the hostile escape would never reach
    /// the wire.
    /// </summary>
    private static string WithUnreadTopLevelMember(string packed, string rawJsonEscape)
    {
        var close = packed.LastIndexOf('}');
        return packed[..close] + $",\"x-unread\":\"{rawJsonEscape}\"" + packed[close..];
    }

    /// <summary>Replace the X25519 <c>epk.x</c> in a JWE's protected header, leaving every other byte alone.</summary>
    private static string WithEpkX(string packed, string x)
    {
        var jwe = JsonNode.Parse(packed)!.AsObject();
        var header = JsonNode.Parse(DidComm.Jose.Base64Url.DecodeUtf8(jwe["protected"]!.GetValue<string>()))!.AsObject();
        header["epk"]!["crv"]!.GetValue<string>().Should().Be("X25519");
        header["epk"]!["x"] = x;
        jwe["protected"] = DidComm.Jose.Base64Url.EncodeUtf8(header.ToJsonString());
        return jwe.ToJsonString();
    }

    // Reports one recipient key as held and hands out a key-agreement handle that records each
    // agreement it is asked to perform.
    private sealed class RecordingKeyResolver : ISecretsResolver, IOpaqueKeyResolver
    {
        private readonly Jwk _publicJwk;

        public RecordingKeyResolver(TestKeyMaterial recipient)
        {
            _publicJwk = recipient.PublicJwk;
            Key = new RecordingEcdhKey(recipient.PrivateJwk);
        }

        public RecordingEcdhKey Key { get; }

        public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default)
            => Task.FromResult<Jwk?>(kid == _publicJwk.Kid ? _publicJwk : null);
        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(kids.Where(k => k == _publicJwk.Kid).ToArray());
        public Task<ISigner?> ResolveSignerAsync(string kid, CancellationToken ct = default)
            => Task.FromResult<ISigner?>(null);
        public Task<DpEnc.IEcdhKey?> ResolveKeyAgreementAsync(string kid, CancellationToken ct = default)
            => Task.FromResult<DpEnc.IEcdhKey?>(kid == _publicJwk.Kid ? Key : null);
    }

    // Does the real X25519 agreement with the recipient's private scalar, recording each peer public
    // key it is given and any exception the agreement raises.
    private sealed class RecordingEcdhKey : DpEnc.IEcdhKey
    {
        private readonly DpEnc.RawEcdhKey _inner;

        public RecordingEcdhKey(Jwk privateJwk)
        {
            Crv = privateJwk.Crv!;
            _inner = new DpEnc.RawEcdhKey(Crv, DidComm.Jose.Base64Url.Decode(privateJwk.D!), _crypto);
        }

        public string Crv { get; }
        public List<byte[]> PeerPublicKeys { get; } = new();
        public List<Exception> Faults { get; } = new();

        public async ValueTask<byte[]> DeriveAsync(ReadOnlyMemory<byte> peerPublicKey, CancellationToken ct = default)
        {
            PeerPublicKeys.Add(peerPublicKey.ToArray());
            try
            {
                return await _inner.DeriveAsync(peerPublicKey, ct);
            }
            catch (Exception ex)
            {
                Faults.Add(ex);
                throw;
            }
        }
    }
}
