using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Transports.ReturnRoute;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #110, owner decision OD-3: every plain receive path of DidComm.AspNetCore opens a message a
/// mediator re-wrapped for this agent (routing.md "Rewrapping": "The outer envelope is a forward
/// message where to is the receiver itself"), with the rule the mediator client uses (at most 8
/// layers; anoncrypted plaintext, one attachment, <c>next</c> = the DID that decrypted it), and
/// handles the message inside as if it had arrived on its own: its own envelope authenticates it,
/// and replay admission, <c>created_time</c> and the id cap apply to it, but it never gets a return
/// route (return_route.md L9: only between the immediate sender and the agent it connects to). Any other forward is left
/// as it arrived; a registered handler for forwards (the routing role) gets every forward as before;
/// whatever fails inside an opened layer is refused like an envelope that does not unpack. The five
/// paths: the two HTTP callback overloads, the registry-aware HTTP endpoint, and the two WebSocket
/// overloads. A WebSocket delivery is followed by an authcrypted fence on the same socket: the loop
/// handles messages in order, so once the fence was handled, the delivery was too (L-065).
/// </summary>
public sealed class RewrappedReceiveTests
{
    public enum PlainPath
    {
        HttpCallback,
        HttpRegistry,
        HttpSessionCallback,
        WebSocketCallback,
        WebSocketRegistry,
    }

    public static TheoryData<PlainPath> AllPaths => new()
    {
        PlainPath.HttpCallback, PlainPath.HttpRegistry, PlainPath.HttpSessionCallback, PlainPath.WebSocketCallback, PlainPath.WebSocketRegistry,
    };

    /// <summary>The paths that run replay admission (FR-RR-08): the registry-aware endpoints and the session-aware callback.</summary>
    public static TheoryData<PlainPath> AdmittingPaths => new() { PlainPath.HttpRegistry, PlainPath.HttpSessionCallback, PlainPath.WebSocketRegistry };

    public static TheoryData<PlainPath> RegistryPaths => new() { PlainPath.HttpRegistry, PlainPath.WebSocketRegistry };

    public static TheoryData<PlainPath, int> PathsAndDepths()
    {
        var data = new TheoryData<PlainPath, int>();
        foreach (var path in Enum.GetValues<PlainPath>())
        {
            data.Add(path, ForwardHandler.MaxSelfAddressedLayers);
            data.Add(path, ForwardHandler.MaxSelfAddressedLayers + 1);
        }
        return data;
    }

    public enum InnerEnvelope
    {
        Authcrypt,
        Anoncrypt,
        SignedInAnoncrypt,
    }

    public static TheoryData<PlainPath, InnerEnvelope> PathsAndEnvelopes()
    {
        var data = new TheoryData<PlainPath, InnerEnvelope>();
        foreach (var path in Enum.GetValues<PlainPath>())
        {
            foreach (var envelope in Enum.GetValues<InnerEnvelope>())
                data.Add(path, envelope);
        }
        return data;
    }

    public enum Forged
    {
        AForwardNamingAnotherDid,
        AMessageEncryptedToAnotherDid,
    }

    public static TheoryData<PlainPath, Forged> PathsAndForgeries()
    {
        var data = new TheoryData<PlainPath, Forged>();
        foreach (var path in Enum.GetValues<PlainPath>())
        {
            data.Add(path, Forged.AForwardNamingAnotherDid);
            data.Add(path, Forged.AMessageEncryptedToAnotherDid);
        }
        return data;
    }

    // ---- what is opened ---------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllPaths))]
    public async Task A_message_rewrapped_for_this_agent_reaches_the_consumer_on_every_plain_path(PlainPath path)
    {
        await using var r = await PlainReceivers.StartAsync();
        var message = r.Host.RecordingMessage(r.Host.Alice).Build();
        var packed = await r.RewrapAsync(await r.Host.AuthcryptAsync(r.Host.Alice, message), layers: 1);

        var delivery = await r.DeliverAsync(path, packed);

        delivery.AssertAccepted(path);
        var seen = delivery.Seen.Should().ContainSingle("the message inside, and only it, reaches the consumer").Subject;
        seen.Unpacked.Message.Id.Should().Be(message.Id);
        seen.Unpacked.Message.Type.Should().Be(message.Type);
        r.Host.Alice.Kids.Should().Contain(seen.Unpacked.SenderKid, "the message inside is Alice's authcrypt");
        r.Host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(PathsAndDepths))]
    public async Task Eight_layers_are_opened_and_a_ninth_is_refused_with_nothing_handed_on(PlainPath path, int layers)
    {
        await using var r = await PlainReceivers.StartAsync();
        var message = r.Host.RecordingMessage(r.Host.Alice).Build();
        var packed = await r.RewrapAsync(await r.Host.AuthcryptAsync(r.Host.Alice, message), layers);

        var delivery = await r.DeliverAsync(path, packed);

        if (layers <= ForwardHandler.MaxSelfAddressedLayers)
        {
            delivery.AssertAccepted(path);
            delivery.Seen.Should().ContainSingle().Which.Unpacked.Message.Id.Should().Be(message.Id);
        }
        else
        {
            delivery.AssertRefused(path, r.Host, "MalformedMessageException");
        }
        r.Host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- the message inside is authenticated by its own envelope ----------------------------------

    [Theory]
    [MemberData(nameof(PathsAndEnvelopes))]
    public async Task The_message_inside_is_authenticated_by_its_own_envelope_never_by_a_layer(PlainPath path, InnerEnvelope envelope)
    {
        await using var r = await PlainReceivers.StartAsync();
        var host = r.Host;
        var message = envelope == InnerEnvelope.Anoncrypt
            ? new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/anonymous").WithTo(host.ServerDid).Build()
            : host.RecordingMessage(host.Alice).Build();
        var inner = envelope switch
        {
            InnerEnvelope.Authcrypt => await host.AuthcryptAsync(host.Alice, message),
            InnerEnvelope.Anoncrypt => await host.AnoncryptAsync(message),
            _ => (await host.PackAsync(host.Alice, message, EnvelopeShape.AnoncryptOfSigned)).Packed,
        };

        var delivery = await r.DeliverAsync(path, await r.RewrapAsync(inner, layers: 1));

        delivery.AssertAccepted(path);
        var seen = delivery.Seen.Should().ContainSingle().Subject.Unpacked;
        seen.Message.Id.Should().Be(message.Id);
        seen.Encrypted.Should().BeTrue();
        switch (envelope)
        {
            case InnerEnvelope.Authcrypt:
                seen.Authenticated.Should().BeTrue();
                seen.AnonymousSender.Should().BeFalse();
                host.Alice.Kids.Should().Contain(seen.SenderKid);
                seen.SignerKid.Should().BeNull();
                break;
            case InnerEnvelope.Anoncrypt:
                seen.AnonymousSender.Should().BeTrue();
                seen.Authenticated.Should().BeFalse();
                seen.SenderKid.Should().BeNull();
                seen.SignerKid.Should().BeNull();
                break;
            default:
                seen.NonRepudiation.Should().BeTrue();
                host.Alice.Kids.Should().Contain(seen.SignerKid, "the signature inside is Alice's");
                seen.SenderKid.Should().BeNull("an anoncrypt envelope names no sender");
                break;
        }
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- no return route for a message opened out of layers (return_route.md L9) -----------------

    public static TheoryData<PlainPath, bool> SessionPathsOpenedOrNot()
    {
        var data = new TheoryData<PlainPath, bool>();
        foreach (var path in new[] { PlainPath.HttpRegistry, PlainPath.HttpSessionCallback, PlainPath.WebSocketRegistry })
        {
            data.Add(path, true);
            data.Add(path, false);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(SessionPathsOpenedOrNot))]
    public async Task A_message_opened_out_of_layers_gets_no_return_route_while_the_same_message_unwrapped_gets_its_reply_in_band(PlainPath path, bool opened)
    {
        // return_route.md L9: "This extension is only to be used between the immediate sending agent
        // and the agent it connects directly to. This extension is not valid when routing keys are in
        // use." The connection an opened message arrived on is the mediator's: no session, and the
        // reply is not written there (202 with an empty body over HTTP, nothing on the socket).
        await using var r = await PlainReceivers.StartAsync(o => o.AllowSameSocketReplies = true);
        var host = r.Host;
        var message = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        r.Reply = received => received.Message.Id == message.Id ? ReturnRouteHttpTests.ReplyTo(host, received.Message, host.Alice.Did) : null;
        var inner = await host.AuthcryptAsync(host.Alice, message);
        var packed = opened ? await r.RewrapAsync(inner, layers: 2) : inner;

        string? replyText = null;
        RecordedSeen seen;
        if (path == PlainPath.WebSocketRegistry)
        {
            using var socket = await host.ConnectAsync(PlainReceivers.PathOf(path));
            await ReturnRouteTestHost.SendAsync(socket, packed);
            if (opened)
            {
                await r.FenceAsync(socket);
                host.Registry.Count.Should().Be(0, "an opened message opens no session");
                await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(socket);
            }
            else
            {
                var frame = await ReturnRouteTestHost.ReceiveAsync(socket, TimeSpan.FromSeconds(15));
                frame.Type.Should().Be(WebSocketMessageType.Text);
                replyText = frame.Text;
                host.Registry.Count.Should().Be(1, "the unwrapped message opened the connection's session");
                await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(socket);
            }
            seen = r.SeenOf(message.Id);
        }
        else
        {
            var response = await host.PostAsync(packed, PlainReceivers.PathOf(path));
            if (opened)
            {
                response.Status.Should().Be(HttpStatusCode.Accepted, "no return route: the answer is the plain 202");
                response.Body.Should().BeEmpty("the reply is not returned in the mediator's response");
            }
            else
            {
                response.Status.Should().Be(HttpStatusCode.OK, "the unwrapped message gets its reply as the 200 body (FR-RR-04)");
                replyText = response.BodyText;
            }
            seen = r.SeenOf(message.Id);
        }

        if (opened)
        {
            seen.Session.Should().BeNull("a message opened out of re-wrapped layers never gets a return-route session");
            host.Logs.Entries.Should().Contain(e => e.Contains("cannot open a session (Routed", StringComparison.Ordinal) && e.Contains(message.Id, StringComparison.Ordinal));
        }
        else
        {
            seen.Session.Should().NotBeNull();
            seen.Session!.PeerDid.Should().Be(host.Alice.Did);
            var reply = await host.Alice.Client.UnpackAsync(replyText!);
            reply.Message.Thid.Should().Be(message.Id, "the in-band reply answers the message");
        }
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task On_a_session_socket_a_message_opened_out_of_layers_neither_uses_nor_changes_the_session()
    {
        // Alice opens a session on the socket with an unwrapped message (return_route all). A message
        // of hers opened out of layers, asking for return_route none, is dispatched without the
        // session and does not change its mode: her next unwrapped header-less message still gets its
        // reply on the socket.
        await using var r = await PlainReceivers.StartAsync(o => o.AllowSameSocketReplies = true);
        var host = r.Host;
        var opening = host.Stamped(host.RecordingMessage(host.Alice, "opening").WithReturnRoute(ReturnRouteMode.All)).Build();
        var routed = host.RecordingMessage(host.Alice, "routed").WithReturnRoute(ReturnRouteMode.None).Build();
        var later = host.RecordingMessage(host.Alice, "later").Build();
        var answered = new[] { opening.Id, routed.Id, later.Id };
        r.Reply = received => answered.Contains(received.Message.Id) ? ReturnRouteHttpTests.ReplyTo(host, received.Message, host.Alice.Did) : null;
        using var socket = await host.ConnectAsync(PlainReceivers.PathOf(PlainPath.WebSocketRegistry));

        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, opening));
        var first = await ReturnRouteTestHost.ReceiveAsync(socket, TimeSpan.FromSeconds(15));
        (await host.Alice.Client.UnpackAsync(first.Text)).Message.Thid.Should().Be(opening.Id);
        await ReturnRouteTestHost.SendAsync(socket, await r.RewrapAsync(await host.AuthcryptAsync(host.Alice, routed), layers: 1));
        await r.FenceAsync(socket);
        r.SeenOf(routed.Id).Session.Should().BeNull("the opened message is dispatched without the connection's session");
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, later));
        var second = await ReturnRouteTestHost.ReceiveAsync(socket, TimeSpan.FromSeconds(15));

        (await host.Alice.Client.UnpackAsync(second.Text)).Message.Thid.Should().Be(later.Id,
            "the opened message's return_route none did not stop the session, and its reply was never written");
        r.SeenOf(later.Id).Session.Should().NotBeNull();
        host.Registry.Count.Should().Be(1);
        await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(socket);
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- replay, created_time and the id cap apply to the message inside --------------------------

    [Theory]
    [MemberData(nameof(AdmittingPaths))]
    public async Task The_same_message_inside_two_different_wrappers_is_admitted_once(PlainPath path)
    {
        await using var r = await PlainReceivers.StartAsync();
        var host = r.Host;
        var message = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var inner = await host.AuthcryptAsync(host.Alice, message);
        var first = await r.RewrapAsync(inner, layers: 1);
        var second = await r.RewrapAsync(inner, layers: 1);
        first.Should().NotBe(second, "two wrappers: a fresh forward each time, with its own id and envelope");

        var a = await r.DeliverAsync(path, first);
        var b = await r.DeliverAsync(path, second);

        a.Seen.Should().ContainSingle().Which.Unpacked.Message.Id.Should().Be(message.Id);
        b.Seen.Should().BeEmpty("replay admission keys on the sender and id of the message inside (FR-RR-08), whatever wraps it");
        if (b.Status is { } status)
            status.Should().Be(HttpStatusCode.Accepted, "a replay is answered like a dropped message");
        host.DropLines(message.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.Duplicate));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData(PlainPath.HttpCallback)]
    [InlineData(PlainPath.WebSocketCallback)]
    public async Task On_an_endpoint_without_replay_admission_the_message_inside_is_handed_on_each_time_as_an_unwrapped_one_would_be(PlainPath path)
    {
        // These overloads run no FR-RR-08 admission for any message (their documented contract): the
        // message inside is treated exactly as if it had arrived unwrapped.
        await using var r = await PlainReceivers.StartAsync();
        var host = r.Host;
        var message = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var inner = await host.AuthcryptAsync(host.Alice, message);

        var a = await r.DeliverAsync(path, await r.RewrapAsync(inner, layers: 1));
        var b = await r.DeliverAsync(path, await r.RewrapAsync(inner, layers: 1));

        a.Seen.Should().ContainSingle().Which.Unpacked.Message.Id.Should().Be(message.Id);
        b.Seen.Should().ContainSingle().Which.Unpacked.Message.Id.Should().Be(message.Id);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(AdmittingPaths))]
    public async Task A_stale_message_inside_is_refused_by_replay_admission(PlainPath path)
    {
        await using var r = await PlainReceivers.StartAsync();
        var host = r.Host;
        var message = host.RecordingMessage(host.Alice)
            .WithReturnRoute(ReturnRouteMode.All)
            .WithCreatedTime(host.Clock.Now.AddMinutes(-10).ToUnixTimeSeconds())
            .Build();

        var delivery = await r.DeliverAsync(path, await r.RewrapAsync(await host.AuthcryptAsync(host.Alice, message), layers: 1));

        delivery.Seen.Should().BeEmpty("the created_time of the message inside is judged (FR-RR-08), not the wrapper's");
        if (delivery.Status is { } status)
            status.Should().Be(HttpStatusCode.Accepted);
        host.DropLines(message.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.TooOld));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(AllPaths))]
    public async Task A_message_inside_with_an_id_over_512_characters_is_refused(PlainPath path)
    {
        await using var r = await PlainReceivers.StartAsync();
        var host = r.Host;
        var message = host.RecordingMessage(host.Alice).Build();
        message.Id = "zqxlong" + new string('q', 513 - 7);
        var inner = await host.RawAuthcryptAsync(host.Alice, ReturnRouteTestHost.UnvalidatedJson(message));

        var delivery = await r.DeliverAsync(path, await r.RewrapAsync(inner, layers: 1));

        delivery.AssertRefused(path, host, "MalformedMessageException");
        host.Logs.Entries.Should().NotContain(e => e.Contains("zqxlong", StringComparison.Ordinal), "the refused id is never written");
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- what is refused once a layer is opened --------------------------------------------------

    [Theory]
    [MemberData(nameof(AllPaths))]
    public async Task A_tampered_message_inside_is_refused(PlainPath path)
    {
        await using var r = await PlainReceivers.StartAsync();
        var host = r.Host;
        var inner = JsonNode.Parse(await host.AuthcryptAsync(host.Alice, host.RecordingMessage(host.Alice).Build()))!.AsObject();
        var ciphertext = inner["ciphertext"]!.GetValue<string>();
        inner["ciphertext"] = (ciphertext[0] == 'A' ? 'B' : 'A') + ciphertext[1..];

        var delivery = await r.DeliverAsync(path, await r.RewrapAsync(inner.ToJsonString(), layers: 1));

        delivery.AssertRefused(path, host, exceptionType: null);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(PathsAndForgeries))]
    public async Task A_layer_addressed_to_this_agent_around_a_message_for_someone_else_is_not_opened_as_ours(PlainPath path, Forged forged)
    {
        await using var r = await PlainReceivers.StartAsync();
        var host = r.Host;
        string inside;
        if (forged == Forged.AForwardNamingAnotherDid)
        {
            // A forward this agent can decrypt, naming Carol, around a message this agent could even
            // read: this agent is not a mediator, so it opens neither.
            var readable = await host.AuthcryptAsync(host.Alice, host.RecordingMessage(host.Alice).Build());
            inside = await host.AnoncryptAsync(ForwardMessage.Create(host.ServerDid, host.Carol.Did, new[] { readable }));
        }
        else
        {
            inside = (await host.Alice.Client.PackEncryptedAsync(
                new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/for-carol").WithFrom(host.Alice.Did).WithTo(host.Carol.Did).Build(),
                new PackEncryptedOptions(Recipients: new[] { host.Carol.Did }, From: host.Alice.Did, Forward: false))).Message;
        }

        var delivery = await r.DeliverAsync(path, await r.RewrapAsync(inside, layers: 1));

        delivery.AssertRefused(path, host, forged == Forged.AForwardNamingAnotherDid ? "ConsistencyException" : null);
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- what is left as it arrived ----------------------------------------------------------------

    public enum OtherForward
    {
        NamingAnotherDid,
        TooBrokenToRead,
        AddressedToThisAgentButAuthcrypted,
    }

    public static TheoryData<PlainPath, OtherForward> PathsAndOtherForwards()
    {
        var data = new TheoryData<PlainPath, OtherForward>();
        foreach (var path in Enum.GetValues<PlainPath>())
        {
            foreach (var shape in Enum.GetValues<OtherForward>())
                data.Add(path, shape);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(PathsAndOtherForwards))]
    public async Task A_forward_that_is_not_rewrapped_for_this_agent_reaches_the_consumer_as_it_arrived_and_is_relayed_nowhere(PlainPath path, OtherForward shape)
    {
        // Today's behaviour, pinned before the change and unchanged by it: a plain endpoint is not a
        // mediator. A callback gets the forward itself; the registry-aware endpoints find no handler
        // for it (none is registered) and answer 202. A forward for someone else, one too broken to
        // read (no next), and one addressed to this agent but authcrypted (a sender's message, not a
        // mediator's wrapper) are all left as they arrived.
        await using var r = await PlainReceivers.StartAsync();
        var host = r.Host;
        var forCarol = await host.Alice.Client.PackEncryptedAsync(
            new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/for-carol").WithFrom(host.Alice.Did).WithTo(host.Carol.Did).Build(),
            new PackEncryptedOptions(Recipients: new[] { host.Carol.Did }, From: host.Alice.Did, Forward: false));
        var forUs = await host.AuthcryptAsync(host.Alice, host.RecordingMessage(host.Alice).Build());
        Message forward;
        string packed;
        switch (shape)
        {
            case OtherForward.NamingAnotherDid:
                forward = ForwardMessage.Create(host.ServerDid, host.Carol.Did, new[] { forCarol.Message });
                packed = await host.AnoncryptAsync(forward);
                break;
            case OtherForward.TooBrokenToRead:
                forward = new MessageBuilder()
                    .WithType(ForwardConstants.ForwardTypeUri)
                    .WithTo(host.ServerDid)
                    .WithBody(new JsonObject())
                    .WithAttachment(ForwardMessage.Create(host.ServerDid, host.ServerDid, new[] { forUs }).Attachments![0])
                    .Build();
                packed = await host.AnoncryptAsync(forward);
                break;
            default:
                forward = ForwardMessage.Create(host.ServerDid, host.ServerDid, new[] { forUs });
                packed = await host.AuthcryptAsync(host.Alice, forward);
                break;
        }

        var delivery = await r.DeliverAsync(path, packed);

        delivery.AssertAccepted(path);
        if (path is PlainPath.HttpRegistry or PlainPath.WebSocketRegistry)
        {
            delivery.Seen.Should().BeEmpty("no handler is registered for forwards");
            host.Logs.Entries.Should().Contain(e => e.Contains("No protocol handler registered", StringComparison.Ordinal) && e.Contains(forward.Id, StringComparison.Ordinal));
        }
        else
        {
            var seen = delivery.Seen.Should().ContainSingle().Subject.Unpacked;
            seen.Message.Id.Should().Be(forward.Id, "the callback gets the forward itself");
            seen.Message.Type.Should().Be(ForwardConstants.ForwardTypeUri);
        }
        host.LeakingLogLines().Should().BeEmpty();
    }

    public enum InsideTheLayer
    {
        AMessage,
        AForwardForTheNextHop,
    }

    public static TheoryData<PlainPath, InsideTheLayer> RegistryPathsAndInsides()
    {
        var data = new TheoryData<PlainPath, InsideTheLayer>();
        foreach (var path in new[] { PlainPath.HttpRegistry, PlainPath.WebSocketRegistry })
        {
            data.Add(path, InsideTheLayer.AMessage);
            data.Add(path, InsideTheLayer.AForwardForTheNextHop);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(RegistryPathsAndInsides))]
    public async Task With_a_handler_registered_for_forwards_a_layer_addressed_to_this_agent_is_opened_only_to_a_message(PlainPath path, InsideTheLayer inside)
    {
        // The routing role (AddRoutingMediator(), AddMediator()) owns every forward to relay: a layer
        // addressed to its own DID around a forward for the next hop goes to its handler as it arrived
        // (its ForwardHandler opens it under its own rules). Around a message that is not a forward,
        // the layer is opened and the message is this agent's own (owner decision OD-6 on #110):
        // dispatched to its own handler, under its own envelope, with no return route.
        await using var r = await PlainReceivers.StartAsync(o => o.ConfigureDidComm = b => b.AddProtocol<RoutingRecordingHandler>());
        var host = r.Host;
        var message = host.RecordingMessage(host.Alice).Build();
        var authcrypted = await host.AuthcryptAsync(host.Alice, message);
        var content = inside == InsideTheLayer.AMessage
            ? authcrypted
            : await host.AnoncryptAsync(ForwardMessage.Create(host.ServerDid, host.Carol.Did, new[] { authcrypted }));
        var packed = await r.RewrapAsync(content, layers: 1);

        var delivery = await r.DeliverAsync(path, packed);

        delivery.AssertAccepted(path);
        var seen = delivery.Seen.Should().ContainSingle().Subject;
        if (inside == InsideTheLayer.AMessage)
        {
            seen.Unpacked.Message.Id.Should().Be(message.Id, "the message's own handler got it, not the routing handler");
            host.Alice.Kids.Should().Contain(seen.Unpacked.SenderKid, "under its own envelope");
            seen.Session.Should().BeNull("no return route for a message opened out of layers");
        }
        else
        {
            seen.Unpacked.Message.Type.Should().Be(ForwardConstants.ForwardTypeUri, "the routing handler got the forward as it arrived");
            ForwardMessage.TryParse(seen.Unpacked.Message, out var next, out _).Should().BeTrue();
            next.Should().Be(host.ServerDid, "the received layer itself, never the forward inside it");
        }
        host.LeakingLogLines().Should().BeEmpty();
    }
}

/// <summary>
/// The five plain receive paths on one <see cref="ReturnRouteTestHost"/>: <c>/plain</c> (the
/// callback overload of <c>MapDidCommEndpoint</c>), <c>/didcomm</c> (registry-aware),
/// <c>/didcomm-callback</c> (the session-aware callback), <c>/ws-plain</c> (the callback overload of
/// <c>MapDidCommWebSocket</c>) and <c>/ws</c> (registry-aware). Every consumer records what it got
/// into one queue (the registry-aware ones through the recording handler's context).
/// </summary>
internal sealed class PlainReceivers : IAsyncDisposable
{
    private const string FencePrefix = "zqxfence";
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(15);

    private readonly ConcurrentQueue<(UnpackResult Unpacked, IReturnRouteSession? Session)> _seen = new();

    private PlainReceivers()
    {
    }

    public ReturnRouteTestHost Host { get; private set; } = null!;

    /// <summary>What the registry-aware handler and the session-aware callback return for a message (default: nothing).</summary>
    public Func<UnpackResult, Message?>? Reply { get; set; }

    /// <summary>How many records the consumers made for the message with id <paramref name="messageId"/>.</summary>
    public int SeenCount(string messageId) => _seen.Count(s => s.Unpacked.Message.Id == messageId);

    /// <summary>The one record a consumer made for the message with id <paramref name="messageId"/>.</summary>
    public RecordedSeen SeenOf(string messageId)
    {
        var record = _seen.Should().ContainSingle(s => s.Unpacked.Message.Id == messageId).Subject;
        return new RecordedSeen(record.Unpacked, record.Session);
    }

    /// <summary>
    /// Send an authcrypted fence on <paramref name="socket"/> and wait until it was handled: everything
    /// sent before it was handled too (the loop handles messages in order). Authcrypted, so a host that
    /// accepts encrypted messages only takes it as well; it asks for no return route.
    /// </summary>
    public async Task FenceAsync(WebSocket socket)
    {
        var fence = Host.RecordingMessage(Host.Alice, "fence")
            .WithId(FencePrefix + Guid.NewGuid().ToString("N"))
            .Build();
        await ReturnRouteTestHost.SendAsync(socket, await Host.AuthcryptAsync(Host.Alice, fence));
        (await ReturnRouteTestHost.WaitUntilAsync(() => _seen.Any(s => s.Unpacked.Message.Id == fence.Id), Hang))
            .Should().BeTrue("the fence sent after the delivery was handled, so the delivery was too");
    }

    public static async Task<PlainReceivers> StartAsync(Action<ReturnRouteTestHostOptions>? more = null)
    {
        var receivers = new PlainReceivers();
        receivers.Host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.Callback = (unpacked, session, _) =>
            {
                receivers._seen.Enqueue((unpacked, session));
                return Task.FromResult(receivers.Reply?.Invoke(unpacked));
            };
            o.MapMore = app =>
            {
                app.MapDidCommEndpoint(PathOf(RewrappedReceiveTests.PlainPath.HttpCallback), (unpacked, _) =>
                {
                    receivers._seen.Enqueue((unpacked, null));
                    return Task.CompletedTask;
                });
                app.MapDidCommWebSocket(PathOf(RewrappedReceiveTests.PlainPath.WebSocketCallback), (unpacked, _) =>
                {
                    receivers._seen.Enqueue((unpacked, null));
                    return Task.CompletedTask;
                });
            };
            more?.Invoke(o);
        });
        receivers.Host.Recorder.Behavior = (_, context) =>
        {
            receivers._seen.Enqueue((context.Received, context.Session));
            return (Array.Empty<Message>(), receivers.Reply?.Invoke(context.Received));
        };
        return receivers;
    }

    public static string PathOf(RewrappedReceiveTests.PlainPath path) => path switch
    {
        RewrappedReceiveTests.PlainPath.HttpCallback => "/plain",
        RewrappedReceiveTests.PlainPath.HttpRegistry => "/didcomm",
        RewrappedReceiveTests.PlainPath.HttpSessionCallback => "/didcomm-callback",
        RewrappedReceiveTests.PlainPath.WebSocketCallback => "/ws-plain",
        _ => "/ws",
    };

    /// <summary><paramref name="layers"/> fresh anoncrypt forward layers addressed to the server itself around <paramref name="inner"/>: what a re-wrapping mediator delivers (each layer is a new forward with its own id).</summary>
    public async Task<string> RewrapAsync(string inner, int layers)
    {
        var packed = inner;
        for (var i = 0; i < layers; i++)
            packed = await Host.AnoncryptAsync(ForwardMessage.Create(Host.ServerDid, Host.ServerDid, new[] { packed }));
        return packed;
    }

    /// <summary>
    /// Deliver <paramref name="packed"/> on <paramref name="path"/> and return the HTTP status (none
    /// on a WebSocket) and what the consumer got for it. A WebSocket delivery is followed by an
    /// authcrypted fence on the same socket and waits for the fence to be handled.
    /// </summary>
    public async Task<Delivery> DeliverAsync(RewrappedReceiveTests.PlainPath path, string packed)
    {
        var before = _seen.Count;
        HttpStatusCode? status = null;
        if (path is RewrappedReceiveTests.PlainPath.HttpCallback or RewrappedReceiveTests.PlainPath.HttpRegistry or RewrappedReceiveTests.PlainPath.HttpSessionCallback)
        {
            var response = await Host.PostAsync(packed, PathOf(path));
            status = response.Status;
            if (status == HttpStatusCode.BadRequest)
                response.Body.Should().BeEmpty("the uniform 400 has no body");
        }
        else
        {
            using var socket = await Host.ConnectAsync(PathOf(path));
            await ReturnRouteTestHost.SendAsync(socket, packed);
            await FenceAsync(socket);
            using var close = new CancellationTokenSource(Hang);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, close.Token);
        }
        var seen = _seen.Skip(before).Where(s => !s.Unpacked.Message.Id.StartsWith(FencePrefix, StringComparison.Ordinal)).ToList();
        return new Delivery(path, status, seen);
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}

/// <summary>What a consumer got for one message: its unpack result and its return-route session (or <c>null</c>).</summary>
internal sealed record RecordedSeen(UnpackResult Unpacked, IReturnRouteSession? Session);

/// <summary>What one delivery on one path produced.</summary>
internal sealed record Delivery(
    RewrappedReceiveTests.PlainPath Path,
    HttpStatusCode? Status,
    IReadOnlyList<(UnpackResult Unpacked, IReturnRouteSession? Session)> Seen)
{
    /// <summary>Accepted: HTTP 202 (a WebSocket writes nothing back either way).</summary>
    public void AssertAccepted(RewrappedReceiveTests.PlainPath path)
    {
        if (Status is { } status)
            status.Should().Be(HttpStatusCode.Accepted, path.ToString());
    }

    /// <summary>
    /// Refused like an envelope that does not unpack: the uniform empty 400 over HTTP, discarded on
    /// a WebSocket, nothing handed to the consumer, and the refusal logged by exception type only
    /// (<paramref name="exceptionType"/> when given).
    /// </summary>
    public void AssertRefused(RewrappedReceiveTests.PlainPath path, ReturnRouteTestHost host, string? exceptionType)
    {
        Seen.Should().BeEmpty("nothing of a refused delivery reaches the consumer ({0})", path);
        string prefix;
        if (Status is { } status)
        {
            status.Should().Be(HttpStatusCode.BadRequest, path.ToString());
            prefix = "DidComm receive: rejecting inbound envelope (";
        }
        else
        {
            prefix = "MapDidCommWebSocket: discarding ";
        }
        var lines = host.Logs.Entries.Where(e => e.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        lines.Should().ContainSingle("one refusal line ({0})", path);
        if (exceptionType is not null)
            lines[0].Should().Contain(exceptionType);
    }
}
