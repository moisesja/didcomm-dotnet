using System.Diagnostics.Metrics;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator;
using DidComm.Mediator.AspNetCore;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCrypto;
using NetDid.Core;

// The static OutOfBand API class shares its name with its namespace: alias it.
using OutOfBandApi = DidComm.Protocols.OutOfBand.OutOfBand;

namespace DidComm.Samples.MediatedAgent;

/// <summary>
/// The mediator: an ASP.NET Core app with <c>AddMediator()</c> and <c>MapDidCommMediator()</c>, on a
/// loopback port, with the operator's own collaborators (an allow-list policy, a mediation store it
/// can inspect, a session registry it can watch, the replay guard whose counters it reads).
/// </summary>
internal sealed class MediatorHost : IAsyncDisposable
{
    /// <summary>The path the mediator's endpoints are mapped under.</summary>
    public const string Pattern = "/didcomm";

    /// <summary>The rate-limiter policy in front of every mediator endpoint.</summary>
    public const string RateLimitPolicy = "mediator";

    private MediatorHost(WebApplication app, MediatorIdentity identity, int port, AllowListMediationPolicy policy, InMemoryMediationStore mediationStore)
    {
        App = app;
        Identity = identity;
        Did = identity.RoutingDids[0];
        Policy = policy;
        MediationStore = mediationStore;
        HttpEndpoint = new Uri($"http://127.0.0.1:{port}{Pattern}");
        WebSocketEndpoint = new Uri($"ws://127.0.0.1:{port}{Pattern}");
        InvitationUrl = OutOfBandApi.ToShortUrl($"http://127.0.0.1:{port}{Pattern}/oob", MediatorEndpointRouteBuilderExtensions.InvitationOobId);
        Sessions = app.Services.GetRequiredService<ObservedSessionRegistry>();
        ReplayGuard = app.Services.GetRequiredService<InMemoryReplayGuard>();
        Queue = app.Services.GetRequiredService<MessagePickupQueue>();
        MessageStore = app.Services.GetRequiredService<IMessageStore>();
        Client = app.Services.GetRequiredService<DidCommClient>();
        StoredForwards = new StoredForwardsWatcher(app.Services.GetRequiredService<IMeterFactory>());
    }

    public WebApplication App { get; }

    /// <summary>The mediator's routing DID: its <c>DIDCommMessaging</c> service names <see cref="HttpEndpoint"/>.</summary>
    public string Did { get; }

    public Uri HttpEndpoint { get; }

    public Uri WebSocketEndpoint { get; }

    /// <summary>The invitation URL the operator publishes (a QR code, a web page).</summary>
    public string InvitationUrl { get; }

    /// <summary>The identity registered for the mediator: its routing DIDs, the first one primary.</summary>
    public MediatorIdentity Identity { get; }

    public AllowListMediationPolicy Policy { get; }

    public InMemoryMediationStore MediationStore { get; }

    public ObservedSessionRegistry Sessions { get; }

    public InMemoryReplayGuard ReplayGuard { get; }

    public MessagePickupQueue Queue { get; }

    public IMessageStore MessageStore { get; }

    public DidCommClient Client { get; }

    public StoredForwardsWatcher StoredForwards { get; }

    /// <summary>
    /// Every mediator setting, in one place. It only assigns values: <c>AddMediator()</c> may run it
    /// more than once, on different instances.
    /// </summary>
    /// <param name="o">The settings to fill in.</param>
    public static void ConfigureMediator(MediatorOptions o)
    {
        // Coordinate Mediation: how many peers it serves, and how many DIDs each may register.
        o.CoordinateMediation.MaxGrants = 100;
        o.CoordinateMediation.MaxRecipientsPerGrant = 16;

        // Message Pickup: each recipient's mailbox. A recipient offline longer than the age limit
        // loses those messages, and nobody is told.
        o.Pickup.MaxQueueMessages = 50;
        o.Pickup.MaxMessageAge = TimeSpan.FromDays(7);

        // Replay protection. The clocks of the mediator and its peers must agree within ClockSkew
        // (a minute by default), or their requests are refused.
        o.Replay.ClockSkew = TimeSpan.FromMinutes(1);
        o.Replay.MaxEntries = 8192;

        // Live connections: how many one peer may hold and how many in all, how many pushes one may
        // have pending, and how many open/close notifications wait for a slow subscriber.
        o.Sessions.MaxSessionsPerPeer = 4;
        o.Sessions.MaxSessions = 1024;
        o.Sessions.MaxPendingPushes = 16;
        o.Sessions.MaxPendingNotifications = 256;

        // Forwards are relayed as they came (no re-wrapping); a sender may ask for up to 30 minutes
        // of delay.
        o.RewrapMode = RewrapMode.PassThrough;
        o.MaxForwardDelay = TimeSpan.FromMinutes(30);

        // Expired messages are removed every PurgeInterval; live pushes run on PushConcurrency
        // workers, each push given up to PushTimeout.
        o.PurgeInterval = TimeSpan.FromMinutes(5);
        o.PushConcurrency = 4;
        o.PushTimeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Build and start the mediator on a free loopback port. Its DID names its own address, and
    /// <c>AddMediator()</c> checks that DID before the server listens, so the port is chosen first
    /// (<see cref="LoopbackPort"/>); in production the address is fixed and known in advance.
    /// </summary>
    /// <param name="settings">Checked settings (<see cref="ConfigureMediator"/>); the mediation store is built from their Coordinate Mediation part.</param>
    public static Task<MediatorHost> StartAsync(MediatorOptions settings)
        => LoopbackPort.StartAsync(port => StartOnPortAsync(port, settings));

    private static async Task<MediatorHost> StartOnPortAsync(int port, MediatorOptions settings)
    {
        // The mediator's own DID: did:peer:2 carries its service in the DID string, so it resolves
        // offline. Its DIDCommMessaging service names the mediator's own address.
        var secrets = new InMemorySecretsResolver();
        var did = await MintAsync(secrets, new Uri($"http://127.0.0.1:{port}{Pattern}")).ConfigureAwait(false);

        // Collaborators the operator wants a handle on, registered BEFORE AddDidComm/AddMediator:
        // every mediator registration is a TryAdd, so these win.
        var policy = new AllowListMediationPolicy(new DefaultMediationPolicy());
        // A store you register keeps the limits you build it with (here: the checked settings').
        var mediationStore = new InMemoryMediationStore(settings.CoordinateMediation);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); // keep the console to the narration
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var services = builder.Services;
        var identity = new MediatorIdentity(did);
        services.AddSingleton<IMediatorIdentity>(identity);
        services.AddSingleton<IMediationStore>(mediationStore);
        services.AddSingleton<IMediationPolicy>(policy);
        // The replay guard the library would register anyway, registered here so the operator can
        // read its counters. It reads IOptions<ReplayProtectionOptions>, so MediatorOptions.Replay
        // still reaches it.
        services.AddSingleton(sp => new InMemoryReplayGuard(
            sp.GetRequiredService<IOptions<ReplayProtectionOptions>>().Value,
            sp.GetService<ILogger<InMemoryReplayGuard>>()));
        services.AddSingleton<IInboundReplayGuard>(sp => sp.GetRequiredService<InMemoryReplayGuard>());
        // The session registry, wrapped so the operator can watch it. Built from
        // IOptions<ReturnRouteSessionOptions>, so MediatorOptions.Sessions still reaches it; the
        // container built it, so the container disposes it.
        services.AddSingleton(sp => new ObservedSessionRegistry(new InMemoryReturnRouteSessionRegistry(
            sp.GetRequiredService<IOptions<ReturnRouteSessionOptions>>().Value,
            sp.GetService<ILogger<InMemoryReturnRouteSessionRegistry>>())));
        services.AddSingleton<IReturnRouteSessionRegistry>(sp => sp.GetRequiredService<ObservedSessionRegistry>());

        services.AddDidComm(b => b
            .UseNetDidResolver()
            .UseSecretsResolver(secrets)
            .AddMediator(ConfigureMediator));

        // Anyone who can reach the endpoints can post forwards and ask for mediation: put a rate
        // limit in front. On the WebSocket endpoint it counts connections, not messages. This one
        // window is shared by every caller; a public mediator would also limit per client.
        services.AddRateLimiter(o => o.AddFixedWindowLimiter(RateLimitPolicy, w =>
        {
            w.PermitLimit = 100;
            w.Window = TimeSpan.FromSeconds(1);
        }));

        var app = builder.Build();
        try
        {
            app.UseWebSockets(); // before mapping, or the WebSocket endpoint answers 400
            app.UseRateLimiter();
            // POST /didcomm (HTTP), GET /didcomm (WebSocket), GET /didcomm/oob (the invitation).
            app.MapDidCommMediator(Pattern).RequireRateLimiting(RateLimitPolicy);

            // Start-up checks the routing DID (it resolves, names a plain address, and its
            // key-agreement key is in the secrets) before the server listens, then builds the
            // invitation.
            await app.StartAsync().ConfigureAwait(false);
            return new MediatorHost(app, identity, port, policy, mediationStore);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<string> MintAsync(InMemorySecretsResolver secrets, Uri endpoint)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(secrets));
        await using var keyTools = services.BuildServiceProvider();
        var service = DidCommServiceBuilder.ForEndpoint(endpoint)
            .WithId("#didcomm-1")
            .WithAccept("didcomm/v2")
            .Build();
        var identity = await PeerIdentityFactory.CreateWithServiceEntryAsync(
            keyTools.GetRequiredService<IDidManager>(),
            keyTools.GetRequiredService<IKeyGenerator>(),
            keyTools.GetRequiredService<ICryptoProvider>(),
            service).ConfigureAwait(false);
        foreach (var key in identity.Privates)
            secrets.Add(key);
        return identity.Did;
    }

    public async ValueTask DisposeAsync()
    {
        StoredForwards.Dispose();
        await App.StopAsync().ConfigureAwait(false);
        await App.DisposeAsync().ConfigureAwait(false);
    }
}
