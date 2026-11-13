using System.Net;
using System.Text;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #105 — the replay rule's own clock skew (<see cref="ReplayProtectionOptions.ClockSkew"/>, default
/// 60 s) at every place it is applied: the registry-aware HTTP and WebSocket endpoints with
/// <c>AddDidComm</c>'s guard, the endpoints' per-application fallback guard, and the client's inbound
/// consumer. <c>DidCommOptions.ExpiresClockSkew</c> no longer widens replay admission, and
/// <c>ClockSkew</c> never widens FR-API-05 expiry. All on an injected, whole-second clock.
/// </summary>
public sealed class ReplayClockSkewEndpointTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // ---- the server endpoints -------------------------------------------------------------------

    [Theory]
    [InlineData(59, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    public async Task With_default_options_an_HTTP_request_created_up_to_60_seconds_ahead_is_admitted(int aheadSeconds, bool admitted)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));
        var inbound = Ahead(host, host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All), aheadSeconds).Build();

        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, inbound));

        if (admitted)
        {
            response.Status.Should().Be(HttpStatusCode.OK);
            host.Recorder.Invocations.Should().Be(1);
            host.DropLines(inbound.Id).Should().BeEmpty();
        }
        else
        {
            response.Status.Should().Be(HttpStatusCode.Accepted);
            response.Body.Should().BeEmpty();
            host.Recorder.Invocations.Should().Be(0, "a refused request reaches no handler");
            host.DropLines(inbound.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.FutureDated));
        }
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData(59, true)]
    [InlineData(61, false)]
    public async Task With_default_options_a_WebSocket_request_created_up_to_60_seconds_ahead_is_admitted(int aheadSeconds, bool admitted)
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        var inbound = Ahead(host, host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All), aheadSeconds).Build();
        using var socket = await host.ConnectAsync();

        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, inbound));

        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Invocations + host.DropLines(inbound.Id).Count > 0, Timeout)).Should().BeTrue();
        host.Recorder.Invocations.Should().Be(admitted ? 1 : 0);
        host.Registry.Find(host.Alice.Did).Should().HaveCount(admitted ? 1 : 0, "only an admitted message opens a session");
        if (!admitted)
            host.DropLines(inbound.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.FutureDated));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task ExpiresClockSkew_alone_no_longer_widens_replay_admission()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
            o.ConfigureDidComm = b => b.Configure(c => c.ExpiresClockSkew = TimeSpan.FromHours(1)));
        var past = Ahead(host, host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All), 61).Build();

        (await host.PostAsync(await host.AuthcryptAsync(host.Alice, past))).Status.Should().Be(HttpStatusCode.Accepted);

        host.Recorder.Invocations.Should().Be(0, "an hour of expiry tolerance does not move the replay bound");
        host.DropLines(past.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.FutureDated));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task ClockSkew_zero_restores_the_strict_bound_whatever_ExpiresClockSkew_says()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.ConfigureDidComm = b => b.Configure(c => c.ExpiresClockSkew = TimeSpan.FromHours(1));
            o.ConfigureReplay = r => r.ClockSkew = TimeSpan.Zero;
        });
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));
        var now = Ahead(host, host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All), 0).Build();
        var oneAhead = Ahead(host, host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All), 1).Build();

        (await host.PostAsync(await host.AuthcryptAsync(host.Alice, now))).Status.Should().Be(HttpStatusCode.OK);
        (await host.PostAsync(await host.AuthcryptAsync(host.Alice, oneAhead))).Status.Should().Be(HttpStatusCode.Accepted);

        host.Recorder.Invocations.Should().Be(1);
        host.DropLines(oneAhead.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.FutureDated));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task With_the_default_skew_a_replay_is_remembered_until_created_plus_Window_plus_skew_and_refused_on_age_after_Window()
    {
        // FR-RR-08 acceptance (clock advancement), with the default 60 s skew and a request stamped
        // 60 s ahead of the server's clock — the latest the server admits.
        await using var host = await ReturnRouteTestHost.StartAsync();
        var guard = (InMemoryReplayGuard)host.ReplayGuard;
        var start = host.Clock.Now;
        var inbound = Ahead(host, host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All), 60).Build();
        var packed = await host.AuthcryptAsync(host.Alice, inbound);
        var created = start.AddSeconds(60);

        async Task<int> ReplayAtAsync(DateTimeOffset at)
        {
            host.Clock.Advance(at - host.Clock.Now);
            // Another sender's in-scope request at the same instant drives the guard's purge.
            var noise = host.Stamped(host.RecordingMessage(host.Carol).WithReturnRoute(ReturnRouteMode.All)).Build();
            (await host.PostAsync(await host.AuthcryptAsync(host.Carol, noise))).Status.Should().Be(HttpStatusCode.Accepted);
            (await host.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
            return guard.PeerCount;
        }

        (await host.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        host.Recorder.Invocations.Should().Be(1);
        (await ReplayAtAsync(created.AddSeconds(300))).Should().Be(2, "at created + Window the entry is held");
        (await ReplayAtAsync(created.AddSeconds(301))).Should().Be(2, "refused on age, and still held");
        (await ReplayAtAsync(created.AddSeconds(360))).Should().Be(2, "held until created + Window + ClockSkew inclusive");
        (await ReplayAtAsync(created.AddSeconds(361))).Should().Be(1, "purged after it");

        host.Recorder.Invocations.Should().Be(1 + 4, "the original and Carol's four requests; no replay");
        host.DropLines(inbound.Id).Select(Outcome).Should().Equal(
            nameof(ReplayAdmission.Duplicate),
            nameof(ReplayAdmission.TooOld),
            nameof(ReplayAdmission.TooOld),
            nameof(ReplayAdmission.TooOld));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, 59, true)]
    [InlineData(false, 61, false)]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]
    public async Task The_endpoints_fallback_guard_reads_ClockSkew_from_the_configured_options(bool strict, int aheadSeconds, bool admitted)
    {
        // A host that maps the endpoints without AddDidComm's registrations (JC-17): the second place
        // a guard is built.
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            if (strict)
                o.ConfigureReplay = r => r.ClockSkew = TimeSpan.Zero;
            o.PostConfigureServices = s =>
            {
                s.RemoveAll<IInboundReplayGuard>();
                s.RemoveAll<IReturnRouteSessionRegistry>();
            };
        });
        host.App.Services.GetService<IInboundReplayGuard>().Should().BeNull("the scenario is a host without the registrations");
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));
        var inbound = Ahead(host, host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All), aheadSeconds).Build();

        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, inbound));

        response.Status.Should().Be(admitted ? HttpStatusCode.OK : HttpStatusCode.Accepted);
        host.Recorder.Invocations.Should().Be(admitted ? 1 : 0);
        if (!admitted)
            host.DropLines(inbound.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.FutureDated));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    public async Task A_large_ClockSkew_never_lets_an_expired_request_through(int expiresInSeconds, bool admitted)
    {
        // FR-API-05 expiry keeps its own (zero) tolerance: an in-scope request with a fresh
        // created_time and an expires_time one second in the past is rejected at unpack.
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.ConfigureReplay = r => r.ClockSkew = TimeSpan.FromHours(1));
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));
        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All))
            .WithExpiresTime(host.Clock.Now.ToUnixTimeSeconds() + expiresInSeconds)
            .Build();

        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, inbound));

        response.Status.Should().Be(admitted ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
        host.Recorder.Invocations.Should().Be(admitted ? 1 : 0);
        host.DropLines(inbound.Id).Should().BeEmpty("expiry refuses it before replay admission is reached");
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- the client's inbound consumer (WebSocket session pushes, #92) ----------------------------

    [Theory]
    [InlineData(59, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    public async Task With_default_options_the_client_admits_a_push_created_up_to_60_seconds_ahead_of_its_clock(int aheadSeconds, bool admitted)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteWebSocketClientHarness.Create(host);
        var push = await ServerPushAsync(host, PickupDelivery(host).WithCreatedTime(host.Clock.Now.ToUnixTimeSeconds() + aheadSeconds).Build());

        await ConsumerOf(client).ConsumeAsync(FrameFor(client, push.Bytes), default);

        client.Recorder.Invocations.Should().Be(admitted ? 1 : 0);
        var drops = ClientDrops(host, push.Id);
        if (admitted)
            drops.Should().BeEmpty();
        else
            drops.Should().ContainSingle().Which.Should().Contain("replay admission " + nameof(ReplayAdmission.FutureDated));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task On_the_client_ClockSkew_zero_refuses_a_push_one_second_ahead_and_ExpiresClockSkew_does_not_widen_it(bool generousExpiry)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteWebSocketClientHarness.Create(host, configureDidComm: b =>
        {
            b.Services.AddOptions<ReplayProtectionOptions>().Configure(o => o.ClockSkew = TimeSpan.Zero);
            if (generousExpiry)
                b.Configure(o => o.ExpiresClockSkew = TimeSpan.FromHours(1));
        });
        var push = await ServerPushAsync(host, PickupDelivery(host).WithCreatedTime(host.Clock.Now.ToUnixTimeSeconds() + 1).Build());

        await ConsumerOf(client).ConsumeAsync(FrameFor(client, push.Bytes), default);

        client.Recorder.Invocations.Should().Be(0);
        ClientDrops(host, push.Id).Should().ContainSingle().Which.Should().Contain("replay admission " + nameof(ReplayAdmission.FutureDated));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    public async Task On_the_client_a_large_ClockSkew_never_lets_an_expired_push_through(int expiresInSeconds, bool admitted)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteWebSocketClientHarness.Create(host, configureDidComm: b =>
            b.Services.AddOptions<ReplayProtectionOptions>().Configure(o => o.ClockSkew = TimeSpan.FromHours(1)));
        var now = host.Clock.Now.ToUnixTimeSeconds();
        var push = await ServerPushAsync(host, PickupDelivery(host).WithCreatedTime(now).WithExpiresTime(now + expiresInSeconds).Build());

        await ConsumerOf(client).ConsumeAsync(FrameFor(client, push.Bytes), default);

        client.Recorder.Invocations.Should().Be(admitted ? 1 : 0);
        if (!admitted)
            host.Logs.Entries.Should().ContainSingle(l => l.Contains("Pushed message dropped: unpack failed", StringComparison.Ordinal),
                "FR-API-05 refuses it at unpack, before replay admission");
        ClientDrops(host, push.Id).Should().BeEmpty();
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static MessageBuilder Ahead(ReturnRouteTestHost host, MessageBuilder builder, int seconds)
        => builder.WithCreatedTime(host.Clock.Now.ToUnixTimeSeconds() + seconds);

    private static string Outcome(string dropLine)
    {
        const string Marker = "replay admission ";
        var start = dropLine.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length;
        var end = dropLine.IndexOf(' ', start);
        return dropLine[start..end];
    }

    private static IReadOnlyList<string> ClientDrops(ReturnRouteTestHost host, string messageId)
        => host.Logs.Entries.Where(l => l.Contains("Pushed message " + messageId + " dropped", StringComparison.Ordinal)).ToList();

    private static IInboundFrameConsumer ConsumerOf(ReturnRouteWebSocketClientHarness client)
        => client.Services.GetRequiredService<IInboundFrameConsumer>();

    /// <summary>A frame as the client's session socket to the server, opened for Alice, would attribute it.</summary>
    private static InboundFrame FrameFor(ReturnRouteWebSocketClientHarness client, byte[] body)
        => new(client.Endpoint, client.Host.Alice.Did, client.Host.ServerDid, body);

    /// <summary>Authcrypt <paramref name="message"/> from the server to Alice, as a real session push would be.</summary>
    private static async Task<(byte[] Bytes, string Id)> ServerPushAsync(ReturnRouteTestHost host, Message message)
    {
        var server = host.App.Services.GetRequiredService<DidCommClient>();
        var packed = await server.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { host.Alice.Did }, From: host.ServerDid, Forward: false));
        return (Encoding.UTF8.GetBytes(packed.Message), message.Id);
    }

    /// <summary>A mediator-service message (FR-RR-08 scope b) the client's pickup 4.0 recorder handles.</summary>
    private static MessageBuilder PickupDelivery(ReturnRouteTestHost host)
        => new MessageBuilder().WithType("https://didcomm.org/message-pickup/4.0/delivery").WithFrom(host.ServerDid).WithTo(host.Alice.Did);
}
