using System.Net;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pickup;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// #96 acceptance, a host that is both a mediator and a recipient (<c>AddMediator()</c> +
/// <c>AddMediatorClient()</c> in one container): the upstream mediator's answers to its pickup
/// client arrive on its WebSocket, go through its own dispatcher — where its own mediator handlers
/// see them and ignore them — and are correlated to its requests; and while one of its client
/// requests is waiting upstream, it keeps relaying for its own recipients.
/// </summary>
public sealed class MediatorAndRecipientHostTests
{
    [Fact]
    public async Task Its_answers_are_correlated_its_mediator_handlers_ignore_them_and_its_relaying_continues_while_a_request_is_pending()
    {
        var upstreamStore = new StatusParkingStore(new InMemoryMessageStore(new MessagePickupOptions()));
        await using var upstream = await MediatorTestServer.StartAsync(o =>
        {
            o.Host = "m1.test";
            o.ConfigureServices = services => services.AddSingleton<IMessageStore>(upstreamStore);
        });
        await using var host = await MediatorAndRecipientHost.StartAsync(upstream);
        var ws = new UriBuilder(upstream.Server.BaseAddress) { Scheme = "ws", Path = upstream.Options.Pattern }.Uri;

        // The host enrols upstream under its own DID and registers it.
        var hostAtUpstream = upstream.Own(TestPeer.Create(host.Identity));
        await upstream.EnrolAsync(hostAtUpstream);
        var alice = await upstream.NewSenderAsync();
        var note = alice.Note(host.Did, "for the host");
        var (packed, _) = await alice.ForwardAsync(upstream.Did, host.Did, await alice.AuthcryptAsync(note, host.Did));
        (await upstream.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        await upstream.FlushAsync();

        // 1. Its pickup client's answers arrive on the socket and are correlated.
        var batch = await PickupE2E.Within(host.Pickup.FetchAsync(upstream.Did, host.Did, 10, serviceEndpointOverride: ws));
        var item = batch.Items.Should().ContainSingle().Subject;
        item.Unpacked!.Message.Id.Should().Be(note.Id);
        item.Unpacked.Message.From.Should().Be(alice.Did);
        upstreamStore.Inner.MessageCount.Should().Be(0, "acknowledged upstream");

        // 2. Its own mediator handlers saw the answers (delivery, status) and left them alone.
        await MediatorTestServer.WaitUntilAsync(
            () => host.Logs.Entries.Any(e => e.Contains("ignored: not a request the mediator role answers")),
            "the host's message-pickup handler saw an answer and ignored it");
        host.Logs.Entries.Should().NotContain(e => e.Contains("No protocol handler registered"));

        // 3. While its status request waits upstream, it relays a forward for its own recipient.
        var parked = upstreamStore.ParkNextStatus();
        var pending = host.Pickup.GetStatusAsync(upstream.Did, host.Did, serviceEndpointOverride: ws);
        await parked.Arrived.WaitAsync(MediatorTestServer.Timeout);

        var bob = upstream.Own(await MediatorCast.MintPeerAsync());
        await host.EnrolAsync(bob, bob.Did);
        var forBob = alice.Note(bob.Did, "relayed by the host");
        var (toHost, _) = await alice.ForwardAsync(host.Did, bob.Did, await alice.AuthcryptAsync(forBob, bob.Did));
        (await host.PostAsync(toHost)).Should().Be(HttpStatusCode.Accepted);
        await host.Forwards.FlushAsync(MediatorTestServer.Timeout);
        host.Store.MessageCount.Should().Be(1, "the host stored Bob's message while its own request upstream was pending");
        var delivered = await host.AskAsync(bob, MessagePickup.CreateDeliveryRequest(bob.Did, host.Did, 10));
        MessagePickup.TryParseDelivery(delivered, out var messages, out _).Should().BeTrue();
        messages.Should().ContainSingle();
        pending.IsCompleted.Should().BeFalse();

        parked.Release();
        (await PickupE2E.Within(pending)).MessageCount.Should().Be(0);

        var everyone = new[] { upstream.Did, host.Did, bob.Did, alice.Did };
        host.Logs.Entries.Where(e => everyone.Any(v => e.Contains(v, StringComparison.Ordinal))).Should().BeEmpty();
        upstream.LeakingLogLines(everyone).Should().BeEmpty();
    }
}
