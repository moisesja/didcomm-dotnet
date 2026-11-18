using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Transports;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static DidComm.InteropTests.Transports.Proxy.HttpTransportHarness;

namespace DidComm.InteropTests.Transports.Proxy;

/// <summary>
/// FR-TRN-13, <see cref="HttpTransportOptions.TrustedProxy"/>: only the proxy the host names carries
/// the transport's connections. The proxy itself is dialled even on loopback; every request (each
/// attempt, each followed 307) is checked before it is handed to the proxy, a refusal is a
/// <see cref="TransportException"/> that is neither retried nor counted by the shared circuit
/// breaker, and a destination the proxy bypasses gets the full connect-time guard.
/// </summary>
/// <remarks>
/// The stand-in proxy listens on loopback and is never on <c>AllowedHosts</c>: the default
/// blocking policy governs unless a case says otherwise. Nothing leaves the machine: public
/// destinations are TEST-NET-3 (<c>203.0.113.10</c>), which only the stand-in answers for.
/// </remarks>
public sealed class HttpTransportTrustedProxyTests
{
    private static Func<StandInConnection, Task> Accept202 => c => c.StatusAsync("202 Accepted", "Connection: close");

    // ----- the proxy carries the send -----------------------------------------------------------

    [Fact]
    public async Task Http_destination_is_forwarded_to_the_trusted_proxy_in_absolute_form_and_accepted()
    {
        await using var standIn = new StandInProxy(Accept202);
        await using var sp = Build(o => o.TrustedProxy = new WebProxy(standIn.Address()));
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var result = await SendAsync(transport, $"http://{PublicHost}/inbox");
        await standIn.WaitForRequestsAsync(1, HangGuard);

        result.Accepted.Should().BeTrue();
        result.HttpStatusCode.Should().Be(202);
        standIn.Requests.Should().Equal($"POST http://{PublicHost}/inbox HTTP/1.1");
    }

    [Fact]
    public async Task Https_destination_is_tunnelled_through_the_trusted_proxy_with_CONNECT()
    {
        // The stand-in records the CONNECT and refuses the tunnel, so the send fails after the proxy
        // was asked. (A proxy that hangs up without an answer makes the runtime itself re-send the
        // CONNECT on new connections, many times; a definite answer keeps it to one.)
        await using var standIn = new StandInProxy(c => c.StatusAsync("403 Forbidden", "Connection: close"));
        await using var sp = Build(o => o.TrustedProxy = new WebProxy(standIn.Address()));
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var thrown = await SendExpectingFailureAsync(transport, $"https://{PublicHost}/inbox");
        await standIn.WaitForRequestsAsync(1, HangGuard);

        standIn.Requests.Should().Equal($"CONNECT {PublicHost}:443 HTTP/1.1");
        thrown.Should().BeOfType<HttpRequestException>(Describe(thrown))
            .Which.HttpRequestError.Should().Be(HttpRequestError.ProxyTunnelError);
    }

    // ----- refused before the proxy is asked ----------------------------------------------------

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("[::ffff:127.0.0.1]")]
    [InlineData("[::127.0.0.1]")]
    [InlineData("[64:ff9b::a9fe:a9fe]")]
    [InlineData("[64:ff9b:1::7f00:1]")]
    [InlineData("127.0.0.1.")]
    [InlineData("2130706433")]
    [InlineData("localhost")] // ResolveDnsNames is on by default: the local lookup finds loopback.
    public async Task A_forbidden_destination_is_refused_once_before_the_proxy_is_asked(string host)
    {
        await using var standIn = new StandInProxy(Accept202);
        var attempts = new AttemptCounter();
        await using var sp = Build(
            o =>
            {
                o.TrustedProxy = new WebProxy(standIn.Address());
                o.MaxRetryAttempts = 3;
            },
            services => CountAttempts(services, attempts));
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var thrown = await SendExpectingFailureAsync(transport, $"http://{host}/inbox");

        thrown.Should().BeOfType<TransportException>("the refusal surfaces as the guard's own exception ({0})", Describe(thrown))
            .Which.Message.Should().Contain("private or reserved");
        attempts.Count.Should().Be(1, "a refusal is not retried, though MaxRetryAttempts = 3");
        standIn.Accepted.Should().Be(0, "the proxy is never asked to reach {0} (it recorded: [{1}])", host, string.Join("; ", standIn.Requests));
    }

    [Fact]
    public async Task Https_forbidden_destination_is_refused_before_any_CONNECT()
    {
        await using var standIn = new StandInProxy();
        await using var sp = Build(o => o.TrustedProxy = new WebProxy(standIn.Address()));
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var thrown = await SendExpectingFailureAsync(transport, "https://169.254.169.254/latest/meta-data");

        thrown.Should().BeOfType<TransportException>(Describe(thrown));
        standIn.Accepted.Should().Be(0, "no CONNECT to the metadata address (it recorded: [{0}])", string.Join("; ", standIn.Requests));
    }

    [Fact]
    public async Task Localhost_reaches_the_proxy_when_ResolveDnsNames_is_off_the_documented_limit()
    {
        // Pins the documented limit: with the local lookup off only IP literals are checked here,
        // and the proxy is asked to reach a name it may resolve to loopback (its egress filter's job).
        await using var standIn = new StandInProxy(Accept202);
        await using var sp = Build(o =>
        {
            o.TrustedProxy = new WebProxy(standIn.Address());
            o.OutboundEndpointPolicy = new OutboundEndpointPolicy { ResolveDnsNames = false };
        });
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var result = await SendAsync(transport, "http://localhost/inbox");
        await standIn.WaitForRequestsAsync(1, HangGuard);

        result.Accepted.Should().BeTrue();
        standIn.Requests.Should().Equal("POST http://localhost/inbox HTTP/1.1");
    }

    // ----- 307 ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://10.0.0.1/x")]
    [InlineData("http://[::ffff:169.254.169.254]/")]
    public async Task A_307_to_a_forbidden_destination_is_refused_before_the_proxy_is_asked_again(string location)
    {
        // The first request gets the redirect; anything after it would be answered 202, so a
        // followed hop that reached the proxy would show as a second recorded request.
        await using var standIn = new StandInProxy(c => c.RequestLine.Contains("/r ", StringComparison.Ordinal)
            ? c.StatusAsync("307 Temporary Redirect", $"Location: {location}", "Connection: close")
            : c.StatusAsync("202 Accepted", "Connection: close"));
        var attempts = new AttemptCounter();
        await using var sp = Build(
            o =>
            {
                o.TrustedProxy = new WebProxy(standIn.Address());
                o.MaxRetryAttempts = 3;
            },
            services => CountAttempts(services, attempts));
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var thrown = await SendExpectingFailureAsync(transport, $"http://{PublicHost}/r");

        thrown.Should().BeOfType<TransportException>(Describe(thrown)).Which.Message.Should().Contain("private or reserved");
        await standIn.WaitForRequestsAsync(1, HangGuard);
        standIn.Requests.Should().Equal($"POST http://{PublicHost}/r HTTP/1.1");
        standIn.Accepted.Should().Be(1);
        attempts.Count.Should().Be(2, "the first hop and the refused redirect hop, neither retried");
    }

    // ----- the shared circuit breaker -----------------------------------------------------------

    [Fact]
    public async Task Refusals_do_not_count_toward_the_shared_circuit_breaker()
    {
        await using var standIn = new StandInProxy(Accept202);
        await using var sp = Build(o =>
        {
            o.TrustedProxy = new WebProxy(standIn.Address());
            o.CircuitBreakerFailureThreshold = 2;
            o.CircuitBreakerOpenDuration = TimeSpan.FromMinutes(5);
        });
        var transport = sp.GetRequiredService<IDidCommTransport>();

        for (var i = 0; i < 6; i++)
        {
            var thrown = await SendExpectingFailureAsync(transport, "http://10.0.0.1/x");
            thrown.Should().BeOfType<TransportException>("refusal {0}: {1}", i + 1, Describe(thrown));
        }

        var result = await SendAsync(transport, $"http://{PublicHost}/inbox");

        result.Accepted.Should().BeTrue("six refusals must not have opened the breaker the good send shares");
        standIn.Requests.Should().Equal($"POST http://{PublicHost}/inbox HTTP/1.1");
    }

    // ----- the policy is honoured on the proxied path -------------------------------------------

    [Fact]
    public async Task An_allowlisted_private_destination_goes_through_the_proxy()
    {
        await using var standIn = new StandInProxy(Accept202);
        await using var sp = Build(o =>
        {
            o.TrustedProxy = new WebProxy(standIn.Address());
            o.OutboundEndpointPolicy = new OutboundEndpointPolicy { AllowedHosts = { "10.0.0.1" } };
        });
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var result = await SendAsync(transport, "http://10.0.0.1/x");
        await standIn.WaitForRequestsAsync(1, HangGuard);

        result.Accepted.Should().BeTrue();
        standIn.Requests.Should().Equal("POST http://10.0.0.1/x HTTP/1.1");
    }

    [Fact]
    public async Task With_BlockPrivateNetworks_off_in_the_inherited_core_policy_a_private_destination_goes_through_the_proxy()
    {
        await using var standIn = new StandInProxy(Accept202);
        await using var sp = Build(
            o =>
            {
                o.TrustedProxy = new WebProxy(standIn.Address());
                o.OutboundEndpointPolicy = null; // inherit DidCommOptions.OutboundEndpointPolicy
            },
            services => services.Configure<DidCommOptions>(o =>
                o.OutboundEndpointPolicy = new OutboundEndpointPolicy { BlockPrivateNetworks = false }));
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var result = await SendAsync(transport, "http://10.0.0.1/x");
        await standIn.WaitForRequestsAsync(1, HangGuard);

        result.Accepted.Should().BeTrue();
        standIn.Requests.Should().Equal("POST http://10.0.0.1/x HTTP/1.1");
    }

    // ----- recognising the proxy's own endpoint -------------------------------------------------

    [Theory]
    [InlineData("LOCALHOST.", "http")]
    [InlineData("LOCALHOST.", "https")]
    [InlineData("127.0.0.1", "http")]
    [InlineData("127.0.0.1", "https")]
    public async Task The_proxy_is_dialled_under_the_blocking_policy_however_its_loopback_host_is_spelled(string proxyHost, string scheme)
    {
        await using var standIn = new StandInProxy(Accept202);
        await AssertReachesStandInAsync(standIn, proxyHost, scheme);
    }

    [IPv6LoopbackTheory]
    [InlineData("http")]
    [InlineData("https")]
    public async Task The_proxy_is_dialled_on_the_IPv6_loopback(string scheme)
    {
        await using var standIn = new StandInProxy(Accept202, IPAddress.IPv6Loopback);
        await AssertReachesStandInAsync(standIn, "[::1]", scheme);
    }

    private static async Task AssertReachesStandInAsync(StandInProxy standIn, string proxyHost, string scheme)
    {
        await using var sp = Build(o => o.TrustedProxy = new WebProxy(standIn.Address("http", proxyHost)));
        var transport = sp.GetRequiredService<IDidCommTransport>();

        if (scheme == "http")
        {
            var result = await SendAsync(transport, $"http://{PublicHost}/inbox");
            result.Accepted.Should().BeTrue();
        }
        else
        {
            // The stand-in answers the CONNECT with a 202, which is not a tunnel: the send fails
            // after the proxy was asked, and the recorded CONNECT is the assertion.
            await SendExpectingFailureAsync(transport, $"https://{PublicHost}/inbox");
        }
        await standIn.WaitForRequestsAsync(1, HangGuard);

        standIn.Requests.Should().Equal(scheme == "http"
            ? $"POST http://{PublicHost}/inbox HTTP/1.1"
            : $"CONNECT {PublicHost}:443 HTTP/1.1");
    }

    // ----- bypass: a destination the proxy skips gets the full guard ----------------------------

    [Theory]
    [InlineData("BypassProxyOnLocal")]
    [InlineData("BypassList")]
    public async Task A_bypassed_destination_is_connected_directly_with_the_full_guard(string bypass)
    {
        // The destination is the stand-in's own port on "localhost": only the host spelling tells it
        // apart from the proxy endpoint. ResolveDnsNames = false, so the per-request check lets the
        // name through and only the connect-time guard can refuse it.
        await using var standIn = new StandInProxy(Accept202);
        var proxy = bypass == "BypassProxyOnLocal"
            ? new WebProxy(standIn.Address()) { BypassProxyOnLocal = true }
            : new WebProxy(standIn.Address()) { BypassList = new[] { "localhost" } };
        await using var sp = Build(o =>
        {
            o.TrustedProxy = proxy;
            o.OutboundEndpointPolicy = new OutboundEndpointPolicy { ResolveDnsNames = false };
        });
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var thrown = await SendExpectingFailureAsync(transport, $"http://localhost:{standIn.Port}/inbox");

        Chain(thrown).OfType<TransportException>().Should().ContainSingle(Describe(thrown))
            .Which.Message.Should().Contain("private or reserved", "the direct connect was vetted by the full guard");
        standIn.Accepted.Should().Be(0, "a bypassed loopback destination must not be dialled unfiltered (recorded: [{0}])", string.Join("; ", standIn.Requests));
    }

    // ----- SOCKS --------------------------------------------------------------------------------

    [Theory]
    [InlineData("socks5", "SOCKS5")]
    [InlineData("socks4a", "SOCKS4")]
    [InlineData("socks4", "SOCKS4")]
    public async Task A_socks_proxy_is_dialled_under_the_blocking_policy(string scheme, string greeting)
    {
        await using var standIn = new StandInProxy();
        await using var sp = Build(o => o.TrustedProxy = new WebProxy(standIn.Address(scheme)));
        var transport = sp.GetRequiredService<IDidCommTransport>();

        // The stand-in records the greeting and hangs up, so the send fails after the proxy was asked.
        await SendExpectingFailureAsync(transport, $"http://{PublicHost}/inbox");
        await standIn.WaitForRequestsAsync(1, HangGuard);

        standIn.Requests.Should().Equal(greeting);
    }

    // ----- the configured instance is copied ----------------------------------------------------

    [Fact]
    public async Task Changing_the_configured_WebProxy_after_resolution_does_not_move_the_transport()
    {
        await using var original = new StandInProxy(Accept202);
        await using var other = new StandInProxy(Accept202);
        var configured = new WebProxy(original.Address());
        await using var sp = Build(o => o.TrustedProxy = configured);
        var transport = sp.GetRequiredService<IDidCommTransport>();

        (await SendAsync(transport, $"http://{PublicHost}/one")).Accepted.Should().BeTrue();
        configured.Address = other.Address();
        var second = await SendAsync(transport, $"http://{PublicHost}/two");
        await original.WaitForRequestsAsync(2, HangGuard);

        second.Accepted.Should().BeTrue();
        original.Requests.Should().Equal($"POST http://{PublicHost}/one HTTP/1.1", $"POST http://{PublicHost}/two HTTP/1.1");
        other.Accepted.Should().Be(0, "the handler holds a copy taken when it was built");
    }

    // ----- validation ---------------------------------------------------------------------------

    // A distinctive host and password: neither may appear in the error message.
    private const string SecretHost = "secret-proxy-host.internal";
    private const string Password = "pw-s3cr3t";

    public static TheoryData<string> InvalidProxies => new()
    {
        "no Address", "relative", "ftp scheme", "user info", "socks without a port", "path", "query", "fragment",
    };

    private static WebProxy InvalidProxy(string rule) => rule switch
    {
        "no Address" => new WebProxy(),
        "relative" => new WebProxy { Address = new Uri("/proxy", UriKind.Relative) },
        "ftp scheme" => new WebProxy(new Uri($"ftp://{SecretHost}:21")),
        "user info" => new WebProxy(new Uri($"http://u:{Password}@{SecretHost}:1")),
        "socks without a port" => new WebProxy(new Uri($"socks5://{SecretHost}")),
        "path" => new WebProxy(new Uri($"http://{SecretHost}:1/x")),
        "query" => new WebProxy(new Uri($"http://{SecretHost}:1/?q=1")),
        "fragment" => new WebProxy(new Uri($"http://{SecretHost}:1/#f")),
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null),
    };

    [Theory]
    [MemberData(nameof(InvalidProxies))]
    public async Task An_invalid_TrustedProxy_fails_the_transport_resolution_without_echoing_the_address(string rule)
    {
        var proxy = InvalidProxy(rule);
        await using var sp = Build(o => o.TrustedProxy = proxy);

        var resolve = () => sp.GetRequiredService<IDidCommTransport>();

        var error = resolve.Should().Throw<InvalidOperationException>(rule).Which;
        error.Message.Should().StartWith("HttpTransportOptions.TrustedProxy", rule);
        Describe(error).Should().NotContain(Password, rule).And.NotContain(SecretHost, rule);
    }

    [Fact]
    public async Task Each_valid_proxy_scheme_resolves()
    {
        foreach (var scheme in new[] { "http", "https", "socks4", "socks4a", "socks5" })
        {
            await using var sp = Build(o => o.TrustedProxy = new WebProxy(new Uri($"{scheme}://127.0.0.1:1080")));
            sp.GetRequiredService<IDidCommTransport>().Should().NotBeNull(scheme);
        }
    }

    // ----- the hint is for direct connections only ----------------------------------------------

    [Fact]
    public async Task An_unreachable_trusted_proxy_fails_with_the_socket_error_and_no_direct_connection_hint()
    {
        var port = ClosedLoopbackPort();
        await using var sp = Build(o => o.TrustedProxy = new WebProxy(new Uri($"http://127.0.0.1:{port}")));
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var thrown = await SendExpectingFailureAsync(transport, $"http://{PublicHost}/inbox");

        thrown.Should().BeOfType<HttpRequestException>(Describe(thrown));
        Chain(thrown).OfType<SocketException>().Should().ContainSingle(Describe(thrown))
            .Which.SocketErrorCode.Should().Be(SocketError.ConnectionRefused, "the proxy was dialled, and refused");
        Describe(thrown).Should().NotContain("TrustedProxy").And.NotContain("connects directly");
    }

    // ----- a host's own primary handler ---------------------------------------------------------

    [Fact]
    public async Task A_host_supplied_primary_handler_decides_how_to_connect()
    {
        await using var standIn = new StandInProxy(Accept202);
        var seen = new List<Uri>();
        await using var sp = Build(
            o => o.TrustedProxy = new WebProxy(standIn.Address()),
            services => services.AddHttpClient(HttpDidCommTransport.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler(seen)));
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var result = await SendAsync(transport, "http://10.0.0.1/x");

        result.Accepted.Should().BeTrue("the host's handler answered");
        seen.Should().Equal(new Uri("http://10.0.0.1/x"));
        standIn.Accepted.Should().Be(0, "TrustedProxy has no effect once the host replaces the primary handler");
    }

    private sealed class RecordingHandler(List<Uri> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (seen)
                seen.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        }
    }
}
