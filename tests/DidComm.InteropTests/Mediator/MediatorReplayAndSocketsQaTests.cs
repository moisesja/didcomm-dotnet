using System.Net;
using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// QA-2 (#95), replay admission (FR-RR-08) on the mediator's endpoints: the same stamped Coordinate
/// Mediation or pickup request applied once whether it is repeated over HTTP, over the WebSocket, or
/// once over each in either order — including the dangerous one, a captured <c>recipient-update</c>
/// "add" replayed after the DID was removed — and the host's global WebSocket switches
/// (<c>AllowSameSocketReplies = false</c>, <c>UseStomp = true</c>) leaving the mediator's socket alone
/// while still governing the host's own <c>MapDidCommWebSocket</c> endpoint.
/// </summary>
public sealed class MediatorReplayAndSocketsQaTests
{
    public enum Route
    {
        HttpThenHttp,
        SocketThenSocket,
        HttpThenSocket,
        SocketThenHttp,
    }

    [Theory]
    [InlineData(Route.HttpThenHttp)]
    [InlineData(Route.SocketThenSocket)]
    [InlineData(Route.HttpThenSocket)]
    [InlineData(Route.SocketThenHttp)]
    public async Task A_captured_recipient_update_add_replayed_after_the_DID_was_removed_does_not_register_it_again(Route route)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        var x = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(bob)).Should().Equal(mediator.Did);
        var add = mediator.Prepare(CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, new[] { new RecipientUpdate(x.Did, RecipientUpdateAction.Add) }));
        var captured = await mediator.AuthcryptAsync(bob, add);
        using var socket = await mediator.ConnectAsync();

        (await DeliverAsync(mediator, socket, bob, captured, first: true, route)).Should().BeTrue("the first copy is applied and answered");
        (await mediator.Mediation.FindGrantByRecipientDidAsync(x.Did, CancellationToken.None)).Should().NotBeNull();
        var remove = await mediator.AskAsync(bob, CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, new[] { new RecipientUpdate(x.Did, RecipientUpdateAction.Remove) }));
        CoordinateMediation.TryParseRecipientUpdateResponse(remove.Message, out var removed).Should().BeTrue();
        removed!.Should().ContainSingle().Which.Result.Should().Be(RecipientUpdateOutcome.Success);

        (await DeliverAsync(mediator, socket, bob, captured, first: false, route)).Should().BeFalse("the replay is dropped and answered with nothing");
        (await mediator.Mediation.FindGrantByRecipientDidAsync(x.Did, CancellationToken.None)).Should().BeNull("the replay did not register the DID again");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        mediator.Logs.Entries.Should().Contain(e => e.Contains("dropped: replay admission") && e.Contains(add.Id));
        mediator.LeakingLogLines(x.Did).Should().BeEmpty();
    }

    [Theory]
    [InlineData(Route.HttpThenHttp)]
    [InlineData(Route.SocketThenSocket)]
    [InlineData(Route.HttpThenSocket)]
    [InlineData(Route.SocketThenHttp)]
    public async Task A_stamped_mediate_request_and_a_stamped_delivery_request_are_each_applied_once(Route route)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        using var socket = await mediator.ConnectAsync();

        var mediate = await mediator.AuthcryptAsync(bob, mediator.Prepare(CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did)));
        (await DeliverAsync(mediator, socket, bob, mediate, first: true, route)).Should().BeTrue();
        (await DeliverAsync(mediator, socket, bob, mediate, first: false, route)).Should().BeFalse();
        await mediator.AddRecipientsAsync(bob, new[] { bob.Did });

        var alice = await mediator.NewSenderAsync();
        await Qa.SendNoteAsync(mediator, alice, bob);
        var fetch = await mediator.AuthcryptAsync(bob, mediator.Prepare(MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10)));
        (await DeliverAsync(mediator, socket, bob, fetch, first: true, route)).Should().BeTrue();
        (await DeliverAsync(mediator, socket, bob, fetch, first: false, route)).Should().BeFalse("a replayed delivery-request leases nothing");
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    [Fact]
    public async Task The_hosts_global_switches_leave_the_mediator_socket_answering_but_keep_the_hosts_own_socket_one_way()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o =>
            o.ConfigurePipeline = app => app.MapDidCommWebSocket("/other"));
        mediator.GlobalReceiveOptions.AllowSameSocketReplies.Should().BeFalse();
        var bob = await mediator.NewPeerAsync();

        // The host's own socket: same-socket replies are off, so a mediate-request is applied but never answered there.
        using (var other = await mediator.ConnectAsync("/other"))
        {
            await ReturnRouteTestHost.SendAsync(other, await mediator.AuthcryptAsync(bob, mediator.Prepare(CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did))));
            (await DrainUntilCloseAsync(other)).Should().Be(0, "no reply on a socket whose host switch is off");
        }
        (await mediator.Mediation.GetGrantAsync(bob.Did, CancellationToken.None)).Should().NotBeNull("dispatched, only not answered");

        // The mediator's socket answers.
        using var socket = await mediator.ConnectAsync();
        var status = MessagePickup.CreateStatusRequest(bob.Did, mediator.Did);
        (await mediator.AskOverSocketAsync(socket, bob, status)).Status(status.Id).MessageCount.Should().Be(0);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    [Fact]
    public async Task With_global_STOMP_the_hosts_own_socket_takes_no_bare_message_while_the_mediator_socket_does()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.GlobalStomp = true;
            o.PostConfigureServices = s => s.Configure<DidCommReceiveOptions>(r => r.AllowSameSocketReplies = true);
            o.ConfigurePipeline = app => app.MapDidCommWebSocket("/other");
        });
        var bob = await mediator.NewPeerAsync();

        using (var other = await mediator.ConnectAsync("/other"))
        {
            await ReturnRouteTestHost.SendAsync(other, await mediator.AuthcryptAsync(bob, mediator.Prepare(CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did))));
            (await DrainUntilCloseAsync(other)).Should().Be(0, "a bare DIDComm message is not a STOMP frame: no DIDComm reply");
        }

        using var socket = await mediator.ConnectAsync();
        (await mediator.RequestMediationOverSocketAsync(socket, bob)).Should().Equal(mediator.Did);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    /// <summary>Send <paramref name="packed"/> by the route's transport for this copy; true when an answer came back.</summary>
    private static async Task<bool> DeliverAsync(MediatorTestServer mediator, WebSocket socket, TestPeer peer, string packed, bool first, Route route)
    {
        var overSocket = route switch
        {
            Route.SocketThenSocket => true,
            Route.HttpThenSocket => !first,
            Route.SocketThenHttp => first,
            _ => false,
        };
        if (!overSocket)
        {
            var response = await mediator.PostAsync(packed);
            if (response.Status == HttpStatusCode.Accepted)
            {
                response.Body.Should().BeEmpty();
                return false;
            }
            response.Status.Should().Be(HttpStatusCode.OK);
            return true;
        }

        // On the socket: the frame that comes back next answers either this message or the control
        // request sent after it (the endpoint handles one message at a time, in order).
        await ReturnRouteTestHost.SendAsync(socket, packed);
        var control = MessagePickup.CreateStatusRequest(peer.Did, mediator.Did);
        await ReturnRouteTestHost.SendAsync(socket, await mediator.AuthcryptAsync(peer, mediator.Prepare(control)));
        var next = await mediator.ReceiveOverSocketAsync(socket, peer);
        if (next.Message.Thid == control.Id)
            return false;
        (await mediator.ReceiveOverSocketAsync(socket, peer)).Message.Thid.Should().Be(control.Id);
        return true;
    }

    /// <summary>Start the close handshake and count the binary messages received before the close.</summary>
    private static async Task<int> DrainUntilCloseAsync(WebSocket socket)
    {
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        var binary = 0;
        while (true)
        {
            WebSocketMessageType type;
            try
            {
                (type, _) = await ReturnRouteTestHost.ReceiveAsync(socket, MediatorTestServer.Timeout);
            }
            catch (WebSocketException)
            {
                return binary; // the server dropped the connection
            }
            if (type == WebSocketMessageType.Close)
                return binary;
            if (type == WebSocketMessageType.Binary)
                binary++;
        }
    }
}

internal static class MediatorTestServerQaExtensions
{
    /// <summary><c>mediate-request</c> → <c>mediate-grant</c> over <paramref name="socket"/>.</summary>
    public static async Task<IReadOnlyList<string>> RequestMediationOverSocketAsync(this MediatorTestServer mediator, WebSocket socket, TestPeer peer)
    {
        var granted = await mediator.AskOverSocketAsync(socket, peer, CoordinateMediation.CreateMediateRequest(peer.Did, mediator.Did));
        CoordinateMediation.TryParseGrant(granted.Message, out var routingDids).Should().BeTrue();
        return routingDids!;
    }
}
