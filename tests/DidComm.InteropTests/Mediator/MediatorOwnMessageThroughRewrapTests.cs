using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.Jose;
using DidComm.Mediator;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// #110, owner decision OD-6: a mediator reached through a rewrapping mediator receives every message
/// sent to it inside a fresh anoncrypted <c>forward</c> addressed to itself (routing.md L71: "The
/// outer envelope is a forward message where to is the receiver itself"). The mediator's endpoints
/// (<c>MapDidCommMediator</c> over HTTP and WebSocket) open such a forward and handle the message
/// inside as one that arrived directly: its own envelope authenticates it, the replay check applies,
/// and it gets no return route (return_route.md L9), so a one-way message is applied and an answer
/// is logged, never sent. Each layer is opened with the restricted opening (no DID lookup), at most 8;
/// only the innermost message gets the full unpack (one lookup of its sender's DID, as a direct
/// message costs). A forward for the next hop inside is the forward handler's, as before, with every
/// layer's expiry and delay; a layer that is not a rewrap shape is the handler's to drop.
/// </summary>
public sealed class MediatorOwnMessageThroughRewrapTests
{
    public enum Transport
    {
        Http,
        WebSocket,
    }

    public enum Wrapper
    {
        /// <summary>A forward addressed to the mediator itself, built by hand (what any rewrapping mediator sends).</summary>
        HandBuilt,

        /// <summary>The real rewrap of a second mediator in <see cref="RewrapMode.ReanoncryptToNext"/>.</summary>
        RewrappingMediator,
    }

    public static TheoryData<Transport, Wrapper> TransportsAndWrappers()
    {
        var data = new TheoryData<Transport, Wrapper>();
        foreach (var transport in Enum.GetValues<Transport>())
        {
            foreach (var wrapper in Enum.GetValues<Wrapper>())
                data.Add(transport, wrapper);
        }
        return data;
    }

    // ---- one-way messages are applied ---------------------------------------------------------

    [Theory]
    [MemberData(nameof(TransportsAndWrappers))]
    public async Task A_recipient_update_through_a_rewrapping_mediator_is_applied_and_nothing_is_answered(Transport transport, Wrapper wrapper)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var newDid = (await mediator.NewPeerAsync()).Did;
        var update = mediator.Prepare(CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, new[] { new RecipientUpdate(newDid, RecipientUpdateAction.Add) }));
        var wrapped = await WrapAsync(mediator, wrapper, await mediator.AuthcryptAsync(bob, update));

        await DeliverAsync(mediator, transport, wrapped, () => mediator.Mediation.FindGrantByRecipientDidAsync(newDid, CancellationToken.None).AsTask().GetAwaiter().GetResult() is not null);

        (await mediator.Mediation.FindGrantByRecipientDidAsync(newDid, CancellationToken.None))!.PeerDid.Should().Be(bob.Did, "Bob's own update, authenticated by its own envelope, was applied");
        mediator.Outcomes.Should().BeEmpty("the message was the mediator's own: nothing was relayed or dropped as a forward");
        AnswerLogged(mediator, CoordinateMediation.ProtocolUri);
        mediator.LeakingLogLines(newDid).Should().BeEmpty();
    }

    [Theory]
    [InlineData(Transport.Http)]
    [InlineData(Transport.WebSocket)]
    public async Task A_messages_received_through_a_rewrapping_mediator_is_applied(Transport transport)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        await Qa.SendNoteAsync(mediator, alice, bob);
        var fetched = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        var received = mediator.Prepare(MessagePickup.CreateMessagesReceived(bob.Did, mediator.Did, new[] { fetched.Id }));
        var wrapped = await WrapAsync(mediator, Wrapper.HandBuilt, await mediator.AuthcryptAsync(bob, received));

        await DeliverAsync(mediator, transport, wrapped, () => mediator.Store.MessageCount == 0);

        mediator.Store.MessageCount.Should().Be(0, "the acknowledgement inside was applied: the message left the mailbox");
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData(Transport.Http)]
    [InlineData(Transport.WebSocket)]
    public async Task A_mediate_request_through_a_rewrapping_mediator_is_applied_and_its_grant_is_logged_not_sent(Transport transport)
    {
        // The owner accepted this limit (OD-6): with routing in use there is no return route
        // (return_route.md L9), and the mediator never sends an answer on its own, so the grant is
        // recorded but cannot reach the requester.
        await using var mediator = await MediatorTestServer.StartAsync();
        var carol = await mediator.NewPeerAsync();
        var request = mediator.Prepare(CoordinateMediation.CreateMediateRequest(carol.Did, mediator.Did));
        var wrapped = await WrapAsync(mediator, Wrapper.HandBuilt, await mediator.AuthcryptAsync(carol, request));

        await DeliverAsync(mediator, transport, wrapped, () => mediator.Mediation.GetGrantAsync(carol.Did, CancellationToken.None).AsTask().GetAwaiter().GetResult() is not null);

        (await mediator.Mediation.GetGrantAsync(carol.Did, CancellationToken.None)).Should().NotBeNull("the request was applied");
        AnswerLogged(mediator, CoordinateMediation.ProtocolUri);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    // ---- the bound and the opening rules stay -------------------------------------------------

    [Theory]
    [InlineData(ForwardHandler.MaxSelfAddressedLayers)]
    [InlineData(ForwardHandler.MaxSelfAddressedLayers + 1)]
    public async Task Eight_layers_around_a_message_for_the_mediator_are_opened_and_a_ninth_is_dropped_by_the_forward_handler(int layers)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var newDid = (await mediator.NewPeerAsync()).Did;
        var update = mediator.Prepare(CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, new[] { new RecipientUpdate(newDid, RecipientUpdateAction.Add) }));
        var wrapped = await WrapAsync(mediator, Wrapper.HandBuilt, await mediator.AuthcryptAsync(bob, update), layers);

        (await mediator.PostAsync(wrapped)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();

        var applied = await mediator.Mediation.FindGrantByRecipientDidAsync(newDid, CancellationToken.None) is not null;
        if (layers <= ForwardHandler.MaxSelfAddressedLayers)
        {
            applied.Should().BeTrue();
            mediator.Outcomes.Should().BeEmpty();
        }
        else
        {
            applied.Should().BeFalse("a ninth layer is never opened");
            mediator.Outcomes.Should().Equal(new[] { ForwardOutcomes.DroppedMalformed }, "the forward handler drops it as before");
        }
        mediator.LeakingLogLines(newDid).Should().BeEmpty();
    }

    public enum NotARewrapShape
    {
        AuthcryptedMiddleLayer,
        SignedMiddleLayer,
        AuthcryptedOuterLayer,
    }

    [Theory]
    [InlineData(NotARewrapShape.AuthcryptedMiddleLayer)]
    [InlineData(NotARewrapShape.SignedMiddleLayer)]
    [InlineData(NotARewrapShape.AuthcryptedOuterLayer)]
    public async Task A_layer_that_is_not_a_rewrap_shape_is_never_opened_to_the_message_inside(NotARewrapShape shape)
    {
        // A layer addressed to the mediator must be an anoncrypt envelope around a plaintext forward;
        // an authcrypt or signed one around the message is not opened to it: the forward handler gets
        // the forward as it arrived and drops it as malformed, as before.
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var newDid = (await mediator.NewPeerAsync()).Did;
        var update = mediator.Prepare(CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, new[] { new RecipientUpdate(newDid, RecipientUpdateAction.Add) }));
        var inner = await mediator.AuthcryptAsync(bob, update);
        var forward = ForwardMessage.Create(mediator.Did, mediator.Did, new[] { inner });
        string packed;
        switch (shape)
        {
            case NotARewrapShape.AuthcryptedOuterLayer:
                packed = (await alice.Client.PackEncryptedAsync(forward, new PackEncryptedOptions(Recipients: new[] { mediator.Did }, From: alice.Did))).Message;
                break;
            default:
                var middle = (await alice.Client.PackEncryptedAsync(forward, shape == NotARewrapShape.AuthcryptedMiddleLayer
                    ? new PackEncryptedOptions(Recipients: new[] { mediator.Did }, From: alice.Did)
                    : new PackEncryptedOptions(Recipients: new[] { mediator.Did }, SignFrom: alice.Did))).Message;
                packed = (await alice.ForwardAsync(mediator.Did, mediator.Did, middle)).Packed;
                break;
        }

        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();

        (await mediator.Mediation.FindGrantByRecipientDidAsync(newDid, CancellationToken.None)).Should().BeNull("the update inside was never opened");
        mediator.Outcomes.Should().Equal(new[] { ForwardOutcomes.DroppedMalformed }, shape.ToString());
        mediator.LeakingLogLines(newDid).Should().BeEmpty();
    }

    // ---- a forward for the next hop is relayed as before ---------------------------------------

    [Fact]
    public async Task A_forward_for_the_next_hop_inside_layers_addressed_to_the_mediator_is_relayed_with_every_layers_expiry_and_delay()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var now = mediator.Clock.Now;
        var (forBob, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did));
        // The middle layer expires first; the outer one asks for a hold.
        var middleExpires = now.AddSeconds(90);
        var (middle, _) = await alice.ForwardAsync(mediator.Did, mediator.Did, forBob, Qa.Expires(middleExpires));
        var (outer, _) = await alice.ForwardAsync(mediator.Did, mediator.Did, middle, Qa.All(Qa.Expires(now.AddSeconds(300)), Qa.Delay(30_000)));

        (await mediator.PostAsync(outer)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        var stored = (await mediator.Store.LeaseAsync(
            new MessageLeaseRequest(bob.Did, null, 10, long.MaxValue, now.AddSeconds(60), new MessageLease("test", now.AddSeconds(80))), CancellationToken.None))
            .Should().ContainSingle().Subject;
        var skew = mediator.App.Services.GetRequiredService<IOptions<DidCommOptions>>().Value.ExpiresClockSkew;
        stored.ExpiresAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(middleExpires.ToUnixTimeSeconds() + (long)Math.Floor(skew.TotalSeconds) + 1),
            "the earliest expiry of every layer bounds the relayed message (FR-ROUTE-09)");
        stored.NotBefore.Should().Be(now.AddSeconds(30), "the outer layer's hold is kept (FR-ROUTE-10)");
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    // ---- one lookup of the sender's DID, as for a direct message ----------------------------

    [Fact]
    public async Task The_message_inside_costs_the_same_lookups_of_its_senders_DID_as_when_it_arrives_directly()
    {
        CountingKeys? keys = null;
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = services =>
            services.AddSingleton<IDidKeyService>(sp => keys = new CountingKeys(new NetDidKeyService(sp.GetRequiredService<NetDid.Core.IDidResolver>()))));
        _ = mediator.App.Services.GetRequiredService<IDidKeyService>();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);

        // Without a return route (so the direct one packs no answer): one-way updates, stamped for
        // replay admission, each with its own id.
        Message Update() => Stamped(mediator, CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did,
            new[] { new RecipientUpdate("did:example:" + Guid.NewGuid().ToString("N"), RecipientUpdateAction.Add) }));

        var before = keys!.CallsFor(bob.Did);
        (await mediator.PostAsync(await mediator.AuthcryptAsync(bob, Update()))).Status.Should().Be(HttpStatusCode.Accepted);
        var direct = keys.CallsFor(bob.Did) - before;

        before = keys.CallsFor(bob.Did);
        (await mediator.PostAsync(await WrapAsync(mediator, Wrapper.HandBuilt, await mediator.AuthcryptAsync(bob, Update()), layers: 3))).Status.Should().Be(HttpStatusCode.Accepted);
        var wrapped = keys.CallsFor(bob.Did) - before;

        direct.Should().BeGreaterThan(0, "an authcrypt message needs its sender's key");
        wrapped.Should().Be(direct, "only the message inside is unpacked in full: its sender is looked up as for a direct message, the layers look up nothing");
    }

    // ---- support -----------------------------------------------------------------------------

    /// <summary>Wrap <paramref name="inner"/> in <paramref name="layers"/> fresh forwards addressed to the mediator itself.</summary>
    internal static async Task<string> WrapAsync(MediatorTestServer mediator, Wrapper wrapper, string inner, int layers = 1)
    {
        if (wrapper == Wrapper.RewrappingMediator)
        {
            // A second mediator in rewrap mode processes a sender's forward to it whose next is this
            // mediator: what it would push or deliver is this mediator's own double-packaged message.
            layers.Should().Be(1);
            await using var upstream = await MediatorTestServer.StartAsync(o =>
            {
                o.Host = "m1.test";
                o.Network = mediator.Network;
                o.Mediator = m => m.RewrapMode = RewrapMode.ReanoncryptToNext;
            });
            var sender = await upstream.NewSenderAsync();
            var (toUpstream, _) = await sender.ForwardAsync(upstream.Did, mediator.Did, inner);
            var rewrapped = await upstream.App.Services.GetRequiredService<ForwardProcessor>().ProcessAsync(toUpstream);
            return Encoding.UTF8.GetString(rewrapped.OnwardPacked);
        }

        var wrapperSender = await mediator.NewSenderAsync();
        var packed = inner;
        for (var i = 0; i < layers; i++)
            packed = (await wrapperSender.ForwardAsync(mediator.Did, mediator.Did, packed)).Packed;
        return packed;
    }

    /// <summary>
    /// Deliver <paramref name="packed"/> over <paramref name="transport"/>, as a rewrapping mediator would.
    /// HTTP: the answer is the plain empty 202 (no return route). WebSocket: wait for
    /// <paramref name="applied"/> (the record the test reads), then show nothing was written back.
    /// </summary>
    private static async Task DeliverAsync(MediatorTestServer mediator, Transport transport, string packed, Func<bool> applied)
    {
        if (transport == Transport.Http)
        {
            var response = await mediator.PostAsync(packed);
            response.Status.Should().Be(HttpStatusCode.Accepted, "no return route once routing is in use: the plain 202");
            response.Body.Should().BeEmpty("nothing is answered on the connection of the mediator that delivered it");
            return;
        }

        using var socket = await mediator.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, packed);
        await MediatorTestServer.WaitUntilAsync(applied, "the message inside was handled");
        await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(socket);
    }

    /// <summary>The handler produced an answer, and it was logged, not returned (no session).</summary>
    private static void AnswerLogged(MediatorTestServer mediator, string protocolUri)
        => mediator.Logs.Entries.Should().Contain(e => e.Contains("Protocol dispatch produced a reply", StringComparison.Ordinal)
            && e.Contains(protocolUri, StringComparison.Ordinal) && e.Contains("returned-on-session=False", StringComparison.Ordinal));

    private static Message Stamped(MediatorTestServer mediator, Message message)
    {
        message.CreatedTime = mediator.Clock.Now.ToUnixTimeSeconds();
        return message;
    }

    /// <summary>The mediator's key service, recording the DID or key id of every lookup.</summary>
    private sealed class CountingKeys(NetDidKeyService inner) : IDidKeyService, IDidKeyBindingService
    {
        private readonly ConcurrentQueue<string> _calls = new();

        public int CallsFor(string did) => _calls.Count(c => c.StartsWith(did, StringComparison.Ordinal));

        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
        {
            _calls.Enqueue(did);
            return inner.GetVerificationMethodsAsync(did, relationship, ct);
        }

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
        {
            _calls.Enqueue(did);
            return inner.IsKeyAuthorizedAsync(did, kid, relationship, ct);
        }

        public void RejectUnsupportedMethod(string did) => inner.RejectUnsupportedMethod(did);

        public Task<ResolvedKeyBinding?> ResolveKeyBindingAsync(string kid, VerificationRelationship relationship, CancellationToken ct = default)
        {
            _calls.Enqueue(kid);
            return inner.ResolveKeyBindingAsync(kid, relationship, ct);
        }
    }
}
