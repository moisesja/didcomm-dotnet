using System.Net;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Pickup;
using Xunit;
using static DidComm.InteropTests.Mediator.MediatorOwnMessageThroughRewrapTests;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// #110 red team pass 2 (OD-6): a pickup request the mediator finds inside a forward addressed to
/// itself has no session (return_route.md L9), so it must neither lease nor turn live delivery on:
/// the recipient's messages stay where pickup finds them, and nothing is written on the connection
/// it arrived on. These pass on the code under test; they record what was attacked.
/// </summary>
public sealed class MediatorOpenedPickupAdv110Pass2Tests
{
    [Theory]
    [InlineData(Transport.Http)]
    [InlineData(Transport.WebSocket)]
    public async Task An_opened_delivery_request_leases_nothing(Transport transport)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        await Qa.SendNoteAsync(mediator, alice, bob);
        mediator.Store.MessageCount.Should().Be(1);

        var request = mediator.Prepare(MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10));
        var wrapped = await WrapAsync(mediator, Wrapper.HandBuilt, await mediator.AuthcryptAsync(bob, request));
        await DeliverAndWaitForTheAnswerAsync(mediator, transport, wrapped);

        (await mediator.FetchAsync(bob)).Should().ContainSingle("the opened request had no session: nothing was leased, so pickup still finds the message");
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task An_opened_live_delivery_change_turns_nothing_on_for_the_socket_it_arrived_on()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();

        var change = mediator.Prepare(MessagePickup.CreateLiveDeliveryChange(bob.Did, mediator.Did, true));
        var wrapped = await WrapAsync(mediator, Wrapper.HandBuilt, await mediator.AuthcryptAsync(bob, change));
        using var socket = await mediator.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, wrapped);
        await MediatorTestServer.WaitUntilAsync(() => AnswerCount(mediator) > 0, "the opened change was handled");

        await Qa.SendNoteAsync(mediator, alice, bob);

        mediator.Store.MessageCount.Should().Be(1, "nothing was pushed live: the message waits for pickup");
        (await mediator.FetchAsync(bob)).Should().ContainSingle();
        await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(socket);
    }

    private static async Task DeliverAndWaitForTheAnswerAsync(MediatorTestServer mediator, Transport transport, string packed)
    {
        if (transport == Transport.Http)
        {
            var response = await mediator.PostAsync(packed);
            response.Status.Should().Be(HttpStatusCode.Accepted);
            response.Body.Should().BeEmpty();
            AnswerCount(mediator).Should().BeGreaterThan(0);
            return;
        }

        using var socket = await mediator.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, packed);
        await MediatorTestServer.WaitUntilAsync(() => AnswerCount(mediator) > 0, "the opened request was handled");
        await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(socket);
    }

    /// <summary>Pickup answers the dispatch logged without a session (the last request's included).</summary>
    private static int AnswerCount(MediatorTestServer mediator)
        => mediator.Logs.Entries.Count(e => e.Contains("Protocol dispatch produced a reply", StringComparison.Ordinal)
            && e.Contains(MessagePickup.ProtocolUri, StringComparison.Ordinal) && e.Contains("returned-on-session=False", StringComparison.Ordinal));
}
