using System.Runtime.CompilerServices;
using System.Text.Json;
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
/// pump is bounded by what its byte budget charges. The budget charges the plaintext's UTF-8 bytes; a
/// forward that waits for its acknowledgement, or is queued behind a busy sink, keeps those bytes and
/// small bounded fields, never the parsed message, whose JSON attachment can be a DOM many times the
/// plaintext. The pump parses the message again from the bytes, and the sink sees what it saw before.
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
        (seen.Received with { Message = unpacked.Message }).Should().Be(unpacked, "everything but the message object is what the unpack produced");
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

    /// <summary>The mediator (one X25519 key), a registry with Bob and a capturing sink.</summary>
    internal sealed class Fixture : IAsyncDisposable
    {
        private Fixture() { }

        public DidCommClient Client { get; private set; } = null!;
        public DidCommOptions Options { get; } = new();
        public ForwardHandler Handler { get; private set; } = null!;
        public ForwardHandlerTests.CapturingSink Sink { get; } = new();
        public ForwardHandlerTests.CapturingLogger Logger { get; } = new();
        public InMemoryThreadStateStore Threads { get; } = new();

        public static Task<Fixture> CreateAsync(long byteBudget = ForwardHandler.DefaultByteBudget)
        {
            var f = new Fixture();
            var key = TestKeyMaterial.Generate(KeyType.X25519, MediatorKid);
            var keyService = new NetDidKeyService(new SpyDidResolver(LengthCap.Doc(Mediator, keyAgreement: key.PublicJwk)));
            f.Client = new DidCommClient(new SpySecretsResolver(key.PrivateJwk), keyService, f.Options);
            var processor = new ForwardProcessor(f.Client, keyService, new ForwardProcessorOptions(), new StaticRecipientRoutingKeyProvider(null), f.Options);
            var recipients = new InMemoryForwardRecipientRegistry();
            recipients.Register(Bob);
            f.Handler = new ForwardHandler(processor, f.Sink, recipients, f.Logger, ForwardHandler.DefaultCapacity, shutdownGrace: TimeSpan.FromSeconds(2), byteBudget);
            return Task.FromResult(f);
        }

        /// <summary>A forward to Bob carrying <paramref name="payload"/>, anoncrypted to the mediator and unpacked by it.</summary>
        public async Task<UnpackResult> UnpackAsync(string payload)
        {
            var forward = ForwardMessage.Create(Mediator, Bob, new[] { payload });
            var packed = await Client.PackEncryptedAsync(forward, new PackEncryptedOptions(Recipients: new[] { Mediator }));
            return await Client.UnpackAsync(packed.Message);
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
                ForwardProcessor.ExtractAttachmentBytes(attachment));
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
/// the bytes the budget charges them. Before the fix a held forward kept its parsed message: about 3×
/// the charge for a flat payload and about 50× for an attachment of 190 000 tiny JSON entries; after
/// it, about 1×. The bound asserted (2×) leaves room for measurement noise; the ratio is written to
/// the test output.
/// </summary>
/// <remarks>
/// The measurement is a heap delta, so the warm-up matters. The unpack path rents its buffers from the
/// shared array pools, which keep up to one array per size on each thread: the first forwards unpacked
/// on a thread leave megabytes in its cache that are not the handler's. So K forwards run end to end
/// (the pump's own parse warms up), then K more are held on the measuring thread before the baseline,
/// with nothing in between that can move the test to another thread. The charge is a delta too.
/// </remarks>
[Collection(ForwardRetentionProbeCollection.Name)]
[Trait("Category", "Adv98Probe")]
public sealed class ForwardHandlerRetentionProbeTests
{
    private readonly ITestOutputHelper _out;

    public ForwardHandlerRetentionProbeTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(false, 32)]
    [InlineData(true, 4)]
    public async Task Held_forwards_retain_at_most_twice_the_plaintext_bytes_they_are_charged(bool manyTinyEntries, int k)
    {
        await using var f = await ForwardHandlerRetentionTests.Fixture.CreateAsync();
        var payload = manyTinyEntries
            ? "{\"protected\":\"a\",\"x\":[" + string.Join(",", Enumerable.Repeat("\"x\"", 190_000)) + "]}"
            : "{\"protected\":\"a\",\"x\":\"" + new string('x', 700 * 1024) + "\"}";
        var packed = (await f.Client.PackEncryptedAsync(
            ForwardMessage.Create(ForwardHandlerRetentionTests.Mediator, ForwardHandlerRetentionTests.Bob, new[] { payload }),
            new PackEncryptedOptions(Recipients: new[] { ForwardHandlerRetentionTests.Mediator }))).Message;
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
        _out.WriteLine($"{(manyTinyEntries ? "190k tiny entries" : "one flat string")}: {k} held forwards, envelope {packed.Length:N0} chars; charged {charged:N0} B ({charged / k:N0} each); retained {retained:N0} B ({retained / k:N0} each); ratio x{retained / (double)charged:N2}");

        gate.SetResult();
        await f.Handler.FlushAsync(TimeSpan.FromSeconds(30));
        f.Sink.Count.Should().Be(3 * k, "every held forward is relayed once acknowledged");
        retained.Should().BeLessThanOrEqualTo(2 * charged, "a held forward keeps about the plaintext bytes it is charged, not its parsed message (L-062)");
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
