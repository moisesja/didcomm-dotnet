using System.Net;
using System.Text.Json.Nodes;
using DidComm.Messages;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

// L-014.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;
using TrustPingApi = DidComm.Protocols.TrustPing.TrustPing;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #138 over the return route: the built-in responders of the #91 TestServer agent answer a request
/// that declares a <c>thid</c> on that <c>thid</c>, whichever way the reply travels back — the
/// <c>200</c> body of the HTTP POST (read raw, and through the client's
/// <see cref="DidComm.Transports.SendResult.Returned"/>) and the WebSocket session socket. DIDComm
/// v2.1 threading.md L15: a request that declares no <c>thid</c> has its <c>id</c> as its
/// <c>thid</c> (the control); L17: every later message in the thread MUST carry that same
/// <c>thid</c>, so a reply on the request's <c>id</c> is not in the declared thread.
/// </summary>
public sealed class ReturnRouteReplyThreadTests
{
    /// <summary>Hang guard only: the reply is certain with a correct product.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    public enum Kind { TrustPing, DiscoverFeatures }

    public static TheoryData<Kind, bool> Matrix()
    {
        var data = new TheoryData<Kind, bool>();
        foreach (var kind in Enum.GetValues<Kind>())
            foreach (var declaresThid in new[] { true, false })
                data.Add(kind, declaresThid);
        return data;
    }

    private static string ReplyType(Kind kind) => kind == Kind.TrustPing ? TrustPingApi.ResponseType : DiscoverFeaturesApi.DiscloseType;

    /// <summary>The request as the protocol's own builder makes it (a fresh id, no thid).</summary>
    private static Message Request(Kind kind, string from, string to) => kind == Kind.TrustPing
        ? TrustPingApi.CreatePing(from, to)
        : DiscoverFeaturesApi.CreateQuery(from, to, new FeatureQuery { FeatureType = "protocol", Match = "*" });

    private static void AssertOnThread(Message reply, Message request, string? declared)
    {
        reply.Thid.Should().Be(declared ?? request.Id); // threading.md L15
        if (declared is not null)
            reply.Thid.Should().NotBe(request.Id, "a reply on the request's id is not in the thread the request declared (threading.md L17)");
        reply.Pthid.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Server_side_HTTP_the_200_body_unpacks_to_a_reply_on_the_thid_the_request_declares(Kind kind, bool declaresThid)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        var declared = declaresThid ? "thr-" + Guid.NewGuid().ToString("N") : null;
        var template = Request(kind, host.Alice.Did, host.ServerDid);
        var builder = host.Stamped(new MessageBuilder()
            .WithType(template.Type)
            .WithFrom(host.Alice.Did)
            .WithTo(host.ServerDid)
            .WithBody((JsonObject)template.Body!.DeepClone())
            .WithReturnRoute(ReturnRouteMode.All));
        if (declared is not null)
            builder.WithThid(declared);
        var request = builder.Build();

        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, request));

        response.Status.Should().Be(HttpStatusCode.OK, "an eligible request with return_route: all gets its reply on the response (FR-RR-04)");
        var reply = await host.Alice.Client.UnpackAsync(response.BodyText);
        reply.Authenticated.Should().BeTrue();
        reply.Message.Type.Should().Be(ReplyType(kind));
        reply.Message.From.Should().Be(host.ServerDid);
        reply.Message.To.Should().Equal(host.Alice.Did);
        AssertOnThread(reply.Message, request, declared);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Client_side_HTTP_SendResult_Returned_is_a_reply_on_the_thid_the_request_declares(Kind kind, bool declaresThid)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteClientHarness.Create(host);
        var declared = declaresThid ? "thr-" + Guid.NewGuid().ToString("N") : null;
        var request = Request(kind, host.Alice.Did, host.ServerDid);
        request.Thid = declared;

        var result = await client.Client.SendAsync(request, client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        result.Transport.HttpStatusCode.Should().Be(200);
        var returned = result.Returned;
        returned.Should().NotBeNull("the server answered on the return route (FR-RR-04/07)");
        returned!.Authenticated.Should().BeTrue();
        returned.Message.Type.Should().Be(ReplyType(kind));
        returned.Message.To.Should().Equal(host.Alice.Did);
        AssertOnThread(returned.Message, request, declared);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task WebSocket_the_reply_pushed_on_the_session_socket_is_on_the_thid_the_request_declares(Kind kind, bool declaresThid)
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        // The harness records ping-responses on the client; a disclose needs its own recorder (last registration wins).
        await using var client = ReturnRouteWebSocketClientHarness.Create(
            host, configureDidComm: b => b.AddProtocol<DiscoverFeaturesRecordingHandler>());
        var declared = declaresThid ? "thr-" + Guid.NewGuid().ToString("N") : null;
        var request = Request(kind, host.Alice.Did, host.ServerDid);
        request.Thid = declared;

        await client.Client.SendAsync(request, client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Count == 1, HangGuard)).Should().BeTrue("the server answered on the session socket");
        var seen = client.Recorder.Seen.Single();
        seen.Message.Type.Should().Be(ReplyType(kind));
        seen.Message.From.Should().Be(host.ServerDid);
        seen.Message.To.Should().Equal(host.Alice.Did);
        AssertOnThread(seen.Message, request, declared);
        host.LeakingLogLines().Should().BeEmpty();
    }
}

/// <summary>A recording handler for Discover Features 2.0 on the client (it records disclosures; it never replies).</summary>
internal sealed class DiscoverFeaturesRecordingHandler : RecordingHandlerBase
{
    public DiscoverFeaturesRecordingHandler(RecordingHandlerState state) : base(state) { }

    protected override string Uri => DiscoverFeaturesApi.ProtocolUri;
}
