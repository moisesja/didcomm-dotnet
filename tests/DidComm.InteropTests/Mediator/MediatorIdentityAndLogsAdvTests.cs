using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// ADV-1 (#95): identity, authorization, hostile input and logs through the real
/// <c>MapDidCommMediator</c> endpoints. Mallory has no grant (or her own); Bob is enrolled with his
/// own DID; Alice is any sender.
/// </summary>
public sealed class MediatorIdentityAndLogsAdvTests
{
    // ── Whose mail: the peel cannot execute a captured mediator-service request ──────────

    [Fact]
    public async Task A_captured_delivery_request_of_Bob_rewrapped_by_Mallory_in_a_self_addressed_forward_is_never_executed()
    {
        // Forwards are outside FR-RR-08 replay admission. If the peel dispatched what it opened, a
        // captured (never posted) delivery-request of Bob's, wrapped in a forward addressed to the
        // mediator itself, would lease Bob's messages with no session to send them on.
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var mallory = await mediator.NewSenderAsync();
        var note = await Qa.SendNoteAsync(mediator, alice, bob);

        var captured = await mediator.AuthcryptAsync(bob, mediator.Prepare(MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10)));
        var (wrapped, _) = await mallory.ForwardAsync(mediator.Did, mediator.Did, captured);
        await Qa.PostForwardAsync(mediator, wrapped);
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.DroppedMalformed);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1);
        var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle("Bob's message was never leased by the captured request").Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, delivered.Payload, alice)).Message.Id.Should().Be(note.Id);

        // The captured request was never admitted either: Bob can still use it himself.
        var response = await mediator.PostAsync(captured);
        response.Status.Should().Be(HttpStatusCode.OK, "the captured request reached replay admission only now");
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task An_anoncrypted_delivery_request_claiming_to_be_from_Bob_is_answered_202_and_leases_nothing()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var mallory = await mediator.NewSenderAsync();
        await Qa.SendNoteAsync(mediator, alice, bob);

        foreach (var request in new[]
        {
            MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10),
            MessagePickup.CreateMessagesReceived(bob.Did, mediator.Did, new[] { "00000000000000000000000000000000" }),
            MessagePickup.CreateStatusRequest(bob.Did, mediator.Did),
        })
        {
            var forged = (await mallory.Client.PackEncryptedAsync(mediator.Prepare(request), new PackEncryptedOptions(Recipients: new[] { mediator.Did }))).Message;
            var response = await mediator.PostAsync(forged);
            response.Status.Should().Be(HttpStatusCode.Accepted);
            response.Body.Should().BeEmpty("a request without an authcrypt layer has no peer and gets no answer");
        }

        (await mediator.FetchAsync(bob)).Should().ContainSingle("nothing was leased or acknowledged by the forged requests");
    }

    // ── Live sessions: a second DID on Mallory's socket ───────────────────────────────────

    [Fact]
    public async Task Bobs_captured_live_delivery_change_sent_on_Mallorys_own_session_socket_does_not_route_Bobs_mail_to_it()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        var mallory = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        await mediator.EnrolAsync(mallory);
        var alice = await mediator.NewSenderAsync();
        var captured = await mediator.AuthcryptAsync(bob, mediator.Prepare(MessagePickup.CreateLiveDeliveryChange(bob.Did, mediator.Did, true)));

        using var socket = await mediator.ConnectAsync();
        var own = MessagePickup.CreateStatusRequest(mallory.Did, mediator.Did);
        (await mediator.AskOverSocketAsync(socket, mallory, own)).Status(own.Id);
        await ReturnRouteTestHost.SendAsync(socket, captured);
        var note = await Qa.SendNoteAsync(mediator, alice, bob);

        mediator.Outcomes.Should().Equal(new[] { ForwardOutcomes.Queued }, "Bob has no live session: the socket is bound to Mallory");
        var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, delivered.Payload, alice)).Message.Id.Should().Be(note.Id);
        Func<Task> nothingForMallory = () => ReturnRouteTestHost.ReceiveAsync(socket, TimeSpan.FromMilliseconds(300));
        await nothingForMallory.Should().ThrowAsync<OperationCanceledException>("neither the status for Bob nor Bob's delivery is written on Mallory's socket");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    [Fact]
    public async Task Whoever_sends_Bobs_unsent_authcrypted_live_delivery_change_first_on_a_fresh_socket_holds_Bobs_live_session_today()
    {
        // Not a #95 change (the session rules are #91/#94's): message-level authentication has no
        // channel binding, so a fresh, never-delivered authcrypt envelope of Bob's that reaches the
        // mediator first on any socket opens Bob's session there. Mallory cannot read what is pushed
        // (it is encrypted to Bob) but holds it under the live lease until her socket closes, and
        // Bob's own copy of the request is then a replay. Requires capturing Bob's envelope before
        // delivery (a hop without TLS, or a compromised one).
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var captured = await mediator.AuthcryptAsync(bob, mediator.Prepare(MessagePickup.CreateLiveDeliveryChange(bob.Did, mediator.Did, true)));

        using var socket = await mediator.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, captured);
        (await ReturnRouteTestHost.ReceiveAsync(socket, MediatorTestServer.Timeout)).Type.Should().Be(WebSocketMessageType.Binary, "the status for Bob");
        await Qa.SendNoteAsync(mediator, alice, bob);

        // L-020 (PR #109 review R1-2): the forward pump now only stores (Queued); the push workers push it live.
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        (await ReturnRouteTestHost.ReceiveAsync(socket, MediatorTestServer.Timeout)).Type.Should().Be(WebSocketMessageType.Binary, "Bob's live delivery");
        await mediator.PushesIdleAsync();
        mediator.PushOutcomes.Should().Equal(DidComm.Mediator.Pipeline.PushOutcomes.PushedLive);
        (await mediator.FetchAsync(bob)).Should().BeEmpty("the message is leased to the session Mallory holds");
        (await mediator.PostAsync(captured)).Status.Should().Be(HttpStatusCode.Accepted, "Bob's own copy is now a replay");

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        var deadline = DateTime.UtcNow + MediatorTestServer.Timeout;
        IReadOnlyList<DeliveredMessage> again;
        while ((again = await mediator.FetchAsync(bob)).Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        again.Should().ContainSingle("closing the socket gives the lease back");
    }

    // ── The peel over the mediator WebSocket (no acknowledgement there) ───────────────────

    [Fact]
    public async Task A_self_addressed_forward_sent_on_the_mediator_WebSocket_is_opened_and_relayed_and_nothing_comes_back()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(bob.Did);
        var (inner, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(note, bob.Did));
        var (outer, _) = await alice.ForwardAsync(mediator.Did, mediator.Did, inner);

        using var socket = await mediator.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, outer);
        await MediatorTestServer.WaitUntilAsync(() => mediator.Outcomes.Count >= 1, "the forward is relayed");
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);

        var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, delivered.Payload, alice)).Message.Id.Should().Be(note.Id);
        Func<Task> nothingBack = () => ReturnRouteTestHost.ReceiveAsync(socket, TimeSpan.FromMilliseconds(300));
        await nothingBack.Should().ThrowAsync<OperationCanceledException>("a forward is one-way: no frame comes back on the socket");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ── DEFECT 5: exception text from the composed endpoints ──────────────────────────────

    private const string Marker = "ADVMARKER-did:peer:2.Ez6LSmallory";

    [Fact]
    public async Task ADV1_D5_No_line_the_mediator_endpoints_write_for_a_strangers_rejected_message_carries_its_text()
    {
        await using var mediator = await MediatorTestServer.StartAsync();

        (await PostPlaintextAsync(mediator, HostilePlaintext())).Should().Be(HttpStatusCode.BadRequest);
        using (var socket = await mediator.ConnectAsync())
        {
            await ReturnRouteTestHost.SendAsync(socket, HostilePlaintext());
            await MediatorTestServer.WaitUntilAsync(() => mediator.Logs.Entries.Count(e => e.Contains("MapDidCommWebSocket", StringComparison.Ordinal)) >= 1, "the socket logged the discard");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        }

        mediator.Logs.Entries.Should().NotContain(e => e.Contains(Marker, StringComparison.Ordinal));
    }

    /// <summary>A plaintext message whose id breaks FR-MSG-02: a DID-looking marker and a line break.</summary>
    private static string HostilePlaintext()
        => "{\"id\":\"x " + Marker + "\\nINFO: forged line\",\"type\":\"https://didcomm.org/basicmessage/2.0/message\",\"body\":{}}";

    private static async Task<HttpStatusCode> PostPlaintextAsync(MediatorTestServer mediator, string plaintext)
    {
        using var client = mediator.Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, mediator.Options.Pattern)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(plaintext)),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.Plaintext);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }
}
