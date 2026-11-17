using System.Threading.RateLimiting;
using DidComm.Extensions.DependencyInjection;
using DidComm.Mediator;
using DidComm.Mediator.AspNetCore;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCrypto;
using NetDid.Core;

namespace DidComm.Samples.Cookbook;

/// <summary>
/// A complete mediator for one cookbook section: an ASP.NET Core app with <c>AddMediator()</c> and
/// <c>MapDidCommMediator("/didcomm")</c>, listening on a free port of 127.0.0.1. Start it with
/// <see cref="StartAsync"/> at the top of a section and dispose it at the end (<c>await using</c>).
/// </summary>
/// <remarks>
/// <para>
/// A mediator's DID names its own address, and <c>AddMediator()</c> checks that DID before the
/// server starts listening, so the port is chosen first (<see cref="LoopbackPort"/>) and the DID is
/// minted for <c>http://127.0.0.1:{port}/didcomm</c>. In production the address is fixed and known
/// up front, and it is <c>https</c> / <c>wss</c>.
/// </para>
/// <para>
/// The mediator holds its own keys in its own secrets resolver; it shares nothing with the wallets
/// that talk to it except its DID. Enrolment is open (the default <c>IMediationPolicy</c> grants any
/// authenticated peer), which is fine for an in-process demo; a mediator on a public address
/// registers an allow-list policy before <c>AddMediator()</c> (section FF).
/// </para>
/// </remarks>
public sealed class CookbookMediatorHost : IAsyncDisposable
{
    private const string Pattern = "/didcomm";

    private readonly WebApplication _app;

    private CookbookMediatorHost(WebApplication app, string did, Uri endpoint)
    {
        _app = app;
        Did = did;
        Endpoint = endpoint;
    }

    /// <summary>The mediator's routing DID (a <c>did:peer:2</c>): what a wallet enrolls with and what its own DID document points at.</summary>
    public string Did { get; }

    /// <summary>Where the mediator listens: <c>http://127.0.0.1:{port}/didcomm</c> — the address the routing DID's service names.</summary>
    public Uri Endpoint { get; }

    /// <summary>The mediator app's services, for the mediator-side calls a section makes (for example <c>MessagePickupQueue</c>).</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>
    /// Mint the mediator's DID for a free loopback port and start the mediator there, trying another
    /// port if a different program takes the first one.
    /// </summary>
    /// <param name="ctx">The cookbook context; its DID tools mint the mediator's DID.</param>
    /// <returns>The running mediator.</returns>
    public static Task<CookbookMediatorHost> StartAsync(CookbookContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return LoopbackPort.StartAsync(port => StartOnPortAsync(ctx, port));
    }

    private static async Task<CookbookMediatorHost> StartOnPortAsync(CookbookContext ctx, int port)
    {
        var endpoint = new Uri($"http://127.0.0.1:{port}{Pattern}");

        // The routing DID: a did:peer:2 whose DIDCommMessaging service is this endpoint. Its private
        // keys go into the mediator's own secrets resolver and nowhere else.
        var identity = await PeerIdentityFactory.CreateAsync(
            ctx.ServiceProvider.GetRequiredService<IDidManager>(),
            ctx.ServiceProvider.GetRequiredService<IKeyGenerator>(),
            ctx.ServiceProvider.GetRequiredService<ICryptoProvider>(),
            new DidCommServiceSpec(endpoint.AbsoluteUri, Accept: ["didcomm/v2"]));
        var secrets = new InMemorySecretsResolver();
        foreach (var key in identity.Privates)
            secrets.Add(key);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); // keep the cookbook's output to the narration
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddDidComm(b => b
            .UseNetDidResolver()
            .UseSecretsResolver(secrets)
            .AddMediator(m => m.CoordinateMediation.RoutingDids.Add(identity.Did)));

        // Anyone who can reach a mediator can post to it, so it sits behind a rate limiter with one
        // window per client address. On the WebSocket endpoint a permit is a connection, not a
        // message. The permits here are generous; size yours to your traffic.
        builder.Services.AddRateLimiter(o => o.AddPolicy("mediator", http =>
            RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 1000, Window = TimeSpan.FromSeconds(1) })));

        var app = builder.Build();
        try
        {
            app.UseWebSockets(); // the mediator's WebSocket endpoint needs it (live delivery, section EE)
            app.UseRateLimiter();
            app.MapDidCommMediator(Pattern).RequireRateLimiting("mediator");
            await app.StartAsync();
            return new CookbookMediatorHost(app, identity.Did, endpoint);
        }
        catch
        {
            // LoopbackPort tries another port when this one was taken: give back what was built.
            await app.DisposeAsync();
            throw;
        }
    }

    /// <summary>Stop the mediator and release its port.</summary>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
