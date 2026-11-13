using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using static DidComm.Tests.Protocols.Routing.ForwardTestEnvelopes;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// #95 JC-11 / design §10: every forward <see cref="ForwardHandler"/> ends is counted exactly once on
/// <c>didcomm.forward.outcomes</c>, with the outcome of the stage that ended it, and a forward handed
/// to the sink that returns normally is not counted by the handler (the sink counts it). Each
/// measurement is read from this test's own meter (<see cref="TestMeterFactory"/>), so tests running in
/// parallel never see each other's counts. Also pins the per-reason rate limit of the drop lines
/// (first and every 1000th at Warning, Debug otherwise, same text) and that no line carries a DID.
/// </summary>
public sealed class ForwardHandlerOutcomeMetricsTests
{
    private const string Mediator = "did:example:m";
    private const string Bob = "did:example:bob";
    private const string Carol = "did:example:carol-unregistered";
    private const string Inner = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";
    private static readonly TimeSpan Flush = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Fixed = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    // ── One outcome per inline drop ───────────────────────────────────────────────────────

    public static TheoryData<string, string> InlineDrops => new()
    {
        { "not-a-forward", ForwardOutcomes.DroppedMalformed },
        { "no-attachments", ForwardOutcomes.DroppedMalformed },
        { "plaintext", ForwardOutcomes.DroppedNotEncrypted },
        { "signed-only", ForwardOutcomes.DroppedNotEncrypted },
        { "no-recipient-kid", ForwardOutcomes.DroppedNotEncrypted },
        { "expired", ForwardOutcomes.DroppedExpired },
        { "next-not-a-did", ForwardOutcomes.DroppedUnknown },
        { "not-registered", ForwardOutcomes.DroppedUnknown },
        { "registry-throws", ForwardOutcomes.Faulted },
    };

    [Theory]
    [MemberData(nameof(InlineDrops))]
    public async Task Each_inline_drop_counts_exactly_one_outcome(string path, string expected)
    {
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        await using var h = new ForwardHandlerTests.Harness(metrics: new ForwardMetrics(meters));
        if (path == "registry-throws")
            h.Registry.Throw = new InvalidOperationException("registry down for " + Bob);

        await HandleInline(h, InlineDrop(path));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(0);
        outcomes.Counts.Should().Equal(new Dictionary<string, long> { [expected] = 1 });
    }

    [Fact]
    public async Task Request_cancelled_before_screening_counts_as_unacknowledged()
    {
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        await using var h = new ForwardHandlerTests.Harness(metrics: new ForwardMetrics(meters));
        using var request = new CancellationTokenSource();
        request.Cancel();
        var forward = Forward(Bob);

        var act = async () => await h.Handler.HandleAsync(forward, h.Context(AsDecryptedByMediator(forward)), request.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedUnacknowledged] = 1 });
    }

    [Fact]
    public async Task Request_cancelled_during_the_registry_lookup_counts_as_unacknowledged()
    {
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        await using var h = new ForwardHandlerTests.Harness(metrics: new ForwardMetrics(meters));
        using var request = new CancellationTokenSource();
        h.Registry.CancelOnLookup = request;
        var forward = Forward(Bob);

        var act = async () => await h.Handler.HandleAsync(forward, h.Context(AsDecryptedByMediator(forward)), request.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedUnacknowledged] = 1 });
    }

    [Fact]
    public async Task Saturation_counts_one_dropped_saturated_per_refused_forward()
    {
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        await using var h = new ForwardHandlerTests.Harness(capacity: 1, metrics: new ForwardMetrics(meters));
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Sink.Started.Task.WaitAsync(Flush);
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        h.Sink.Gate.TrySetResult();
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(1);
        outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedSaturated] = 2 },
            "the forward that reached the sink is the sink's to count");
    }

    // ── Acknowledgement ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancelled_acknowledgement_counts_as_unacknowledged()
    {
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        await using var h = new ForwardHandlerTests.Harness(metrics: new ForwardMetrics(meters));
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)), acknowledged.Task);
        acknowledged.TrySetCanceled();
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(0);
        outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedUnacknowledged] = 1 });
    }

    [Fact]
    public async Task Faulted_acknowledgement_counts_as_unacknowledged()
    {
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        await using var h = new ForwardHandlerTests.Harness(metrics: new ForwardMetrics(meters));

        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)), Task.FromException(new InvalidOperationException("400 after the handler")));
        await h.Handler.FlushAsync(Flush);

        outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedUnacknowledged] = 1 });
    }

    // ── The pump ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Forward_that_expires_while_queued_counts_as_expired()
    {
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        await using var h = new ForwardHandlerTests.Harness(metrics: new ForwardMetrics(meters));
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Sink.Started.Task.WaitAsync(Flush);
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob, expires: Fixed.AddSeconds(30).ToUnixTimeSeconds())));
        h.Now = Fixed.AddSeconds(31);
        h.Sink.Gate.TrySetResult();
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(1);
        outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedExpired] = 1 });
    }

    [Fact]
    public async Task Processor_refusing_the_forward_as_inconsistent_counts_as_malformed()
    {
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        await using var h = new ForwardHandlerTests.Harness(metrics: new ForwardMetrics(meters));

        await h.Dispatch(AsDecryptedByMediator(ForwardMessage.Create(Mediator, Bob, new[] { Inner, Inner })));
        await h.Handler.FlushAsync(Flush);

        outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedMalformed] = 1 });
    }

    [Fact]
    public async Task Processor_refusing_a_payload_it_cannot_rewrap_counts_as_malformed()
    {
        // Rewrap over a non-JSON attachment: MalformedMessageException from the processor.
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        await using var h = new ForwardHandlerTests.Harness(
            processorOptions: new ForwardProcessorOptions(Mode: RewrapMode.ReanoncryptToNext),
            metrics: new ForwardMetrics(meters));
        var forward = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithTo(Mediator)
            .WithBody(new JsonObject { ["next"] = Bob })
            .WithAttachment(new Attachment { Data = new AttachmentData { Base64 = Base64Url.EncodeUtf8("not json") } })
            .Build();

        await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        h.Logger.Entries.Should().Contain(e => e.Formatted.Contains(nameof(MalformedMessageException)));
        outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedMalformed] = 1 });
    }

    [Theory]
    [InlineData(nameof(DidResolutionException))]
    [InlineData(nameof(UnsupportedDidMethodException))]
    public async Task A_next_hop_the_rewrap_cannot_resolve_counts_as_unknown_not_faulted(string failure)
    {
        // #95 ADV-3 D4: the next hop's DID is the recipient's — any granted peer may register any DID —
        // so one that resolves to no usable keyAgreement key, or is of a refused method, means the
        // recipient cannot be reached: Dropped-Unknown, never a fault of the mediator. (This test
        // used to pin the DidResolutionException case as Faulted.)
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        await using var h = new ForwardHandlerTests.Harness(
            processorOptions: new ForwardProcessorOptions(Mode: RewrapMode.ReanoncryptToNext),
            metrics: new ForwardMetrics(meters),
            keyService: failure == nameof(DidResolutionException)
                ? new EmptyKeyService() // no keyAgreement key: the processor throws DidResolutionException
                : new ThrowingKeyService(new UnsupportedDidMethodException("web", Bob, "refused for this test")));

        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Handler.FlushAsync(Flush);

        outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedUnknown] = 1 });
        h.Logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Formatted.Contains("next hop cannot be reached: " + failure, StringComparison.Ordinal));
        ForwardHandlerTests.AssertLogsCarryNoExceptionObjectAndNone(h.Logger, Bob);
    }

    [Fact]
    public async Task Processor_fault_counts_as_faulted()
    {
        // Rewrap mode over a key service that throws something other than a resolution refusal: the
        // mediator's own collaborator failed.
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        await using var h = new ForwardHandlerTests.Harness(
            processorOptions: new ForwardProcessorOptions(Mode: RewrapMode.ReanoncryptToNext),
            metrics: new ForwardMetrics(meters),
            keyService: new ThrowingKeyService(new InvalidOperationException("the resolver is down")));

        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Handler.FlushAsync(Flush);

        outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.Faulted] = 1 });
        h.Logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Formatted.Contains("not relayed: InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sink_that_throws_a_ConsistencyException_counts_as_faulted_not_malformed()
    {
        // Only the processor's refusal is a malformed forward; the same type out of the sink is a fault.
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        await using var h = new ForwardHandlerTests.Harness(metrics: new ForwardMetrics(meters));
        h.Sink.Throw = new ConsistencyException("sink refused");

        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Handler.FlushAsync(Flush);

        outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.Faulted] = 1 });
    }

    [Fact]
    public async Task Sink_that_returns_normally_leaves_the_count_to_the_sink()
    {
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        await using var h = new ForwardHandlerTests.Harness(metrics: new ForwardMetrics(meters));

        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(2);
        outcomes.Total.Should().Be(0, "a forward handed to a sink that returns is counted by that sink");
    }

    [Fact]
    public async Task A_sink_counting_on_the_shared_metrics_gives_one_outcome_per_forward_in_total()
    {
        // The mediator's sink records on the same ForwardMetrics the handler was given.
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        var metrics = new ForwardMetrics(meters);
        var sink = new CountingSink(metrics);
        await using var h = new ForwardHandlerTests.Harness(metrics: metrics, sink: sink);

        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Dispatch(AsDecryptedByMediator(Forward(Carol)));
        await h.Dispatch(AsPlaintext(Forward(Bob)));
        await h.Handler.FlushAsync(Flush);

        outcomes.Counts.Should().Equal(new Dictionary<string, long>
        {
            [ForwardOutcomes.Queued] = 1,
            [ForwardOutcomes.DroppedUnknown] = 1,
            [ForwardOutcomes.DroppedNotEncrypted] = 1,
        });
    }

    // ── Shutdown ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Shutdown_counts_every_discarded_forward_once_including_the_one_in_the_sink()
    {
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        await using var h = new ForwardHandlerTests.Harness(capacity: 8, shutdownGrace: TimeSpan.FromSeconds(5), metrics: new ForwardMetrics(meters));
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // One in the sink (cancelled by the shutdown), three queued behind it (discarded in bulk),
        // one parked on its acknowledgement (discarded when the shutdown unblocks it).
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Sink.Started.Task.WaitAsync(Flush);
        for (var i = 0; i < 3; i++)
            await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        var neverAcknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)), neverAcknowledged.Task);

        await h.Handler.DisposeAsync();
        await h.Handler.ShutdownCompletion.WaitAsync(Flush);

        outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedShutdown] = 5 });
        h.Logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Formatted.Contains("discarded"))
            .Which.Formatted.Should().Contain("4", "the summary line counts the queued and parked forwards, not the one cancelled inside the sink");
    }

    // ── Who owns the counter ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Public_constructor_counts_on_its_own_meter_and_disposes_it_with_the_handler()
    {
        var handler = new ForwardHandler(NewProcessor(), new ForwardHandlerTests.CapturingSink(), new InMemoryForwardRecipientRegistry());
        using var outcomes = ForwardOutcomeRecorder.For(handler.Metrics.Meter);
        var forward = Forward(Bob);

        await handler.HandleAsync(forward, Context(AsPlaintext(forward)), CancellationToken.None);
        outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedNotEncrypted] = 1 });

        await handler.DisposeAsync();
        await handler.ShutdownCompletion.WaitAsync(Flush);
        outcomes.Completed.Should().BeTrue("the handler created this meter and disposes it");
    }

    [Fact]
    public async Task Shared_metrics_outlive_the_handler()
    {
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        var metrics = new ForwardMetrics(meters);
        var h = new ForwardHandlerTests.Harness(metrics: metrics);

        h.Handler.Metrics.Should().BeSameAs(metrics);
        await h.DisposeAsync();
        await h.Handler.ShutdownCompletion.WaitAsync(Flush);

        outcomes.Completed.Should().BeFalse("the handler never disposes a counter it was given");
        metrics.Record(ForwardOutcomes.Queued);
        outcomes[ForwardOutcomes.Queued].Should().Be(1);
    }

    [Fact]
    public void Internal_constructor_refuses_null_metrics()
    {
        var act = () => new ForwardHandler(NewProcessor(), new ForwardHandlerTests.CapturingSink(), new InMemoryForwardRecipientRegistry(), (ForwardMetrics)null!, null);

        act.Should().Throw<ArgumentNullException>().WithParameterName("metrics");
    }

    // ── Rate-limited drop lines ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_drop_reason_logs_the_first_and_every_1000th_at_Warning_and_the_rest_at_Debug_with_the_same_text()
    {
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        await using var h = new ForwardHandlerTests.Harness(metrics: new ForwardMetrics(meters));

        for (var i = 0; i < 2001; i++)
            await h.Dispatch(AsDecryptedByMediator(Forward(Carol)));

        var lines = h.Logger.Entries.Where(e => e.Formatted.Contains("not a registered recipient")).ToList();
        lines.Should().HaveCount(2001, "every drop is still written, the quiet ones at Debug");
        lines.Count(e => e.Level == LogLevel.Warning).Should().Be(3, "the 1st, the 1000th and the 2000th");
        lines.Count(e => e.Level == LogLevel.Debug).Should().Be(1998);
        lines.Select(e => e.Level).ToList().IndexOf(LogLevel.Warning).Should().Be(0);
        lines[999].Level.Should().Be(LogLevel.Warning);
        lines[1999].Level.Should().Be(LogLevel.Warning);
        lines.Select(e => e.State["{OriginalFormat}"]).Distinct().Should().ContainSingle("Warning and Debug lines share one template");
        outcomes[ForwardOutcomes.DroppedUnknown].Should().Be(2001, "the metric counts every drop, quiet or not");
    }

    [Fact]
    public async Task Each_drop_reason_has_its_own_rate_limit()
    {
        await using var h = new ForwardHandlerTests.Harness();

        for (var i = 0; i < 5; i++)
            await h.Dispatch(AsDecryptedByMediator(Forward(Carol)));
        await h.Dispatch(AsPlaintext(Forward(Bob)));
        await h.Dispatch(AsDecryptedByMediator(Forward("not-a-did")));

        h.Logger.Warnings.Select(e => e.Formatted).Should().SatisfyRespectively(
            first => first.Should().Contain("not a registered recipient"),
            second => second.Should().Contain("plaintext, signed-only"),
            third => third.Should().Contain("not a DID"));
        h.Logger.Entries.Count(e => e.Level == LogLevel.Debug && e.Formatted.Contains("not a registered recipient")).Should().Be(4);
    }

    [Fact]
    public async Task Saturation_line_keeps_its_total_and_is_written_at_Debug_between_the_loud_ones()
    {
        await using var h = new ForwardHandlerTests.Harness(capacity: 1);
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Sink.Started.Task.WaitAsync(Flush);

        for (var i = 0; i < 3; i++)
            await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));

        var lines = h.Logger.Entries.Where(e => e.Formatted.Contains("saturated")).ToList();
        lines.Select(e => e.Level).Should().Equal(LogLevel.Warning, LogLevel.Debug, LogLevel.Debug);
        lines[2].Formatted.Should().Contain("Total dropped: 3");
        h.Sink.Gate.TrySetResult();
    }

    [Fact]
    public async Task Expired_while_queued_line_is_Information_first_then_Debug()
    {
        await using var h = new ForwardHandlerTests.Harness();
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Sink.Started.Task.WaitAsync(Flush);
        for (var i = 0; i < 3; i++)
            await h.Dispatch(AsDecryptedByMediator(Forward(Bob, expires: Fixed.AddSeconds(30).ToUnixTimeSeconds())));

        h.Now = Fixed.AddSeconds(31);
        h.Sink.Gate.TrySetResult();
        await h.Handler.FlushAsync(Flush);

        h.Logger.Entries.Where(e => e.Formatted.Contains("expired while queued")).Select(e => e.Level)
            .Should().Equal(LogLevel.Information, LogLevel.Debug, LogLevel.Debug);
    }

    // ── No DID in any line ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task No_line_on_any_drop_path_carries_a_DID_or_an_exception_object()
    {
        await using var h = new ForwardHandlerTests.Harness(capacity: 2);
        foreach (var path in new[] { "not-a-forward", "no-attachments", "plaintext", "signed-only", "no-recipient-kid", "expired", "next-not-a-did", "not-registered" })
            await HandleInline(h, InlineDrop(path));
        await h.Dispatch(AsDecryptedByMediator(ForwardMessage.Create(Mediator, Bob, new[] { Inner, Inner })));
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)), Task.FromCanceled(new CancellationToken(canceled: true)));
        h.Sink.Throw = new InvalidOperationException("sink down for " + Bob);
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Handler.FlushAsync(Flush);
        h.Registry.Throw = new InvalidOperationException("registry down for " + Bob);
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        h.Registry.Throw = null;
        h.Sink.Throw = null;
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var i = 0; i < 4; i++)
            await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        h.Sink.Gate.TrySetResult();
        await h.Handler.FlushAsync(Flush);

        h.Logger.Entries.Should().HaveCountGreaterThanOrEqualTo(12);
        ForwardHandlerTests.AssertLogsCarryNoExceptionObjectAndNone(h.Logger, "did:", MediatorKid, SignerKid, "down for");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static Message Forward(string next, long? expires = null) => ForwardHandlerTests.Forward(next, expires);

    /// <summary>Straight into the handler (a non-forward type would not be routed to it by the dispatcher).</summary>
    private static Task<Message?> HandleInline(ForwardHandlerTests.Harness h, UnpackResult unpacked)
        => h.Handler.HandleAsync(unpacked.Message, h.Context(unpacked), CancellationToken.None);

    private static UnpackResult InlineDrop(string path) => path switch
    {
        "not-a-forward" => AsDecryptedByMediator(new MessageBuilder().WithType("https://didcomm.org/trust-ping/2.0/ping").WithBody(new JsonObject()).Build()),
        "no-attachments" => AsDecryptedByMediator(new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithTo(Mediator)
            .WithBody(new JsonObject { ["next"] = Bob })
            .Build()),
        "plaintext" => AsPlaintext(Forward(Bob)),
        "signed-only" => AsSignedOnly(Forward(Bob)),
        "no-recipient-kid" => AsEncryptedWithoutRecipientKid(Forward(Bob)),
        "expired" => AsDecryptedByMediator(Forward(Bob, expires: Fixed.AddSeconds(-10).ToUnixTimeSeconds())),
        "next-not-a-did" => AsDecryptedByMediator(Forward("not-a-did")),
        "not-registered" => AsDecryptedByMediator(Forward(Carol)),
        "registry-throws" => AsDecryptedByMediator(Forward(Bob)),
        _ => throw new ArgumentOutOfRangeException(nameof(path)),
    };

    private static ProtocolContext Context(UnpackResult unpacked)
        => new(unpacked, new DidComm.Threading.ThreadState(unpacked.Message.Thid ?? unpacked.Message.Id), Client: null,
            new DidCommOptions { Clock = () => Fixed }, new DidComm.Threading.InMemoryThreadStateStore());

    /// <summary>A sink that counts on the shared metrics, as the mediator's sink does.</summary>
    private sealed class CountingSink(ForwardMetrics metrics) : IForwardSink
    {
        public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct)
        {
            metrics.Record(ForwardOutcomes.Queued);
            return Task.CompletedTask;
        }
    }
}
