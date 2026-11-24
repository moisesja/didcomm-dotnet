using System.Net;
using System.Reflection;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly.Timeout;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// #110 (D-7): the mediator's own single-attempt HTTP push, <see cref="DirectPushSender"/>. Its
/// transport settings (every public setting copied, three overridden), the checks made before a
/// record is taken (<see cref="DirectPushSender.CheckTargetAsync"/>), how a send's outcome is reported,
/// and the start-up refusals. The egress on the wire (retries, breaker, 307 chains, the connect-time
/// guard, proxies) is in <c>DidComm.InteropTests</c>, <c>DirectPushEgressTests</c>.
/// </summary>
public sealed class DirectPushSenderTests
{
    /// <summary>Every public settable property of <see cref="HttpTransportOptions"/> the copy names. A new one fails the copy tests until the copy (and this list) names it.</summary>
    private static readonly string[] CopiedSettings =
    {
        nameof(HttpTransportOptions.RequestTimeout),
        nameof(HttpTransportOptions.MaxRetryAttempts),
        nameof(HttpTransportOptions.RetryBaseDelay),
        nameof(HttpTransportOptions.CircuitBreakerFailureThreshold),
        nameof(HttpTransportOptions.CircuitBreakerOpenDuration),
        nameof(HttpTransportOptions.AllowedSchemes),
        nameof(HttpTransportOptions.MaxRedirectHops),
        nameof(HttpTransportOptions.OutboundEndpointPolicy),
        nameof(HttpTransportOptions.TrustedProxy),
    };

    private static PropertyInfo[] PublicSettings() =>
        typeof(HttpTransportOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.SetMethod!.IsPublic)
            .ToArray();

    [Fact]
    public void The_copy_names_every_public_setting_of_the_transport_options()
    {
        PublicSettings().Select(p => p.Name).Should().BeEquivalentTo(CopiedSettings,
            "a setting added to HttpTransportOptions must be copied for direct push, or deliberately overridden, before this list names it");
    }

    [Fact]
    public void The_copy_keeps_every_host_setting_except_no_retry_a_breaker_that_never_opens_and_the_resolved_policy()
    {
        // Every setting off its default, so a setting the copy forgets shows as the default.
        var host = new HttpTransportOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(7),
            MaxRetryAttempts = 9,
            RetryBaseDelay = TimeSpan.FromMilliseconds(123),
            CircuitBreakerFailureThreshold = 4,
            CircuitBreakerOpenDuration = TimeSpan.FromSeconds(11),
            AllowedSchemes = new List<string> { "https", "http" },
            MaxRedirectHops = 2,
            OutboundEndpointPolicy = new OutboundEndpointPolicy { ResolveDnsNames = false },
            TrustedProxy = new WebProxy("http://proxy.example:3128"),
        };
        foreach (var setting in PublicSettings())
        {
            setting.GetValue(host).Should().NotBeEquivalentTo(setting.GetValue(new HttpTransportOptions()),
                "precondition: {0} must be off its default, or forgetting to copy it would go unnoticed", setting.Name);
        }
        var resolved = new OutboundEndpointPolicy { AllowedHosts = { "inbox.example" } };

        // Everything else is the host's, the request timeout included: a stall it ends before the
        // push deadline ends the push as not delivered, which pauses the recipient's direct pushes.
        var copy = DirectPushSender.CopyForDirectPush(host, resolved);

        var overrides = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [nameof(HttpTransportOptions.MaxRetryAttempts)] = 0,
            [nameof(HttpTransportOptions.CircuitBreakerFailureThreshold)] = int.MaxValue,
            [nameof(HttpTransportOptions.OutboundEndpointPolicy)] = resolved,
        };
        foreach (var setting in PublicSettings())
        {
            var actual = setting.GetValue(copy);
            if (overrides.TryGetValue(setting.Name, out var expected) && expected is int)
                actual.Should().Be(expected, setting.Name);
            else if (expected is not null)
                actual.Should().BeSameAs(expected, setting.Name);
            else if (setting.Name == nameof(HttpTransportOptions.AllowedSchemes))
                actual.Should().BeEquivalentTo(host.AllowedSchemes, o => o.WithStrictOrdering(), setting.Name).And.NotBeSameAs(host.AllowedSchemes, "the list is copied");
            else if (setting.PropertyType.IsValueType)
                actual.Should().Be(setting.GetValue(host), setting.Name);
            else
                actual.Should().BeSameAs(setting.GetValue(host), setting.Name);
        }
        host.MaxRetryAttempts.Should().Be(9, "the host's settings are not changed");
        host.CircuitBreakerFailureThreshold.Should().Be(4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_runs_the_private_transport_with_the_hosts_request_timeout_and_the_resolved_policy(bool transportHasItsOwnPolicy)
    {
        var core = new OutboundEndpointPolicy { AllowedHosts = { "core.example" } };
        var own = new OutboundEndpointPolicy { AllowedHosts = { "own.example" } };
        await using var sp = Container(
            http: o =>
            {
                o.RequestTimeout = TimeSpan.FromSeconds(5);
                o.MaxRetryAttempts = 3;
                if (transportHasItsOwnPolicy)
                    o.OutboundEndpointPolicy = own;
            },
            core: o => o.OutboundEndpointPolicy = core,
            more: s => s.Configure<MediatorOptions>(o => o.PushTimeout = TimeSpan.FromSeconds(45)));

        var settings = DirectPushSender.Create(sp).TransportSettings;

        settings.Should().NotBeNull();
        settings!.RequestTimeout.Should().Be(TimeSpan.FromSeconds(5), "the host's, whatever the push deadline (45 s): a stall it ends first pauses the recipient's direct pushes like the deadline");
        settings.MaxRetryAttempts.Should().Be(0);
        settings.CircuitBreakerFailureThreshold.Should().Be(int.MaxValue);
        settings.OutboundEndpointPolicy.Should().BeSameAs(transportHasItsOwnPolicy ? own : core);
    }

    [Fact]
    public async Task Polly_accepts_the_breaker_that_never_opens_and_a_hundred_failures_in_a_row_each_reach_the_endpoint()
    {
        var host = new HttpTransportOptions { AllowedSchemes = new[] { "https" }, CircuitBreakerFailureThreshold = 5, MaxRetryAttempts = 3, RetryBaseDelay = TimeSpan.FromMilliseconds(1) };
        var calls = 0;
        using var sp = StubClient(_ =>
        {
            Interlocked.Increment(ref calls);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        // Building the pipeline validates MinimumThroughput = int.MaxValue against Polly's range [2, int.MaxValue].
        var transport = new HttpDidCommTransport(
            sp.GetRequiredService<IHttpClientFactory>(),
            Options.Create(DirectPushSender.CopyForDirectPush(host, new OutboundEndpointPolicy())));
        var sender = new DirectPushSender(transport, new OutboundEndpointPolicy());

        for (var i = 1; i <= 100; i++)
        {
            var delivered = await sender.SendAsync(new Uri("https://203.0.113.10/inbox"), new byte[] { 0x7b, 0x7d }, CancellationToken.None);
            delivered.Should().Be(DirectSendResult.Failed, "a 503");
            Volatile.Read(ref calls).Should().Be(i, "each attempt is one POST, and the breaker never opens (attempt {0})", i);
        }
    }

    [Fact]
    public async Task No_endpoint_and_a_relative_endpoint_are_refused_with_their_reasons()
    {
        await using var sp = Container(http: o => o.AllowedSchemes = new[] { "https" });
        var sender = DirectPushSender.Create(sp);

        var none = await sender.CheckTargetAsync(null, CancellationToken.None);
        var relative = await sender.CheckTargetAsync(new Uri("/inbox", UriKind.Relative), CancellationToken.None);

        none.Should().Be(new PushTargetCheck(PushTarget.NoEndpoint, DirectPushSender.ReasonNoEndpoint));
        relative.Should().Be(new PushTargetCheck(PushTarget.NotUsable, DirectPushSender.ReasonNotAbsolute));
    }

    public static TheoryData<string, string, string> Refused => new()
    {
        { "ws://203.0.113.10/socket", nameof(PushTarget.NotUsable), DirectPushSender.ReasonScheme },
        { "wss://203.0.113.10/socket", nameof(PushTarget.NotUsable), DirectPushSender.ReasonScheme },
        { "didcomm:transport/queue", nameof(PushTarget.NotUsable), DirectPushSender.ReasonScheme },
        { "ftp://203.0.113.10/inbox", nameof(PushTarget.NotUsable), DirectPushSender.ReasonScheme },
        { "http://203.0.113.10/inbox", nameof(PushTarget.NotUsable), DirectPushSender.ReasonTransportScheme },
        { "https://10.0.0.1/inbox", nameof(PushTarget.Refused), DirectPushSender.ReasonGuard },
        { "https://169.254.169.254/latest/meta-data", nameof(PushTarget.Refused), DirectPushSender.ReasonGuard },
        { "https://[::ffff:127.0.0.1]/inbox", nameof(PushTarget.Refused), DirectPushSender.ReasonGuard },
        { "https://127.0.0.1/inbox", nameof(PushTarget.Refused), DirectPushSender.ReasonGuard },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task An_endpoint_that_may_not_be_pushed_to_is_refused_with_a_fixed_reason(string endpoint, string verdict, string expected)
    {
        // The host allows https only (the default), and the core policy is the default one.
        await using var sp = Container(http: o => o.AllowedSchemes = new[] { "https" });
        var sender = DirectPushSender.Create(sp);

        var check = await sender.CheckTargetAsync(new Uri(endpoint), CancellationToken.None);

        check.Should().Be(new PushTargetCheck(Enum.Parse<PushTarget>(verdict), expected), endpoint);
    }

    [Theory]
    [InlineData("https://203.0.113.10/inbox")]
    [InlineData("HTTPS://203.0.113.10/inbox")]
    public async Task A_public_https_endpoint_is_accepted(string endpoint)
    {
        await using var sp = Container(http: o => o.AllowedSchemes = new[] { "https" });
        var sender = DirectPushSender.Create(sp);

        var check = await sender.CheckTargetAsync(new Uri(endpoint), CancellationToken.None);

        check.Should().Be(new PushTargetCheck(PushTarget.Accepted, string.Empty));
    }

    [Fact]
    public async Task The_check_before_the_take_uses_the_core_policy_that_SendPackedAsync_uses()
    {
        // The core policy allows 10.0.0.1; the transport's own policy (the connect-time one) is
        // stricter and does not. The pre-send check is the core one, as for DidCommClient.SendPackedAsync.
        await using var sp = Container(
            http: o => o.OutboundEndpointPolicy = new OutboundEndpointPolicy(),
            core: o => o.OutboundEndpointPolicy = new OutboundEndpointPolicy { AllowedHosts = { "10.0.0.1" } });
        var sender = DirectPushSender.Create(sp);

        (await sender.CheckTargetAsync(new Uri("https://10.0.0.1/inbox"), CancellationToken.None)).Verdict.Should().Be(PushTarget.Accepted);
        (await sender.CheckTargetAsync(new Uri("https://10.0.0.2/inbox"), CancellationToken.None)).Should().Be(new PushTargetCheck(PushTarget.Refused, DirectPushSender.ReasonGuard));
    }

    public static TheoryData<string> HostNames => new()
    {
        // A name that resolves to the loopback, one that resolves to nothing, one the lookup refuses outright.
        "https://localhost/inbox",
        "https://didcomm-direct-push-no-such-host.invalid/inbox",
        "https://" + string.Join('.', Enumerable.Repeat(new string('a', 60), 5)) + ".example/inbox",
    };

    // ADV-110-2a-2: a host name is looked up before the take (asynchronously, under the deadline), as
    // the host's own sends look it up, whatever the host's connection path.
    [Theory]
    [MemberData(nameof(HostNames))]
    public async Task A_host_name_is_looked_up_before_the_take(string endpoint)
    {
        await using var sp = Container(http: o => o.AllowedSchemes = new[] { "https" });
        var sender = DirectPushSender.Create(sp);

        var check = await sender.CheckTargetAsync(new Uri(endpoint), CancellationToken.None);

        check.Should().Be(new PushTargetCheck(PushTarget.Refused, DirectPushSender.ReasonGuard), endpoint);
    }

    // The check honours the push deadline while a name is looked up: a lookup that never completes
    // ends with the deadline.
    [Fact]
    public async Task The_name_lookup_before_the_take_ends_with_the_push_deadline()
    {
        var lookups = new SemaphoreSlim(0);
        var policy = new OutboundEndpointPolicy
        {
            ResolveDnsAsync = (_, _) =>
            {
                lookups.Release();
                return new TaskCompletionSource<IPAddress[]>().Task;
            },
        };
        await using var sp = Container(http: o => o.AllowedSchemes = new[] { "https" }, core: o => o.OutboundEndpointPolicy = policy);
        var sender = DirectPushSender.Create(sp);
        using var deadline = new CancellationTokenSource();

        var check = sender.CheckTargetAsync(new Uri("https://inbox.recipient.example/didcomm"), deadline.Token).AsTask();
        (await lookups.WaitAsync(PushWait)).Should().BeTrue("the name is looked up through the asynchronous lookup");
        await deadline.CancelAsync();
        var caught = await Record.ExceptionAsync(() => check.WaitAsync(PushWait));

        caught.Should().BeAssignableTo<OperationCanceledException>().Which.CancellationToken.Should().Be(deadline.Token);
    }

    // The real sender over UseHttpTransport()'s handler, in the real dispatcher and push step: a name
    // that resolves only to private addresses is refused by the check before the take. No POST and
    // nothing taken; as for a failed send, the recipient's direct pushes pause, and both messages wait
    // for pickup.
    [Fact]
    public async Task A_name_that_resolves_to_a_private_address_is_refused_before_the_take_with_no_POST_and_a_pause()
    {
        var trap = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        trap.Start();
        var connections = 0;
        using var stop = new CancellationTokenSource();
        var accepting = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await trap.AcceptTcpClientAsync(stop.Token);
                Interlocked.Increment(ref connections);
            }
        });
        try
        {
            await using var container = Container(http: o =>
            {
                o.AllowedSchemes = new[] { "http", "https" };
                o.MaxRetryAttempts = 3;
            });
            var sender = DirectPushSender.Create(container);
            var endpoints = new DirectEndpoints();
            await using var world = await LivePushWorld.StartAsync(null, s =>
                DirectPushRouting.Register(s, new DirectPushRoute(endpoints, new Lazy<IDirectPushSender>(() => sender))));
            var fixture = world.Fixture;
            var grant = await world.GrantAsync("connect-refused");
            endpoints.Set(grant.Peer, new Uri($"http://localhost:{((IPEndPoint)trap.LocalEndpoint).Port}/didcomm"));
            var first = await world.StoreAsync(grant, 1);
            var second = await world.StoreAsync(grant, 2);

            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await fixture.WaitForPushOutcomesAsync(1, PushWait);
            await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the refused push settled");
            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await fixture.WaitForPushOutcomesAsync(2, PushWait);
            await fixture.Dispatcher.WaitForIdleAsync(PushWait);

            fixture.PushOutcomeLog.Should().Equal(new[] { PushOutcomes.Failed, PushOutcomes.SkippedCooldown }, "refused before the take, then the pause");
            Volatile.Read(ref connections).Should().Be(0, "no connection reached the private address");
            (await world.PickupAsync(grant.Peer)).Should().BeEquivalentTo(new[] { first.MessageId, second.MessageId }, "both wait for pickup");
        }
        finally
        {
            await stop.CancelAsync();
            trap.Stop();
            await Task.WhenAny(accepting, Task.Delay(PushWait));
        }
    }

    private static readonly TimeSpan PushWait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_2xx_is_delivered_and_the_request_is_the_stored_bytes_as_an_encrypted_message_with_no_return_route()
    {
        var transport = new ScriptedTransport(_ => Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: 202)));
        var sender = new DirectPushSender(transport, new OutboundEndpointPolicy());
        var endpoint = new Uri("https://203.0.113.10/inbox");
        var payload = new byte[] { 0x7b, 0x22, 0x61, 0x22, 0x3a, 0x31, 0x7d };

        var delivered = await sender.SendAsync(endpoint, payload, CancellationToken.None);

        delivered.Should().Be(DirectSendResult.Delivered);
        var request = transport.Requests.Should().ContainSingle().Which;
        request.Endpoint.Should().BeSameAs(endpoint);
        request.Payload.ToArray().Should().Equal(payload);
        request.MediaType.Should().Be(MediaTypes.Encrypted);
        request.ReturnRoute.Should().BeNull();
        request.LocalDid.Should().BeNull();
        request.PeerDid.Should().BeNull();
        request.MaxResponseBytes.Should().BeNull("no response body is read");
    }

    // A 4xx other than 408 and 429 is the endpoint's definite "not this message" (Refused: the next
    // record goes on, no pause, ADV-110-1a-4); every other answer is Failed (the recipient's direct
    // pushes pause): a 5xx, 408 and 429 (which say "later"), and a 3xx at the end of the chain.
    [Theory]
    [InlineData(500, false)]
    [InlineData(502, false)]
    [InlineData(503, false)]
    [InlineData(408, false)]
    [InlineData(429, false)]
    [InlineData(400, true)]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(404, true)]
    [InlineData(410, true)]
    [InlineData(413, true)]
    [InlineData(422, true)]
    [InlineData(499, true)]
    [InlineData(301, false)]
    [InlineData(302, false)]
    [InlineData(303, false)]
    [InlineData(307, false)]
    [InlineData(308, false)]
    public async Task An_endpoint_that_answered_anything_but_a_2xx_is_not_delivered_a_4xx_other_than_408_and_429_is_refused_and_only_its_status_is_logged(int status, bool refused)
    {
        const string Secret = "https://203.0.113.10/capability-secret";
        const string Location = "https://203.0.113.99/location-secret";
        var transport = new ScriptedTransport(_ => throw new TransportException(
            $"HTTP POST to '{Secret}' failed with status {status}; Location '{Location}'.", httpStatusCode: status, scheme: "https"));
        var sink = new HandlerLogSink();
        var sender = new DirectPushSender(transport, new OutboundEndpointPolicy(), new HandlerCapturingLogger<DirectPushSender>(sink));

        var delivered = await sender.SendAsync(new Uri(Secret), new byte[] { 0x7b, 0x7d }, CancellationToken.None);

        delivered.Should().Be(refused ? DirectSendResult.Refused : DirectSendResult.Failed);
        transport.Requests.Should().ContainSingle();
        var line = sink.Entries.Should().ContainSingle().Which;
        line.Everything.Should().Contain(status.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .And.NotContain("capability-secret").And.NotContain("location-secret").And.NotContain("203.0.113").And.NotContain("failed with status");
    }

    public static TheoryData<string> Faults => new() { "transport-refusal", "connection", "timeout", "unexpected" };

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task Any_other_fault_propagates_and_nothing_is_logged(string fault)
    {
        Exception thrown = fault switch
        {
            // A refusal with no status: the endpoint never answered (a destination check through a proxy).
            "transport-refusal" => new TransportException("Refusing to send to 'https://203.0.113.10/x'.", httpStatusCode: null, scheme: "https"),
            "connection" => new HttpRequestException("connection refused 203.0.113.10"),
            "timeout" => new TimeoutRejectedException("timed out"),
            _ => new InvalidOperationException("unexpected"),
        };
        var transport = new ScriptedTransport(_ => throw thrown);
        var sink = new HandlerLogSink();
        var sender = new DirectPushSender(transport, new OutboundEndpointPolicy(), new HandlerCapturingLogger<DirectPushSender>(sink));

        var caught = await Record.ExceptionAsync(() => sender.SendAsync(new Uri("https://203.0.113.10/x"), new byte[] { 0x7b, 0x7d }, CancellationToken.None));

        caught.Should().BeSameAs(thrown);
        transport.Requests.Should().ContainSingle();
        sink.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_of_the_push_propagates()
    {
        using var cts = new CancellationTokenSource();
        var transport = new ScriptedTransport(async ct =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return new TransportResult(Accepted: true, HttpStatusCode: 202);
        });
        var sender = new DirectPushSender(transport, new OutboundEndpointPolicy());

        var caught = await Record.ExceptionAsync(() => sender.SendAsync(new Uri("https://203.0.113.10/x"), new byte[] { 0x7b, 0x7d }, cts.Token));

        caught.Should().BeAssignableTo<OperationCanceledException>().Which.CancellationToken.Should().Be(cts.Token);
    }

    /// <summary>How each start-up row builds the named <c>"didcomm"</c> client (QA2-2, QA2-3).</summary>
    private static Action<IServiceCollection> NamedClient(string row) => row switch
    {
        "default handler" => s => s.AddHttpClient(HttpDidCommTransport.HttpClientName),
        "default handler behind a host handler" => s => s.AddHttpClient(HttpDidCommTransport.HttpClientName).AddHttpMessageHandler(() => new PassThrough()),
        "HttpClientHandler" => s => s.AddHttpClient(HttpDidCommTransport.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()),
        "auto-redirect turned back on" => s => s.AddHttpClient(HttpDidCommTransport.HttpClientName).UseSocketsHttpHandler((h, _) => h.AllowAutoRedirect = true),
        "no connect callback" => s => s.AddHttpClient(HttpDidCommTransport.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }),
        "the environment's proxy" => s => s.AddHttpClient(HttpDidCommTransport.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, ConnectCallback = RefuseConnect }),
        "a TrustedProxy whose proxy was cleared" => s => s.AddHttpClient(HttpDidCommTransport.HttpClientName).UseSocketsHttpHandler((h, _) => h.Proxy = null),
        "a vetted SocketsHttpHandler" => s => s.AddHttpClient(HttpDidCommTransport.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectCallback = RefuseConnect }),
        "the host's own handler" => s => s.AddHttpClient(HttpDidCommTransport.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted))),
        "UseHttpTransport behind a host handler" => s => s.AddHttpClient(HttpDidCommTransport.HttpClientName).AddHttpMessageHandler(() => new PassThrough()),
        _ => _ => { },
    };

    public static TheoryData<string, bool, string> UnvettedClients => new()
    {
        // The row, whether UseHttpTransport() is called first, what the refusal names.
        { "default handler", false, "follows redirects on its own" },
        { "default handler behind a host handler", false, "follows redirects on its own" },
        { "HttpClientHandler", true, "HttpClientHandler" },
        { "auto-redirect turned back on", true, "follows redirects on its own" },
        { "no connect callback", true, "no connect callback" },
        { "the environment's proxy", true, "a proxy that is not HttpTransportOptions.TrustedProxy" },
        { "a TrustedProxy whose proxy was cleared", true, "a proxy that is not HttpTransportOptions.TrustedProxy" },
    };

    [Theory]
    [MemberData(nameof(UnvettedClients))]
    public async Task Create_refuses_a_named_client_that_would_undo_what_a_push_relies_on(string row, bool useHttpTransport, string named)
    {
        await using var sp = Container(
            http: useHttpTransport ? o => { if (row.StartsWith("a TrustedProxy", StringComparison.Ordinal)) o.TrustedProxy = new WebProxy("http://proxy.example:3128"); } : null,
            more: NamedClient(row));

        var caught = Record.Exception(() => DirectPushSender.Create(sp));

        caught.Should().BeOfType<InvalidOperationException>(row).Which.Message.Should().Contain(named, row)
            .And.Contain("UseHttpTransport()").And.Contain("Direct push").And.NotContain("://");
    }

    public static TheoryData<string, bool> VettedClients => new()
    {
        { "UseHttpTransport", true },
        { "UseHttpTransport with a TrustedProxy", true },
        { "UseHttpTransport behind a host handler", true },
        { "a vetted SocketsHttpHandler", false },
        { "the host's own handler", false },
    };

    [Theory]
    [MemberData(nameof(VettedClients))]
    public async Task Create_accepts_a_named_client_that_keeps_what_a_push_relies_on_or_is_the_hosts_own(string row, bool useHttpTransport)
    {
        await using var sp = Container(
            http: useHttpTransport ? o => { if (row.EndsWith("TrustedProxy", StringComparison.Ordinal)) o.TrustedProxy = new WebProxy("http://proxy.example:3128"); } : null,
            more: NamedClient(row));

        var caught = Record.Exception(() => DirectPushSender.Create(sp));

        caught.Should().BeNull(row);
    }

    [Fact]
    public void Create_refuses_a_container_without_an_HTTP_client_factory()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        using var sp = services.BuildServiceProvider();

        var caught = Record.Exception(() => DirectPushSender.Create(sp));

        caught.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("no HTTP client factory").And.Contain("UseHttpTransport()");
    }

    [Fact]
    public async Task Create_refuses_a_container_without_the_HTTP_transport_and_names_UseHttpTransport()
    {
        // An HTTP client factory is there (another library added one); the HTTP transport is not.
        await using var sp = Container(http: null, more: s => s.AddHttpClient(HttpDidCommTransport.HttpClientName));

        var caught = Record.Exception(() => DirectPushSender.Create(sp));

        caught.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("UseHttpTransport()")
            .And.Contain("Direct push", "it says direct push needs it").And.NotContain("://");
    }

    [Fact]
    public async Task Create_refuses_an_invalid_TrustedProxy()
    {
        // No Address: refused by the transport's own check, when the host's transport and the private one are built.
        await using var sp = Container(http: o => o.TrustedProxy = new WebProxy());

        var caught = Record.Exception(() => DirectPushSender.Create(sp));

        caught.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("TrustedProxy");
    }

    [Fact]
    public async Task Create_refuses_a_null_core_policy()
    {
        await using var sp = Container(http: _ => { }, core: o => o.OutboundEndpointPolicy = null!);

        var caught = Record.Exception(() => DirectPushSender.Create(sp));

        caught.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("DidCommOptions.OutboundEndpointPolicy");
    }

    /// <summary>
    /// A container the way a mediator host builds one: <c>AddDidComm</c> with, when
    /// <paramref name="http"/> is not <c>null</c>, <c>UseHttpTransport</c>.
    /// </summary>
    private static ServiceProvider Container(
        Action<HttpTransportOptions>? http,
        Action<DidCommOptions>? core = null,
        Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            if (http is not null)
                b.UseHttpTransport(http);
            if (core is not null)
                b.Configure(core);
        });
        more?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>A container whose named <c>"didcomm"</c> client answers every request with <paramref name="respond"/>.</summary>
    private static ServiceProvider StubClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(HttpDidCommTransport.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new StubHandler(respond));
        return services.BuildServiceProvider();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    /// <summary>A connect callback that never connects (the start-up check only looks for one).</summary>
    private static ValueTask<Stream> RefuseConnect(SocketsHttpConnectionContext context, CancellationToken ct)
        => ValueTask.FromException<Stream>(new NotSupportedException("no connections in this test"));

    /// <summary>A host's own handler in front of the primary one.</summary>
    private sealed class PassThrough : DelegatingHandler;

    /// <summary>A transport that records each request and answers with a script.</summary>
    private sealed class ScriptedTransport(Func<CancellationToken, Task<TransportResult>> answer) : IDidCommTransport
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<TransportRequest> _requests = new();

        public IReadOnlyCollection<TransportRequest> Requests => _requests;

        public string Scheme => "https";

        public bool CanHandle(Uri endpoint) => endpoint.Scheme == "https";

        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
        {
            _requests.Enqueue(request);
            return answer(ct);
        }
    }
}
