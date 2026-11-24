using System.Globalization;
using System.Net;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.Transports.Proxy;
using DidComm.Mediator;
using DidComm.Mediator.Pipeline;
using DidComm.Protocols.Routing;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// QA of #110 (direct push): recipient endpoints that misbehave, through the real
/// <c>AddMediator(o =&gt; o.DirectPush = true)</c> + <c>MapDidCommMediator</c> pipeline, with a second
/// recipient pushed meanwhile and the host's own HTTP sends measured before and after. Per message:
/// at most <c>MaxRedirectHops</c> + 1 POSTs, never a retry, the attempt spent (no push takes it
/// again), the message left for pickup, and no endpoint or <c>Location</c> in any line the library
/// writes. The host's transport retries twice and its breaker opens after two failures, so a push
/// that leaked into it would show.
/// </summary>
public sealed class DirectPushEndpointsQaTests
{
    private const string Site = "m1.test";
    private const string Elsewhere = "203.0.113.50";
    private const string Landing = "m2.test";
    private static readonly int MaxHops = new HttpTransportOptions().MaxRedirectHops;

    public static TheoryData<string, int, bool> Misbehaving => new()
    {
        // The endpoint's path, POSTs per message, delivered.
        { "/status/400", 1, false },
        { "/status/404", 1, false },
        { "/status/410", 1, false },
        { "/status/408", 1, false },
        { "/status/429", 1, false },
        { "/status/429-retry", 1, false },
        { "/status/500", 1, false },
        { "/status/503", 1, false },
        { "/status/503-retry", 1, false },
        { "/redirect/301", 1, false },
        { "/redirect/302", 1, false },
        { "/redirect/303", 1, false },
        { "/redirect/308", 1, false },
        { $"/chain/{MaxHops - 1}", MaxHops, true },
        { $"/chain/{MaxHops}", MaxHops + 1, true },
        { $"/chain/{MaxHops + 1}", MaxHops + 1, false },
        { "/loop", MaxHops + 1, false },
        { "/relative", 2, true },
        { "/no-location", 1, false },
        { "/to-ftp", 1, false },
        { "/to-ws", 1, false },
        { "/to-queue", 1, false },
        { "/reset", 1, false },
        { "/endless-202", 1, true },
        { "/endless-503", 1, false },
    };

    [Theory]
    [MemberData(nameof(Misbehaving))]
    public async Task A_misbehaving_endpoint_gets_its_bounded_POSTs_once_per_message_and_never_stops_other_pushes_or_the_hosts_own_sends(string path, int postsPerMessage, bool delivered)
    {
        var network = new MediatorNetwork();
        await using var site = await ScriptedSite.StartAsync(network, Site);
        await using var elsewhere = await ScriptedSite.StartAsync(network, Elsewhere);
        await using var landing = await ScriptedSite.StartAsync(network, Landing);
        site.Respond = Script;
        elsewhere.Respond = Script;
        await using var world = await DirectPushWorld.StartAsync(o => o.PostConfigureServices = s => s.Configure<HttpTransportOptions>(h =>
        {
            h.MaxRetryAttempts = 2;
            h.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
            h.CircuitBreakerFailureThreshold = 2;
        }), network: network);
        var (bob, b, _) = await world.EnrolAsync("bob");
        world.Endpoints.Set(bob.Did, site.At(path));
        var (_, c, carolEndpoint) = await world.EnrolAsync("carol");
        var alice = await world.Mediator.NewSenderAsync();

        // No send of the host's own before the pushes: its breaker opens only when every call in its
        // sampling window failed, so an earlier success would hide pushes that leaked into it.
        for (var i = 0; i < 3; i++)
            await Qa.SendNoteAsync(world.Mediator, alice, b);
        await Qa.SendNoteAsync(world.Mediator, alice, c);
        var bobOutcome = delivered ? PushOutcomes.PushedDirect : PushOutcomes.Failed;
        // A 4xx other than 408 and 429 is the endpoint's definite "not this message": the next one
        // goes on at once (ADV-110-1a-4). Any other failure ends the run and pauses Bob's direct pushes.
        var refused = path.StartsWith("/status/4", StringComparison.Ordinal) && path is not ("/status/408" or "/status/429" or "/status/429-retry");
        await world.OutcomesAsync(PushOutcomes.PushedDirect, delivered ? 4 : 1);
        await world.OutcomesAsync(bobOutcome, delivered ? 4 : refused ? 3 : 1);
        carolEndpoint.Posts.Should().Be(1, "the second recipient is pushed meanwhile");
        // A paused recipient: one message per pause, each with its bounded POSTs; the wake-ups during
        // the pause post nothing.
        var pause = 2 * new MediatorOptions().PushTimeout;
        for (var attempted = 1; !delivered && !refused && attempted < 3; attempted++)
        {
            BobPosts().Should().Be(attempted * postsPerMessage, "nothing is posted during the pause");
            world.Time.Advance(pause);
            world.Pushes.Wake(bob.Did).Should().BeTrue();
            await world.OutcomesAsync(PushOutcomes.Failed, attempted + 1);
        }
        BobPosts().Should().Be(3 * postsPerMessage, "{0} POST(s) per message, never retried", postsPerMessage);

        // A later push takes only the new message: every earlier attempt is spent.
        if (!delivered && !refused)
            world.Time.Advance(pause);
        await Qa.SendNoteAsync(world.Mediator, alice, b);
        await world.OutcomesAsync(bobOutcome, 4 + (delivered ? 1 : 0));
        BobPosts().Should().Be(4 * postsPerMessage);
        world.Store.DirectTakes.Should().HaveCount(5).And.OnlyHaveUniqueItems("no record is handed to a direct push twice");
        if (delivered)
            (await world.StoredAsync(bob.Did)).Should().Be(0);
        else
            (await world.Mediator.FetchAsync(bob)).Should().HaveCount(4, "every message that was not delivered waits for pickup (OD-2)");

        // After: the host's own send still goes out; no push touched its breaker.
        var after = await Record.ExceptionAsync(() => HostEgress.SendAsync(world.Mediator, landing.At("/own")));
        after.Should().BeNull("the host's own transport never saw the pushes");
        landing.Requests.Count(r => r == "POST /own").Should().Be(1);
        HostEgress.OwnLines(world.Mediator).Should().NotContain(
            line => line.Contains(Site, StringComparison.OrdinalIgnoreCase)
                    || line.Contains(Elsewhere, StringComparison.Ordinal)
                    || line.Contains(Landing, StringComparison.OrdinalIgnoreCase)
                    || line.Contains(path, StringComparison.Ordinal)
                    || line.Contains("ftp:", StringComparison.Ordinal)
                    || line.Contains("ws:", StringComparison.Ordinal),
            "no line the library writes carries an endpoint or a Location");
        world.Mediator.LeakingLogLines(b.Did, c.Did).Should().BeEmpty();

        int BobPosts() => site.Posts + elsewhere.Posts + landing.Requests.Count(r => r != "POST /own");
    }

    // The control of the theory above: in the same configuration the host's own breaker is live, and
    // the host's own 5xx failures open it for every one of its sends.
    [Fact]
    public async Task Control_the_hosts_own_breaker_opens_on_its_own_failures_in_this_configuration()
    {
        var network = new MediatorNetwork();
        await using var site = await ScriptedSite.StartAsync(network, Site);
        await using var landing = await ScriptedSite.StartAsync(network, Landing);
        site.Respond = Script;
        await using var world = await DirectPushWorld.StartAsync(o => o.PostConfigureServices = s => s.Configure<HttpTransportOptions>(h =>
        {
            h.MaxRetryAttempts = 2;
            h.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
            h.CircuitBreakerFailureThreshold = 2;
        }), network: network);

        var own = await Record.ExceptionAsync(() => HostEgress.SendAsync(world.Mediator, site.At("/status/503")));
        var blocked = await Record.ExceptionAsync(() => HostEgress.SendAsync(world.Mediator, landing.At("/own")));

        own.Should().NotBeNull();
        blocked.Should().BeOfType<BrokenCircuitException>("the host's own breaker opens on the host's own failures");
        landing.Posts.Should().Be(0);
    }

    [Fact]
    public async Task While_a_push_is_held_at_a_tarpit_the_hosts_own_sends_and_another_recipients_push_go_out()
    {
        var network = new MediatorNetwork();
        await using var landing = await ScriptedSite.StartAsync(network, Landing);
        await using var world = await DirectPushWorld.StartAsync(network: network);
        var (bob, b, bobEndpoint) = await world.EnrolAsync("bob");
        var (_, c, carolEndpoint) = await world.EnrolAsync("carol");
        var held = bobEndpoint.Hold();
        var alice = await world.Mediator.NewSenderAsync();

        await Qa.SendNoteAsync(world.Mediator, alice, b);
        await MediatorTestServer.WaitUntilAsync(() => bobEndpoint.Posts == 1, "Bob's POST is held at his endpoint");
        var during = await HostEgress.SendAsync(world.Mediator, landing.At("/own"));
        await Qa.SendNoteAsync(world.Mediator, alice, c);
        await MediatorTestServer.WaitUntilAsync(() => world.Mediator.PushOutcome(PushOutcomes.PushedDirect) == 1, "Carol's push went out while Bob's is held");

        during.Accepted.Should().BeTrue("the host's own send does not wait for a held push");
        carolEndpoint.Posts.Should().Be(1);
        bobEndpoint.Ended.Should().Be(0, "Bob's POST is still held");
        held.SetResult();
        await world.OutcomesAsync(PushOutcomes.PushedDirect, 2);
        (await HostEgress.SendAsync(world.Mediator, landing.At("/own"))).Accepted.Should().BeTrue();
        landing.Posts.Should().Be(2);
        (await world.StoredAsync(bob.Did)).Should().Be(0);
    }

    private static async Task Script(HttpContext context)
    {
        var path = context.Request.Path.Value!;
        var response = context.Response;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        switch (segments[0])
        {
            case "status":
                var status = segments[1].Split('-');
                response.StatusCode = int.Parse(status[0], CultureInfo.InvariantCulture);
                if (status.Length > 1)
                    response.Headers.RetryAfter = "0";
                return;
            case "redirect":
                response.StatusCode = int.Parse(segments[1], CultureInfo.InvariantCulture);
                response.Headers.Location = $"http://{Landing}/moved";
                return;
            case "chain":
                var n = int.Parse(segments[1], CultureInfo.InvariantCulture);
                if (n == 0)
                {
                    response.StatusCode = StatusCodes.Status202Accepted;
                    return;
                }
                // Each hop names another host, as a recipient's server could.
                response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                response.Headers.Location = $"http://{(n % 2 == 0 ? Elsewhere : Site)}/chain/{n - 1}";
                return;
            case "loop":
                response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                response.Headers.Location = "/loop";
                return;
            case "relative":
                response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                response.Headers.Location = "landed";
                return;
            case "landed":
                response.StatusCode = StatusCodes.Status202Accepted;
                return;
            case "no-location":
                response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                return;
            case "to-ftp":
                response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                response.Headers.Location = $"ftp://{Landing}/inbox";
                return;
            case "to-ws":
                response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                response.Headers.Location = $"ws://{Landing}/inbox";
                return;
            case "to-queue":
                response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                response.Headers.Location = "didcomm:transport/queue";
                return;
            case "reset":
                context.Abort();
                return;
            case "endless-202":
            case "endless-503":
                response.StatusCode = segments[0] == "endless-202" ? StatusCodes.Status202Accepted : StatusCodes.Status503ServiceUnavailable;
                await EndlessBodyAsync(context);
                return;
            default:
                response.StatusCode = StatusCodes.Status202Accepted;
                return;
        }
    }

    // A body that never ends: written until the client goes away (or 64 MiB, a hang guard).
    private static async Task EndlessBodyAsync(HttpContext context)
    {
        var chunk = new byte[16 * 1024];
        try
        {
            for (var written = 0L; written < 64L * 1024 * 1024 && !context.RequestAborted.IsCancellationRequested; written += chunk.Length)
                await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
        }
        catch (Exception) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away without reading the body.
        }
        catch (IOException)
        {
            // The client disposed the response.
        }
    }

    // ---- QA-110-1 (QA2-1 in the plan) end to end: the transport's own request timeout --------------

    // The host's transport gives up on a request after 250 ms, sooner than the push deadline (30 s on
    // the push workers' manual clock). Before the fix the stall ended "not delivered" with no pause,
    // and each queued message got a stalled POST of its own (QA2-1). Now any direct push that does not
    // deliver ends its run and pauses its recipient's direct pushes, whatever ended it: one stalled
    // POST, then the other messages wait (for the first wake-up after the pause, or pickup).
    [Fact]
    public async Task QA_110_1_A_tarpit_the_transports_own_timeout_gives_up_on_is_posted_once_and_its_recipients_direct_pushes_pause()
    {
        var network = new MediatorNetwork();
        await using var site = await ScriptedSite.StartAsync(network, Site);
        // Never answers. A client that went away gets no answer at all, as from a real server (a
        // TestServer would otherwise hand the late default 200 to a cancelled client).
        site.Respond = async context =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }
            context.Abort();
        };
        await using var world = await DirectPushWorld.StartAsync(
            o => o.PostConfigureServices = s => s.Configure<HttpTransportOptions>(h => h.RequestTimeout = TimeSpan.FromMilliseconds(250)),
            network: network);
        world.Mediator.App.Services.GetRequiredService<DirectPushSender>().TransportSettings!.RequestTimeout
            .Should().Be(TimeSpan.FromMilliseconds(250), "the push's transport keeps the host's request timeout");
        var (bob, b, _) = await world.EnrolAsync("bob");
        world.Endpoints.Set(bob.Did, site.At("/tarpit"));
        var alice = await world.Mediator.NewSenderAsync();
        // Three messages queued before the first push takes one.
        var parked = world.Endpoints.ParkNext(bob.Did);
        for (var i = 0; i < 3; i++)
            await Qa.SendNoteAsync(world.Mediator, alice, b);
        await parked.Arrived.WaitAsync(MediatorTestServer.Timeout);
        parked.Release();

        // The transport gives up after 250 ms of real time, long before the push deadline (on the push
        // workers' manual clock, never moved here): the push did not deliver, and Bob's direct pushes pause.
        await world.OutcomesAsync(PushOutcomes.Failed, 1);
        await MediatorTestServer.WaitUntilAsync(() => world.Pushes.Unsettled == 0, "the operation ended");
        await world.Mediator.PushesIdleAsync();

        // One attempt, counted where it is exact (the store's direct takes); the 250 ms are real time,
        // so on a loaded machine they may run out before the POST reaches the site.
        world.Store.DirectTakes.Should().ContainSingle("a tarpit holds a worker once per pause (residual 1), whatever ends the stall");
        site.Posts.Should().BeLessThanOrEqualTo(1);
        world.Mediator.PushOutcomes.Where(o => o != PushOutcomes.SkippedCooldown).Should().Equal(new[] { PushOutcomes.Failed }, "one push failed, and the other messages waited");
        var skipped = world.Mediator.PushOutcome(PushOutcomes.SkippedCooldown);
        await Qa.SendNoteAsync(world.Mediator, alice, b);
        await MediatorTestServer.WaitUntilAsync(() => world.Mediator.PushOutcome(PushOutcomes.SkippedCooldown) == skipped + 1, "Bob's direct pushes are paused");
        world.Store.DirectTakes.Should().ContainSingle("nothing is attempted during the pause");
        site.Posts.Should().BeLessThanOrEqualTo(1);
        (await world.Mediator.FetchAsync(bob)).Should().HaveCount(4, "nothing is lost");
    }

    // ---- the mediator's own endpoint as a recipient's ---------------------------------------------

    // A directory built from the recipient's DID document names the mediator itself (a mediated
    // recipient's service endpoint is the mediator's). The push re-enters the mediator once with a
    // message it cannot open: refused, not removed, no loop; pickup still delivers it.
    [Fact]
    public async Task A_directory_that_names_the_mediators_own_endpoint_re_enters_once_is_refused_and_leaves_the_message_for_pickup()
    {
        await using var world = await DirectPushWorld.StartAsync();
        var (bob, b, _) = await world.EnrolAsync("bob");
        world.Endpoints.Set(bob.Did, world.Mediator.Endpoint);
        var alice = await world.Mediator.NewSenderAsync();

        var note = await Qa.SendNoteAsync(world.Mediator, alice, b);
        await world.OutcomesAsync(PushOutcomes.Failed, 1);
        await world.Mediator.FlushAsync();
        await world.Mediator.PushesIdleAsync();

        world.Mediator.PushOutcomes.Should().Equal(new[] { PushOutcomes.Failed }, "the mediator does not answer its own push with a 2xx");
        world.Mediator.Outcomes.Should().Equal(new[] { ForwardOutcomes.Queued }, "the re-entry is not a forward the mediator opens: nothing is stored again, nothing loops");
        (await world.StoredAsync(bob.Did)).Should().Be(1, "not removed");
        var fetched = (await world.Mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(b, fetched.Payload, alice)).Message.Id.Should().Be(note.Id, "pickup delivers it");
        world.Mediator.LeakingLogLines(b.Did).Should().BeEmpty();
    }
}

/// <summary>
/// QA of #110 (direct push, D-7): the container the push is built from. The guarantees a push relies
/// on (the connect-time guard, no automatic redirects, no environment proxy) live in the named
/// client's primary handler, so the start-up check looks at that handler (QA2-2, QA2-3), not at which
/// transports are registered.
/// </summary>
public sealed class DirectPushContainerQaTests
{
    // A host that builds the HTTP transport through its public constructor and registers it itself,
    // without UseHttpTransport(). Over the default handler of the named "didcomm" client (automatic
    // redirects, the environment's proxy, no connect-time check) direct push refuses to start; over a
    // SocketsHttpHandler the host configured the way UseHttpTransport() does, it starts and its pushes
    // keep D-7: a 308 loop is one POST, and a 302 to a guarded address is one POST that reaches nothing.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QA_110_2_An_HTTP_transport_registered_without_UseHttpTransport_is_refused_or_its_pushes_keep_D7(bool vettedHandler)
    {
        await using var trap = new StandInProxy(c => StandInAnswers.AnswerAsync(c, "202 Accepted"));
        await using var loop = await LoopbackEndpoint.StartAsync(308, "/loop");
        await using var toTrap = await LoopbackEndpoint.StartAsync(302, $"http://[::ffff:127.0.0.1]:{trap.Port.ToString(CultureInfo.InvariantCulture)}/inbox");
        var policy = new OutboundEndpointPolicy { AllowedHosts = { "127.0.0.1" } };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.Configure(o => o.OutboundEndpointPolicy = policy);
        });
        if (vettedHandler)
        {
            var guard = new OutboundEndpointGuard(policy);
            services.AddHttpClient(HttpDidCommTransport.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectCallback = async (context, ct) => new System.Net.Sockets.NetworkStream(await guard.ConnectAsync(context.DnsEndPoint, ct), ownsSocket: true),
            });
        }
        else
        {
            services.AddHttpClient();
        }
        services.AddOptions<HttpTransportOptions>().Configure(o => o.AllowedSchemes = new[] { "http", "https" });
        services.AddSingleton<IDidCommTransport>(sp => new HttpDidCommTransport(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<HttpTransportOptions>>()));
        await using var sp = services.BuildServiceProvider();

        DirectPushSender? sender = null;
        var refused = Record.Exception(() => sender = DirectPushSender.Create(sp));
        if (!vettedHandler)
        {
            refused.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("UseHttpTransport()").And.Contain("follows redirects on its own");
            return;
        }
        refused.Should().BeNull("the host's handler keeps what a push relies on");

        var loopDelivered = await sender!.SendAsync(loop.At("/loop"), DirectPushHost.Payload, CancellationToken.None).WaitAsync(DirectPushHost.HangGuard);
        var trapDelivered = await sender.SendAsync(toTrap.At("/inbox"), DirectPushHost.Payload, CancellationToken.None).WaitAsync(DirectPushHost.HangGuard);

        loopDelivered.Should().Be(DirectSendResult.Failed, "a 308 at the end of the chain: not delivered, and the recipient's direct pushes pause");
        loop.Posts.Should().Be(1, "a 308 is never followed (OD-1): one POST");
        trapDelivered.Should().Be(DirectSendResult.Failed, "a 302 is never followed, so a guarded address can never count as delivered");
        toTrap.Posts.Should().Be(1);
        trap.Accepted.Should().Be(0, "no connection reaches a guarded address (D-7)");
    }

    // A host that decorates its transports (logging, metrics) after UseHttpTransport(): the named
    // client is still the guarded one.
    [Fact]
    public async Task QA_110_3_A_host_decorator_around_the_HTTP_transport_does_not_refuse_direct_push()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.UseHttpTransport();
        });
        var registered = services.Single(d => d.ServiceType == typeof(IDidCommTransport));
        services.Remove(registered);
        services.AddSingleton<IDidCommTransport>(sp => new CountingTransport((IDidCommTransport)registered.ImplementationFactory!(sp)));
        await using var sp = services.BuildServiceProvider();

        var refused = Record.Exception(() => DirectPushSender.Create(sp));

        refused.Should().BeNull("UseHttpTransport() was called; the named client is the guarded one");
    }

    // The pre-take check (the core policy, the guard of SendPackedAsync) refuses every spelling of a
    // private, loopback, link-local or metadata address; rows the engineers' table does not have.
    public static TheoryData<string> GuardedSpellings => new()
    {
        "https://[::1]/inbox",
        "https://[fd00::1]/inbox",
        "https://[fe80::1]/inbox",
        "https://172.16.0.1/inbox",
        "https://172.31.255.255/inbox",
        "https://192.168.1.1/inbox",
        "https://10.255.255.255/inbox",
        "https://2130706433/inbox",
        "https://0x7f000001/inbox",
        "https://0177.0.0.1/inbox",
        "https://127.1/inbox",
        "https://0.0.0.0/inbox",
        "https://user:pass@127.0.0.1/inbox",
        "https://[::ffff:169.254.169.254]/latest",
        "https://LOCALHOST/inbox",
    };

    [Theory]
    [MemberData(nameof(GuardedSpellings))]
    public async Task Every_spelling_of_a_guarded_address_is_refused_before_anything_is_taken(string endpoint)
    {
        await using var host = DirectPushHost.Build();

        var check = await host.Sender.CheckTargetAsync(new Uri(endpoint), CancellationToken.None);

        check.Should().Be(new PushTargetCheck(PushTarget.Refused, DirectPushSender.ReasonGuard), endpoint);
    }

    [Fact]
    public async Task Userinfo_in_a_public_endpoint_is_never_logged()
    {
        var endpoints = new ScriptedEndpoints(_ => ScriptedEndpoints.Answer(503));
        await using var host = DirectPushHost.Build(primary: endpoints);
        var endpoint = new Uri("https://qa-user:qa-secret@203.0.113.10/inbox");
        (await host.Sender.CheckTargetAsync(endpoint, CancellationToken.None)).Verdict.Should().Be(PushTarget.Accepted, "precondition: a public endpoint");

        var delivered = await host.PushAsync(endpoint);

        delivered.Should().BeFalse();
        endpoints.Posts.Should().ContainSingle();
        host.AssertNoWireValuesLogged("qa-user", "qa-secret", "203.0.113.10");
    }

    private sealed class CountingTransport(IDidCommTransport inner) : IDidCommTransport
    {
        public string Scheme => inner.Scheme;

        public bool CanHandle(Uri endpoint) => inner.CanHandle(endpoint);

        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct) => inner.SendAsync(request, ct);
    }
}
