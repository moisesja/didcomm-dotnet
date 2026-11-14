using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.Transports;
using DidComm.Messages;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.Stomp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #95 D1 (JC-10): the registry-aware WebSocket endpoint's internal core,
/// <c>MapDidCommWebSocketCore(endpoints, pattern, mediatorEndpoint)</c>. The public
/// <c>MapDidCommWebSocket(pattern)</c> passes <c>false</c> and keeps its behaviour (it reads
/// <see cref="DidCommReceiveOptions"/> on every connection); with <c>true</c> — the mediator
/// package's endpoint — the connection uses <c>DidCommReceiveOptions.ForMediatorEndpoint()</c>:
/// same-socket replies on and STOMP off for that endpoint only, while the host's global options and
/// its other WebSocket endpoints are untouched. Replay admission (FR-RR-08) runs on both.
/// </summary>
/// <remarks>
/// One TestServer host maps both endpoints side by side: <c>/ws</c> (the public overload) and
/// <c>/ws-mediator</c> (the core with <c>mediatorEndpoint: true</c>). The recording handler answers
/// every inbound with a reply addressed to its sender, so "a reply came back on the socket" and
/// "nothing was written" are both observable; the receive loop handles a socket's messages in order
/// and the session has one first-in first-out writer, so the next frame after a request is that
/// request's answer unless something else was written first.
/// </remarks>
public sealed class MediatorWebSocketCoreTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const string PublicPath = "/ws";
    private const string MediatorPath = "/ws-mediator";

    // ---- ForMediatorEndpoint ----------------------------------------------------------------

    [Fact]
    public void ForMediatorEndpoint_turns_same_socket_replies_on_and_STOMP_off_in_a_new_instance_and_copies_the_rest()
    {
        IReadOnlyList<string> mediaTypes = new[] { "application/x-test+json" };
        var floor = TimeSpan.FromMilliseconds(37);
        var global = new DidCommReceiveOptions
        {
            AcceptedMediaTypes = mediaTypes,
            ReceiveRejectionFloor = floor,
            AllowSameSocketReplies = false,
            UseStomp = true,
        };

        var copy = global.ForMediatorEndpoint();

        copy.Should().NotBeSameAs(global);
        copy.AllowSameSocketReplies.Should().BeTrue("pickup over a WebSocket answers on the socket");
        copy.UseStomp.Should().BeFalse("a STOMP session can never push");
        copy.AcceptedMediaTypes.Should().Equal(mediaTypes);
        copy.ReceiveRejectionFloor.Should().Be(floor);

        // The host's instance is never changed: its other endpoints keep FR-TRN-10 one-way and STOMP.
        global.AllowSameSocketReplies.Should().BeFalse();
        global.UseStomp.Should().BeTrue();
        global.AcceptedMediaTypes.Should().BeSameAs(mediaTypes);
        global.ReceiveRejectionFloor.Should().Be(floor);

        // The other direction too: a host that already turned same-socket replies on and STOMP off.
        var open = new DidCommReceiveOptions { AllowSameSocketReplies = true, UseStomp = false };
        var openCopy = open.ForMediatorEndpoint();
        openCopy.AllowSameSocketReplies.Should().BeTrue();
        openCopy.UseStomp.Should().BeFalse();
        openCopy.AcceptedMediaTypes.Should().Equal(new DidCommReceiveOptions().AcceptedMediaTypes);
        openCopy.ReceiveRejectionFloor.Should().Be(new DidCommReceiveOptions().ReceiveRejectionFloor);
    }

    [Fact]
    public void DidCommReceiveOptions_has_no_setting_that_ForMediatorEndpoint_does_not_handle()
    {
        // ForMediatorEndpoint() copies field by field. A setting added to DidCommReceiveOptions
        // without a line there would silently fall back to its default on the mediator's socket;
        // this list makes that a test failure instead. Update ForMediatorEndpoint() and the list.
        typeof(DidCommReceiveOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().BeEquivalentTo(
                nameof(DidCommReceiveOptions.AcceptedMediaTypes),
                nameof(DidCommReceiveOptions.AllowSameSocketReplies),
                nameof(DidCommReceiveOptions.UseStomp),
                nameof(DidCommReceiveOptions.ReceiveRejectionFloor));
    }

    // ---- the two endpoints side by side ------------------------------------------------------

    [Fact]
    public async Task With_same_socket_replies_off_globally_the_public_endpoint_stays_one_way_while_the_mediator_endpoint_opens_a_session_and_replies()
    {
        await using var host = await CoreHost.StartAsync(allowSameSocketReplies: false, useStomp: false);

        // The public endpoint: admitted and dispatched without a session; nothing is written.
        var overPublic = host.Inbound(host.Alice);
        using (var socket = await host.ConnectAsync(PublicPath))
        {
            await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, overPublic));
            (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 1, Timeout)).Should().BeTrue();
            host.Registry.Count.Should().Be(0, "the public endpoint never opens a session with the switch off");
            await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(socket);
        }
        host.Recorder.Seen.Single(r => r.Message.Id == overPublic.Id).Session.Should().BeNull("FR-TRN-10: one-way");

        // The mediator endpoint: the first eligible message opens a session and the reply rides it.
        var overMediator = host.Inbound(host.Alice);
        using (var socket = await host.ConnectAsync(MediatorPath))
        {
            await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, overMediator));
            var reply = await host.ReceiveReplyAsync(socket, host.Alice);
            reply.Thid.Should().Be(overMediator.Id);
            host.Registry.Count.Should().Be(1, "the mediator endpoint registered the connection's session");
            host.Registry.Find(host.Alice.Did).Should().ContainSingle();
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        }
        host.Recorder.Seen.Single(r => r.Message.Id == overMediator.Id).Session.Should().NotBeNull();
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 0, Timeout)).Should().BeTrue("closing the socket unregisters the session");

        // The global options were read, not changed.
        host.GlobalReceiveOptions.AllowSameSocketReplies.Should().BeFalse();
        host.GlobalReceiveOptions.UseStomp.Should().BeFalse();

        // And the public endpoint is still one-way after the mediator endpoint served a session.
        var again = host.Inbound(host.Alice);
        using (var socket = await host.ConnectAsync(PublicPath))
        {
            await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, again));
            (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 3, Timeout)).Should().BeTrue();
            await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(socket);
        }
        host.Recorder.Seen.Single(r => r.Message.Id == again.Id).Session.Should().BeNull();
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Under_a_global_UseStomp_the_mediator_endpoint_speaks_bare_envelopes_and_opens_a_session_while_the_public_endpoint_speaks_STOMP(bool allowSameSocketReplies)
    {
        await using var host = await CoreHost.StartAsync(allowSameSocketReplies, useStomp: true);

        // The public endpoint speaks STOMP: CONNECT is answered CONNECTED, an envelope rides a SEND
        // frame and is dispatched without a session (STOMP never gets one, FR-RR-05).
        var overStomp = host.Inbound(host.Alice);
        using (var socket = await host.ConnectAsync(PublicPath))
        {
            await SendStompAsync(socket, new StompFrame("CONNECT", new[]
            {
                KeyValuePair.Create("accept-version", "1.2"),
                KeyValuePair.Create("host", "localhost"),
            }, ReadOnlyMemory<byte>.Empty));
            StompFrameCodec.Decode(await ReceiveBytesAsync(socket)).Command.Should().Be("CONNECTED");
            await SendStompAsync(socket, new StompFrame("SEND", new[]
            {
                KeyValuePair.Create("destination", PublicPath),
                KeyValuePair.Create("content-type", "application/didcomm-encrypted+json"),
            }, Encoding.UTF8.GetBytes(await host.AuthcryptAsync(host.Alice, overStomp))));
            (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 1, Timeout)).Should().BeTrue();
            await SendStompAsync(socket, new StompFrame("DISCONNECT", Array.Empty<KeyValuePair<string, string>>(), ReadOnlyMemory<byte>.Empty));
            var closing = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
            closing.Type.Should().Be(WebSocketMessageType.Close, "nothing was written between CONNECTED and the DISCONNECT close");
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", default);
        }
        host.Recorder.Seen.Single(r => r.Message.Id == overStomp.Id).Session.Should().BeNull();
        host.Registry.Count.Should().Be(0);

        // The mediator endpoint ignores the global STOMP switch: a bare envelope (which STOMP would
        // refuse with a 1002 close) opens a session and its reply comes back as one binary message.
        var bare = host.Inbound(host.Alice);
        using (var socket = await host.ConnectAsync(MediatorPath))
        {
            await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, bare));
            var reply = await host.ReceiveReplyAsync(socket, host.Alice);
            reply.Thid.Should().Be(bare.Id);
            host.Registry.Count.Should().Be(1);

            // The session carries on for the next request on the same socket.
            var second = host.Inbound(host.Alice);
            await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, second));
            (await host.ReceiveReplyAsync(socket, host.Alice)).Thid.Should().Be(second.Id);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        }
        host.Recorder.Seen.Single(r => r.Message.Id == bare.Id).Session.Should().NotBeNull();
        host.Logs.Entries.Should().NotContain(e => e.Contains("is not available under STOMP") && e.Contains(bare.Id),
            "the mediator endpoint is not in STOMP mode, so it never logs the STOMP notice for its messages");

        host.GlobalReceiveOptions.UseStomp.Should().BeTrue("the host's STOMP setting is left as it was");
        host.GlobalReceiveOptions.AllowSameSocketReplies.Should().Be(allowSameSocketReplies);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_admission_applies_on_the_mediator_endpoint_a_repeated_message_is_dropped()
    {
        await using var host = await CoreHost.StartAsync(allowSameSocketReplies: false, useStomp: false);
        var first = host.Inbound(host.Alice);
        var packed = await host.AuthcryptAsync(host.Alice, first);
        var third = host.Inbound(host.Alice);

        using var socket = await host.ConnectAsync(MediatorPath);
        await ReturnRouteTestHost.SendAsync(socket, packed);
        await ReturnRouteTestHost.SendAsync(socket, packed);
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, third));

        // In order: the first's reply, then the third's — nothing was written for the repeat.
        (await host.ReceiveReplyAsync(socket, host.Alice)).Thid.Should().Be(first.Id);
        (await host.ReceiveReplyAsync(socket, host.Alice)).Thid.Should().Be(third.Id);

        host.Recorder.Invocations.Should().Be(2, "the repeat is refused before dispatch (FR-RR-08)");
        host.Recorder.Seen.Select(r => r.Message.Id).Should().Equal(first.Id, third.Id);
        host.DropLines(first.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.Duplicate));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task The_mediator_endpoint_shares_the_replay_cache_of_the_host_a_message_admitted_on_the_public_endpoint_is_a_duplicate_there()
    {
        await using var host = await CoreHost.StartAsync(allowSameSocketReplies: false, useStomp: false);
        var inbound = host.Inbound(host.Alice);
        var packed = await host.AuthcryptAsync(host.Alice, inbound);

        using (var socket = await host.ConnectAsync(PublicPath))
        {
            await ReturnRouteTestHost.SendAsync(socket, packed);
            (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 1, Timeout)).Should().BeTrue();
            await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(socket);
        }

        using (var socket = await host.ConnectAsync(MediatorPath))
        {
            await ReturnRouteTestHost.SendAsync(socket, packed);
            var control = host.Inbound(host.Alice);
            await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, control));
            (await host.ReceiveReplyAsync(socket, host.Alice)).Thid.Should().Be(control.Id, "the replayed message got no reply and opened no session");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        }

        host.Recorder.Seen.Count(r => r.Message.Id == inbound.Id).Should().Be(1);
        host.DropLines(inbound.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.Duplicate));
    }

    // ---- the public overload is unchanged ----------------------------------------------------

    [Fact]
    public async Task The_public_overload_still_reads_the_receive_options_on_every_connection_and_the_mediator_endpoint_still_forces_its_own()
    {
        // The options are read when a connection arrives, not when the endpoint is mapped: a host
        // that changes the switch affects the next connection of the public endpoint. The mediator
        // endpoint reads them per connection too, and still forces its two settings.
        await using var host = await CoreHost.StartAsync(allowSameSocketReplies: false, useStomp: false);

        host.GlobalReceiveOptions.AllowSameSocketReplies = true;
        var opened = host.Inbound(host.Alice);
        using (var socket = await host.ConnectAsync(PublicPath))
        {
            await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, opened));
            (await host.ReceiveReplyAsync(socket, host.Alice)).Thid.Should().Be(opened.Id, "the public endpoint read the switch when this connection arrived");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        }

        host.GlobalReceiveOptions.AllowSameSocketReplies = false;
        host.GlobalReceiveOptions.UseStomp = true;
        var forced = host.Inbound(host.Alice);
        using (var socket = await host.ConnectAsync(MediatorPath))
        {
            await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, forced));
            (await host.ReceiveReplyAsync(socket, host.Alice)).Thid.Should().Be(forced.Id);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        }
        host.GlobalReceiveOptions.AllowSameSocketReplies.Should().BeFalse();
        host.GlobalReceiveOptions.UseStomp.Should().BeTrue();
    }

    [Fact]
    public async Task The_public_overload_and_the_core_validate_their_arguments_as_before()
    {
        await using var host = await CoreHost.StartAsync(allowSameSocketReplies: false, useStomp: false);
        IEndpointRouteBuilder endpoints = host.App;

        Action nullEndpoints = () => DidCommEndpointRouteBuilderExtensions.MapDidCommWebSocket(null!, "/x");
        Action nullPattern = () => endpoints.MapDidCommWebSocket(null!);
        Action emptyPattern = () => endpoints.MapDidCommWebSocket(string.Empty);
        Action coreNullEndpoints = () => DidCommEndpointRouteBuilderExtensions.MapDidCommWebSocketCore(null!, "/x", mediatorEndpoint: true);
        Action coreEmptyPattern = () => DidCommEndpointRouteBuilderExtensions.MapDidCommWebSocketCore(endpoints, string.Empty, mediatorEndpoint: true);

        nullEndpoints.Should().Throw<ArgumentNullException>().WithParameterName("endpoints");
        nullPattern.Should().Throw<ArgumentNullException>().WithParameterName("pattern");
        emptyPattern.Should().Throw<ArgumentException>().WithParameterName("pattern");
        coreNullEndpoints.Should().Throw<ArgumentNullException>().WithParameterName("endpoints");
        coreEmptyPattern.Should().Throw<ArgumentException>().WithParameterName("pattern");
    }

    // ---- harness ----------------------------------------------------------------------------

    private static Task SendStompAsync(WebSocket socket, StompFrame frame)
        => socket.SendAsync(StompFrameCodec.Encode(frame), WebSocketMessageType.Binary, endOfMessage: true, default);

    private static async Task<byte[]> ReceiveBytesAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cts.Token);
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return ms.ToArray();
    }

    /// <summary>
    /// A TestServer agent with did:peer resolution, the recording handler (which replies to every
    /// inbound's sender), an injected clock and a capturing logger; <c>/ws</c> is the public
    /// registry-aware WebSocket endpoint and <c>/ws-mediator</c> the core with
    /// <c>mediatorEndpoint: true</c>. The global <see cref="DidCommReceiveOptions"/> are the
    /// test's.
    /// </summary>
    private sealed class CoreHost : IAsyncDisposable
    {
        private CoreHost(WebApplication app, TestClock clock, CapturingLoggerProvider logs, RecordingHandlerState recorder, string serverDid, IReadOnlyList<string> serverKids, TestPeer alice)
        {
            App = app;
            Clock = clock;
            Logs = logs;
            Recorder = recorder;
            ServerDid = serverDid;
            ServerKids = serverKids;
            Alice = alice;
        }

        public WebApplication App { get; }

        public TestClock Clock { get; }

        public CapturingLoggerProvider Logs { get; }

        public RecordingHandlerState Recorder { get; }

        public string ServerDid { get; }

        public IReadOnlyList<string> ServerKids { get; }

        public TestPeer Alice { get; }

        public IReturnRouteSessionRegistry Registry => App.Services.GetRequiredService<IReturnRouteSessionRegistry>();

        /// <summary>The host's own instance of the receive options (what <c>IOptions</c> hands every endpoint).</summary>
        public DidCommReceiveOptions GlobalReceiveOptions => App.Services.GetRequiredService<IOptions<DidCommReceiveOptions>>().Value;

        public static async Task<CoreHost> StartAsync(bool allowSameSocketReplies, bool useStomp)
        {
            var serverSecrets = new InMemorySecretsResolver();
            var (server, alice, carol, mallory) = await ReturnRouteTestHost.CreateIdentitiesAsync(serverSecrets);
            await carol.DisposeAsync();
            await mallory.DisposeAsync();

            var clock = new TestClock(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
            var logs = new CapturingLoggerProvider();
            var recorder = new RecordingHandlerState();
            var serverDid = server.Did;
            recorder.Behavior = (message, _) => (Array.Empty<Message>(), new MessageBuilder()
                .WithType(RecordingHandler.ProtocolUri + "/reply")
                .WithFrom(serverDid)
                .WithTo(message.From!)
                .WithThid(message.Id)
                .Build());

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddProvider(logs);
            builder.Services.AddSingleton(recorder);
            builder.Services.AddDidComm(b =>
            {
                b.UseNetDidResolver();
                b.UseSecretsResolver(serverSecrets);
                b.AddBuiltInProtocols();
                b.AddProtocol<RecordingHandler>();
                b.Configure(o => o.Clock = () => clock.Now);
            });
            builder.Services.AddOptions<DidCommReceiveOptions>().Configure(o =>
            {
                o.AllowSameSocketReplies = allowSameSocketReplies;
                o.UseStomp = useStomp;
            });

            var app = builder.Build();
            app.UseWebSockets();
            app.UseRouting();
            app.MapDidCommWebSocket(PublicPath);
            DidCommEndpointRouteBuilderExtensions.MapDidCommWebSocketCore(app, MediatorPath, mediatorEndpoint: true);
            await app.StartAsync();

            return new CoreHost(app, clock, logs, recorder, server.Did, ReturnRouteTestHost.KidsOf(server), alice);
        }

        /// <summary>A stamped message of the recording protocol from <paramref name="from"/> with <c>return_route: all</c> (in the FR-RR-08 scope).</summary>
        public Message Inbound(TestPeer from)
            => new MessageBuilder()
                .WithType(RecordingHandler.ProtocolUri + "/probe")
                .WithFrom(from.Did)
                .WithTo(ServerDid)
                .WithCreatedTime(Clock.Now.ToUnixTimeSeconds())
                .WithReturnRoute(ReturnRouteMode.All)
                .Build();

        public async Task<string> AuthcryptAsync(TestPeer from, Message message)
            => (await from.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
                Recipients: new[] { ServerDid },
                From: from.Did,
                Forward: false))).Message;

        public Task<WebSocket> ConnectAsync(string path)
            => App.GetTestServer().CreateWebSocketClient().ConnectAsync(
                new UriBuilder(App.GetTestServer().BaseAddress) { Scheme = "ws", Path = path }.Uri, default);

        /// <summary>Receive the next whole message on <paramref name="socket"/>: one binary frame, authcrypted by the server to exactly <paramref name="peer"/>.</summary>
        public async Task<Message> ReceiveReplyAsync(WebSocket socket, TestPeer peer)
        {
            var (type, text) = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
            type.Should().Be(WebSocketMessageType.Binary, "each reply is one binary WebSocket message");
            var unpacked = await peer.Client.UnpackAsync(text);
            unpacked.Encrypted.Should().BeTrue();
            unpacked.Authenticated.Should().BeTrue();
            unpacked.SenderKid.Should().StartWith(ServerDid + "#");
            unpacked.Message.From.Should().Be(ServerDid);
            unpacked.Message.To.Should().Equal(peer.Did);
            return unpacked.Message;
        }

        /// <summary>The FR-RR-08 drop lines (Warning and Debug) for <paramref name="messageId"/>.</summary>
        public IReadOnlyList<string> DropLines(string messageId)
            => Logs.Entries.Where(line => line.Contains("dropped: replay admission", StringComparison.Ordinal)
                                          && line.Contains(messageId, StringComparison.Ordinal)).ToList();

        /// <summary>Log lines that carry a DID or key id of the server or Alice (must be empty).</summary>
        public IReadOnlyList<string> LeakingLogLines()
        {
            var sensitive = ServerKids.Concat(Alice.Kids).Append(ServerDid).Append(Alice.Did).ToArray();
            return Logs.Entries.Where(line => sensitive.Any(v => line.Contains(v, StringComparison.Ordinal))).ToList();
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            await Alice.DisposeAsync();
        }
    }
}
