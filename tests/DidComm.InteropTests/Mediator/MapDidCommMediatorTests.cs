using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Transports;
using DidComm.Mediator.AspNetCore;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

// L-014: alias the static OutOfBand API class so the same-named namespace doesn't shadow it.
using OutOfBandApi = DidComm.Protocols.OutOfBand.OutOfBand;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// #95 D2 (design §8, JC-10): <c>MapDidCommMediator(pattern)</c> maps <c>POST {pattern}</c> (the
/// registry-aware HTTP endpoint, unchanged), <c>GET {pattern}</c> (the WebSocket core with
/// same-socket replies on and STOMP off for itself only) and <c>GET {base}/oob</c> (the mediator's
/// out-of-band invitation under <see cref="MediatorEndpointRouteBuilderExtensions.InvitationOobId"/>),
/// refuses to map without <c>AddMediator()</c>, and applies conventions to all three endpoints.
/// </summary>
/// <remarks>
/// <c>AddMediator()</c> is still a stub on this branch, so the container is composed by hand the way
/// it will compose it: Coordinate Mediation, the routing mediator and Message Pickup over did:peer
/// identities, a recording <see cref="IForwardSink"/> registered first (the host's sink wins), plus
/// the internal <see cref="MediatorMarker"/> and <see cref="MediatorInvitationHolder"/>. The global
/// <see cref="DidCommReceiveOptions"/> keep same-socket replies off and, where it matters, STOMP on,
/// so every WebSocket exchange proves the endpoint's own override.
/// </remarks>
public sealed class MapDidCommMediatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // ---- map time -----------------------------------------------------------------------------

    [Fact]
    public async Task Mapping_without_AddMediator_is_refused_at_map_time_and_maps_nothing()
    {
        await using var app = await BuildBareAppAsync(registerMediator: false);

        var act = () => app.MapDidCommMediator("/didcomm");

        act.Should().Throw<InvalidOperationException>().WithMessage("MapDidCommMediator requires AddMediator()*");
        EndpointsOf(app).Should().BeEmpty("nothing is mapped when the mediator is not registered");
    }

    [Fact]
    public async Task The_pattern_is_validated_before_anything_is_mapped_and_the_refusal_never_quotes_it()
    {
        await using var app = await BuildBareAppAsync(registerMediator: true);
        IEndpointRouteBuilder endpoints = app;

        Action nullEndpoints = () => MediatorEndpointRouteBuilderExtensions.MapDidCommMediator(null!, "/didcomm");
        Action nullPattern = () => endpoints.MapDidCommMediator(null!);
        nullEndpoints.Should().Throw<ArgumentNullException>().WithParameterName("endpoints");
        nullPattern.Should().Throw<ArgumentNullException>().WithParameterName("pattern");

        foreach (var bad in new[] { string.Empty, "relative-segment-x", "~/tilde-segment-x", " /leading-space-x" })
        {
            Action map = () => endpoints.MapDidCommMediator(bad);
            var refused = map.Should().Throw<ArgumentException>().Which;
            refused.ParamName.Should().Be("pattern");
            refused.Should().NotBeOfType<ArgumentNullException>();
            if (bad.Length > 0)
                refused.Message.Should().NotContain(bad.Trim(), "a refusal names the parameter, never the value");
        }

        // Patterns routing refuses — the pattern itself, or the invitation path below it (a
        // catch-all must be the last segment) — are refused before any endpoint is mapped.
        foreach (var bad in new[] { "/doubled//slash-x", "/catch-all-x/{**rest}" })
        {
            Action map = () => endpoints.MapDidCommMediator(bad);
            var refused = map.Should().Throw<ArgumentException>().Which;
            refused.ParamName.Should().Be("pattern");
            refused.Message.Should().NotContain(bad);
        }

        EndpointsOf(app).Should().BeEmpty("every refusal happens before the first endpoint is mapped");
    }

    [Fact]
    public async Task Mapping_logs_one_Information_line_with_the_paths_only()
    {
        await using var host = await MediatorEndpointHost.StartAsync(o => o.Pattern = "/didcomm");

        var lines = host.Logs.Records.Where(r => r.Text.StartsWith("Mediator endpoints mapped", StringComparison.Ordinal)).ToList();
        lines.Should().ContainSingle();
        lines[0].Level.Should().Be(LogLevel.Information);
        lines[0].Text.Should().Contain("POST /didcomm").And.Contain("GET /didcomm").And.Contain("GET /didcomm/oob?_oobid=mediator");
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- routes -------------------------------------------------------------------------------

    [Theory]
    [InlineData("/")]
    [InlineData("/didcomm")]
    [InlineData("/didcomm/")]
    [InlineData("/tenants/{tenant}/didcomm")]
    public async Task Each_pattern_routes_HTTP_WebSocket_and_the_invitation_to_the_right_endpoint(string pattern)
    {
        await using var host = await MediatorEndpointHost.StartAsync(o => o.Pattern = pattern);
        var path = pattern.Replace("{tenant}", "acme", StringComparison.Ordinal);
        var basePath = path.TrimEnd('/');

        // POST: Coordinate Mediation over the registry-aware HTTP endpoint (the grant is the 200 body).
        await host.EnrolAsync(host.Carol, path);

        // GET with an upgrade: the WebSocket endpoint answers a status-request on the socket.
        using (var socket = await host.ConnectAsync(path))
        {
            var request = MessagePickup.CreateStatusRequest(host.Carol.Did, host.ServerDid);
            (await host.AskOverSocketAsync(socket, host.Carol, request)).Status(request.Id).MessageCount.Should().Be(0);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        }

        // GET without an upgrade reaches the WebSocket endpoint too (400), not a 404 or 405.
        (await host.GetAsync(path)).Status.Should().Be(HttpStatusCode.BadRequest);

        // GET {base}/oob: the URL a host publishes, built exactly as the XML docs say.
        var url = OutOfBandApi.ToShortUrl($"{host.PublicBase}{basePath}/oob", MediatorEndpointRouteBuilderExtensions.InvitationOobId);
        var invitation = await host.GetAsync(url);
        invitation.Status.Should().Be(HttpStatusCode.OK);
        invitation.ContentType.Should().StartWith(MediaTypes.Plaintext);
        invitation.Body.Should().Be(host.InvitationJson);
        var parsed = OutOfBandApi.FromPlaintext(invitation.Body);
        parsed.Message.From.Should().Be(host.ServerDid);
        parsed.Message.Body!["goal_code"]!.GetValue<string>().Should().Be("request-mediate");

        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_pattern_with_a_trailing_slash_also_answers_without_it()
    {
        await using var host = await MediatorEndpointHost.StartAsync(o => o.Pattern = "/didcomm/");

        await host.EnrolAsync(host.Carol, "/didcomm");
        using var socket = await host.ConnectAsync("/didcomm");
        var request = MessagePickup.CreateStatusRequest(host.Carol.Did, host.ServerDid);
        (await host.AskOverSocketAsync(socket, host.Carol, request)).Status(request.Id).MessageCount.Should().Be(0);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ---- HTTP: the transport acknowledgement is kept ------------------------------------------

    [Fact]
    public async Task A_forward_is_neither_processed_nor_handed_to_the_sink_until_its_202_has_been_committed()
    {
        await using var host = await MediatorEndpointHost.StartAsync();
        await host.EnrolAsync(host.Carol);
        var (packed, forward, inner) = await host.ForwardFromAliceAsync(host.Carol);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Controls.ResponseGate = gate;

        var post = Task.Run(() => host.PostAsync(packed));
        await host.WaitUntilAsync(() => host.Handler.Outstanding == 1, "the handler admits the forward while the response is held");

        post.IsCompleted.Should().BeFalse("the response is held at OnStarting");
        await Task.Delay(100);
        host.Sink.Entered.Should().Be(0, "the sink must not run before the 202 is committed (FR-ROUTE-11)");
        host.Handler.Outstanding.Should().Be(1);

        gate.SetResult();
        var response = await post.WaitAsync(Timeout);
        response.Status.Should().Be(HttpStatusCode.Accepted);
        response.Body.Should().BeEmpty();

        var results = await host.Sink.WaitForCountAsync(1, Timeout);
        await host.Handler.FlushAsync(Timeout);
        host.Sink.Entered.Should().Be(1, "exactly one sink call, after the 202");
        results.Should().ContainSingle().Which.NextHop.Should().Be(host.Carol.Did);
        var onward = await host.Carol.Client.UnpackAsync(Encoding.UTF8.GetString(results[0].OnwardPacked));
        onward.Message.Id.Should().Be(inner.Id);
        onward.SenderKid.Should().StartWith(host.Alice.Did + "#");
        host.Handler.Outstanding.Should().Be(0);
        host.Logs.Entries.Should().NotContain(e => e.Contains(forward.Id) && e.Contains("did not acknowledge"));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_forward_whose_request_is_aborted_before_the_202_is_committed_never_reaches_the_sink()
    {
        // As in MediatorEndpointRelayTests: TestServer cannot drop a connection while a response is
        // pending, so a middleware aborts the request after the endpoint returned and before the 202
        // is committed — the RequestAborted signal Kestrel raises for a dropped connection.
        await using var host = await MediatorEndpointHost.StartAsync();
        await host.EnrolAsync(host.Carol);
        var (packed, forward, _) = await host.ForwardFromAliceAsync(host.Carol);
        host.Controls.AbortNextPost = true;

        var act = async () => await host.PostAsync(packed);
        await act.Should().ThrowAsync<Exception>("the client never receives a response for an aborted request");

        await host.WaitUntilAsync(() => host.Handler.Outstanding == 0, "the abort cancels the acknowledgement and releases the reservation");
        await host.Handler.FlushAsync(Timeout);
        await Task.Delay(100);
        host.Sink.Entered.Should().Be(0, "a forward whose sender never received the 202 is not relayed");
        host.Logs.Entries.Should().Contain(e => e.Contains(forward.Id) && e.Contains("did not acknowledge"));
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- WebSocket: pickup works although the host's own switch is off ------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pickup_and_live_delivery_work_over_the_mediator_WebSocket_with_same_socket_replies_off_globally(bool globalStomp)
    {
        await using var host = await MediatorEndpointHost.StartAsync(o => o.UseStomp = globalStomp);
        host.GlobalReceiveOptions.AllowSameSocketReplies.Should().BeFalse();
        await host.EnrolAsync(host.Carol);
        var queued = await host.QueueForAsync(host.Carol, PickupDeliveryOutcome.Queued);

        using var socket = await host.ConnectAsync();
        var statusRequest = MessagePickup.CreateStatusRequest(host.Carol.Did, host.ServerDid);
        var status = (await host.AskOverSocketAsync(socket, host.Carol, statusRequest)).Status(statusRequest.Id);
        status.MessageCount.Should().Be(1, "the status-request with return_route: all is answered on the socket");
        status.LiveDelivery.Should().BeFalse();

        var deliveryRequest = MessagePickup.CreateDeliveryRequest(host.Carol.Did, host.ServerDid, 10);
        var delivered = (await host.AskOverSocketAsync(socket, host.Carol, deliveryRequest)).Delivery(deliveryRequest.Id);
        delivered.Should().ContainSingle().Which.Payload.ToArray().Should().Equal(queued);

        var live = MessagePickup.CreateLiveDeliveryChange(host.Carol.Did, host.ServerDid, true);
        (await host.AskOverSocketAsync(socket, host.Carol, live)).Status(live.Id).LiveDelivery.Should().BeTrue();

        // A new arrival is pushed on the socket as a live delivery (no thread).
        var pushedPayload = await host.QueueForAsync(host.Carol, PickupDeliveryOutcome.PushedLive);
        var push = await host.ReceiveOverSocketAsync(socket, host.Carol);
        var pushed = push.Delivery(thid: null).Should().ContainSingle().Subject;
        pushed.Payload.ToArray().Should().Equal(pushedPayload);

        var ack = MessagePickup.CreateMessagesReceived(host.Carol.Did, host.ServerDid, new[] { delivered[0].Id, pushed.Id });
        (await host.AskOverSocketAsync(socket, host.Carol, ack)).Status(ack.Id).MessageCount.Should().Be(0);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);

        host.GlobalReceiveOptions.AllowSameSocketReplies.Should().BeFalse("the mediator endpoint never changes the host's switch");
        host.GlobalReceiveOptions.UseStomp.Should().Be(globalStomp);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_admission_runs_on_the_mediator_WebSocket_a_repeated_status_request_is_dropped()
    {
        await using var host = await MediatorEndpointHost.StartAsync();
        await host.EnrolAsync(host.Carol);
        var first = host.Prepare(MessagePickup.CreateStatusRequest(host.Carol.Did, host.ServerDid));
        var packed = await host.AuthcryptAsync(host.Carol, first);
        var control = MessagePickup.CreateStatusRequest(host.Carol.Did, host.ServerDid);

        using var socket = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, packed);
        await ReturnRouteTestHost.SendAsync(socket, packed);
        (await host.ReceiveOverSocketAsync(socket, host.Carol)).Status(first.Id);
        (await host.AskOverSocketAsync(socket, host.Carol, control)).Status(control.Id);

        host.Logs.Entries.Should().Contain(e => e.Contains("dropped: replay admission") && e.Contains(first.Id) && e.Contains(nameof(ReplayAdmission.Duplicate)));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ---- the invitation -----------------------------------------------------------------------

    [Fact]
    public async Task The_invitation_endpoint_serves_the_holders_invitation_for_its_id_only()
    {
        await using var host = await MediatorEndpointHost.StartAsync();
        var oob = "/didcomm/oob";

        var served = await host.GetAsync($"{oob}?_oobid=mediator");
        served.Status.Should().Be(HttpStatusCode.OK);
        served.Body.Should().Be(host.InvitationJson);

        (await host.GetAsync($"{oob}?_oobid=other")).Status.Should().Be(HttpStatusCode.NotFound);
        (await host.GetAsync($"{oob}?_oobid=Mediator")).Status.Should().Be(HttpStatusCode.NotFound, "the id is compared exactly");
        (await host.GetAsync(oob)).Status.Should().Be(HttpStatusCode.BadRequest, "no _oobid");
        (await host.GetAsync($"{oob}?_oobid=")).Status.Should().Be(HttpStatusCode.BadRequest);

        // Before the mediator has built its invitation (or outside a generic host): 404.
        host.Invitation.PlaintextJson = null;
        (await host.GetAsync($"{oob}?_oobid=mediator")).Status.Should().Be(HttpStatusCode.NotFound);
        host.Invitation.PlaintextJson = string.Empty;
        (await host.GetAsync($"{oob}?_oobid=mediator")).Status.Should().Be(HttpStatusCode.NotFound);

        // The holder is read on every request.
        host.Invitation.PlaintextJson = host.InvitationJson;
        (await host.GetAsync($"{oob}?_oobid=mediator")).Body.Should().Be(host.InvitationJson);
    }

    [Fact]
    public void The_invitation_store_is_read_only()
    {
        var holder = new MediatorInvitationHolder { PlaintextJson = "{}" };
        var store = new MediatorInvitationStore(holder);

        store.Retrieve(MediatorEndpointRouteBuilderExtensions.InvitationOobId).Should().Be("{}");
        store.Retrieve(null!).Should().BeNull();
        store.Retrieve(string.Empty).Should().BeNull();
        Action write = () => store.Store(MediatorEndpointRouteBuilderExtensions.InvitationOobId, "{\"x\":1}");
        Action remove = () => store.Remove(MediatorEndpointRouteBuilderExtensions.InvitationOobId);
        write.Should().Throw<NotSupportedException>();
        remove.Should().Throw<NotSupportedException>();
        holder.PlaintextJson.Should().Be("{}");
    }

    // ---- conventions ----------------------------------------------------------------------------

    [Fact]
    public async Task Conventions_on_the_returned_builder_reach_all_three_endpoints()
    {
        var probe = new ConventionProbe();
        var finalProbe = new ConventionProbe();
        await using var host = await MediatorEndpointHost.StartAsync(o => o.Conventions = mapped =>
        {
            mapped.WithMetadata(probe).RequireHost("mediator.example");
            mapped.Finally(b => b.Metadata.Add(finalProbe));
        });

        var mediatorEndpoints = EndpointsOf(host.App).OfType<RouteEndpoint>().ToList();
        mediatorEndpoints.Select(Describe).Should().BeEquivalentTo("POST /didcomm", "GET /didcomm", "GET /didcomm/oob");
        mediatorEndpoints.Should().AllSatisfy(e =>
        {
            e.Metadata.GetOrderedMetadata<ConventionProbe>().Should().Contain(probe).And.Contain(finalProbe);
            e.Metadata.GetMetadata<IHostMetadata>()!.Hosts.Should().Equal("mediator.example");
        });

        // And the host filter is in force on each of them.
        (await host.GetAsync("/didcomm/oob?_oobid=mediator")).Status.Should().Be(HttpStatusCode.NotFound, "localhost is not the mediator's host");
        (await host.GetAsync("http://mediator.example/didcomm/oob?_oobid=mediator")).Status.Should().Be(HttpStatusCode.OK);
        (await host.PostAsync("{}")).Status.Should().Be(HttpStatusCode.NotFound);
        (await host.PostAsync("{}", "http://mediator.example/didcomm")).Status.Should().Be(HttpStatusCode.BadRequest, "the HTTP endpoint is reached on the mediator's host");
        (await host.GetAsync("/didcomm")).Status.Should().Be(HttpStatusCode.NotFound);
        (await host.GetAsync("http://mediator.example/didcomm")).Status.Should().Be(HttpStatusCode.BadRequest, "the WebSocket endpoint is reached on the mediator's host");
    }

    [Fact]
    public async Task RequireRateLimiting_as_documented_limits_all_three_endpoints()
    {
        // One fixed-window policy with one permit: whichever request comes first takes it, and every
        // later request to any of the three endpoints is refused by the limiter — so each carries it.
        await using var host = await MediatorEndpointHost.StartAsync(o =>
        {
            o.ConfigureBuilder = builder => builder.Services.AddRateLimiter(r =>
            {
                r.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                r.AddFixedWindowLimiter("didcomm", w =>
                {
                    w.PermitLimit = 1;
                    w.Window = TimeSpan.FromHours(1);
                    w.QueueLimit = 0;
                });
            });
            o.ConfigurePipeline = app => app.UseRateLimiter();
            o.Conventions = mapped => mapped.RequireRateLimiting("didcomm");
        });

        (await host.GetAsync("/didcomm/oob?_oobid=mediator")).Status.Should().Be(HttpStatusCode.OK, "the one permit");
        (await host.GetAsync("/didcomm/oob?_oobid=mediator")).Status.Should().Be(HttpStatusCode.TooManyRequests);
        (await host.PostAsync("{}")).Status.Should().Be(HttpStatusCode.TooManyRequests);
        (await host.GetAsync("/didcomm")).Status.Should().Be(HttpStatusCode.TooManyRequests);
    }

    // ---- harness ----------------------------------------------------------------------------

    private sealed class ConventionProbe
    {
    }

    private static string Describe(RouteEndpoint endpoint)
        => $"{endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single()} /{endpoint.RoutePattern.RawText!.TrimStart('/')}";

    private static IReadOnlyList<Endpoint> EndpointsOf(IEndpointRouteBuilder app)
        => app.DataSources.SelectMany(d => d.Endpoints).ToList();

    /// <summary>An unstarted app with the DIDComm container and, when asked, the mediator's two internal singletons.</summary>
    private static Task<WebApplication> BuildBareAppAsync(bool registerMediator)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
        });
        if (registerMediator)
        {
            builder.Services.AddSingleton<MediatorMarker>();
            builder.Services.AddSingleton(new MediatorInvitationHolder());
        }
        return Task.FromResult(builder.Build());
    }

    /// <summary>Knobs of <see cref="MediatorEndpointHost.StartAsync"/>.</summary>
    private sealed class MediatorEndpointHostOptions
    {
        public string Pattern { get; set; } = "/didcomm";

        /// <summary>The host's global STOMP switch (same-socket replies stay off globally).</summary>
        public bool UseStomp { get; set; }

        /// <summary>Conventions applied to the builder <c>MapDidCommMediator</c> returns.</summary>
        public Action<IEndpointConventionBuilder>? Conventions { get; set; }

        public Action<WebApplicationBuilder>? ConfigureBuilder { get; set; }

        /// <summary>Runs after <c>UseWebSockets()</c>, before the mediator is mapped.</summary>
        public Action<WebApplication>? ConfigurePipeline { get; set; }
    }

    /// <summary>Test switches the request pipeline reads on every request.</summary>
    private sealed class HostControls
    {
        private TaskCompletionSource? _responseGate;
        private int _abortNextPost;

        /// <summary>When set, every POST's response is held at <c>OnStarting</c> until it completes.</summary>
        public TaskCompletionSource? ResponseGate
        {
            get => Volatile.Read(ref _responseGate);
            set => Volatile.Write(ref _responseGate, value);
        }

        /// <summary>When set, the next POST is aborted after the endpoint returned and before its response is committed.</summary>
        public bool AbortNextPost
        {
            set => Volatile.Write(ref _abortNextPost, value ? 1 : 0);
        }

        public bool TakeAbort() => Interlocked.Exchange(ref _abortNextPost, 0) == 1;
    }

    /// <summary>
    /// A TestServer mediator: did:peer identities (the server is the mediator, Carol the recipient,
    /// Alice a sender), the hand-composed mediator container, the global receive options with
    /// same-socket replies off, a recording forward sink, an injected clock and a capturing logger,
    /// and <c>MapDidCommMediator(pattern)</c>.
    /// </summary>
    private sealed class MediatorEndpointHost : IAsyncDisposable
    {
        private MediatorEndpointHost(WebApplication app, MediatorEndpointHostOptions options, HostControls controls, TestClock clock, CapturingLoggerProvider logs,
            RecordingForwardSink sink, PeerIdentityKids server, TestPeer alice, TestPeer carol, TestPeer mallory, string invitationJson)
        {
            App = app;
            Options = options;
            Controls = controls;
            Clock = clock;
            Logs = logs;
            Sink = sink;
            ServerDid = server.Did;
            ServerKids = server.Kids;
            Alice = alice;
            Carol = carol;
            Mallory = mallory;
            InvitationJson = invitationJson;
        }

        public WebApplication App { get; }

        public MediatorEndpointHostOptions Options { get; }

        public HostControls Controls { get; }

        public TestClock Clock { get; }

        public CapturingLoggerProvider Logs { get; }

        public RecordingForwardSink Sink { get; }

        public string ServerDid { get; }

        public IReadOnlyList<string> ServerKids { get; }

        public TestPeer Alice { get; }

        public TestPeer Carol { get; }

        public TestPeer Mallory { get; }

        /// <summary>The plaintext invitation the holder was given at start.</summary>
        public string InvitationJson { get; }

        public TestServer Server => App.GetTestServer();

        /// <summary>The server's base address without a trailing <c>/</c> (the <c>publicBase</c> of the docs).</summary>
        public string PublicBase => Server.BaseAddress.GetLeftPart(UriPartial.Authority);

        public MediatorInvitationHolder Invitation => App.Services.GetRequiredService<MediatorInvitationHolder>();

        public ForwardHandler Handler => App.Services.GetRequiredService<ForwardHandler>();

        public MessagePickupQueue Queue => App.Services.GetRequiredService<MessagePickupQueue>();

        public DidCommReceiveOptions GlobalReceiveOptions => App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DidCommReceiveOptions>>().Value;

        public static async Task<MediatorEndpointHost> StartAsync(Action<MediatorEndpointHostOptions>? configure = null)
        {
            var options = new MediatorEndpointHostOptions();
            configure?.Invoke(options);

            var serverSecrets = new InMemorySecretsResolver();
            var (server, alice, carol, mallory) = await ReturnRouteTestHost.CreateIdentitiesAsync(serverSecrets);
            var invitation = OutOfBandApi.CreateInvitation(from: server.Did, goal: "RequestMediate", goalCode: "request-mediate", accept: new[] { "didcomm/v2" });
            var invitationJson = await alice.Client.PackPlaintextAsync(invitation.Message);

            var clock = new TestClock(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
            var logs = new CapturingLoggerProvider();
            var sink = new RecordingForwardSink();
            var controls = new HostControls();

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddProvider(logs);
            // Host services before AddDidComm: the recording sink wins over the TryAdd default.
            builder.Services.AddSingleton<IForwardSink>(sink);
            builder.Services.AddDidComm(b =>
            {
                b.UseNetDidResolver();
                b.UseSecretsResolver(serverSecrets);
                b.AddBuiltInProtocols();
                // What AddMediator() composes (design §7): Coordinate Mediation, then the routing
                // mediator (that order), then Message Pickup.
                b.AddCoordinateMediation(o => o.RoutingDids.Add(server.Did));
                b.AddRoutingMediator();
                b.AddMessagePickup();
                b.Configure(o =>
                {
                    o.Clock = () => clock.Now;
                    o.MaxReceiveBytes = 1024 * 1024;
                });
            });
            builder.Services.AddOptions<DidCommReceiveOptions>().Configure(o =>
            {
                o.AllowSameSocketReplies = false;
                o.UseStomp = options.UseStomp;
            });
            builder.Services.AddSingleton<MediatorMarker>();
            builder.Services.AddSingleton(new MediatorInvitationHolder { PlaintextJson = invitationJson });
            options.ConfigureBuilder?.Invoke(builder);

            var app = builder.Build();
            app.Use(async (HttpContext context, RequestDelegate next) =>
            {
                var post = HttpMethods.IsPost(context.Request.Method);
                if (post && controls.ResponseGate is { } gate)
                    context.Response.OnStarting(static state => ((TaskCompletionSource)state!).Task, gate);
                await next(context);
                if (post && controls.TakeAbort())
                    context.Abort();
            });
            app.UseWebSockets();
            options.ConfigurePipeline?.Invoke(app);
            var mapped = app.MapDidCommMediator(options.Pattern);
            options.Conventions?.Invoke(mapped);
            await app.StartAsync();

            app.Services.GetRequiredService<IForwardSink>().Should().BeSameAs(sink);
            return new MediatorEndpointHost(app, options, controls, clock, logs, sink,
                new PeerIdentityKids(server.Did, ReturnRouteTestHost.KidsOf(server)), alice, carol, mallory, invitationJson);
        }

        // ---- requests ---------------------------------------------------------------------

        /// <summary>What <c>SendAsync</c> does for a mediator request (FR-RR-09): <c>created_time</c> from the server's clock and <c>return_route: all</c>.</summary>
        public Message Prepare(Message message)
        {
            message.CreatedTime = Clock.Now.ToUnixTimeSeconds();
            ReturnRouteHeaderWriter.Write(message, ReturnRouteMode.All, thread: null);
            return message;
        }

        public async Task<string> AuthcryptAsync(TestPeer from, Message message)
            => (await from.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
                Recipients: new[] { ServerDid },
                From: from.Did,
                Forward: false))).Message;

        /// <summary>POST a packed envelope to <paramref name="path"/> (the pattern by default, or an absolute URL).</summary>
        public async Task<(HttpStatusCode Status, string? ContentType, string Body)> PostAsync(string packed, string? path = null)
        {
            using var client = Server.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, path ?? Options.Pattern)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(packed)),
            };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.Encrypted);
            using var response = await client.SendAsync(request);
            return (response.StatusCode, response.Content.Headers.ContentType?.ToString(), await response.Content.ReadAsStringAsync());
        }

        /// <summary>GET <paramref name="pathOrUrl"/> (relative to the server, or an absolute URL whose host the TestServer accepts as is).</summary>
        public async Task<(HttpStatusCode Status, string? ContentType, string Body)> GetAsync(string pathOrUrl)
        {
            using var client = Server.CreateClient();
            using var response = await client.GetAsync(pathOrUrl);
            return (response.StatusCode, response.Content.Headers.ContentType?.ToString(), await response.Content.ReadAsStringAsync());
        }

        /// <summary>Authcrypt <paramref name="request"/> (prepared here) from <paramref name="from"/>, POST it and read the mediator's answer from the 200 body.</summary>
        public async Task<PickupAnswer> AskOverHttpAsync(TestPeer from, Message request, string? path = null)
        {
            var response = await PostAsync(await AuthcryptAsync(from, Prepare(request)), path);
            response.Status.Should().Be(HttpStatusCode.OK);
            response.ContentType.Should().Be(MediaTypes.Encrypted);
            return await UnpackAnswerAsync(from, Encoding.UTF8.GetBytes(response.Body));
        }

        /// <summary>Enrol <paramref name="peer"/> over the HTTP endpoint: <c>mediate-request</c> → grant, then <c>recipient-update</c> adding its own DID.</summary>
        public async Task EnrolAsync(TestPeer peer, string? path = null)
        {
            var granted = await AskOverHttpAsync(peer, CoordinateMediation.CreateMediateRequest(peer.Did, ServerDid), path);
            CoordinateMediation.TryParseGrant(granted.Message, out var routingDids).Should().BeTrue();
            routingDids.Should().Equal(ServerDid);

            var updated = await AskOverHttpAsync(peer, CoordinateMediation.CreateRecipientUpdate(peer.Did, ServerDid,
                new[] { new RecipientUpdate(peer.Did, RecipientUpdateAction.Add) }), path);
            CoordinateMediation.TryParseRecipientUpdateResponse(updated.Message, out var results).Should().BeTrue();
            results!.Should().OnlyContain(r => r.Result == RecipientUpdateOutcome.Success);
        }

        public Task<WebSocket> ConnectAsync(string? path = null)
            => Server.CreateWebSocketClient().ConnectAsync(new UriBuilder(Server.BaseAddress) { Scheme = "ws", Path = path ?? Options.Pattern }.Uri, default);

        /// <summary>Authcrypt <paramref name="request"/> (prepared here) on <paramref name="socket"/> and return the next frame, the mediator's answer.</summary>
        public async Task<PickupAnswer> AskOverSocketAsync(WebSocket socket, TestPeer from, Message request)
        {
            await ReturnRouteTestHost.SendAsync(socket, await AuthcryptAsync(from, Prepare(request)));
            return await ReceiveOverSocketAsync(socket, from);
        }

        public async Task<PickupAnswer> ReceiveOverSocketAsync(WebSocket socket, TestPeer peer)
        {
            var (type, text) = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
            type.Should().Be(WebSocketMessageType.Binary, "each message of the mediator is one binary WebSocket message, never a STOMP frame");
            return await UnpackAnswerAsync(peer, Encoding.UTF8.GetBytes(text));
        }

        private async Task<PickupAnswer> UnpackAnswerAsync(TestPeer peer, byte[] packed)
        {
            var unpacked = await peer.Client.UnpackAsync(Encoding.UTF8.GetString(packed));
            unpacked.Encrypted.Should().BeTrue();
            unpacked.Authenticated.Should().BeTrue();
            unpacked.SenderKid.Should().StartWith(ServerDid + "#");
            unpacked.Message.From.Should().Be(ServerDid);
            unpacked.Message.To.Should().Equal(peer.Did);
            return new PickupAnswer(unpacked.Message, packed);
        }

        // ---- payloads ---------------------------------------------------------------------

        private async Task<(byte[] Packed, Message Inner)> PackFromAliceAsync(TestPeer to)
        {
            var inner = new MessageBuilder()
                .WithType("https://didcomm.org/basicmessage/2.0/message")
                .WithFrom(Alice.Did)
                .WithTo(to.Did)
                .WithBody(new JsonObject { ["content"] = "hello" })
                .Build();
            var packed = await Alice.Client.PackEncryptedAsync(inner, new PackEncryptedOptions(
                Recipients: new[] { to.Did },
                From: Alice.Did,
                Forward: false));
            return (Encoding.UTF8.GetBytes(packed.Message), inner);
        }

        /// <summary>A message from Alice for <paramref name="to"/> handed straight to the queue (the forward → queue sink is engineer B's); returns its packed bytes.</summary>
        public async Task<byte[]> QueueForAsync(TestPeer to, PickupDeliveryOutcome expected)
        {
            var (packed, _) = await PackFromAliceAsync(to);
            (await Queue.DeliverAsync(to.Did, packed)).Outcome.Should().Be(expected);
            return packed;
        }

        /// <summary>Alice's message for <paramref name="to"/> wrapped in a <c>forward</c> anoncrypted to the mediator.</summary>
        public async Task<(string Packed, Message Forward, Message Inner)> ForwardFromAliceAsync(TestPeer to)
        {
            var (inner, innerMessage) = await PackFromAliceAsync(to);
            var forward = ForwardMessage.Create(ServerDid, to.Did, new[] { Encoding.UTF8.GetString(inner) });
            var packed = await Alice.Client.PackEncryptedAsync(forward, new PackEncryptedOptions(
                Recipients: new[] { ServerDid },
                From: null,
                Forward: false));
            return (packed.Message, forward, innerMessage);
        }

        // ---- checks -----------------------------------------------------------------------

        public async Task WaitUntilAsync(Func<bool> condition, string because)
        {
            (await ReturnRouteTestHost.WaitUntilAsync(condition, Timeout)).Should().BeTrue(because);
        }

        /// <summary>Log lines that carry any DID or key id of the cast (must be empty).</summary>
        public IReadOnlyList<string> LeakingLogLines()
        {
            var sensitive = new[] { ServerDid, Alice.Did, Carol.Did, Mallory.Did }
                .Concat(ServerKids).Concat(Alice.Kids).Concat(Carol.Kids).Concat(Mallory.Kids)
                .ToArray();
            return Logs.Entries.Where(line => sensitive.Any(v => line.Contains(v, StringComparison.Ordinal))).ToList();
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            await Alice.DisposeAsync();
            await Carol.DisposeAsync();
            await Mallory.DisposeAsync();
        }
    }

    private sealed record PeerIdentityKids(string Did, IReadOnlyList<string> Kids);

    /// <summary>A host <see cref="IForwardSink"/> that records what reaches it (counted on entry).</summary>
    private sealed class RecordingForwardSink : IForwardSink
    {
        private readonly ConcurrentQueue<ForwardProcessingResult> _results = new();
        private int _entered;

        public int Entered => Volatile.Read(ref _entered);

        public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _entered);
            _results.Enqueue(result);
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<ForwardProcessingResult>> WaitForCountAsync(int count, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            while (_results.Count < count)
            {
                if (watch.Elapsed > timeout)
                    throw new TimeoutException($"The sink received {_results.Count} result(s); expected {count} within {timeout}.");
                await Task.Delay(10);
            }
            return _results.ToList();
        }
    }
}
