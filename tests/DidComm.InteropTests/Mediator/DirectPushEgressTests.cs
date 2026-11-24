using System.Net;
using System.Net.Http;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.InteropTests.Samples;
using DidComm.InteropTests.Transports.Proxy;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
using DidComm.Transports;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using Polly.Timeout;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// #110 (D-7, the AD-6 replays): what one direct push puts on the wire. The push is one attempt on
/// the mediator's own HTTP transport: no retry whatever the host's transport retries, a circuit
/// breaker that never opens (so one failing recipient endpoint never stops other pushes or the
/// host's own sends), 307 followed up to the host's <c>MaxRedirectHops</c> and nothing else followed
/// (owner decision OD-1), every connection through the host's named client (the connect-time guard,
/// direct unless the host named a <c>TrustedProxy</c>). No log line carries an endpoint, a
/// <c>Location</c> or an exception's message.
/// </summary>
/// <remarks>
/// The rows about retries, the breaker and redirects answer from a scripted primary handler (no
/// network). The rows that need the real guard run against Kestrel on the loopback with the host's
/// own <c>UseHttpTransport</c> handler, allowing <c>127.0.0.1</c> for the first hop only.
/// </remarks>
public sealed class DirectPushEgressTests
{
    private const string Failing = "203.0.113.10";
    private const string Healthy = "203.0.113.11";
    private const string HostsOwn = "203.0.113.12";
    private const string Elsewhere = "203.0.113.20";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task AD6_A_503_is_one_POST_whatever_the_transports_retry_setting(int hostRetries)
    {
        var endpoints = new ScriptedEndpoints(_ => ScriptedEndpoints.Answer(503));
        await using var host = DirectPushHost.Build(http: o => o.MaxRetryAttempts = hostRetries, primary: endpoints);
        var endpoint = $"https://{Failing}/inbox";
        (await host.Sender.CheckTargetAsync(new Uri(endpoint), CancellationToken.None)).Verdict.Should().Be(PushTarget.Accepted, "precondition");

        var delivered = await host.PushAsync(endpoint);

        delivered.Should().BeFalse();
        endpoints.Posts.Should().ContainSingle("one attempt, never retried (the host retries {0} times)", hostRetries);
        host.SenderLines.Should().ContainSingle().Which.Should().Contain("503");
        host.AssertNoWireValuesLogged(endpoint, Failing);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("timeout")]
    public async Task AD6_A_connection_failure_or_a_timeout_is_one_attempt_and_propagates(string fault)
    {
        var endpoints = new ScriptedEndpoints(async (_, ct) =>
        {
            if (fault == "connection")
                throw new HttpRequestException("connection refused by " + Failing);
            // Parks this attempt until the transport's request timeout cancels it.
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ScriptedEndpoints.Answer(202);
        });
        // The host retries both 3 times; the request timeout (the push deadline, which the push's
        // transport never undercuts) is the trigger of the timeout row, and fires with a correct
        // product (no retry): it is not the property under test.
        await using var host = DirectPushHost.Build(
            http: o =>
            {
                o.MaxRetryAttempts = 3;
                o.RequestTimeout = TimeSpan.FromMilliseconds(200);
            },
            primary: endpoints,
            mediator: o => o.PushTimeout = TimeSpan.FromMilliseconds(200));
        var endpoint = $"https://{Failing}/inbox";

        var caught = await Record.ExceptionAsync(() => host.PushAsync(endpoint));

        if (fault == "connection")
            caught.Should().BeOfType<HttpRequestException>();
        else
            caught.Should().BeOfType<TimeoutRejectedException>();
        endpoints.Posts.Should().ContainSingle("one attempt, never retried");
        host.AssertNoWireValuesLogged([endpoint, Failing, .. DirectPushHost.Messages(caught!)]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task AD6_A_failing_endpoint_never_opens_a_breaker_for_other_recipients_or_for_the_hosts_own_sends(int hostRetries)
    {
        var endpoints = new ScriptedEndpoints(request => ScriptedEndpoints.Answer(request.RequestUri!.Host == Failing ? 503 : 202));
        // The host's breaker opens after 5 failures in a row.
        await using var host = DirectPushHost.Build(
            http: o =>
            {
                o.CircuitBreakerFailureThreshold = 5;
                o.MaxRetryAttempts = hostRetries;
            },
            primary: endpoints);
        var failing = $"https://{Failing}/inbox";

        for (var i = 1; i <= 25; i++)
        {
            var refused = await host.PushAsync(failing);
            refused.Should().BeFalse();
            endpoints.PostsTo(Failing).Should().Be(i, "push {0} made exactly one POST: no retry, and no breaker stopped it", i);
        }

        var other = await host.PushAsync($"https://{Healthy}/inbox");
        var again = await host.PushAsync(failing);
        var own = await host.Services.GetRequiredService<DidCommClient>()
            .SendPackedAsync(DirectPushHost.Payload, new Uri($"https://{HostsOwn}/inbox"), MediaTypes.Encrypted)
            .WaitAsync(DirectPushHost.HangGuard);

        other.Should().BeTrue("another recipient's push is not stopped by the failing one");
        endpoints.PostsTo(Healthy).Should().Be(1);
        again.Should().BeFalse();
        endpoints.PostsTo(Failing).Should().Be(26, "the failing endpoint is still tried once per push");
        own.Accepted.Should().BeTrue("the host's own transport never saw the pushes, so its breaker is closed");
        endpoints.PostsTo(HostsOwn).Should().Be(1);
        host.AssertNoWireValuesLogged(failing, Failing, Healthy);
    }

    public static TheoryData<int, int, int, int, bool> RedirectChains => new()
    {
        // 307s in the chain, the status at its end, MaxRedirectHops, the host's retries, delivered → POSTs.
        { 10, 202, 5, 3, false },
        { 5, 503, 5, 3, false },
        { 10, 202, 2, 3, false },
        { 5, 202, 5, 3, true },
    };

    [Theory]
    [MemberData(nameof(RedirectChains))]
    public async Task AD6_N_307s_make_at_most_MaxRedirectHops_plus_one_POSTs_with_no_retry(int chain, int final, int maxHops, int hostRetries, bool expected)
    {
        var endpoints = new ScriptedEndpoints(request =>
        {
            var n = int.Parse(request.RequestUri!.Segments[^1], System.Globalization.CultureInfo.InvariantCulture);
            // Each hop names another public host, as a recipient's server could.
            return n < chain
                ? ScriptedEndpoints.Answer(307, $"https://203.0.113.{100 + n + 1}/hop/{n + 1}")
                : ScriptedEndpoints.Answer(final);
        });
        await using var host = DirectPushHost.Build(
            http: o =>
            {
                o.MaxRedirectHops = maxHops;
                o.MaxRetryAttempts = hostRetries;
            },
            primary: endpoints);
        var endpoint = "https://203.0.113.100/hop/0";

        var delivered = await host.PushAsync(endpoint);

        delivered.Should().Be(expected);
        endpoints.Posts.Should().HaveCount(Math.Min(chain, maxHops) + 1, "a chain is followed up to {0} 307s, each hop one POST", maxHops);
        endpoints.Posts.Should().OnlyContain(p => p.Body.SequenceEqual(DirectPushHost.Payload), "every hop carries the stored message unchanged");
        host.AssertNoWireValuesLogged([endpoint, .. endpoints.Posts.Select(p => p.Uri.ToString())]);
    }

    [Fact]
    public async Task AD6_A_307_loop_ends_at_the_bound_and_leaves_the_message_for_pickup()
    {
        var endpoints = new ScriptedEndpoints(_ => ScriptedEndpoints.Answer(307, "/loop"));
        await using var host = DirectPushHost.Build(http: o => o.MaxRetryAttempts = 3, primary: endpoints);
        var endpoint = $"https://{Failing}/loop";

        var delivered = await host.PushAsync(endpoint);

        delivered.Should().BeFalse("not delivered: the message stays for pickup");
        endpoints.Posts.Should().HaveCount(new HttpTransportOptions().MaxRedirectHops + 1, "the default bound of 307s, then the end");
        host.SenderLines.Should().ContainSingle().Which.Should().Contain("307");
        host.AssertNoWireValuesLogged(endpoint, Failing, "/loop");
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(308)]
    public async Task AD6_301_302_303_and_308_are_one_POST_and_not_followed(int status)
    {
        var location = $"https://{Elsewhere}/moved";
        var endpoints = new ScriptedEndpoints(request => request.RequestUri!.Host == Elsewhere
            ? ScriptedEndpoints.Answer(202)
            : ScriptedEndpoints.Answer(status, location));
        await using var host = DirectPushHost.Build(http: o => o.MaxRetryAttempts = 3, primary: endpoints);
        var endpoint = $"https://{Failing}/inbox";

        var delivered = await host.PushAsync(endpoint);

        delivered.Should().BeFalse();
        endpoints.Posts.Should().ContainSingle();
        endpoints.PostsTo(Elsewhere).Should().Be(0, "a {0} is never followed", status);
        host.AssertNoWireValuesLogged(endpoint, location, Failing, Elsewhere);
    }

    [Fact]
    public async Task A_307_without_a_Location_is_one_POST_and_not_delivered()
    {
        var endpoints = new ScriptedEndpoints(_ => ScriptedEndpoints.Answer(307));
        await using var host = DirectPushHost.Build(http: o => o.MaxRetryAttempts = 3, primary: endpoints);

        var endpoint = $"https://{Failing}/inbox";

        var delivered = await host.PushAsync(endpoint);

        delivered.Should().BeFalse();
        endpoints.Posts.Should().ContainSingle();
        host.SenderLines.Should().ContainSingle().Which.Should().Contain("307");
        host.AssertNoWireValuesLogged(endpoint, Failing);
    }

    [Fact]
    public async Task A_307_followed_to_a_2xx_is_delivered_at_the_end_of_the_chain()
    {
        var location = $"https://{Elsewhere}/v2/inbox";
        var endpoints = new ScriptedEndpoints(request => request.RequestUri!.Host == Elsewhere
            ? ScriptedEndpoints.Answer(202)
            : ScriptedEndpoints.Answer(307, location));
        await using var host = DirectPushHost.Build(primary: endpoints);
        var endpoint = $"https://{Failing}/inbox";

        var delivered = await host.PushAsync(endpoint);

        delivered.Should().BeTrue();
        endpoints.Posts.Select(p => p.Uri.ToString()).Should().Equal(endpoint, location);
        endpoints.Posts[^1].Body.Should().Equal(DirectPushHost.Payload);
        host.SenderLines.Should().BeEmpty();
        host.AssertNoWireValuesLogged(endpoint, location, Failing, Elsewhere);
    }

    public static TheoryData<string> GuardedTargets => new()
    {
        // An IPv4-mapped spelling of the loopback, where a listener answers if the guard lets the connection through.
        "http://[::ffff:127.0.0.1]:{trap}/inbox",
        "http://10.0.0.1/inbox",
        "http://169.254.169.254/latest/meta-data",
    };

    [Theory]
    [MemberData(nameof(GuardedTargets))]
    public async Task AD6_A_307_to_a_guarded_address_is_refused(string target)
    {
        await using var trap = new StandInProxy(c => StandInAnswers.AnswerAsync(c, "202 Accepted"));
        var location = target.Replace("{trap}", trap.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        await using var first = await LoopbackEndpoint.StartAsync(307, location);
        // The host's own handler (no stub): its connect-time guard vets every connection. Only the
        // first hop's literal is allowed.
        await using var host = DirectPushHost.Build(
            http: o =>
            {
                o.MaxRetryAttempts = 3;
                o.RequestTimeout = TimeSpan.FromSeconds(10);
            },
            core: o => o.OutboundEndpointPolicy = new OutboundEndpointPolicy { AllowedHosts = { "127.0.0.1" } },
            mediator: o => o.PushTimeout = TimeSpan.FromSeconds(10));
        var endpoint = first.At("/inbox");
        (await host.Sender.CheckTargetAsync(endpoint, CancellationToken.None)).Verdict.Should().Be(PushTarget.Accepted, "precondition: the first hop may be pushed to");

        var caught = await Record.ExceptionAsync(() => host.PushAsync(endpoint));

        caught.Should().NotBeNull("the push fails: the redirect's target is refused at connect time");
        DirectPushHost.Chain(caught!).OfType<TransportException>().Should().ContainSingle()
            .Which.Message.Should().Contain("private or reserved");
        first.Posts.Should().Be(1, "the first hop was posted once, never retried");
        trap.Accepted.Should().Be(0, "no connection reached the guarded address");
        host.AssertNoWireValuesLogged([endpoint.ToString(), location, .. DirectPushHost.Messages(caught!)]);
    }

    public static TheoryData<string, int> BehindOuterHandlers => new()
    {
        // The endpoint's script, the POSTs one push makes.
        { "503", 1 },
        { "307 chain ending in 503", 6 },
    };

    // ADV-110-1a-1: a host can put handlers in front of every client (ConfigureHttpClientDefaults, as
    // .NET Aspire's service defaults do with a resilience handler that retries POST on a 5xx and has a
    // breaker of its own). A direct push sends through the named client's innermost handler only:
    // those handlers never see it, so they can neither repeat it nor share a breaker with the host's
    // own sends, which still go through them.
    [Theory]
    [MemberData(nameof(BehindOuterHandlers))]
    public async Task A_handler_the_host_puts_in_front_of_every_client_never_sees_a_push(string script, int posts)
    {
        var endpoints = new ScriptedEndpoints(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (script == "503" || path == "/hop/5")
                return ScriptedEndpoints.Answer(503);
            var n = int.Parse(path["/hop/".Length..], System.Globalization.CultureInfo.InvariantCulture);
            return ScriptedEndpoints.Answer(307, $"/hop/{n + 1}");
        });
        var outer = new OuterHandlers();
        await using var host = DirectPushHost.Build(
            http: o => o.MaxRetryAttempts = 0,
            primary: endpoints,
            more: s => s.ConfigureHttpClientDefaults(c => c.AddHttpMessageHandler(() => new RetryingHandler(outer, retries: 3))));
        var endpoint = $"https://{Failing}/hop/0";

        var delivered = await host.PushAsync(endpoint);

        delivered.Should().BeFalse();
        endpoints.Posts.Should().HaveCount(posts, "one attempt: {0}", script);
        outer.Requests.Should().Be(0, "the host's own handlers are not in a push's path");

        // Control: the host's own send goes through them (its 503 is retried three times).
        var hostsOwn = await Record.ExceptionAsync(() => host.Services.GetRequiredService<DidCommClient>()
            .SendPackedAsync(DirectPushHost.Payload, new Uri($"https://{HostsOwn}/hop/0"), MediaTypes.Encrypted)
            .WaitAsync(DirectPushHost.HangGuard));
        hostsOwn.Should().BeOfType<TransportException>();
        outer.Requests.Should().BeGreaterThan(0, "precondition: the handler is in the named client's chain");
        endpoints.PostsTo(HostsOwn).Should().Be(posts + 3, "precondition: it retries the 503");
    }

    // ADV-110-2a-1: every name a push looks up (the endpoint before the take, and each followed hop's
    // destination through a TrustedProxy) goes through the outbound check's lookup, here its seam,
    // under the send's token. A 307 to a name that resolves to a private address is refused before
    // the proxy is asked for it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Behind_a_TrustedProxy_every_name_a_push_looks_up_goes_through_the_asynchronous_lookup(bool hopResolvesPrivate)
    {
        var lookedUp = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var policy = new OutboundEndpointPolicy
        {
            ResolveDnsAsync = (host, _) =>
            {
                lookedUp.Enqueue(host);
                var address = hopResolvesPrivate && host.StartsWith("hop.", StringComparison.Ordinal) ? "10.0.0.1" : "203.0.113.30";
                return Task.FromResult(new[] { IPAddress.Parse(address) });
            },
        };
        await using var proxy = new StandInProxy(c => c.RequestLine.Contains("push.recipient.example", StringComparison.Ordinal)
            ? StandInAnswers.AnswerAsync(c, "307 Temporary Redirect", "Location: http://hop.recipient.example/inbox")
            : StandInAnswers.AnswerAsync(c, "202 Accepted"));
        await using var host = DirectPushHost.Build(
            http: o =>
            {
                o.TrustedProxy = new WebProxy(proxy.Address());
                o.MaxRetryAttempts = 3;
            },
            core: o => o.OutboundEndpointPolicy = policy);
        var endpoint = new Uri("http://push.recipient.example/inbox");

        var check = await host.Sender.CheckTargetAsync(endpoint, CancellationToken.None);
        DirectSendResult? result = null;
        var caught = await Record.ExceptionAsync(async () => result = await host.PushResultAsync(endpoint));

        check.Verdict.Should().Be(PushTarget.Accepted);
        lookedUp.Should().Equal("push.recipient.example", "push.recipient.example", "hop.recipient.example");
        if (hopResolvesPrivate)
        {
            caught.Should().BeOfType<TransportException>().Which.Message.Should().Contain("private or reserved");
            proxy.Requests.Should().Equal("POST http://push.recipient.example/inbox HTTP/1.1");
        }
        else
        {
            caught.Should().BeNull();
            result.Should().Be(DirectSendResult.Delivered);
            proxy.Requests.Should().Equal("POST http://push.recipient.example/inbox HTTP/1.1", "POST http://hop.recipient.example/inbox HTTP/1.1");
        }
    }

    [Fact]
    public async Task With_a_TrustedProxy_a_private_endpoint_is_refused_before_the_proxy_is_contacted()
    {
        await using var proxy = new StandInProxy(c => StandInAnswers.AnswerAsync(c, "202 Accepted"));
        await using var host = DirectPushHost.Build(http: o =>
        {
            o.TrustedProxy = new WebProxy(proxy.Address());
            o.MaxRetryAttempts = 3;
        });
        var endpoint = "http://10.0.0.1/inbox";

        var check = await host.Sender.CheckTargetAsync(new Uri(endpoint), CancellationToken.None);
        var caught = await Record.ExceptionAsync(() => host.PushAsync(endpoint));

        check.Should().Be(new PushTargetCheck(PushTarget.Refused, DirectPushSender.ReasonGuard));
        // Sent anyway: the transport's own destination check (it runs because the push names the
        // host's proxy) refuses it before anything goes to the proxy.
        caught.Should().BeOfType<TransportException>().Which.HttpStatusCode.Should().BeNull();
        proxy.Accepted.Should().Be(0);
        host.AssertNoWireValuesLogged([endpoint, .. DirectPushHost.Messages(caught!)]);
    }

    [Fact]
    public async Task With_a_TrustedProxy_a_307_to_a_private_address_is_refused_before_the_proxy_is_asked_for_it()
    {
        const string Location = "http://10.0.0.1/inbox";
        await using var proxy = new StandInProxy(c => c.RequestLine.Contains(Failing, StringComparison.Ordinal)
            ? StandInAnswers.AnswerAsync(c, "307 Temporary Redirect", "Location: " + Location)
            : StandInAnswers.AnswerAsync(c, "202 Accepted"));
        await using var host = DirectPushHost.Build(http: o =>
        {
            o.TrustedProxy = new WebProxy(proxy.Address());
            o.MaxRetryAttempts = 3;
        });
        var endpoint = $"http://{Failing}/inbox";
        (await host.Sender.CheckTargetAsync(new Uri(endpoint), CancellationToken.None)).Verdict.Should().Be(PushTarget.Accepted, "precondition");

        var caught = await Record.ExceptionAsync(() => host.PushAsync(endpoint));

        caught.Should().BeOfType<TransportException>().Which.HttpStatusCode.Should().BeNull("the destination check refused the hop; the endpoint did not answer it");
        caught!.Message.Should().Contain("private or reserved");
        proxy.Requests.Should().ContainSingle().Which.Should().Contain(Failing, "only the first hop reached the proxy");
        host.AssertNoWireValuesLogged([endpoint, Location, .. DirectPushHost.Messages(caught)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_a_TrustedProxy_the_transports_policy_is_inherited_from_the_core_options_when_it_has_none(bool transportHasItsOwn)
    {
        const string Location = "http://10.0.0.1/inbox";
        await using var proxy = new StandInProxy(c => c.RequestLine.Contains(Failing, StringComparison.Ordinal)
            ? StandInAnswers.AnswerAsync(c, "307 Temporary Redirect", "Location: " + Location)
            : StandInAnswers.AnswerAsync(c, "202 Accepted"));
        // The core policy allows 10.0.0.1; a transport policy of its own (the default one) does not.
        await using var host = DirectPushHost.Build(
            http: o =>
            {
                o.TrustedProxy = new WebProxy(proxy.Address());
                if (transportHasItsOwn)
                    o.OutboundEndpointPolicy = new OutboundEndpointPolicy();
            },
            core: o => o.OutboundEndpointPolicy = new OutboundEndpointPolicy { AllowedHosts = { "10.0.0.1" } });
        var endpoint = $"http://{Failing}/inbox";

        bool? delivered = null;
        var caught = await Record.ExceptionAsync(async () => delivered = await host.PushAsync(endpoint));

        if (transportHasItsOwn)
        {
            caught.Should().BeOfType<TransportException>().Which.Message.Should().Contain("private or reserved");
            proxy.Requests.Should().ContainSingle();
        }
        else
        {
            caught.Should().BeNull("the core policy is the one in effect and allows the redirect's target");
            delivered.Should().BeTrue();
            proxy.Requests.Should().Equal($"POST {endpoint} HTTP/1.1", $"POST {Location} HTTP/1.1");
        }
    }
}

/// <summary>
/// #110 (D-7): with a process-wide proxy set (<see cref="HttpClient.DefaultProxy"/> or a proxy
/// environment variable), a direct push still connects directly, through the host's named client,
/// and its connect-time guard refuses a forbidden destination itself. Runs in the non-parallel
/// <see cref="DefaultProxyCollection"/>.
/// </summary>
/// <remarks>
/// The stand-in proxy is allowed by the policy, so a push that used the process proxy would get
/// past the guard and the stand-in would see a connection.
/// </remarks>
[Collection(nameof(DefaultProxyCollection))]
public sealed class DirectPushProcessProxyTests
{
    [Theory]
    [InlineData("DefaultProxy")]
    [InlineData("HTTP_PROXY")]
    [InlineData("ALL_PROXY")]
    public async Task With_a_process_proxy_set_the_push_goes_direct(string source)
    {
        await using var standIn = new StandInProxy();
        var proxy = standIn.Address();
        using var scope = source == "DefaultProxy"
            ? ProcessProxyScope.FromDefaultProxy(proxy)
            : ProcessProxyScope.FromEnvironment(source, proxy);
        const string Endpoint = "http://10.0.0.1/inbox";
        ProcessProxyScope.AssertProxies(new Uri(Endpoint), proxy);
        await using var host = DirectPushHost.Build(
            http: o => o.MaxRetryAttempts = 3,
            core: o => o.OutboundEndpointPolicy = new OutboundEndpointPolicy { AllowedHosts = { "127.0.0.1" } });

        var caught = await Record.ExceptionAsync(() => host.PushAsync(Endpoint));

        // The push has finished, so no later connection can come: the count is final.
        standIn.Accepted.Should().Be(0, "the {0} process proxy must not carry the push (it recorded: [{1}])", source, string.Join("; ", standIn.Requests));
        caught.Should().NotBeNull();
        DirectPushHost.Chain(caught!).OfType<TransportException>().Should().ContainSingle()
            .Which.Message.Should().Contain("private or reserved", "the connect-time guard refused 10.0.0.1 itself");
        host.AssertNoWireValuesLogged([Endpoint, .. DirectPushHost.Messages(caught!)]);
    }
}

/// <summary>
/// #110 (ADV-110-1a-1's fix): a direct push sends over the named client's innermost handler, taken
/// from the factory's current chain for each send. IHttpClientFactory rotates that chain after its
/// handler lifetime and disposes an expired chain once nothing references its outer handler. These
/// tests run with a one-second handler lifetime and the factory's own cleanup (every 10 s), waiting
/// on the factory's log lines and on the handlers' disposal, never on a fixed time.
/// </summary>
public sealed class DirectPushHandlerLifetimeTests
{
    // IHttpClientFactory rotates the named client's chain after its handler lifetime and disposes the
    // old chain once nothing references it. A push takes the current chain for each send and keeps it
    // until the send ends, so a push after a rotation never uses a disposed handler.
    [Fact]
    public async Task A_push_after_each_handler_rotation_uses_the_current_handler_with_one_POST()
    {
        var primaries = new RotatingPrimaries();
        await using var host = DirectPushHost.Build(more: s => s.AddHttpClient(HttpDidCommTransport.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(primaries.Next)
            .SetHandlerLifetime(TimeSpan.FromSeconds(1)));
        var endpoint = "https://203.0.113.11/inbox";

        for (var rotation = 0; rotation < 3; rotation++)
        {
            var before = primaries.Created;
            var delivered = await host.PushAsync(endpoint);

            delivered.Should().BeTrue("push {0}", rotation);
            primaries.Posts.Should().Be(rotation + 1, "each push is one POST");
            var used = primaries.LastUsed;
            used.Should().NotBeNull();
            if (rotation == 2)
                break;
            // The chain that push used expires (1 s), and the factory disposes it once it is unreachable.
            await used!.DisposedAsync(TimeSpan.FromSeconds(60));
            primaries.Created.Should().BeGreaterThanOrEqualTo(before, "a rotation creates a new primary handler for the next push");
        }
        primaries.Faults.Should().Be(0, "no push used a disposed handler");
    }

    // The named client's own timeout (ConfigureHttpClient) still bounds a push: only the handlers in
    // front of the primary one are left out.
    [Fact]
    public async Task The_named_clients_own_timeout_still_ends_a_stalled_push()
    {
        var stalled = new ScriptedEndpoints(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ScriptedEndpoints.Answer(202);
        });
        await using var host = DirectPushHost.Build(
            primary: stalled,
            more: s => s.AddHttpClient(HttpDidCommTransport.HttpClientName).ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMilliseconds(200)));

        var caught = await Record.ExceptionAsync(() => host.PushAsync("https://203.0.113.11/inbox"));

        caught.Should().BeAssignableTo<OperationCanceledException>("the client's 200 ms timeout ended the stall")
            .Which.InnerException.Should().BeOfType<TimeoutException>();
        stalled.Posts.Should().ContainSingle();
    }

    // A push holds the chain it took until its send ends: the factory's cleanup, which runs while the
    // push is held after its chain expired, does not dispose the handler the push is using.
    [Fact]
    public async Task A_push_in_flight_keeps_its_handler_chain_through_the_factorys_cleanup()
    {
        var primaries = new RotatingPrimaries { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = DirectPushHost.Build(more: s => s.AddHttpClient(HttpDidCommTransport.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(primaries.Next)
            .SetHandlerLifetime(TimeSpan.FromSeconds(1)));

        var baseline = host.Logs.Entries.Count;
        var push = host.Sender.SendAsync(new Uri("https://203.0.113.11/inbox"), DirectPushHost.Payload, CancellationToken.None);
        await primaries.Arrived.Task.WaitAsync(DirectPushHost.HangGuard);
        var held = primaries.LastArrived!;

        // The chain the held push uses expires, then a cleanup cycle runs after that (the collector
        // nudged meanwhile, so an unreferenced chain would be found and disposed).
        using (var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            while (!CleanupRanAfterExpiry(host.Logs.Entries.Skip(baseline).ToArray()))
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(TimeSpan.FromMilliseconds(250), guard.Token);
            }
        }
        primaries.Hold.SetResult();
        var delivered = await push.WaitAsync(DirectPushHost.HangGuard);

        held.DisposedWhileInFlight.Should().BeFalse("the factory never disposes a chain a push is using");
        delivered.Should().Be(DirectSendResult.Delivered);
        primaries.Faults.Should().Be(0);

        static bool CleanupRanAfterExpiry(string[] lines)
        {
            var expired = Array.FindIndex(lines, l => l.StartsWith("HttpMessageHandler expired after", StringComparison.Ordinal));
            return expired >= 0 && lines.Skip(expired + 1).Any(l => l.StartsWith("Ending HttpMessageHandler cleanup cycle", StringComparison.Ordinal));
        }
    }
}

/// <summary>Counts what reaches the handlers a host put in front of the named client.</summary>
internal sealed class OuterHandlers
{
    private int _requests;

    /// <summary>Requests the outer handler was handed (each may be sent several times inside).</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Count one.</summary>
    public void Count() => Interlocked.Increment(ref _requests);
}

/// <summary>Retries a 5xx answer, as the standard resilience handler's retry does by default.</summary>
internal sealed class RetryingHandler(OuterHandlers outer, int retries) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        outer.Count();
        for (var attempt = 0; ; attempt++)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode < 500 || attempt == retries)
                return response;
            response.Dispose();
        }
    }
}

/// <summary>
/// The named client's primary handlers, one per rotation: each answers 202, records that it was
/// used and when the factory disposed it, and fails a request it gets after that.
/// </summary>
internal sealed class RotatingPrimaries
{
    private int _created;
    private int _posts;
    private int _faults;
    private volatile Primary? _lastUsed;

    public int Created => Volatile.Read(ref _created);

    public int Posts => Volatile.Read(ref _posts);

    public int Faults => Volatile.Read(ref _faults);

    public Primary? LastUsed => _lastUsed;

    /// <summary>When set, each request waits for it before it is answered.</summary>
    public TaskCompletionSource? Hold { get; init; }

    /// <summary>Completed when a request arrives at a primary handler.</summary>
    public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The primary handler the last request arrived at.</summary>
    public Primary? LastArrived { get; private set; }

    /// <summary>The factory's primary-handler callback.</summary>
    public HttpMessageHandler Next()
    {
        Interlocked.Increment(ref _created);
        return new Primary(this);
    }

    internal sealed class Primary(RotatingPrimaries owner) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Wait (nudging the collector, bounded) until the factory disposed this handler.</summary>
        public async Task DisposedAsync(TimeSpan hangGuard)
        {
            using var cts = new CancellationTokenSource(hangGuard);
            while (!_disposed.Task.IsCompleted)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.WhenAny(_disposed.Task, Task.Delay(TimeSpan.FromMilliseconds(250), cts.Token));
                cts.Token.ThrowIfCancellationRequested();
            }
        }

        /// <summary>Whether the factory disposed this handler while a request was in it.</summary>
        public bool DisposedWhileInFlight { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_disposed.Task.IsCompleted)
            {
                Interlocked.Increment(ref owner._faults);
                throw new ObjectDisposedException(nameof(Primary));
            }
            if (owner.Hold is { } hold)
            {
                owner.LastArrived = this;
                owner.Arrived.TrySetResult();
                await hold.Task;
                if (_disposed.Task.IsCompleted)
                {
                    DisposedWhileInFlight = true;
                    Interlocked.Increment(ref owner._faults);
                    throw new ObjectDisposedException(nameof(Primary));
                }
            }
            Interlocked.Increment(ref owner._posts);
            owner._lastUsed = this;
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }

        protected override void Dispose(bool disposing)
        {
            _disposed.TrySetResult();
            base.Dispose(disposing);
        }
    }
}
