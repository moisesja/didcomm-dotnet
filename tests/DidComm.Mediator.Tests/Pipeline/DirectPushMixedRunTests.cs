using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// #110, the yield rule in a run that mixes direct and live pushes. A run that met a direct refusal
/// yields at its <see cref="MessagePickupOptions.MaxDeliveryBatch"/> bound when it delivered
/// something, live or direct. Here the recipient's live session is downgraded when the run starts, so
/// the first push goes direct and is refused. During that send the session resumes
/// (<c>return_route: all</c> again). A resume raises no wake-up: only a stored message or turning
/// live delivery on does. So every push after the refusal goes live, and the run must yield at its
/// bound and go on in its next turn until the backlog is out, with no new wake-up.
/// </summary>
public sealed class DirectPushMixedRunTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    public async Task A_direct_refusal_then_a_resumed_live_session_delivers_the_whole_backlog_across_the_batch_bound_with_no_new_wake_up(int batch)
    {
        await using var direct = await DirectWorld.StartAsync(o => o.Pickup.MaxDeliveryBatch = batch);
        var fixture = direct.Fixture;
        var grant = await direct.World.GrantAsync($"mixed-{batch}");
        direct.Endpoints.Set(grant.Peer, DirectWorld.EndpointFor($"mixed-{batch}"));
        var session = new PickupFakeSession(grant.Peer, direct.World.Mediator.Did, live: true);
        await TurnLiveOnAsync(fixture, session);
        session.Downgrade();

        const byte Refused = 40;
        var refused = await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, Refused));
        var backlog = new List<string>();
        for (var i = 0; i < 2 * batch; i++)
            backlog.Add((await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, (byte)(Refused + 1 + i)))).MessageId);
        direct.Sends.OnAnswer = (_, payload, _) =>
        {
            // The session comes back while the first direct send is out: no wake-up.
            session.Resume();
            return Task.FromResult(StandInJwe.SeedOf(payload.Span) == Refused ? DirectSendResult.Refused : DirectSendResult.Delivered);
        };

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        // Idle once no grant is in line or being served: a run that yields puts its grant back in
        // line before it is done with it, so this waits for every turn the wake-up led to.
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        direct.Sends.Seeds.Should().Equal(new[] { Refused }, "only the first push found the session downgraded");
        var pushedLive = session.Sent.SelectMany(m => MessagePickup.TryParseDelivery(m, out var delivered, out _) ? delivered : Array.Empty<DeliveredMessage>()).Select(d => d.Id).ToList();
        pushedLive.Should().Equal(backlog, "a run that delivered live after a direct refusal yields at its bound ({0}) and goes on: the whole backlog goes out in one wake-up (pushed live {1} of {2})", batch, pushedLive.Count, backlog.Count);
        fixture.PushOutcomeLog.Should().Equal(new[] { PushOutcomes.Failed }.Concat(Enumerable.Repeat(PushOutcomes.PushedLive, backlog.Count)));
        (await direct.World.PickupAsync(grant.Peer)).Should().Equal(new[] { refused.MessageId }, "only the refused message is left, for pickup");
    }

    /// <summary>Register <paramref name="session"/> and turn live delivery on over it, as its peer would; returns once the live-on wake-up was served.</summary>
    private static async Task TurnLiveOnAsync(PushFixture fixture, IReturnRouteSession session)
    {
        fixture.Services.GetRequiredService<IReturnRouteSessionRegistry>().Register(session);
        var client = fixture.Services.GetRequiredService<DidCommClient>();
        var change = MessagePickup.CreateLiveDeliveryChange(session.PeerDid, session.LocalDid, liveDelivery: true);
        var packed = await client.PackEncryptedAsync(change, new PackEncryptedOptions(Recipients: new[] { session.LocalDid }, From: session.PeerDid));
        await fixture.Services.GetRequiredService<ProtocolDispatcher>().DispatchAsync(await client.UnpackAsync(packed.Message), client, fixture.Options, Task.CompletedTask, session, CancellationToken.None);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
    }
}
