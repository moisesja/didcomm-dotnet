using System.Net;
using AwesomeAssertions;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Transports.ReturnRoute;
using Xunit;
using PlainPath = DidComm.InteropTests.AspNetCore.RewrappedReceiveTests.PlainPath;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// QA round 3 (#110, owner decision OD-3): the plain receive paths that open a forward re-wrapped
/// for this agent, past the rows the engineers pinned — a broken layer deep inside the chain rather
/// than right under the outermost one, no WebSocket session opened by the message inside, an
/// anonymous message inside asking for a return route, and one message replayed across two paths
/// in two different wrappers. Real envelopes on the <see cref="PlainReceivers"/> host; a WebSocket
/// delivery is fenced (L-065).
/// </summary>
public sealed class RewrappedReceiveQaTests
{
    public enum BadLayer
    {
        NamingAnotherDid,
        Authcrypted,
        TwoAttachments,
        LinkedAttachment,
        EmptyAttachment,
        NotUtf8Attachment,
    }

    public static TheoryData<PlainPath, int, BadLayer> PathsDepthsAndBadLayers()
    {
        var data = new TheoryData<PlainPath, int, BadLayer>();
        foreach (var path in Enum.GetValues<PlainPath>())
        {
            foreach (var depth in new[] { 2, ForwardHandler.MaxSelfAddressedLayers })
            {
                foreach (var bad in Enum.GetValues<BadLayer>())
                    data.Add(path, depth, bad);
            }
        }
        return data;
    }

    // Whatever is not the message nor another layer re-wrapped for this agent fails the whole
    // delivery wherever it sits in the chain: depth 2 (right under the received layer) and depth 8
    // (the deepest a layer may be). Nothing of it reaches a consumer, and the refusal is the one an
    // envelope that does not unpack gets (the uniform empty 400, or a discard on a WebSocket).
    [Theory]
    [MemberData(nameof(PathsDepthsAndBadLayers))]
    public async Task A_bad_layer_anywhere_in_the_chain_is_refused_like_an_envelope_that_does_not_unpack(PlainPath path, int depth, BadLayer bad)
    {
        await using var r = await PlainReceivers.StartAsync();
        var host = r.Host;
        var message = host.RecordingMessage(host.Alice).Build();
        var inner = await host.AuthcryptAsync(host.Alice, message);
        var broken = await BadLayerAsync(host, bad, inner);

        var delivery = await r.DeliverAsync(path, await r.RewrapAsync(broken, depth - 1));

        delivery.AssertRefused(path, host, exceptionType: null);
        host.LeakingLogLines().Should().BeEmpty();
    }

    // A WebSocket connection (same-socket replies on) gets no session from a message opened out of
    // layers, authcrypted and asking for return_route all as it is: return route is only between the
    // immediate sender and the agent it connects to (return_route.md L9; #110 G-1, which changed this
    // row from "opens a session bound to its own sender").
    [Fact]
    public async Task On_a_WebSocket_the_message_inside_opens_no_session()
    {
        await using var r = await PlainReceivers.StartAsync(o => o.AllowSameSocketReplies = true);
        var host = r.Host;
        var message = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();

        var delivery = await r.DeliverAsync(PlainPath.WebSocketRegistry, await r.RewrapAsync(await host.AuthcryptAsync(host.Alice, message), layers: 3));

        var seen = delivery.Seen.Should().ContainSingle().Subject;
        seen.Unpacked.Message.Id.Should().Be(message.Id);
        host.Alice.Kids.Should().Contain(seen.Unpacked.SenderKid, "the message inside keeps its own sender");
        seen.Session.Should().BeNull("a message opened out of re-wrapped layers never opens a session");
        host.Registry.Count.Should().Be(0);
        host.LeakingLogLines().Should().BeEmpty();
    }

    public static TheoryData<PlainPath> SessionPaths => new() { PlainPath.HttpRegistry, PlainPath.HttpSessionCallback, PlainPath.WebSocketRegistry };

    // An anoncrypted message inside asking for return_route all opens no session (FR-RR-03: a
    // session needs an authenticated sender), and the layer around it lends it none.
    [Theory]
    [MemberData(nameof(SessionPaths))]
    public async Task An_anoncrypted_message_inside_asking_for_a_return_route_opens_no_session(PlainPath path)
    {
        await using var r = await PlainReceivers.StartAsync(o => o.AllowSameSocketReplies = true);
        var host = r.Host;
        var message = host.Stamped(new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/anonymous").WithTo(host.ServerDid).WithReturnRoute(ReturnRouteMode.All)).Build();

        var delivery = await r.DeliverAsync(path, await r.RewrapAsync(await host.AnoncryptAsync(message), layers: 1));

        delivery.AssertAccepted(path);
        var seen = delivery.Seen.Should().ContainSingle().Subject;
        seen.Unpacked.Message.Id.Should().Be(message.Id);
        seen.Unpacked.AnonymousSender.Should().BeTrue();
        seen.Session.Should().BeNull("an anonymous message never gets a session, wrapped or not");
        host.LeakingLogLines().Should().BeEmpty();
    }

    public enum FirstArrival
    {
        Unwrapped,
        Rewrapped,
    }

    // One authcrypted message asking for a return route, first on the registry-aware HTTP endpoint
    // (as it is, or in a wrapper), then on the registry-aware WebSocket endpoint in a different
    // wrapper: replay admission is the application's (one guard for every endpoint, JC-17) and keys
    // on the message inside, so the second arrival is refused as a duplicate.
    [Theory]
    [InlineData(FirstArrival.Unwrapped)]
    [InlineData(FirstArrival.Rewrapped)]
    public async Task A_message_admitted_on_one_path_is_refused_on_another_in_a_different_wrapper(FirstArrival first)
    {
        await using var r = await PlainReceivers.StartAsync(o => o.AllowSameSocketReplies = true);
        var host = r.Host;
        var message = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var inner = await host.AuthcryptAsync(host.Alice, message);

        var a = await r.DeliverAsync(PlainPath.HttpRegistry, first == FirstArrival.Unwrapped ? inner : await r.RewrapAsync(inner, layers: 1));
        var b = await r.DeliverAsync(PlainPath.WebSocketRegistry, await r.RewrapAsync(inner, layers: 2));

        a.Status.Should().Be(HttpStatusCode.Accepted);
        a.Seen.Should().ContainSingle().Which.Unpacked.Message.Id.Should().Be(message.Id);
        b.Seen.Should().BeEmpty("replay admission keys on the sender and id of the message inside, whatever wraps it and whichever endpoint it reaches");
        host.DropLines(message.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.Duplicate));
        host.LeakingLogLines().Should().BeEmpty();
    }

    /// <summary>A layer around <paramref name="inner"/> the receiver can decrypt that is not a layer re-wrapped for it (or one whose attachment does not hold a message).</summary>
    private static async Task<string> BadLayerAsync(ReturnRouteTestHost host, BadLayer bad, string inner)
    {
        var good = ForwardMessage.Create(host.ServerDid, host.ServerDid, new[] { inner });
        switch (bad)
        {
            case BadLayer.NamingAnotherDid:
                return await host.AnoncryptAsync(ForwardMessage.Create(host.ServerDid, host.Carol.Did, new[] { inner }));
            case BadLayer.Authcrypted:
                return await host.AuthcryptAsync(host.Alice, good);
            case BadLayer.TwoAttachments:
                return await host.AnoncryptAsync(ForwardMessage.Create(host.ServerDid, host.ServerDid, new[] { inner, inner }));
            default:
                var attachment = good.Attachments![0];
                attachment.Data = bad switch
                {
                    BadLayer.LinkedAttachment => new AttachmentData { Links = new[] { "https://qa3.example/payload" }, Hash = "zQmQa3" },
                    BadLayer.EmptyAttachment => new AttachmentData { Base64 = "    " },
                    _ => new AttachmentData { Base64 = Convert.ToBase64String(new byte[] { 0x7B, 0x22, 0x61, 0x22, 0x3A, 0x22, 0xFF, 0x22, 0x7D }).TrimEnd('=').Replace('+', '-').Replace('/', '_') },
                };
                return await host.AnoncryptAsync(good);
        }
    }
}
