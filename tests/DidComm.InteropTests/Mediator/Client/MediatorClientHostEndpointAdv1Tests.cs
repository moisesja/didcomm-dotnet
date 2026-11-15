using System.Collections;
using System.Net;
using System.Reflection;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols.ProblemReport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// ADV-1 (#96 red team), end to end: a host that runs its own DIDComm receive endpoint with the
/// dispatcher (<c>MapDidCommMediator</c>) and is also a pickup client upstream over a WebSocket.
/// While its <c>status-request</c> is held unanswered upstream, Mallory — knowing the pending
/// request id (the worst case; it is a fresh UUID she cannot learn) — posts forged answers to the
/// host's own endpoint, stamped so that FR-RR-08 admits them and they reach the dispatcher and the
/// channel. None answers or fails the request; the genuine answer then completes it. Held.
/// </summary>
public sealed class MediatorClientHostEndpointAdv1Tests
{
    [Fact]
    public async Task Forged_answers_posted_to_the_hosts_own_endpoint_reach_the_matcher_and_change_nothing()
    {
        var upstreamStore = new StatusParkingStore(new InMemoryMessageStore(new MessagePickupOptions()));
        await using var upstream = await MediatorTestServer.StartAsync(o =>
        {
            o.Host = "m1.test";
            o.ConfigureServices = services => services.AddSingleton<IMessageStore>(upstreamStore);
        });
        await using var host = await MediatorAndRecipientHost.StartAsync(upstream);
        var ws = new UriBuilder(upstream.Server.BaseAddress) { Scheme = "ws", Path = upstream.Options.Pattern }.Uri;
        await upstream.EnrolAsync(upstream.Own(TestPeer.Create(host.Identity)));
        await using var mallory = await upstream.NewSenderAsync();

        var parked = upstreamStore.ParkNextStatus();
        var pending = host.Pickup.GetStatusAsync(upstream.Did, host.Did, serviceEndpointOverride: ws);
        await parked.Arrived.WaitAsync(MediatorTestServer.Timeout);

        var channel = host.App.Services.GetRequiredService<MediatorClientChannel>();
        var pendingId = PendingIds(channel).Should().ContainSingle().Subject;
        var lookups = channel.Lookups;
        var now = host.Clock.Now.ToUnixTimeSeconds();

        Message Stamped(Message message)
        {
            message.CreatedTime = now;
            return message;
        }

        // 1. Mallory's own authcrypt status, threaded to the pending request.
        var threaded = Stamped(MessagePickup.CreateStatus(mallory.Did, host.Did, new PickupStatus(42), thid: pendingId));
        (await host.PostAsync(await mallory.AuthcryptAsync(threaded, host.Did))).Should().NotBe(HttpStatusCode.InternalServerError);
        // 2. Her problem report naming it in pthid.
        var problem = Stamped(ProblemReport.Create(mallory.Did, host.Did, "e.p.req.not-enrolled", pthid: pendingId));
        (await host.PostAsync(await mallory.AuthcryptAsync(problem, host.Did))).Should().NotBe(HttpStatusCode.InternalServerError);
        // 3. A thread-less status (the type + mediator fallback).
        var threadless = Stamped(MessagePickup.CreateStatus(mallory.Did, host.Did, new PickupStatus(42)));
        (await host.PostAsync(await mallory.AuthcryptAsync(threadless, host.Did))).Should().NotBe(HttpStatusCode.InternalServerError);
        // 4. Anoncrypt, claiming the upstream mediator in 'from'.
        var claimed = Stamped(MessagePickup.CreateStatus(upstream.Did, host.Did, new PickupStatus(42), thid: pendingId));
        var anoncrypted = (await mallory.Client.PackEncryptedAsync(claimed, new PackEncryptedOptions(Recipients: new[] { host.Did }, Forward: false))).Message;
        (await host.PostAsync(anoncrypted)).Should().NotBe(HttpStatusCode.InternalServerError);

        // The three authcrypted forgeries passed the endpoint's admission and the matcher's identity
        // rule (they are what they say: Mallory's) and were looked up; nothing was completed.
        await MediatorTestServer.WaitUntilAsync(() => channel.Lookups - lookups >= 3, "the forgeries reached the matcher");
        pending.IsCompleted.Should().BeFalse("no forgery answers or fails the pending request");

        parked.Release();
        var status = await PickupE2E.Within(pending);
        status.MessageCount.Should().Be(0, "the genuine answer, not Mallory's 42");
    }

    private static IReadOnlyList<string> PendingIds(MediatorClientChannel channel)
    {
        var field = typeof(MediatorClientChannel).GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((IDictionary)field.GetValue(channel)!).Keys.Cast<string>().ToArray();
    }
}
