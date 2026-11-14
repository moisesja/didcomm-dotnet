using System.Text.Json;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// QA-1 (#95): the real <see cref="ForwardHandler"/> and the mediator's sink together, as
/// <c>AddMediator()</c> wires them. Every forward ends with exactly one outcome on
/// <c>didcomm.forward.outcomes</c>, whichever stage ended it; each collaborator is made to throw on
/// every path that reads it (L-054); no line carries a DID, a queue id or an exception message.
/// </summary>
public sealed class ForwardPipelineQaTests
{
    private const string Peer = "did:example:qa-e2e-peer";
    private const string Recipient = "did:example:qa-e2e-recipient";
    private const string Inner = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    // ── one outcome per forward, every stage ─────────────────────────────────────────────

    [Fact]
    public async Task A_registered_recipient_is_queued_and_counted_once()
    {
        await using var fixture = PushFixture.Create(configure: o => o.DirectPush = false);
        await fixture.EnrolAsync(Peer, Recipient);

        await RunAsync(fixture, Forward(Recipient, "e2e-1"));

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued);
        fixture.Store.MessageCount.Should().Be(1);
        NoLeaks(fixture);
    }

    [Fact]
    public async Task A_DID_URL_next_is_queued_under_its_DID_and_counted_once()
    {
        await using var fixture = PushFixture.Create(configure: o => o.DirectPush = false);
        await fixture.EnrolAsync(Peer, Recipient);

        await RunAsync(fixture, Forward(Recipient + "#key-x25519-1", "e2e-url"));

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued);
        (await fixture.Store.GetStatusAsync(Peer, Recipient, fixture.Time.GetUtcNow(), CancellationToken.None)).MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task An_unregistered_recipient_is_dropped_by_the_handler_and_counted_once()
    {
        await using var fixture = PushFixture.Create();

        await RunAsync(fixture, Forward(Recipient, "e2e-unknown"));

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.DroppedUnknown);
        fixture.Store.MessageCount.Should().Be(0);
        NoLeaks(fixture);
    }

    [Fact]
    public async Task A_grant_revoked_between_the_inline_check_and_the_sink_is_dropped_by_the_sink_and_counted_once()
    {
        await using var fixture = PushFixture.Create(directory: _ => new Uri("https://qa-e2e-host.example/in"));
        await fixture.EnrolAsync(Peer, Recipient);
        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = RunAsync(fixture, Forward(Recipient, "e2e-revoked"), ack.Task);
        await WaitUntil(() => Handler(fixture).Outstanding == 1);
        (await fixture.Queue.RevokeAndPurgeAsync(Peer)).Revoked.Should().BeTrue();
        ack.SetResult();
        await running;

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.DroppedUnknown);
        fixture.Store.MessageCount.Should().Be(0);
        fixture.PushOutcomeLog.Should().BeEmpty("a dropped forward is never handed to direct push");
    }

    [Fact]
    public async Task An_acknowledgement_that_never_comes_is_counted_once_as_unacknowledged()
    {
        await using var fixture = PushFixture.Create();
        await fixture.EnrolAsync(Peer, Recipient);

        await RunAsync(fixture, Forward(Recipient, "e2e-abort"), Task.FromCanceled(new CancellationToken(true)));

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.DroppedUnacknowledged);
        fixture.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_forward_already_expired_is_dropped_by_the_handler_and_counted_once()
    {
        await using var fixture = PushFixture.Create();
        await fixture.EnrolAsync(Peer, Recipient);

        await RunAsync(fixture, Forward(Recipient, "e2e-expired", expiresTime: fixture.Time.GetUtcNow().ToUnixTimeSeconds() - 1));

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.DroppedExpired);
        fixture.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_forward_that_expires_between_the_pump_check_and_the_queue_is_dropped_by_the_sink_and_counted_once()
    {
        // Clock reads: (1) the handler inline, (2) the pump's second check, (3) the queue. The third
        // read is an hour later, so only the queue finds it expired.
        var start = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        var reads = 0;
        await using var fixture = PushFixture.Create(didComm: o => o.Clock = () => Interlocked.Increment(ref reads) <= 2 ? start : start.AddHours(1));
        await fixture.EnrolAsync(Peer, Recipient);

        await RunAsync(fixture, Forward(Recipient, "e2e-late-expiry", expiresTime: start.ToUnixTimeSeconds() + 60));

        reads.Should().Be(3);
        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.DroppedExpired);
        fixture.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_forward_larger_than_the_queue_accepts_is_counted_once_as_too_large()
    {
        await using var fixture = PushFixture.Create(configure: o => o.Pickup.MaxDeliveryBytes = 64 * 1024);
        await fixture.EnrolAsync(Peer, Recipient);
        var big = $$"""{"protected":"{{new string('p', fixture.Queue.MaxAdmissiblePayloadBytes)}}","ciphertext":"x","iv":"","tag":"","recipients":[]}""";

        await RunAsync(fixture, ForwardMessageWith(Recipient, "e2e-big", big));

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.DroppedTooLarge);
        fixture.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task Forwards_past_the_quota_are_each_counted_once_as_quota()
    {
        await using var fixture = PushFixture.Create(configure: o =>
        {
            o.DirectPush = false;
            o.Pickup.MaxQueueMessages = 2;
        });
        await fixture.EnrolAsync(Peer, Recipient);

        for (var i = 0; i < 5; i++)
            await RunAsync(fixture, Forward(Recipient, $"e2e-q{i}"));

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.Queued, ForwardOutcomes.DroppedQuota, ForwardOutcomes.DroppedQuota, ForwardOutcomes.DroppedQuota);
        fixture.Store.MessageCount.Should().Be(2);
    }

    // ── each collaborator throws, on every path that reads it (L-054) ─────────────────────

    [Theory]
    [InlineData(1, ForwardOutcomes.Faulted, 0)]   // the handler's inline registration check
    [InlineData(2, ForwardOutcomes.Faulted, 0)]   // the queue's lookup
    [InlineData(3, ForwardOutcomes.Queued, 1)]    // the queue's re-check after storing: kept, reported Queued
    public async Task A_mediation_store_that_throws_on_one_lookup_ends_the_forward_with_one_outcome_and_no_leak(int failingCall, string expected, int retained)
    {
        var mediation = new HookedMediationStore();
        await using var fixture = PushFixture.Create(configure: o => o.DirectPush = false, services: s => s.AddSingleton<IMediationStore>(mediation));
        await fixture.EnrolAsync(Peer, Recipient);
        mediation.OnFind = (call, _, _) => call == failingCall
            ? throw new HandlerBoomException(HandlerBoomException.Marker + " " + Recipient + " " + Peer)
            : null;

        await RunAsync(fixture, Forward(Recipient, "e2e-boom"));

        mediation.FindCalls.Should().Be(failingCall == 1 ? 1 : failingCall == 2 ? 2 : 3);
        fixture.ForwardOutcomeLog.Should().Equal(expected);
        fixture.Store.MessageCount.Should().Be(retained);
        NoLeaks(fixture);
    }

    [Fact]
    public async Task A_message_store_that_throws_on_enqueue_is_one_Faulted_and_no_leak()
    {
        var store = new HookedMessageStore { OnEnqueue = _ => throw new HandlerBoomException(HandlerBoomException.Marker + " " + Recipient) };
        await using var fixture = PushFixture.Create(services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);

        await RunAsync(fixture, Forward(Recipient, "e2e-store-boom"));

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Faulted);
        store.Inner.MessageCount.Should().Be(0);
        NoLeaks(fixture);
        PipelineLogs.Of<MediatorForwardSink>(fixture.Logs).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Error);
    }

    [Fact]
    public async Task A_message_store_that_answers_DuplicateId_every_time_is_one_Faulted_after_two_attempts()
    {
        var store = new HookedMessageStore { OnEnqueue = _ => EnqueueOutcome.DuplicateId };
        await using var fixture = PushFixture.Create(services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);

        await RunAsync(fixture, Forward(Recipient, "e2e-dup"));

        store.EnqueueIds.Should().HaveCount(2).And.OnlyHaveUniqueItems("each attempt mints a fresh id");
        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Faulted);
        NoLeaks(fixture, store.EnqueueIds.ToArray());
    }

    [Fact]
    public async Task A_message_store_that_answers_DuplicateId_once_stores_under_the_second_id()
    {
        var first = true;
        var store = new HookedMessageStore { OnEnqueue = _ => { var dup = first; first = false; return dup ? EnqueueOutcome.DuplicateId : null; } };
        await using var fixture = PushFixture.Create(configure: o => o.DirectPush = false, services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);

        await RunAsync(fixture, Forward(Recipient, "e2e-dup-once"));

        store.EnqueueIds.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued);
        store.Inner.MessageCount.Should().Be(1);
        var now = fixture.Time.GetUtcNow();
        (await store.Inner.LeaseAsync(new MessageLeaseRequest(Peer, null, 10, int.MaxValue, now, new MessageLease(null, now.AddMinutes(1))), CancellationToken.None))
            .Should().ContainSingle().Which.Id.Should().Be(store.EnqueueIds.Last());
    }

    // ── cancellation after the message is stored ──────────────────────────────────────────

    // QA-1 DEFECT 4 (P3, fixed by #95 Q-6): the handler shuts down while the queue is re-checking the
    // registration of a message it has already stored. DeliverAsync kept the record but left with
    // OperationCanceledException; the sink re-threw it and the handler counted the forward as
    // Dropped-Shutdown — while the message was in the queue and would be delivered — and the
    // message was never handed to direct push. Now the stored message is reported Queued, counted
    // Queued, and handed to direct push like any queued message; this fixture's push endpoint
    // accepts it, so the push delivers and removes it (whether that has happened by the time the
    // shutdown returns is a race, so the test waits for the push workers before it looks).
    [Fact]
    public async Task A_shutdown_after_the_message_is_stored_counts_what_happened_to_it()
    {
        var (fixture, _) = await ParkedOnRecheckAsync();
        await using var __ = fixture;
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Pushed);
        fixture.Store.MessageCount.Should().Be(0, "the direct push it was handed to delivered it");
    }

    // ── an attachment that decodes to nothing (DEFECT 5, mediator side) ───────────────────

    [Fact]
    public async Task A_forward_whose_attachment_decodes_to_nothing_is_dropped_as_malformed()
    {
        await using var fixture = PushFixture.Create();
        await fixture.EnrolAsync(Peer, Recipient);

        await RunAsync(fixture, WithBase64Attachment(Forward(Recipient, "e2e-empty"), " "));

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.DroppedMalformed);
        PipelineLogs.Of<MediatorForwardSink>(fixture.Logs).Should().NotContain(e => e.Level == LogLevel.Error);
    }

    // ── the public string overload, over a real anoncrypt envelope ────────────────────────

    [Theory]
    [InlineData(500L, 500L)]
    [InlineData(long.MaxValue, 3_600_000L)]
    [InlineData(0L, 0L)]
    public async Task The_packed_overload_of_the_mediators_processor_applies_the_one_hour_cap(long hint, long expectedMs)
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        await using var fixture = PushFixture.Create(secrets: peers.Secrets, mediatorDid: mediator.Did, configure: o => o.DirectPush = false);
        var client = fixture.Services.GetRequiredService<DidCommClient>();
        var forward = ForwardMessage.Create(mediator.Did, Recipient, new[] { Inner });
        forward.AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["delay_milli"] = JsonDocument.Parse(hint.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone(),
        };
        var packed = await client.PackEncryptedAsync(forward, new PackEncryptedOptions(Recipients: new[] { mediator.Did }));
        var now = fixture.Time.GetUtcNow();

        var result = await fixture.Services.GetRequiredService<ForwardProcessor>().ProcessAsync(packed.Message);

        result.Delay.Should().Be(TimeSpan.FromMilliseconds(expectedMs));
        result.NotBefore.Should().Be(now + TimeSpan.FromMilliseconds(expectedMs));
    }

    [Fact]
    public async Task The_packed_overload_draws_a_negative_hint_under_the_cap()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        await using var fixture = PushFixture.Create(secrets: peers.Secrets, mediatorDid: mediator.Did, configure: o =>
        {
            o.DirectPush = false;
            o.MaxForwardDelay = TimeSpan.FromMilliseconds(3);
        });
        var client = fixture.Services.GetRequiredService<DidCommClient>();
        var forward = ForwardMessage.Create(mediator.Did, Recipient, new[] { Inner });
        forward.AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["delay_milli"] = JsonDocument.Parse("-9223372036854775808").RootElement.Clone(),
        };
        var packed = await client.PackEncryptedAsync(forward, new PackEncryptedOptions(Recipients: new[] { mediator.Did }));

        var result = await fixture.Services.GetRequiredService<ForwardProcessor>().ProcessAsync(packed.Message);

        result.Delay!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(3));
        result.NotBefore.Should().Be(fixture.Time.GetUtcNow() + result.Delay.Value);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    /// <summary>A forward stored, then parked in the queue's post-enqueue re-check while the handler shuts down.</summary>
    private static async Task<(PushFixture Fixture, HookedMediationStore Mediation)> ParkedOnRecheckAsync()
    {
        var mediation = new HookedMediationStore();
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = PushFixture.Create(directory: _ => new Uri("https://qa-e2e-host.example/in"), services: s => s.AddSingleton<IMediationStore>(mediation));
        await fixture.EnrolAsync(Peer, Recipient);
        mediation.OnFindAsync = async (call, _, ct) =>
        {
            if (call != 3)
                return false;
            parked.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        };
        var handler = Handler(fixture);
        await handler.HandleAsync(Forward(Recipient, "e2e-parked"), Context(fixture, Forward(Recipient, "e2e-parked"), Task.CompletedTask), CancellationToken.None);
        await parked.Task.WaitAsync(Wait);

        await handler.DisposeAsync();
        await handler.ShutdownCompletion.WaitAsync(Wait);
        return (fixture, mediation);
    }

    private static ForwardHandler Handler(PushFixture fixture) => fixture.Services.GetRequiredService<ForwardHandler>();

    private static async Task RunAsync(PushFixture fixture, Message forward, Task? acknowledged = null)
    {
        var handler = Handler(fixture);
        (await handler.HandleAsync(forward, Context(fixture, forward, acknowledged ?? Task.CompletedTask), CancellationToken.None)).Should().BeNull();
        await handler.FlushAsync(Wait);
    }

    private static ProtocolContext Context(PushFixture fixture, Message forward, Task acknowledged)
        => new(
            new UnpackResult(
                forward,
                new[] { EnvelopeKind.Encrypted, EnvelopeKind.Plaintext },
                Encrypted: true,
                Authenticated: false,
                NonRepudiation: false,
                AnonymousSender: true,
                ContentEncryption: null,
                KeyWrap: null,
                SignatureAlgorithm: null,
                SignerKid: null,
                SenderKid: null,
                RecipientKid: PushFixture.MediatorDid + "#key-1",
                AllRecipientKids: new[] { PushFixture.MediatorDid + "#key-1" },
                FromPrior: null),
            new DidComm.Threading.ThreadState(forward.Id),
            Client: null,
            fixture.Options,
            new InMemoryThreadStateStore())
        { Acknowledged = acknowledged };

    private static Message Forward(string next, string id, long? expiresTime = null)
        => ForwardMessageWith(next, id, Inner, expiresTime);

    private static Message ForwardMessageWith(string next, string id, string inner, long? expiresTime = null)
    {
        var message = ForwardMessage.Create(PushFixture.MediatorDid, next, new[] { inner });
        message.Id = id;
        message.ExpiresTime = expiresTime;
        return message;
    }

    private static Message WithBase64Attachment(Message forward, string base64)
    {
        forward.Attachments = new List<Attachment> { new() { Id = "a1", Data = new AttachmentData { Base64 = base64 } } };
        return forward;
    }

    private static void NoLeaks(PushFixture fixture, params string[] more)
        => PipelineLogs.Leaking(fixture.Logs, new[] { Peer, Recipient, PushFixture.MediatorDid, HandlerBoomException.Marker }.Concat(more).ToArray())
            .Should().BeEmpty("no line may carry a DID, a queue id or an exception message");

    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Wait)
                throw new TimeoutException("condition not met");
            await Task.Yield();
        }
    }
}

/// <summary>The in-memory mediation store, with a hook on each recipient lookup (numbered from 1).</summary>
internal sealed class HookedMediationStore : IMediationStore
{
    private readonly InMemoryMediationStore _inner = new(new CoordinateMediationOptions());
    private int _findCalls;

    public int FindCalls => Volatile.Read(ref _findCalls);

    /// <summary>Synchronous hook: return a grant to answer with it, <c>null</c> to fall through, or throw.</summary>
    public Func<int, string, CancellationToken, MediationGrant?>? OnFind { get; set; }

    /// <summary>Asynchronous hook: return <c>true</c> when it answered (the store is then not asked), <c>false</c> to fall through.</summary>
    public Func<int, string, CancellationToken, Task<bool>>? OnFindAsync { get; set; }

    public ValueTask<MediationGrant?> GetGrantAsync(string peerDid, CancellationToken ct) => _inner.GetGrantAsync(peerDid, ct);
    public ValueTask<MediationGrant?> GrantAsync(MediationGrant grant, CancellationToken ct) => _inner.GrantAsync(grant, ct);
    public ValueTask<bool> RevokeAsync(string peerDid, CancellationToken ct) => _inner.RevokeAsync(peerDid, ct);
    public ValueTask<IReadOnlyList<RecipientStoreOutcome>> UpdateRecipientsAsync(string peerDid, IReadOnlyList<RecipientUpdate> updates, CancellationToken ct) => _inner.UpdateRecipientsAsync(peerDid, updates, ct);
    public ValueTask<RecipientPage> ListRecipientsAsync(string peerDid, int offset, int limit, CancellationToken ct) => _inner.ListRecipientsAsync(peerDid, offset, limit, ct);

    public async ValueTask<MediationGrant?> FindGrantByRecipientDidAsync(string recipientDid, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _findCalls);
        if (OnFind?.Invoke(call, recipientDid, ct) is { } answer)
            return answer;
        if (OnFindAsync is { } hook && await hook(call, recipientDid, ct))
            return null;
        return await _inner.FindGrantByRecipientDidAsync(recipientDid, ct);
    }
}

/// <summary>The in-memory message store, with a hook on enqueue that can refuse, throw or fall through; records each id it was asked to store.</summary>
internal sealed class HookedMessageStore : IMessageStore
{
    public InMemoryMessageStore Inner { get; } = new(new MessagePickupOptions());

    /// <summary>Return an outcome to answer with it (nothing stored), <c>null</c> to store in <see cref="Inner"/>, or throw.</summary>
    public Func<QueuedMessage, EnqueueOutcome?>? OnEnqueue { get; set; }

    public System.Collections.Concurrent.ConcurrentQueue<string> EnqueueIds { get; } = new();
    public System.Collections.Concurrent.ConcurrentQueue<QueuedMessage> Enqueued { get; } = new();

    public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct)
    {
        EnqueueIds.Enqueue(message.Id);
        Enqueued.Enqueue(message);
        if (OnEnqueue?.Invoke(message) is { } outcome)
            return ValueTask.FromResult(outcome);
        return Inner.EnqueueAsync(message, lease, now, ct);
    }

    public ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct) => Inner.LeaseAsync(request, ct);
    public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct) => Inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => Inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct) => Inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
    public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => Inner.ReleaseLeasesAsync(leaseOwner, ct);
    public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => Inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => Inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => Inner.PurgeGrantAsync(grantPeerDid, ct);
    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => Inner.PurgeExpiredAsync(now, ct);
}
