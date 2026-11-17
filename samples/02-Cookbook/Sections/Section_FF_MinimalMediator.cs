using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator;
using DidComm.Mediator.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.Secrets;
using DidComm.TestSupport;
using DidComm.Threading;
using DidComm.Transports;
using DidComm.Transports.Http;
using DidComm.Transports.ReturnRoute;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetCrypto;
using NetDid.Core;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Section FF: a complete mediator — Coordinate Mediation, the routing mediator, Message Pickup,
/// live delivery — is <c>AddMediator()</c> plus <c>MapDidCommMediator(...)</c>. Then the same roles
/// composed by hand, with a container and without one, and the replay protection every mediator
/// runs, with the clock rule it implies. The mediator references the <c>DidComm.Mediator</c> and
/// <c>DidComm.Mediator.AspNetCore</c> packages; a wallet references <c>DidComm.Mediator.Client</c>
/// and a transport.
/// Maps to PRD §14.2 task FF (FR-MED-01..08, FR-PICK-01..10, FR-ROUTE-09..11, FR-RR-08).
/// </summary>
public static class Section_FF_MinimalMediator
{
    // Every wait in this section is on the event the next line reads, with this hang guard.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("FF", "Run a minimal mediator in 20 lines");

        // The wallets that try the mediators below: Bob (on the allow-list) and Carol (not), plus
        // Alice, who sends Bob a message. Each holds only its own keys.
        var carol = await MintAsync(ctx, service: null);
        var walletSecrets = new InMemorySecretsResolver(ctx.Bob.Privates.Concat(carol.Privates));
        await using var wallet = BuildHttpAgent(walletSecrets, b => b.AddMediatorClient());
        await using var alice = BuildHttpAgent(new InMemorySecretsResolver(ctx.Alice.Privates), more: null);

        await RunTwentyLinesAsync(ctx, wallet, carol);
        await RunWithContainerAsync(ctx, wallet, walletSecrets, alice);
        await RunWithoutContainerAsync(ctx);
        RunReplayGuard(ctx);
    }

    // ── FF.1 The 20 lines ─────────────────────────────────────────────────────────────────────────
    private static async Task RunTwentyLinesAsync(CookbookContext ctx, IServiceProvider wallet, PeerIdentity carol)
    {
        var narrator = ctx.Narrator;
        narrator.Step("FF.1 A whole mediator: AddMediator() + MapDidCommMediator(...) + rate limiting + an allow-list.");
        var secrets = new InMemorySecretsResolver();

        // A mediator's DID names its own address, and AddMediator() checks that DID before the
        // server listens, so the port must be known first: LoopbackPort finds a free one. In
        // production the address is your fixed public https:// one and none of this is needed.
        var (app, mediatorDid) = await LoopbackPort.StartAsync(async port =>
        {
            var url = $"http://127.0.0.1:{port}"; // loopback http for this demo; production is https
            var did = await MintMediatorAsync(ctx, secrets, $"{url}/didcomm");

            // ── the mediator ───────────────────────────────────────────────────────────────────
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders(); // keep this cookbook's console to the narration
            builder.WebHost.UseUrls(url);
            builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("mediator", w =>
            {
                w.PermitLimit = 100;
                w.Window = TimeSpan.FromSeconds(1);
            }));
            builder.Services.AddSingleton<IMediationPolicy>(new AllowListMediationPolicy(ctx.Bob.Did));
            builder.Services.AddDidComm(b => b
                .UseNetDidResolver()
                .UseSecretsResolver(secrets)
                .AddMediator(o =>
                {
                    o.CoordinateMediation.RoutingDids.Add(did);
                    o.Replay.ClockSkew = TimeSpan.FromMinutes(1); // the default, shown: see FF.3
                }));
            var mediator = builder.Build();
            mediator.UseWebSockets(); // before mapping, or the WebSocket endpoint answers 400
            mediator.UseRateLimiter();
            mediator.MapDidCommMediator("/didcomm").RequireRateLimiting("mediator");
            // ───────────────────────────────────────────────────────────────────────────────────

            try
            {
                await mediator.StartAsync();
            }
            catch
            {
                await mediator.DisposeAsync(); // LoopbackPort retries with a new port
                throw;
            }
            return (mediator, did);
        });

        try
        {
            narrator.Value("[mediator] routing DID", Trunc(mediatorDid));

            var mediation = wallet.GetRequiredService<MediationClient>();
            var bobGrant = await mediation.RequestMediationAsync(mediatorDid, ctx.Bob.Did);
            narrator.Value("[bob] granted (on the allow-list)", bobGrant is not null);
            var carolGrant = await mediation.RequestMediationAsync(mediatorDid, carol.Did);
            narrator.Value("[carol] granted", carolGrant is not null);
            narrator.Note("[carol] mediate-deny: not on the allow-list");

            narrator.Note("Put rate limiting in front: anyone can post forwards, and a flood from freshly made DIDs fills the replay protection budget, which then refuses in-scope messages for everyone for a few minutes.");
            narrator.Note("Enrolment is open by default (anyone who can authcrypt a mediate-request gets a grant, and grants never expire): register an allow-list or rate-limiting IMediationPolicy before AddMediator().");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        // AddMediator() relays every forward through its own pipeline (queue first, then live push),
        // so it refuses a forward sink of the host's own, at registration, before anything is added.
        var withSink = new ServiceCollection();
        withSink.AddSingleton<IForwardSink, QueueingForwardSink>();
        try
        {
            withSink.AddDidComm(b => b.AddMediator(o => o.CoordinateMediation.RoutingDids.Add(mediatorDid)));
            narrator.Value("AddMediator() with a host IForwardSink", "accepted");
        }
        catch (InvalidOperationException ex)
        {
            narrator.Value("AddMediator() with a host IForwardSink", "refused");
            narrator.Value("Refusal", ex.Message);
        }
        narrator.Note("AddMediator() refuses a host IForwardSink: use AddRoutingMediator() for that (FF.2 does).");
    }

    // ── FF.2 Compose the roles yourself: (a) with a container ─────────────────────────────────────
    private static async Task RunWithContainerAsync(
        CookbookContext ctx, IServiceProvider wallet, InMemorySecretsResolver walletSecrets, IServiceProvider alice)
    {
        var narrator = ctx.Narrator;
        narrator.Step("FF.2 (a) The roles à la carte: Coordinate Mediation, the routing mediator, Message Pickup, and a forward sink of your own.");
        var secrets = new InMemorySecretsResolver();
        var deliveries = new DeliveryWatch();

        var (app, mediatorDid) = await LoopbackPort.StartAsync(async port =>
        {
            var url = $"http://127.0.0.1:{port}";
            var did = await MintMediatorAsync(ctx, secrets, $"{url}/didcomm");

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(url);
            // Your own collaborators go in first: the roles register theirs only if none is there.
            builder.Services.AddSingleton(deliveries);
            builder.Services.AddSingleton<IForwardSink, QueueingForwardSink>();
            builder.Services.AddSingleton<IMediationPolicy>(new AllowListMediationPolicy(ctx.Bob.Did));
            builder.Services.AddDidComm(b => b
                .UseNetDidResolver()
                .UseSecretsResolver(secrets)
                // Coordinate Mediation first: its grant store becomes the routing mediator's
                // directory of recipients, so a forward is relayed exactly when its next is a DID
                // some peer registered...
                .AddCoordinateMediation(o =>
                {
                    o.RoutingDids.Add(did);              // the DID a grant names (the first is the primary one)
                    o.MaxGrants = 100;                   // bounds of the in-memory grant store
                    o.MaxRecipientsPerGrant = 16;
                    o.MaxRecipients = 1_000;
                    o.MaxPageSize = 50;                  // one recipient-query page
                    o.MaxUpdatesPerMessage = 64;         // entries in one recipient-update
                    o.UseAsForwardRecipientRegistry = true; // the default; false when you bring your own directory
                })
                // ...then the routing mediator, which hands each relayed forward to your sink...
                .AddRoutingMediator()
                // ...and the mailbox your sink fills.
                .AddMessagePickup(o =>
                {
                    o.MaxDeliveryBatch = 10;                    // messages in one delivery
                    o.MaxDeliveryBytes = 512 * 1024;            // one packed delivery; what your recipients accept
                    o.MaxAckIds = 100;                          // ids in one messages-received
                    o.LeaseTimeout = TimeSpan.FromSeconds(60);  // a delivered message waits this long for its acknowledgement
                    o.MaxQueueMessages = 100;                   // per recipient...
                    o.MaxQueueBytes = 8L * 1024 * 1024;
                    o.MaxStoreMessages = 5_000;                 // ...and for everyone together
                    o.MaxStoreBytes = 128L * 1024 * 1024;
                    o.MaxMessageAge = TimeSpan.FromDays(30);    // the mailbox age limit
                })
                // Discover Features tells wallets which roles you serve (AddMediator() adds it itself).
                .AddBuiltInProtocols());
            var mediator = builder.Build();
            mediator.MapDidCommEndpoint("/didcomm"); // the registry-aware endpoint dispatches to the roles
            try
            {
                await mediator.StartAsync();
            }
            catch
            {
                await mediator.DisposeAsync();
                throw;
            }
            return (mediator, did);
        });

        try
        {
            // The roles the container built, as Discover Features discloses them.
            foreach (var handler in app.Services.GetServices<IProtocolHandler>())
            {
                if (handler is IProtocolHandlerRoles withRoles)
                    narrator.Value($"Role served for {handler.ProtocolUri}", string.Join(", ", withRoles.Roles));
            }

            // Bob enrols and registers a public DID routed through this mediator (section CC).
            var mediation = wallet.GetRequiredService<MediationClient>();
            var grant = await mediation.RequestMediationAsync(mediatorDid, ctx.Bob.Did)
                ?? throw new InvalidOperationException("The mediator denied mediation.");
            var bobPublic = await MintAsync(ctx, new DidCommServiceSpec(grant.RoutingDids[0]));
            // Added while nothing is being unpacked: this sample key store is add-then-read only.
            foreach (var jwk in bobPublic.Privates) walletSecrets.Add(jwk);
            await mediation.UpdateRecipientsAsync(mediatorDid, ctx.Bob.Did,
                new[] { new RecipientUpdate(bobPublic.Did, RecipientUpdateAction.Add) });

            // Alice writes to Bob's public DID; SendAsync wraps it in a forward for the mediator.
            var note = new MessageBuilder()
                .WithType("https://didcomm.org/basicmessage/2.0/message")
                .WithFrom(ctx.Alice.Did)
                .WithTo(bobPublic.Did)
                .WithBody(new JsonObject { ["content"] = "Hello Bob, via your mailbox" })
                .Build();
            var sent = await alice.GetRequiredService<DidCommClient>()
                .SendAsync(note, new SendOptions(new[] { bobPublic.Did }, ctx.Alice.Did));
            narrator.Value("[alice] mediator answered HTTP", sent.Transport.HttpStatusCode);

            // The routing mediator answered 202 first, then handed the forward to the sink.
            var delivered = await AwaitOrFailAsync(deliveries.Delivered, "the forward sink");
            narrator.Value("[sink] MessagePickupQueue.DeliverAsync", delivered.Outcome);

            var status = await wallet.GetRequiredService<PickupClient>().GetStatusAsync(mediatorDid, ctx.Bob.Did);
            narrator.Value("[bob] messages waiting", status.MessageCount);
            narrator.Note("Live push to a waiting WebSocket is AddMediator()'s pipeline: composed this way, recipients pick up (section DD).");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    // ── FF.2 Compose the roles yourself: (b) without a container ──────────────────────────────────
    private static async Task RunWithoutContainerAsync(CookbookContext ctx)
    {
        var narrator = ctx.Narrator;
        narrator.Step("FF.2 (b) The same roles without a container: build the stores, the queue and the handlers, and dispatch in-process.");

        // The mediator's identity. Nothing crosses a network here, so its DID needs no address.
        var mediatorSecrets = new InMemorySecretsResolver();
        var mediator = await MintAsync(ctx, service: null);
        foreach (var jwk in mediator.Privates) mediatorSecrets.Add(jwk);
        var didCommOptions = new DidCommOptions();
        var mediatorClient = new DidCommClient(
            mediatorSecrets, ctx.ServiceProvider.GetRequiredService<IDidKeyService>(), didCommOptions);

        // Settings: check them before you use them (the roles check again when they are built).
        var mediationOptions = new CoordinateMediationOptions { MaxGrants = 100, MaxPageSize = 50 };
        mediationOptions.Validate();
        var pickupOptions = new MessagePickupOptions { MaxDeliveryBatch = 10, MaxQueueMessages = 100 };
        pickupOptions.Validate();

        var mediationStore = new InMemoryMediationStore();            // default bounds (1 000 grants)
        var messageStore = new InMemoryMessageStore(pickupOptions);   // quotas from the pickup settings
        using var sessions = new InMemoryReturnRouteSessionRegistry(); // live sessions: none in-process
        var queue = new MessagePickupQueue(
            messageStore, mediationStore, sessions,
            Options.Create(pickupOptions), Options.Create(didCommOptions),
            NullLogger<MessagePickupQueue>.Instance);
        var pickupHandler = new MessagePickupHandler(queue, NullLogger<MessagePickupHandler>.Instance);
        var mediationHandler = new CoordinateMediationHandler(
            mediationStore,
            new DefaultMediationPolicy(),                       // grants everyone: fine in-process, not in public
            new MediatorIdentity(new List<string> { mediator.Did }),
            Options.Create(mediationOptions),
            NullLogger<CoordinateMediationHandler>.Instance);
        narrator.Value("CoordinateMediationHandler.Roles", string.Join(", ", mediationHandler.Roles));
        narrator.Value("MessagePickupHandler.Roles", string.Join(", ", pickupHandler.Roles));

        var registry = new ProtocolHandlerRegistry();
        registry.Register(mediationHandler);
        registry.Register(pickupHandler);
        await using var dispatcher = new ProtocolDispatcher(registry, new InMemoryThreadStateStore());

        // Bob asks; the mediator unpacks, dispatches and packs the reply back to him. A transport
        // would carry both envelopes; here they stay in memory.
        async Task<Message> AskAsync(Message request)
        {
            var packed = (await ctx.Client.PackEncryptedAsync(request,
                new PackEncryptedOptions(Recipients: new[] { mediator.Did }, From: ctx.Bob.Did))).Message;
            var outcome = await dispatcher.DispatchAsync(await mediatorClient.UnpackAsync(packed), mediatorClient, didCommOptions);
            var reply = outcome.Reply ?? throw new InvalidOperationException($"No reply ({outcome.Result}).");
            var replyPacked = (await mediatorClient.PackEncryptedAsync(reply,
                new PackEncryptedOptions(Recipients: new[] { ctx.Bob.Did }, From: mediator.Did))).Message;
            return (await ctx.Client.UnpackAsync(replyPacked)).Message;
        }

        var grantMessage = await AskAsync(CoordinateMediation.CreateMediateRequest(ctx.Bob.Did, mediator.Did));
        var granted = CoordinateMediation.TryParseGrant(grantMessage, out var routingDids);
        narrator.Value("[bob] mediate-request answered with a grant", granted);
        narrator.Value("[bob] grant names the mediator", granted && routingDids![0] == mediator.Did);

        var statusMessage = await AskAsync(MessagePickup.CreateStatusRequest(ctx.Bob.Did, mediator.Did));
        narrator.Value("[bob] status-request answered: messages waiting",
            MessagePickup.TryParseStatus(statusMessage, out var status) ? status.MessageCount : -1);
    }

    // ── FF.3 Replay protection and clocks ────────────────────────────────────────────────────────
    private static void RunReplayGuard(CookbookContext ctx)
    {
        var narrator = ctx.Narrator;
        narrator.Step("FF.3 Replay protection: every authenticated message id is admitted once, and its timestamp must be recent.");

        // A mediator (and a wallet reading a live connection) admits each in-scope message once per
        // sender and message id, and only while its created_time is fresh. These are the defaults.
        var replay = new ReplayProtectionOptions
        {
            Window = TimeSpan.FromMinutes(5),     // how old a message may be
            ClockSkew = TimeSpan.FromMinutes(1),  // how far ahead of our clock a sender's clock may run
            RequireCreatedTime = true,            // a message without created_time is refused
            MaxEntries = 32_768,                  // ids remembered in total (fail closed when full)...
            MaxEntriesPerPeer = 1024,             // ...and per sender, so one peer cannot starve the rest
            MaxMessageIdLength = 512,
        };
        replay.Validate();
        narrator.Note("Two hosts' clocks must agree within a minute by default (ClockSkew): a message stamped more than that ahead of the receiver's clock is refused, silently. Keep your hosts on NTP.");

        IInboundReplayGuard guard = new InMemoryReplayGuard(replay);
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero); // the receiver's clock reading
        var stamped = now.ToUnixTimeSeconds();
        var sender = ctx.Alice.Did;
        narrator.Value("msg-1, first time", guard.TryAdmit(sender, "msg-1", stamped, now));
        narrator.Value("msg-1, again", guard.TryAdmit(sender, "msg-1", stamped, now));
        narrator.Value("msg-2, sender clock 30 s ahead", guard.TryAdmit(sender, "msg-2", stamped + 30, now));
        narrator.Value("msg-3, sender clock 2 min ahead", guard.TryAdmit(sender, "msg-3", stamped + 120, now));
        narrator.Value("msg-4, stamped 10 min ago", guard.TryAdmit(sender, "msg-4", stamped - 600, now));
        narrator.Value("msg-5, no created_time", guard.TryAdmit(sender, "msg-5", null, now));
        narrator.Value("an id longer than MaxMessageIdLength", guard.TryAdmit(sender, new string('m', 600), stamped, now));
        if (guard is InMemoryReplayGuard counted)
            narrator.Value("Remembered ids / senders", $"{counted.Count} / {counted.PeerCount}");

        // A tiny guard shows the two failure counters an operator watches (the mediator's purge
        // schedule logs their growth). Pass your logger: those refusals are logged as warnings.
        var tiny = new InMemoryReplayGuard(
            new ReplayProtectionOptions { MaxEntries = 4, MaxEntriesPerPeer = 1 },
            NullLogger<InMemoryReplayGuard>.Instance);
        tiny.TryAdmit(sender, "a", stamped, now);
        narrator.Value("Second id from the same sender, per-sender budget 1", tiny.TryAdmit(sender, "b", stamped, now));
        var stepBack = now - TimeSpan.FromMinutes(7); // the host clock stepped back more than Window + ClockSkew
        narrator.Value("Message stamped by a clock that stepped back 7 min", tiny.TryAdmit(ctx.Bob.Did, "c", stepBack.ToUnixTimeSeconds(), stepBack));
        narrator.Value("SaturationCount / ClockBehindRefusalCount", $"{tiny.SaturationCount} / {tiny.ClockBehindRefusalCount}");

        // Which messages are in scope: authcrypted ones that ask for a return route, and every
        // mediator-service request; a forward is exempt (each copy is relayed again).
        narrator.Value("Mediator-service protocols", MediatorServiceProtocols.All.Count);
        narrator.Value("status-request is a mediator-service type",
            MediatorServiceProtocols.IsMediatorServiceType("https://didcomm.org/messagepickup/3.0/status-request"));
        narrator.Value("forward is exempt",
            MediatorServiceProtocols.IsReplayExempt("https://didcomm.org/routing/2.0/forward"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A DIDComm container that sends over HTTP to loopback only.</summary>
    private static ServiceProvider BuildHttpAgent(ISecretsResolver secrets, Action<DidCommBuilder>? more)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver()
                .UseSecretsResolver(secrets)
                // Plain http is for this loopback demo only; production endpoints are https (the default).
                .UseHttpTransport(o =>
                {
                    o.AllowedSchemes = new[] { "http", "https" };
                    o.MaxRetryAttempts = 0;
                })
                // The SSRF guard refuses loopback by default: open exactly 127.0.0.1 and nothing else.
                .Configure(o => o.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1"));
            more?.Invoke(b);
        });
        return services.BuildServiceProvider();
    }

    private static async Task<string> MintMediatorAsync(CookbookContext ctx, InMemorySecretsResolver secrets, string endpoint)
    {
        var mediator = await MintAsync(ctx, new DidCommServiceSpec(endpoint, Accept: new[] { "didcomm/v2" }));
        foreach (var jwk in mediator.Privates) secrets.Add(jwk);
        return mediator.Did;
    }

    private static Task<PeerIdentity> MintAsync(CookbookContext ctx, DidCommServiceSpec? service)
        => PeerIdentityFactory.CreateAsync(
            ctx.ServiceProvider.GetRequiredService<IDidManager>(),
            ctx.ServiceProvider.GetRequiredService<IKeyGenerator>(),
            ctx.ServiceProvider.GetRequiredService<ICryptoProvider>(),
            service);

    private static async Task<T> AwaitOrFailAsync<T>(Task<T> task, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(HangGuard)) != task)
            throw new TimeoutException($"Timed out waiting for {what}.");
        return await task;
    }

    private static string Trunc(string value) => value.Length <= 56 ? value : value[..55] + "…";

    // A DID URL (did:…#key-1, did:…?query, did:…/path) reduced to its DID subject.
    private static string DidSubject(string did)
    {
        var end = did.IndexOfAny(new[] { '#', '?', '/' });
        return end < 0 ? did : did[..end];
    }

    /// <summary>
    /// Grants mediation only to the DIDs on the operator's list, which is fixed at start-up (nothing
    /// from the network is ever added to it). Everything else is the default policy's.
    /// </summary>
    private sealed class AllowListMediationPolicy(params string[] allowed) : IMediationPolicy
    {
        private readonly string[] _allowed = allowed.Select(DidSubject).ToArray();
        private readonly DefaultMediationPolicy _inner = new();

        public ValueTask<MediationDecision> OnMediateRequestAsync(string peerDid, ProtocolContext context, CancellationToken ct)
        {
            // One pass over the whole list, with no early exit and an ordinal comparison of DID
            // subjects, so the time taken does not tell a caller where (or whether) it matched.
            var subject = DidSubject(peerDid);
            var listed = false;
            foreach (var did in _allowed)
                listed |= string.Equals(did, subject, StringComparison.Ordinal);
            return listed ? _inner.OnMediateRequestAsync(peerDid, context, ct) : ValueTask.FromResult(MediationDecision.Deny);
        }

        // The protocol asks for no proof of ownership, so the first peer to register a DID gets it.
        public ValueTask<bool> MayRegisterAsync(string peerDid, string recipientDid, ProtocolContext context, CancellationToken ct)
            => _inner.MayRegisterAsync(peerDid, recipientDid, context, ct);
    }

    /// <summary>Lets the section wait for the sink's first delivery.</summary>
    private sealed class DeliveryWatch
    {
        private readonly TaskCompletionSource<PickupDeliveryResult> _delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PickupDeliveryResult> Delivered => _delivered.Task;

        public void Report(PickupDeliveryResult result) => _delivered.TrySetResult(result);
    }

    /// <summary>
    /// A host forward sink: puts each relayed message in the recipient's pickup queue. It may take
    /// the queue in its constructor (the queue depends on no handler, dispatcher or client).
    /// </summary>
    private sealed class QueueingForwardSink(MessagePickupQueue queue, DeliveryWatch watch) : IForwardSink
    {
        public async Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct)
        {
            // Store only (AllowLive: false): a live push packs for the recipient, which resolves its
            // DID, and that must not hold up the forwards behind it. AddMediator() pushes from
            // background workers instead.
            var delivered = await queue.DeliverAsync(
                DidSubject(result.NextHop),
                result.OnwardPacked,
                new PickupDeliveryOptions(result.NotBefore, result.ExpiresTime, AllowLive: false),
                ct);
            watch.Report(delivered);
        }
    }
}
