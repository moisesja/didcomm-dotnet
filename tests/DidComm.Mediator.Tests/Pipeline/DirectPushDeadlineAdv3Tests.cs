using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// ADV-3 (#95): the push deadline abandons a store or transport call that ignores its token (the
/// worker stops waiting; the push itself runs on until the call returns, PR #109 review round 2). What an abandoned call does when it finally returns:
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
    public async Task A_take_that_returns_after_the_deadline_sends_nothing_and_gives_the_record_back_at_once()
    {
        // L-020 (PR #109 review round 2, D-3/D-5): the abandoned take used to keep its record until
        // its lease ran out. The push now runs on after the deadline only to end what it took: the
        // take returns, sees the deadline passed, sends nothing and gives the record back; the grant
        // is not served again before that.
        var store = new ScriptedMessageStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<QueuedMessage>>? lateTake = null;
        await using var fixture = PushFixture.Create(directory: _ => Inbox, services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.StoreAsync(Recipient);
        store.OnLease = (request, _) =>
        {
            entered.TrySetResult();
            lateTake = TakeLateAsync();
            return new ValueTask<IReadOnlyList<QueuedMessage>>(lateTake);

            async Task<IReadOnlyList<QueuedMessage>> TakeLateAsync()
            {
                await release.Task; // ignores its token
                return await store.Inner.LeaseAsync(request, CancellationToken.None);
            }
        };

        fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);
        fixture.Time.Advance(PastDeadline);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        fixture.Dispatcher.Unsettled.Should().Be(1, "the take still runs");

        store.OnLease = null;
        release.SetResult();
        (await lateTake!.WaitAsync(Wait)).Should().ContainSingle("the abandoned take did lease the record when it finally ran");
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the push ended what it took");

        var now = fixture.Options.Now();
        (await store.Inner.LeaseAsync(PickupRequest(now), CancellationToken.None)).Should().ContainSingle("given back at once, not left to its lease")
            .Which.Id.Should().Be(job.MessageId);
        fixture.Router.Requests.Should().BeEmpty("an abandoned take never leads to a send");
        fixture.PushOutcomeLog.Should().Equal(new[] { PushOutcomes.Failed }, "one outcome per push attempt, whatever the abandoned call did later");
    }

    [Fact]
    public async Task A_2xx_that_arrives_after_the_deadline_removes_the_record_it_delivered_and_a_pickup_never_held_it_meanwhile()
    {
        // L-020 (PR #109 review round 2, D-5): the push used to give its lease back at the deadline,
        // so a pickup could take the record while the abandoned send ran on, and the late 2xx had to
        // be ignored. The lease now ends exactly once, when the send ends: a pickup does not get the
        // record meanwhile, and the late 2xx — a delivery — removes it.
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
        var job = await fixture.StoreAsync(Recipient);

        fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);
        fixture.Time.Advance(PastDeadline);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);

        var now = fixture.Options.Now();
        (await fixture.Store.LeaseAsync(PickupRequest(now), CancellationToken.None)).Should().BeEmpty("the record stays under the push's lease while its send runs");

        release.SetResult();
        (await lateSend!.WaitAsync(Wait)).HttpStatusCode.Should().Be(202);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the abandoned push settled");

        (await fixture.Store.GetStatusAsync(Peer, null, now, CancellationToken.None)).MessageCount.Should().Be(0, "the late 2xx was a delivery: its record is removed");
        fixture.PushOutcomeLog.Should().Equal(new[] { PushOutcomes.Failed }, "counted once, at the deadline");
    }

    [Fact]
    public async Task A_removal_that_returns_after_the_deadline_removes_exactly_the_delivered_record_and_the_push_keeps_one_outcome()
    {
        // L-020 (PR #109 review round 2, D-3): the removal is part of the push now, so the push that
        // reached its deadline inside the removal counts as failed (the worker cannot tell it was
        // delivered); the removal still takes exactly the delivered record when it returns.
        var store = new ScriptedMessageStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? lateRemoval = null;
        await using var fixture = PushFixture.Create(directory: _ => Inbox, services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);
        var pushed = await fixture.StoreAsync(Recipient, PushFixture.Payload(32, seed: 1));
        var other = await fixture.StoreAsync(Recipient, PushFixture.Payload(32, seed: 2));
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

        fixture.Dispatcher.Wake(pushed.GrantPeerDid).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);
        fixture.Time.Advance(PastDeadline);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);

        // A pickup meanwhile leases what it can: only the other record (the pushed one is still under
        // the push's lease, never given back after a 2xx).
        var now = fixture.Options.Now();
        (await store.Inner.LeaseAsync(PickupRequest(now), CancellationToken.None)).Select(m => m.Id).Should().Equal(other.MessageId);

        release.SetResult();
        (await lateRemoval!.WaitAsync(Wait)).Should().Be(1);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the push settled");

        var status = await store.Inner.GetStatusAsync(Peer, null, now, CancellationToken.None);
        status.MessageCount.Should().Be(1, "the late removal took exactly the delivered record");
        (await store.Inner.AcknowledgeAsync(Peer, new[] { other.MessageId }, CancellationToken.None)).Should().Be(1, "the pickup's record was untouched");
        fixture.PushOutcomeLog.Should().Equal(new[] { PushOutcomes.Failed }, "one outcome per push attempt");
        fixture.Router.Requests.Should().ContainSingle();
    }

    private static MessageLeaseRequest PickupRequest(DateTimeOffset now)
        => new(Peer, null, 10, long.MaxValue, now, new MessageLease("adv3-pickup-session", now + TimeSpan.FromSeconds(60)));
}
