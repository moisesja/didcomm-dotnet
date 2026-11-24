using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using DidComm.Transports;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// #110 end to end: a real <c>AddMediator(o =&gt; o.DirectPush = true)</c> + <c>MapDidCommMediator</c>
/// with the HTTP transport, the host's endpoint directory, and the recipients' own HTTP endpoints on
/// the test network. Alice sends with a forward over HTTP; the mediator stores it and, with no live
/// session for the recipient, POSTs the stored message to the endpoint the directory names; the
/// recipient opens it with its own keys. Every interleaving is forced with a gate (the endpoint, the
/// directory lookup) or the push workers' manual clock (L-044, L-065).
/// </summary>
public sealed class DirectPushEndToEndTests
{
    private static readonly TimeSpan PushTimeout = new MediatorOptions().PushTimeout;

    [Fact]
    public async Task A_forward_for_a_recipient_without_a_live_session_is_posted_to_its_registered_endpoint_and_removed()
    {
        await using var world = await DirectPushWorld.StartAsync();
        var (bob, b, endpoint) = await world.EnrolAsync("bob");
        var alice = await world.Mediator.NewSenderAsync();

        var note = await Qa.SendNoteAsync(world.Mediator, alice, b);
        await world.OutcomesAsync(PushOutcomes.PushedDirect, 1);

        world.Mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        world.Mediator.PushOutcomes.Should().Equal(PushOutcomes.PushedDirect);
        var body = endpoint.Bodies.Should().ContainSingle().Subject;
        endpoint.ContentTypes.Single().Should().StartWith(MediaTypes.Encrypted, "the stored JWE goes with the encrypted media type");
        (await MediatorRecipient.UnpackFromSenderAsync(b, body, alice)).Message.Id.Should().Be(note.Id, "the recipient opens Alice's message with its own keys");
        (await world.StoredAsync(bob.Did)).Should().Be(0, "a 2xx removes it");
        (await world.Mediator.StatusAsync(bob)).MessageCount.Should().Be(0);
        world.Mediator.LeakingLogLines(b.Did).Should().BeEmpty();
        MediatorLines(world).Should().NotContain(line => line.Contains(DirectPushInbox.Host, StringComparison.OrdinalIgnoreCase), "the mediator never logs an endpoint");
    }

    [Fact]
    public async Task A_failing_endpoint_leaves_its_message_for_pickup_a_second_recipient_is_pushed_meanwhile_and_no_later_push_posts_it_again()
    {
        await using var world = await DirectPushWorld.StartAsync();
        var (bob, b, bobEndpoint) = await world.EnrolAsync("bob");
        var (_, c, carolEndpoint) = await world.EnrolAsync("carol");
        bobEndpoint.Status = 500;
        var alice = await world.Mediator.NewSenderAsync();

        var refused = await Qa.SendNoteAsync(world.Mediator, alice, b);
        var forCarol = await Qa.SendNoteAsync(world.Mediator, alice, c);
        await world.OutcomesAsync(PushOutcomes.Failed, 1);
        await world.OutcomesAsync(PushOutcomes.PushedDirect, 1);

        bobEndpoint.Posts.Should().Be(1, "one attempt, no retry");
        (await MediatorRecipient.UnpackFromSenderAsync(c, carolEndpoint.Bodies.Single(), alice)).Message.Id.Should().Be(forCarol.Id);
        (await world.StoredAsync(bob.Did)).Should().Be(1, "the refused message stays queued");

        // Pickup is the later delivery (OD-2).
        var fetched = (await world.Mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(b, fetched.Payload, alice)).Message.Id.Should().Be(refused.Id);
        (await world.Mediator.AcknowledgeAsync(bob, fetched.Id)).MessageCount.Should().Be(0);

        // Before the pickup acknowledged it, no push could have taken it again; a later one, after the
        // pause the refusal started for Bob's direct pushes, posts only the new message.
        bobEndpoint.Status = 202;
        world.Time.Advance(2 * new MediatorOptions().PushTimeout);
        var later = await Qa.SendNoteAsync(world.Mediator, alice, b);
        await world.OutcomesAsync(PushOutcomes.PushedDirect, 2);
        bobEndpoint.Posts.Should().Be(2);
        (await MediatorRecipient.UnpackFromSenderAsync(b, bobEndpoint.Bodies[1], alice)).Message.Id.Should().Be(later.Id);
        world.Mediator.LeakingLogLines(b.Did, c.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task A_slow_endpoint_answering_under_the_deadline_delivers_and_a_second_recipient_is_pushed_meanwhile()
    {
        await using var world = await DirectPushWorld.StartAsync();
        var (bob, b, bobEndpoint) = await world.EnrolAsync("bob");
        var (_, c, carolEndpoint) = await world.EnrolAsync("carol");
        var held = bobEndpoint.Hold();
        var alice = await world.Mediator.NewSenderAsync();

        var forBob = await Qa.SendNoteAsync(world.Mediator, alice, b);
        await MediatorTestServer.WaitUntilAsync(() => bobEndpoint.Posts == 1, "Bob's POST reached his endpoint");
        var forCarol = await Qa.SendNoteAsync(world.Mediator, alice, c);
        await MediatorTestServer.WaitUntilAsync(() => world.Mediator.PushOutcome(PushOutcomes.PushedDirect) == 1, "Carol's message was delivered while Bob's POST is held");

        bobEndpoint.Ended.Should().Be(0, "Bob's POST is still held");
        (await MediatorRecipient.UnpackFromSenderAsync(c, carolEndpoint.Bodies.Single(), alice)).Message.Id.Should().Be(forCarol.Id);

        held.SetResult();
        await world.OutcomesAsync(PushOutcomes.PushedDirect, 2);
        world.Mediator.PushOutcomes.Should().Equal(PushOutcomes.PushedDirect, PushOutcomes.PushedDirect);
        (await MediatorRecipient.UnpackFromSenderAsync(b, bobEndpoint.Bodies.Single(), alice)).Message.Id.Should().Be(forBob.Id);
        (await world.StoredAsync(bob.Did)).Should().Be(0);
        bobEndpoint.MaxInFlight.Should().Be(1);
    }

    [Fact]
    public async Task A_tarpitting_endpoint_is_given_up_at_the_deadline_its_messages_wait_for_pickup_and_a_second_recipient_is_pushed_meanwhile()
    {
        await using var world = await DirectPushWorld.StartAsync();
        var (bob, b, bobEndpoint) = await world.EnrolAsync("bob");
        var (_, c, carolEndpoint) = await world.EnrolAsync("carol");
        bobEndpoint.Hold(); // never answers
        var alice = await world.Mediator.NewSenderAsync();

        var mark = world.Time.Mark();
        var trapped = await Qa.SendNoteAsync(world.Mediator, alice, b);
        await MediatorTestServer.WaitUntilAsync(() => bobEndpoint.Posts == 1, "Bob's POST reached his endpoint");
        await world.Time.WaitForTimerAsync(mark, PushTimeout, MediatorTestServer.Timeout);
        var forCarol = await Qa.SendNoteAsync(world.Mediator, alice, c);
        await MediatorTestServer.WaitUntilAsync(() => world.Mediator.PushOutcome(PushOutcomes.PushedDirect) == 1, "Carol's message was delivered while Bob's POST is held");
        (await MediatorRecipient.UnpackFromSenderAsync(c, carolEndpoint.Bodies.Single(), alice)).Message.Id.Should().Be(forCarol.Id);
        // Carol's operation has ended (its last, empty push makes a deadline timer of its own, due
        // with Bob's): the move fires Bob's deadline only (L-065 rule 3).
        await MediatorTestServer.WaitUntilAsync(() => world.Pushes.Unsettled == 1, "only Bob's push is still running");

        world.Time.Advance(PushTimeout);
        await world.OutcomesAsync(PushOutcomes.Failed, 1);
        world.Mediator.PushOutcomes.Should().Equal(new[] { PushOutcomes.PushedDirect, PushOutcomes.Failed }, "Bob's push failed at its deadline, and nothing else did");
        await MediatorTestServer.WaitUntilAsync(() => bobEndpoint.Ended == 1, "the abandoned POST was cancelled at its deadline");
        await MediatorTestServer.WaitUntilAsync(() => world.Pushes.Unsettled == 0, "the abandoned push settled");

        // Paused: the next message for Bob is not pushed, and waits for pickup with the first one.
        var waiting = await Qa.SendNoteAsync(world.Mediator, alice, b);
        await MediatorTestServer.WaitUntilAsync(() => world.Mediator.PushOutcome(PushOutcomes.SkippedCooldown) == 1, "Bob's grant is cooling down");
        bobEndpoint.Posts.Should().Be(1, "one POST of the first message; none of the second");
        var fetched = await world.Mediator.FetchAsync(bob);
        var ids = new List<string>();
        foreach (var message in fetched)
            ids.Add((await MediatorRecipient.UnpackFromSenderAsync(b, message.Payload, alice)).Message.Id);
        ids.Should().BeEquivalentTo(new[] { trapped.Id, waiting.Id }, "both wait for pickup; the timed-out one's attempt is spent");
        world.Mediator.LeakingLogLines(b.Did, c.Did).Should().BeEmpty();
    }

    // ---- what lands before, during and after a push -----------------------------------------------

    [Fact]
    public async Task A_revoke_and_purge_landing_before_the_take_leaves_nothing_to_post()
    {
        await using var world = await DirectPushWorld.StartAsync();
        var (bob, b, endpoint) = await world.EnrolAsync("bob");
        var alice = await world.Mediator.NewSenderAsync();
        var lookup = world.Endpoints.ParkNext(bob.Did);

        await Qa.SendNoteAsync(world.Mediator, alice, b);
        await lookup.Arrived.WaitAsync(MediatorTestServer.Timeout);
        await world.Mediator.Queue.RevokeAndPurgeAsync(bob.Did);
        lookup.Release();
        await world.Mediator.PushesIdleAsync();

        endpoint.Posts.Should().Be(0, "the push takes from the store at push time, and the purge emptied it");
        world.Mediator.PushOutcomes.Should().BeEmpty();
        (await world.StoredAsync(bob.Did)).Should().Be(0);
    }

    [Fact]
    public async Task A_revoke_and_purge_landing_during_the_POST_lets_it_complete_and_leaves_nothing_behind()
    {
        await using var world = await DirectPushWorld.StartAsync();
        var (bob, b, endpoint) = await world.EnrolAsync("bob");
        var held = endpoint.Hold();
        var alice = await world.Mediator.NewSenderAsync();

        var note = await Qa.SendNoteAsync(world.Mediator, alice, b);
        await MediatorTestServer.WaitUntilAsync(() => endpoint.Posts == 1, "the POST reached the endpoint");
        await world.Mediator.Queue.RevokeAndPurgeAsync(bob.Did);
        (await world.StoredAsync(bob.Did)).Should().Be(0);
        held.SetResult();
        await world.OutcomesAsync(PushOutcomes.PushedDirect, 1);

        (await MediatorRecipient.UnpackFromSenderAsync(b, endpoint.Bodies.Single(), alice)).Message.Id.Should().Be(note.Id, "the POST in flight completes (residual 4)");
        (await world.StoredAsync(bob.Did)).Should().Be(0);
        endpoint.Posts.Should().Be(1);
    }

    [Fact]
    public async Task A_message_that_expires_before_its_take_is_never_posted()
    {
        await using var world = await DirectPushWorld.StartAsync();
        var (bob, b, endpoint) = await world.EnrolAsync("bob");
        var alice = await world.Mediator.NewSenderAsync();
        var lookup = world.Endpoints.ParkNext(bob.Did);

        await Qa.SendNoteAsync(world.Mediator, alice, b, shape: Qa.Expires(world.Mediator.Clock.Now + TimeSpan.FromSeconds(10)));
        await lookup.Arrived.WaitAsync(MediatorTestServer.Timeout);
        world.Mediator.Clock.Advance(TimeSpan.FromSeconds(30));
        lookup.Release();
        await world.Mediator.PushesIdleAsync();

        endpoint.Posts.Should().Be(0);
        world.Mediator.PushOutcomes.Should().BeEmpty("an expired message is never handed to a push");
        (await world.Mediator.FetchAsync(bob)).Should().BeEmpty("nor to a pickup");
    }

    [Fact]
    public async Task A_delayed_message_is_not_posted_before_it_is_due_and_once_due_goes_with_the_grants_next_push()
    {
        await using var world = await DirectPushWorld.StartAsync();
        var (bob, b, endpoint) = await world.EnrolAsync("bob");
        var alice = await world.Mediator.NewSenderAsync();

        var delayed = await Qa.SendNoteAsync(world.Mediator, alice, b, shape: Qa.Delay(5000));
        await world.Mediator.PushesIdleAsync();
        endpoint.Posts.Should().Be(0, "not before it is due");
        world.Mediator.Clock.Advance(TimeSpan.FromSeconds(6));
        await world.Mediator.PushesIdleAsync();
        endpoint.Posts.Should().Be(0, "nothing schedules a due time (D2): it waits for the grant's next push or a pickup");
        world.Mediator.PushOutcomes.Should().BeEmpty();

        var next = await Qa.SendNoteAsync(world.Mediator, alice, b);
        await world.OutcomesAsync(PushOutcomes.PushedDirect, 2);

        var ids = new List<string>();
        foreach (var body in endpoint.Bodies)
            ids.Add((await MediatorRecipient.UnpackFromSenderAsync(b, body, alice)).Message.Id);
        ids.Should().Equal(new[] { delayed.Id, next.Id }, "the delayed one, due by now, goes first with the grant's next push");
        (await world.StoredAsync(bob.Did)).Should().Be(0);
    }

    [Fact]
    public async Task An_acknowledged_pickup_landing_before_the_take_leaves_nothing_to_post()
    {
        await using var world = await DirectPushWorld.StartAsync();
        var (bob, b, endpoint) = await world.EnrolAsync("bob");
        var alice = await world.Mediator.NewSenderAsync();
        var lookup = world.Endpoints.ParkNext(bob.Did);

        var note = await Qa.SendNoteAsync(world.Mediator, alice, b);
        await lookup.Arrived.WaitAsync(MediatorTestServer.Timeout);
        var fetched = (await world.Mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(b, fetched.Payload, alice)).Message.Id.Should().Be(note.Id);
        (await world.Mediator.AcknowledgeAsync(bob, fetched.Id)).MessageCount.Should().Be(0);
        lookup.Release();
        await world.Mediator.PushesIdleAsync();

        endpoint.Posts.Should().Be(0, "what was picked up and acknowledged is not there to take");
        world.Mediator.PushOutcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pickup_during_a_held_POST_gets_nothing_and_an_acknowledgement_landing_then_changes_nothing_for_the_push()
    {
        await using var world = await DirectPushWorld.StartAsync();
        var (bob, b, endpoint) = await world.EnrolAsync("bob");
        var held = endpoint.Hold();
        var alice = await world.Mediator.NewSenderAsync();

        var note = await Qa.SendNoteAsync(world.Mediator, alice, b);
        await MediatorTestServer.WaitUntilAsync(() => endpoint.Posts == 1, "the POST reached the endpoint");
        (await world.Mediator.FetchAsync(bob)).Should().BeEmpty("the message is leased to the push while its POST runs");
        var taken = world.Store.DirectTakes.Should().ContainSingle().Subject;
        (await world.Mediator.AcknowledgeAsync(bob, taken)).MessageCount.Should().Be(0, "an acknowledgement by id removes it at once");
        held.SetResult();
        await world.OutcomesAsync(PushOutcomes.PushedDirect, 1);

        (await MediatorRecipient.UnpackFromSenderAsync(b, endpoint.Bodies.Single(), alice)).Message.Id.Should().Be(note.Id);
        endpoint.Posts.Should().Be(1, "delivered once, by the push");
        (await world.StoredAsync(bob.Did)).Should().Be(0);
    }

    // ---- the delivery order, and a restart -----------------------------------------------------------

    [Fact]
    public async Task D6_live_first_then_direct_once_the_session_is_gone_and_nothing_is_delivered_twice()
    {
        await using var world = await DirectPushWorld.StartAsync();
        var (bob, b, endpoint) = await world.EnrolAsync("bob");
        var alice = await world.Mediator.NewSenderAsync();
        using var socket = await world.Mediator.ConnectAsync();
        await MediatorDeliveryModesQaTests.GoLiveAsync(world.Mediator, socket, bob, waitForLiveOnWakeUp: true);

        var first = await Qa.SendNoteAsync(world.Mediator, alice, b);
        var pushed = (await world.Mediator.ReceiveOverSocketAsync(socket, bob)).Delivery(thid: null).Should().ContainSingle().Subject;
        await world.OutcomesAsync(PushOutcomes.PushedLive, 1);
        (await MediatorDeliveryModesQaTests.AckOverSocketAsync(world.Mediator, socket, bob, pushed.Id)).MessageCount.Should().Be(0);
        endpoint.Posts.Should().Be(0, "a live session wins: nothing is posted while the recipient is live");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        await MediatorDeliveryModesQaTests.WaitForNoLiveSessionAsync(world.Mediator, bob.Did);

        var second = await Qa.SendNoteAsync(world.Mediator, alice, b);
        await world.OutcomesAsync(PushOutcomes.PushedDirect, 1);

        (await world.Mediator.FetchAsync(bob)).Should().BeEmpty("nothing is left for pickup");
        var received = new List<string>
        {
            (await MediatorRecipient.UnpackFromSenderAsync(b, pushed.Payload, alice)).Message.Id,
            (await MediatorRecipient.UnpackFromSenderAsync(b, endpoint.Bodies.Single(), alice)).Message.Id,
        };
        received.Should().Equal(new[] { first.Id, second.Id }, "each message reached the recipient once, live then direct");
        world.Mediator.PushOutcomes.Should().Equal(PushOutcomes.PushedLive, PushOutcomes.PushedDirect);
    }

    [Fact]
    public async Task After_a_restart_over_the_same_store_no_attempted_record_is_posted_again()
    {
        var network = new MediatorNetwork();
        await using var inbox = await DirectPushInbox.StartAsync(network);
        var store = new DirectTakeRecordingStore(new InMemoryMessageStore(new MessagePickupOptions()));
        var mediation = new InMemoryMediationStore(new CoordinateMediationOptions());
        var identity = await MediatorCast.MintAsync(new DidCommServiceSpec("http://mediator.test/", Accept: new[] { "didcomm/v2" }));
        var clock = new TestClock(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        await using var bob = await MediatorCast.MintPeerAsync();
        await using var b = await MediatorCast.MintRecipientAsync(identity.Did);
        await using var alice = await MediatorSender.CreateAsync(network, clock);
        var endpoint = inbox["/bob"];
        endpoint.Status = 500;
        void Same(MediatorTestServerOptions o)
        {
            o.Identity = identity;
            o.Clock = clock;
        }

        Message refused;
        await using (var first = await DirectPushWorld.StartAsync(Same, network, inbox, store, mediation))
        {
            first.Endpoints.Set(bob.Did, endpoint.Endpoint);
            await first.Mediator.EnrolAsync(bob, b.Did);
            refused = await Qa.SendNoteAsync(first.Mediator, alice, b);
            await first.OutcomesAsync(PushOutcomes.Failed, 1);
        }
        store.DirectTakes.Should().ContainSingle();
        var refusedRecord = store.DirectTakes.Single();

        endpoint.Status = 202;
        await using var second = await DirectPushWorld.StartAsync(Same, network, inbox, store, mediation);
        second.Endpoints.Set(bob.Did, endpoint.Endpoint);
        var next = await Qa.SendNoteAsync(second.Mediator, alice, b);
        await second.OutcomesAsync(PushOutcomes.PushedDirect, 1);

        second.Mediator.PushOutcomes.Should().Equal(new[] { PushOutcomes.PushedDirect }, "the restarted mediator takes only the new message");
        store.DirectTakes.Count(id => id == refusedRecord).Should().Be(1, "the attempted record is never taken by a push again, across the restart");
        endpoint.Posts.Should().Be(2, "one POST per message, over both runs");
        (await MediatorRecipient.UnpackFromSenderAsync(b, endpoint.Bodies[0], alice)).Message.Id.Should().Be(refused.Id);
        (await MediatorRecipient.UnpackFromSenderAsync(b, endpoint.Bodies[1], alice)).Message.Id.Should().Be(next.Id);
        var fetched = (await second.Mediator.FetchAsync(bob)).Should().ContainSingle("the refused message is the recipient's to pick up").Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(b, fetched.Payload, alice)).Message.Id.Should().Be(refused.Id);
    }

    private static IEnumerable<string> MediatorLines(DirectPushWorld world)
        => world.Mediator.Logs.CategorizedRecords.Where(r => r.Category.StartsWith("DidComm.Mediator", StringComparison.Ordinal)).Select(r => r.Text);
}
