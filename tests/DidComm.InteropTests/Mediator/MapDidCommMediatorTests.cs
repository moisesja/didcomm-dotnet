using System.Net;
using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator;
using DidComm.Mediator.AspNetCore;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
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
/// #95 D2 (design §8, JC-10): <c>MapDidCommMediator(pattern)</c> over a mediator registered with
/// <c>AddMediator()</c> maps <c>POST {pattern}</c> (the registry-aware HTTP endpoint, unchanged),
/// <c>GET {pattern}</c> (the WebSocket core with same-socket replies on and STOMP off for itself only)
/// and <c>GET {base}/oob</c> (the mediator's out-of-band invitation under
/// <see cref="MediatorEndpointRouteBuilderExtensions.InvitationOobId"/>), refuses to map without
/// <c>AddMediator()</c>, and applies conventions to all three endpoints. The host's global
/// <see cref="DidCommReceiveOptions"/> keep same-socket replies off (and, where it matters, STOMP
/// on), so every WebSocket exchange proves the endpoint's own override. The acknowledgement
/// ordering of the HTTP endpoint is in <see cref="MediatorAcknowledgementTests"/>.
/// </summary>
public sealed class MapDidCommMediatorTests
{
    // ---- map time -----------------------------------------------------------------------------

    [Fact]
    public async Task Mapping_without_AddMediator_is_refused_at_map_time_and_maps_nothing()
    {
        await using var app = BuildBareApp(addMediator: false);

        var act = () => app.MapDidCommMediator("/didcomm");

        act.Should().Throw<InvalidOperationException>().WithMessage("MapDidCommMediator requires AddMediator()*");
        EndpointsOf(app).Should().BeEmpty("nothing is mapped when the mediator is not registered");
    }

    [Fact]
    public async Task The_pattern_is_validated_before_anything_is_mapped_and_the_refusal_never_quotes_it()
    {
        await using var app = BuildBareApp(addMediator: true);
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
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Pattern = "/didcomm");

        var lines = mediator.Logs.Records.Where(r => r.Text.StartsWith("Mediator endpoints mapped", StringComparison.Ordinal)).ToList();
        lines.Should().ContainSingle();
        lines[0].Level.Should().Be(LogLevel.Information);
        lines[0].Text.Should().Contain("POST /didcomm").And.Contain("GET /didcomm").And.Contain("GET /didcomm/oob?_oobid=mediator");
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    // ---- routes -------------------------------------------------------------------------------

    [Theory]
    [InlineData("/")]
    [InlineData("/didcomm")]
    [InlineData("/didcomm/")]
    [InlineData("/tenants/{tenant}/didcomm")]
    public async Task Each_pattern_routes_HTTP_WebSocket_and_the_invitation_to_the_right_endpoint(string pattern)
    {
        var path = pattern.Replace("{tenant}", "acme", StringComparison.Ordinal);
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Pattern = pattern;
            o.ServicePath = path;
        });
        var basePath = path.TrimEnd('/');
        var carol = await mediator.NewPeerAsync();

        // POST: Coordinate Mediation over the registry-aware HTTP endpoint (the grant is the 200 body).
        (await mediator.RequestMediationAsync(carol, path)).Should().Equal(mediator.Did);
        await mediator.AddRecipientsAsync(carol, new[] { carol.Did }, path);

        // GET with an upgrade: the WebSocket endpoint answers a status-request on the socket.
        using (var socket = await mediator.ConnectAsync(path))
        {
            var request = MessagePickup.CreateStatusRequest(carol.Did, mediator.Did);
            (await mediator.AskOverSocketAsync(socket, carol, request)).Status(request.Id).MessageCount.Should().Be(0);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        }

        // GET without an upgrade reaches the WebSocket endpoint too (400), not a 404 or 405.
        (await mediator.GetAsync(path)).Status.Should().Be(HttpStatusCode.BadRequest);

        // GET {base}/oob: the URL a host publishes, built exactly as the XML docs say.
        var url = OutOfBandApi.ToShortUrl($"{mediator.PublicBase}{basePath}/oob", MediatorEndpointRouteBuilderExtensions.InvitationOobId);
        var invitation = await mediator.GetAsync(url);
        invitation.Status.Should().Be(HttpStatusCode.OK);
        invitation.ContentType.Should().StartWith(MediaTypes.Plaintext);
        invitation.Body.Should().Be(mediator.Invitation.PlaintextJson);
        var parsed = OutOfBandApi.FromPlaintext(invitation.Body);
        parsed.Message.From.Should().Be(mediator.Did);
        parsed.Message.Body!["goal_code"]!.GetValue<string>().Should().Be("request-mediate");

        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_pattern_with_a_trailing_slash_also_answers_without_it()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Pattern = "/didcomm/");
        var carol = await mediator.NewPeerAsync();

        (await mediator.RequestMediationAsync(carol, "/didcomm")).Should().Equal(mediator.Did);
        using var socket = await mediator.ConnectAsync("/didcomm");
        var request = MessagePickup.CreateStatusRequest(carol.Did, mediator.Did);
        (await mediator.AskOverSocketAsync(socket, carol, request)).Status(request.Id).MessageCount.Should().Be(0);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ---- WebSocket: pickup works although the host's own switch is off ------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pickup_and_live_delivery_work_over_the_mediator_WebSocket_with_same_socket_replies_off_globally(bool globalStomp)
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.GlobalStomp = globalStomp);
        mediator.GlobalReceiveOptions.AllowSameSocketReplies.Should().BeFalse();
        var carol = await mediator.NewPeerAsync();
        var alice = await mediator.NewSenderAsync();
        await mediator.EnrolAsync(carol);
        var queued = await QueueAsync(mediator, alice, carol, PickupDeliveryOutcome.Queued);

        using var socket = await mediator.ConnectAsync();
        var statusRequest = MessagePickup.CreateStatusRequest(carol.Did, mediator.Did);
        var status = (await mediator.AskOverSocketAsync(socket, carol, statusRequest)).Status(statusRequest.Id);
        status.MessageCount.Should().Be(1, "the status-request with return_route: all is answered on the socket");
        status.LiveDelivery.Should().BeFalse();

        var deliveryRequest = MessagePickup.CreateDeliveryRequest(carol.Did, mediator.Did, 10);
        var delivered = (await mediator.AskOverSocketAsync(socket, carol, deliveryRequest)).Delivery(deliveryRequest.Id);
        delivered.Should().ContainSingle().Which.Payload.ToArray().Should().Equal(queued);

        var live = MessagePickup.CreateLiveDeliveryChange(carol.Did, mediator.Did, true);
        (await mediator.AskOverSocketAsync(socket, carol, live)).Status(live.Id).LiveDelivery.Should().BeTrue();

        // A new arrival is pushed on the socket as a live delivery (no thread).
        var pushedPayload = await QueueAsync(mediator, alice, carol, PickupDeliveryOutcome.PushedLive);
        var push = await mediator.ReceiveOverSocketAsync(socket, carol);
        var pushed = push.Delivery(thid: null).Should().ContainSingle().Subject;
        pushed.Payload.ToArray().Should().Equal(pushedPayload);

        var ack = MessagePickup.CreateMessagesReceived(carol.Did, mediator.Did, new[] { delivered[0].Id, pushed.Id });
        (await mediator.AskOverSocketAsync(socket, carol, ack)).Status(ack.Id).MessageCount.Should().Be(0);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);

        mediator.GlobalReceiveOptions.AllowSameSocketReplies.Should().BeFalse("the mediator endpoint never changes the host's switch");
        mediator.GlobalReceiveOptions.UseStomp.Should().Be(globalStomp);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_admission_runs_on_the_mediator_WebSocket_a_repeated_status_request_is_dropped()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var carol = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(carol);
        var first = mediator.Prepare(MessagePickup.CreateStatusRequest(carol.Did, mediator.Did));
        var packed = await mediator.AuthcryptAsync(carol, first);
        var control = MessagePickup.CreateStatusRequest(carol.Did, mediator.Did);

        using var socket = await mediator.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, packed);
        await ReturnRouteTestHost.SendAsync(socket, packed);
        (await mediator.ReceiveOverSocketAsync(socket, carol)).Status(first.Id);
        (await mediator.AskOverSocketAsync(socket, carol, control)).Status(control.Id);

        mediator.Logs.Entries.Should().Contain(e => e.Contains("dropped: replay admission") && e.Contains(first.Id) && e.Contains(nameof(ReplayAdmission.Duplicate)));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ---- the invitation -----------------------------------------------------------------------

    [Fact]
    public async Task The_invitation_endpoint_serves_the_mediators_invitation_for_its_id_only()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Pattern = "/didcomm");
        const string Oob = "/didcomm/oob";
        var built = mediator.Invitation.PlaintextJson;
        built.Should().NotBeNullOrEmpty("the mediator built its invitation when it started");

        var served = await mediator.GetAsync($"{Oob}?_oobid=mediator");
        served.Status.Should().Be(HttpStatusCode.OK);
        served.Body.Should().Be(built);

        (await mediator.GetAsync($"{Oob}?_oobid=other")).Status.Should().Be(HttpStatusCode.NotFound);
        (await mediator.GetAsync($"{Oob}?_oobid=Mediator")).Status.Should().Be(HttpStatusCode.NotFound, "the id is compared exactly");
        (await mediator.GetAsync(Oob)).Status.Should().Be(HttpStatusCode.BadRequest, "no _oobid");
        (await mediator.GetAsync($"{Oob}?_oobid=")).Status.Should().Be(HttpStatusCode.BadRequest);

        // Before the mediator has built its invitation: 404. The holder is read on every request.
        mediator.Invitation.PlaintextJson = null;
        (await mediator.GetAsync($"{Oob}?_oobid=mediator")).Status.Should().Be(HttpStatusCode.NotFound);
        mediator.Invitation.PlaintextJson = string.Empty;
        (await mediator.GetAsync($"{Oob}?_oobid=mediator")).Status.Should().Be(HttpStatusCode.NotFound);
        mediator.Invitation.PlaintextJson = built;
        (await mediator.GetAsync($"{Oob}?_oobid=mediator")).Body.Should().Be(built);
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
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Pattern = "/didcomm";
            o.Conventions = mapped =>
            {
                mapped.WithMetadata(probe).RequireHost("mediator.example");
                mapped.Finally(b => b.Metadata.Add(finalProbe));
            };
        });

        var mediatorEndpoints = EndpointsOf(mediator.App).OfType<RouteEndpoint>().ToList();
        mediatorEndpoints.Select(Describe).Should().BeEquivalentTo("POST /didcomm", "GET /didcomm", "GET /didcomm/oob");
        mediatorEndpoints.Should().AllSatisfy(e =>
        {
            e.Metadata.GetOrderedMetadata<ConventionProbe>().Should().Contain(probe).And.Contain(finalProbe);
            e.Metadata.GetMetadata<IHostMetadata>()!.Hosts.Should().Equal("mediator.example");
        });

        // And the host filter is in force on each of them.
        (await mediator.GetAsync("/didcomm/oob?_oobid=mediator")).Status.Should().Be(HttpStatusCode.NotFound, "mediator.test is not the mediator's host here");
        (await mediator.GetAsync("http://mediator.example/didcomm/oob?_oobid=mediator")).Status.Should().Be(HttpStatusCode.OK);
        (await mediator.PostAsync("{}")).Status.Should().Be(HttpStatusCode.NotFound);
        (await mediator.PostAsync("{}", "http://mediator.example/didcomm")).Status.Should().Be(HttpStatusCode.BadRequest, "the HTTP endpoint is reached on the mediator's host");
        (await mediator.GetAsync("/didcomm")).Status.Should().Be(HttpStatusCode.NotFound);
        (await mediator.GetAsync("http://mediator.example/didcomm")).Status.Should().Be(HttpStatusCode.BadRequest, "the WebSocket endpoint is reached on the mediator's host");
    }

    [Fact]
    public async Task RequireRateLimiting_as_documented_limits_all_three_endpoints()
    {
        // One fixed-window policy with one permit: whichever request comes first takes it, and every
        // later request to any of the three endpoints is refused by the limiter — so each carries it.
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Pattern = "/didcomm";
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

        (await mediator.GetAsync("/didcomm/oob?_oobid=mediator")).Status.Should().Be(HttpStatusCode.OK, "the one permit");
        (await mediator.GetAsync("/didcomm/oob?_oobid=mediator")).Status.Should().Be(HttpStatusCode.TooManyRequests);
        (await mediator.PostAsync("{}")).Status.Should().Be(HttpStatusCode.TooManyRequests);
        (await mediator.GetAsync("/didcomm")).Status.Should().Be(HttpStatusCode.TooManyRequests);
    }

    // ---- harness ----------------------------------------------------------------------------

    private sealed class ConventionProbe
    {
    }

    private static async Task<byte[]> QueueAsync(MediatorTestServer mediator, MediatorSender alice, TestPeer to, PickupDeliveryOutcome expected)
    {
        var packed = System.Text.Encoding.UTF8.GetBytes(await alice.AuthcryptAsync(alice.Note(to.Did), to.Did));
        (await mediator.Queue.DeliverAsync(to.Did, packed)).Outcome.Should().Be(expected);
        return packed;
    }

    private static string Describe(RouteEndpoint endpoint)
        => $"{endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single()} /{endpoint.RoutePattern.RawText!.TrimStart('/')}";

    private static IReadOnlyList<Endpoint> EndpointsOf(IEndpointRouteBuilder app)
        => app.DataSources.SelectMany(d => d.Endpoints).ToList();

    /// <summary>An unstarted app with the DIDComm container and, when asked, <c>AddMediator()</c> (registration only: nothing starts).</summary>
    private static WebApplication BuildBareApp(bool addMediator)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            if (addMediator)
                b.AddMediator(o => o.CoordinateMediation.RoutingDids.Add("did:example:mediator"));
        });
        return builder.Build();
    }
}
