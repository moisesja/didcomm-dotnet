using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Protocols.Routing;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// QA-2 for #100 on the wire, against the real <c>AddMediator()</c> + <c>MapDidCommMediator</c> host
/// with hand-rolled Message Pickup 4.0 requests: live mode refused on a connection incapable of it
/// (4.0 L306, L342-L369) — over HTTP on a thread of its own (the report's <c>pthid</c> is that thread)
/// and on a WebSocket session downgraded by <c>return_route: none</c> (no answer can travel there, and
/// nothing changes) — and a 4.0 live session that never acknowledges: what it can hold is bounded by
/// the session's push bound and the grant's quota throughout, and its close gives every push back to
/// the queue (L308). And QA2-Q1 as the owner ruled it: a push whose lease ran out is not pushed live
/// again; it waits for a delivery-request (L304).
/// </summary>
public sealed class PickupV4LiveWireQa2Tests
{
    // ---- 5. live mode on a connection incapable of it --------------------------------------------

    [Fact]
    public async Task Qa2_Live_delivery_asked_for_over_HTTP_on_a_thread_gets_the_4_0_problem_report_whose_pthid_is_that_thread_and_changes_nothing()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);

        var request = MessagePickup40.CreateLiveDeliveryChange(bob.Did, mediator.Did, liveDelivery: true);
        request.Thid = "qa2-thread-" + Guid.NewGuid().ToString("N");
        var answer = await mediator.AskAsync(bob, request); // asserts the 200 and the encrypted body
        var (code, comment) = answer.Problem40(pthid: request.Thid);

        code.Should().Be("e.m.live-mode-not-supported");
        comment.Should().Be("Connection does not support Live Delivery");
        answer.Message.Thid.Should().BeNull("the report names the thread of the request as pthid (L359-L369; DD-11: thid, else id)");
        (await PickupV4E2E.Status40Async(mediator, bob)).LiveDelivery.Should().BeFalse("nothing was turned on");
    }

    [Theory]
    [InlineData("header-less")]
    [InlineData("none")]
    public async Task Qa2_A_4_0_live_delivery_change_on_a_WebSocket_session_downgraded_by_none_is_not_applied_and_nothing_is_written_for_it(string shape)
    {
        // After return_route: none (FR-RR-05) the session can carry neither the status L340 asks for
        // nor the problem report L342 suggests, nor a push: it is a connection incapable of live
        // delivery for as long as it stays downgraded (the 4.0 counterpart of #98 D4).
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        using var socket = await mediator.ConnectAsync();
        var opened = MessagePickup40.CreateStatusRequest(bob.Did, mediator.Did);
        (await mediator.AskOverSocketAsync(socket, bob, opened)).Status40(opened.Id).LiveDelivery.Should().BeFalse();
        var session = mediator.App.Services.GetRequiredService<IReturnRouteSessionRegistry>().Find(bob.Did).Should().ContainSingle().Subject;

        var none = PickupV4E2E.Stamped(mediator, MessagePickup40.CreateStatusRequest(bob.Did, mediator.Did));
        ReturnRouteHeaderWriter.Write(none, ReturnRouteMode.None, thread: null);
        await PickupV4E2E.SendAsync(socket, await PickupV4E2E.PackAsIsAsync(mediator, bob, none));
        await MediatorTestServer.WaitUntilAsync(() => session.Mode == ReturnRouteMode.None, "the session is downgraded");

        var change = PickupV4E2E.Stamped(mediator, MessagePickup40.CreateLiveDeliveryChange(bob.Did, mediator.Did, liveDelivery: true));
        if (shape == "none")
            ReturnRouteHeaderWriter.Write(change, ReturnRouteMode.None, thread: null);
        await PickupV4E2E.SendAsync(socket, await PickupV4E2E.PackAsIsAsync(mediator, bob, change));
        var during = await Qa.SendNoteAsync(mediator, alice, bob, content: "while downgraded");
        await mediator.PushesIdleAsync();

        // return_route: all again. The mediator reads the socket in order and writes on it first in,
        // first out, so the first frame back answers this request: nothing was written for the change.
        var status = MessagePickup40.CreateStatusRequest(bob.Did, mediator.Did);
        var after = (await mediator.AskOverSocketAsync(socket, bob, status)).Status40(status.Id);
        after.LiveDelivery.Should().BeFalse("the change arrived on a connection that could carry neither its answer nor a push");
        after.MessageCount.Should().Be(1);

        // Live delivery stayed off: the next arrival is queued, and the next frame is a delivery-request's answer.
        var later = await Qa.SendNoteAsync(mediator, alice, bob, content: "after all again");
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(0);
        var fetched = await PickupV4Qa2.Fetch40OverSocketAsync(mediator, socket, bob);
        (await PickupV4Qa2.InnerIdsAsync(bob, fetched)).Should().Equal(during.Id, later.Id);
        (await PickupV4Qa2.Ack40OverSocketAsync(mediator, socket, bob, fetched.Select(m => m.Id).ToArray())).MessageCount.Should().Be(0);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ---- 10. a live session that never acknowledges ------------------------------------------------

    [Fact]
    public async Task Qa2_A_4_0_live_session_that_never_acknowledges_holds_at_most_its_push_bound_and_its_quota_and_its_close_gives_every_push_back()
    {
        // A peer that turned live mode on in 4.0 and then neither reads nor acknowledges. TestHost's
        // socket never makes a send wait (#119), so the mediator's side is held (GatedSockets): its
        // writer stays in the first push's send. Throughout, the session holds at most one frame in
        // the send and MaxPendingPushes queued, and the store at most the grant's quota; every push
        // the session took stays leased to it. When the peer goes, its session's close gives every
        // push back to the queue (L308): a 4.0 delivery-request on a new connection gets all of it.
        const int MaxPendingPushes = 2;
        const int Quota = 6;
        const int Sent = 9;
        const int Live = 1 + MaxPendingPushes;
        var sockets = new GatedSockets();
        Qa2LiveStore store = null!;
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Mediator = m =>
            {
                m.Sessions.MaxPendingPushes = MaxPendingPushes;
                m.Pickup.MaxQueueMessages = Quota;
            };
            o.ConfigurePipeline = sockets.Install;
            o.ConfigureServices = s => Qa2LiveStore.Register(s, x => store = x);
        });
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var socket = await mediator.ConnectAsync();
        await PickupV4Qa2.GoLive40Async(mediator, socket, bob);
        var server = await sockets.AcceptedAsync(0);
        server.Hold();

        var sent = new List<string> { (await Qa.SendNoteAsync(mediator, alice, bob, content: "m0")).Id };
        await MediatorTestServer.WaitUntilAsync(() => server.SendsWaiting == 1, "m0's frame is in the mediator's send");
        await mediator.PushesIdleAsync();
        for (var i = 1; i < Sent; i++)
        {
            sent.Add((await Qa.SendNoteAsync(mediator, alice, bob, content: $"m{i}")).Id);
            await mediator.PushesIdleAsync();
            mediator.PushOutcome(PushOutcomes.PushedLive).Should().BeLessThanOrEqualTo(Live, "the session takes one frame into its send and MaxPendingPushes more, never more");
            Qa2.MessageCount(mediator).Should().BeLessThanOrEqualTo(Quota, "the store holds at most the grant's quota");
            server.SendsWaiting.Should().Be(1, "nothing more was written while the peer did not read");
        }

        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(Live);
        mediator.Outcome(ForwardOutcomes.Queued).Should().Be(Quota);
        mediator.Outcome(ForwardOutcomes.DroppedQuota).Should().Be(Sent - Quota, "past the quota nothing more is kept");
        Qa2.MessageCount(mediator).Should().Be(Quota);
        store.OwnerReleases.Should().BeEmpty("precondition: the session is open, and its pushes are leased to it");

        // The peer goes away without acknowledging anything.
        socket.Abort();
        socket.Dispose();
        await MediatorTestServer.WaitUntilAsync(() => store.OwnerReleases.Count == 1, "the mediator saw the session close and gave its leases back");
        store.OwnerReleases.Should().Equal(new[] { Live }, "every push the session took went back to the queue (L308)");

        using var again = await mediator.ConnectAsync();
        var fetched = await PickupV4Qa2.Fetch40OverSocketAsync(mediator, again, bob);
        (await PickupV4Qa2.InnerIdsAsync(bob, fetched)).Should().Equal(sent.Take(Quota), "everything kept, in arrival order, the pushed ones included");
        (await PickupV4Qa2.Ack40OverSocketAsync(mediator, again, bob, fetched.Select(m => m.Id).ToArray())).MessageCount.Should().Be(0);
        Qa2.MessageCount(mediator).Should().Be(0);
        await again.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ---- QA2-Q1, as the owner ruled it ----------------------------------------------------------------

    [Fact]
    public async Task Qa2_Q1_A_live_push_whose_lease_ran_out_unacknowledged_is_not_pushed_live_again_and_waits_for_a_delivery_request()
    {
        // QA-2 reported this reading as QA2-Q1: the mediator pushed, at each wake-up of the grant, the
        // oldest deliverable message that arrived after the session went live — including one whose
        // earlier push was never acknowledged and whose lease ran out, so m1 reached the live
        // connection again as a push. The owner ruled (2026-11-20, #88, "Never push it live again"): a
        // message pushed live and returned unconfirmed waits until the recipient asks for it, in 3.0
        // and 4.0 alike. 4.0 L304: "Messages already in the queue are not affected by Live Mode; they
        // MUST still be requested with delivery-request messages." L308: "If a message is not
        // acknowledged, the message MUST be added to the queue for later pickup." So at m2's wake-up
        // only m2 is pushed, and a delivery-request gets m1.
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        using var socket = await mediator.ConnectAsync();
        await PickupV4Qa2.GoLive40Async(mediator, socket, bob);

        var m1 = await Qa.SendNoteAsync(mediator, alice, bob, content: "m1");
        var pushed = (await mediator.ReceiveOverSocketAsync(socket, bob)).Delivery40(thid: null).Should().ContainSingle().Subject;
        await mediator.PushesIdleAsync();
        mediator.Clock.Advance(new MessagePickupOptions().LeaseTimeout + TimeSpan.FromSeconds(1)); // never acknowledged: back in the queue (L308)

        var m2 = await Qa.SendNoteAsync(mediator, alice, bob, content: "m2");
        var next = (await mediator.ReceiveOverSocketAsync(socket, bob)).Delivery40(thid: null).Should().ContainSingle().Subject;
        await mediator.PushesIdleAsync();

        (await PickupV4Qa2.InnerIdsAsync(bob, new[] { next })).Should().Equal(new[] { m2.Id }, "only the newly arriving message is pushed (L302); the returned m1 is not pushed again");
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(2);
        var fetched = await PickupV4Qa2.Fetch40OverSocketAsync(mediator, socket, bob);
        fetched.Select(m => m.Id).Should().Equal(new[] { pushed.Id }, "m1 must be requested with a delivery-request (L304)");
        (await PickupV4Qa2.InnerIdsAsync(bob, fetched)).Should().Equal(m1.Id);
        (await PickupV4Qa2.Ack40OverSocketAsync(mediator, socket, bob, pushed.Id, next.Id)).MessageCount.Should().Be(0);
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(2);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }
}
