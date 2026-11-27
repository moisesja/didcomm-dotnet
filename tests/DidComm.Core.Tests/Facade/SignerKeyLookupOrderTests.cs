using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Composition;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Secrets;
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
/// Every case runs through two receivers. Through the envelope reader, the signer lookup itself
/// records each kid it is asked for, so the recorded list is exactly the lookups made: which kids,
/// in which order, and how many times each. Through <see cref="DidCommClient.UnpackAsync"/>, a key
/// service in front of the production <see cref="NetDidKeyService"/> records each kid a signer key
/// is resolved for: what a consumer's key service sees. (Within one unpack the client resolves a
/// kid once and answers a repeated request for it from that, so a repeat would show through the
/// reader only.)
/// </para>
/// <para>
/// And every case runs with each <see cref="Lookups"/>: every lookup answers at once; every lookup
/// has to be waited for; the first signature's answers at once and the others' have to be waited
/// for; only the last signature's has to be waited for. A lookup that answers at once returns a
/// completed task. One that has to be waited for returns a task that is not complete, which the
/// test completes on its own thread once the unpack has come back to it (<see cref="HeldLookups"/>);
/// every case asserts that exactly the lookups its mode names were waited for and that every other
/// answer was complete when it was handed out. The kids looked up, their order, the result and the
/// failure are the same in every mode.
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

    /// <summary>Which signer-key lookups have to be waited for, by the position of their signature in the envelope.</summary>
    public enum Lookups
    {
        /// <summary>Every lookup answers at once.</summary>
        AllAnswerAtOnce,

        /// <summary>Every lookup has to be waited for.</summary>
        AllAreWaitedFor,

        /// <summary>The first signature's lookup answers at once; every other has to be waited for.</summary>
        AllButTheFirstAreWaitedFor,

        /// <summary>Only the last signature's lookup has to be waited for.</summary>
        OnlyTheLastIsWaitedFor,
    }

    /// <summary>How a lookup that fails hands its exception over.</summary>
    public enum Throws
    {
        /// <summary>The lookup throws when it is called.</summary>
        WhenCalled,

        /// <summary>The lookup returns a task that ends with the exception.</summary>
        ThroughItsTask,
    }

    /// <summary>Every receiver with every lookup mode.</summary>
    public static TheoryData<Through, Lookups> Ways()
    {
        var ways = new TheoryData<Through, Lookups>();
        foreach (var through in Enum.GetValues<Through>())
        {
            foreach (var lookups in Enum.GetValues<Lookups>())
                ways.Add(through, lookups);
        }
        return ways;
    }

    /// <summary>Every receiver with every lookup mode and every way a failing lookup hands its exception over.</summary>
    public static TheoryData<Through, Lookups, Throws> WaysToFail()
    {
        var ways = new TheoryData<Through, Lookups, Throws>();
        foreach (var through in Enum.GetValues<Through>())
        {
            foreach (var lookups in Enum.GetValues<Lookups>())
            {
                foreach (var throws in Enum.GetValues<Throws>())
                    ways.Add(through, lookups, throws);
            }
        }
        return ways;
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
    [MemberData(nameof(Ways))]
    public async Task Two_unknown_keys_then_a_signature_that_verifies_opens_after_one_lookup_for_each_kid_in_order(Through through, Lookups lookups)
    {
        var outcome = await Unpack(through, lookups, (Kid1, Key.Unknown), (Kid2, Key.Unknown), (Kid3, Key.Verifies));

        outcome.Failure.Should().BeNull();
        outcome.SignerKid.Should().Be(Kid3);
        outcome.Asked.Should().Equal(Kid1, Kid2, Kid3);
    }

    [Theory]
    [MemberData(nameof(Ways))]
    public async Task A_first_signature_that_verifies_is_the_result_and_no_later_kid_is_looked_up(Through through, Lookups lookups)
    {
        var outcome = await Unpack(through, lookups, (Kid1, Key.Verifies), (Kid2, Key.Unknown), (Kid3, Key.DoesNotVerify));

        outcome.Failure.Should().BeNull();
        outcome.SignerKid.Should().Be(Kid1);
        outcome.Asked.Should().Equal(Kid1);
    }

    [Theory]
    [MemberData(nameof(Ways))]
    public async Task When_no_key_is_known_the_failure_is_the_no_key_failure_of_the_last_kid(Through through, Lookups lookups)
    {
        var outcome = await Unpack(through, lookups, (Kid1, Key.Unknown), (Kid2, Key.Unknown), (Kid3, Key.Unknown));

        outcome.Failure.Should().BeOfType<CryptoException>().Which.Message.Should().Be(NoKeyFor(Kid3));
        outcome.Asked.Should().Equal(Kid1, Kid2, Kid3);
    }

    [Theory]
    [MemberData(nameof(Ways))]
    public async Task A_signature_that_does_not_verify_then_an_unknown_key_reports_the_no_key_failure_of_the_last_kid(Through through, Lookups lookups)
    {
        var outcome = await Unpack(through, lookups, (Kid1, Key.DoesNotVerify), (Kid2, Key.Unknown));

        outcome.Failure.Should().BeOfType<CryptoException>().Which.Message.Should().Be(NoKeyFor(Kid2));
        outcome.Asked.Should().Equal(Kid1, Kid2);
    }

    [Theory]
    [MemberData(nameof(Ways))]
    public async Task An_unknown_key_then_a_signature_that_does_not_verify_reports_that_signature_as_not_verified(Through through, Lookups lookups)
    {
        var outcome = await Unpack(through, lookups, (Kid1, Key.Unknown), (Kid2, Key.DoesNotVerify));

        outcome.Failure.Should().BeOfType<CryptoException>().Which.Message.Should().Be(DidNotVerifyFor(Kid2));
        outcome.Asked.Should().Equal(Kid1, Kid2);
    }

    [Theory]
    [MemberData(nameof(Ways))]
    public async Task A_single_signature_with_an_unknown_key_reports_its_no_key_failure(Through through, Lookups lookups)
    {
        var outcome = await Unpack(through, lookups, (Kid1, Key.Unknown));

        outcome.Failure.Should().BeOfType<CryptoException>().Which.Message.Should().Be(NoKeyFor(Kid1));
        outcome.Asked.Should().Equal(Kid1);
    }

    [Theory]
    [MemberData(nameof(Ways))]
    public async Task A_kid_over_the_limits_after_two_unknown_keys_is_refused_as_malformed_and_is_never_looked_up(Through through, Lookups lookups)
    {
        // The long kid's own key is known and its signature verifies, so only the limit refuses it.
        var longKid = LengthCap.KidOfLength(Alice, LengthCap.KeyIdCap + 1);

        var outcome = await Unpack(through, lookups, (Kid1, Key.Unknown), (Kid2, Key.Unknown), (longKid, Key.Verifies));

        var refusal = outcome.Failure.Should().BeOfType<MalformedMessageException>().Which;
        refusal.Message.Should().Contain("JWS signer 'kid'").And.Contain("4096");
        refusal.ShouldNotQuoteTheValue();
        outcome.Asked.Should().Equal(Kid1, Kid2);
    }

    [Theory]
    [MemberData(nameof(WaysToFail))]
    public async Task A_lookup_that_fails_with_an_exception_of_this_library_ends_the_unpack_with_that_exception_and_no_later_kid_is_looked_up(
        Through through, Lookups lookups, Throws throws)
    {
        var failure = new DidResolutionException(Alice, "the resolver did not answer");

        var outcome = await Unpack(through, lookups, new FailingLookup(Kid2, throws, failure),
            (Kid1, Key.Unknown), (Kid2, Key.Verifies), (Kid3, Key.Verifies));

        outcome.Failure.Should().BeSameAs(failure);
        outcome.Asked.Should().Equal(Kid1, Kid2);
    }

    [Theory]
    [MemberData(nameof(WaysToFail))]
    public async Task A_lookup_that_fails_with_any_other_exception_ends_the_unpack_as_malformed_with_that_exception_inside_and_no_later_kid_is_looked_up(
        Through through, Lookups lookups, Throws throws)
    {
        var failure = new TimeoutException("the resolver did not answer");

        var outcome = await Unpack(through, lookups, new FailingLookup(Kid2, throws, failure),
            (Kid1, Key.Unknown), (Kid2, Key.Verifies), (Kid3, Key.Verifies));

        var refusal = outcome.Failure.Should().BeOfType<MalformedMessageException>().Which;
        refusal.Message.Should().Be("Malformed JWS.");
        refusal.InnerException.Should().BeSameAs(failure);
        outcome.Asked.Should().Equal(Kid1, Kid2);
    }

    // ---- Helpers ----

    private static string NoKeyFor(string kid) => $"No verifier public JWK supplied for signer kid '{kid}'.";

    private static string DidNotVerifyFor(string kid) => $"JWS signature did not verify for kid '{kid}'.";

    /// <summary>How an unpack ended, and every kid a signer key was asked for, in call order.</summary>
    private sealed record Outcome(string? SignerKid, Exception? Failure, IReadOnlyList<string> Asked);

    /// <summary>A lookup that fails: for which kid, how it hands its exception over, and the exception.</summary>
    private sealed record FailingLookup(string Kid, Throws How, Exception Failure);

    private static Task<Outcome> Unpack(Through through, Lookups lookups, params (string Kid, Key Key)[] signatures)
        => Unpack(through, lookups, failing: null, signatures);

    /// <summary>
    /// Sign a message from Alice once for each of <paramref name="signatures"/>, in order, each with
    /// a new key under the given kid, and unpack the envelope as a receiver that knows, for each
    /// kid, what its <see cref="Key"/> says, whose lookups answer at once or have to be waited for
    /// as <paramref name="lookups"/> says, and whose lookup for <paramref name="failing"/>'s kid
    /// fails.
    /// </summary>
    private static async Task<Outcome> Unpack(Through through, Lookups lookups, FailingLookup? failing, params (string Kid, Key Key)[] signatures)
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

        var answers = new Answers(signatures.Select(signature => signature.Kid).ToArray(), lookups, failing);
        var outcome = through == Through.Reader
            ? ThroughTheReader(packed, known, answers)
            : ThroughTheClient(packed, known, answers);

        answers.Held.Released.Should().Be(answers.AskedAndHeld,
            "the lookups this mode says have to be waited for were waited for, and no other");
        answers.AnsweredAtOnceButNotComplete.Should().Be(0,
            "a lookup this mode says answers at once hands out a completed task");
        return outcome;
    }

    private static Outcome ThroughTheReader(string packed, IReadOnlyList<Jwk> known, Answers answers)
    {
        var secrets = new DictionarySecretsLookup(Array.Empty<Jwk>());
        var unpack = EnvelopeReader.UnpackAsync(
            packed,
            new KeyOperationResolver(secrets, opaque: null, Crypto),
            senderLookup: null,
            signerLookup: (kid, _) => new ValueTask<Jwk?>(
                answers.For(kid, () => Task.FromResult(known.FirstOrDefault(key => key.Kid == kid)))),
            Crypto);
        return Ended(unpack, result => result.SignerKid, answers);
    }

    private static Outcome ThroughTheClient(string packed, IReadOnlyList<Jwk> known, Answers answers)
    {
        var resolver = new SpyDidResolver(LengthCap.DocWithKeys(Alice, authentication: known, keyAgreement: Array.Empty<Jwk>()));
        var receiver = new DidCommClient(
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            new RecordingKeyService(new NetDidKeyService(resolver), answers),
            new DidCommOptions());

        return Ended(receiver.UnpackAsync(packed), result => result.SignerKid, answers);
    }

    /// <summary>
    /// Release the lookups <paramref name="unpack"/> is waiting for, one at a time on this thread,
    /// until it is complete, and report how it ended.
    /// </summary>
    private static Outcome Ended<TResult>(Task<TResult> unpack, Func<TResult, string?> signerKidOf, Answers answers)
    {
        answers.Held.ReleaseUntilComplete(unpack);

        string? signerKid = null;
        var failure = Record.Exception(() => signerKid = signerKidOf(unpack.GetAwaiter().GetResult()));
        return new Outcome(signerKid, failure, answers.Asked);
    }

    /// <summary>
    /// What a receiver's signer-key lookups do: each records its kid, and then fails, answers at
    /// once or hands out an answer that has to be waited for.
    /// </summary>
    private sealed class Answers(string[] kidsInEnvelopeOrder, Lookups lookups, FailingLookup? failing)
    {
        /// <summary>Every kid a signer key was asked for, in call order.</summary>
        public List<string> Asked { get; } = [];

        /// <summary>The answers that have to be waited for.</summary>
        public HeldLookups Held { get; } = new();

        /// <summary>How many of the lookups made were handed an answer that has to be waited for.</summary>
        public int AskedAndHeld { get; private set; }

        /// <summary>How many of the answers handed out at once were tasks that were not complete.</summary>
        public int AnsweredAtOnceButNotComplete { get; private set; }

        /// <summary>
        /// Record a lookup of <paramref name="kid"/> and answer it: throw, when its lookup fails
        /// that way; else return the task of <paramref name="answer"/> (or one that ends with the
        /// failure), at once or held until the test releases it.
        /// </summary>
        public Task<T> For<T>(string kid, Func<Task<T>> answer)
        {
            Asked.Add(kid);
            if (failing?.Kid == kid)
            {
                if (failing.How == Throws.WhenCalled)
                    throw failing.Failure;
                answer = () => Task.FromException<T>(failing.Failure);
            }

            if (!IsWaitedFor(kid))
            {
                var atOnce = answer();
                if (!atOnce.IsCompleted)
                    AnsweredAtOnceButNotComplete++;
                return atOnce;
            }

            AskedAndHeld++;
            return Held.Hold(answer);
        }

        private bool IsWaitedFor(string kid)
        {
            var position = Array.IndexOf(kidsInEnvelopeOrder, kid);
            return lookups switch
            {
                Lookups.AllAnswerAtOnce => false,
                Lookups.AllAreWaitedFor => true,
                Lookups.AllButTheFirstAreWaitedFor => position > 0,
                Lookups.OnlyTheLastIsWaitedFor => position == kidsInEnvelopeOrder.Length - 1,
                _ => throw new ArgumentOutOfRangeException(nameof(lookups)),
            };
        }
    }

    /// <summary>
    /// The production key service, with each resolution of a signer (authentication) key recorded
    /// and answered as <paramref name="answers"/> says. It offers what <see cref="NetDidKeyService"/>
    /// offers, so the client takes the same path as with the production service alone.
    /// </summary>
    private sealed class RecordingKeyService(NetDidKeyService inner, Answers answers) : IDidKeyService, IDidKeyBindingService
    {
        public Task<ResolvedKeyBinding?> ResolveKeyBindingAsync(string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => relationship == VerificationRelationship.Authentication
                ? answers.For(kid, () => inner.ResolveKeyBindingAsync(kid, relationship, ct))
                : inner.ResolveKeyBindingAsync(kid, relationship, ct);

        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
            => inner.GetVerificationMethodsAsync(did, relationship, ct);

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => inner.IsKeyAuthorizedAsync(did, kid, relationship, ct);

        public void RejectUnsupportedMethod(string did) => inner.RejectUnsupportedMethod(did);
    }
}
