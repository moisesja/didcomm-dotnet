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
/// test's thread before and after the call. It counts that thread only, so tests running in
/// parallel add nothing to it. It covers the whole unpack because the unpack runs on the calling
/// thread from start to end: the resolver answers with completed tasks, so no <c>await</c> on the
/// path suspends. Two assertions show that on every run: the task <c>UnpackAsync</c> returns is
/// already complete when the call returns, and the resolver saw every lookup on the test's thread.
/// Each input is unpacked once before it is measured, so what only a first use allocates (type
/// initialisation, pooled buffers) is not in the figures. No time is measured.
/// </para>
/// </remarks>
public sealed class UnknownSignerKeyAllocationTests(ITestOutputHelper output)
{
    /// <summary>The size the signed message is padded to, in bytes.</summary>
    internal const int PayloadBytes = 256 * 1024;

    private const int FewSignatures = 64;
    private const int ManySignatures = 1024;

    [Fact]
    public async Task Unpack_of_a_JWS_whose_signer_keys_are_all_unknown_allocates_far_less_than_a_payload_copy_for_each_signature()
    {
        var signed = await RepeatedSignatureJws.SignAsync(PayloadBytes);
        var few = signed.WithSignatures(FewSignatures).ShouldFitTheReceiveLimit();
        var many = signed.WithSignatures(ManySignatures).ShouldFitTheReceiveLimit();
        var document = RepeatedSignatureJws.SignerDocumentWithoutAuthenticationKeys();

        // Nothing is awaited from here on, so every unpack below runs on this one thread.
        UnpackAllocation.Measure(few, document);
        UnpackAllocation.Measure(many, document);
        var fewRun = UnpackAllocation.Measure(few, document);
        var manyRun = UnpackAllocation.Measure(many, document);

        var forEachAddedSignature = (manyRun.ThreadBytes - fewRun.ThreadBytes) / (double)(ManySignatures - FewSignatures);
        output.WriteLine(FormattableString.Invariant($"payload: {signed.PayloadBytes:N0} bytes"));
        output.WriteLine(FormattableString.Invariant($"{FewSignatures} signatures: wire {Encoding.UTF8.GetByteCount(few):N0} bytes, allocated {fewRun.ThreadBytes:N0} bytes"));
        output.WriteLine(FormattableString.Invariant($"{ManySignatures} signatures: wire {Encoding.UTF8.GetByteCount(many):N0} bytes, allocated {manyRun.ThreadBytes:N0} bytes"));
        output.WriteLine(FormattableString.Invariant($"allocated for each added signature: {forEachAddedSignature:N0} bytes"));

        fewRun.ShouldHaveLookedUpEveryKidAndFailedWith(RepeatedSignatureJws.NoKeyFor(FewSignatures), FewSignatures);
        manyRun.ShouldHaveLookedUpEveryKidAndFailedWith(RepeatedSignatureJws.NoKeyFor(ManySignatures), ManySignatures);
        forEachAddedSignature.Should().BeLessThan(signed.PayloadBytes / 8.0,
            "a signature whose key is not known must not take a copy of the payload");
    }
}

/// <summary>
/// Figures, not a check of the suite: the bytes one <see cref="DidCommClient.UnpackAsync"/> call
/// allocates for the input of <see cref="UnknownSignerKeyAllocationTests"/> with 64, 256 and 1,024
/// signatures whose keys are unknown, and with 64 signatures whose keys are known and none of which
/// verifies. Skipped unless <c>DIDCOMM_111_SIGNER_KEY_FIGURES=1</c>.
/// </summary>
/// <remarks>
/// Each input is unpacked once unmeasured and then three times measured. Every line gives the wire
/// size, the calling thread's allocated bytes, and the whole process's allocated bytes over the same
/// call. The process figure is this unpack's only when nothing else runs in the process, so run the
/// class alone: <c>dotnet test --filter FullyQualifiedName~SignerKeyAllocationFigures</c>.
/// </remarks>
public sealed class SignerKeyAllocationFigures(ITestOutputHelper output)
{
    /// <summary>The environment variable that turns the figures on.</summary>
    public const string Variable = "DIDCOMM_111_SIGNER_KEY_FIGURES";

    private static readonly int[] UnknownKeySignatures = [64, 256, 1024];
    private const int KnownKeySignatures = 64;

    [SignerKeyFiguresFact]
    public async Task Allocated_bytes_of_one_unpack_by_number_of_signatures_and_by_whether_their_keys_are_known()
    {
        var signed = await RepeatedSignatureJws.SignAsync(UnknownSignerKeyAllocationTests.PayloadBytes);
        output.WriteLine(FormattableString.Invariant($"payload: {signed.PayloadBytes:N0} bytes"));

        var unknown = RepeatedSignatureJws.SignerDocumentWithoutAuthenticationKeys();
        foreach (var signatures in UnknownKeySignatures)
        {
            Report($"keys unknown, {signatures} signatures", signed.WithSignatures(signatures), unknown,
                RepeatedSignatureJws.NoKeyFor(signatures), signatures);
        }

        Report($"keys known, no signature verifies, {KnownKeySignatures} signatures", signed.WithSignatures(KnownKeySignatures),
            RepeatedSignatureJws.SignerDocumentWithOtherKeys(KnownKeySignatures),
            RepeatedSignatureJws.DidNotVerifyFor(KnownKeySignatures), KnownKeySignatures);
    }

    private void Report(string input, string packed, DidDocument document, string expectedFailure, int signatures)
    {
        UnpackAllocation.Measure(packed, document);
        for (var run = 1; run <= 3; run++)
        {
            var measured = UnpackAllocation.Measure(packed, document, processWide: true);
            measured.ShouldHaveLookedUpEveryKidAndFailedWith(expectedFailure, signatures);
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

    private RepeatedSignatureJws(string payload, string @protected, string signature)
    {
        _payload = payload;
        _protected = @protected;
        _signature = signature;
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
            entry["signature"]!.GetValue<string>());
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

    /// <summary>The signer's DID document with no authentication keys: no kid of the JWS has a key.</summary>
    public static DidDocument SignerDocumentWithoutAuthenticationKeys() => new() { Id = new Did(Signer) };

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
    /// allocated over the same call is returned as well.
    /// </summary>
    public static Run Measure(string packed, DidDocument document, bool processWide = false)
    {
        var resolver = new CallingThreadResolver(document);
        var receiver = new DidCommClient(
            new DictionarySecretsLookup(Array.Empty<Jwk>()), new NetDidKeyService(resolver), new DidCommOptions());

        var processBefore = processWide ? GC.GetTotalAllocatedBytes(precise: true) : 0;
        var threadBefore = GC.GetAllocatedBytesForCurrentThread();
        var unpack = receiver.UnpackAsync(packed);
        var threadBytes = GC.GetAllocatedBytesForCurrentThread() - threadBefore;
        long? processBytes = processWide ? GC.GetTotalAllocatedBytes(precise: true) - processBefore : null;

        unpack.IsCompleted.Should().BeTrue("the unpack must end on the calling thread for that thread's counter to cover it");
        return new Run(threadBytes, processBytes, unpack.Exception?.InnerException, resolver.Lookups, resolver.LookupsOnAnotherThread);
    }

    /// <summary>What one measured unpack allocated, how it ended and the lookups its resolver saw.</summary>
    public sealed record Run(long ThreadBytes, long? ProcessBytes, Exception? Failure, int Lookups, int LookupsOnAnotherThread)
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
    }

    /// <summary>
    /// Serves one DID document for every DID with a completed task, and counts the resolutions and
    /// those made on a thread other than the one that created it.
    /// </summary>
    private sealed class CallingThreadResolver(DidDocument document) : IDidResolver
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
            return Task.FromResult(new DidResolutionResult { DidDocument = document, ResolutionMetadata = new DidResolutionMetadata() });
        }
    }
}
