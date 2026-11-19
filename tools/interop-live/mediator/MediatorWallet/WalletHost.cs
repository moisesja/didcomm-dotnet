using System.Net;
using System.Text.Json.Nodes;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Resolution;
using DidComm.TestSupport;
using DidComm.Transports.Http;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediatorWallet;

/// <summary>
/// The options every command takes:
/// <list type="bullet">
/// <item><c>--allow-hosts h1,h2</c>: hosts the outbound guard lets through although they are
/// loopback or private (<c>OutboundEndpointPolicy.AllowedHosts</c>).</item>
/// <item><c>--trusted-proxy U</c>: <c>TrustedProxy</c> on both transports (HTTP and WebSocket).</item>
/// <item><c>--resolve-dns-names true|false</c>: <c>OutboundEndpointPolicy.ResolveDnsNames</c>
/// (default true; false where only the proxy can resolve the mediator's name).</item>
/// <item><c>--expect-env-proxy U</c>: precondition (exit 3) that the process proxy
/// (<see cref="HttpClient.DefaultProxy"/>, from <c>HTTP_PROXY</c> and friends) would send every
/// mediator address this command uses to <c>U</c>, so a direct success proves the library ignored
/// it.</item>
/// <item><c>--relax-created-time</c>: <c>ReplayProtectionOptions.RequireCreatedTime = false</c>, so
/// unstamped answers on a WebSocket are not dropped.</item>
/// <item><c>--request-timeout s</c>: <c>MediatorClientOptions.RequestTimeout</c> (default 20).</item>
/// <item><c>--log-out F</c>: every log record as a JSON line (template and named values).</item>
/// </list>
/// </summary>
internal sealed class CommonOptions
{
    public required IReadOnlyList<string> AllowHosts { get; init; }
    public Uri? TrustedProxy { get; init; }
    public bool ResolveDnsNames { get; init; } = true;
    public Uri? ExpectEnvProxy { get; init; }
    public bool RelaxCreatedTime { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public string? LogOut { get; init; }
    public CoordinateMediationVersion Version { get; init; } = CoordinateMediationVersion.Negotiate;

    public static CommonOptions From(CommandLine line)
    {
        var pin = line.Get("pin") switch
        {
            null => CoordinateMediationVersion.Negotiate,
            "V2" => CoordinateMediationVersion.V2,
            "V3" => CoordinateMediationVersion.V3,
            var other => throw new UsageException($"Unknown --pin '{other}' (V2|V3)."),
        };
        return new CommonOptions
        {
            AllowHosts = (line.Get("allow-hosts") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            TrustedProxy = line.Get("trusted-proxy") is { } proxy ? new Uri(proxy, UriKind.Absolute) : null,
            ResolveDnsNames = line.Bool("resolve-dns-names", true),
            ExpectEnvProxy = line.Get("expect-env-proxy") is { } env ? new Uri(env, UriKind.Absolute) : null,
            RelaxCreatedTime = line.Flag("relax-created-time"),
            RequestTimeout = TimeSpan.FromSeconds(line.Int("request-timeout", 20)),
            LogOut = line.Get("log-out"),
            Version = pin,
        };
    }

    public JsonObject Describe() => new()
    {
        ["allow_hosts"] = new JsonArray(AllowHosts.Select(h => (JsonNode?)h).ToArray()),
        ["trusted_proxy"] = TrustedProxy?.AbsoluteUri,
        ["resolve_dns_names"] = ResolveDnsNames,
        ["expect_env_proxy"] = ExpectEnvProxy?.AbsoluteUri,
        ["relax_created_time"] = RelaxCreatedTime,
        ["request_timeout_s"] = RequestTimeout.TotalSeconds,
        ["coordinate_mediation_version"] = Version.ToString(),
    };
}

/// <summary>
/// The wallet's container, as an application builds it: the net-did resolver, the HTTP transport
/// and the WebSocket transport (receiving, with the inbound dispatcher, so a WebSocket route works),
/// <c>AddMediatorClient()</c>, and the common options applied. Every log record goes to the
/// capture.
/// </summary>
internal sealed class WalletHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private WalletHost(ServiceProvider services, CaptureLoggerProvider capture)
    {
        _services = services;
        Capture = capture;
    }

    public CaptureLoggerProvider Capture { get; }
    public IServiceProvider Services => _services;
    public DidCommClient DidComm => _services.GetRequiredService<DidCommClient>();
    public MediationClient Mediation => _services.GetRequiredService<MediationClient>();
    public PickupClient Pickup => _services.GetRequiredService<PickupClient>();

    public static WalletHost Create(CommonOptions common, InMemorySecretsResolver secrets, CaptureLoggerProvider capture, TimeSpan? requestTimeout = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(capture);
        });
        services.AddDidComm(b => b
            .UseNetDidResolver()
            .UseSecretsResolver(secrets)
            .UseHttpTransport(o =>
            {
                o.AllowedSchemes = new[] { "http", "https" };
                o.MaxRetryAttempts = 0;
                if (common.TrustedProxy is { } proxy)
                    o.TrustedProxy = new WebProxy(proxy);
            })
            .UseWebSocketTransport(o =>
            {
                o.AllowedSchemes = new[] { "ws", "wss" };
                o.ReceiveInbound = true;
                o.MaxReconnectAttempts = 0;
                if (common.TrustedProxy is { } proxy)
                    o.TrustedProxy = new WebProxy(proxy);
            })
            .UseInboundDispatch()
            .AddMediatorClient(o =>
            {
                o.RequestTimeout = requestTimeout ?? common.RequestTimeout;
                o.CoordinateMediationVersion = common.Version;
            })
            .Configure(o =>
            {
                foreach (var host in common.AllowHosts)
                    o.OutboundEndpointPolicy.AllowedHosts.Add(host);
                o.OutboundEndpointPolicy.ResolveDnsNames = common.ResolveDnsNames;
            }));
        if (common.RelaxCreatedTime)
            services.Configure<ReplayProtectionOptions>(o => o.RequireCreatedTime = false);
        return new WalletHost(services.BuildServiceProvider(), capture);
    }

    /// <summary>The mediator's <c>DIDCommMessaging</c> addresses, in document order.</summary>
    public async Task<IReadOnlyList<string>> ResolveEndpointsAsync(string mediatorDid, CancellationToken ct)
    {
        var services = await _services.GetRequiredService<IServiceEndpointResolver>().ResolveAsync(mediatorDid, ct).ConfigureAwait(false);
        return services.Select(s => s.Uri).ToArray();
    }

    public static string? First(IEnumerable<string> endpoints, params string[] schemes)
        => endpoints.FirstOrDefault(e => Uri.TryCreate(e, UriKind.Absolute, out var u) && schemes.Contains(u.Scheme, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The live form of <c>ProcessProxyScope.AssertProxies</c>: the process proxy must send each
    /// destination (and, for a ws(s) address, the http(s) address the WebSocket handshake is made to)
    /// to <paramref name="proxy"/> and not bypass it; otherwise the cell would prove nothing.
    /// </summary>
    public static JsonObject AssertEnvProxy(Uri proxy, IEnumerable<Uri> destinations)
    {
        var current = HttpClient.DefaultProxy;
        var checkedDestinations = new JsonArray();
        foreach (var destination in destinations.SelectMany(Handshake).Distinct())
        {
            var bypassed = current.IsBypassed(destination);
            var chosen = bypassed ? null : current.GetProxy(destination);
            checkedDestinations.Add(new JsonObject { ["destination"] = destination.AbsoluteUri, ["bypassed"] = bypassed, ["proxy"] = chosen?.AbsoluteUri });
            if (bypassed || chosen is null || !string.Equals(chosen.Authority, proxy.Authority, StringComparison.OrdinalIgnoreCase))
            {
                throw new PreconditionException(
                    $"Precondition failed: the process proxy does not send '{destination}' to '{proxy}' (bypassed: {bypassed}, proxy: {chosen?.AbsoluteUri ?? "none"}). The cell would prove nothing.");
            }
        }

        var variables = new JsonArray();
        foreach (var name in new[] { "http_proxy", "HTTP_PROXY", "https_proxy", "HTTPS_PROXY", "all_proxy", "ALL_PROXY", "no_proxy", "NO_PROXY" })
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                variables.Add(name);
        }
        return new JsonObject { ["proxy"] = proxy.AbsoluteUri, ["variables_set"] = variables, ["destinations"] = checkedDestinations };

        static IEnumerable<Uri> Handshake(Uri destination)
        {
            yield return destination;
            if (destination.Scheme is "ws" or "wss")
                yield return new UriBuilder(destination) { Scheme = destination.Scheme == "ws" ? "http" : "https" }.Uri;
        }
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
