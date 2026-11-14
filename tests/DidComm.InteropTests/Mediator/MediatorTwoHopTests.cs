using System.Net;
using System.Text;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using DidComm.Transports;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// #95 acceptance, the two-mediator route: Bob's DID B is reached through mediator M1 and then
/// mediator M2 (<c>{uri: did:m1, routingKeys: [did:m2]}</c>). Alice's <c>SendAsync</c> wraps twice;
/// M1 unwraps its layer and keeps the inner forward for <c>did:m2</c>, which M2 enrolled at M1 as an
/// ordinary recipient; M1 hands it on through the queue (M2 picks it up from M1 like any recipient
/// and posts it to its own endpoint); M2 unwraps its layer and queues the message for Bob, who picks
/// it up.
/// </summary>
/// <remarks>
/// Both mediators are did:peer identities minted with real <c>DIDCommMessaging</c> services. The
/// issue suggested <c>SpecActorRegistry</c>'s <c>mediator1</c> / <c>mediator2</c>, but their fixture
/// documents carry no <c>DIDCommMessaging</c> service — the FR-MED-07 start-up check refuses such a
/// routing DID — and they share Bob's key bytes, so they cannot run as mediators here.
/// </remarks>
public sealed class MediatorTwoHopTests
{
    [Fact]
    public async Task The_first_mediator_relays_to_the_second_which_delivers_to_Bob()
    {
        var network = new MediatorNetwork();
        var clock = new TestClock(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var m2Identity = await MediatorCast.MintAsync(new DidCommServiceSpec("http://m2.test/", Accept: new[] { "didcomm/v2" }));

        await using var m1 = await MediatorTestServer.StartAsync(o =>
        {
            o.Host = "m1.test";
            o.Network = network;
            o.Clock = clock;
        });
        await using var m2 = await MediatorTestServer.StartAsync(o =>
        {
            o.Host = "m2.test";
            o.Network = network;
            o.Clock = clock;
            o.Identity = m2Identity;
        });

        // M2 is an ordinary recipient of M1: it enrols there under its own DID and registers it.
        var m2AtM1 = m1.Own(TestPeer.Create(m2Identity));
        await m1.EnrolAsync(m2AtM1);

        // Bob enrols at M2 and registers B, reached through M1 then M2.
        var bob = await m2.NewPeerAsync();
        (await m2.RequestMediationAsync(bob)).Should().Equal(m2.Did);
        var b = m2.Own(await MediatorCast.MintRecipientAsync(m1.Did, m2.Did));
        await m2.AddRecipientsAsync(bob, new[] { b.Did });

        var alice = await m1.NewSenderAsync();
        var note = alice.Note(b.Did);
        var sent = await alice.Client.SendAsync(note, new SendOptions(new[] { b.Did }, alice.Did));
        sent.EndpointUsed.Should().Be(m1.Endpoint);
        sent.Transport.Accepted.Should().BeTrue();
        await m1.FlushAsync();
        m1.Outcomes.Should().Equal(new[] { ForwardOutcomes.Queued }, "M1 keeps the inner forward for did:m2, its registered recipient");

        // M2 picks the message up from M1 like any recipient and posts it to its own endpoint.
        var relayed = (await m1.FetchAsync(m2AtM1)).Should().ContainSingle().Subject;
        (await m2.PostAsync(Encoding.UTF8.GetString(relayed.Payload.Span))).Status.Should().Be(HttpStatusCode.Accepted);
        (await m1.AcknowledgeAsync(m2AtM1, relayed.Id)).MessageCount.Should().Be(0);
        await m1.PushesIdleAsync();
        m1.PushOutcomes.Should().BeEmpty("M2 has no live session at M1, so nothing is pushed");

        await m2.FlushAsync();
        m2.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        var delivered = (await m2.FetchAsync(bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(b, delivered.Payload, alice)).Message.Id.Should().Be(note.Id);
        (await m2.AcknowledgeAsync(bob, delivered.Id)).MessageCount.Should().Be(0);

        var everyone = new[] { m1.Did, m2.Did, b.Did, bob.Did, alice.Did };
        m1.LeakingLogLines(everyone).Should().BeEmpty();
        m2.LeakingLogLines(everyone).Should().BeEmpty();
    }
}
