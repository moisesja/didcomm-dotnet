using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using DidComm.Transports;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// QA-2 (#95), Alice → mediator → Bob over every pair of transports: Alice sends with
/// <c>SendAsync</c> (FR-ROUTE-04: Bob's service <c>uri</c> is the mediator's routing DID) over HTTP or
/// over the WebSocket transport, the inner message authcrypted or anoncrypted; Bob picks up over HTTP,
/// over the mediator's WebSocket, or live. Then many senders and many grants at once, with one and
/// several recipient DIDs per grant and a mix of live and polling recipients: nothing is lost,
/// duplicated within a lease or delivered to the wrong grant.
/// </summary>
public sealed class MediatorMatrixQaTests
{
    public enum Pickup
    {
        Http,
        WebSocket,
        Live,
    }

    [Theory]
    [InlineData(false, Pickup.Http, false)]
    [InlineData(false, Pickup.Http, true)]
    [InlineData(false, Pickup.WebSocket, false)]
    [InlineData(false, Pickup.WebSocket, true)]
    [InlineData(false, Pickup.Live, false)]
    [InlineData(false, Pickup.Live, true)]
    [InlineData(true, Pickup.Http, false)]
    [InlineData(true, Pickup.Http, true)]
    [InlineData(true, Pickup.WebSocket, false)]
    [InlineData(true, Pickup.WebSocket, true)]
    [InlineData(true, Pickup.Live, false)]
    [InlineData(true, Pickup.Live, true)]
    public async Task Alice_sends_with_SendAsync_over_either_transport_and_Bob_receives_over_each_pickup_mode(bool aliceOverWebSocket, Pickup pickup, bool anoncrypt)
    {
        PeerIdentity? identity = aliceOverWebSocket
            ? await MediatorCast.MintAsync(new DidCommServiceSpec("ws://mediator.test/", Accept: new[] { "didcomm/v2" }))
            : null;
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Identity = identity);
        var bob = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(bob)).Should().Equal(mediator.Did);
        var b = await mediator.NewRecipientAsync();
        await mediator.AddRecipientsAsync(bob, new[] { b.Did });
        await using var alice = await QaSender.CreateAsync(mediator, aliceOverWebSocket);

        WebSocket? socket = null;
        if (pickup != Pickup.Http)
        {
            socket = await mediator.ConnectAsync();
            if (pickup == Pickup.Live)
                await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, socket, bob);
        }

        var note = anoncrypt ? AnonNote(b.Did) : alice.Note(b.Did);
        var sent = await alice.Client.SendAsync(note, new SendOptions(new[] { b.Did }, anoncrypt ? null : alice.Did));
        sent.Transport.Accepted.Should().BeTrue();
        sent.EndpointUsed.Scheme.Should().Be(aliceOverWebSocket ? "ws" : "http");
        if (aliceOverWebSocket)
            await MediatorTestServer.WaitUntilAsync(() => mediator.Outcomes.Count == 1, "the forward sent over the WebSocket was relayed");
        await mediator.FlushAsync();

        DeliveredMessage delivered;
        switch (pickup)
        {
            case Pickup.Http:
                mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
                delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
                (await mediator.AcknowledgeAsync(bob, delivered.Id)).MessageCount.Should().Be(0);
                break;
            case Pickup.WebSocket:
                mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
                var request = MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
                delivered = (await mediator.AskOverSocketAsync(socket!, bob, request)).Delivery(request.Id).Should().ContainSingle().Subject;
                (await MediatorDeliveryModesQaTests.AckOverSocketAsync(mediator, socket!, bob, delivered.Id)).MessageCount.Should().Be(0);
                break;
            default:
                delivered = (await mediator.ReceiveOverSocketAsync(socket!, bob)).Delivery(thid: null).Should().ContainSingle().Subject;
                mediator.Outcomes.Should().Equal(ForwardOutcomes.DeliveredLive);
                (await MediatorDeliveryModesQaTests.AckOverSocketAsync(mediator, socket!, bob, delivered.Id)).MessageCount.Should().Be(0);
                break;
        }

        var unpacked = await b.Client.UnpackAsync(Encoding.UTF8.GetString(delivered.Payload.Span));
        unpacked.Encrypted.Should().BeTrue();
        unpacked.Message.Id.Should().Be(note.Id, "exactly one forward layer was added and removed");
        unpacked.Authenticated.Should().Be(!anoncrypt);
        if (!anoncrypt)
            unpacked.SenderKid.Should().StartWith(alice.Did + "#");
        mediator.Store.MessageCount.Should().Be(0);
        if (socket is not null)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
            socket.Dispose();
        }
        mediator.LeakingLogLines(b.Did, delivered.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task Many_senders_many_grants_and_several_recipient_DIDs_per_grant_concurrently_lose_nothing_duplicate_nothing_and_cross_nothing()
    {
        Qa.RaiseThreadPoolFloor();
        const int Grants = 4;
        const int DidsPerGrant = 2;
        const int Senders = 3;
        const int PerPair = 4;
        await using var mediator = await MediatorTestServer.StartAsync();

        var peers = new List<TestPeer>();
        var recipients = new List<List<TestPeer>>();
        for (var g = 0; g < Grants; g++)
        {
            var peer = await mediator.NewPeerAsync();
            var dids = new List<TestPeer>();
            for (var d = 0; d < DidsPerGrant; d++)
                dids.Add(await mediator.NewPeerAsync());
            (await mediator.RequestMediationAsync(peer)).Should().Equal(mediator.Did);
            await mediator.AddRecipientsAsync(peer, dids.Select(r => r.Did).ToArray());
            peers.Add(peer);
            recipients.Add(dids);
        }
        var senders = new List<MediatorSender>();
        for (var s = 0; s < Senders; s++)
            senders.Add(await mediator.NewSenderAsync());

        // Grant 0 is live on a WebSocket; grant 1 polls over a WebSocket; grants 2 and 3 over HTTP.
        using var liveSocket = await mediator.ConnectAsync();
        await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, liveSocket, peers[0]);
        using var pollSocket = await mediator.ConnectAsync();
        var hello = MessagePickup.CreateStatusRequest(peers[1].Did, mediator.Did);
        (await mediator.AskOverSocketAsync(pollSocket, peers[1], hello)).Status(hello.Id);

        // Every forward packed first; then all posted at once.
        var expected = new Dictionary<int, HashSet<string>>();
        var packed = new List<string>();
        for (var g = 0; g < Grants; g++)
        {
            expected[g] = new HashSet<string>(StringComparer.Ordinal);
            for (var d = 0; d < DidsPerGrant; d++)
            {
                for (var s = 0; s < Senders; s++)
                {
                    for (var k = 0; k < PerPair; k++)
                    {
                        var to = recipients[g][d];
                        var content = $"g{g}-d{d}-s{s}-k{k}";
                        expected[g].Add(content);
                        var note = senders[s].Note(to.Did, content);
                        packed.Add((await senders[s].ForwardAsync(mediator.Did, to.Did, await senders[s].AuthcryptAsync(note, to.Did))).Packed);
                    }
                }
            }
        }
        var shuffled = packed.OrderBy(_ => Random.Shared.Next()).ToArray();
        var statuses = await Task.WhenAll(shuffled.Select(p => Task.Run(() => mediator.PostAsync(p))));
        statuses.Should().OnlyContain(r => r.Status == HttpStatusCode.Accepted && r.Body.Length == 0);
        await mediator.FlushAsync();
        var perGrant = DidsPerGrant * Senders * PerPair;
        mediator.Outcome(ForwardOutcomes.DeliveredLive).Should().Be(perGrant, "every message of the live grant is pushed");
        mediator.Outcome(ForwardOutcomes.Queued).Should().Be((Grants - 1) * perGrant);

        // Grant 0: the live pushes, one per message.
        var live = new List<DeliveredMessage>();
        while (live.Count < perGrant)
            live.AddRange((await mediator.ReceiveOverSocketAsync(liveSocket, peers[0])).Delivery(thid: null));
        await ShouldBeExactlyAsync(live, recipients[0], senders, expected[0]);
        (await MediatorDeliveryModesQaTests.AckOverSocketAsync(mediator, liveSocket, peers[0], live.Select(m => m.Id).ToArray())).MessageCount.Should().Be(0);

        // Grant 1: delivery-request over its socket, acknowledging as it goes.
        var polled = new List<DeliveredMessage>();
        while (true)
        {
            var request = MessagePickup.CreateDeliveryRequest(peers[1].Did, mediator.Did, 10);
            var answer = await mediator.AskOverSocketAsync(pollSocket, peers[1], request);
            if (answer.Message.Type == MessagePickup.StatusType)
                break;
            var batch = answer.Delivery(request.Id);
            polled.AddRange(batch);
            await MediatorDeliveryModesQaTests.AckOverSocketAsync(mediator, pollSocket, peers[1], batch.Select(m => m.Id).ToArray());
        }
        await ShouldBeExactlyAsync(polled, recipients[1], senders, expected[1]);

        // Grants 2 and 3 over HTTP: three delivery-requests at once never hand out one message twice.
        for (var g = 2; g < Grants; g++)
        {
            var peer = peers[g];
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => mediator.FetchAsync(peer, 10))));
            var first = concurrent.SelectMany(x => x).ToList();
            first.Select(m => m.Id).Should().OnlyHaveUniqueItems("concurrent requests lease disjoint messages");
            first.Should().HaveCount(perGrant);
            await ShouldBeExactlyAsync(first, recipients[g], senders, expected[g]);
            (await mediator.AcknowledgeAsync(peer, first.Select(m => m.Id).ToArray())).MessageCount.Should().Be(0);
        }

        mediator.Store.MessageCount.Should().Be(0);
        await liveSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        await pollSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        mediator.LeakingLogLines(recipients.SelectMany(r => r).Select(r => r.Did).ToArray()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_forward_whose_payload_travels_as_base64_is_relayed_as_the_original_bytes(bool padded)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(bob.Did);
        var inner = await alice.AuthcryptAsync(note, bob.Did);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(inner)).Replace('+', '-').Replace('/', '_');
        if (!padded)
            encoded = encoded.TrimEnd('=');

        var (packed, _) = await alice.ForwardAsync(mediator.Did, bob.Did, inner, f => f.Attachments![0].Data = new AttachmentData { Base64 = encoded });
        await Qa.PostForwardAsync(mediator, packed);
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        Encoding.UTF8.GetString(delivered.Payload.Span).Should().Be(inner, "the recipient gets exactly the bytes the sender packed");
        (await MediatorRecipient.UnpackFromSenderAsync(bob, delivered.Payload, alice)).Message.Id.Should().Be(note.Id);
    }

    private static async Task ShouldBeExactlyAsync(IReadOnlyList<DeliveredMessage> delivered, IReadOnlyList<TestPeer> dids, IReadOnlyList<MediatorSender> senders, IReadOnlySet<string> expected)
    {
        delivered.Select(m => m.Id).Should().OnlyHaveUniqueItems();
        var contents = new List<string>();
        foreach (var message in delivered)
        {
            var text = Encoding.UTF8.GetString(message.Payload.Span);
            UnpackResult? unpacked = null;
            foreach (var did in dids)
            {
                try
                {
                    unpacked = await did.Client.UnpackAsync(text);
                    unpacked.Message.To.Should().Equal(did.Did);
                    break;
                }
                catch (Exception) when (unpacked is null)
                {
                    // Not this DID's message; another DID of the grant must open it.
                }
            }
            unpacked.Should().NotBeNull("every delivered message belongs to one of the grant's own recipient DIDs");
            senders.Select(s => s.Did).Should().Contain(unpacked!.Message.From);
            contents.Add(unpacked.Message.Body!["content"]!.GetValue<string>());
        }
        contents.Should().BeEquivalentTo(expected, "nothing lost, nothing duplicated, nothing of another grant");
    }

    private static Message AnonNote(string to)
        => new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithTo(to)
            .WithBody(new JsonObject { ["content"] = "anonymous" })
            .Build();
}
