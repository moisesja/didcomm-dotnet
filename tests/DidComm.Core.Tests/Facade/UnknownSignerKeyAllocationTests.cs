using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Composition;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Tests.Facade;

/// <summary>
/// What one <see cref="DidCommClient.UnpackAsync"/> call allocates for a signed envelope whose
/// signer keys are all unknown grows, for each signature, by far less than a copy of the payload:
/// a signature whose key is not known is skipped, and the payload is not read again for it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The input:</strong> a General JSON JWS over a message of about 256 kB, once with 64 and
/// once with 1,024 signature entries, each under a kid of its own. The two inputs share the payload
/// and differ only in the number of entries; both fit the default receive limit. The receiver is a
/// <see cref="DidCommClient"/> over the production <see cref="NetDidKeyService"/>. Its resolver
/// serves the signer's DID document without authentication keys, so every kid is looked up and
/// none has a key.
/// </para>
/// <para>
/// <strong>What is asserted:</strong> each unpack ends with the <see cref="CryptoException"/> for
/// the last kid, after one lookup for each signature; and the bytes allocated for the
/// 1,024-signature input exceed those for the 64-signature input by less than one eighth of the
/// payload's size for each added signature. What an unpack allocates independently of the number
/// of signatures is in both figures, so their difference is the growth alone. The growth measured
/// is a few kilobytes for each signature and the bound is about 32 kB. The measured bytes are
/// written to the test output.
/// </para>
/// <para>
/// <strong>The counter:</strong> <see cref="GC.GetAllocatedBytesForCurrentThread"/>, read on the
/// test's thread before and after the call. It counts that thread only, so what tests running in
/// parallel allocate on their own threads is not in it. It covers the whole unpack because the
/// unpack runs on the calling thread from start to end: the resolver answers with completed tasks,
/// so no <c>await</c> on the path suspends. Two assertions show that on every run: the task
/// <c>UnpackAsync</c> returns is already complete when the call returns, and the resolver saw every
/// lookup on the test's thread. Each input is unpacked once before it is measured, so what only a
/// first use allocates (type initialisation, pooled buffers) is not in the figures. What this
/// thread itself allocates still varies a little while other tests run beside it: over 50 runs of
/// the whole suite the growth measured was between about 3 kB and 5 kB for each added signature.
/// The bound leaves room for that. No time is measured.
/// </para>
/// <para>
/// <strong>Lookups that have to be waited for:</strong> the second test makes the same two unpacks
/// with a resolver whose every answer is a task that is not complete when it is handed out
/// (<see cref="HeldLookups"/>). The test completes those tasks one at a time, on its own thread,
/// while the unpack's task is not complete, and the unpack continues inside each completion, so the
/// same counter still covers the whole unpack. It asserts in addition that every lookup was waited
/// for. What each held lookup takes is in the figure; the growth measured is about 4.4 kB for each
/// added signature, against the same bound.
/// </para>
/// </remarks>
public sealed class UnknownSignerKeyAllocationTests(ITestOutputHelper output)
{
    /// <summary>The size the signed message is padded to, in bytes.</summary>
    internal const int PayloadBytes = 256 * 1024;

    private const int FewSignatures = 64;
    private const int ManySignatures = 1024;

    [Fact]
    public Task Unpack_of_a_JWS_whose_signer_keys_are_all_unknown_allocates_far_less_than_a_payload_copy_for_each_signature()
        => MeasureTheGrowthForEachAddedSignature(lookupsWait: false);

    [Fact]
    public Task Unpack_of_a_JWS_whose_signer_keys_are_all_unknown_allocates_far_less_than_a_payload_copy_for_each_signature_when_every_lookup_has_to_be_waited_for()
        => MeasureTheGrowthForEachAddedSignature(lookupsWait: true);

    private async Task MeasureTheGrowthForEachAddedSignature(bool lookupsWait)
    {
        var signed = await RepeatedSignatureJws.SignAsync(PayloadBytes);
        var few = signed.WithSignatures(FewSignatures).ShouldFitTheReceiveLimit();
        var many = signed.WithSignatures(ManySignatures).ShouldFitTheReceiveLimit();
        var document = RepeatedSignatureJws.SignerDocumentWithoutAuthenticationKeys();

        // Nothing is awaited from here on, so every unpack below runs on this one thread.
        UnpackAllocation.Measure(few, document, lookupsWait: lookupsWait);
        UnpackAllocation.Measure(many, document, lookupsWait: lookupsWait);
        var fewRun = UnpackAllocation.Measure(few, document, lookupsWait: lookupsWait);
        var manyRun = UnpackAllocation.Measure(many, document, lookupsWait: lookupsWait);

        var forEachAddedSignature = (manyRun.ThreadBytes - fewRun.ThreadBytes) / (double)(ManySignatures - FewSignatures);
        output.WriteLine(FormattableString.Invariant($"payload: {signed.PayloadBytes:N0} bytes"));
        output.WriteLine(FormattableString.Invariant($"{FewSignatures} signatures: wire {Encoding.UTF8.GetByteCount(few):N0} bytes, allocated {fewRun.ThreadBytes:N0} bytes"));
        output.WriteLine(FormattableString.Invariant($"{ManySignatures} signatures: wire {Encoding.UTF8.GetByteCount(many):N0} bytes, allocated {manyRun.ThreadBytes:N0} bytes"));
        output.WriteLine(FormattableString.Invariant($"allocated for each added signature: {forEachAddedSignature:N0} bytes"));

        fewRun.ShouldHaveLookedUpEveryKidAndFailedWith(RepeatedSignatureJws.NoKeyFor(FewSignatures), FewSignatures);
        manyRun.ShouldHaveLookedUpEveryKidAndFailedWith(RepeatedSignatureJws.NoKeyFor(ManySignatures), ManySignatures);
        fewRun.LookupsWaitedFor.Should().Be(lookupsWait ? FewSignatures : 0);
        manyRun.LookupsWaitedFor.Should().Be(lookupsWait ? ManySignatures : 0);
        forEachAddedSignature.Should().BeLessThan(signed.PayloadBytes / 8.0,
            "a signature whose key is not known must not take a copy of the payload");
    }
}

/// <summary>
/// What one <see cref="DidCommClient.UnpackAsync"/> call allocates for a signed envelope whose
/// signer keys are all known and none of whose signatures verifies grows, for each signature, by
/// what the check of one signature takes inside one parse of the envelope, not by a parse of the
/// payload for each signature.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The input:</strong> the General JSON JWS of <see cref="UnknownSignerKeyAllocationTests"/>
/// (a payload of about 256 kB), once with 16 and once with 64 signature entries. The resolver
/// serves, with completed tasks, the signer's DID document publishing for each of the 64 kids an
/// authentication key other than the one the payload was signed with: every lookup answers at once
/// with a key, and no signature verifies under it.
/// </para>
/// <para>
/// <strong>What is asserted:</strong> each unpack ends with the <see cref="CryptoException"/> of
/// the last signature ("did not verify"), after one lookup for each signature; and the bytes
/// allocated for the 64-signature input exceed those for the 16-signature input by less than
/// <see cref="BoundInPayloads"/> times the payload's size for each added signature.
/// </para>
/// <para>
/// <strong>The figures behind the bound:</strong> to check one signature the JWS parser builds the
/// signing input (the protected header, a dot and the payload's base64url text) as a string and as
/// bytes, which takes about 4 times the payload's bytes. That is the growth when the envelope is
/// parsed once and every signature is checked inside that parse: about 1,056,000 bytes for each
/// added signature, 4.03 times the payload, measured here; 4.02 times on the commit this branch
/// started from (<c>667d1d3</c>), where the parser was always run once. When each signature whose
/// key is known was handed to the parser in a JWS of its own, also when its lookup had answered at
/// once (<c>bb5ff33</c>), each one took in addition a copy of the <c>payload</c> member, the
/// payload's text and its decoded bytes: about 3,071,000 bytes, 11.71 times. The bound, 7 times,
/// is about 1.7 times the first figure and the second is about 1.7 times the bound. The
/// counter is the one <see cref="UnknownSignerKeyAllocationTests"/> describes, with lookups that
/// answer at once. No time is measured.
/// </para>
/// </remarks>
public sealed class KnownSignerKeyAllocationTests(ITestOutputHelper output)
{
    private const int FewSignatures = 16;
    private const int ManySignatures = 64;

    /// <summary>The bound on the growth for each added signature, in payload sizes.</summary>
    private const double BoundInPayloads = 7;

    [Fact]
    public async Task Unpack_of_a_JWS_whose_signer_keys_are_all_known_and_verify_nothing_allocates_for_each_signature_what_one_check_takes_not_a_parse_of_the_payload()
    {
        var signed = await RepeatedSignatureJws.SignAsync(UnknownSignerKeyAllocationTests.PayloadBytes);
        var few = signed.WithSignatures(FewSignatures).ShouldFitTheReceiveLimit();
        var many = signed.WithSignatures(ManySignatures).ShouldFitTheReceiveLimit();
        var document = RepeatedSignatureJws.SignerDocumentWithOtherKeys(ManySignatures);

        // Nothing is awaited from here on, so every unpack below runs on this one thread.
        UnpackAllocation.Measure(few, document);
        UnpackAllocation.Measure(many, document);
        var fewRun = UnpackAllocation.Measure(few, document);
        var manyRun = UnpackAllocation.Measure(many, document);

        var forEachAddedSignature = (manyRun.ThreadBytes - fewRun.ThreadBytes) / (double)(ManySignatures - FewSignatures);
        output.WriteLine(FormattableString.Invariant($"payload: {signed.PayloadBytes:N0} bytes"));
        output.WriteLine(FormattableString.Invariant($"{FewSignatures} signatures: wire {Encoding.UTF8.GetByteCount(few):N0} bytes, allocated {fewRun.ThreadBytes:N0} bytes"));
        output.WriteLine(FormattableString.Invariant($"{ManySignatures} signatures: wire {Encoding.UTF8.GetByteCount(many):N0} bytes, allocated {manyRun.ThreadBytes:N0} bytes"));
        output.WriteLine(FormattableString.Invariant(
            $"allocated for each added signature: {forEachAddedSignature:N0} bytes, {forEachAddedSignature / signed.PayloadBytes:N2} times the payload"));

        fewRun.ShouldHaveLookedUpEveryKidAndFailedWith(RepeatedSignatureJws.DidNotVerifyFor(FewSignatures), FewSignatures);
        manyRun.ShouldHaveLookedUpEveryKidAndFailedWith(RepeatedSignatureJws.DidNotVerifyFor(ManySignatures), ManySignatures);
        forEachAddedSignature.Should().BeLessThan(BoundInPayloads * signed.PayloadBytes,
            "a signature whose key is known is checked inside the one parse of the envelope while lookups answer at once");
    }
}

/// <summary>
/// What one <see cref="DidCommClient.UnpackAsync"/> call allocates for a signed envelope whose
/// signatures have known keys and are refused by the parser from their protected header grows, for
/// each signature, by far less than a copy of the payload, also after a lookup had to be waited
/// for: such a signature is refused without the payload being read again for it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The input:</strong> the General JSON JWS of <see cref="UnknownSignerKeyAllocationTests"/>
/// (a payload of about 256 kB) with entries whose protected header says <c>alg</c> "none". The
/// parser refuses such a signature once it has the key, before it checks the signature. In the
/// first test every entry names the one kid whose key the signer's DID document publishes, with 64
/// and with 1,024 entries; the resolver's answer has to be waited for, and the client resolves the
/// kid once, so one lookup is waited for and the others are answered from it. In the second test
/// every entry has a kid of its own, the document publishes a key for each of 256 kids, and every
/// lookup has to be waited for, with 64 and with 256 entries.
/// </para>
/// <para>
/// <strong>What is asserted:</strong> each unpack ends with the <see cref="MalformedMessageException"/>
/// carrying the parser's message for the last signature; the lookups made and waited for are the
/// ones described; and the bytes allocated grow by less than one eighth of the payload's size for
/// each added signature, the bound of <see cref="UnknownSignerKeyAllocationTests"/>, about 32 kB.
/// The growth measured is about 6 kB for each added entry in the first test and about 10 kB in the
/// second, where each entry's lookup reads a document of 256 keys. When each such signature was
/// handed to the parser in a JWS of its own that carried the payload (<c>9046d00</c>), it was about
/// 2,020,000 bytes in both.
/// </para>
/// <para>
/// The counter and the held lookups are the ones <see cref="UnknownSignerKeyAllocationTests"/>
/// describes. No time is measured.
/// </para>
/// </remarks>
public sealed class HeaderRefusedSignatureAllocationTests(ITestOutputHelper output)
{
    private const int FewOfOneKid = 64;
    private const int ManyOfOneKid = 1024;
    private const int FewOfOwnKids = 64;
    private const int ManyOfOwnKids = 256;

    [Fact]
    public async Task Unpack_of_a_JWS_whose_entries_repeat_one_known_kid_and_say_alg_none_allocates_far_less_than_a_payload_copy_for_each_entry_after_the_lookup_was_waited_for()
    {
        var signed = await RepeatedSignatureJws.SignAsync(UnknownSignerKeyAllocationTests.PayloadBytes);
        var few = signed.WithAlgNoneSignatures(FewOfOneKid, oneKid: true).ShouldFitTheReceiveLimit();
        var many = signed.WithAlgNoneSignatures(ManyOfOneKid, oneKid: true).ShouldFitTheReceiveLimit();
        var document = signed.SignerDocumentWithTheSigningKey();

        var forEachAddedSignature = MeasureTheGrowth(signed, few, FewOfOneKid, many, ManyOfOneKid, document, lookups: _ => 1);

        forEachAddedSignature.Should().BeLessThan(signed.PayloadBytes / 8.0,
            "a signature the parser refuses from its header must not take a copy of the payload");
    }

    [Fact]
    public async Task Unpack_of_a_JWS_whose_entries_have_known_kids_of_their_own_and_say_alg_none_allocates_far_less_than_a_payload_copy_for_each_entry_when_every_lookup_has_to_be_waited_for()
    {
        var signed = await RepeatedSignatureJws.SignAsync(UnknownSignerKeyAllocationTests.PayloadBytes);
        var few = signed.WithAlgNoneSignatures(FewOfOwnKids, oneKid: false).ShouldFitTheReceiveLimit();
        var many = signed.WithAlgNoneSignatures(ManyOfOwnKids, oneKid: false).ShouldFitTheReceiveLimit();
        var document = RepeatedSignatureJws.SignerDocumentWithOtherKeys(ManyOfOwnKids);

        var forEachAddedSignature = MeasureTheGrowth(signed, few, FewOfOwnKids, many, ManyOfOwnKids, document, lookups: signatures => signatures);

        forEachAddedSignature.Should().BeLessThan(signed.PayloadBytes / 8.0,
            "a signature the parser refuses from its header must not take a copy of the payload");
    }

    /// <summary>
    /// Unpack both inputs with lookups that have to be waited for, each once unmeasured and once
    /// measured, assert how each ended and the lookups made, and return the bytes allocated for
    /// each signature the second input has more than the first.
    /// </summary>
    private double MeasureTheGrowth(
        RepeatedSignatureJws signed, string few, int fewSignatures, string many, int manySignatures, DidDocument document, Func<int, int> lookups)
    {
        // Nothing is awaited from here on, so every unpack below runs on this one thread.
        UnpackAllocation.Measure(few, document, lookupsWait: true);
        UnpackAllocation.Measure(many, document, lookupsWait: true);
        var fewRun = UnpackAllocation.Measure(few, document, lookupsWait: true);
        var manyRun = UnpackAllocation.Measure(many, document, lookupsWait: true);

        var forEachAddedSignature = (manyRun.ThreadBytes - fewRun.ThreadBytes) / (double)(manySignatures - fewSignatures);
        output.WriteLine(FormattableString.Invariant($"payload: {signed.PayloadBytes:N0} bytes"));
        output.WriteLine(FormattableString.Invariant($"{fewSignatures} signatures: wire {Encoding.UTF8.GetByteCount(few):N0} bytes, allocated {fewRun.ThreadBytes:N0} bytes"));
        output.WriteLine(FormattableString.Invariant($"{manySignatures} signatures: wire {Encoding.UTF8.GetByteCount(many):N0} bytes, allocated {manyRun.ThreadBytes:N0} bytes"));
        output.WriteLine(FormattableString.Invariant($"allocated for each added signature: {forEachAddedSignature:N0} bytes"));

        fewRun.ShouldHaveBeenRefusedForAlgNoneAfter(lookups(fewSignatures));
        manyRun.ShouldHaveBeenRefusedForAlgNoneAfter(lookups(manySignatures));
        return forEachAddedSignature;
    }
}

/// <summary>
/// Figures, not a check of the suite: the bytes one <see cref="DidCommClient.UnpackAsync"/> call
/// allocates for the input of <see cref="UnknownSignerKeyAllocationTests"/> with 64, 256 and 1,024
/// signatures whose keys are unknown; with 64 signatures whose keys are known and none of which
/// verifies; for the two 64-signature inputs again with lookups that have to be waited for; and
/// with one signature, whose key is known and verifies it or is not known, with a lookup that
/// answers at once or has to be waited for; and with 64 and 256 entries that repeat one known kid
/// and say <c>alg</c> "none", with a lookup that answers at once or has to be waited for. Skipped
/// unless <c>DIDCOMM_111_SIGNER_KEY_FIGURES=1</c>.
/// </summary>
/// <remarks>
/// Each input is unpacked once unmeasured and then three times measured. Every line gives the wire
/// size, the calling thread's allocated bytes, and the whole process's allocated bytes over the same
/// call. The process figure is this unpack's only when nothing else runs in the process, so run the
/// class alone: <c>dotnet test --filter FullyQualifiedName~SignerKeyAllocationFigures</c>. Where the
/// line says the lookups are waited for, every answer of the resolver is held and released by the
/// probe on its own thread (<see cref="HeldLookups"/>); otherwise the resolver answers with
/// completed tasks.
/// </remarks>
public sealed class SignerKeyAllocationFigures(ITestOutputHelper output)
{
    /// <summary>The environment variable that turns the figures on.</summary>
    public const string Variable = "DIDCOMM_111_SIGNER_KEY_FIGURES";

    private static readonly int[] UnknownKeySignatures = [64, 256, 1024];
    private const int KnownKeySignatures = 64;
    private const int WaitedForSignatures = 64;
    private static readonly int[] AlgNoneSignatures = [64, 256];

    [SignerKeyFiguresFact]
    public async Task Allocated_bytes_of_one_unpack_by_number_of_signatures_and_by_whether_their_keys_are_known()
    {
        var signed = await RepeatedSignatureJws.SignAsync(UnknownSignerKeyAllocationTests.PayloadBytes);
        output.WriteLine(FormattableString.Invariant($"payload: {signed.PayloadBytes:N0} bytes"));

        var unknown = RepeatedSignatureJws.SignerDocumentWithoutAuthenticationKeys();
        foreach (var signatures in UnknownKeySignatures)
        {
            Report($"keys unknown, {signatures} signatures", signed.WithSignatures(signatures), unknown, lookupsWait: false,
                run => run.ShouldHaveLookedUpEveryKidAndFailedWith(RepeatedSignatureJws.NoKeyFor(signatures), signatures));
        }

        var known = RepeatedSignatureJws.SignerDocumentWithOtherKeys(KnownKeySignatures);
        Report($"keys known, no signature verifies, {KnownKeySignatures} signatures", signed.WithSignatures(KnownKeySignatures), known, lookupsWait: false,
            run => run.ShouldHaveLookedUpEveryKidAndFailedWith(RepeatedSignatureJws.DidNotVerifyFor(KnownKeySignatures), KnownKeySignatures));

        Report($"keys known, no signature verifies, {KnownKeySignatures} signatures, every lookup waited for", signed.WithSignatures(KnownKeySignatures), known, lookupsWait: true,
            run =>
            {
                run.ShouldHaveLookedUpEveryKidAndFailedWith(RepeatedSignatureJws.DidNotVerifyFor(KnownKeySignatures), KnownKeySignatures);
                run.LookupsWaitedFor.Should().Be(KnownKeySignatures);
            });

        Report($"keys unknown, {WaitedForSignatures} signatures, every lookup waited for", signed.WithSignatures(WaitedForSignatures), unknown, lookupsWait: true,
            run =>
            {
                run.ShouldHaveLookedUpEveryKidAndFailedWith(RepeatedSignatureJws.NoKeyFor(WaitedForSignatures), WaitedForSignatures);
                run.LookupsWaitedFor.Should().Be(WaitedForSignatures);
            });

        Report("one signature, key known, verifies", signed.WithSignatures(1), signed.SignerDocumentWithTheSigningKey(), lookupsWait: false,
            run =>
            {
                run.Failure.Should().BeNull();
                run.LookupsOnAnotherThread.Should().Be(0, "the lookups must run on the measuring thread");
            });

        Report("one signature, key known, verifies, every lookup waited for", signed.WithSignatures(1), signed.SignerDocumentWithTheSigningKey(), lookupsWait: true,
            run =>
            {
                run.Failure.Should().BeNull();
                run.LookupsOnAnotherThread.Should().Be(0, "the lookups must run on the measuring thread");
                run.LookupsWaitedFor.Should().Be(run.Lookups);
            });

        Report("one signature, key unknown, lookup waited for", signed.WithSignatures(1), unknown, lookupsWait: true,
            run =>
            {
                run.ShouldHaveLookedUpEveryKidAndFailedWith(RepeatedSignatureJws.NoKeyFor(1), 1);
                run.LookupsWaitedFor.Should().Be(1);
            });

        var signingKey = signed.SignerDocumentWithTheSigningKey();
        foreach (var signatures in AlgNoneSignatures)
        {
            var packed = signed.WithAlgNoneSignatures(signatures, oneKid: true);
            Report($"alg none, one known kid repeated, {signatures} entries", packed, signingKey, lookupsWait: false,
                run => run.ShouldHaveBeenRefusedForAlgNoneAfter(lookups: 1, waitedFor: 0));
            Report($"alg none, one known kid repeated, {signatures} entries, the lookup waited for", packed, signingKey, lookupsWait: true,
                run => run.ShouldHaveBeenRefusedForAlgNoneAfter(lookups: 1));
        }
    }

    private void Report(string input, string packed, DidDocument document, bool lookupsWait, Action<UnpackAllocation.Run> check)
    {
        UnpackAllocation.Measure(packed, document, lookupsWait: lookupsWait);
        for (var run = 1; run <= 3; run++)
        {
            var measured = UnpackAllocation.Measure(packed, document, processWide: true, lookupsWait: lookupsWait);
            check(measured);
            output.WriteLine(FormattableString.Invariant(
                $"{input} | wire {Encoding.UTF8.GetByteCount(packed):N0} bytes | run {run} | thread {measured.ThreadBytes:N0} bytes | process {measured.ProcessBytes:N0} bytes"));
        }
    }
}

/// <summary>A <see cref="FactAttribute"/> that runs only when <c>DIDCOMM_111_SIGNER_KEY_FIGURES=1</c>.</summary>
internal sealed class SignerKeyFiguresFactAttribute : FactAttribute
{
    public SignerKeyFiguresFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(SignerKeyAllocationFigures.Variable) != "1")
            Skip = "Figures that mean something only in a process of their own: set " + SignerKeyAllocationFigures.Variable + "=1 and run this class alone.";
    }
}

/// <summary>
/// A General JSON JWS with a chosen number of signature entries over one payload. The payload is
/// signed once; every entry carries that signature under a kid of its own, in the unprotected
/// header. Inputs built from one instance differ only in the number of entries.
/// </summary>
internal sealed class RepeatedSignatureJws
{
    /// <summary>The DID the message is from and every kid belongs to.</summary>
    public const string Signer = "did:example:alice";

    private const string Recipient = "did:example:bob";

    private readonly string _payload;
    private readonly string _protected;
    private readonly string _signature;
    private readonly Jwk _signingKey;

    private RepeatedSignatureJws(string payload, string @protected, string signature, Jwk signingKey)
    {
        _payload = payload;
        _protected = @protected;
        _signature = signature;
        _signingKey = signingKey;
        PayloadBytes = DataProofsDotnet.Jose.Base64Url.Decode(payload).Length;
    }

    /// <summary>The size of the signed payload (the plaintext message), in bytes.</summary>
    public int PayloadBytes { get; }

    /// <summary>The kid of the signature at <paramref name="position"/> (1-based). Every kid has the same length.</summary>
    public static string Kid(int position) => $"{Signer}#key-{position:D4}";

    /// <summary>The failure message for the signature at <paramref name="position"/> when its key is not known.</summary>
    public static string NoKeyFor(int position) => $"No verifier public JWK supplied for signer kid '{Kid(position)}'.";

    /// <summary>The failure message for the signature at <paramref name="position"/> when it does not verify under its known key.</summary>
    public static string DidNotVerifyFor(int position) => $"JWS signature did not verify for kid '{Kid(position)}'.";

    /// <summary>Sign a message from <see cref="Signer"/> whose body pads it to about <paramref name="payloadBytes"/> bytes.</summary>
    public static async Task<RepeatedSignatureJws> SignAsync(int payloadBytes)
    {
        var message = new MessageBuilder()
            .WithType("https://example.com/protocols/test/1.0/ping")
            .WithFrom(Signer)
            .WithTo(Recipient)
            .WithBody(new JsonObject { ["pad"] = new string('p', payloadBytes) })
            .Build();
        var signingKey = TestKeyMaterial.Generate(KeyType.Ed25519, Kid(1));
        var packed = await EnvelopeWriter.PackSignedAsync(
            new PackSignedParameters(message, new[] { signingKey.PrivateJwk.ToJwsSigner() }));

        var jws = JsonNode.Parse(packed)!;
        var entry = jws["signatures"]!.AsArray().Single()!;
        return new RepeatedSignatureJws(
            jws["payload"]!.GetValue<string>(),
            entry["protected"]!.GetValue<string>(),
            entry["signature"]!.GetValue<string>(),
            signingKey.PublicJwk);
    }

    /// <summary>The JWS with <paramref name="signatures"/> entries, under <see cref="Kid"/> 1 to <paramref name="signatures"/> in order.</summary>
    public string WithSignatures(int signatures)
    {
        var entries = new JsonArray();
        for (var position = 1; position <= signatures; position++)
        {
            entries.Add(new JsonObject
            {
                ["protected"] = _protected,
                ["header"] = new JsonObject { ["kid"] = Kid(position) },
                ["signature"] = _signature,
            });
        }
        return new JsonObject { ["payload"] = _payload, ["signatures"] = entries }.ToJsonString();
    }

    /// <summary>The parser's message for a signature whose protected header says <c>alg</c> "none".</summary>
    public const string AlgNoneRefusal = "JWS protected header 'alg' is missing or \"none\"; unsigned JWS is not accepted.";

    private static readonly string AlgNoneProtected = DataProofsDotnet.Jose.Base64Url.Encode(
        Encoding.UTF8.GetBytes("""{"typ":"application/didcomm-signed+json","alg":"none"}"""));

    /// <summary>
    /// The JWS with <paramref name="signatures"/> entries whose protected header says <c>alg</c>
    /// "none": the parser refuses each once it has the key, before it checks the signature. With
    /// <paramref name="oneKid"/> every entry names the first <see cref="Kid"/>; otherwise the
    /// entries are under <see cref="Kid"/> 1 to <paramref name="signatures"/> in order.
    /// </summary>
    public string WithAlgNoneSignatures(int signatures, bool oneKid)
    {
        var entries = new JsonArray();
        for (var position = 1; position <= signatures; position++)
        {
            entries.Add(new JsonObject
            {
                ["protected"] = AlgNoneProtected,
                ["header"] = new JsonObject { ["kid"] = Kid(oneKid ? 1 : position) },
                ["signature"] = _signature,
            });
        }
        return new JsonObject { ["payload"] = _payload, ["signatures"] = entries }.ToJsonString();
    }

    /// <summary>The signer's DID document with no authentication keys: no kid of the JWS has a key.</summary>
    public static DidDocument SignerDocumentWithoutAuthenticationKeys() => new() { Id = new Did(Signer) };

    /// <summary>
    /// The signer's DID document publishing the key the payload was signed with, under the first
    /// kid: the first signature verifies.
    /// </summary>
    public DidDocument SignerDocumentWithTheSigningKey()
        => LengthCap.DocWithKeys(Signer, authentication: new[] { _signingKey }, keyAgreement: Array.Empty<Jwk>());

    /// <summary>
    /// The signer's DID document publishing, for each of the first <paramref name="signatures"/>
    /// kids, an authentication key other than the one the payload was signed with: every kid has a
    /// key, and no signature verifies under it.
    /// </summary>
    public static DidDocument SignerDocumentWithOtherKeys(int signatures)
        => LengthCap.DocWithKeys(
            Signer,
            authentication: Enumerable.Range(1, signatures)
                .Select(position => TestKeyMaterial.Generate(KeyType.Ed25519, Kid(position)).PublicJwk).ToArray(),
            keyAgreement: Array.Empty<Jwk>());
}

/// <summary>
/// One <see cref="DidCommClient.UnpackAsync"/> call on the calling thread, with the bytes that
/// thread allocated for it.
/// </summary>
internal static class UnpackAllocation
{
    /// <summary>
    /// Unpack <paramref name="packed"/> with a new receiver over the production
    /// <see cref="NetDidKeyService"/>, whose resolver serves <paramref name="document"/> for every
    /// DID, and return what the calling thread allocated between the call and its return. The call
    /// must have completed by then. With <paramref name="processWide"/>, what the whole process
    /// allocated over the same call is returned as well. With <paramref name="lookupsWait"/>, every
    /// answer of the resolver is a task that is not complete when it is handed out; the calling
    /// thread completes them one at a time until the unpack is complete, and that is measured too.
    /// </summary>
    public static Run Measure(string packed, DidDocument document, bool processWide = false, bool lookupsWait = false)
    {
        var held = lookupsWait ? new HeldLookups() : null;
        var resolver = new CallingThreadResolver(document, held);
        var receiver = new DidCommClient(
            new DictionarySecretsLookup(Array.Empty<Jwk>()), new NetDidKeyService(resolver), new DidCommOptions());

        var processBefore = processWide ? GC.GetTotalAllocatedBytes(precise: true) : 0;
        var threadBefore = GC.GetAllocatedBytesForCurrentThread();
        var unpack = receiver.UnpackAsync(packed);
        held?.ReleaseUntilComplete(unpack);
        var threadBytes = GC.GetAllocatedBytesForCurrentThread() - threadBefore;
        long? processBytes = processWide ? GC.GetTotalAllocatedBytes(precise: true) - processBefore : null;

        unpack.IsCompleted.Should().BeTrue("the unpack must end on the calling thread for that thread's counter to cover it");
        return new Run(threadBytes, processBytes, unpack.Exception?.InnerException, resolver.Lookups, resolver.LookupsOnAnotherThread, held?.Released ?? 0);
    }

    /// <summary>What one measured unpack allocated, how it ended and the lookups its resolver saw.</summary>
    /// <param name="ThreadBytes">The bytes the calling thread allocated over the call.</param>
    /// <param name="ProcessBytes">The bytes the whole process allocated over the call, when asked for.</param>
    /// <param name="Failure">The exception the unpack ended with, or <c>null</c> when it opened.</param>
    /// <param name="Lookups">The resolutions the resolver saw.</param>
    /// <param name="LookupsOnAnotherThread">Those of them made on a thread other than the measuring one.</param>
    /// <param name="LookupsWaitedFor">Those of them whose answer was not complete when handed out and was released by the measuring thread.</param>
    public sealed record Run(long ThreadBytes, long? ProcessBytes, Exception? Failure, int Lookups, int LookupsOnAnotherThread, int LookupsWaitedFor)
    {
        /// <summary>
        /// Assert the unpack resolved the signer's document once for each of the
        /// <paramref name="signatures"/> kids, on the measuring thread, and ended with a
        /// <see cref="CryptoException"/> carrying <paramref name="message"/>.
        /// </summary>
        public void ShouldHaveLookedUpEveryKidAndFailedWith(string message, int signatures)
        {
            Failure.Should().BeOfType<CryptoException>().Which.Message.Should().Be(message);
            Lookups.Should().Be(signatures, "every signature's kid is looked up once");
            LookupsOnAnotherThread.Should().Be(0, "the lookups must run on the measuring thread");
        }

        /// <summary>
        /// Assert the unpack resolved the signer's document <paramref name="lookups"/> times, on
        /// the measuring thread, <paramref name="waitedFor"/> of them waited for (all of them when
        /// not given), and ended with a <see cref="MalformedMessageException"/> carrying the
        /// parser's message for a signature whose protected header says <c>alg</c> "none".
        /// </summary>
        public void ShouldHaveBeenRefusedForAlgNoneAfter(int lookups, int? waitedFor = null)
        {
            Failure.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Be(RepeatedSignatureJws.AlgNoneRefusal);
            Lookups.Should().Be(lookups);
            LookupsWaitedFor.Should().Be(waitedFor ?? lookups);
            LookupsOnAnotherThread.Should().Be(0, "the lookups must run on the measuring thread");
        }
    }

    /// <summary>
    /// Serves one DID document for every DID and counts the resolutions and those made on a thread
    /// other than the one that created it. Without <paramref name="held"/> it answers with a
    /// completed task; with it, with a task that is not complete until the test releases it.
    /// </summary>
    private sealed class CallingThreadResolver(DidDocument document, HeldLookups? held) : IDidResolver
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;

        public int Lookups { get; private set; }

        public int LookupsOnAnotherThread { get; private set; }

        public bool CanResolve(string did) => true;

        public Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
        {
            Lookups++;
            if (Environment.CurrentManagedThreadId != _thread)
                LookupsOnAnotherThread++;
            var answer = Task.FromResult(new DidResolutionResult { DidDocument = document, ResolutionMetadata = new DidResolutionMetadata() });
            return held is null ? answer : Held(held, answer);
        }

        // A method of its own, so that an answer given at once takes nothing for the holding.
        private static Task<DidResolutionResult> Held(HeldLookups held, Task<DidResolutionResult> answer)
            => held.Hold(() => answer);
    }
}

/// <summary>
/// Lookups that have to be waited for. A task handed out by <see cref="Hold"/> is not complete
/// until the test releases it, and <see cref="ReleaseUntilComplete"/> releases the held tasks one at
/// a time, on the calling thread, until an unpack is complete.
/// </summary>
/// <remarks>
/// Nothing here starts a thread or waits for one. A held task is completed on the thread that calls
/// <see cref="ReleaseUntilComplete"/>, and the unpack that was waiting for it continues on that
/// thread, inside the release, up to its next held lookup or to its end. .NET continues an
/// <c>await</c> inside the completion only on a thread without a
/// <see cref="SynchronizationContext"/>, so the method takes the calling thread's away for as long
/// as it runs. An unpack that is not complete while no lookup is held is waiting for something
/// else; the method then throws instead of waiting.
/// </remarks>
internal sealed class HeldLookups
{
    private readonly Queue<TaskCompletionSource> _held = new();

    /// <summary>The held lookups released so far.</summary>
    public int Released { get; private set; }

    /// <summary>
    /// A task that is not complete when it is returned and, once released, ends as the task
    /// <paramref name="answer"/> returns ends: with its result or with its exception.
    /// </summary>
    public async Task<T> Hold<T>(Func<Task<T>> answer)
    {
        var release = new TaskCompletionSource();
        _held.Enqueue(release);
        await release.Task.ConfigureAwait(false);
        return await answer().ConfigureAwait(false);
    }

    /// <summary>
    /// Release the held lookups one at a time, each on the calling thread, until
    /// <paramref name="unpack"/> is complete.
    /// </summary>
    /// <exception cref="InvalidOperationException">When <paramref name="unpack"/> is not complete and no lookup is held.</exception>
    public void ReleaseUntilComplete(Task unpack)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            while (!unpack.IsCompleted)
            {
                if (!_held.TryDequeue(out var release))
                    throw new InvalidOperationException("The unpack is not complete and no lookup is held: it did not continue on the calling thread.");
                release.SetResult();
                Released++;
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }
}
