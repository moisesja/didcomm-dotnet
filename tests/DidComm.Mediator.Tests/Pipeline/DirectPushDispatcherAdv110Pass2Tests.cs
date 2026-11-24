using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// Red team pass 2 (#110, direct push), the dispatcher's state after the pass-1 fixes: the
/// pause-clean-up queue (ADV-110-1b-1), the 4xx rule's run bookkeeping (ADV-110-1a-4), live-on waking
/// only an enrolled peer (ADV-110-1b-2), and the push mark under those rules. Seam-driven: the scripted
/// directory and sender of <see cref="DirectWorld"/>, a gated mediation store, the manual clock; no
/// sleeps.
/// </summary>
public sealed class DirectPushDispatcherAdv110Pass2Tests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PushTimeout = new MediatorOptions().PushTimeout;
    private static readonly TimeSpan Pause = PushDispatcher.CooldownPushTimeouts * PushTimeout;

    // ---- the 4xx rule: one refusal and the run's yield ----------------------------------------------------

    // The ADV-110-1a-4 fix says (MediatorOptions.DirectPush, PushDispatcher remarks): "messages anyone
    // stored ahead of a real one cannot hold it back; a real message behind more than MaxDeliveryBatch
    // such refused ones waits for the next relayed message". But the run's yield is suppressed by ANY
    // refusal in the run (more = !failed), not by a run made only of refusals. So one junk message a
    // third party stored ahead of a backlog holds back every real message past the run's bound until
    // the next wake-up, behind a single refusal. Measured at the default MaxDeliveryBatch (10): one
    // junk + 20 real messages, one wake-up → 9 of 20 real messages posted, 11 wait (the control below,
    // 21 real messages and no junk, posts all 21 in one wake-up). Fixed (the coordinator's call under
    // the churn rule): a run yields unless it met a refusal and delivered nothing.
    [Fact]
    public async Task ADV_110_2b_1_One_refused_message_ahead_of_a_backlog_holds_no_real_message_back_past_the_run_bound()
    {
        await using var direct = await DirectWorld.StartAsync();
        var fixture = direct.Fixture;
        var batch = fixture.Dispatcher.PerRun;
        var grant = await direct.GrantAsync("adv2b-one-junk");
        const byte Junk = 100;
        direct.Sends.Refuse(Junk);
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, Junk)); // a third party's, oldest
        var real = Enumerable.Range(0, 2 * batch).Select(i => (byte)(Junk + 1 + i)).ToArray();
        foreach (var seed in real)
            await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, seed));

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(batch, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        var posted = direct.Sends.Seeds.Where(s => s != Junk).ToList();
        posted.Should().Equal(
            real,
            "one refused message is fewer than MaxDeliveryBatch ({0}) refused ones, so it holds no real message back (posted {1} of {2} real messages in the wake-up)",
            batch, posted.Count, real.Length);
    }

    // The control for ADV-110-2b-1: the same backlog with no junk goes out in one wake-up (the run yields
    // at its bound and takes its turn again).
    [Fact]
    public async Task Control_A_backlog_with_no_refusal_goes_out_in_one_wake_up_across_the_runs_yields()
    {
        await using var direct = await DirectWorld.StartAsync();
        var fixture = direct.Fixture;
        var batch = fixture.Dispatcher.PerRun;
        var grant = await direct.GrantAsync("adv2b-no-junk");
        var real = Enumerable.Range(0, 2 * batch + 1).Select(i => (byte)(101 + i)).ToArray();
        foreach (var seed in real)
            await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, seed));

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(real.Length, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        direct.Sends.Seeds.Should().Equal(real);
    }

    // A run that mixes a refusal, a removal (the recipient DID was unregistered), a delivery, a second
    // refusal and then a 5xx: one outcome per attempt, the 5xx pauses and ends the run (the refusals
    // before it did not), each later wake-up during the pause counts Push-Skipped-Cooldown once and posts
    // nothing, and after the pause the rest go, each once. Pickup gets exactly the three that did not
    // deliver. (Could not break.)
    [Fact]
    public async Task A_run_mixing_refusals_a_removal_a_delivery_and_a_5xx_counts_one_outcome_per_attempt_pauses_once_and_loses_nothing()
    {
        await using var direct = await DirectWorld.StartAsync();
        var fixture = direct.Fixture;
        var grant = await direct.GrantAsync("adv2b-mixed");
        var unregistered = grant.Recipient + "-second";
        (await fixture.Mediation.UpdateRecipientsAsync(grant.Peer, new[] { new RecipientUpdate(unregistered, RecipientUpdateAction.Add) }, CancellationToken.None))
            .Should().Equal(RecipientStoreOutcome.Added);
        direct.Sends.Refuse(60);
        direct.Sends.Refuse(63);
        direct.Sends.Fail(64);
        var r60 = await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 60));
        await fixture.StoreAsync(unregistered, PushFixture.Payload(64, 61));
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 62));
        var r63 = await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 63));
        var r64 = await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 64));
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 65));
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 66));
        await fixture.Mediation.UpdateRecipientsAsync(grant.Peer, new[] { new RecipientUpdate(unregistered, RecipientUpdateAction.Remove) }, CancellationToken.None);

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(5, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.SkippedUnregistered, PushOutcomes.PushedDirect, PushOutcomes.Failed, PushOutcomes.Failed);
        direct.Sends.Seeds.Should().Equal(new byte[] { 60, 62, 63, 64 }, "the 5xx ended the run; the refusals before it did not");

        for (var i = 0; i < 3; i++)
        {
            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await fixture.WaitForPushOutcomesAsync(6 + i, Wait);
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }
        fixture.PushOutcomeLog.Skip(5).Should().Equal(Enumerable.Repeat(PushOutcomes.SkippedCooldown, 3), "once per operation");
        direct.Sends.Seeds.Should().HaveCount(4, "nothing is posted during the pause");

        fixture.Time.Advance(Pause);
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(10, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Skip(8).Should().Equal(PushOutcomes.PushedDirect, PushOutcomes.PushedDirect);
        direct.Sends.Seeds.Should().Equal(new byte[] { 60, 62, 63, 64, 65, 66 }, "each record at most once");
        (await direct.World.PickupAsync(grant.Peer)).Should().BeEquivalentTo(new[] { r60.MessageId, r63.MessageId, r64.MessageId });
    }

    // Two dispatchers with a direct route over one store (as two processes would be), woken together and
    // again and again, against an endpoint that refuses some records with a 4xx and fails some with a
    // 5xx (each dispatcher keeps its own pause). The push mark alone keeps every record to one POST.
    // (Could not break.)
    [Fact]
    public async Task Two_dispatchers_over_one_store_with_4xx_and_5xx_answers_never_post_a_record_twice()
    {
        await using var direct = await DirectWorld.StartAsync(o => o.Pickup.MaxDeliveryBatch = 3);
        var fixture = direct.Fixture;
        var grant = await direct.GrantAsync("adv2b-two-dispatchers");
        var posts = new ConcurrentDictionary<byte, int>();
        direct.Sends.OnAnswer = async (_, payload, _) =>
        {
            var seed = StandInJwe.SeedOf(payload.Span);
            posts.AddOrUpdate(seed, 1, (_, n) => n + 1);
            await Task.Yield();
            return (seed % 3, seed % 7) switch
            {
                (0, _) => DirectSendResult.Refused,
                (_, 0) => DirectSendResult.Failed,
                _ => DirectSendResult.Delivered,
            };
        };
        for (byte seed = 1; seed <= 40; seed++)
            await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, seed));
        await using var second = new PushDispatcher(
            fixture.Services.GetRequiredService<IOptions<MediatorOptions>>(),
            fixture.Queue,
            fixture.Services.GetRequiredService<MediatorMetrics>(),
            logger: null,
            fixture.Time,
            shutdownGrace: null,
            new DirectPushRoute(direct.Endpoints, new Lazy<IDirectPushSender>(() => direct.Sends)));

        for (var round = 0; round < 8; round++)
        {
            fixture.Dispatcher.Wake(grant.Peer);
            second.Wake(grant.Peer);
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
            await second.WaitForIdleAsync(Wait);
            if (round % 2 == 1)
                fixture.Time.Advance(Pause); // both dispatchers' pauses run out
        }

        posts.Where(p => p.Value > 1).Should().BeEmpty("the store's push mark keeps every record to one POST, whichever dispatcher takes it");
        (posts.Count(p => p.Key % 3 != 0 && p.Key % 7 != 0) + fixture.Store.MessageCount).Should().Be(40, "every record was delivered once or is still queued for pickup");
    }

    // ---- the pause-clean-up queue -------------------------------------------------------------------------

    // N recipients fail together (a 5xx each), their pauses run out, every grant is revoked and granted
    // again under the same DID, and they all fail together again; then wake-ups during the new pause.
    // The clean-up queue holds one start per recipient per pause (never more), the entries never exceed
    // the recipients, every operation reads the monotonic clock a constant number of times whatever N
    // is, and once the last pause has run out one worker finish leaves nothing behind. (Could not break.)
    [Theory]
    [InlineData(10)]
    [InlineData(200)]
    public async Task Pause_starts_stay_one_per_recipient_per_pause_under_a_burst_and_same_DID_churn_with_constant_clock_reads_per_operation(int recipients)
    {
        CountingClock? clock = null;
        await using var direct = await DirectWorld.StartAsync(clock: inner => clock = new CountingClock(inner));
        var fixture = direct.Fixture;
        _ = fixture.Dispatcher; // built now, on the counting clock
        const byte Failing = 90;
        direct.Sends.Fail(Failing);
        var grants = new List<DirectGrant>();
        for (var i = 0; i < recipients; i++)
            grants.Add(await direct.GrantAsync($"adv2b-burst-{i}"));
        var outcomes = 0;

        async Task<double> BurstAsync(bool storeFirst)
        {
            if (storeFirst)
            {
                foreach (var grant in grants)
                {
                    await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, Failing));
                    await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 91));
                }
            }
            var before = clock!.Reads;
            foreach (var grant in grants)
                fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            outcomes += grants.Count;
            await fixture.WaitForPushOutcomesAsync(outcomes, Wait);
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
            return (clock.Reads - before) / (double)grants.Count;
        }

        var firstBurst = await BurstAsync(storeFirst: true);
        fixture.Dispatcher.PauseStarts.Should().Be(recipients);
        fixture.Dispatcher.GrantEntries.Should().Be(recipients);

        // The pauses run out; every grant is revoked and granted again under the same DID.
        fixture.Time.Advance(Pause);
        foreach (var grant in grants)
        {
            (await fixture.Queue.RevokeAndPurgeAsync(grant.Peer)).Revoked.Should().BeTrue();
            await fixture.EnrolAsync(grant.Peer, grant.Recipient);
        }
        var secondBurst = await BurstAsync(storeFirst: true);
        fixture.Dispatcher.PauseStarts.Should().Be(recipients, "each spent start went at the next pause start; one new start per recipient");
        fixture.Dispatcher.GrantEntries.Should().Be(recipients);

        // Wake-ups during the new pause: skipped, nothing added to the queue.
        var skipped = await BurstAsync(storeFirst: false);
        fixture.Dispatcher.PauseStarts.Should().Be(recipients);
        fixture.PushOutcomeLog.Skip(2 * recipients).Should().OnlyContain(o => o == PushOutcomes.SkippedCooldown);
        direct.Sends.Seeds.Should().HaveCount(2 * recipients).And.OnlyContain(s => s == Failing, "one POST per stored failing record, never the one behind it during a pause");

        // Constant work per operation, whatever the number of recipients.
        firstBurst.Should().BeLessThanOrEqualTo(6, "clock reads per failing operation (N = {0})", recipients);
        secondBurst.Should().BeLessThanOrEqualTo(6, "clock reads per failing operation after churn (N = {0}); the spent starts are dropped once each", recipients);
        skipped.Should().BeLessThanOrEqualTo(6, "clock reads per skipped operation (N = {0})", recipients);

        // The last pauses run out: one healthy operation's finish leaves nothing behind.
        fixture.Time.Advance(Pause);
        var healthy = await direct.GrantAsync("adv2b-burst-healthy");
        await fixture.StoreAsync(healthy.Recipient, PushFixture.Payload(64, 92));
        fixture.Dispatcher.Wake(healthy.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(outcomes + 1, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Dispatcher.PauseStarts.Should().Be(0);
        fixture.Dispatcher.GrantEntries.Should().Be(0);
        fixture.Dispatcher.RetainedCharacters.Should().Be(0);
    }

    // A direct push abandoned at its deadline (a sender that ignores cancellation) pauses the
    // recipient; the pause runs out before the abandoned send ends, so the late settlement drops the
    // entry while its pause start is still queued (no worker finish follows a late settlement). A new
    // wake-up makes a NEW entry for the same DID, whose push fails and pauses it. The stale start, for
    // an entry that was dropped and made again, is cleaned up by that very pause start and must not cut
    // the new pause short: the next wake-up posts nothing. (Could not break.)
    [Fact]
    public async Task A_stale_pause_start_of_an_entry_dropped_and_made_again_never_cuts_the_new_entrys_pause_short()
    {
        await using var direct = await DirectWorld.StartAsync();
        var fixture = direct.Fixture;
        var grant = await direct.GrantAsync("adv2b-stale-start");
        const byte Stalled = 70, Failing = 71, Behind = 72;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        direct.Sends.OnAnswer = async (_, payload, _) =>
        {
            switch (StandInJwe.SeedOf(payload.Span))
            {
                case Stalled:
                    entered.TrySetResult();
                    await release.Task; // ignores its token: abandoned at the deadline
                    return DirectSendResult.Failed;
                case Failing:
                    return DirectSendResult.Failed;
                default:
                    return DirectSendResult.Delivered;
            }
        };
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, Stalled));

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);
        fixture.Time.Advance(PushTimeout); // the deadline: abandoned, the recipient paused
        await fixture.WaitForPushOutcomesAsync(1, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait); // the worker finished (the abandoned send runs on)
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        fixture.Dispatcher.PauseStarts.Should().Be(1);

        // The pause runs out, then the abandoned send ends: its settlement drops the entry.
        fixture.Time.Advance(Pause);
        release.TrySetResult();
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0 && fixture.Dispatcher.GrantEntries == 0, "the late settlement dropped the entry");
        fixture.Dispatcher.PauseStarts.Should().Be(1, "precondition: the stale start of the dropped entry is still queued");

        // A new entry for the same DID: its push fails and pauses it; the stale start is cleaned up then.
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, Failing));
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, Behind));
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(2, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Dispatcher.PauseStarts.Should().Be(1, "the stale start went; the new one is queued");
        fixture.Dispatcher.GrantEntries.Should().Be(1, "the new entry is paused");

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(3, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.Failed, PushOutcomes.SkippedCooldown);
        direct.Sends.Seeds.Should().Equal(new[] { Stalled, Failing }, "the new pause holds: the record behind it is not posted");
    }

    // ---- live-on wakes only an enrolled peer (ADV-110-1b-2) -----------------------------------------------

    // The ADV-110-1b-2 fix raises the live-on wake-up only when the ownership read of the live-delivery
    // change found a grant. That read is taken before the session is live. A peer whose grant is made
    // while that read is in flight (the operator enrols it as its app connects and turns live delivery
    // on), and a message relayed to it in the same window, leave the message with no wake-up at all: the
    // forward found no live session (direct push off: it wakes nothing), and the live-on raised nothing
    // (it read "no grant"). Its status mark is 0, so the message is the session's to be pushed, but it is
    // pushed only at the next relayed message (a delivery-request would not be asked for: the status
    // said 0). Before the fix (3bc0242) the live-on woke the grant and the message was pushed at once.
    // Measured: 0 live pushes of 1 eligible message (1 with the `&& owned` condition removed). Fixed (the
    // coordinator's call under the churn rule): the live-on wakes the grant whatever the ownership read
    // said, as before 1b-2; IRecipientEndpointDirectory's contract is widened instead (2b-3).
    [Fact]
    public async Task ADV_110_2b_2_A_message_relayed_while_live_on_reads_the_grant_is_pushed_live_when_the_grant_was_made_in_between()
    {
        var mediation = new GatedMediationStore();
        await using var world = await LivePushWorld.StartAsync(services: s => s.AddSingleton<IMediationStore>(mediation));
        var fixture = world.Fixture;
        fixture.Dispatcher.PushesWithoutLiveSession.Should().BeFalse("precondition: direct push off");
        var peer = await world.Peers.NewAsync();
        const string Recipient = "did:example:adv2b-race-recipient";
        var session = new LiveTestSession(peer.Did, world.Mediator.Did);

        // The live-delivery change reads the grant (none yet) and parks right after the read.
        var parked = mediation.ParkNextGrantRead(peer.Did);
        var liveOn = Task.Run(() => TurnLiveOnAsync(fixture, session));
        await parked.Read.WaitAsync(Wait);

        // Meanwhile the operator enrols the peer, and a message is relayed to it.
        await fixture.EnrolAsync(peer.Did, Recipient);
        await world.ForwardAsync(Recipient, 1);
        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued);

        parked.Release();
        await liveOn.WaitAsync(Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        mediation.ParkedReads.Should().Be(1, "precondition: the live-on's ownership read was the one parked");

        LivePushWorld.Deliveries(session).Should().HaveCount(
            1,
            "the message arrived after the live-on's status (mark 0) while the peer held a grant, so it is the live session's: nothing else will wake it (live pushes {0})",
            session.Sent.Count);
    }

    // ---- the directory may be asked about a DID with no grant; nothing is taken or sent ---------------

    // A forward stored and woken just before its grant is revoked (and purged): its queued wake-up still
    // runs the direct step, which asks the host's directory for the endpoint before the take finds that
    // the grant has nothing left. The directory's contract now says it may be asked about a DID with no
    // grant (the coordinator's call under the churn rule, ADV-110-2b-3): whatever it answers — here an
    // endpoint for every DID — nothing is taken or sent for it.
    [Fact]
    public async Task ADV_110_2b_3_A_wake_up_queued_before_a_revoke_may_ask_the_directory_about_the_revoked_DID_and_nothing_is_taken_or_sent()
    {
        await using var direct = await DirectWorld.StartAsync(o => o.PushConcurrency = 1);
        var fixture = direct.Fixture;
        var parker = await direct.GrantAsync("adv2b-parker");
        await fixture.StoreAsync(parker.Recipient, PushFixture.Payload(64, 40));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        direct.Sends.OnAnswer = async (_, payload, ct) =>
        {
            if (StandInJwe.SeedOf(payload.Span) == 40)
            {
                entered.TrySetResult();
                await gate.Task.WaitAsync(ct);
            }
            return DirectSendResult.Delivered;
        };
        fixture.Dispatcher.Wake(parker.Peer).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);

        var revoked = await direct.GrantAsync("adv2b-revoked");
        await direct.ForwardAsync(revoked.Recipient, PushFixture.Payload(64, 41), "fwd-adv2b-revoked"); // stored; its grant woken
        (await fixture.Queue.RevokeAndPurgeAsync(revoked.Peer)).Revoked.Should().BeTrue();
        (await fixture.Mediation.GetGrantAsync(revoked.Peer, CancellationToken.None)).Should().BeNull();
        // A directory that answers an endpoint for any DID it is asked about (the contract allows anything).
        direct.Endpoints.OnFind = (_, _) => ValueTask.FromResult<Uri?>(DirectWorld.EndpointFor("anyone"));

        gate.TrySetResult();
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "both operations settled");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        direct.Sends.Seeds.Should().Equal(new byte[] { 40 }, "nothing is sent for a DID with no grant, whatever the directory answers");
        fixture.PushOutcomeLog.Should().Equal(new[] { PushOutcomes.PushedDirect }, "the revoked grant's push took nothing: no outcome");
        fixture.Store.MessageCount.Should().Be(0, "the purge removed the revoked grant's message; the parker's was delivered");
    }

    // ---- support -------------------------------------------------------------------------------------------

    private static async Task TurnLiveOnAsync(PushFixture fixture, IReturnRouteSession session)
    {
        fixture.Services.GetRequiredService<IReturnRouteSessionRegistry>().Register(session);
        var client = fixture.Services.GetRequiredService<DidCommClient>();
        var change = MessagePickup.CreateLiveDeliveryChange(session.PeerDid, session.LocalDid, liveDelivery: true);
        var packed = await client.PackEncryptedAsync(change, new PackEncryptedOptions(Recipients: new[] { session.LocalDid }, From: session.PeerDid));
        await fixture.Services.GetRequiredService<ProtocolDispatcher>().DispatchAsync(await client.UnpackAsync(packed.Message), client, fixture.Options, Task.CompletedTask, session, CancellationToken.None);
    }

    /// <summary>The dispatcher's clock: the manual one, counting its monotonic readings.</summary>
    private sealed class CountingClock(TimeProvider inner) : TimeProvider
    {
        private long _reads;

        public long Reads => Interlocked.Read(ref _reads);

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp()
        {
            Interlocked.Increment(ref _reads);
            return inner.GetTimestamp();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => inner.CreateTimer(callback, state, dueTime, period);
    }

    /// <summary>A grant read parked right after it read the store: the value it read is what it returns once released.</summary>
    private sealed class ParkedGrantRead(string peerDid)
    {
        private readonly TaskCompletionSource _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string PeerDid { get; } = peerDid;

        /// <summary>Completes once the parked read has read the store.</summary>
        public Task Read => _read.Task;

        public Task Released => _release.Task;

        public void Signal() => _read.TrySetResult();

        public void Release() => _release.TrySetResult();
    }

    /// <summary>The real in-memory mediation store; the next grant read of a given peer can be parked after it read.</summary>
    private sealed class GatedMediationStore : IMediationStore
    {
        private readonly InMemoryMediationStore _inner = new(new CoordinateMediationOptions());
        private ParkedGrantRead? _next;
        private int _parkedReads;

        public int ParkedReads => Volatile.Read(ref _parkedReads);

        public ParkedGrantRead ParkNextGrantRead(string peerDid)
        {
            var parked = new ParkedGrantRead(peerDid);
            Volatile.Write(ref _next, parked);
            return parked;
        }

        public async ValueTask<MediationGrant?> GetGrantAsync(string peerDid, CancellationToken ct)
        {
            var grant = await _inner.GetGrantAsync(peerDid, ct);
            if (Volatile.Read(ref _next) is { } parked && string.Equals(parked.PeerDid, peerDid, StringComparison.Ordinal)
                && ReferenceEquals(Interlocked.CompareExchange(ref _next, null, parked), parked))
            {
                Interlocked.Increment(ref _parkedReads);
                parked.Signal();
                await parked.Released;
            }
            return grant;
        }

        public ValueTask<MediationGrant?> GrantAsync(MediationGrant grant, CancellationToken ct) => _inner.GrantAsync(grant, ct);
        public ValueTask<bool> RevokeAsync(string peerDid, CancellationToken ct) => _inner.RevokeAsync(peerDid, ct);
        public ValueTask<IReadOnlyList<RecipientStoreOutcome>> UpdateRecipientsAsync(string peerDid, IReadOnlyList<RecipientUpdate> updates, CancellationToken ct) => _inner.UpdateRecipientsAsync(peerDid, updates, ct);
        public ValueTask<RecipientPage> ListRecipientsAsync(string peerDid, int offset, int limit, CancellationToken ct) => _inner.ListRecipientsAsync(peerDid, offset, limit, ct);
        public ValueTask<MediationGrant?> FindGrantByRecipientDidAsync(string recipientDid, CancellationToken ct) => _inner.FindGrantByRecipientDidAsync(recipientDid, ct);
    }
}
