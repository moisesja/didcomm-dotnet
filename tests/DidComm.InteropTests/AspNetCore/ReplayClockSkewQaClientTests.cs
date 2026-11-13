using System.Net;
using System.Text;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #105 QA pass (client side), kept as regressions: the replay rule's own skew on the recipient side — real session-socket
/// pushes judged by the client's inbound consumer, <c>SendResult.Returned</c> over HTTP, FR-API-05
/// expiry on both sides, and one container that is both a client and a server. Clocks are injected
/// (<see cref="TestClock"/> plus the harness's <c>clockOffset</c>); waits are polls, never sleeps.
/// </summary>
public sealed class ReplayClockSkewQaClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // ---- 1. pushes judged by the client's inbound consumer ------------------------------------

    public static TheoryData<int, int?> PushMatrix()
    {
        var data = new TheoryData<int, int?>();
        foreach (var ahead in new[] { 0, 1, 59, 60, 61, 300 })
        {
            foreach (var skew in new int?[] { null, 0, 3600 })
                data.Add(ahead, skew);
        }
        return data;
    }

    /// <summary>
    /// The server's clock is <paramref name="serverAheadSeconds"/> ahead of the client's. A real
    /// request over the client's session socket opens the session; the server's handler pushes an
    /// in-scope message by type (Pickup 4.0), one in scope by header (<c>return_route: all</c>), an
    /// out-of-scope one stamped an hour ahead, and an unstamped out-of-scope sentinel (frames are
    /// consumed in order, so once it is seen everything before it was judged).
    /// </summary>
    [Theory]
    [MemberData(nameof(PushMatrix))]
    public async Task Session_pushes_from_a_server_ahead_are_admitted_iff_the_lead_is_within_the_clients_ClockSkew(int serverAheadSeconds, int? clientSkewSeconds)
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        await using var client = ReturnRouteWebSocketClientHarness.Create(
            host,
            configureDidComm: ClientReplay(clientSkewSeconds),
            clockOffset: TimeSpan.FromSeconds(-serverAheadSeconds));
        var serverNow = host.Clock.Now.ToUnixTimeSeconds();
        var byType = ToAlice(host, Pickup40RecordingHandler.ProtocolUri + "/delivery").Build();
        var byHeader = ToAlice(host, RecordingHandler.ProtocolUri + "/push-rr").WithReturnRoute(ReturnRouteMode.All).Build();
        var outOfScopeAhead = ToAlice(host, RecordingHandler.ProtocolUri + "/push-ahead").WithCreatedTime(serverNow + 3600).Build();
        var sentinel = ToAlice(host, RecordingHandler.ProtocolUri + "/push-plain").Build();
        host.Recorder.Behavior = (_, _) => (new[] { byType, byHeader, outOfScopeAhead, sentinel }, null);
        var request = host.RecordingMessage(host.Alice, "open").Build();

        await client.Client.SendAsync(request, client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Seen.Any(r => r.Message.Id == sentinel.Id), Timeout))
            .Should().BeTrue("the out-of-scope sentinel always arrives");
        host.Recorder.Seen.Should().ContainSingle("the server admitted the client's request (stamped {0} s behind its clock)", serverAheadSeconds)
            .Which.PushResults.Should().Equal(true, true, true, true);
        host.DropLines(request.Id).Should().BeEmpty();

        var admitted = serverAheadSeconds <= (clientSkewSeconds ?? 60);
        foreach (var inScope in new[] { byType, byHeader })
        {
            var seen = client.Recorder.Seen.SingleOrDefault(r => r.Message.Id == inScope.Id);
            var drops = ClientDrops(host, inScope.Id);
            if (admitted)
            {
                seen.Should().NotBeNull("a push {0} s ahead is within the client's skew", serverAheadSeconds);
                seen!.Message.CreatedTime.Should().Be(serverNow, "the server's session stamps with the server's clock (FR-RR-09)");
                drops.Should().BeEmpty();
            }
            else
            {
                seen.Should().BeNull("a push {0} s ahead is beyond the client's skew", serverAheadSeconds);
                drops.Should().ContainSingle().Which.Should().Contain("replay admission " + nameof(ReplayAdmission.FutureDated));
            }
        }

        client.Recorder.Seen.Single(r => r.Message.Id == outOfScopeAhead.Id).Message.CreatedTime.Should().Be(serverNow + 3600, "out of scope: not judged, whatever its stamp");
        client.Recorder.Seen.Single(r => r.Message.Id == sentinel.Id).Message.CreatedTime.Should().BeNull("out of scope: not stamped");
        ClientDrops(host, outOfScopeAhead.Id).Should().BeEmpty();
        ClientDrops(host, sentinel.Id).Should().BeEmpty();
        host.LeakingLogLines().Should().BeEmpty();
    }

    /// <summary>The other side of the client's window: a server 301 s behind (the server's skew widened so it admits the request) is TooOld on the client.</summary>
    [Theory]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public async Task Session_pushes_from_a_server_behind_the_client_are_refused_on_age_after_Window(int clientAheadSeconds, bool admitted)
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.ConfigureReplay = r => r.ClockSkew = TimeSpan.FromHours(1);
        });
        await using var client = ReturnRouteWebSocketClientHarness.Create(host, clockOffset: TimeSpan.FromSeconds(clientAheadSeconds));
        var byType = ToAlice(host, Pickup40RecordingHandler.ProtocolUri + "/delivery").Build();
        var sentinel = ToAlice(host, RecordingHandler.ProtocolUri + "/push-plain").Build();
        host.Recorder.Behavior = (_, _) => (new[] { byType, sentinel }, null);

        await client.Client.SendAsync(host.RecordingMessage(host.Alice, "open").Build(), client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Seen.Any(r => r.Message.Id == sentinel.Id), Timeout)).Should().BeTrue();
        client.Recorder.Seen.Any(r => r.Message.Id == byType.Id).Should().Be(admitted);
        if (!admitted)
            ClientDrops(host, byType.Id).Should().ContainSingle().Which.Should().Contain("replay admission " + nameof(ReplayAdmission.TooOld));
    }

    /// <summary>
    /// The retention / age seam on the client for a push stamped by a server <paramref name="serverAheadSeconds"/>
    /// ahead: the same frame replayed every second for Window + 2 × ClockSkew after admission is
    /// never dispatched again — Duplicate until created + Window, TooOld after.
    /// </summary>
    [Theory]
    [InlineData(0, null)]
    [InlineData(60, null)]
    [InlineData(30, 30)]
    [InlineData(0, 0)]
    public async Task A_replayed_push_is_never_admitted_again_on_the_client_whatever_the_servers_lead(int serverAheadSeconds, int? clientSkewSeconds)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteWebSocketClientHarness.Create(host, configureDidComm: ClientReplay(clientSkewSeconds), clockOffset: TimeSpan.FromSeconds(-serverAheadSeconds));
        var consumer = client.Services.GetRequiredService<IInboundFrameConsumer>();
        var skew = clientSkewSeconds ?? 60;
        var created = host.Clock.Now.ToUnixTimeSeconds();
        var push = ToAlice(host, Pickup40RecordingHandler.ProtocolUri + "/delivery").WithCreatedTime(created).Build();
        var server = host.App.Services.GetRequiredService<DidCommClient>();
        var packed = await server.PackEncryptedAsync(push, new PackEncryptedOptions(Recipients: new[] { host.Alice.Did }, From: host.ServerDid, Forward: false));
        var frame = new InboundFrame(client.Endpoint, host.Alice.Did, host.ServerDid, Encoding.UTF8.GetBytes(packed.Message));

        await consumer.ConsumeAsync(frame, default);
        client.Recorder.Invocations.Should().Be(1);

        var outcomes = new List<string>();
        for (var second = 0; second <= 300 + 2 * skew + 2; second++)
        {
            host.Clock.Advance(TimeSpan.FromSeconds(1));
            await consumer.ConsumeAsync(frame, default);
            var clientNow = (host.Clock.Now - TimeSpan.FromSeconds(serverAheadSeconds)).ToUnixTimeSeconds();
            outcomes.Add(clientNow > created + 300 ? nameof(ReplayAdmission.TooOld) : nameof(ReplayAdmission.Duplicate));
        }

        client.Recorder.Invocations.Should().Be(1, "no instant re-admits the replay");
        var reasons = host.Logs.Entries
            .Where(l => l.Contains("Pushed message " + push.Id + " dropped: replay admission ", StringComparison.Ordinal))
            .Select(l => l.Split("replay admission ")[1].Split(' ')[0])
            .ToList();
        reasons.Should().Equal(outcomes);
    }

    // ---- 2. SendResult.Returned over HTTP -------------------------------------------------------

    public static TheoryData<int, int?> ReturnedMatrix()
    {
        var data = new TheoryData<int, int?>();
        foreach (var ahead in new[] { 0, 61, 300, 3600 })
        {
            foreach (var skew in new int?[] { null, 0, 3600 })
                data.Add(ahead, skew);
        }
        return data;
    }

    /// <summary>
    /// The server is <paramref name="serverAheadSeconds"/> ahead and stamps its in-scope reply (a
    /// Pickup 4.0 type) with its clock. The client's ClockSkew never decides <c>Returned</c>: it is
    /// not replay-admitted (FR-RR-07). The server's Window is widened so its own age check admits a
    /// request stamped up to an hour behind.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReturnedMatrix))]
    public async Task HTTP_Returned_is_not_judged_by_any_ClockSkew_whatever_the_servers_lead(int serverAheadSeconds, int? clientSkewSeconds)
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.ConfigureReplay = r => r.Window = TimeSpan.FromHours(2));
        host.Recorder.Behavior = (inbound, _) => (Array.Empty<Message>(), ToAlice(host, Pickup40RecordingHandler.ProtocolUri + "/status").WithThid(inbound.Id).Build());
        await using var client = ReturnRouteClientHarness.Create(host, configureDidComm: ClientReplay(clientSkewSeconds), clockOffset: TimeSpan.FromSeconds(-serverAheadSeconds));
        var request = host.RecordingMessage(host.Alice).Build();

        var result = await client.Client.SendAsync(request, client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        result.Transport.HttpStatusCode.Should().Be(200);
        result.Returned.Should().NotBeNull("Returned proves identity, not freshness");
        result.Returned!.Message.Thid.Should().Be(request.Id);
        result.Returned.Message.CreatedTime.Should().Be(host.Clock.Now.ToUnixTimeSeconds(), "stamped by the server, {0} s ahead of the client", serverAheadSeconds);
        host.Logs.Entries.Should().NotContain(l => l.StartsWith("Pushed message ", StringComparison.Ordinal), "nothing on the client went through the inbound consumer");
        host.LeakingLogLines().Should().BeEmpty();
    }

    /// <summary>A client ahead of the server: the server's skew decides (202, <c>Returned</c> null); the client's own ClockSkew is irrelevant.</summary>
    [Theory]
    [InlineData(59, 0, true)]
    [InlineData(59, 3600, true)]
    [InlineData(61, 0, false)]
    [InlineData(61, 3600, false)]
    public async Task HTTP_a_client_ahead_is_judged_by_the_servers_ClockSkew_only(int clientAheadSeconds, int clientSkewSeconds, bool admitted)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        host.Recorder.Behavior = (inbound, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, inbound, host.Alice.Did));
        await using var client = ReturnRouteClientHarness.Create(host, configureDidComm: ClientReplay(clientSkewSeconds), clockOffset: TimeSpan.FromSeconds(clientAheadSeconds));
        var request = host.RecordingMessage(host.Alice).Build();

        var result = await client.Client.SendAsync(request, client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        result.Transport.HttpStatusCode.Should().Be(admitted ? 200 : 202);
        (result.Returned is not null).Should().Be(admitted);
        host.DropLines(request.Id).Should().HaveCount(admitted ? 0 : 1);
    }

    /// <summary>
    /// What a clock offset does to <c>Returned</c>: only FR-API-05 expiry of the reply, by the
    /// client's clock and its <c>ExpiresClockSkew</c>. A reply expiring 10 s after the server's now,
    /// read by a client 30 s ahead, is expired unless ExpiresClockSkew ≥ 20 s; ClockSkew changes nothing.
    /// </summary>
    [Theory]
    [InlineData(0, null, false)]
    [InlineData(0, 3600, false)]
    [InlineData(19, 3600, false)]
    [InlineData(20, null, true)]
    [InlineData(20, 0, true)]
    public async Task HTTP_Returned_with_expires_time_follows_ExpiresClockSkew_by_the_clients_clock(int expiresSkewSeconds, int? replaySkewSeconds, bool accepted)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        host.Recorder.Behavior = (inbound, _) => (Array.Empty<Message>(),
            ToAlice(host, RecordingHandler.ProtocolUri + "/reply").WithThid(inbound.Id).WithExpiresTime(host.Clock.Now.ToUnixTimeSeconds() + 10).Build());
        await using var client = ReturnRouteClientHarness.Create(
            host,
            configureDidComm: b =>
            {
                ClientReplay(replaySkewSeconds)?.Invoke(b);
                b.Configure(o => o.ExpiresClockSkew = TimeSpan.FromSeconds(expiresSkewSeconds));
            },
            clockOffset: TimeSpan.FromSeconds(30));
        var request = host.RecordingMessage(host.Alice).Build();

        var send = async () => await client.Client.SendAsync(request, client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        if (accepted)
            (await send()).Returned.Should().NotBeNull();
        else
            (await send.Should().ThrowAsync<TransportException>()).Which.InnerException.Should().BeOfType<MalformedMessageException>()
                .Which.Message.Should().Contain("FR-API-05");
        host.Recorder.Invocations.Should().Be(1, "the server admitted the request (30 s ahead, within its 60 s skew)");
    }

    // ---- 4. FR-API-05 is unaffected: endpoints and the client's consumer ------------------------

    public static TheoryData<bool, int, int, int> EndpointExpiryMatrix()
    {
        var data = new TheoryData<bool, int, int, int>();
        foreach (var ws in new[] { false, true })
        {
            foreach (var expiresSkew in new[] { 0, 5 })
            {
                foreach (var replaySkew in new[] { 0, 3600 })
                {
                    foreach (var expiresIn in new[] { -6, -5, -1, 0 })
                        data.Add(ws, expiresSkew, replaySkew, expiresIn);
                }
            }
        }
        return data;
    }

    /// <summary>An in-scope request with a fresh created_time and <c>expires_time = now + expiresIn</c>: admitted iff <c>expiresIn ≥ −ExpiresClockSkew</c>, over HTTP and WebSocket, whatever ClockSkew is.</summary>
    [Theory]
    [MemberData(nameof(EndpointExpiryMatrix))]
    public async Task Endpoint_expiry_follows_ExpiresClockSkew_and_never_ClockSkew(bool overWebSocket, int expiresSkewSeconds, int replaySkewSeconds, int expiresInSeconds)
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.ConfigureDidComm = b => b.Configure(c => c.ExpiresClockSkew = TimeSpan.FromSeconds(expiresSkewSeconds));
            o.ConfigureReplay = r => r.ClockSkew = TimeSpan.FromSeconds(replaySkewSeconds);
        });
        var now = host.Clock.Now.ToUnixTimeSeconds();
        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).WithExpiresTime(now + expiresInSeconds).Build();
        var admitted = expiresInSeconds >= -expiresSkewSeconds;

        if (overWebSocket)
        {
            using var socket = await host.ConnectAsync();
            var sentinel = host.Stamped(host.RecordingMessage(host.Alice, "sentinel")).Build();
            await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, inbound));
            await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, sentinel));
            (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Seen.Any(r => r.Message.Id == sentinel.Id), Timeout)).Should().BeTrue();
        }
        else
        {
            var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, inbound));
            response.Status.Should().Be(admitted ? HttpStatusCode.Accepted : HttpStatusCode.BadRequest);
        }

        host.Recorder.Seen.Any(r => r.Message.Id == inbound.Id).Should().Be(admitted);
        host.DropLines(inbound.Id).Should().BeEmpty("expiry is judged at unpack, before replay admission");
        host.LeakingLogLines().Should().BeEmpty();
    }

    public static TheoryData<int, int?, int> ConsumerExpiryMatrix()
    {
        var data = new TheoryData<int, int?, int>();
        foreach (var expiresSkew in new[] { 0, 5 })
        {
            foreach (var replaySkew in new int?[] { 0, null, 3600 })
            {
                foreach (var expiresIn in new[] { -6, -5, -1, 0 })
                    data.Add(expiresSkew, replaySkew, expiresIn);
            }
        }
        return data;
    }

    /// <summary>A real push carrying <c>expires_time = now + expiresIn</c>: the client admits it iff <c>expiresIn ≥ −ExpiresClockSkew</c>, whatever its ClockSkew is.</summary>
    [Theory]
    [MemberData(nameof(ConsumerExpiryMatrix))]
    public async Task Client_consumer_expiry_follows_ExpiresClockSkew_and_never_ClockSkew(int expiresSkewSeconds, int? replaySkewSeconds, int expiresInSeconds)
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        await using var client = ReturnRouteWebSocketClientHarness.Create(host, configureDidComm: b =>
        {
            ClientReplay(replaySkewSeconds)?.Invoke(b);
            b.Configure(o => o.ExpiresClockSkew = TimeSpan.FromSeconds(expiresSkewSeconds));
        });
        var now = host.Clock.Now.ToUnixTimeSeconds();
        var push = ToAlice(host, Pickup40RecordingHandler.ProtocolUri + "/delivery").WithExpiresTime(now + expiresInSeconds).Build();
        var sentinel = ToAlice(host, RecordingHandler.ProtocolUri + "/push-plain").Build();
        host.Recorder.Behavior = (_, _) => (new[] { push, sentinel }, null);

        await client.Client.SendAsync(host.RecordingMessage(host.Alice, "open").Build(), client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Seen.Any(r => r.Message.Id == sentinel.Id), Timeout)).Should().BeTrue();
        host.Recorder.Seen.Single().PushResults.Should().Equal(true, true);
        var admitted = expiresInSeconds >= -expiresSkewSeconds;
        client.Recorder.Seen.Any(r => r.Message.Id == push.Id).Should().Be(admitted);
        host.Logs.Entries.Count(l => l.Contains("Pushed message dropped: unpack failed (MalformedMessageException)", StringComparison.Ordinal))
            .Should().Be(admitted ? 0 : 1);
        ClientDrops(host, push.Id).Should().BeEmpty();
    }

    // ---- 5. one container that is both a client and a server ------------------------------------

    /// <summary>
    /// Host H (a server with its endpoints, and a client of peer P over a WebSocket session socket,
    /// one container, one guard) and peer P whose clock is <paramref name="peerAheadSeconds"/> ahead
    /// of H's. P sends H a request through H's HTTP endpoint and pushes to H on H's own session
    /// socket; both are judged by H's single ClockSkew. H's request that opens the session is judged
    /// by P's.
    /// </summary>
    [Theory]
    [InlineData(30, null, null)]
    [InlineData(30, 0, null)]
    [InlineData(61, null, null)]
    [InlineData(90, 120, null)]
    [InlineData(-61, null, null)]
    [InlineData(-90, 0, 120)]
    public async Task One_container_as_client_and_server_judges_an_offset_peer_with_one_ClockSkew_in_both_roles(int peerAheadSeconds, int? hostSkewSeconds, int? peerSkewSeconds)
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        await using var peer = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.Start = start.AddSeconds(peerAheadSeconds);
            if (peerSkewSeconds is int ps)
                o.ConfigureReplay = r => r.ClockSkew = TimeSpan.FromSeconds(ps);
        });
        await using var dual = await StartDualAsync(peer, start, hostSkewSeconds);
        var hostClient = dual.App.Services.GetRequiredService<DidCommClient>();

        var hostAdmits = peerAheadSeconds <= (hostSkewSeconds ?? 60) && -peerAheadSeconds <= 300;
        var peerAdmits = -peerAheadSeconds <= (peerSkewSeconds ?? 60) && peerAheadSeconds <= 300;

        // P -> H's server endpoint, stamped with P's clock.
        var toEndpoint = FromPeer(peer, dual, "to-endpoint").WithReturnRoute(ReturnRouteMode.All).WithCreatedTime(peer.Clock.Now.ToUnixTimeSeconds()).Build();
        (await dual.PostAsync(await PackFromPeerAsync(peer, dual, toEndpoint))).Status.Should().Be(HttpStatusCode.Accepted);
        dual.Recorder.Seen.Any(r => r.Message.Id == toEndpoint.Id).Should().Be(hostAdmits, "H's endpoint judges P's stamp with H's ClockSkew");

        // H -> P over H's client session socket; P pushes back, stamped with P's clock.
        var push = FromPeer(peer, dual, "push-rr").WithReturnRoute(ReturnRouteMode.All).Build();
        var sentinel = FromPeer(peer, dual, "push-plain").Build();
        peer.Recorder.Behavior = (_, _) => (new[] { push, sentinel }, null);
        var open = new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/open").WithFrom(dual.ServerDid).WithTo(peer.ServerDid).Build();
        await hostClient.SendAsync(open, new SendOptions(new[] { peer.ServerDid }, From: dual.ServerDid, ServiceEndpointOverride: peer.WebSocketUri("/ws")) { ReturnRoute = ReturnRouteMode.All });

        if (!peerAdmits)
        {
            (await ReturnRouteTestHost.WaitUntilAsync(() => peer.DropLines(open.Id).Count > 0, Timeout)).Should().BeTrue("P judges H's stamp with P's ClockSkew");
            peer.Recorder.Count.Should().Be(0);
            return;
        }
        (await ReturnRouteTestHost.WaitUntilAsync(() => dual.Recorder.Seen.Any(r => r.Message.Id == sentinel.Id), Timeout)).Should().BeTrue();
        dual.Recorder.Seen.Any(r => r.Message.Id == push.Id).Should().Be(hostAdmits, "H's client consumer judges P's push with the same ClockSkew as H's endpoint");
        ClientDrops(dual, push.Id).Should().HaveCount(hostAdmits ? 0 : 1);
    }

    /// <summary>One guard serves both roles: a message admitted by H's endpoint is a Duplicate on H's client consumer, and the other way round.</summary>
    [Fact]
    public async Task One_container_shares_one_replay_cache_between_its_endpoint_and_its_client_consumer()
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        await using var peer = await ReturnRouteTestHost.StartAsync(o => o.Start = start.AddSeconds(30));
        await using var dual = await StartDualAsync(peer, start, hostSkewSeconds: null);
        var consumer = dual.App.Services.GetRequiredService<IInboundFrameConsumer>();
        var endpoint = peer.WebSocketUri("/ws");

        var first = FromPeer(peer, dual, "first").WithReturnRoute(ReturnRouteMode.All).WithCreatedTime(peer.Clock.Now.ToUnixTimeSeconds()).Build();
        var firstPacked = await PackFromPeerAsync(peer, dual, first);
        (await dual.PostAsync(firstPacked)).Status.Should().Be(HttpStatusCode.Accepted);
        dual.Recorder.Seen.Should().ContainSingle(r => r.Message.Id == first.Id);
        await consumer.ConsumeAsync(new InboundFrame(endpoint, dual.ServerDid, peer.ServerDid, Encoding.UTF8.GetBytes(firstPacked)), default);
        ClientDrops(dual, first.Id).Should().ContainSingle().Which.Should().Contain("replay admission " + nameof(ReplayAdmission.Duplicate));

        var second = FromPeer(peer, dual, "second").WithReturnRoute(ReturnRouteMode.All).WithCreatedTime(peer.Clock.Now.ToUnixTimeSeconds()).Build();
        var secondPacked = await PackFromPeerAsync(peer, dual, second);
        await consumer.ConsumeAsync(new InboundFrame(endpoint, dual.ServerDid, peer.ServerDid, Encoding.UTF8.GetBytes(secondPacked)), default);
        dual.Recorder.Seen.Should().ContainSingle(r => r.Message.Id == second.Id);
        (await dual.PostAsync(secondPacked)).Status.Should().Be(HttpStatusCode.Accepted);
        dual.Recorder.Seen.Count(r => r.Message.Id == second.Id).Should().Be(1);
        dual.DropLines(second.Id).Should().ContainSingle(l => l.StartsWith("Inbound ", StringComparison.Ordinal))
            .Which.Should().Contain("replay admission " + nameof(ReplayAdmission.Duplicate));
    }

    // ---- misconfiguration: a ClockSkew that fails Validate() ------------------------------------

    public static TheoryData<string> InvalidSkews => new() { "negative", "TimeSpan.MaxValue" };

    private static TimeSpan InvalidSkew(string name) => name == "negative" ? TimeSpan.FromSeconds(-1) : TimeSpan.MaxValue;

    /// <summary>
    /// An invalid ClockSkew fails closed at first use, as an invalid Window always has: on the client
    /// the guard is resolved on every push, before the scope is known, so every pushed message —
    /// out-of-scope application traffic included — is dropped as "could not be resolved". Nothing is
    /// admitted under a configuration that failed validation. (Validating at start-up instead is a
    /// follow-up outside #105.)
    /// </summary>
    [Theory]
    [MemberData(nameof(InvalidSkews))]
    public async Task An_invalid_client_ClockSkew_fails_closed_at_the_first_push(string skew)
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        await using var client = ReturnRouteWebSocketClientHarness.Create(host, configureDidComm: b =>
            b.Services.AddOptions<ReplayProtectionOptions>().Configure(o => o.ClockSkew = InvalidSkew(skew)));
        var outOfScope = ToAlice(host, RecordingHandler.ProtocolUri + "/push-plain").Build();
        host.Recorder.Behavior = (_, _) => (new[] { outOfScope }, null);

        var send = async () => await client.Client.SendAsync(host.RecordingMessage(host.Alice, "open").Build(), client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        await send.Should().NotThrowAsync("sending does not resolve the guard");
        (await ReturnRouteTestHost.WaitUntilAsync(
            () => host.Logs.Entries.Any(l => l.Contains("Pushed message dropped: the inbound dispatch services could not be resolved", StringComparison.Ordinal)), Timeout))
            .Should().BeTrue();
        client.Recorder.Seen.Should().BeEmpty("the out-of-scope push was dropped too");
    }

    /// <summary>On the server an invalid ClockSkew fails closed on the first request to a registry-aware endpoint — also for an out-of-scope message; no handler runs.</summary>
    [Theory]
    [MemberData(nameof(InvalidSkews))]
    public async Task An_invalid_server_ClockSkew_fails_closed_at_the_registry_aware_endpoint(string skew)
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.ConfigureReplay = r => r.ClockSkew = InvalidSkew(skew));
        var outOfScope = host.RecordingMessage(host.Alice).Build();

        var post = async () => await host.PostAsync(await host.AuthcryptAsync(host.Alice, outOfScope));

        (await post.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("ClockSkew");
        host.Recorder.Invocations.Should().Be(0);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static Action<DidCommBuilder>? ClientReplay(int? skewSeconds)
        => skewSeconds is int s
            ? b => b.Services.AddOptions<ReplayProtectionOptions>().Configure(o => o.ClockSkew = TimeSpan.FromSeconds(s))
            : null;

    private static MessageBuilder ToAlice(ReturnRouteTestHost host, string type)
        => new MessageBuilder().WithType(type).WithFrom(host.ServerDid).WithTo(host.Alice.Did);

    private static MessageBuilder FromPeer(ReturnRouteTestHost peer, ReturnRouteTestHost dual, string name)
        => new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/" + name).WithFrom(peer.ServerDid).WithTo(dual.ServerDid);

    private static async Task<string> PackFromPeerAsync(ReturnRouteTestHost peer, ReturnRouteTestHost dual, Message message)
    {
        var client = peer.App.Services.GetRequiredService<DidCommClient>();
        return (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { dual.ServerDid }, From: peer.ServerDid, Forward: false))).Message;
    }

    /// <summary>A server host whose own container is also a WebSocket client (session sockets, inbound dispatch) of <paramref name="peer"/>.</summary>
    private static Task<ReturnRouteTestHost> StartDualAsync(ReturnRouteTestHost peer, DateTimeOffset start, int? hostSkewSeconds)
        => ReturnRouteTestHost.StartAsync(o =>
        {
            o.Start = start;
            if (hostSkewSeconds is int hs)
                o.ConfigureReplay = r => r.ClockSkew = TimeSpan.FromSeconds(hs);
            o.ConfigureDidComm = b =>
            {
                b.UseWebSocketTransport(w =>
                {
                    w.AllowedSchemes = new[] { "ws" };
                    w.MaxReconnectAttempts = 0;
                    w.ReceiveInbound = true;
                    w.WebSocketFactory = () => new TestServerClientWebSocket();
                    w.Connect = (socket, uri, ct) => ((TestServerClientWebSocket)socket).ConnectAsync(peer.Server, uri, ct);
                });
                b.UseInboundDispatch();
            };
        });

    private static IReadOnlyList<string> ClientDrops(ReturnRouteTestHost host, string messageId)
        => host.Logs.Entries.Where(l => l.Contains("Pushed message " + messageId + " dropped", StringComparison.Ordinal)).ToList();
}
