using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Tests.Facade;
using DidComm.Threading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using static DidComm.Tests.Protocols.Routing.ForwardTestEnvelopes;
using ThreadState = DidComm.Threading.ThreadState;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// Plan matrix U2 (#90): <see cref="ForwardHandler"/> driven through
/// <see cref="ProtocolDispatcher.DispatchAsync"/> with synthetic <see cref="UnpackResult"/>s, a
/// capturing sink, a capturing logger, an <see cref="InMemoryForwardRecipientRegistry"/> holding
/// <c>did:example:bob</c>, and a fixed clock. Pins the FR-ROUTE-11 posture (drop + log the id only +
/// no reply), FR-ROUTE-09 expiry inline and in the pump, FR-ROUTE-07 silence, the detached bounded
/// queue (saturation, shutdown, parallel admission) and the no-wire-values logging posture.
/// </summary>
public sealed class ForwardHandlerTests
{
    private const string Mediator = "did:example:m";
    private const string Bob = "did:example:bob";
    private const string Inner = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";
    private static readonly TimeSpan Flush = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Fixed = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    // ── Identity ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProtocolUri_is_routing_2_0_and_Roles_is_mediator()
    {
        await using var h = new Harness();

        h.Handler.ProtocolUri.Should().Be("https://didcomm.org/routing/2.0");
        h.Handler.Roles.Should().Equal("mediator");
        h.Handler.Should().BeAssignableTo<IProtocolHandlerRoles>();
    }

    // ── Happy path ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Registered_encrypted_forward_reaches_the_sink_exactly_once_with_NoReply_and_Handler_ForwardHandler()
    {
        await using var h = new Harness();
        var forward = Forward(Bob);

        var outcome = await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        outcome.Reply.Should().BeNull();
        outcome.Handler.Should().BeSameAs(h.Handler);
        h.Sink.Count.Should().Be(1);
        var result = h.Sink.Results.Single();
        result.NextHop.Should().Be(Bob);
        Encoding.UTF8.GetString(result.OnwardPacked).Should().Be(Inner);
        h.Sink.Contexts.Single().Received.Message.Id.Should().Be(forward.Id);
        h.Handler.Outstanding.Should().Be(0);
    }

    [Fact]
    public async Task Sink_receives_the_handlers_shutdown_token_not_the_request_token()
    {
        // J7: a client abort after the 202 must not cancel the relay.
        await using var h = new Harness();
        using var request = new CancellationTokenSource();

        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)), request.Token);
        request.Cancel();
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(1);
        h.Sink.Tokens.Single().IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task Delay_milli_reaches_the_sink_as_NotBefore_on_the_fixed_clock()
    {
        await using var h = new Harness();
        var forward = Forward(Bob);
        forward.AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["delay_milli"] = JsonDocument.Parse("750").RootElement.Clone(),
        };

        await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        var result = h.Sink.Results.Single();
        result.Delay.Should().Be(TimeSpan.FromMilliseconds(750));
        result.NotBefore.Should().Be(Fixed.AddMilliseconds(750));
    }

    [Fact]
    public async Task Base64_attachment_reaches_the_sink_decoded()
    {
        await using var h = new Harness();
        var forward = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithTo(Mediator)
            .WithBody(new JsonObject { ["next"] = Bob })
            .WithAttachment(new Attachment { Data = new AttachmentData { Base64 = Base64Url.EncodeUtf8(Inner) } })
            .Build();

        await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        Encoding.UTF8.GetString(h.Sink.Results.Single().OnwardPacked).Should().Be(Inner);
    }

    [Fact]
    public async Task Forward_whose_to_names_someone_else_is_still_relayed_because_posture_is_kid_based()
    {
        await using var h = new Harness();
        var forward = ForwardMessage.Create("did:example:someone-else", Bob, new[] { Inner });

        await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(1);
    }

    // ── FR-ROUTE-09 expiry ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Expired_forward_is_dropped_before_the_sink()
    {
        await using var h = new Harness();
        var forward = Forward(Bob, expires: Fixed.AddSeconds(-10).ToUnixTimeSeconds());

        var outcome = await h.Dispatch(AsDecryptedByMediator(forward));

        outcome.Result.Should().Be(DispatchResult.NoReply);
        h.Handler.Outstanding.Should().Be(0, "an expired forward never costs a queue slot");
        await h.Handler.FlushAsync(Flush);
        h.Sink.Count.Should().Be(0);
        h.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("expired at relay time").And.Contain(forward.Id);
        h.Registry.Calls.Should().Be(0, "expiry is judged before the registry is consulted");
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task Zero_or_negative_expires_time_is_dropped(long expires)
    {
        await using var h = new Harness();

        await h.Dispatch(AsDecryptedByMediator(Forward(Bob, expires: expires)));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(0);
        h.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("expired");
    }

    [Fact]
    public async Task Forward_expiring_within_skew_is_relayed()
    {
        await using var h = new Harness(options => options.ExpiresClockSkew = TimeSpan.FromSeconds(5));
        var forward = Forward(Bob, expires: Fixed.AddSeconds(-3).ToUnixTimeSeconds());

        await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(1);
        h.Logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Forward_expiring_exactly_now_is_relayed()
    {
        await using var h = new Harness();

        await h.Dispatch(AsDecryptedByMediator(Forward(Bob, expires: Fixed.ToUnixTimeSeconds())));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(1);
    }

    [Fact]
    public async Task Forward_that_expires_while_queued_is_dropped_by_the_pump()
    {
        await using var h = new Harness();
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // First forward: reaches the sink and holds the pump on the gate.
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Sink.Started.Task.WaitAsync(Flush);

        // Second forward: admitted now (not yet expired), expires while queued behind the first.
        var queued = Forward(Bob, expires: Fixed.AddSeconds(30).ToUnixTimeSeconds());
        await h.Dispatch(AsDecryptedByMediator(queued));
        h.Handler.Outstanding.Should().Be(2);

        h.Now = Fixed.AddSeconds(31);
        h.Sink.Gate.TrySetResult();
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(1, "only the first forward reached the sink");
        h.Logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Formatted.Contains("expired while queued"))
            .Which.Formatted.Should().Contain(queued.Id);
        h.Handler.Outstanding.Should().Be(0);
    }

    // ── FR-ROUTE-11 posture ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Plaintext_forward_is_dropped()
    {
        await using var h = new Harness();
        var forward = Forward(Bob);

        var outcome = await h.Dispatch(AsPlaintext(forward));
        await h.Handler.FlushAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        outcome.Handler.Should().BeSameAs(h.Handler);
        h.Sink.Count.Should().Be(0);
        h.Handler.Outstanding.Should().Be(0);
        h.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("FR-ROUTE-11").And.Contain(forward.Id);
        h.Registry.Calls.Should().Be(0, "the registry is never consulted for a forward that fails the posture check");
    }

    [Fact]
    public async Task Signed_only_forward_is_dropped()
    {
        await using var h = new Harness();

        await h.Dispatch(AsSignedOnly(Forward(Bob)));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(0);
        h.Registry.Calls.Should().Be(0);
        h.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().NotContain(SignerKid);
    }

    [Fact]
    public async Task Encrypted_forward_without_RecipientKid_is_dropped()
    {
        await using var h = new Harness();

        await h.Dispatch(AsEncryptedWithoutRecipientKid(Forward(Bob)));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(0);
        h.Registry.Calls.Should().Be(0);
        h.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("FR-ROUTE-11");
    }

    [Fact]
    public async Task Encrypted_forward_with_empty_RecipientKid_is_dropped()
    {
        await using var h = new Harness();

        await h.Dispatch(AsDecryptedByMediator(Forward(Bob), recipientKid: ""));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(0);
        h.Registry.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Unknown_next_is_dropped_with_NoReply()
    {
        await using var h = new Harness();
        var forward = Forward("did:example:carol");

        var outcome = await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        outcome.Reply.Should().BeNull("no problem report toward the sender (D4)");
        h.Sink.Count.Should().Be(0);
        h.Registry.Calls.Should().Be(1);
        h.Registry.Requested.Single().Should().Be("did:example:carol");
        h.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("not a registered recipient").And.NotContain("carol");
    }

    [Fact]
    public async Task Next_as_DID_URL_with_fragment_matches_the_registered_bare_DID()
    {
        await using var h = new Harness();

        await h.Dispatch(AsDecryptedByMediator(Forward(Bob + "#key-1")));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(1);
        h.Sink.Results.Single().NextHop.Should().Be(Bob + "#key-1", "the sink sees next unchanged");
        h.Registry.Requested.Single().Should().Be(Bob, "the registry is asked for the DID subject");
    }

    [Theory]
    [InlineData("did:example:bob/some/path")]
    [InlineData("did:example:bob?versionId=1")]
    [InlineData("did:example:bob/path?q=1")]
    public async Task Next_with_path_or_query_but_no_fragment_matches_the_registered_bare_DID(string next)
    {
        await using var h = new Harness();

        await h.Dispatch(AsDecryptedByMediator(Forward(next)));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(1);
        h.Registry.Requested.Single().Should().Be(Bob);
    }

    [Fact]
    public async Task Registry_lookup_is_ordinal_so_a_case_variant_of_next_is_dropped()
    {
        await using var h = new Harness();

        await h.Dispatch(AsDecryptedByMediator(Forward("did:example:Bob")));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(0);
    }

    [Theory]
    [InlineData("not-a-did")]
    [InlineData("did:")]
    [InlineData("https://example.com/bob")]
    [InlineData("#key-1")]
    public async Task Non_DID_next_is_dropped(string next)
    {
        await using var h = new Harness();
        var forward = Forward(next);

        var outcome = await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        h.Sink.Count.Should().Be(0);
        h.Registry.Calls.Should().Be(0, "a non-DID never reaches the registry");
        h.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("not a DID").And.NotContain(next);
    }

    [Fact]
    public async Task Next_over_the_FR_API_09_cap_is_dropped_not_thrown()
    {
        await using var h = new Harness();
        var forward = Forward(LengthCap.KidOfLength(Bob, LengthCap.KeyIdCap + 1));

        var outcome = await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        h.Sink.Count.Should().Be(0);
        h.Registry.Calls.Should().Be(0);
        var warning = h.Logger.Warnings.Should().ContainSingle().Which;
        warning.Formatted.Should().Contain("malformed forward").And.Contain("MalformedMessageException").And.Contain(forward.Id).And.NotContain(LengthCap.Run);
        warning.Exception.Should().BeNull("the exception type name is logged, never the exception object");
        AssertLogsCarryNoExceptionObjectAndNone(h.Logger, LengthCap.Run);
    }

    [Fact]
    public async Task Forward_without_attachments_is_dropped_not_thrown()
    {
        await using var h = new Harness();
        var forward = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithTo(Mediator)
            .WithBody(new JsonObject { ["next"] = Bob })
            .Build();

        var outcome = await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        h.Sink.Count.Should().Be(0);
        h.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("malformed forward");
    }

    [Fact]
    public async Task Forward_without_next_is_dropped_not_thrown()
    {
        await using var h = new Harness();
        var forward = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithTo(Mediator)
            .WithBody(new JsonObject())
            .WithAttachment(new Attachment { Data = new AttachmentData { Json = JsonNode.Parse(Inner) } })
            .Build();

        var outcome = await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        h.Sink.Count.Should().Be(0);
    }

    [Fact]
    public async Task Two_attachments_are_dropped_in_the_pump_and_logged()
    {
        await using var h = new Harness();
        var forward = ForwardMessage.Create(Mediator, Bob, new[] { Inner, Inner });

        var outcome = await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        h.Sink.Count.Should().Be(0);
        var warning = h.Logger.Warnings.Should().ContainSingle().Which;
        warning.Formatted.Should().Contain("not relayed").And.Contain(forward.Id).And.Contain("ConsistencyException");
        h.Handler.Outstanding.Should().Be(0, "the slot is released after a pump failure");
    }

    [Fact]
    public async Task Attachment_without_data_is_dropped_in_the_pump_and_logged()
    {
        await using var h = new Harness();
        var forward = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithTo(Mediator)
            .WithBody(new JsonObject { ["next"] = Bob })
            .WithAttachment(new Attachment { Data = new AttachmentData { Hash = "stub" } })
            .Build();

        await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(0);
        h.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("not relayed").And.Contain("ConsistencyException");
    }

    [Fact]
    public async Task Processor_fault_in_the_pump_is_logged_and_never_escapes()
    {
        // Rewrap mode over a key service that throws: the pump's ProcessAsync faults. (A next hop that
        // resolves to no usable key is not a fault since #95 ADV-3 D4 — see the test below.)
        await using var h = new Harness(
            processorOptions: new ForwardProcessorOptions(Mode: RewrapMode.ReanoncryptToNext),
            keyService: new ThrowingKeyService(new InvalidOperationException("resolver down for " + Bob)));
        var forward = Forward(Bob);

        var outcome = await h.Dispatch(AsDecryptedByMediator(forward));
        var flush = async () => await h.Handler.FlushAsync(Flush);

        await flush.Should().NotThrowAsync();
        outcome.Result.Should().Be(DispatchResult.NoReply);
        h.Sink.Count.Should().Be(0);
        var warning = h.Logger.Warnings.Should().ContainSingle().Which;
        warning.Formatted.Should().Contain("not relayed").And.Contain(forward.Id).And.Contain("InvalidOperationException").And.NotContain(Bob);
        h.Handler.Outstanding.Should().Be(0);
    }

    [Fact]
    public async Task Sink_exception_does_not_escape_and_produces_no_reply()
    {
        await using var h = new Harness();
        h.Sink.Throw = new InvalidOperationException("sink down");
        var forward = Forward(Bob);

        var outcome = await h.Dispatch(AsDecryptedByMediator(forward));
        var flush = async () => await h.Handler.FlushAsync(Flush);

        await flush.Should().NotThrowAsync();
        outcome.Result.Should().Be(DispatchResult.NoReply);
        outcome.Reply.Should().BeNull();
        h.Sink.Count.Should().Be(1);
        h.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("not relayed").And.Contain("InvalidOperationException").And.Contain(forward.Id);
        h.Handler.Outstanding.Should().Be(0);

        // The pump survives: the next forward is still relayed.
        h.Sink.Throw = null;
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Handler.FlushAsync(Flush);
        h.Sink.Count.Should().Be(2);
    }

    [Fact]
    public async Task Registry_fault_is_a_drop_not_a_throw()
    {
        await using var h = new Harness();
        h.Registry.Throw = new InvalidOperationException("registry down");
        var forward = Forward(Bob);

        var outcome = await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        h.Sink.Count.Should().Be(0);
        h.Handler.Outstanding.Should().Be(0);
        h.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("inline screening failed").And.Contain("InvalidOperationException").And.Contain(forward.Id);
    }

    [Fact]
    public async Task Logger_fault_does_not_change_the_outcome()
    {
        await using var h = new Harness(logger: new ThrowingLogger());

        // A dropped forward (logger throws on the warning) and a relayed one (logger throws on nothing).
        var dropped = await h.Dispatch(AsPlaintext(Forward(Bob)));
        var relayed = await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Handler.FlushAsync(Flush);

        dropped.Result.Should().Be(DispatchResult.NoReply);
        relayed.Result.Should().Be(DispatchResult.NoReply);
        h.Sink.Count.Should().Be(1);
    }

    [Fact]
    public async Task Please_ack_on_a_forward_yields_no_reply()
    {
        await using var h = new Harness();
        var forward = Forward(Bob);
        forward.AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["please_ack"] = JsonDocument.Parse("""["RECEIPT"]""").RootElement.Clone(),
        };

        var outcome = await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        outcome.Reply.Should().BeNull("FR-ROUTE-07: a mediator never acks a forward");
        h.Sink.Count.Should().Be(1, "the forward is still relayed");
        h.Threads.GetOrCreate(forward.Id).AckRequested.Should().BeFalse();
    }

    // ── Queue lifecycle ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Queue_saturation_drops_with_a_warning()
    {
        await using var h = new Harness(capacity: 1);
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Forward(Bob);
        var second = Forward(Bob);
        var third = Forward(Bob);
        await h.Dispatch(AsDecryptedByMediator(first));
        await h.Sink.Started.Task.WaitAsync(Flush);
        h.Handler.Outstanding.Should().Be(1, "the in-flight forward holds its slot");

        var outcome2 = await h.Dispatch(AsDecryptedByMediator(second));
        var outcome3 = await h.Dispatch(AsDecryptedByMediator(third));

        outcome2.Result.Should().Be(DispatchResult.NoReply);
        outcome3.Result.Should().Be(DispatchResult.NoReply);
        h.Handler.Outstanding.Should().Be(1);
        var saturated = h.Logger.Warnings.Where(e => e.Formatted.Contains("saturated")).ToList();
        saturated.Should().ContainSingle("the first drop logs; the second is rate-limited")
            .Which.Formatted.Should().Contain(second.Id).And.Contain("capacity 1");
        saturated.Single().State["Capacity"].Should().Be(1);
        saturated.Single().State["Dropped"].Should().Be(1L);

        h.Sink.Gate.TrySetResult();
        await h.Handler.FlushAsync(Flush);
        h.Sink.Count.Should().Be(1);
        h.Handler.Outstanding.Should().Be(0);

        // Once a slot frees, admission resumes.
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Handler.FlushAsync(Flush);
        h.Sink.Count.Should().Be(2);
    }

    [Fact]
    public async Task DisposeAsync_completes_within_grace_and_discards_queued_forwards()
    {
        var h = new Harness(capacity: 8, shutdownGrace: TimeSpan.FromSeconds(5));
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Sink.Started.Task.WaitAsync(Flush);
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        h.Handler.Outstanding.Should().Be(3);

        var dispose = h.Handler.DisposeAsync().AsTask();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Handler.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10));

        h.Handler.Outstanding.Should().Be(0);
        h.Sink.Count.Should().Be(1, "the two queued forwards were discarded, never delivered");
        h.Logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Formatted.Contains("discarded"))
            .Which.State["Count"].Should().Be(2);
        h.Sink.Tokens.Single().IsCancellationRequested.Should().BeTrue("the in-flight sink call was cancelled cooperatively");
        await h.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_is_idempotent_across_sync_and_async()
    {
        var h = new Harness();
        h.Handler.Dispose();

        var act = async () => { await h.Handler.DisposeAsync(); h.Handler.Dispose(); await h.Handler.DisposeAsync(); };

        await act.Should().NotThrowAsync();
        await h.Handler.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        await h.DisposeAsync();
    }

    [Fact]
    public async Task Dispatch_after_dispose_is_dropped_not_thrown()
    {
        var h = new Harness();
        await h.Handler.DisposeAsync();

        var outcome = await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));

        outcome.Result.Should().Be(DispatchResult.NoReply);
        h.Sink.Count.Should().Be(0);
        h.Handler.Outstanding.Should().Be(0);
        await h.DisposeAsync();
    }

    [Fact]
    public async Task FlushAsync_after_dispose_throws_ObjectDisposedException()
    {
        var h = new Harness();
        await h.Handler.DisposeAsync();

        var act = async () => await h.Handler.FlushAsync(TimeSpan.FromSeconds(1));

        await act.Should().ThrowAsync<ObjectDisposedException>();
        await h.DisposeAsync();
    }

    [Fact]
    public async Task FlushAsync_from_two_callers_concurrently_both_complete()
    {
        await using var h = new Harness();
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Sink.Started.Task.WaitAsync(Flush);

        var flushA = h.Handler.FlushAsync(Flush);
        var flushB = h.Handler.FlushAsync(Flush);
        flushA.IsCompleted.Should().BeFalse("the sink is still gated");
        flushB.IsCompleted.Should().BeFalse();

        h.Sink.Gate.TrySetResult();
        await Task.WhenAll(flushA, flushB).WaitAsync(Flush);

        h.Sink.Count.Should().Be(1);
    }

    [Fact]
    public async Task Fifty_parallel_forwards_yield_fifty_sink_calls()
    {
        await using var h = new Harness();
        var forwards = Enumerable.Range(0, 50).Select(_ => Forward(Bob)).ToArray();

        var outcomes = await Task.WhenAll(forwards.Select(f => Task.Run(() => h.Dispatch(AsDecryptedByMediator(f)))));
        await h.Handler.FlushAsync(Flush);

        outcomes.Should().AllSatisfy(o => o.Result.Should().Be(DispatchResult.NoReply));
        h.Sink.Count.Should().Be(50);
        h.Sink.Contexts.Select(c => c.Received.Message.Id).Should().BeEquivalentTo(forwards.Select(f => f.Id));
        h.Handler.Outstanding.Should().Be(0);
        h.Logger.Warnings.Should().BeEmpty();
    }

    // ── Logging posture ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Logs_carry_the_message_id_and_never_the_next_DID_or_the_recipient_kid()
    {
        await using var h = new Harness(capacity: 1);
        var ids = new List<string>();

        async Task Run(UnpackResult unpacked)
        {
            ids.Add(unpacked.Message.Id);
            await h.Dispatch(unpacked);
        }

        // Every inline drop reason.
        await Run(AsPlaintext(Forward(Bob)));
        await Run(AsSignedOnly(Forward(Bob)));
        await Run(AsEncryptedWithoutRecipientKid(Forward(Bob)));
        await Run(AsDecryptedByMediator(Forward(Bob, expires: 1)));
        await Run(AsDecryptedByMediator(Forward("did:example:carol")));
        await Run(AsDecryptedByMediator(Forward("not-a-did")));
        await Run(AsDecryptedByMediator(Forward(LengthCap.KidOfLength(Bob, LengthCap.KeyIdCap + 1))));
        // Pump drop reasons.
        await Run(AsDecryptedByMediator(ForwardMessage.Create(Mediator, Bob, new[] { Inner, Inner })));
        await h.Handler.FlushAsync(Flush);
        h.Sink.Throw = new InvalidOperationException("sink down");
        await Run(AsDecryptedByMediator(Forward(Bob)));
        await h.Handler.FlushAsync(Flush);
        h.Sink.Throw = null;
        // Saturation.
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(AsDecryptedByMediator(Forward(Bob)));
        await h.Sink.Started.Task.WaitAsync(Flush);
        await Run(AsDecryptedByMediator(Forward(Bob)));
        h.Sink.Gate.TrySetResult();
        await h.Handler.FlushAsync(Flush);

        h.Logger.Entries.Should().HaveCountGreaterThanOrEqualTo(10);
        foreach (var entry in h.Logger.Entries)
        {
            entry.State.Should().ContainKey("MessageId");
            ids.Should().Contain((string)entry.State["MessageId"]!);
            entry.Formatted.Should().Contain((string)entry.State["MessageId"]!);
        }
        AssertLogsCarryNoExceptionObjectAndNone(h.Logger, Bob, "carol", MediatorKid, SignerKid, LengthCap.Run, "ciphertext");
    }

    [Fact]
    public async Task Rewrap_resolution_failure_is_logged_by_exception_type_only_and_never_renders_the_recipient_DID()
    {
        // Rewrap mode over an empty key service: ProcessAsync throws DidResolutionException(nextDid, ...)
        // — an exception whose message carries the next-hop DID. A console provider prints
        // exception.ToString(), so the object must never reach the logger (review round 1, finding 2).
        // Since #95 ADV-3 D4 the forward ends Dropped-Unknown ("the next hop cannot be reached").
        const string PrivateRecipient = "did:example:private-recipient-marker";
        await using var h = new Harness(processorOptions: new ForwardProcessorOptions(Mode: RewrapMode.ReanoncryptToNext));
        h.Recipients.Register(PrivateRecipient);
        var forward = Forward(PrivateRecipient);

        var outcome = await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        h.Sink.Count.Should().Be(0);
        var warning = h.Logger.Warnings.Should().ContainSingle().Which;
        warning.Formatted.Should().Contain("the next hop cannot be reached").And.Contain(forward.Id).And.Contain("DidResolutionException");
        warning.State["ExceptionType"].Should().Be("DidResolutionException");
        AssertLogsCarryNoExceptionObjectAndNone(h.Logger, PrivateRecipient, "private-recipient");
    }

    [Fact]
    public async Task Sink_exception_whose_message_embeds_a_DID_is_logged_by_type_only()
    {
        const string Leak = "did:example:private-recipient-marker";
        await using var h = new Harness();
        h.Sink.Throw = new InvalidOperationException($"sink refused {Leak}", new TimeoutException($"inner {Leak}"));
        var forward = Forward(Bob);

        await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        var warning = h.Logger.Warnings.Should().ContainSingle().Which;
        warning.Formatted.Should().Contain("not relayed").And.Contain(forward.Id).And.Contain("InvalidOperationException").And.Contain("TimeoutException");
        warning.State["InnerExceptionType"].Should().Be("TimeoutException");
        AssertLogsCarryNoExceptionObjectAndNone(h.Logger, Leak, Bob);
    }

    [Fact]
    public async Task Registry_exception_whose_message_embeds_a_DID_is_logged_by_type_only_on_the_inline_path()
    {
        const string Leak = "did:example:private-recipient-marker";
        await using var h = new Harness();
        h.Registry.Throw = new InvalidOperationException($"registry refused {Leak}");
        var forward = Forward(Bob);

        var outcome = await h.Dispatch(AsDecryptedByMediator(forward));

        outcome.Result.Should().Be(DispatchResult.NoReply);
        h.Handler.Outstanding.Should().Be(0);
        var warning = h.Logger.Warnings.Should().ContainSingle().Which;
        warning.Formatted.Should().Contain("inline screening failed").And.Contain(forward.Id).And.Contain("InvalidOperationException");
        AssertLogsCarryNoExceptionObjectAndNone(h.Logger, Leak, Bob);
    }

    // ── Transport acknowledgement (review round 1, finding 1) ─────────────────────────────

    [Fact]
    public async Task Admitted_forward_is_not_processed_or_relayed_until_the_transport_acknowledges_it()
    {
        await using var h = new Harness();
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forward = Forward(Bob);

        var outcome = await h.Dispatch(AsDecryptedByMediator(forward), acknowledged.Task);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        h.Handler.Outstanding.Should().Be(1, "the slot is reserved inline, before the acknowledgement");
        h.Handler.OutstandingBytes.Should().BeGreaterThan(0);
        await Task.Delay(50);
        h.Provider.Calls.Should().Be(0, "the processor must not run before the 202 is committed");
        h.Sink.Count.Should().Be(0, "the sink must not run before the 202 is committed");

        acknowledged.SetResult();
        await h.Handler.FlushAsync(Flush);

        h.Provider.Calls.Should().Be(1);
        h.Sink.Count.Should().Be(1);
        h.Sink.Contexts.Single().Received.Message.Id.Should().Be(forward.Id);
        h.Handler.Outstanding.Should().Be(0);
        h.Handler.OutstandingBytes.Should().Be(0);
        h.Logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelled_acknowledgement_releases_the_reservation_and_drops_the_forward_with_the_id_only()
    {
        await using var h = new Harness();
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forward = Forward(Bob);
        await h.Dispatch(AsDecryptedByMediator(forward), acknowledged.Task);
        h.Handler.Outstanding.Should().Be(1);

        acknowledged.SetCanceled();
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(0);
        h.Provider.Calls.Should().Be(0);
        h.Handler.Outstanding.Should().Be(0, "the slot is released");
        h.Handler.OutstandingBytes.Should().Be(0, "the bytes are released");
        var warning = h.Logger.Warnings.Should().ContainSingle().Which;
        warning.Formatted.Should().Contain("did not acknowledge").And.Contain(forward.Id);
        warning.State.Keys.Should().BeEquivalentTo("MessageId", "{OriginalFormat}");
        AssertLogsCarryNoExceptionObjectAndNone(h.Logger, Bob, MediatorKid);

        // Admission resumes: the next forward (acknowledged at once) is relayed.
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Handler.FlushAsync(Flush);
        h.Sink.Count.Should().Be(1);
    }

    [Fact]
    public async Task Faulted_acknowledgement_drops_the_forward_without_rendering_the_fault()
    {
        const string Leak = "did:example:private-recipient-marker";
        await using var h = new Harness();
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forward = Forward(Bob);
        await h.Dispatch(AsDecryptedByMediator(forward), acknowledged.Task);

        acknowledged.SetException(new InvalidOperationException($"response failed for {Leak}"));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(0);
        h.Handler.Outstanding.Should().Be(0);
        h.Handler.OutstandingBytes.Should().Be(0);
        h.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("did not acknowledge").And.Contain(forward.Id);
        AssertLogsCarryNoExceptionObjectAndNone(h.Logger, Leak, Bob);
    }

    [Fact]
    public async Task Already_cancelled_acknowledgement_is_dropped_inline_without_holding_a_slot()
    {
        await using var h = new Harness();
        var forward = Forward(Bob);

        await h.Dispatch(AsDecryptedByMediator(forward), Task.FromCanceled(new CancellationToken(canceled: true)));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(0);
        h.Handler.Outstanding.Should().Be(0);
        h.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("did not acknowledge").And.Contain(forward.Id);
    }

    [Fact]
    public async Task FlushAsync_waits_for_a_pending_acknowledgement()
    {
        await using var h = new Harness();
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)), acknowledged.Task);

        var flush = h.Handler.FlushAsync(Flush);
        await Task.Delay(100);
        flush.IsCompleted.Should().BeFalse("a forward admitted before the call has neither reached the sink nor been dropped");
        h.Sink.Count.Should().Be(0);

        acknowledged.SetResult();
        await flush;

        h.Sink.Count.Should().Be(1, "the flush completes only after the acknowledged forward reached the sink");
        h.Handler.Outstanding.Should().Be(0);
    }

    [Fact]
    public async Task FlushAsync_times_out_while_an_acknowledgement_is_still_pending()
    {
        await using var h = new Harness();
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)), acknowledged.Task);

        var act = async () => await h.Handler.FlushAsync(TimeSpan.FromMilliseconds(200));

        await act.Should().ThrowAsync<OperationCanceledException>();
        h.Handler.Outstanding.Should().Be(1, "a timed-out flush changes nothing");
        acknowledged.SetResult();
        await h.Handler.FlushAsync(Flush);
        h.Sink.Count.Should().Be(1);
    }

    [Fact]
    public async Task Dispose_with_a_pending_acknowledgement_discards_it_and_never_reaches_the_sink()
    {
        var h = new Harness(capacity: 8, shutdownGrace: TimeSpan.FromSeconds(5));
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)), acknowledged.Task);
        h.Handler.Outstanding.Should().Be(1);

        await h.Handler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await h.Handler.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10));

        h.Handler.Outstanding.Should().Be(0, "the reservation is released at shutdown");
        h.Handler.OutstandingBytes.Should().Be(0);
        h.Logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Formatted.Contains("discarded"))
            .Which.State["Count"].Should().Be(1);

        // The transport acknowledges later: nothing is left to publish.
        acknowledged.SetResult();
        await Task.Delay(50);
        h.Sink.Count.Should().Be(0);
        h.Provider.Calls.Should().Be(0);
        h.Logger.Warnings.Should().BeEmpty("a shutdown discard is not a transport failure");
        await h.DisposeAsync();
    }

    [Fact]
    public async Task Saturation_is_decided_inline_while_acknowledgements_are_pending()
    {
        await using var h = new Harness(capacity: 1);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = Forward(Bob);
        var refused = Forward(Bob);

        await h.Dispatch(AsDecryptedByMediator(admitted), first.Task);
        var outcome = await h.Dispatch(AsDecryptedByMediator(refused), second.Task);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        h.Handler.Outstanding.Should().Be(1, "the reserved-but-unpublished forward holds the only slot");
        h.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("saturated").And.Contain(refused.Id);

        first.SetResult();
        second.SetResult();
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(1);
        h.Sink.Contexts.Single().Received.Message.Id.Should().Be(admitted.Id);
        h.Handler.Outstanding.Should().Be(0);
    }

    [Fact]
    public async Task Acknowledged_forwards_are_published_in_acknowledgement_order_and_each_exactly_once()
    {
        await using var h = new Harness();
        var acks = Enumerable.Range(0, 5).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var forwards = Enumerable.Range(0, 5).Select(_ => Forward(Bob)).ToArray();
        for (var i = 0; i < 5; i++)
            await h.Dispatch(AsDecryptedByMediator(forwards[i]), acks[i].Task);
        h.Handler.Outstanding.Should().Be(5);

        // Acknowledge in reverse: the pump sees them as they are acknowledged, not as admitted.
        for (var i = 4; i >= 0; i--)
        {
            acks[i].SetResult();
            await h.WaitUntilAsync(() => h.Sink.Count == 5 - i, "each acknowledgement publishes exactly one forward");
        }
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(5);
        h.Sink.Contexts.Select(c => c.Received.Message.Id).Should().Equal(forwards.Reverse().Select(f => f.Id));
        h.Handler.Outstanding.Should().Be(0);
    }

    // ── Schedule fixed at receipt (review round 1, finding 3) ─────────────────────────────

    [Fact]
    public async Task NotBefore_is_fixed_at_admission_not_at_dequeue()
    {
        await using var h = new Harness();
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Sink.Started.Task.WaitAsync(Flush);

        // Admitted at t0 with a 500 ms hint; the pump reaches it 10 s later.
        var delayed = Forward(Bob);
        delayed.AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["delay_milli"] = JsonDocument.Parse("500").RootElement.Clone(),
        };
        await h.Dispatch(AsDecryptedByMediator(delayed));
        h.Now = Fixed.AddSeconds(10);
        h.Sink.Gate.TrySetResult();
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(2);
        var result = h.Sink.Results.Last();
        result.Delay.Should().Be(TimeSpan.FromMilliseconds(500));
        result.NotBefore.Should().Be(Fixed.AddMilliseconds(500), "receipt time plus the hint, not dequeue time plus the hint");
    }

    [Fact]
    public async Task Negative_delay_milli_is_sampled_once_at_admission_and_does_not_move_while_queued()
    {
        await using var h = new Harness();
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Sink.Started.Task.WaitAsync(Flush);

        var delayed = Forward(Bob);
        delayed.AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["delay_milli"] = JsonDocument.Parse("-500").RootElement.Clone(),
        };
        await h.Dispatch(AsDecryptedByMediator(delayed));
        h.Now = Fixed.AddSeconds(10);
        h.Sink.Gate.TrySetResult();
        await h.Handler.FlushAsync(Flush);

        var result = h.Sink.Results.Last();
        result.Delay.Should().NotBeNull();
        result.Delay!.Value.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(500));
        result.NotBefore.Should().Be(Fixed + result.Delay.Value, "one sample at admission, on the receipt clock");
        result.NotBefore.Should().BeOnOrAfter(Fixed).And.BeOnOrBefore(Fixed.AddMilliseconds(500));
        h.Options.Now().Should().Be(Fixed.AddSeconds(10), "the clock has moved on; the schedule has not");
    }

    [Fact]
    public async Task Expiry_is_still_re_checked_at_dequeue_although_the_schedule_is_fixed_at_admission()
    {
        await using var h = new Harness();
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Sink.Started.Task.WaitAsync(Flush);

        // Admitted at t0 (not yet expired), expires at t0 + 5 s, dequeued at t0 + 10 s.
        var expiring = Forward(Bob, expires: Fixed.AddSeconds(5).ToUnixTimeSeconds());
        expiring.AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["delay_milli"] = JsonDocument.Parse("500").RootElement.Clone(),
        };
        await h.Dispatch(AsDecryptedByMediator(expiring));
        h.Handler.Outstanding.Should().Be(2);
        h.Now = Fixed.AddSeconds(10);
        h.Sink.Gate.TrySetResult();
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(1, "the pump re-check drops the forward that expired while queued (FR-ROUTE-09)");
        h.Logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Formatted.Contains("expired while queued"))
            .Which.Formatted.Should().Contain(expiring.Id);
        h.Handler.Outstanding.Should().Be(0);
    }

    // ── Direct HandleAsync contract ───────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_throws_only_OperationCanceledException_for_a_cancelled_caller_token()
    {
        await using var h = new Harness();
        var forward = Forward(Bob);
        var context = h.Context(AsDecryptedByMediator(forward));

        var act = async () => await h.Handler.HandleAsync(forward, context, new CancellationToken(canceled: true));

        await act.Should().ThrowExactlyAsync<OperationCanceledException>();
        h.Handler.Outstanding.Should().Be(0);
        h.Sink.Count.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_propagates_cancellation_raised_inside_the_registry()
    {
        await using var h = new Harness();
        using var cts = new CancellationTokenSource();
        h.Registry.CancelOnLookup = cts;
        var forward = Forward(Bob);

        var act = async () => await h.Handler.HandleAsync(forward, h.Context(AsDecryptedByMediator(forward)), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        h.Handler.Outstanding.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_rejects_null_arguments()
    {
        await using var h = new Harness();
        var forward = Forward(Bob);

        var nullMessage = async () => await h.Handler.HandleAsync(null!, h.Context(AsDecryptedByMediator(forward)), CancellationToken.None);
        var nullContext = async () => await h.Handler.HandleAsync(forward, null!, CancellationToken.None);

        await nullMessage.Should().ThrowAsync<ArgumentNullException>();
        await nullContext.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task HandleAsync_drops_a_non_forward_message_without_throwing()
    {
        // Unreachable through the registry; the guard still holds.
        await using var h = new Harness();
        var ping = new MessageBuilder().WithType("https://didcomm.org/trust-ping/2.0/ping").WithBody(new JsonObject()).Build();

        var reply = await h.Handler.HandleAsync(ping, h.Context(AsDecryptedByMediator(ping)), CancellationToken.None);

        reply.Should().BeNull();
        h.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("not a forward").And.NotContain("trust-ping");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Ctor_rejects_an_invalid_capacity(int capacity)
    {
        var act = () => new ForwardHandler(NewProcessor(), new CapturingSink(), new InMemoryForwardRecipientRegistry(), null, capacity, null);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("capacity");
    }

    [Fact]
    public void Ctor_rejects_a_non_positive_grace()
    {
        var act = () => new ForwardHandler(NewProcessor(), new CapturingSink(), new InMemoryForwardRecipientRegistry(), null, 4, TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("shutdownGrace");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The logging contract (review round 1, finding 2): no entry ever carries an exception object
    /// — so nothing a console provider renders, template text or <c>exception.ToString()</c>, and
    /// no state value, contains any of <paramref name="forbidden"/> (DIDs, kids, hostile markers).
    /// </summary>
    internal static void AssertLogsCarryNoExceptionObjectAndNone(CapturingLogger logger, params string[] forbidden)
    {
        foreach (var entry in logger.Entries)
        {
            entry.Exception.Should().BeNull("the handler logs exception type names only, never the exception object (its message may carry a DID)");
            foreach (var value in forbidden)
            {
                entry.Rendered.Should().NotContain(value);
                foreach (var stateValue in entry.State.Values)
                    (stateValue?.ToString() ?? string.Empty).Should().NotContain(value);
            }
        }
    }

    internal static Message Forward(string next, long? expires = null)
        => ForwardMessage.Create(Mediator, next, new[] { Inner }, expiresTimeEpochSeconds: expires);

    internal sealed class Harness : IAsyncDisposable
    {
        private readonly ProtocolDispatcher _dispatcher;

        public DateTimeOffset Now { get; set; } = Fixed;
        public DidCommOptions Options { get; }
        public CapturingSink Sink { get; } = new();
        public CapturingLogger Logger { get; } = new();
        public CountingRegistry Registry { get; }
        /// <summary>
        /// Consulted by <see cref="ForwardProcessor"/> in pass-through mode: its call count is the
        /// proof that the processor ran for a forward (never in rewrap mode).
        /// </summary>
        public CountingRoutingKeyProvider Provider { get; } = new();
        /// <summary>The directory behind <see cref="Registry"/>, holding <c>did:example:bob</c>.</summary>
        public InMemoryForwardRecipientRegistry Recipients { get; }
        public ForwardHandler Handler { get; }
        public InMemoryThreadStateStore Threads { get; } = new();

        public Harness(
            Action<DidCommOptions>? configure = null,
            ForwardProcessorOptions? processorOptions = null,
            ILogger<ForwardHandler>? logger = null,
            int capacity = ForwardHandler.DefaultCapacity,
            TimeSpan? shutdownGrace = null,
            long byteBudget = ForwardHandler.DefaultByteBudget,
            ForwardMetrics? metrics = null,
            IForwardSink? sink = null,
            DidComm.Resolution.IDidKeyService? keyService = null)
        {
            Options = new DidCommOptions { Clock = () => Now };
            configure?.Invoke(Options);
            Recipients = new InMemoryForwardRecipientRegistry();
            Recipients.Register(Bob);
            Registry = new CountingRegistry(Recipients);
            var resolvedProcessorOptions = processorOptions ?? new ForwardProcessorOptions();
            Handler = new ForwardHandler(
                NewProcessor(resolvedProcessorOptions, routingKeys: Provider, didCommOptions: Options, keyService: keyService),
                sink ?? Sink, Registry, logger ?? Logger, capacity, shutdownGrace, byteBudget, metrics);
            var registry = new ProtocolHandlerRegistry();
            registry.Register(Handler);
            _dispatcher = new ProtocolDispatcher(registry, Threads);
        }

        public Task<DispatchOutcome> Dispatch(UnpackResult unpacked, CancellationToken ct = default)
            => _dispatcher.DispatchAsync(unpacked, client: null, Options, ct);

        /// <summary>Dispatch with a transport acknowledgement the test controls (the HTTP endpoint's shape).</summary>
        public Task<DispatchOutcome> Dispatch(UnpackResult unpacked, Task acknowledged, CancellationToken ct = default)
            => _dispatcher.DispatchAsync(unpacked, client: null, Options, acknowledged, ct);

        /// <summary>Poll a handler seam until it holds, or fail after <see cref="Flush"/>.</summary>
        public async Task WaitUntilAsync(Func<bool> condition, string because)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.Elapsed > Flush)
                    throw new TimeoutException($"Condition not met within {Flush}: {because}");
                await Task.Delay(5);
            }
        }

        public ProtocolContext Context(UnpackResult unpacked)
            => new(unpacked, new ThreadState(unpacked.Message.Thid ?? unpacked.Message.Id), Client: null, Options, Threads);

        public async ValueTask DisposeAsync()
        {
            Sink.Gate?.TrySetResult();
            await Handler.DisposeAsync();
            await _dispatcher.DisposeAsync();
        }
    }

    internal sealed class CapturingSink : IForwardSink
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public ConcurrentQueue<ForwardProcessingResult> Results { get; } = new();
        public ConcurrentQueue<ProtocolContext> Contexts { get; } = new();
        public ConcurrentQueue<CancellationToken> Tokens { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>When set, every call waits on it (cooperatively) before returning.</summary>
        public TaskCompletionSource? Gate { get; set; }
        public Exception? Throw { get; set; }

        public async Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _count);
            Results.Enqueue(result);
            Contexts.Enqueue(context);
            Tokens.Enqueue(ct);
            Started.TrySetResult();
            if (Gate is { } gate)
                await gate.Task.WaitAsync(ct);
            if (Throw is { } ex)
                throw ex;
        }
    }

    internal sealed class CountingRoutingKeyProvider : IRecipientRoutingKeyProvider
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<IReadOnlyList<Jwk>> GetAsync(string recipientDid, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult<IReadOnlyList<Jwk>>(Array.Empty<Jwk>());
        }
    }

    internal sealed class CountingRegistry : IForwardRecipientRegistry
    {
        private readonly IForwardRecipientRegistry _inner;
        private int _calls;
        public CountingRegistry(IForwardRecipientRegistry inner) => _inner = inner;
        public int Calls => Volatile.Read(ref _calls);
        public ConcurrentQueue<string> Requested { get; } = new();
        public Exception? Throw { get; set; }
        public CancellationTokenSource? CancelOnLookup { get; set; }

        public ValueTask<bool> IsRegisteredAsync(string recipientDid, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            Requested.Enqueue(recipientDid);
            if (Throw is { } ex)
                throw ex;
            if (CancelOnLookup is { } cts)
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            return _inner.IsRegisteredAsync(recipientDid, ct);
        }
    }

    /// <summary>
    /// One captured line. <see cref="Formatted"/> is what the template renders; <see cref="Rendered"/>
    /// is what a console provider would print — the formatted text followed by
    /// <c>exception.ToString()</c> when an exception object was handed to the logger.
    /// </summary>
    internal sealed record LogEntry(LogLevel Level, IReadOnlyDictionary<string, object?> State, string Formatted, Exception? Exception)
    {
        public string Rendered => Exception is null ? Formatted : Formatted + Environment.NewLine + Exception;
    }

    internal sealed class CapturingLogger : ILogger<ForwardHandler>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public IEnumerable<LogEntry> Warnings => Entries.Where(e => e.Level == LogLevel.Warning);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            Entries.Enqueue(new LogEntry(logLevel, values, formatter(state, exception), exception));
        }
    }

    private sealed class ThrowingLogger : ILogger<ForwardHandler>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("logging provider down");
    }
}
