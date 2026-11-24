using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DidComm.Jose;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Json;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using DidComm.Tests.Facade;
using DidComm.Threading;
using NetCrypto;
using Xunit;
using Xunit.Abstractions;
using static DidComm.Tests.Protocols.Routing.ForwardTestEnvelopes;
using ThreadState = DidComm.Threading.ThreadState;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// #98 FX-2 (L-062): what <see cref="ForwardHandler"/> keeps of a forward between admission and the
/// pump is what its byte budget charges. A forward that waits for its acknowledgement, or is queued
/// behind a busy sink, keeps its plaintext as UTF-8 bytes, its envelope's recipient kid list and its
/// thread id — all charged — and small bounded fields, never the parsed message, whose JSON attachment
/// can be a DOM many times the plaintext. The pump rebuilds the result, and the sink gets an equal one.
/// Real anoncrypt to the mediator, so the unpack's verified snapshot is in play; the synthetic case
/// (a host-built result) is covered too.
/// </summary>
public sealed class ForwardHandlerRetentionTests
{
    internal const string Mediator = "did:example:m";
    internal const string MediatorKid = Mediator + "#ka-1";
    internal const string Bob = "did:example:bob";
    private static readonly TimeSpan Flush = TimeSpan.FromSeconds(30);

    /// <summary>A packed-message stand-in: a JSON object whose 4 096 entries become 4 096 DOM nodes once parsed.</summary>
    private static readonly string ManyEntries = "{\"protected\":\"a\",\"x\":[" + string.Join(",", Enumerable.Repeat("\"x\"", 4096)) + "]}";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_forward_waiting_for_its_acknowledgement_keeps_neither_its_parsed_message_nor_its_attachment(bool verified)
    {
        await using var f = await Fixture.CreateAsync();
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var admitted = await f.AdmitAsync(ManyEntries, acknowledged.Task, verified);
        f.Handler.Outstanding.Should().Be(1, "the forward holds its reservation until the acknowledgement");
        Collect();

        admitted.Message.IsAlive.Should().BeFalse("a forward waiting for its 202 keeps its plaintext bytes, not the parsed message (L-062)");
        admitted.Attachment.IsAlive.Should().BeFalse("nor the attachment's JSON DOM");

        acknowledged.SetResult();
        await f.Handler.FlushAsync(Flush);
        f.AssertRelayedOnce(admitted);
    }

    [Fact]
    public async Task A_forward_queued_behind_a_busy_sink_keeps_neither_its_parsed_message_nor_its_attachment()
    {
        await using var f = await Fixture.CreateAsync();
        f.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = await f.AdmitAsync(ManyEntries, Task.CompletedTask, verified: true);
        await f.Sink.Started.Task.WaitAsync(Flush);
        var queued = await f.AdmitAsync(ManyEntries, Task.CompletedTask, verified: true);
        f.Handler.Outstanding.Should().Be(2, "one forward is in the sink and one is queued behind it");
        Collect();

        queued.Message.IsAlive.Should().BeFalse("a queued forward keeps its plaintext bytes, not the parsed message (L-062)");
        queued.Attachment.IsAlive.Should().BeFalse("nor the attachment's JSON DOM");

        f.Sink.Gate.SetResult();
        await f.Handler.FlushAsync(Flush);
        f.Sink.Results.Select(r => r.OnwardPacked).Should().SatisfyRespectively(
            r => r.Should().Equal(first.Onward),
            r => r.Should().Equal(queued.Onward, "the queued forward relays the same bytes it would have"));
    }

    [Fact]
    public async Task The_sink_gets_the_same_unpack_outcome_and_the_same_verified_facts_after_the_message_is_parsed_again()
    {
        await using var f = await Fixture.CreateAsync();
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacked = await f.UnpackAsync(ManyEntries);
        var context = f.Context(unpacked, acknowledged.Task);
        var before = InboundObservation.FromUnpackResult(unpacked);
        before.RecipientKeyBinding.Should().NotBeNull("the fixture's key service captures same-document evidence");
        before.RecipientAddressing.Should().Be(RecipientAddressing.Addressed);

        (await f.Handler.HandleAsync(unpacked.Message, context, CancellationToken.None)).Should().BeNull();
        acknowledged.SetResult();
        await f.Handler.FlushAsync(Flush);

        var seen = f.Sink.Contexts.Should().ContainSingle().Subject;
        (seen with { Received = context.Received }).Should().Be(context, "everything but the received result is the context the handler was given");
        seen.Received.Should().NotBeSameAs(unpacked, "the result is rebuilt after the acknowledgement");
        seen.Received.Should().BeEquivalentTo(unpacked, o => o.Excluding(r => r.Message), "an equal unpack outcome: the same flags, kids, kid list and bindings");
        (seen.Received with { Message = unpacked.Message, AllRecipientKids = unpacked.AllRecipientKids }).Should().Be(unpacked, "and every other member is the very one the unpack produced");
        JsonSerializer.Serialize(seen.Received.Message, DidCommJson.Default)
            .Should().Be(JsonSerializer.Serialize(unpacked.Message, DidCommJson.Default), "the message parsed again is the one unpacked");
        // Observers and host sinks read the trust facts from the verified snapshot: the message
        // parsed again carries the same one, so they read what they read before.
        InboundObservation.FromUnpackResult(seen.Received).Should().BeEquivalentTo(before, o => o.Excluding(x => x.Message));
    }

    [Fact]
    public async Task A_forward_the_transport_never_acknowledges_is_dropped_with_its_bounded_id_and_releases_its_charge()
    {
        await using var f = await Fixture.CreateAsync();
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = await f.AdmitAsync(ManyEntries, acknowledged.Task, verified: true);
        f.Handler.OutstandingBytes.Should().BeGreaterThan(0);

        acknowledged.SetCanceled();
        await f.Handler.FlushAsync(Flush);

        f.Handler.OutstandingBytes.Should().Be(0);
        f.Sink.Count.Should().Be(0);
        f.Logger.Warnings.Should().ContainSingle(w => w.Formatted.Contains("did not acknowledge"))
            .Which.Formatted.Should().Contain(admitted.Id, "the drop line still names the forward by its (bounded) id");
    }

    [Fact]
    public async Task A_forward_is_charged_the_recipient_kid_list_and_thread_id_it_holds_and_the_sink_gets_the_same_kids()
    {
        // An anoncrypt forward that names the mediator and 192 other recipient kids of 4 000
        // characters: under 1 MiB, a small plaintext, and about 1.5 MB of kids the sink is handed.
        await using var f = await Fixture.CreateAsync(junkRecipients: true);
        var unpacked = await f.UnpackAsync(Small, junkRecipients: true);
        unpacked.AllRecipientKids.Should().HaveCount(1 + Fixture.JunkKidCount);
        InboundMessageSnapshot.TryGetFor(unpacked.Message, out var snapshot).Should().BeTrue();
        var plaintext = snapshot.Utf8ByteCount;
        var expected = plaintext + unpacked.AllRecipientKids.Sum(k => 2L * k.Length + 4) + 2L * unpacked.Message.Id.Length;
        expected.Should().BeGreaterThan(1_500_000, "the kid list, not the plaintext, is most of what the forward holds");
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        (await f.Handler.HandleAsync(unpacked.Message, f.Context(unpacked, acknowledged.Task), CancellationToken.None)).Should().BeNull();
        f.Handler.OutstandingBytes.Should().Be(expected, "the charge is the plaintext's UTF-8 bytes, 2 bytes a character and 4 a kid of the kid list, and 2 bytes a character of the thread id");

        acknowledged.SetResult();
        await f.Handler.FlushAsync(Flush);
        f.Sink.Contexts.Should().ContainSingle().Which.Received.AllRecipientKids.Should().Equal(unpacked.AllRecipientKids, "the kid list rebuilt is the list received, in order");
        f.Handler.OutstandingBytes.Should().Be(0);
    }

    [Fact]
    public async Task A_forward_whose_kid_list_does_not_fit_the_budget_is_dropped_as_saturated_although_its_plaintext_would()
    {
        // Before #98 this forward was admitted for its plaintext alone (a few hundred bytes) and held
        // its 1.5 MB kid list for free; 1 024 of them held about 3 GiB.
        await using var f = await Fixture.CreateAsync(byteBudget: 64 * 1024, junkRecipients: true);
        using var outcomes = ForwardOutcomeRecorder.For(f.Handler.Metrics.Meter);
        var unpacked = await f.UnpackAsync(Small, junkRecipients: true);
        InboundMessageSnapshot.TryGetFor(unpacked.Message, out var snapshot).Should().BeTrue();
        (snapshot.Utf8ByteCount + 2L * unpacked.Message.Id.Length).Should().BeLessThan(64 * 1024, "the plaintext and thread id alone fit the budget");

        (await f.Handler.HandleAsync(unpacked.Message, f.Context(unpacked, Task.CompletedTask), CancellationToken.None)).Should().BeNull();
        await f.Handler.FlushAsync(Flush);

        outcomes[ForwardOutcomes.DroppedSaturated].Should().Be(1);
        outcomes.Total.Should().Be(1);
        f.Handler.Outstanding.Should().Be(0);
        f.Sink.Count.Should().Be(0);
        f.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("saturated").And.Contain(unpacked.Message.Id);
    }

    /// <summary>A small packed-message stand-in.</summary>
    private const string Small = "{\"protected\":\"a\"}";

    /// <summary>Collect until weakly held garbage is gone: finalizers can keep a graph alive for one more cycle.</summary>
    private static void Collect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    /// <summary>What a test keeps of an admitted forward: weak references only, plus the expected relay.</summary>
    internal sealed record Admitted(WeakReference Message, WeakReference Attachment, string Id, byte[] Onward);

    /// <summary>
    /// The mediator (one X25519 key), a registry with Bob and a capturing sink; with
    /// <c>junkRecipients</c>, also <see cref="JunkDids"/> DIDs of <see cref="RecipientKeySelectionCap"/>
    /// key-agreement keys each, whose kids are 4 000 characters long (under the FR-API-09 4 096).
    /// </summary>
    internal sealed class Fixture : IAsyncDisposable
    {
        public const int JunkDids = 3;
        public const int RecipientKeySelectionCap = 64; // RecipientKeySelection.MaxKeysPerDid
        public const int JunkKidCount = JunkDids * RecipientKeySelectionCap;
        private readonly List<string> _junkDids = new();

        private Fixture() { }

        public DidCommClient Client { get; private set; } = null!;
        public DidCommOptions Options { get; } = new();
        public ForwardHandler Handler { get; private set; } = null!;
        public ForwardHandlerTests.CapturingSink Sink { get; } = new();
        public ForwardHandlerTests.CapturingLogger Logger { get; } = new();
        public InMemoryThreadStateStore Threads { get; } = new();

        public static Task<Fixture> CreateAsync(long byteBudget = ForwardHandler.DefaultByteBudget, bool junkRecipients = false)
        {
            var f = new Fixture();
            var key = TestKeyMaterial.Generate(KeyType.X25519, MediatorKid);
            var docs = new List<NetDid.Core.Model.DidDocument> { LengthCap.Doc(Mediator, keyAgreement: key.PublicJwk) };
            for (var d = 0; junkRecipients && d < JunkDids; d++)
            {
                var did = $"did:example:junk{d}";
                f._junkDids.Add(did);
                var keys = Enumerable.Range(0, RecipientKeySelectionCap)
                    .Select(i => TestKeyMaterial.Generate(KeyType.X25519, did + "#" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + new string('k', 4000 - did.Length - 5)).PublicJwk)
                    .ToArray();
                docs.Add(LengthCap.DocWithKeys(did, Array.Empty<Jwk>(), keys));
            }
            var keyService = new NetDidKeyService(new SpyDidResolver(docs.ToArray()));
            // Not the spy resolvers' secrets store: it records every kid it is asked about, which
            // for an envelope of long recipient kids is megabytes the measurement would count.
            f.Client = new DidCommClient(new DictionarySecretsLookup(new[] { key.PrivateJwk }), keyService, f.Options);
            var processor = new ForwardProcessor(f.Client, keyService, new ForwardProcessorOptions(), new StaticRecipientRoutingKeyProvider(null), f.Options);
            var recipients = new InMemoryForwardRecipientRegistry();
            recipients.Register(Bob);
            f.Handler = new ForwardHandler(processor, f.Sink, recipients, f.Logger, ForwardHandler.DefaultCapacity, shutdownGrace: TimeSpan.FromSeconds(2), byteBudget);
            return Task.FromResult(f);
        }

        /// <summary>
        /// A forward to Bob carrying <paramref name="payload"/>, anoncrypted to the mediator (and, with
        /// <paramref name="junkRecipients"/>, to every key of the junk DIDs too) and unpacked by it.
        /// </summary>
        public async Task<UnpackResult> UnpackAsync(string payload, bool junkRecipients = false)
            => await Client.UnpackAsync(await PackAsync(payload, junkRecipients));

        /// <summary>The envelope <see cref="UnpackAsync"/> unpacks.</summary>
        public async Task<string> PackAsync(string payload, bool junkRecipients = false)
        {
            var forward = ForwardMessage.Create(Mediator, Bob, new[] { payload });
            var recipients = junkRecipients ? _junkDids.Prepend(Mediator).ToArray() : new[] { Mediator };
            var packed = (await Client.PackEncryptedAsync(forward, new PackEncryptedOptions(Recipients: recipients))).Message;
            Encoding.UTF8.GetByteCount(packed).Should().BeLessThan(Options.MaxReceiveBytes, "the envelope fits the receive cap");
            return packed;
        }

        public ProtocolContext Context(UnpackResult unpacked, Task acknowledged)
            => new(unpacked, new ThreadState(unpacked.Message.Thid ?? unpacked.Message.Id), Client: null, Options, Threads)
            {
                Acknowledged = acknowledged,
            };

        /// <summary>
        /// Unpack a forward and hand it to the handler, keeping nothing of it but weak references:
        /// a separate, non-inlined frame, so no local of the test's own frame roots the message.
        /// <paramref name="verified"/> false hands the handler a host-built result (no verified snapshot).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public async Task<Admitted> AdmitAsync(string payload, Task acknowledged, bool verified)
        {
            var unpacked = await UnpackAsync(payload);
            if (!verified)
                unpacked = AsDecryptedByMediator(JsonSerializer.Deserialize<DidComm.Messages.Message>(JsonSerializer.Serialize(unpacked.Message, DidCommJson.Default), DidCommJson.Default)!, MediatorKid);
            var attachment = unpacked.Message.Attachments![0];
            var admitted = new Admitted(
                new WeakReference(unpacked.Message),
                new WeakReference(attachment.Data.Json),
                unpacked.Message.Id,
                ForwardProcessor.ExtractAttachmentBytes(attachment, long.MaxValue));
            (await Handler.HandleAsync(unpacked.Message, Context(unpacked, acknowledged), CancellationToken.None)).Should().BeNull();
            return admitted;
        }

        public void AssertRelayedOnce(Admitted admitted)
        {
            Sink.Results.Should().ContainSingle().Which.OnwardPacked.Should().Equal(admitted.Onward, "the forward relays the bytes it would have relayed");
            Sink.Contexts.Should().ContainSingle().Which.Received.Message.Id.Should().Be(admitted.Id);
            Handler.OutstandingBytes.Should().Be(0);
        }

        public async ValueTask DisposeAsync()
        {
            Sink.Gate?.TrySetResult();
            await Handler.DisposeAsync();
        }
    }
}

/// <summary>The retained-heap probe runs alone: other tests' allocations would blur its measurement.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ForwardRetentionProbeCollection
{
    public const string Name = "Forward retention probe (serial)";
}

/// <summary>
/// #98 FX-2 / D3-F4 (L-062), measured: K forwards held behind an acknowledgement retain at most about
/// the bytes the budget charges them. Before the fix a held forward kept its parsed message and its
/// uncharged recipient kid list: about 3× the charge for a flat payload, about 45× for an attachment of
/// 190 000 tiny JSON entries, and about 9 000× for a small forward that names 192 recipient kids of
/// 4 000 characters; after it, about 1×. The bound asserted (2×) leaves room for measurement noise;
/// the ratio is written to the test output.
/// </summary>
/// <remarks>
/// The measurement is a heap delta, so the warm-up matters. The unpack path rents its buffers from the
/// shared array pools, which keep up to one array per size on each thread: the first forwards unpacked
/// on a thread leave megabytes in its cache that are not the handler's. So K forwards run end to end
/// (the pump's own parse warms up), then K more are held on the measuring thread before the baseline,
/// with nothing in between that can move the test to another thread. The charge is a delta too.
/// Opt-in (<c>DIDCOMM_ADV98_PROBES=1</c>): a heap delta also counts what other test classes in the same
/// process allocate, so a full parallel run can push the ratio past its bound with no change in the
/// handler. The deterministic tests above (what a waiting forward references, and its exact charge)
/// are the regression; this probe is the measurement behind them.
/// </remarks>
[Collection(ForwardRetentionProbeCollection.Name)]
[Trait("Category", "Adv98Probe")]
public sealed class ForwardHandlerRetentionProbeTests
{
    private readonly ITestOutputHelper _out;

    public ForwardHandlerRetentionProbeTests(ITestOutputHelper output) => _out = output;

    [Adv98ProbeTheory]
    [InlineData("one flat string", 32)]
    [InlineData("190k tiny entries", 4)]
    [InlineData("192 long recipient kids", 8)]
    public async Task Held_forwards_retain_at_most_twice_the_bytes_they_are_charged(string shape, int k)
    {
        var junkKids = shape.Contains("kids", StringComparison.Ordinal);
        await using var f = await ForwardHandlerRetentionTests.Fixture.CreateAsync(junkRecipients: junkKids);
        var payload = shape switch
        {
            "190k tiny entries" => "{\"protected\":\"a\",\"x\":[" + string.Join(",", Enumerable.Repeat("\"x\"", 190_000)) + "]}",
            "one flat string" => "{\"protected\":\"a\",\"x\":\"" + new string('x', 700 * 1024) + "\"}",
            _ => "{\"protected\":\"a\"}",
        };
        var packed = await f.PackAsync(payload, junkKids);
        payload = null!;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        for (var i = 0; i < k; i++)
            await AdmitAsync(f, packed, Task.CompletedTask);
        await f.Handler.FlushAsync(TimeSpan.FromSeconds(30));
        f.Sink.Results.Clear();
        f.Sink.Contexts.Clear();
        for (var i = 0; i < k; i++)
            await AdmitAsync(f, packed, gate.Task);

        var baseline = LiveBytes();
        var chargedBefore = f.Handler.OutstandingBytes;
        for (var i = 0; i < k; i++)
            await AdmitAsync(f, packed, gate.Task);
        var retained = LiveBytes() - baseline;
        var charged = f.Handler.OutstandingBytes - chargedBefore;
        f.Handler.Outstanding.Should().Be(2 * k, "every forward is held behind its acknowledgement");
        _out.WriteLine($"{shape}: {k} held forwards, envelope {packed.Length:N0} chars; charged {charged:N0} B ({charged / k:N0} each); retained {retained:N0} B ({retained / k:N0} each); ratio x{retained / (double)charged:N2}");

        gate.SetResult();
        await f.Handler.FlushAsync(TimeSpan.FromSeconds(30));
        f.Sink.Count.Should().Be(3 * k, "every held forward is relayed once acknowledged");
        retained.Should().BeLessThanOrEqualTo(2 * charged, "a held forward keeps about the bytes it is charged: not its parsed message, and no kid list it is not charged for (L-062)");
    }

    /// <summary>Bytes in use after full, compacting collections (the LOH too): without compaction, the free space a parsed DOM leaves behind blurs the count.</summary>
    private static long LiveBytes()
    {
        for (var i = 0; i < 2; i++)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        return GC.GetTotalMemory(forceFullCollection: false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AdmitAsync(ForwardHandlerRetentionTests.Fixture f, string packed, Task acknowledged)
    {
        var unpacked = await f.Client.UnpackAsync(packed);
        (await f.Handler.HandleAsync(unpacked.Message, f.Context(unpacked, acknowledged), CancellationToken.None)).Should().BeNull();
    }
}

/// <summary>A <see cref="TheoryAttribute"/> that runs only when <c>DIDCOMM_ADV98_PROBES=1</c>.</summary>
internal sealed class Adv98ProbeTheoryAttribute : TheoryAttribute
{
    public Adv98ProbeTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("DIDCOMM_ADV98_PROBES") != "1")
            Skip = "A heap measurement that other tests in the process disturb: set DIDCOMM_ADV98_PROBES=1 to run it.";
    }
}
