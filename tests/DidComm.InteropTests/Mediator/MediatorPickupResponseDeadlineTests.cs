using System.Net;
using System.Text;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// #98 FX-1, adversarial finding D3-F2. A client that never reads held the return-route 200 of a
/// <c>delivery-request</c> (a 2 KB request, a response of up to <c>MaxDeliveryBytes</c>) for as long as
/// Kestrel's minimum data rate allowed, and once the pickup lease (<c>LeaseTimeout</c>, 60 s) ran out
/// the same records were served into another held response, so copies piled up. Now the mediator's
/// HTTP endpoint aborts a 200 that is not committed within the lease, counted from when its dispatch
/// began, on the container's <see cref="TimeProvider"/>: at most one copy of a leased record is held at
/// a time, and a record whose response was aborted is served again, never lost. The responses are held
/// at <c>OnStarting</c> (<see cref="MediatorRequestControls.ResponseGate"/>); both clocks — the
/// mediator's <c>DidCommOptions.Clock</c>, which times the lease, and the <see cref="TimeProvider"/>,
/// which times the deadline — move only by hand and together, the timer clock first.
/// </summary>
public sealed class MediatorPickupResponseDeadlineTests
{
    private static readonly TimeSpan Lease = new MessagePickupOptions().LeaseTimeout;
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    /// <summary>A delivery of the three 16 KiB notes is far above this; a <c>status</c> far below.</summary>
    private const int DeliveryBytes = 32 * 1024;

    [Fact]
    public async Task A_delivery_a_client_never_reads_is_aborted_at_the_lease_and_served_again_afterwards_never_two_held_at_once()
    {
        var time = new QaManualTimeProvider();
        var deliveries = new DeadlineResponseTracker(HttpMethods.Post, DeliveryBytes);
        await using var mediator = await MediatorAcknowledgementDeadlineTests.StartAsync(time, deliveries);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var sent = new List<string>();
        for (var i = 0; i < 3; i++)
            sent.Add((await Qa.SendNoteAsync(mediator, alice, bob, content: new string((char)('a' + i), 16 * 1024))).Id);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Held unread: the records are leased to it.
            mediator.Controls.ResponseGate = gate;
            var first = Task.Run(() => RequestDeliveryAsync(mediator, bob));
            await MediatorTestServer.WaitUntilAsync(() => deliveries.Started == 1, "the first delivery is held unread");
            mediator.Controls.ResponseGate = null;
            Advance(mediator, time, Lease - Tick);
            var withinLease = await mediator.FetchAsync(bob);
            var abortedWithinLease = deliveries.Aborted;

            // At the lease: aborted, as the lease runs out.
            Advance(mediator, time, Tick);
            await MediatorTestServer.WaitUntilAsync(() => deliveries.Aborted == 1, "the held delivery is aborted at the lease");
            var firstFailure = await Record.ExceptionAsync(() => first.WaitAsync(MediatorTestServer.Timeout));

            // Served once again, into a second held response: the first is gone by then.
            mediator.Controls.ResponseGate = gate;
            var second = Task.Run(() => RequestDeliveryAsync(mediator, bob));
            await MediatorTestServer.WaitUntilAsync(() => deliveries.Started == 2, "the records are served again");
            var heldWhenServedAgain = deliveries.Held;
            mediator.Controls.ResponseGate = null;
            Advance(mediator, time, Lease);
            await MediatorTestServer.WaitUntilAsync(() => deliveries.Aborted == 2, "the second held delivery is aborted at its lease");
            var secondFailure = await Record.ExceptionAsync(() => second.WaitAsync(MediatorTestServer.Timeout));

            // A client that reads gets every message once; the aborts lost nothing.
            var delivered = await mediator.FetchAsync(bob);
            var opened = new List<string>();
            foreach (var item in delivered)
                opened.Add((await MediatorRecipient.UnpackFromSenderAsync(bob, item.Payload, alice)).Message.Id);
            var status = await mediator.AcknowledgeAsync(bob, delivered.Select(d => d.Id).ToArray());

            withinLease.Should().BeEmpty("within the lease the held response has the records");
            abortedWithinLease.Should().Be(0, "one tick before the lease nothing is aborted");
            firstFailure.Should().NotBeNull().And.NotBeOfType<TimeoutException>("a client whose 200 was not committed never receives it");
            secondFailure.Should().NotBeNull().And.NotBeOfType<TimeoutException>();
            heldWhenServedAgain.Should().Be(1, "the second copy is served only after the first was aborted");
            deliveries.MaxHeld.Should().Be(1, "never two held copies at once");
            opened.Should().Equal(sent, "every message is delivered once, in arrival order");
            status.MessageCount.Should().Be(0);
            mediator.LeakingLogLines(delivered.Select(d => d.Id).ToArray()).Should().BeEmpty();
        }
        finally
        {
            mediator.Controls.ResponseGate = null;
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_delivery_read_one_tick_before_the_lease_is_kept_and_its_deadline_is_gone()
    {
        var time = new QaManualTimeProvider();
        var deliveries = new DeadlineResponseTracker(HttpMethods.Post, DeliveryBytes);
        await using var mediator = await MediatorAcknowledgementDeadlineTests.StartAsync(time, deliveries);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var sent = new List<string>();
        for (var i = 0; i < 3; i++)
            sent.Add((await Qa.SendNoteAsync(mediator, alice, bob, content: new string((char)('a' + i), 16 * 1024))).Id);
        var timers = time.ScheduledTimers;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.Controls.ResponseGate = gate;
        try
        {
            var request = MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
            var post = Task.Run(() => PostAsync(mediator, bob, request));
            await MediatorTestServer.WaitUntilAsync(() => deliveries.Started == 1, "the delivery is held unread");
            time.ScheduledTimers.Should().Be(timers + 1, "the held 200 has its deadline");

            Advance(mediator, time, Lease - Tick);
            gate.SetResult(); // the client reads its delivery one tick before the lease
            var response = await post.WaitAsync(MediatorTestServer.Timeout);
            await MediatorTestServer.WaitUntilAsync(() => deliveries.Completed == 1, "the 200 is committed");
            await MediatorTestServer.WaitUntilAsync(() => time.ScheduledTimers == timers, "the commit disposes the deadline");
            Advance(mediator, time, Lease); // far past where the deadline was
            var unpacked = await bob.Client.UnpackAsync(response.Body);
            var delivered = new PickupAnswer(unpacked.Message, Encoding.UTF8.GetBytes(response.Body)).Delivery(request.Id);
            var opened = new List<string>();
            foreach (var item in delivered)
                opened.Add((await MediatorRecipient.UnpackFromSenderAsync(bob, item.Payload, alice)).Message.Id);
            var status = await mediator.AcknowledgeAsync(bob, delivered.Select(d => d.Id).ToArray());

            response.Status.Should().Be(HttpStatusCode.OK);
            response.ContentType.Should().Be(MediaTypes.Encrypted);
            deliveries.Aborted.Should().Be(0, "completion wins: nothing is aborted after the commit");
            opened.Should().Equal(sent);
            status.MessageCount.Should().Be(0, "the delivery that was read is acknowledged and gone");
        }
        finally
        {
            mediator.Controls.ResponseGate = null;
            gate.TrySetResult();
        }
    }

    /// <summary>Move the deadline's clock, then the lease's, by <paramref name="by"/>.</summary>
    private static void Advance(MediatorTestServer mediator, QaManualTimeProvider time, TimeSpan by)
    {
        time.Advance(by);
        mediator.Clock.Advance(by);
    }

    private static Task<(HttpStatusCode Status, string? ContentType, string Body)> RequestDeliveryAsync(MediatorTestServer mediator, TestPeer peer)
        => PostAsync(mediator, peer, MessagePickup.CreateDeliveryRequest(peer.Did, mediator.Did, 10));

    /// <summary>Stamp <paramref name="request"/> (created_time, <c>return_route: all</c>), authcrypt it from <paramref name="peer"/> and POST it.</summary>
    private static async Task<(HttpStatusCode Status, string? ContentType, string Body)> PostAsync(MediatorTestServer mediator, TestPeer peer, Message request)
        => await mediator.PostAsync(await mediator.AuthcryptAsync(peer, mediator.Prepare(request)));
}
