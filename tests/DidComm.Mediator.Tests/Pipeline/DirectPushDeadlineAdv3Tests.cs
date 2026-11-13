using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// ADV-3 (#95): the push deadline (<c>PushDispatcher.BoundedAsync</c>) abandons a store or
/// transport call that ignores its token. What an abandoned call does when it finally returns:
/// can it corrupt the queue (a late 2xx removing a record a pickup holds, a late take holding a
/// lease a pickup needs) or give a job a second outcome? Deterministic: every call is a gate the test
/// opens, every deadline the manual clock the test moves.
/// </summary>
public sealed class DirectPushDeadlineAdv3Tests
{
    private const string Peer = "did:example:adv3-peer";
    private const string Recipient = "did:example:adv3-recipient";
    private static readonly Uri Inbox = new("https://adv3-recipient.example/inbox");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PastDeadline = TimeSpan.FromSeconds(31); // PushTimeout is 30 s

    [Fact]
    public async Task A_take_that_returns_after_the_deadline_sends_nothing_and_holds_the_record_only_until_its_lease_ends()
    {
        var store = new ScriptedMessageStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<QueuedMessage?>? lateTake = null;
        await using var fixture = PushFixture.Create(directory: _ => Inbox, services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.QueuedJobAsync(Recipient);
        store.OnLeaseById = (grant, id, lease, now, _) =>
        {
            entered.TrySetResult();
            lateTake = TakeLateAsync();
            return new ValueTask<QueuedMessage?>(lateTake);

            async Task<QueuedMessage?> TakeLateAsync()
            {
                await release.Task; // ignores its token
                return await store.Inner.LeaseByIdAsync(grant, id, lease, now, CancellationToken.None);
            }
        };

        fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);
        fixture.Time.Advance(PastDeadline);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);

        release.SetResult();
        (await lateTake!.WaitAsync(Wait)).Should().NotBeNull("the abandoned take did lease the record when it finally ran");

        var now = fixture.Options.Now();
        (await store.Inner.LeaseAsync(PickupRequest(now), CancellationToken.None)).Should().BeEmpty("the abandoned take holds it until its lease ends");
        fixture.Time.Advance(TimeSpan.FromSeconds(30)); // the push's lease: the deadline plus the 30 s margin, from the take
        (await store.Inner.LeaseAsync(PickupRequest(fixture.Options.Now()), CancellationToken.None)).Should().ContainSingle()
            .Which.Id.Should().Be(job.MessageId);

        fixture.Router.Requests.Should().BeEmpty("an abandoned take never leads to a send");
        fixture.PushOutcomeLog.Should().Equal(new[] { PushOutcomes.Failed }, "one outcome per job, whatever the abandoned call did later");
    }

    [Fact]
    public async Task A_2xx_that_arrives_after_the_deadline_is_ignored_and_never_touches_the_record_a_pickup_took_since()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<TransportResult>? lateSend = null;
        await using var fixture = PushFixture.Create(
            send: (_, _) =>
            {
                entered.TrySetResult();
                return lateSend = SendLateAsync();

                async Task<TransportResult> SendLateAsync()
                {
                    await release.Task; // a transport that ignores its token
                    return new TransportResult(Accepted: true, HttpStatusCode: 202);
                }
            },
            directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.QueuedJobAsync(Recipient);

        fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);
        fixture.Time.Advance(PastDeadline);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);

        // The push gave its lease back at once; a pickup takes the record.
        var now = fixture.Options.Now();
        var picked = await fixture.Store.LeaseAsync(PickupRequest(now), CancellationToken.None);
        picked.Should().ContainSingle().Which.Id.Should().Be(job.MessageId);

        // Now the abandoned send answers 202.
        release.SetResult();
        (await lateSend!.WaitAsync(Wait)).HttpStatusCode.Should().Be(202);

        (await fixture.Store.GetStatusAsync(Peer, null, now, CancellationToken.None)).MessageCount.Should().Be(1, "the late 2xx removed nothing");
        (await fixture.Store.LeaseAsync(PickupRequest(now) with { Lease = new MessageLease("another-session", now + TimeSpan.FromSeconds(60)) }, CancellationToken.None))
            .Should().BeEmpty("the pickup's lease is intact");
        (await fixture.Store.AcknowledgeAsync(Peer, new[] { job.MessageId }, CancellationToken.None)).Should().Be(1, "the pickup acknowledges what it delivered");
        fixture.PushOutcomeLog.Should().Equal(new[] { PushOutcomes.Failed }, "one outcome per job");
    }

    [Fact]
    public async Task A_removal_that_returns_after_the_deadline_removes_exactly_the_delivered_record_and_the_job_keeps_one_outcome()
    {
        var store = new ScriptedMessageStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? lateRemoval = null;
        await using var fixture = PushFixture.Create(directory: _ => Inbox, services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);
        var pushed = await fixture.QueuedJobAsync(Recipient, PushFixture.Payload(32, seed: 1), forwardId: "fwd-pushed");
        var other = await fixture.QueuedJobAsync(Recipient, PushFixture.Payload(32, seed: 2), forwardId: "fwd-other");
        store.OnAcknowledge = (grant, ids, _) =>
        {
            entered.TrySetResult();
            lateRemoval = RemoveLateAsync();
            return new ValueTask<int>(lateRemoval);

            async Task<int> RemoveLateAsync()
            {
                await release.Task; // ignores its token
                return await store.Inner.AcknowledgeAsync(grant, ids, CancellationToken.None);
            }
        };

        fixture.Dispatcher.TryEnqueue(pushed).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);
        fixture.Time.Advance(PastDeadline);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Pushed);

        // A pickup meanwhile leases what it can: only the other record (the pushed one is still under
        // the push's lease, never given back after a 2xx).
        var now = fixture.Options.Now();
        (await store.Inner.LeaseAsync(PickupRequest(now), CancellationToken.None)).Select(m => m.Id).Should().Equal(other.MessageId);

        release.SetResult();
        (await lateRemoval!.WaitAsync(Wait)).Should().Be(1);

        var status = await store.Inner.GetStatusAsync(Peer, null, now, CancellationToken.None);
        status.MessageCount.Should().Be(1, "the late removal took exactly the delivered record");
        (await store.Inner.AcknowledgeAsync(Peer, new[] { other.MessageId }, CancellationToken.None)).Should().Be(1, "the pickup's record was untouched");
        fixture.PushOutcomeLog.Should().Equal(new[] { PushOutcomes.Pushed }, "one outcome per job");
        fixture.Router.Requests.Should().ContainSingle();
    }

    private static MessageLeaseRequest PickupRequest(DateTimeOffset now)
        => new(Peer, null, 10, long.MaxValue, now, new MessageLease("adv3-pickup-session", now + TimeSpan.FromSeconds(60)));
}
