using DidComm.Mediator;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
using DidComm.Samples.Shared;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// QA (#110, direct push) end to end: two mediator processes of one deployment (same routing DID,
/// same clock) running at the same time over one message store and one mediation store, the
/// recipients' endpoints on the test network. What is measured is POSTs per stored message at the
/// recipient's endpoint.
/// </summary>
public sealed class DirectPushStateQaTests
{
    // Every message refused by the endpoint, both processes woken for every message (the forward
    // wakes the one it reached; the test wakes the other), more wake-ups after every lease has run
    // out and once the endpoint accepts again: each message is POSTed once across both processes,
    // and every one is the recipient's to pick up.
    [Fact]
    public async Task Two_mediator_processes_running_at_once_over_one_store_post_each_message_once()
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

        await using var first = await DirectPushWorld.StartAsync(o =>
        {
            o.Identity = identity;
            o.Clock = clock;
        }, network, inbox, store, mediation);
        await using var second = await DirectPushWorld.StartAsync(o =>
        {
            o.Identity = identity;
            o.Clock = clock;
            o.Host = "m2.test";
        }, network, inbox, store, mediation);
        first.Endpoints.Set(bob.Did, endpoint.Endpoint);
        second.Endpoints.Set(bob.Did, endpoint.Endpoint);
        await first.Mediator.EnrolAsync(bob, b.Did);

        long Failed() => first.Mediator.PushOutcome(PushOutcomes.Failed) + second.Mediator.PushOutcome(PushOutcomes.Failed);
        var pause = 2 * new MediatorOptions().PushTimeout;
        var sent = new List<Message>();
        for (var i = 0; i < 4; i++)
        {
            sent.Add(await Qa.SendNoteAsync(first.Mediator, alice, b));
            second.Pushes.Wake(bob.Did).Should().BeTrue();
        }
        // Each refusal pauses that process's direct pushes to Bob: the messages are attempted one per
        // pause and process, each once across both.
        for (var round = 0; round < 4; round++)
        {
            await first.Mediator.PushesIdleAsync();
            await second.Mediator.PushesIdleAsync();
            if (Failed() >= 4)
                break;
            first.Time.Advance(pause);
            second.Time.Advance(pause);
            first.Pushes.Wake(bob.Did).Should().BeTrue();
            second.Pushes.Wake(bob.Did).Should().BeTrue();
        }
        await MediatorTestServer.WaitUntilAsync(() => Failed() >= 4, "every message was attempted once");
        await first.Mediator.PushesIdleAsync();
        await second.Mediator.PushesIdleAsync();
        endpoint.Posts.Should().Be(4, "one POST per message across both processes");

        // Every lease and every pause has run out; the endpoint accepts again; both processes are woken again.
        clock.Advance(TimeSpan.FromMinutes(5));
        first.Time.Advance(pause);
        second.Time.Advance(pause);
        endpoint.Status = 202;
        for (var wake = 0; wake < 3; wake++)
        {
            first.Pushes.Wake(bob.Did).Should().BeTrue();
            second.Pushes.Wake(bob.Did).Should().BeTrue();
            await first.Mediator.PushesIdleAsync();
            await second.Mediator.PushesIdleAsync();
        }

        endpoint.Posts.Should().Be(4, "an attempted message is never posted again, by either process");
        store.DirectTakes.GroupBy(id => id).Should().OnlyContain(g => g.Count() == 1, "each record was handed to a direct push once");
        var posted = new List<string>();
        foreach (var body in endpoint.Bodies)
            posted.Add((await MediatorRecipient.UnpackFromSenderAsync(b, body, alice)).Message.Id);
        posted.Should().BeEquivalentTo(sent.Select(m => m.Id), "each message was posted once");
        var fetched = await first.Mediator.FetchAsync(bob);
        fetched.Should().HaveCount(4, "the refused messages are the recipient's to pick up");
        (first.Mediator.PushOutcome(PushOutcomes.PushedDirect) + second.Mediator.PushOutcome(PushOutcomes.PushedDirect)).Should().Be(0);
    }

    // QA-110-2 end to end. Bob has no live session when Alice's first note arrives, so its push goes
    // direct and asks the host's directory (held here). Meanwhile Bob connects and turns live
    // delivery on: the status that answers counts the first note, which is his to pick up
    // (FR-PICK-06); a second note arrives while he is live. Before the fix, once the lookup answered,
    // the direct push took the first note and POSTed it, although Bob was live (QA1-2). Now it looks for
    // a live session right before its take, after the lookup: Bob wins and nothing is POSTed.
    [Fact]
    public async Task QA2_A_recipient_who_turns_live_delivery_on_while_the_directory_answers_gets_nothing_posted()
    {
        await using var world = await DirectPushWorld.StartAsync();
        var (bob, b, endpoint) = await world.EnrolAsync("bob");
        var alice = await world.Mediator.NewSenderAsync();
        var lookup = world.Endpoints.ParkNext(bob.Did);

        var counted = await Qa.SendNoteAsync(world.Mediator, alice, b);
        await lookup.Arrived.WaitAsync(MediatorTestServer.Timeout);
        using var socket = await world.Mediator.ConnectAsync();
        await MediatorDeliveryModesQaTests.GoLiveAsync(world.Mediator, socket, bob);
        var live = await Qa.SendNoteAsync(world.Mediator, alice, b);
        lookup.Release();
        var pushed = (await world.Mediator.ReceiveOverSocketAsync(socket, bob)).Delivery(thid: null).Should().ContainSingle().Subject;
        await world.Mediator.PushesIdleAsync();

        (await MediatorRecipient.UnpackFromSenderAsync(b, pushed.Payload, alice)).Message.Id.Should().Be(live.Id, "what arrived after live-on goes live");
        endpoint.Posts.Should().Be(0, "a live session wins: nothing is POSTed while the recipient is live");
        var fetched = (await world.Mediator.FetchAsync(bob)).Should().ContainSingle("what the live-on status counted is the recipient's to pick up").Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(b, fetched.Payload, alice)).Message.Id.Should().Be(counted.Id);
    }
}
