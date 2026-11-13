using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Jose;
using DidComm.Mediator;
using DidComm.Mediator.Pipeline;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// QA-2 (#95), routes through the real mediator: the longest route a sender builds (8 forward layers,
/// the mediator-as-endpoint layer included) is relayed and every layer peels; a 9-layer route is
/// refused before anything is sent (FR-ROUTE-04). Rewrap mode and per-recipient extra routing keys
/// (FR-ROUTE-05/06) produce onions the recipient unwraps whether the message reaches it live, from the
/// queue or by direct push; and a two-mediator route still delivers when the first mediator rewraps.
/// </summary>
public sealed class MediatorRoutesQaTests
{
    // ---- route length ------------------------------------------------------------------------------

    [Fact]
    public async Task An_8_layer_route_through_the_mediator_is_relayed_and_every_layer_peels_to_Alices_message()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var hops = new List<TestPeer>();
        for (var i = 0; i < 7; i++)
            hops.Add(await mediator.NewPeerAsync());
        // The first routing key is the mediator's registered recipient; its holder picks it up.
        var holder = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(holder, hops[0].Did);
        var b = await mediator.NewRecipientAsync(hops.Select(h => h.Did).ToArray());
        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(b.Did);

        var sent = await alice.Client.SendAsync(note, new SendOptions(new[] { b.Did }, alice.Did));
        sent.EndpointUsed.Should().Be(mediator.Endpoint);
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);

        var payload = (await mediator.FetchAsync(holder)).Should().ContainSingle().Subject.Payload.ToArray();
        for (var i = 0; i < hops.Count; i++)
        {
            var peeled = await PeelOnceAsync(hops[i], payload);
            DidComm.Consistency.DidSubject.DidSubjectOf(peeled.NextHop).Should().Be(i + 1 < hops.Count ? hops[i + 1].Did : b.Did, "layer {0} names the next hop", i + 1);
            payload = peeled.OnwardPacked;
        }
        (await MediatorRecipient.UnpackFromSenderAsync(b, payload, alice)).Message.Id.Should().Be(note.Id);
        mediator.LeakingLogLines(hops.Select(h => h.Did).Append(b.Did).ToArray()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_9_layer_route_is_refused_by_the_sender_and_nothing_reaches_the_mediator()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var keys = new List<string>();
        for (var i = 0; i < 8; i++)
            keys.Add((await mediator.NewPeerAsync()).Did);
        var holder = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(holder, keys[0]);
        var b = mediator.Own(TestPeer.Create(await MediatorCast.MintWithServiceEntryAsync(RawService(mediator.Did, keys))));
        var alice = await mediator.NewSenderAsync();

        var act = () => alice.Client.SendAsync(alice.Note(b.Did), new SendOptions(new[] { b.Did }, alice.Did));

        // Refused while Alice's own sender reads B's service entry (the exception is Alice's, about her own input).
        (await act.Should().ThrowAsync<DidComm.Exceptions.DidResolutionException>()).Which.Message.Should().Contain("more than the maximum of 8");
        mediator.Handler.Outstanding.Should().Be(0);
        await mediator.FlushAsync();
        mediator.Outcomes.Should().BeEmpty("nothing was sent");
        mediator.Store.MessageCount.Should().Be(0);
    }

    // ---- rewrap mode -------------------------------------------------------------------------------

    [Theory]
    [InlineData(Delivery.Queue)]
    [InlineData(Delivery.Live)]
    [InlineData(Delivery.DirectPush)]
    public async Task In_rewrap_mode_the_recipient_peels_one_fresh_layer_whichever_way_it_receives_the_message(Delivery delivery)
        => await RoundTripAsync(delivery, m => m.RewrapMode = RewrapMode.ReanoncryptToNext, provider: null, expectLayers: 1);

    [Theory]
    [InlineData(Delivery.Queue, 1)]
    [InlineData(Delivery.Live, 1)]
    [InlineData(Delivery.DirectPush, 1)]
    [InlineData(Delivery.Queue, 2)]
    [InlineData(Delivery.Live, 2)]
    [InlineData(Delivery.DirectPush, 2)]
    public async Task Per_recipient_extra_routing_keys_add_layers_the_recipient_peels_whichever_way_it_receives_the_message(Delivery delivery, int keys)
        => await RoundTripAsync(delivery, configure: null, provider: new QaRoutingKeys(), expectLayers: keys);

    [Fact]
    public async Task Extra_routing_keys_of_one_recipient_are_not_applied_to_another_recipient_of_the_same_or_another_grant()
    {
        var provider = new QaRoutingKeys();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IRecipientRoutingKeyProvider>(provider));
        var bob = await mediator.NewPeerAsync();
        var bIdentity = await MediatorCast.MintAsync(new DidCommServiceSpec(mediator.Did, Accept: new[] { "didcomm/v2" }));
        var b = mediator.Own(TestPeer.Create(bIdentity));
        var b2 = await mediator.NewRecipientAsync();
        (await mediator.RequestMediationAsync(bob)).Should().Equal(mediator.Did);
        await mediator.AddRecipientsAsync(bob, new[] { b.Did, b2.Did });
        var carol = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(carol);
        provider.For(b.Did, PublicX25519(bIdentity));
        var alice = await mediator.NewSenderAsync();

        await Qa.SendNoteAsync(mediator, alice, b2);
        await Qa.SendNoteAsync(mediator, alice, carol);
        await Qa.SendNoteAsync(mediator, alice, b);

        var forBob = await mediator.FetchAsync(bob);
        forBob.Should().HaveCount(2);
        var direct = 0;
        var layered = 0;
        foreach (var m in forBob)
        {
            var text = Encoding.UTF8.GetString(m.Payload.Span);
            var unpackedByB2 = await TryUnpackAsync(b2, text);
            if (unpackedByB2 is not null)
            {
                unpackedByB2.Message.From.Should().Be(alice.Did, "B2's message arrives with no extra layer");
                direct++;
            }
            else
            {
                var peeled = await PeelAsync(b, m.Payload);
                (await MediatorRecipient.UnpackFromSenderAsync(b, peeled, alice)).Message.To.Should().Equal(b.Did);
                layered++;
            }
        }
        direct.Should().Be(1);
        layered.Should().Be(1);
        var forCarol = (await mediator.FetchAsync(carol)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(carol, forCarol.Payload, alice)).Message.To.Should().Equal(carol.Did);
    }

    // ---- two mediators, the first rewrapping -------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_two_mediator_route_delivers_when_the_first_mediator_rewraps(bool viaDirectPush)
    {
        var network = new MediatorNetwork();
        var clock = new TestClock(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var m2Identity = await MediatorCast.MintAsync(new DidCommServiceSpec("http://m2.test/", Accept: new[] { "didcomm/v2" }));
        var directory = new TestEndpointDirectory();
        await using var m1 = await MediatorTestServer.StartAsync(o =>
        {
            o.Host = "m1.test";
            o.Network = network;
            o.Clock = clock;
            o.Mediator = m =>
            {
                m.DirectPush = viaDirectPush;
                m.RewrapMode = RewrapMode.ReanoncryptToNext;
            };
            o.ConfigureServices = s => s.AddSingleton<IRecipientEndpointDirectory>(directory);
        });
        await using var m2 = await MediatorTestServer.StartAsync(o =>
        {
            o.Host = "m2.test";
            o.Network = network;
            o.Clock = clock;
            o.Identity = m2Identity;
        });
        var m2AtM1 = m1.Own(TestPeer.Create(m2Identity));
        await m1.EnrolAsync(m2AtM1);
        directory.Map(m2.Did, m2.Endpoint);
        var bob = await m2.NewPeerAsync();
        (await m2.RequestMediationAsync(bob)).Should().Equal(m2.Did);
        var b = m2.Own(await MediatorCast.MintRecipientAsync(m1.Did, m2.Did));
        await m2.AddRecipientsAsync(bob, new[] { b.Did });
        var alice = await m1.NewSenderAsync();
        var note = alice.Note(b.Did);

        (await alice.Client.SendAsync(note, new SendOptions(new[] { b.Did }, alice.Did))).Transport.Accepted.Should().BeTrue();
        await m1.FlushAsync();
        m1.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        if (viaDirectPush)
        {
            await m1.PushesIdleAsync();
            m1.PushOutcomes.Should().Equal(PushOutcomes.Pushed);
        }
        else
        {
            var relayed = (await m1.FetchAsync(m2AtM1)).Should().ContainSingle().Subject;
            await Qa.PostForwardAsync(m2, Encoding.UTF8.GetString(relayed.Payload.Span));
        }

        await m2.FlushAsync();
        m2.Outcomes.Should().Equal(new[] { ForwardOutcomes.Queued }, "M2 peels M1's fresh layer and relays the inner forward to B");
        var delivered = (await m2.FetchAsync(bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(b, delivered.Payload, alice)).Message.Id.Should().Be(note.Id);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    public enum Delivery
    {
        Queue,
        Live,
        DirectPush,
    }

    private static async Task RoundTripAsync(Delivery delivery, Action<MediatorOptions>? configure, QaRoutingKeys? provider, int expectLayers)
    {
        var network = new MediatorNetwork();
        await using var inbox = await InboxServer.StartAsync(network);
        var directory = new TestEndpointDirectory();
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Network = network;
            o.Mediator = m =>
            {
                m.DirectPush = delivery == Delivery.DirectPush;
                configure?.Invoke(m);
            };
            o.ConfigureServices = s =>
            {
                s.AddSingleton<IRecipientEndpointDirectory>(directory);
                if (provider is not null)
                    s.AddSingleton<IRecipientRoutingKeyProvider>(provider);
            };
        });
        var bob = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(bob)).Should().Equal(mediator.Did);
        var bIdentity = await MediatorCast.MintAsync(new DidCommServiceSpec(mediator.Did, Accept: new[] { "didcomm/v2" }));
        var b = mediator.Own(TestPeer.Create(bIdentity));
        if (provider is not null)
            provider.For(b.Did, Enumerable.Repeat(PublicX25519(bIdentity), expectLayers).ToArray());
        await mediator.AddRecipientsAsync(bob, new[] { b.Did });
        directory.Map(bob.Did, inbox.Endpoint("/bob"));
        var alice = await mediator.NewSenderAsync();

        WebSocket? socket = null;
        if (delivery == Delivery.Live)
        {
            socket = await mediator.ConnectAsync();
            await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, socket, bob);
        }

        var note = alice.Note(b.Did);
        (await alice.Client.SendAsync(note, new SendOptions(new[] { b.Did }, alice.Did))).Transport.Accepted.Should().BeTrue();
        await mediator.FlushAsync();

        ReadOnlyMemory<byte> payload;
        switch (delivery)
        {
            case Delivery.Live:
                payload = (await mediator.ReceiveOverSocketAsync(socket!, bob)).Delivery(thid: null).Should().ContainSingle().Subject.Payload;
                // L-020 (PR #109 review R1-2): the forward pump now only stores (Queued); the push workers push it live (Pushed-Live).
                await mediator.PushesIdleAsync();
                mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
                mediator.PushOutcomes.Should().Equal(PushOutcomes.PushedLive);
                break;
            case Delivery.DirectPush:
                await mediator.PushesIdleAsync();
                mediator.PushOutcomes.Should().Equal(PushOutcomes.Pushed);
                payload = inbox.Received.Should().ContainSingle().Subject.Body;
                break;
            default:
                payload = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject.Payload;
                break;
        }

        for (var i = 0; i < expectLayers; i++)
            payload = await PeelAsync(b, payload);
        (await MediatorRecipient.UnpackFromSenderAsync(b, payload, alice)).Message.Id.Should().Be(note.Id);
        if (socket is not null)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
            socket.Dispose();
        }
        mediator.LeakingLogLines(b.Did).Should().BeEmpty();
    }

    /// <summary>Peel one forward layer addressed to <paramref name="recipient"/> (its <c>next</c> is the recipient) and return the inner payload.</summary>
    internal static async Task<byte[]> PeelAsync(TestPeer recipient, ReadOnlyMemory<byte> payload)
    {
        var peeled = await PeelOnceAsync(recipient, payload.ToArray());
        DidComm.Consistency.DidSubject.DidSubjectOf(peeled.NextHop).Should().Be(recipient.Did);
        return peeled.OnwardPacked;
    }

    private static async Task<ForwardProcessingResult> PeelOnceAsync(TestPeer holder, byte[] payload)
    {
        var packed = Encoding.UTF8.GetString(payload);
        var outer = await holder.Client.UnpackAsync(packed);
        outer.Message.Type.Should().Be(ForwardConstants.ForwardTypeUri);
        outer.Encrypted.Should().BeTrue();
        outer.SenderKid.Should().BeNull("a forward layer is anoncrypted");
        var processor = new ForwardProcessor(holder.Client, holder.Services.GetRequiredService<IDidKeyService>(), new ForwardProcessorOptions());
        return await processor.ProcessAsync(packed);
    }

    private static async Task<DidComm.Facade.UnpackResult?> TryUnpackAsync(TestPeer peer, string packed)
    {
        try
        {
            var unpacked = await peer.Client.UnpackAsync(packed);
            return unpacked.Message.Type == ForwardConstants.ForwardTypeUri ? null : unpacked;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Jwk PublicX25519(PeerIdentity identity)
    {
        var x25519 = identity.Privates.Single(k => k.Crv == "X25519");
        return new Jwk { Kty = x25519.Kty, Crv = x25519.Crv, X = x25519.X, Kid = x25519.Kid };
    }

    private static NetDid.Core.Model.Service RawService(string uri, IReadOnlyList<string> routingKeys) => new()
    {
        Id = "#didcomm",
        Type = "DIDCommMessaging",
        ServiceEndpoint = NetDid.Core.Model.ServiceEndpointValue.FromMap(new Dictionary<string, JsonElement>
        {
            ["uri"] = JsonSerializer.SerializeToElement(uri),
            ["routingKeys"] = JsonSerializer.SerializeToElement(routingKeys),
            ["accept"] = JsonSerializer.SerializeToElement(new[] { "didcomm/v2" }),
        }),
    };
}

/// <summary>A host provider of per-recipient extra routing keys.</summary>
internal sealed class QaRoutingKeys : IRecipientRoutingKeyProvider
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<Jwk>> _keys = new(StringComparer.Ordinal);

    public void For(string recipientDid, params Jwk[] keys) => _keys[recipientDid] = keys;

    public ValueTask<IReadOnlyList<Jwk>> GetAsync(string recipientDid, CancellationToken ct)
        => ValueTask.FromResult(_keys.GetValueOrDefault(recipientDid) ?? Array.Empty<Jwk>());
}
