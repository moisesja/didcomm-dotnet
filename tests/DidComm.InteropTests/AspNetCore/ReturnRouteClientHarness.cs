using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Secrets;
using DidComm.Transports;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// The #92 client side of the return-route tests: a DI container built the way an application would
/// (<c>AddDidComm</c>, did:peer resolution, <c>UseHttpTransport</c>) whose facade holds the secrets of
/// one or more of the <see cref="ReturnRouteTestHost"/>'s peers, shares the host's injected clock
/// (so its FR-RR-09 stamps are what the server judges) and writes to the host's capturing logger (so
/// <see cref="ReturnRouteTestHost.LeakingLogLines"/> scans the client too). The HTTP transport's
/// primary handler is the TestServer's by default, or a stub a test supplies.
/// </summary>
internal sealed class ReturnRouteClientHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private ReturnRouteClientHarness(ServiceProvider services, ReturnRouteTestHost host)
    {
        _services = services;
        Host = host;
        Client = services.GetRequiredService<DidCommClient>();
    }

    public ReturnRouteTestHost Host { get; }

    /// <summary>The client's facade (from its own container).</summary>
    public DidCommClient Client { get; }

    public IServiceProvider Services => _services;

    /// <summary>The server's registry-aware HTTP endpoint.</summary>
    public Uri Endpoint => new(Host.Server.BaseAddress, "/didcomm");

    /// <summary>
    /// Build a client holding the secrets of <paramref name="holders"/> (Alice when none is given).
    /// </summary>
    /// <param name="host">The server under test.</param>
    /// <param name="primaryHandler">The HTTP transport's primary handler; <c>null</c> sends to the TestServer.</param>
    /// <param name="configureHttp">Extra HTTP transport options.</param>
    /// <param name="holders">The peers whose private keys the client holds.</param>
    public static ReturnRouteClientHarness Create(
        ReturnRouteTestHost host,
        Func<HttpMessageHandler>? primaryHandler = null,
        Action<HttpTransportOptions>? configureHttp = null,
        params TestPeer[] holders)
    {
        var peers = holders.Length == 0 ? new[] { host.Alice } : holders;
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(host.Logs);
        });
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new CompositeSecrets(peers.Select(p => (ISecretsResolver)p.Secrets).ToArray()));
            b.UseHttpTransport(o =>
            {
                o.AllowedSchemes = new[] { "http", "https" };
                o.MaxRetryAttempts = 0;
                o.CircuitBreakerFailureThreshold = 100;
                configureHttp?.Invoke(o);
            });
            b.Configure(o => o.Clock = () => host.Clock.Now);
        });
        // Registered after UseHttpTransport, so this primary handler replaces its socket handler.
        services.AddHttpClient(HttpDidCommTransport.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(_ => primaryHandler?.Invoke() ?? host.Server.CreateHandler());
        return new ReturnRouteClientHarness(services.BuildServiceProvider(), host);
    }

    /// <summary>Send options from <paramref name="from"/> to the server, delivered directly to <see cref="Endpoint"/>.</summary>
    public SendOptions ToServer(TestPeer? from = null)
        => new(Recipients: new[] { Host.ServerDid }, From: (from ?? Host.Alice).Did, ServiceEndpointOverride: Endpoint);

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    /// <summary>Several peers' secrets as one resolver (the client holds Alice's and Carol's keys).</summary>
    private sealed class CompositeSecrets : ISecretsResolver
    {
        private readonly ISecretsResolver[] _inner;

        public CompositeSecrets(ISecretsResolver[] inner) => _inner = inner;

        public async Task<DataProofsDotnet.Jose.Jwk?> FindAsync(string kid, CancellationToken ct = default)
        {
            foreach (var resolver in _inner)
            {
                if (await resolver.FindAsync(kid, ct) is { } found)
                    return found;
            }
            return null;
        }

        public async Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
        {
            var asked = kids.ToList();
            var present = new List<string>();
            foreach (var resolver in _inner)
                present.AddRange(await resolver.FindPresentAsync(asked, ct));
            return present.Distinct(StringComparer.Ordinal).ToList();
        }
    }
}
