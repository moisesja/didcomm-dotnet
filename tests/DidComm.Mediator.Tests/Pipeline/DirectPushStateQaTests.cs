using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Protocols.Routing;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// QA (#110, direct push): the state a direct push leaves behind, measured as POSTs per stored
/// record — the push mark lost or bypassed by a host store, a session that turns live delivery on
/// in the middle of a direct push, a lease that runs out while a POST is in flight, two push
/// dispatchers over one store, records stored before direct push was turned on, more grants than
/// <c>MaxGrants</c> over host stores, and ids that differ only by case or break the record-id rules.
/// Seam-driven: scripted directory and sender, store hooks, the manual clock.
/// </summary>
public sealed class DirectPushStateQaTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // ---- the push mark lost by a host store --------------------------------------------------------

    public enum InnerStore
    {
        InMemory,
        Reference,
    }

    // A host store that drops the mark field (plan residual 2: "a host store that drops the mark
    // re-pushes"). Measured: one refused record. Without the mark the next take is the same record;
    // before the fix the per-run yield re-woke the grant by itself, so one wake-up never ended while
    // the endpoint refused (QA1-1). Now a direct push that does not deliver ends its run and pauses
    // the recipient's direct pushes: one POST of the record per pause, none during it. The test lets
    // the endpoint accept once the refused record has been posted 3 × MaxDeliveryBatch times, so a
    // looping product ends.
    [Theory]
    [InlineData(InnerStore.InMemory)]
    [InlineData(InnerStore.Reference)]
    public async Task QA1_A_host_store_that_drops_the_push_mark_posts_a_refused_record_at_most_once_per_pause_never_in_a_loop(InnerStore inner)
    {
        var store = new MarkDroppingStore(inner == InnerStore.InMemory ? new InMemoryMessageStore() : new ReferenceMessageStore());
        await using var direct = await DirectWorld.StartAsync(services: s => s.AddSingleton<IMessageStore>(store));
        var grant = await direct.GrantAsync("qa1-mark-dropped");
        var cap = 3 * direct.Fixture.Dispatcher.PerRun;
        var posts = 0;
        // Refused until the cap, then accepted (removed): a product that loops stops there.
        direct.Sends.OnSend = (_, _, _) => Task.FromResult(Interlocked.Increment(ref posts) > cap);
        await direct.Fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 201));

        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Fixture.WaitForPushOutcomesAsync(1, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        Volatile.Read(ref posts).Should().Be(1, "one wake-up is at most one attempt of a record, even when the store lost the mark (residual 2 is a re-push after the pause, not a loop)");
        direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        store.PushTakes.Should().ContainSingle("the refusal ended the run: the record was taken once");

        // Wake-ups during the pause post nothing; the first after it posts the record once more.
        for (var wake = 0; wake < 3; wake++)
        {
            direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await direct.Fixture.WaitForPushOutcomesAsync(2 + wake, Wait);
            await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        }
        Volatile.Read(ref posts).Should().Be(1, "nothing is posted during the pause");
        direct.Fixture.Time.Advance(2 * new MediatorOptions().PushTimeout);
        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Fixture.WaitForPushOutcomesAsync(5, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        Volatile.Read(ref posts).Should().Be(2, "a store that lost the mark gets the refused record posted at most once per pause (IMessageStore)");
        direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.SkippedCooldown, PushOutcomes.SkippedCooldown, PushOutcomes.SkippedCooldown, PushOutcomes.Failed);
    }

    // The same shape with a store whose clock lags the mediator's (it judges expiry by its own clock,
    // as a database's server time would, not by the request's): it hands out a record the direct
    // step, reading the mediator's clock, finds expired. The step gives it back unsent and says
    // "Expired, go on"; the next take is the same record, and the per-run yield re-wakes the grant,
    // so one wake-up takes the record again and again for as long as the clocks disagree (here, on
    // a frozen manual clock: until the store catches up after 3 × MaxDeliveryBatch takes). Nothing
    // is sent; the cost was a busy loop of store round trips (QA1-3). Now a record found expired at
    // send time ends the run: one take per wake-up.
    [Fact]
    public async Task QA3_A_host_store_whose_clock_lags_makes_one_wake_up_take_an_expired_record_a_bounded_number_of_times()
    {
        var store = new LaggingClockStore(new InMemoryMessageStore(), TimeSpan.FromSeconds(5));
        await using var direct = await DirectWorld.StartAsync(services: s => s.AddSingleton<IMessageStore>(store));
        var grant = await direct.GrantAsync("qa3-lagging-store");
        var expires = direct.Fixture.Time.GetUtcNow().ToUnixTimeSeconds() + 2;
        await direct.Fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 71), new PickupDeliveryOptions(null, expires, AllowLive: false));
        direct.Fixture.Time.Advance(TimeSpan.FromSeconds(3)); // expired on the mediator's clock, not yet on the store's
        store.CatchUpAfter = 3 * direct.Fixture.Dispatcher.PerRun;

        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        direct.Sends.Sent.Should().BeEmpty("an expired message is never sent");
        store.PushTakes.Should().BeLessThanOrEqualTo(2, "one wake-up takes a record it found expired a bounded number of times, not until the clocks agree");
    }

    // A host store that hands a push lease a record still carrying an earlier push's mark: the
    // take's contract check refuses it (engineer step test pins the step); here the dispatcher, over
    // many wake-ups: never a second POST, each refusal one Failed outcome, and the worker goes on.
    [Fact]
    public async Task A_host_store_that_hands_a_push_a_marked_record_is_refused_at_every_wake_up_and_never_posted_again()
    {
        var hooked = new HookedStore(new InMemoryMessageStore());
        await using var direct = await DirectWorld.StartAsync(services: s => s.AddSingleton<IMessageStore>(hooked));
        var grant = await direct.GrantAsync("qa-marked-handed");
        direct.Sends.Fail(202);
        await direct.Fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 202));
        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Fixture.WaitForPushOutcomesAsync(1, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        direct.Sends.Sent.Should().ContainSingle();

        hooked.IgnorePushMark = true;
        for (var wake = 0; wake < 5; wake++)
        {
            direct.Fixture.Time.Advance(2 * new MediatorOptions().PushTimeout); // past the pause the last failure started
            direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await direct.Fixture.WaitForPushOutcomesAsync(2 + wake, Wait);
            await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        }

        direct.Sends.Sent.Should().ContainSingle("a record a push took is never posted again, whatever the store hands out");
        direct.Fixture.PushOutcomeLog.Should().Equal(Enumerable.Repeat(PushOutcomes.Failed, 6));
        hooked.IgnorePushMark = false;
        (await direct.World.PickupFromAsync(hooked, grant.Peer)).Should().ContainSingle("pickup still gets it");
    }

    // ---- D6: a live session wins, also one that turns live on during a direct push ----------------

    public enum LiveOnMoment
    {
        WhileTheDirectoryAnswers,
        BetweenTheTakeAndTheSend,
    }

    // The dispatcher looks for a live session, finds none, and goes direct; the direct step then
    // asks the host's directory (host code, any duration). Before the fix it took the oldest record
    // with no second look, so a session that turned live delivery on in that window had the record
    // its live-on status counted (the recipient's to pick up, FR-PICK-06) POSTed while it was live
    // (QA1-2). Now the step looks for a live session right before the take: one that turned live
    // delivery on while the directory answered wins, and nothing is POSTed. One that turns it on
    // after the take finds the push in flight (the documented residual): that one POST goes ahead,
    // at most a duplicate delivery, and the live part is unaffected.
    [Theory]
    [InlineData(LiveOnMoment.WhileTheDirectoryAnswers)]
    [InlineData(LiveOnMoment.BetweenTheTakeAndTheSend)]
    public async Task QA2_A_session_that_turns_live_delivery_on_before_a_direct_push_takes_wins_and_one_that_does_after_the_take_costs_at_most_that_one_POST(LiveOnMoment when)
    {
        var hooked = new HookedStore(new InMemoryMessageStore());
        await using var direct = await DirectWorld.StartAsync(services: s => s.AddSingleton<IMessageStore>(hooked));
        var grant = await direct.World.GrantAsync("qa2-live-wins");
        var endpoint = DirectWorld.EndpointFor("qa2-live-wins");
        direct.Endpoints.Set(grant.Peer, endpoint);
        var counted = await direct.World.StoreAsync(grant, 1); // before live-on: its status counts it
        var session = new LiveTestSession(grant.Peer, direct.World.Mediator.Did);
        StoredMessage? afterLiveOn = null;
        var turned = 0;
        async Task TurnLiveOnOnceAsync()
        {
            if (Interlocked.Exchange(ref turned, 1) != 0)
                return;
            await TurnLiveOnAsync(direct.Fixture, session);
            afterLiveOn = await direct.World.StoreAsync(grant, 2); // after live-on: the session's
        }
        if (when == LiveOnMoment.WhileTheDirectoryAnswers)
        {
            direct.Endpoints.OnFind = async (_, _) =>
            {
                await TurnLiveOnOnceAsync();
                return endpoint;
            };
        }
        else
        {
            hooked.AfterLease = async (request, taken) =>
            {
                if (request.Lease is { ForPush: true, Owner: null } && taken.Count == 1)
                    await TurnLiveOnOnceAsync();
            };
        }

        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Fixture.Recorder.WaitForAsync(r => r.TagValues(MediatorMetrics.PushOutcomesInstrument, MediatorMetrics.PushOutcomeTag).Contains(PushOutcomes.PushedLive), Wait, "the message that arrived after live-on was pushed live");
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        LivePushWorld.Deliveries(session).Select(d => d.Id).Should().Equal(new[] { afterLiveOn!.MessageId }, "what arrived after live-on goes live ({0})", when);
        if (when == LiveOnMoment.WhileTheDirectoryAnswers)
        {
            direct.Sends.Sent.Should().BeEmpty("a live session that turned live delivery on before the take wins: nothing is POSTed");
            direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedLive);
            (await direct.World.PickupFromAsync(hooked, grant.Peer)).Should().Equal(new[] { counted.MessageId }, "what the live-on status counted is the recipient's to pick up");
        }
        else
        {
            // The residual: the push in flight goes ahead, once; the record it delivered is removed.
            direct.Sends.Seeds.Should().Equal(new[] { LivePushWorld.Seed(1) }, "at most the one POST that was in flight");
            direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedDirect, PushOutcomes.PushedLive);
            (await direct.World.PickupFromAsync(hooked, grant.Peer)).Should().BeEmpty("delivered by the POST; the live push's record is the session's");
        }
    }

    // A live session that becomes a live target again during the directory lookup without turning
    // live delivery on anew — it was downgraded (return_route none) and resumes return_route all —
    // raises no wake-up of its own: the direct step's "live session, take nothing" must hand the
    // operation on to its next push, which goes live, or the message would wait for the next wake-up.
    [Fact]
    public async Task A_live_session_that_resumes_while_the_directory_answers_gets_the_message_in_the_same_operation_and_nothing_is_posted()
    {
        await using var direct = await DirectWorld.StartAsync();
        var grant = await direct.World.GrantAsync("qa2-resumed");
        var endpoint = DirectWorld.EndpointFor("qa2-resumed");
        direct.Endpoints.Set(grant.Peer, endpoint);
        var session = new PickupFakeSession(grant.Peer, direct.World.Mediator.Did, live: true);
        await TurnLiveOnAsync(direct.Fixture, session);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait); // the live-on wake-up found nothing
        session.Downgrade();
        direct.Endpoints.OnFind = (_, _) =>
        {
            session.Resume();
            return ValueTask.FromResult<Uri?>(endpoint);
        };
        var stored = await direct.World.StoreAsync(grant, 1); // after live-on: the session's

        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Fixture.Recorder.WaitForAsync(r => r.TagValues(MediatorMetrics.PushOutcomesInstrument, MediatorMetrics.PushOutcomeTag).Contains(PushOutcomes.PushedLive), Wait, "the resumed session got the message in this operation");
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        session.Sent.SelectMany(m => MessagePickup.TryParseDelivery(m, out var delivered, out _) ? delivered : Array.Empty<DeliveredMessage>())
            .Select(d => d.Id).Should().Equal(stored.MessageId);
        direct.Sends.Sent.Should().BeEmpty("the live session wins");
        direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedLive);
    }

    // ---- the mark across a lease that runs out while a POST is in flight -------------------------

    // PushTimeout (3 min) longer than the lease (30 s): the lease runs out while the POST is held. A
    // delivery-request then gets the record (the recipient may now hold it twice: plan residual 3),
    // lets its own lease run out, the held wake-up and a second dispatcher (another mediator process
    // over the same store) find nothing to push, and the POST ends. Exactly one POST, whatever it
    // answers; the record is removed after a 2xx and kept marked (pickup only) otherwise.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_lease_that_runs_out_during_a_held_POST_lets_a_pickup_have_it_and_no_push_of_any_process_posts_it_again(bool accepted)
    {
        await using var direct = await DirectWorld.StartAsync(o =>
        {
            o.PushTimeout = TimeSpan.FromMinutes(3);
            o.Pickup.LeaseTimeout = TimeSpan.FromSeconds(30);
        });
        var fixture = direct.Fixture;
        var grant = await direct.GrantAsync("qa-lease-runs-out");
        var gate = new PushGate();
        direct.Sends.OnSend = async (_, _, ct) =>
        {
            await gate.PassAsync(ct);
            return accepted;
        };
        var stored = await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 31));
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);

        fixture.Time.Advance(TimeSpan.FromSeconds(31)); // the push's lease has run out; its deadline has not
        (await direct.World.PickupAsync(grant.Peer)).Should().Equal(new[] { stored.MessageId }, "a delivery-request may take a marked record whose lease ran out");
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue("held while the grant is busy");
        fixture.Time.Advance(TimeSpan.FromSeconds(61)); // the pickup's lease has run out too
        await using (var other = NewDirectDispatcher(direct))
        {
            other.Wake(grant.Peer).Should().BeTrue();
            await other.WaitForIdleAsync(Wait);
        }
        direct.Sends.Sent.Should().ContainSingle("another process's push finds the record marked");

        gate.Open();
        await fixture.WaitForPushOutcomesAsync(accepted ? 1 : 2, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        direct.Sends.Sent.Should().ContainSingle("one POST per record, across the lease's end, a pickup and two dispatchers");
        // Refused: the wake-up held while the POST ran is served during the pause the refusal started.
        fixture.PushOutcomeLog.Should().Equal(accepted ? new[] { PushOutcomes.PushedDirect } : new[] { PushOutcomes.Failed, PushOutcomes.SkippedCooldown });
        (await direct.World.PickupAsync(grant.Peer)).Should().Equal(accepted ? Array.Empty<string>() : new[] { stored.MessageId }, "removed after a 2xx; otherwise the recipient's to pick up");
    }

    // A delivery-request takes a record a direct push marked and gives it back every way a pickup
    // can (its lease runs out; it is released; it is given back as unsent; it is passed over, too large for the recipient's envelope): the mark
    // is the direct push's, so no push takes the record again.
    [Theory]
    [InlineData(StoreKind.InMemory, "expires")]
    [InlineData(StoreKind.InMemory, "passed-over")]
    [InlineData(StoreKind.InMemory, "released")]
    [InlineData(StoreKind.InMemory, "returned-unsent")]
    [InlineData(StoreKind.Reference, "expires")]
    [InlineData(StoreKind.Reference, "passed-over")]
    [InlineData(StoreKind.Reference, "released")]
    [InlineData(StoreKind.Reference, "returned-unsent")]
    public async Task A_pickup_that_takes_a_record_a_direct_push_marked_and_gives_it_back_never_makes_it_pushable_again(StoreKind kind, string how)
    {
        using var world = DidComm.Mediator.Tests.Pickup.DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("qa-pickup-gives-back");
        await world.QueueAsync(peer, seed: 1);
        world.Sender.Script.Enqueue(SendAnswer.Fail);
        (await world.PushAsync(peer)).Should().Be(DirectStep.NotDelivered);

        var now = world.Clock.GetUtcNow();
        var pickup = new MessageLease("qa-pickup-lease", now + world.LeaseFor);
        var taken = await world.Store.LeaseAsync(new MessageLeaseRequest(peer, null, 10, long.MaxValue, now, pickup), CancellationToken.None);
        taken.Should().ContainSingle().Which.PushTaken.Should().BeTrue();
        switch (how)
        {
            case "passed-over":
                (await world.Store.PassOverAsync(peer, new[] { taken[0].Id }, pickup.Id, now + TimeSpan.FromSeconds(1), CancellationToken.None)).Should().Be(1);
                break;
            case "released":
                (await world.Store.ReleaseAsync(peer, new[] { taken[0].Id }, pickup.Id, CancellationToken.None)).Should().Be(1);
                break;
            case "returned-unsent":
                (await world.Store.ReturnUnsentAsync(peer, new[] { taken[0].Id }, pickup.Id, CancellationToken.None)).Should().Be(1);
                break;
        }
        world.Clock.Advance(world.LeaseFor + TimeSpan.FromSeconds(1));

        (await world.PushAsync(peer)).Should().Be(DirectStep.Empty, "the direct push's mark outlives every give-back of a pickup ({0})", how);
        (await world.Coordinator.PushNextLiveAsync(peer, CancellationToken.None)).Should().Be(LiveStep.NoSession);
        world.Sender.Posts.Should().ContainSingle();
        (await world.PeekPickupAsync(peer)).Should().ContainSingle().Which.PushTaken.Should().BeTrue();
    }

    // ---- two push dispatchers (two mediator processes) over one store ----------------------------

    // Two dispatchers race on the same grant, woken together, every record refused or accepted:
    // never two POSTs of one record. Racy by nature, so run 50 times in a row inside the test.
    [Fact]
    public async Task Two_dispatchers_woken_together_over_one_store_never_post_a_record_twice()
    {
        for (var run = 0; run < 50; run++)
        {
            await using var direct = await DirectWorld.StartAsync();
            var grant = await direct.GrantAsync($"qa-two-dispatchers-{run}");
            for (byte seed = 40; seed < 48; seed++)
            {
                if (seed % 3 == 0)
                    direct.Sends.Fail(seed);
                await direct.Fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, seed));
            }
            await using var other = NewDirectDispatcher(direct);

            direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            other.Wake(grant.Peer).Should().BeTrue();
            await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
            await other.WaitForIdleAsync(Wait);
            // A refusal pauses the direct pushes of the dispatcher that met it; the rest go after the
            // pause (both share the clock). Two refusals: at most three rounds.
            for (var round = 0; round < 3 && direct.Fixture.Store.MessageCount > 2; round++)
            {
                direct.Fixture.Time.Advance(2 * new MediatorOptions().PushTimeout);
                direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
                other.Wake(grant.Peer).Should().BeTrue();
                await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
                await other.WaitForIdleAsync(Wait);
            }

            direct.Sends.Seeds.GroupBy(s => s).Should().OnlyContain(g => g.Count() == 1, "run {0}: no record is posted twice", run);
            direct.Sends.Seeds.OrderBy(s => s).Should().Equal(Enumerable.Range(40, 8).Select(i => (byte)i), "run {0}: every record is posted once (in either dispatcher's order)", run);
            direct.Fixture.Store.MessageCount.Should().Be(2, "run {0}: only the refused records (seeds 42 and 45) stay", run);
        }
    }

    // ---- records stored before direct push was turned on -------------------------------------------

    // A mediator without direct push stores messages: one pushed live (and returned unconfirmed by
    // the session's close), one picked up and never acknowledged, one untouched. Restarted over the
    // same stores with DirectPush on: only the untouched and the picked-up one are pushed, once
    // each; the live-pushed one stays the recipient's to ask for; nothing is posted twice across
    // further wake-ups and lease ends.
    [Fact]
    public async Task Records_stored_before_direct_push_was_turned_on_are_pushed_once_after_a_restart_with_it_on_and_a_live_pushed_one_never()
    {
        var store = new InMemoryMessageStore();
        var mediation = new InMemoryMediationStore(new CoordinateMediationOptions());
        void Shared(IServiceCollection s)
        {
            s.AddSingleton<IMessageStore>(store);
            s.AddSingleton<IMediationStore>(mediation);
        }

        LivePushGrant grant;
        StoredMessage live, picked;
        await using (var before = await LivePushWorld.StartAsync(services: Shared))
        {
            before.Fixture.Dispatcher.PushesWithoutLiveSession.Should().BeFalse("precondition: direct push is off here");
            grant = await before.GrantAsync("qa-before");
            var session = await before.LiveAsync(grant);
            live = await before.StoreAsync(grant, 1);
            before.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await before.Fixture.WaitForPushOutcomesAsync(1, Wait);
            await before.Fixture.Dispatcher.WaitForIdleAsync(Wait);
            LivePushWorld.Deliveries(session).Select(d => d.Id).Should().Equal(live.MessageId);
            session.Close();
            picked = await before.StoreAsync(grant, 2);
            // A delivery-request takes it (with whatever else no lease holds) and never acknowledges.
            (await before.PickupFromAsync(store, grant.Peer)).Should().Contain(picked.MessageId);
            await before.StoreAsync(grant, 3);
        }

        // The restarted mediator: a new container over the same stores, with direct push on, once
        // every lease of the first one (the live push's, the pickup's) has run out.
        await using var direct = await DirectWorld.StartAsync(services: Shared);
        direct.Endpoints.Set(grant.Peer, DirectWorld.EndpointFor("qa-before"));
        direct.Fixture.Time.Advance(TimeSpan.FromMinutes(2));

        for (var wake = 0; wake < 3; wake++)
        {
            direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
            direct.Fixture.Time.Advance(TimeSpan.FromMinutes(2));
        }

        direct.Sends.Seeds.Should().Equal(new[] { LivePushWorld.Seed(2), LivePushWorld.Seed(3) }, "the records no push took are pushed once each, in order; the live-pushed one never");
        direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedDirect, PushOutcomes.PushedDirect);
        (await direct.World.PickupFromAsync(store, grant.Peer)).Should().Equal(new[] { live.MessageId }, "the live-pushed record waits for a delivery-request");
    }

    // ---- more grants than MaxGrants over host stores ------------------------------------------------

    // The ADV-6 shape over a host message store as well as a host mediation store, with two refused
    // records at the head of every grant and every lease run out between the rounds (the slowest
    // honest caller outwaits every timer: L-058): each record posted at most once, every good one
    // delivered at the first wake-up, and nothing left in the dispatcher once it is idle.
    [Fact]
    public async Task More_direct_push_grants_than_MaxGrants_over_host_stores_post_each_record_once_and_leave_no_dispatcher_state()
    {
        var store = new ReferenceMessageStore();
        await using var direct = await DirectWorld.StartAsync(
            o =>
            {
                o.CoordinateMediation.MaxGrants = 2;
                o.PushConcurrency = 2;
            },
            s =>
            {
                s.AddSingleton<IMediationStore>(new ScriptedMediationStore());
                s.AddSingleton<IMessageStore>(store);
            });
        var fixture = direct.Fixture;
        var grants = new List<DirectGrant>();
        var refused = new List<string>();
        byte seed = 100;
        for (var g = 0; g < 10; g++)
        {
            var grant = await direct.GrantAsync($"qa-many-{g}");
            grants.Add(grant);
            for (var r = 0; r < 4; r++, seed++)
            {
                if (r < 2)
                    direct.Sends.Fail(seed);
                var stored = await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, seed));
                if (r < 2)
                    refused.Add(stored.MessageId);
            }
        }

        // Each refused record ends its grant's run and pauses its direct pushes: rounds 0 and 1 meet
        // the two refused records of every grant, round 2 delivers every good one, round 3 finds
        // nothing to push.
        for (var round = 0; round < 4; round++)
        {
            foreach (var grant in grants)
                fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            if (round < 3)
                await fixture.WaitForPushOutcomesAsync(round switch { 0 => 10, 1 => 20, _ => 40 }, Wait);
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
            if (round == 2)
                fixture.Pushes(PushOutcomes.PushedDirect).Should().Be(20, "every good record goes at the first wake-up after its grant's pauses");
            fixture.Time.Advance(TimeSpan.FromMinutes(2)); // every lease and every pause has run out
        }

        direct.Sends.Seeds.GroupBy(s => s).Should().OnlyContain(g => g.Count() == 1, "no record is posted twice");
        direct.Sends.Seeds.Should().HaveCount(40);
        fixture.Pushes(PushOutcomes.Failed).Should().Be(20);
        store.MessageCount.Should().Be(20, "only the refused records remain");
        fixture.Dispatcher.GrantEntries.Should().Be(0, "an idle dispatcher holds no grant");
        fixture.Dispatcher.RetainedCharacters.Should().Be(0);
        var picked = new List<string>();
        foreach (var grant in grants)
            picked.AddRange(await direct.World.PickupFromAsync(store, grant.Peer));
        picked.Should().BeEquivalentTo(refused, "pickup gets exactly the refused records");
    }

    // ---- ids ----------------------------------------------------------------------------------------

    // Forwards whose ids differ only by case are different messages: nothing on the direct path
    // correlates or de-duplicates by a wire id. Each is stored, posted once, and removed on its own.
    [Fact]
    public async Task Forwards_whose_ids_differ_only_by_case_are_each_posted_once()
    {
        await using var direct = await DirectWorld.StartAsync();
        var grant = await direct.GrantAsync("qa-case");
        var ids = new LetterIds("qa-case-forward");
        var lower = ids.NewId();
        var upper = lower.ToUpperInvariant();
        lower.Should().NotBe(upper);
        direct.Sends.Fail(51);

        await direct.ForwardAsync(grant.Recipient, PushFixture.Payload(64, 51), lower);
        await direct.Fixture.WaitForPushOutcomesAsync(1, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        direct.Fixture.Time.Advance(2 * new MediatorOptions().PushTimeout); // past the pause the refusal started
        await direct.ForwardAsync(grant.Recipient, PushFixture.Payload(64, 52), upper);
        await direct.Fixture.WaitForPushOutcomesAsync(2, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        direct.Fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.Queued);
        direct.Sends.Seeds.Should().Equal(new byte[] { 51, 52 });
        direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.PushedDirect);
        direct.Fixture.Store.MessageCount.Should().Be(1, "the refused one stays; the accepted one alone was removed");
    }

    // A host store that hands a push a record whose id is over the record-id cap or not made of
    // unreserved characters: refused before anything is sent, given back under the take's lease.
    [Theory]
    [InlineData("over-cap")]
    [InlineData("reserved")]
    [InlineData("empty")]
    public async Task A_host_store_record_with_an_unusable_id_is_refused_and_nothing_is_sent(string shape)
    {
        var bad = shape switch
        {
            "over-cap" => new string('a', 2049),
            "reserved" => "rec/with space",
            _ => string.Empty,
        };
        var store = new IdRewritingStore(new InMemoryMessageStore(), bad);
        await using var direct = await DirectWorld.StartAsync(services: s => s.AddSingleton<IMessageStore>(store));
        var grant = await direct.GrantAsync("qa-bad-id-" + shape);
        await direct.Fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 61));

        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Fixture.WaitForPushOutcomesAsync(1, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        direct.Sends.Sent.Should().BeEmpty("a record outside the store contract is never sent");
        direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        PipelineLogs.Leaking(direct.Fixture.Logs, bad.Length > 0 ? new[] { bad } : Array.Empty<string>()).Should().BeEmpty();
    }

    // The 512-character forward id cap through the whole direct path is pinned by the engineer test
    // R1_1_Forwards_waiting_for_a_held_direct_push_leave_nothing_of_their_wire_ids_behind.

    // ---- support -------------------------------------------------------------------------------------

    /// <summary>A second push dispatcher (another mediator process) over the same container's stores and coordinator, with the same direct-push route.</summary>
    private static PushDispatcher NewDirectDispatcher(DirectWorld direct)
        => new(
            direct.Fixture.Services.GetRequiredService<IOptions<MediatorOptions>>(),
            direct.Fixture.Queue,
            direct.Fixture.Services.GetRequiredService<MediatorMetrics>(),
            direct.Fixture.Services.GetService<ILogger<PushDispatcher>>(),
            direct.Fixture.Time,
            shutdownGrace: null,
            new DirectPushRoute(direct.Endpoints, new Lazy<IDirectPushSender>(() => direct.Sends)));

    /// <summary>
    /// Turn live delivery on over <paramref name="session"/> as its peer would (what
    /// <see cref="PushFixture.GoLiveAsync"/> does) without waiting for the push workers to be idle:
    /// it runs inside a push.
    /// </summary>
    private static async Task TurnLiveOnAsync(PushFixture fixture, IReturnRouteSession session)
    {
        fixture.Services.GetRequiredService<IReturnRouteSessionRegistry>().Register(session);
        var client = fixture.Services.GetRequiredService<DidCommClient>();
        var change = MessagePickup.CreateLiveDeliveryChange(session.PeerDid, session.LocalDid, liveDelivery: true);
        var packed = await client.PackEncryptedAsync(change, new PackEncryptedOptions(Recipients: new[] { session.LocalDid }, From: session.PeerDid));
        await fixture.Services.GetRequiredService<DidComm.Protocols.ProtocolDispatcher>().DispatchAsync(await client.UnpackAsync(packed.Message), client, fixture.Options, Task.CompletedTask, session, CancellationToken.None);
    }
}

/// <summary>
/// A host store with no column for the push mark: it keeps every other part of the contract (over
/// the store it wraps) but never sets, filters by, or returns the mark (plan residual 2). Records
/// the id of every record handed to a push lease.
/// </summary>
internal sealed class MarkDroppingStore(IMessageStore inner) : IMessageStore
{
    public ConcurrentQueue<string> PushTakes { get; } = new();

    public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct)
        => inner.EnqueueAsync(message, lease is null ? null : lease with { ForPush = false }, now, ct);

    public async ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct)
    {
        var taken = await inner.LeaseAsync(request with { Lease = request.Lease with { ForPush = false } }, ct);
        if (request.Lease.ForPush)
        {
            foreach (var record in taken)
                PushTakes.Enqueue(record.Id);
        }
        return taken;
    }

    public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct) => inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> ReturnUnsentAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => inner.ReturnUnsentAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct) => inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
    public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => inner.ReleaseLeasesAsync(leaseOwner, ct);
    public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => inner.PurgeGrantAsync(grantPeerDid, ct);
    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => inner.PurgeExpiredAsync(now, ct);
}

/// <summary>
/// A host store that judges by its own clock, <paramref name="lag"/> behind the request's
/// <c>Now</c> (outside the contract), until <see cref="CatchUpAfter"/> push takes have returned a
/// record; counts the push takes that returned records.
/// </summary>
internal sealed class LaggingClockStore(IMessageStore inner, TimeSpan lag) : IMessageStore
{
    private int _pushTakes;

    /// <summary>After this many push takes that returned a record, the store's clock agrees with the request's.</summary>
    public int CatchUpAfter { get; set; } = int.MaxValue;

    /// <summary>How many push takes returned a record.</summary>
    public int PushTakes => Volatile.Read(ref _pushTakes);

    public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct) => inner.EnqueueAsync(message, lease, now, ct);

    public async ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct)
    {
        var lagging = Volatile.Read(ref _pushTakes) < CatchUpAfter;
        var taken = await inner.LeaseAsync(lagging ? request with { Now = request.Now - lag } : request, ct);
        if (request.Lease.ForPush && taken.Count > 0)
            Interlocked.Increment(ref _pushTakes);
        return taken;
    }

    public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct) => inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> ReturnUnsentAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => inner.ReturnUnsentAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct) => inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
    public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => inner.ReleaseLeasesAsync(leaseOwner, ct);
    public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => inner.PurgeGrantAsync(grantPeerDid, ct);
    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => inner.PurgeExpiredAsync(now, ct);
}

/// <summary>A host store that hands every push lease its records under the id <paramref name="id"/> (outside the contract).</summary>
internal sealed class IdRewritingStore(IMessageStore inner, string id) : IMessageStore
{
    public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct) => inner.EnqueueAsync(message, lease, now, ct);

    public async ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct)
    {
        var taken = await inner.LeaseAsync(request, ct);
        return request.Lease.ForPush ? taken.Select(r => r with { Id = id }).ToList() : taken;
    }

    public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct) => inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> ReturnUnsentAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => inner.ReturnUnsentAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct) => inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
    public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => inner.ReleaseLeasesAsync(leaseOwner, ct);
    public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => inner.PurgeGrantAsync(grantPeerDid, ct);
    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => inner.PurgeExpiredAsync(now, ct);
}
