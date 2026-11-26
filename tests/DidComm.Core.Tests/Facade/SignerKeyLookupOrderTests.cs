using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Composition;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using NetCrypto;
using Xunit;
using DpSig = DataProofsDotnet.Jose.Signing;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Tests.Facade;

/// <summary>
/// A signed envelope with one or several signatures whose signer keys are known or not: which kids
/// are looked up, in which order and how often, which signature is the result, and which failure is
/// reported when none verifies.
/// </summary>
/// <remarks>
/// <para>
/// The rule: signatures are tried in order; a kid is looked up only once every signature before it
/// has failed; a signature whose key is not known is skipped; the first signature that verifies is
/// the result; when none verifies, the failure reported is the last signature's own, with the
/// parser's message for it. A kid over the FR-API-09 limits is refused before its lookup.
/// </para>
/// <para>
/// Every case runs twice. Through the envelope reader, the signer lookup itself records each kid it
/// is asked for, so the recorded list is exactly the lookups made: which kids, in which order, and
/// how many times each. Through <see cref="DidCommClient.UnpackAsync"/>, a key service in front of
/// the production <see cref="NetDidKeyService"/> records each kid a signer key is resolved for:
/// what a consumer's key service sees. (Within one unpack the client resolves a kid once and
/// answers a repeated request for it from that, so a repeat would show through the reader only.)
/// </para>
/// </remarks>
public sealed class SignerKeyLookupOrderTests
{
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";
    private const string Kid1 = Alice + "#key-1";
    private const string Kid2 = Alice + "#key-2";
    private const string Kid3 = Alice + "#key-3";

    private static readonly JoseCryptoProvider Crypto = new();

    /// <summary>Where the envelope is unpacked.</summary>
    public enum Through
    {
        /// <summary>The envelope reader, with a signer lookup that records what it is asked.</summary>
        Reader,

        /// <summary><see cref="DidCommClient.UnpackAsync"/>, with a key service that records what it is asked.</summary>
        Client,
    }

    /// <summary>What the receiver knows about a signature's kid.</summary>
    private enum Key
    {
        /// <summary>No key is known for the kid.</summary>
        Unknown,

        /// <summary>The key the signature was made with is known for the kid: the signature verifies.</summary>
        Verifies,

        /// <summary>Another key is known for the kid: the signature does not verify.</summary>
        DoesNotVerify,
    }

    [Theory]
    [InlineData(Through.Reader)]
    [InlineData(Through.Client)]
    public async Task Two_unknown_keys_then_a_signature_that_verifies_opens_after_one_lookup_for_each_kid_in_order(Through through)
    {
        var outcome = await Unpack(through, (Kid1, Key.Unknown), (Kid2, Key.Unknown), (Kid3, Key.Verifies));

        outcome.Failure.Should().BeNull();
        outcome.SignerKid.Should().Be(Kid3);
        outcome.Asked.Should().Equal(Kid1, Kid2, Kid3);
    }

    [Theory]
    [InlineData(Through.Reader)]
    [InlineData(Through.Client)]
    public async Task A_first_signature_that_verifies_is_the_result_and_no_later_kid_is_looked_up(Through through)
    {
        var outcome = await Unpack(through, (Kid1, Key.Verifies), (Kid2, Key.Unknown), (Kid3, Key.DoesNotVerify));

        outcome.Failure.Should().BeNull();
        outcome.SignerKid.Should().Be(Kid1);
        outcome.Asked.Should().Equal(Kid1);
    }

    [Theory]
    [InlineData(Through.Reader)]
    [InlineData(Through.Client)]
    public async Task When_no_key_is_known_the_failure_is_the_no_key_failure_of_the_last_kid(Through through)
    {
        var outcome = await Unpack(through, (Kid1, Key.Unknown), (Kid2, Key.Unknown), (Kid3, Key.Unknown));

        outcome.Failure.Should().BeOfType<CryptoException>().Which.Message.Should().Be(NoKeyFor(Kid3));
        outcome.Asked.Should().Equal(Kid1, Kid2, Kid3);
    }

    [Theory]
    [InlineData(Through.Reader)]
    [InlineData(Through.Client)]
    public async Task A_signature_that_does_not_verify_then_an_unknown_key_reports_the_no_key_failure_of_the_last_kid(Through through)
    {
        var outcome = await Unpack(through, (Kid1, Key.DoesNotVerify), (Kid2, Key.Unknown));

        outcome.Failure.Should().BeOfType<CryptoException>().Which.Message.Should().Be(NoKeyFor(Kid2));
        outcome.Asked.Should().Equal(Kid1, Kid2);
    }

    [Theory]
    [InlineData(Through.Reader)]
    [InlineData(Through.Client)]
    public async Task An_unknown_key_then_a_signature_that_does_not_verify_reports_that_signature_as_not_verified(Through through)
    {
        var outcome = await Unpack(through, (Kid1, Key.Unknown), (Kid2, Key.DoesNotVerify));

        outcome.Failure.Should().BeOfType<CryptoException>().Which.Message.Should().Be(DidNotVerifyFor(Kid2));
        outcome.Asked.Should().Equal(Kid1, Kid2);
    }

    [Theory]
    [InlineData(Through.Reader)]
    [InlineData(Through.Client)]
    public async Task A_single_signature_with_an_unknown_key_reports_its_no_key_failure(Through through)
    {
        var outcome = await Unpack(through, (Kid1, Key.Unknown));

        outcome.Failure.Should().BeOfType<CryptoException>().Which.Message.Should().Be(NoKeyFor(Kid1));
        outcome.Asked.Should().Equal(Kid1);
    }

    [Theory]
    [InlineData(Through.Reader)]
    [InlineData(Through.Client)]
    public async Task A_kid_over_the_limits_after_two_unknown_keys_is_refused_as_malformed_and_is_never_looked_up(Through through)
    {
        // The long kid's own key is known and its signature verifies, so only the limit refuses it.
        var longKid = LengthCap.KidOfLength(Alice, LengthCap.KeyIdCap + 1);

        var outcome = await Unpack(through, (Kid1, Key.Unknown), (Kid2, Key.Unknown), (longKid, Key.Verifies));

        var refusal = outcome.Failure.Should().BeOfType<MalformedMessageException>().Which;
        refusal.Message.Should().Contain("JWS signer 'kid'").And.Contain("4096");
        refusal.ShouldNotQuoteTheValue();
        outcome.Asked.Should().Equal(Kid1, Kid2);
    }

    // ---- Helpers ----

    private static string NoKeyFor(string kid) => $"No verifier public JWK supplied for signer kid '{kid}'.";

    private static string DidNotVerifyFor(string kid) => $"JWS signature did not verify for kid '{kid}'.";

    /// <summary>How an unpack ended, and every kid a signer key was asked for, in call order.</summary>
    private sealed record Outcome(string? SignerKid, Exception? Failure, IReadOnlyList<string> Asked);

    /// <summary>
    /// Sign a message from Alice once for each of <paramref name="signatures"/>, in order, each with
    /// a new key under the given kid, and unpack the envelope as a receiver that knows, for each
    /// kid, what its <see cref="Key"/> says.
    /// </summary>
    private static async Task<Outcome> Unpack(Through through, params (string Kid, Key Key)[] signatures)
    {
        var signers = new List<DpSig.JwsSigner>();
        var known = new List<Jwk>();
        foreach (var (kid, key) in signatures)
        {
            var signing = TestKeyMaterial.Generate(KeyType.Ed25519, kid);
            signers.Add(signing.PrivateJwk.ToJwsSigner());
            if (key == Key.Verifies)
                known.Add(signing.PublicJwk);
            else if (key == Key.DoesNotVerify)
                known.Add(TestKeyMaterial.Generate(KeyType.Ed25519, kid).PublicJwk);
        }

        var message = new MessageBuilder()
            .WithType("https://example.com/protocols/test/1.0/ping")
            .WithFrom(Alice)
            .WithTo(Bob)
            .WithBody(new JsonObject { ["v"] = 1 })
            .Build();
        var packed = await EnvelopeWriter.PackSignedAsync(new PackSignedParameters(message, signers));
        JsonNode.Parse(packed)!["signatures"]!.AsArray().Should().HaveCount(signatures.Length);

        return through == Through.Reader
            ? ThroughTheReader(packed, known)
            : await ThroughTheClient(packed, known);
    }

    private static Outcome ThroughTheReader(string packed, IReadOnlyList<Jwk> known)
    {
        var asked = new List<string>();
        Jwk? Lookup(string kid)
        {
            asked.Add(kid);
            return known.FirstOrDefault(key => key.Kid == kid);
        }

        string? signerKid = null;
        var failure = Record.Exception(() => signerKid = EnvelopeReaderTestRunner.Unpack(
            packed, new DictionarySecretsLookup(Array.Empty<Jwk>()), senderLookup: null, signerLookup: Lookup, Crypto).SignerKid);
        return new Outcome(signerKid, failure, asked);
    }

    private static async Task<Outcome> ThroughTheClient(string packed, IReadOnlyList<Jwk> known)
    {
        var asked = new List<string>();
        var resolver = new SpyDidResolver(LengthCap.DocWithKeys(Alice, authentication: known, keyAgreement: Array.Empty<Jwk>()));
        var receiver = new DidCommClient(
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            new RecordingKeyService(new NetDidKeyService(resolver), asked),
            new DidCommOptions());

        string? signerKid = null;
        var failure = await Record.ExceptionAsync(async () => signerKid = (await receiver.UnpackAsync(packed)).SignerKid);
        return new Outcome(signerKid, failure, asked);
    }

    /// <summary>
    /// The production key service, recording each kid a signer (authentication) key is resolved
    /// for, in call order. It offers what <see cref="NetDidKeyService"/> offers, so the client takes
    /// the same path as with the production service alone.
    /// </summary>
    private sealed class RecordingKeyService(NetDidKeyService inner, List<string> asked) : IDidKeyService, IDidKeyBindingService
    {
        public Task<ResolvedKeyBinding?> ResolveKeyBindingAsync(string kid, VerificationRelationship relationship, CancellationToken ct = default)
        {
            if (relationship == VerificationRelationship.Authentication)
                asked.Add(kid);
            return inner.ResolveKeyBindingAsync(kid, relationship, ct);
        }

        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
            => inner.GetVerificationMethodsAsync(did, relationship, ct);

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => inner.IsKeyAuthorizedAsync(did, kid, relationship, ct);

        public void RejectUnsupportedMethod(string did) => inner.RejectUnsupportedMethod(did);
    }
}
