using System.Text;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Transports;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// The Identus mediator v1.2.1 pushes a live message encrypted to the forward's <c>next</c> (another
/// DID the wallet holds), with <c>to</c> naming that DID and a <c>thid</c>, on the account DID's
/// return-route WebSocket (#98 O-1b). Making the <c>to</c> header advisory in the matcher (#98 owner
/// decision C2) must not let such a push in: the client's receive side refuses it before any
/// matcher sees it, because the key that decrypted it is not the session's local DID
/// (<c>LocalMismatch</c>, FR-RR-07), and the live stream never yields it. Trust comes from the keys,
/// not from the socket (DIDComm v2.1 transports). The mediator is scripted on an in-memory socket so
/// the test sends exactly the frames it names, packed with the real mediator's keys.
/// </summary>
public sealed class PickupClientLiveForeignPushTests
{
    [Fact]
    public async Task A_push_encrypted_to_another_DID_on_the_account_socket_is_refused_by_the_receive_side_and_never_yielded()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var sockets = new ScriptedSocketFactory();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.WebSocket = w =>
            {
                w.WebSocketFactory = sockets.Create;
                w.Connect = (_, _, _) => Task.CompletedTask;
            };
        });
        var bob = await harness.NewPeerAsync();
        var other = await harness.NewPeerAsync();
        var alice = await harness.NewAliceAsync();
        var server = mediator.App.Services.GetRequiredService<DidCommClient>();
        var ws = new Uri("ws://mediator.test/");

        async Task<Message> ReceivedAsync(int index)
        {
            (await ReturnRouteTestHost.WaitUntilAsync(() => sockets.Count == 1 && sockets.Sockets[0].Sent.Count > index, MediatorTestServer.Timeout))
                .Should().BeTrue($"the client sent request {index + 1}");
            return (await server.UnpackAsync(Encoding.UTF8.GetString(sockets.Sockets[0].Sent[index]))).Message;
        }
        async Task PushAsync(Message message, string to)
        {
            message.CreatedTime = mediator.Clock.Now.ToUnixTimeSeconds();
            var packed = await server.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { to }, From: mediator.Did));
            sockets.Sockets[0].PushMessage(Encoding.UTF8.GetBytes(packed.Message));
        }
        DeliveredMessage[] Carrying(string packed) => new[] { new DeliveredMessage(Guid.NewGuid().ToString("N"), Encoding.UTF8.GetBytes(packed)) };

        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, ws).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        var liveOn = await ReceivedAsync(0);
        await PushAsync(MessagePickup.CreateStatus(mediator.Did, bob.Did, new PickupStatus(0, LiveDelivery: true), thid: liveOn.Id), bob.Did);
        var drain = await ReceivedAsync(1);
        await PushAsync(MessagePickup.CreateStatus(mediator.Did, bob.Did, new PickupStatus(0, LiveDelivery: true), thid: drain.Id), bob.Did);

        // The Identus shape: for `other`, addressed to `other`, threaded to a forward, on Bob's socket.
        var foreign = alice.Note(other.Did, "for the other DID");
        await PushAsync(MessagePickup.CreateDelivery(mediator.Did, other.Did, Carrying(await alice.AuthcryptAsync(foreign, other.Did)), thid: Guid.NewGuid().ToString()), other.Did);
        // Then the genuine push to Bob.
        var genuine = alice.Note(bob.Did, "for Bob");
        await PushAsync(MessagePickup.CreateDelivery(mediator.Did, bob.Did, Carrying(await alice.AuthcryptAsync(genuine, bob.Did))), bob.Did);

        // One receive loop reads the socket in order, and the stream yields pushes in the order they
        // reached it: had the foreign push reached the stream, it would be the first item.
        var yielded = await PickupE2E.Within(first);
        var item = stream.Current;
        var dropped = mediator.Logs.Entries.Count(l => l.Contains("Pushed message dropped: LocalMismatch", StringComparison.Ordinal));

        // End the stream (the item is not acknowledged): it turns live delivery off, and the scripted
        // mediator answers.
        var ending = stream.DisposeAsync().AsTask();
        var liveOff = await ReceivedAsync(2);
        await PushAsync(MessagePickup.CreateStatus(mediator.Did, bob.Did, new PickupStatus(0, LiveDelivery: false), thid: liveOff.Id), bob.Did);
        await ending.WaitAsync(MediatorTestServer.Timeout);

        yielded.Should().BeTrue();
        item.Error.Should().BeNull();
        item.Unpacked!.Message.Id.Should().Be(genuine.Id, "the first item yielded is the genuine push: the foreign one before it was never yielded");
        dropped.Should().Be(1, "the receive side refused the push for the other DID on Bob's socket");
        liveOn.Type.Should().Be(MessagePickup.LiveDeliveryChangeType);
        drain.Type.Should().Be(MessagePickup.DeliveryRequestType);
        liveOff.Type.Should().Be(MessagePickup.LiveDeliveryChangeType);
        mediator.LeakingLogLines(other.Did, bob.Did).Should().BeEmpty();
    }
}
