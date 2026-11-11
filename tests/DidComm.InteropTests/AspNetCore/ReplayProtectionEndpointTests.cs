using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Protocols.TrustPing;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #91 F10 — FR-RR-08 replay admission at the registry-aware endpoints, end to end against a
/// TestServer: the scope (an authcrypted message carrying <c>return_route</c> other than
/// <c>none</c>, or a mediator-service type; <c>forward</c> exempt; ordinary traffic untouched),
/// the freshness and timestamp rules, and atomic admission under concurrent duplicates on HTTP,
/// across HTTP and WebSocket, and on one socket. A drop answers 202 / writes nothing, dispatches
/// nothing and opens no session; its log line carries the id and the outcome name and nothing else
/// (F14: no DID, key id or thread value in any captured line).
/// </summary>
public sealed class ReplayProtectionEndpointTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const string PickupStatusRequest = PickupRecordingHandler.ProtocolUri + "/status-request";

    [Fact]
    public async Task An_immediate_duplicate_is_answered_202_empty_and_dispatched_once()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));

        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var packed = await host.AuthcryptAsync(host.Alice, inbound);

        var first = await host.PostAsync(packed);
        var second = await host.PostAsync(packed);

        first.Status.Should().Be(HttpStatusCode.OK);
        second.Status.Should().Be(HttpStatusCode.Accepted, "a replay is answered exactly like a dropped message (FR-RR-08)");
        second.Body.Should().BeEmpty();
        host.Recorder.Invocations.Should().Be(1, "the replay re-executes nothing");
        host.Recorder.Seen.Should().ContainSingle().Which.Session.Should().NotBeNull("only the first request got a session");
        host.DropLines(inbound.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.Duplicate));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_timestamp_free_ping_with_return_route_all_is_dropped_and_logged_by_id_and_outcome()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        var ping = new MessageBuilder()
            .WithType(TrustPing.PingType)
            .WithFrom(host.Alice.Did)
            .WithTo(host.ServerDid)
            .WithReturnRoute(ReturnRouteMode.All)
            .Build();

        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, ping));

        response.Status.Should().Be(HttpStatusCode.Accepted);
        response.Body.Should().BeEmpty("no session, no ping-response");
        host.Logs.Entries.Should().NotContain(e => e.Contains("Protocol dispatch"), "the trust-ping handler was never invoked");
        host.DropLines(ping.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.MissingCreatedTime));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_timestamp_free_ping_without_the_header_is_ordinary_traffic_and_is_dispatched()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        var ping = TrustPing.CreatePing(host.Alice.Did, host.ServerDid);

        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, ping));

        response.Status.Should().Be(HttpStatusCode.Accepted);
        host.Logs.Entries.Should().Contain(e => e.Contains("Protocol dispatch produced a reply") && e.Contains("returned-on-session=False"),
            "FR-RR-08 exempts a message without return_route outside the mediator PIURIs");
        host.DropLines(ping.Id).Should().BeEmpty();
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_ten_minute_old_application_message_without_the_header_is_dispatched()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        var old = host.RecordingMessage(host.Alice)
            .WithCreatedTime(host.Clock.Now.AddMinutes(-10).ToUnixTimeSeconds())
            .Build();

        (await host.PostAsync(await host.AuthcryptAsync(host.Alice, old))).Status.Should().Be(HttpStatusCode.Accepted);

        host.Recorder.Invocations.Should().Be(1, "only FR-API-05 expiry applies to ordinary traffic");
        host.DropLines(old.Id).Should().BeEmpty();
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData(-10 * 60, nameof(ReplayAdmission.TooOld))]
    [InlineData(-5 * 60 - 1, nameof(ReplayAdmission.TooOld))]
    [InlineData(10 * 60, nameof(ReplayAdmission.FutureDated))]
    public async Task An_out_of_window_message_with_return_route_all_is_dropped(int createdOffsetSeconds, string outcome)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        var inbound = host.RecordingMessage(host.Alice)
            .WithReturnRoute(ReturnRouteMode.All)
            .WithCreatedTime(host.Clock.Now.ToUnixTimeSeconds() + createdOffsetSeconds)
            .Build();

        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, inbound));

        response.Status.Should().Be(HttpStatusCode.Accepted);
        response.Body.Should().BeEmpty();
        host.Recorder.Invocations.Should().Be(0);
        host.DropLines(inbound.Id).Should().ContainSingle().Which.Should().Contain(outcome);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task The_window_edge_is_admitted()
    {
        // created == now - Window is still fresh (age rejection is created < now - Window).
        await using var host = await ReturnRouteTestHost.StartAsync();
        var inbound = host.RecordingMessage(host.Alice)
            .WithReturnRoute(ReturnRouteMode.All)
            .WithCreatedTime(host.Clock.Now.AddMinutes(-5).ToUnixTimeSeconds())
            .Build();

        (await host.PostAsync(await host.AuthcryptAsync(host.Alice, inbound))).Status.Should().Be(HttpStatusCode.Accepted);

        host.Recorder.Invocations.Should().Be(1);
        host.Recorder.Seen.Single().Session.Should().NotBeNull();
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_timestamp_free_authcrypt_pickup_request_without_the_header_is_dropped()
    {
        // Scope (b): a mediator-service type is in scope whatever its header.
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.ConfigureDidComm = b => b.AddProtocol<PickupRecordingHandler>());
        var request = PickupRequest(host, host.Alice);

        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, request));

        response.Status.Should().Be(HttpStatusCode.Accepted);
        host.Recorder.Invocations.Should().Be(0, "the pickup handler is never reached");
        host.DropLines(request.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.MissingCreatedTime));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData(EnvelopeShape.Anoncrypt)]
    [InlineData(EnvelopeShape.SignedOnly)]
    [InlineData(EnvelopeShape.AnoncryptOfSigned)]
    [InlineData(EnvelopeShape.Plaintext)]
    public async Task An_unauthcrypted_pickup_request_passes_admission_to_its_handler(EnvelopeShape shape)
    {
        // Plaintext, anoncrypt and signed-only requests can neither open a session nor mutate
        // mediator state: they pass through and FR-MED-05 / FR-PICK-01 drop them in the handler.
        // Regression (QA #91): signed-only and anoncrypt(signed) report Authenticated (the JWS sets
        // it) and used to be dropped by admission as NoSenderSubject.
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.ConfigureDidComm = b => b.AddProtocol<PickupRecordingHandler>());
        var request = PickupRequest(host, host.Alice);
        var (packed, contentType) = await host.PackAsync(host.Alice, request, shape);

        (await host.PostAsync(packed, contentType: contentType)).Status.Should().Be(HttpStatusCode.Accepted);

        host.Recorder.Invocations.Should().Be(1);
        host.Recorder.Seen.Single().Session.Should().BeNull();
        host.DropLines(request.Id).Should().BeEmpty();
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task An_authcrypt_forward_carrying_return_route_all_is_exempt_and_never_opens_a_session()
    {
        // FR-RR-02 / JC-14: forward is replay-exempt (it carries no created_time) and the header on
        // it is never honoured; posting the same forward twice reaches the routing handler twice.
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.ConfigureDidComm = b => b.AddProtocol<RoutingRecordingHandler>();
        });
        var payload = await host.Carol.Client.PackEncryptedAsync(
            new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/inner").WithTo(host.Carol.Did).Build(),
            new PackEncryptedOptions(Recipients: new[] { host.Carol.Did }, Forward: false));
        var forward = ForwardMessage.Create(host.ServerDid, host.Carol.Did, new[] { payload.Message });
        forward.From = host.Alice.Did;
        forward.AdditionalHeaders ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        forward.AdditionalHeaders[ReturnRouteHeader.HeaderName] = JsonSerializer.SerializeToElement(ReturnRouteHeader.AllValue);
        forward.CreatedTime.Should().BeNull("the exemption is exercised with a timestamp-free forward");
        var packed = await host.AuthcryptAsync(host.Alice, forward);

        var first = await host.PostAsync(packed);
        var second = await host.PostAsync(packed);

        first.Status.Should().Be(HttpStatusCode.Accepted);
        second.Status.Should().Be(HttpStatusCode.Accepted);
        first.Body.Should().BeEmpty();
        host.Recorder.Invocations.Should().Be(2, "forward is exempt from FR-RR-08 (its handler dedupes on its own terms)");
        host.Recorder.Seen.Should().AllSatisfy(r => r.Session.Should().BeNull("a forward never opens a session (FR-RR-02)"));
        host.Recorder.Seen.Should().AllSatisfy(r => r.ReturnRoute.Mode.Should().Be(ReturnRouteMode.All, "the header is parsed, just not honoured"));
        host.DropLines(forward.Id).Should().BeEmpty();

        // The same on a WebSocket with the switch on: no session, nothing registered.
        using var socket = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, packed);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Invocations == 3, Timeout)).Should().BeTrue();
        host.Registry.Count.Should().Be(0);
        await AssertNothingWrittenThenCloseAsync(socket);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Sixteen_concurrent_posts_of_one_message_dispatch_it_exactly_once_and_all_answer_2xx()
    {
        // FR-RR-08 (3): atomic check-and-insert. All 16 admissions are parked in the guard until they
        // have all arrived (bounded), then race into the real guard. Invariant for every interleaving:
        // exactly one dispatch, exactly one 200 (the admitted request's reply), fifteen 202.
        const int N = 16;
        RendezvousReplayGuard? guard = null;
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.ConfigureServices = services =>
            services.AddSingleton<IInboundReplayGuard>(sp => guard = new RendezvousReplayGuard(
                new InMemoryReplayGuard(new ReplayProtectionOptions(), sp.GetRequiredService<IOptions<DidCommOptions>>().Value),
                new Rendezvous(N, Timeout))));
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));

        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var packed = await host.AuthcryptAsync(host.Alice, inbound);

        var responses = await Task.WhenAll(Enumerable.Range(0, N).Select(_ => Task.Run(() => host.PostAsync(packed))));

        guard!.Rendezvous.MaxConcurrent.Should().BeGreaterThan(1, "the admissions overlapped (L-044)");
        host.Recorder.Invocations.Should().Be(1, "exactly one admission wins");
        responses.Should().AllSatisfy(r => ((int)r.Status).Should().BeInRange(200, 299));
        responses.Count(r => r.Status == HttpStatusCode.OK).Should().Be(1);
        responses.Count(r => r.Status == HttpStatusCode.Accepted && r.Body.Length == 0).Should().Be(N - 1);
        host.DropLines(inbound.Id).Should().HaveCount(N - 1).And.AllSatisfy(l => l.Should().Contain(nameof(ReplayAdmission.Duplicate)));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task One_message_racing_over_HTTP_and_a_WebSocket_is_dispatched_once()
    {
        // The HTTP and WebSocket endpoints share the registered guard (AddDidComm), so the same
        // envelope arriving on both concurrently is admitted on exactly one of them.
        RendezvousReplayGuard? guard = null;
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.ConfigureServices = services => services.AddSingleton<IInboundReplayGuard>(sp => guard = new RendezvousReplayGuard(
                new InMemoryReplayGuard(new ReplayProtectionOptions(), sp.GetRequiredService<IOptions<DidCommOptions>>().Value),
                new Rendezvous(2, Timeout)));
        });
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));

        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var packed = await host.AuthcryptAsync(host.Alice, inbound);
        using var socket = await host.ConnectAsync();

        var http = Task.Run(() => host.PostAsync(packed));
        await ReturnRouteTestHost.SendAsync(socket, packed);
        var response = await http.WaitAsync(Timeout);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.DropLines(inbound.Id).Count == 1, Timeout)).Should().BeTrue("the loser is dropped");
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 1, Timeout)).Should().BeTrue("the winner is dispatched");

        guard!.Rendezvous.MaxConcurrent.Should().Be(2, "both admissions were inside the guard together (L-044)");
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        var frame = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
        host.Recorder.Invocations.Should().Be(1, "exactly one admission wins");
        var httpWon = response.Status == HttpStatusCode.OK;
        var wsWon = frame.Type == WebSocketMessageType.Binary;
        (httpWon ^ wsWon).Should().BeTrue("exactly one transport returned the reply (HTTP {0}, first WS frame {1})", response.Status, frame.Type);
        if (!httpWon)
            response.Status.Should().Be(HttpStatusCode.Accepted);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_message_twice_on_one_socket_is_dispatched_and_pushed_once()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        host.Recorder.Behavior = (message, _) => (new[] { ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did, "push") }, null);

        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var packed = await host.AuthcryptAsync(host.Alice, inbound);
        using var socket = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, packed);
        await ReturnRouteTestHost.SendAsync(socket, packed);

        var push = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
        push.Type.Should().Be(WebSocketMessageType.Binary);
        (await host.Alice.Client.UnpackAsync(push.Text)).Message.Thid.Should().Be(inbound.Id);
        await AssertNothingWrittenThenCloseAsync(socket);

        host.Recorder.Invocations.Should().Be(1);
        host.DropLines(inbound.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.Duplicate));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Drop_lines_carry_the_id_and_are_rate_limited_from_Warning_to_Debug()
    {
        // JC-22: the first drop per outcome is a Warning with the running count; later ones are Debug.
        await using var host = await ReturnRouteTestHost.StartAsync();
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var inbound = host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All).Build(); // no created_time
            ids.Add(inbound.Id);
            (await host.PostAsync(await host.AuthcryptAsync(host.Alice, inbound))).Status.Should().Be(HttpStatusCode.Accepted);
        }

        host.Recorder.Invocations.Should().Be(0);
        foreach (var id in ids)
            host.DropLines(id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.MissingCreatedTime));
        host.DropLines(ids[0]).Single().Should().Contain("1 such drops", "the first drop is the Warning with the running count");
        host.DropLines(ids[1]).Single().Should().NotContain("such drops", "later drops are Debug lines");
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static Message PickupRequest(ReturnRouteTestHost host, TestPeer from)
        => new MessageBuilder()
            .WithType(PickupStatusRequest)
            .WithFrom(from.Did)
            .WithTo(host.ServerDid)
            .Build();

    /// <summary>Close from the client side; the first frame the server sends back must be its close (nothing else was written).</summary>
    internal static async Task AssertNothingWrittenThenCloseAsync(WebSocket socket)
    {
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        var first = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
        first.Type.Should().Be(WebSocketMessageType.Close, "nothing was written on the socket before the server's close");
    }
}
