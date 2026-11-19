using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.Protocols.Routing;
using Xunit;

namespace DidComm.InteropTests.Adv98.D4;

/// <summary>
/// #98 D4 attack 9 / E-38: forwards received on one WebSocket connection are relayed in the order
/// they arrived (docs/codebase-architecture.md, routing core step 4). No baseline test pinned the
/// WebSocket half of that sentence.
/// </summary>
public sealed class D4RelayOrderTests
{
    [Fact]
    public async Task D4_Forwards_sent_on_one_mediator_WebSocket_are_stored_and_picked_up_in_the_order_they_were_sent()
    {
        const int count = 10; // one delivery batch (MaxDeliveryBatch default 10)
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var sent = new List<string>();
        var packed = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var note = alice.Note(bob.Did, $"n{i}");
            sent.Add(note.Id);
            packed.Add((await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(note, bob.Did))).Packed);
        }

        using var socket = await mediator.ConnectAsync();
        foreach (var envelope in packed)
            await ReturnRouteTestHost.SendAsync(socket, envelope);
        await MediatorTestServer.WaitUntilAsync(() => mediator.Outcome(ForwardOutcomes.Queued) == count, "every forward on the socket is relayed");
        await mediator.FlushAsync();

        var delivered = await mediator.FetchAsync(bob, limit: count);
        var order = new List<string>();
        foreach (var message in delivered)
            order.Add((await Qa.OpenAsync(bob, message.Payload)).Message.Id);
        order.Should().Equal(sent, "one connection relays in arrival order (E-38)");
    }
}
