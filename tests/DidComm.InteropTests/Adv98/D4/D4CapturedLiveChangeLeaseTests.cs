using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Protocols.Routing;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.InteropTests.Adv98.D4;

/// <summary>
/// #98 D4 attack 5 / E-28 extended (hypothesis 3). The documented limit says a captured, unsent
/// authcrypted <c>live-delivery-change</c> sent first on a fresh socket "holds the victim's live
/// session". This measures what else it holds: every message pushed to Mallory's socket is leased to
/// it for <see cref="MessagePickupOptions.LeaseTimeout"/>, so Bob's own HTTP pickup sees nothing of
/// it within the lease; and once a lease ends while Mallory still holds the socket, the next wake-up
/// pushes the record to Mallory again under a new lease.
/// </summary>
public sealed class D4CapturedLiveChangeLeaseTests(ITestOutputHelper output)
{
    [Fact]
    public async Task D4_Mallory_replaying_Bobs_live_change_keeps_Bobs_mail_leased_to_her_socket_and_releases_it_only_by_timeout_or_close()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var captured = await mediator.AuthcryptAsync(bob, mediator.Prepare(MessagePickup.CreateLiveDeliveryChange(bob.Did, mediator.Did, true)));

        // Mallory sends Bob's captured change first, on her own fresh socket, and never acknowledges.
        using var socket = await mediator.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, captured);
        (await ReturnRouteTestHost.ReceiveAsync(socket, MediatorTestServer.Timeout)).Type.Should().Be(WebSocketMessageType.Text, "the status for Bob, which Mallory cannot read");

        await Qa.SendNoteAsync(mediator, alice, bob);
        await MediatorTestServer.WaitUntilAsync(() => mediator.PushOutcome(PushOutcomes.PushedLive) == 1, "the first message is pushed to Mallory's socket");
        (await ReturnRouteTestHost.ReceiveAsync(socket, MediatorTestServer.Timeout)).Type.Should().Be(WebSocketMessageType.Text);
        var withinLease = (await mediator.FetchAsync(bob)).Count;

        // The lease runs out while Mallory still holds the socket; Bob does not poll in between, and
        // a second message arrives (the grant's wake-up).
        mediator.Clock.Advance(new MessagePickupOptions().LeaseTimeout + TimeSpan.FromSeconds(1));
        await Qa.SendNoteAsync(mediator, alice, bob);
        await mediator.PushesIdleAsync();
        var pushedAfterExpiry = mediator.PushOutcome(PushOutcomes.PushedLive);
        for (var i = 1; i < pushedAfterExpiry; i++)
            (await ReturnRouteTestHost.ReceiveAsync(socket, MediatorTestServer.Timeout)).Type.Should().Be(WebSocketMessageType.Text);
        var afterExpiry = (await mediator.FetchAsync(bob)).Count;
        output.WriteLine($"D4 E-28: Bob fetched {withinLease} within the lease; pushes to Mallory after expiry+arrival {pushedAfterExpiry} (outcomes [{string.Join(",", mediator.PushOutcomes)}]); Bob fetched {afterExpiry} after");

        withinLease.Should().Be(0, "the pushed record is leased to the session Mallory holds");
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.Queued);
        // Pins today's behaviour: the second arrival re-pushes the expired record to Mallory too
        // (3 pushes: first, then first again and second), so Bob's poll right after finds nothing.
        pushedAfterExpiry.Should().Be(3, "the wake-up pushes the expired record again and the new one, both to Mallory");
        afterExpiry.Should().Be(0, "every record is leased to Mallory's socket again");

        // Closing Mallory's socket gives the leases back in the background. Poll Bob's own pickup,
        // the record asserted below, until both come back; the monotonic clock is only a hang guard.
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        IReadOnlyList<DeliveredMessage> again;
        while ((again = await mediator.FetchAsync(bob)).Count < 2 && System.Diagnostics.Stopwatch.GetElapsedTime(started) < MediatorTestServer.Timeout)
            await Task.Delay(20);
        again.Should().HaveCount(2, "closing Mallory's socket gives every lease back");
    }
}
