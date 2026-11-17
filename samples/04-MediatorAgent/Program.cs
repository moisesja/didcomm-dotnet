using System.Diagnostics.Metrics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DidComm.AspNetCore;
using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.Threading;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using DidComm.Transports.ReturnRoute;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCrypto;
using NetDid.Core;

namespace DidComm.Samples.MediatorAgent;

/// <summary>
/// Mediated routing over HTTP, end to end (PRD §14.3 sample 04): an ASP.NET Core mediator runs the
/// library's routing role (<c>AddRoutingMediator()</c> behind the registry-aware
/// <c>MapDidCommEndpoint</c>) and relays each forward to the recipient's own HTTP inbox through a
/// host-written <see cref="IForwardSink"/>, while a console flow routes Alice → Mediator → Bob.
/// Bob's <c>did:peer:2</c> names the mediator as its route, so Alice's <c>SendAsync</c> discovers the
/// route, wraps the forward and posts it without any routing code of her own. Also shows the
/// outbound-endpoint SSRF guard refusing the loopback mediator by default and the narrow opt-in a
/// local demo needs, a Discover Features answer returned on the same request, a registered
/// recipient without an inbox, a forward for an unregistered DID, and the same relay composed
/// without a DI container.
/// <see cref="Main"/> is the CLI; <see cref="RunAsync"/> is the testable seam invoked by the
/// InteropTests smoke test (FR-DX-02: dynamic ports, loopback only, no fixed timing).
/// </summary>
public static class Program
{
    /// <summary>The hang guard on every wait: a broken run fails loudly instead of hanging.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>CLI entry point — writes to <see cref="Console.Out"/> and exits 0 on success.</summary>
    public static async Task<int> Main()
    {
        try
        {
            await RunAsync(Console.Out).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MediatorAgent failed: {ex}");
            return 1;
        }
    }

    /// <summary>Run the whole flow, writing the narration to <paramref name="output"/>.</summary>
    /// <param name="output">Destination for narrator output. <c>null</c> uses <see cref="Console.Out"/>.</param>
    public static async Task RunAsync(TextWriter? output = null)
    {
        var narrator = output is null ? new Narrator() : new Narrator(output);

        // Each party owns its own keys — separate secrets resolvers, exactly like separate
        // processes would have. did:peer:2 resolves offline, so no party ever needs another's
        // private material, only its DID.
        var mediatorSecrets = new InMemorySecretsResolver();
        var bobSecrets = new InMemorySecretsResolver();
        var carolSecrets = new InMemorySecretsResolver();
        var aliceSecrets = new InMemorySecretsResolver();

        // ── 1. The mediator: the library's routing role on a dynamic loopback port ───────────
        narrator.Section("1", "Start the mediator — the library's routing role (AddRoutingMediator)");

        // How the routing role relays. PassThrough hands the inner envelope on untouched (the
        // default); ReanoncryptToNext re-wraps it for the next hop (FR-ROUTE-06); extra routing
        // keys add wraps a recipient set up out of band (FR-ROUTE-05). MaxDelay caps how long a
        // sender's delay_milli may ask the mediator to hold a message (FR-ROUTE-10).
        var routingOptions = new ForwardProcessorOptions(RewrapMode.PassThrough) { MaxDelay = TimeSpan.FromMinutes(5) };
        // AddRoutingMediator() validates too; calling Validate() in your own configuration code
        // fails sooner, with the setting's name.
        routingOptions.Validate();
        narrator.Value("Rewrap mode", routingOptions.Mode);
        narrator.Value("Extra routing wraps", routingOptions.ExtraRecipientRoutingKeys?.Count ?? 0);
        narrator.Value("Longest hold a sender may ask for", routingOptions.MaxDelay);
        try
        {
            (routingOptions with { MaxDelay = TimeSpan.FromMilliseconds(-1) }).Validate();
            narrator.Note("UNEXPECTED: a negative MaxDelay was accepted.");
        }
        catch (ArgumentException)
        {
            narrator.Step("A negative MaxDelay is refused by Validate(), as designed.");
        }

        var mediatorBuilder = WebApplication.CreateBuilder();
        mediatorBuilder.Logging.ClearProviders(); // keep the sample's console output to the narration
        mediatorBuilder.WebHost.UseUrls("http://127.0.0.1:0"); // port 0 = let the OS pick a free one

        // The host's own relay (InboxRelaySink.cs). Register it BEFORE AddRoutingMediator(): every
        // registration that method makes is "add if missing", so this sink replaces the library's
        // default NullForwardSink. The concrete type is registered too, so the host can fill its
        // inbox directory and read what it relayed.
        mediatorBuilder.Services.AddSingleton<InboxRelaySink>();
        mediatorBuilder.Services.AddSingleton<IForwardSink>(sp => sp.GetRequiredService<InboxRelaySink>());

        mediatorBuilder.Services.AddDidComm(b => b
            .UseNetDidResolver()
            .UseSecretsResolver(mediatorSecrets)
            // The relay sends onward over HTTP. Loopback plaintext http is a demo-only choice —
            // production endpoints are https (the transport's default AllowedSchemes).
            .UseHttpTransport(o =>
            {
                o.AllowedSchemes = new[] { "http", "https" };
                o.MaxRetryAttempts = 0;
            })
            .Configure(o =>
            {
                // The SSRF guard (section 4) also applies to the relay: a recipient's inbox URL is
                // data someone else chose. This process deliberately talks to loopback, so it opens
                // exactly that host and nothing else.
                o.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1");
                // Some in-the-wild DID documents still write serviceEndpoint as a bare string
                // instead of the spec's object form. Accepting that is a receive-side tolerance
                // you must opt into — this mediator keeps the spec-conformant stance (DD-10).
                o.AllowBareStringServiceEndpoint = false;
            })
            // Trust Ping, Discover Features and the other built-in protocols, so a sender can ask
            // this agent what it does (section 5).
            .AddBuiltInProtocols()
            // The routing role: the forward handler, its recipient registry and the sink above.
            .AddRoutingMediator(routingOptions));

        var mediatorApp = mediatorBuilder.Build();
        // The registry-aware endpoint: it unpacks each envelope and dispatches it to the
        // registered protocol handlers. A forward goes to the routing role's handler, which checks
        // it, lets the endpoint answer 202 and only then hands it to the sink (FR-ROUTE-11).
        mediatorApp.MapDidCommEndpoint("/didcomm");

        // The routing role counts every forward it drops on the didcomm.forward.outcomes counter
        // (meter "didcomm-dotnet", tag didcomm.forward.outcome). An operator would export it with
        // OpenTelemetry; this sample listens in-process to show the drop in section 7. It listens
        // only to this host's meters (the host's IMeterFactory is the meter's scope).
        var meterFactory = mediatorApp.Services.GetRequiredService<IMeterFactory>();
        var droppedUnknown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var outcomeListener = new MeterListener();
        outcomeListener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, meterFactory) && instrument.Name == "didcomm.forward.outcomes")
                listener.EnableMeasurementEvents(instrument);
        };
        outcomeListener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "didcomm.forward.outcome" && tag.Value is "Dropped-Unknown")
                    droppedUnknown.TrySetResult();
            }
        });
        outcomeListener.Start();

        // The protocols that declare a role are the ones Discover Features discloses with one.
        foreach (var handler in mediatorApp.Services.GetServices<IProtocolHandler>())
        {
            if (handler is IProtocolHandlerRoles withRoles)
                narrator.Value($"Role declared for {handler.ProtocolUri}", string.Join(", ", withRoles.Roles));
        }

        await mediatorApp.StartAsync();
        var mediatorInbox = new Uri(mediatorApp.Urls.Single() + "/didcomm");
        narrator.Value("Mediator inbox", mediatorInbox);

        // The relay and the recipient registry are singletons of the mediator's container.
        var relay = mediatorApp.Services.GetRequiredService<InboxRelaySink>();
        var recipients = mediatorApp.Services.GetRequiredService<InMemoryForwardRecipientRegistry>();

        try
        {
            // ── 2. Identities: Bob's and Carol's DIDs carry the mediator as their route ──────
            narrator.Section("2", "Mint identities — Bob's and Carol's did:peer name the mediator as their route");

            var manager = mediatorApp.Services.GetRequiredService<IDidManager>();
            var keyGen = mediatorApp.Services.GetRequiredService<IKeyGenerator>();
            var crypto = mediatorApp.Services.GetRequiredService<ICryptoProvider>();

            var mediator = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
            foreach (var key in mediator.Privates) mediatorSecrets.Add(key);
            narrator.Step($"Minted mediator = {Trunc(mediator.Did, 64)}");

            // The mediator's X25519 key-agreement kid is the routing key: senders add one forward
            // layer encrypted to this key (FR-ROUTE-02).
            var mediatorRoutingKid = mediator.Privates
                .First(k => string.Equals(k.Crv, "X25519", StringComparison.Ordinal)).Kid
                ?? throw new InvalidOperationException("Mediator X25519 key has no kid.");

            // Bob's and Carol's DIDs embed a DIDCommMessaging service: "reach me at the mediator's
            // inbox, and wrap for this routing key first". Everything a sender needs to route to
            // them travels inside the DID string — no directory lookup, no side channel.
            var routeViaMediator = new DidCommServiceSpec(
                EndpointUriOrDid: mediatorInbox.ToString(),
                RoutingKeys: new[] { mediatorRoutingKid },
                Accept: new[] { "didcomm/v2" });
            var bob = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto, routeViaMediator);
            foreach (var key in bob.Privates) bobSecrets.Add(key);
            var carol = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto, routeViaMediator);
            foreach (var key in carol.Privates) carolSecrets.Add(key);
            narrator.Step($"Minted bob = {Trunc(bob.Did, 64)}");
            narrator.Step($"Minted carol = {Trunc(carol.Did, 64)}");
            narrator.Value("Their routingKeys[0]", Trunc(mediatorRoutingKid, 64));

            // Who the mediator relays for. The routing role silently drops every forward whose
            // `next` is not registered (FR-ROUTE-11). In a full mediator, Coordinate Mediation
            // fills this in when a recipient enrolls; here the host registers both by hand.
            recipients.Register(bob.Did);
            recipients.Register(carol.Did);
            narrator.Value("[mediator] registered recipients", recipients.Count);
            // What the forward handler asks for every forward, keyed by bare DID.
            IForwardRecipientRegistry lookup = recipients;
            narrator.Value("[mediator] relays for Bob", await lookup.IsRegisteredAsync(bob.Did, CancellationToken.None));
            narrator.Value("[mediator] relays for Carol", await lookup.IsRegisteredAsync(carol.Did, CancellationToken.None));

            // ── 3. Bob: his own ASP.NET Core inbox on a second dynamic port ──────────────
            narrator.Section("3", "Start Bob's agent (his own inbox) and give the mediator's relay his address");

            var bobDeliveries = Channel.CreateBounded<(UnpackResult Unpacked, bool HadSession)>(8);
            var bobBuilder = WebApplication.CreateBuilder();
            bobBuilder.Logging.ClearProviders();
            bobBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            bobBuilder.Services.AddDidComm(b => b
                .UseNetDidResolver()
                .UseSecretsResolver(bobSecrets)
                // Telling the client which DIDs are "us" lets unpack report whether the message
                // was actually addressed to this agent (FR-CONSIST-04, advisory).
                .Configure(o => o.OwnIdentifiers = new[] { bob.Did }));

            var bobApp = bobBuilder.Build();
            // The callback overload that can answer: it receives each message with its
            // return-route session and may return a reply to send back on the same request. A
            // session exists only when an authenticated sender asked for a reply (return_route);
            // a message relayed through a mediator never has one, so Bob answers nothing here.
            bobApp.MapDidCommEndpoint("/didcomm", (unpacked, session, _) =>
            {
                bobDeliveries.Writer.TryWrite((unpacked, session is not null));
                return Task.FromResult<Message?>(null);
            });
            await bobApp.StartAsync();
            try
            {
                var bobInbox = new Uri(bobApp.Urls.Single() + "/didcomm");
                narrator.Value("Bob inbox (known only to the mediator)", bobInbox);

                // The relay's own directory: Bob has an inbox; Carol does not (she would collect
                // her messages some other way). The library's built-in push to a recipient's
                // endpoint is a planned feature (#110); until then this directory is the host's.
                narrator.Value("[mediator] Bob's inbox added to the relay's directory", relay.TryAddInbox(bob.Did, bobInbox));
                narrator.Value("[mediator] inboxes in the directory", $"{relay.InboxCount} of {InboxRelaySink.MaxInboxes}");

                // ── 4. Alice: the SSRF guard ─────────────────────────────────────────────
                narrator.Section("4", "The outbound-endpoint guard — why the default refuses this demo");

                // Alice discovers the mediator's inbox URL from BOB'S DID document — data a
                // counterparty chose. A malicious DID could point that endpoint at a cloud
                // metadata service or an internal host, turning Alice's agent into an SSRF
                // proxy. DidCommOptions.OutboundEndpointPolicy therefore blocks private,
                // loopback, link-local, and metadata destinations by default; this sample runs
                // entirely on 127.0.0.1, so the default policy MUST refuse it.
                var alice = await MintAliceAsync(aliceSecrets);

                var strictAlice = BuildAliceClient(aliceSecrets, allowLoopback: false);
                await using (strictAlice.Provider)
                {
                    try
                    {
                        await strictAlice.Client.SendAsync(
                            Hello(alice.Did, bob.Did, "This must not leave the guard."),
                            new SendOptions(Recipients: new[] { bob.Did }, From: alice.Did));
                        narrator.Note("UNEXPECTED: the default policy did not refuse the loopback endpoint.");
                    }
                    catch (TransportException ex)
                    {
                        narrator.Step("Default policy refused, as designed:");
                        narrator.Note(ex.Message);
                    }

                    // The guard is not only in the facade: the HTTP transport checks the address
                    // again when it opens the connection, so code that goes around SendAsync and
                    // hands bytes straight to the transport router is refused as well.
                    // (Any bytes do: the connection is refused before anything is sent.)
                    var router = strictAlice.Provider.GetRequiredService<ITransportRouter>();
                    try
                    {
                        await router.SendAsync(
                            new TransportRequest(mediatorInbox, Encoding.UTF8.GetBytes("{}"), MediaTypes.Encrypted),
                            CancellationToken.None);
                        narrator.Note("UNEXPECTED: the transport connected to a loopback address under the default policy.");
                    }
                    catch (Exception ex) when (ex is TransportException || ex.InnerException is TransportException)
                    {
                        // The HTTP client reports a refused connection as HttpRequestException,
                        // with the guard's TransportException inside.
                        var refusal = ex as TransportException ?? (TransportException)ex.InnerException!;
                        narrator.Step("The transport's own connection check refused it too:");
                        narrator.Note(refusal.Message);
                    }
                }

                // The opt-in is explicit and narrow: allow exactly this loopback host, keep the
                // rest of the policy intact. A private-network deployment would allowlist its
                // known mediator hosts the same way rather than disabling the guard wholesale.
                var routedAlice = BuildAliceClient(aliceSecrets, allowLoopback: true);
                await using (routedAlice.Provider)
                {
                    // ── 5. Alice asks the mediator what it does ─────────────────────────────
                    narrator.Section("5", "Alice asks the mediator what it does — Discover Features, answered on the same request");

                    // What Alice's resolver reads out of Bob's DID before any bytes move: the
                    // DIDCommMessaging service — endpoint URI, the routing keys to wrap for, and
                    // the accepted profiles. This record IS the route.
                    var serviceInfo = (await routedAlice.Provider
                        .GetRequiredService<IServiceEndpointResolver>()
                        .ResolveAsync(bob.Did)).Single();
                    narrator.Value("Bob's service endpoint (from his DID)", serviceInfo.Uri);
                    narrator.Value("Bob's routingKeys to wrap for", serviceInfo.RoutingKeys.Count);
                    var mediatorDid = InboxRelaySink.DidSubjectOf(serviceInfo.RoutingKeys[0])
                        ?? throw new InvalidOperationException("Bob's routing key is not a DID URL.");

                    // Ask the mediator Bob named which routing protocol it serves, and in which
                    // role. With ReturnRoute = All the answer comes back as the HTTP response to
                    // this very request (FR-RR-07), so Alice needs no inbox of her own. The
                    // mediator answers only because Alice authcrypts (From is set): a reply rides
                    // back only to an authenticated sender that asked for it, and only to a
                    // directly connected agent — never through a mediator.
                    var query = DiscoverFeatures.CreateQuery(alice.Did, mediatorDid, new FeatureQuery
                    {
                        FeatureType = DiscoverFeatures.FeatureTypeProtocol,
                        Match = "https://didcomm.org/routing/*",
                    });
                    var askOptions = new SendOptions(
                        Recipients: new[] { mediatorDid },
                        From: alice.Did,
                        ServiceEndpointOverride: new Uri(serviceInfo.Uri))
                    {
                        ReturnRoute = ReturnRouteMode.All,
                    };
                    narrator.Value("return_route", askOptions.ReturnRoute);
                    narrator.Value("return_route_thread", askOptions.ReturnRouteThread ?? "<none: only the thread mode names one>");

                    var asked = await routedAlice.Client.SendAsync(query, askOptions);
                    narrator.Value("Answer arrived as HTTP", asked.Transport.HttpStatusCode);
                    // Returned has passed the identity rule: authcrypted by the DID Alice asked,
                    // to Alice. Correlate it with the question on its thid.
                    var answer = asked.Returned
                        ?? throw new InvalidOperationException("The mediator returned no answer on the request.");
                    narrator.Value("Answer type", answer.Message.Type);
                    narrator.Value("Answer authenticated", answer.Authenticated);
                    narrator.Value("Answer threads to the question", answer.Message.Thid == query.Id);
                    foreach (var disclosure in DiscoverFeatures.ReadDisclosures(answer.Message))
                    {
                        var roles = disclosure.Roles is { Count: > 0 } r ? string.Join(", ", r) : "<none>";
                        narrator.Value($"Disclosed {disclosure.Id} roles", roles);
                    }

                    // ── 6. Alice → Mediator → Bob ───────────────────────────────────────────
                    narrator.Section("6", "Alice sends — route discovery, forward wrapping, 202, then the relay to Bob's inbox");

                    // One call does all of it: resolve Bob's DIDCommMessaging service, wrap a
                    // Routing 2.0 forward for the mediator's routing key (FR-ROUTE-02), pick the
                    // HTTP transport by URI scheme (FR-TRN-01), and POST to the mediator.
                    var sent = await routedAlice.Client.SendAsync(
                        Hello(alice.Did, bob.Did, "Routed through the mediator."),
                        new SendOptions(Recipients: new[] { bob.Did }, From: alice.Did));
                    narrator.Value("Endpoint used (the mediator, from Bob's DID)", sent.EndpointUsed);
                    narrator.Value("Transport HTTP status", sent.Transport.HttpStatusCode);

                    // The mediator answered 202 as soon as it had checked the forward; its relay
                    // runs afterwards, in the background. Wait for the relay's own record.
                    var relayed = await NextRecordAsync(relay, "the mediator's relay to Bob");
                    NarrateRecord(narrator, relayed, "the 202 was on the wire");

                    var delivered = await AwaitOrFailAsync(bobDeliveries.Reader.ReadAsync().AsTask(), "delivery to Bob");
                    narrator.Step("[bob] unpacked the relayed envelope as if no mediator had been involved:");
                    NarrateDelivery(narrator, delivered.Unpacked, delivered.HadSession, alice.Did);
                    narrator.Note("The mediator saw only an anoncrypt forward addressed to its routing key — never the content, never Alice's authenticated identity.");

                    // ── 7. Carol: registered without an inbox, then unregistered ──────────────
                    narrator.Section("7", "Carol — registered without an inbox here, then unregistered");

                    await routedAlice.Client.SendAsync(
                        Hello(alice.Did, carol.Did, "Hello Carol."),
                        new SendOptions(Recipients: new[] { carol.Did }, From: alice.Did));
                    var noInbox = await NextRecordAsync(relay, "the mediator's relay for Carol");
                    narrator.Step($"[mediator] Carol is registered but has no inbox here: outcome = {noInbox.Outcome} (handed to the library's NullForwardSink, which drops it and logs the message id).");

                    // Carol leaves this mediator. From now on the forward handler drops her
                    // forwards itself, before any sink runs, and still answers the sender 202:
                    // the response must not tell a stranger who is registered (FR-ROUTE-11).
                    narrator.Value("[mediator] Carol unregistered", recipients.Unregister(carol.Did));
                    narrator.Value("[mediator] registered recipients", recipients.Count);
                    narrator.Value("[mediator] relays for Carol", await lookup.IsRegisteredAsync(carol.Did, CancellationToken.None));

                    var toUnregistered = await routedAlice.Client.SendAsync(
                        Hello(alice.Did, carol.Did, "Hello again, Carol."),
                        new SendOptions(Recipients: new[] { carol.Did }, From: alice.Did));
                    narrator.Value("Transport HTTP status for the unregistered DID", toUnregistered.Transport.HttpStatusCode);
                    await AwaitOrFailAsync(droppedUnknown.Task, "the forward handler's drop of the unregistered DID");
                    narrator.Step("[mediator] the forward handler dropped it before any sink: didcomm.forward.outcome = Dropped-Unknown.");

                    // ── 8. The same relay, composed by hand ───────────────────────────────────
                    await RunWithoutContainerAsync(
                        narrator, mediatorApp.Services, routingOptions, routedAlice.Client,
                        alice, mediator, bob, bobInbox, bobDeliveries.Reader);
                }

                // Teardown: Bob leaves the mediator too.
                narrator.Section("9", "Teardown");
                narrator.Value("[mediator] Bob unregistered", recipients.Unregister(bob.Did));
                narrator.Value("[mediator] registered recipients", recipients.Count);
            }
            finally
            {
                await bobApp.StopAsync();
                await bobApp.DisposeAsync();
            }
        }
        finally
        {
            await mediatorApp.StopAsync();
            await mediatorApp.DisposeAsync();
        }
    }

    /// <summary>
    /// Section 8: the same routing role without a DI container — every piece built with its public
    /// constructor and driven through <see cref="ProtocolDispatcher"/> directly, as a host with its
    /// own transport loop would.
    /// </summary>
    private static async Task RunWithoutContainerAsync(
        Narrator narrator,
        IServiceProvider mediatorServices,
        ForwardProcessorOptions routingOptions,
        DidCommClient aliceClient,
        PeerIdentity alice,
        PeerIdentity mediator,
        PeerIdentity bob,
        Uri bobInbox,
        ChannelReader<(UnpackResult Unpacked, bool HadSession)> bobDeliveries)
    {
        narrator.Section("8", "The same relay without a DI container");

        // The facade (keys, DID resolution, HTTP transport) comes from wherever your app builds
        // it — here the mediator's own, so the same keys decrypt. Everything below is a plain
        // constructor.
        var facade = mediatorServices.GetRequiredService<DidCommClient>();
        var keyService = mediatorServices.GetRequiredService<IDidKeyService>();
        var didCommOptions = mediatorServices.GetRequiredService<IOptions<DidCommOptions>>().Value;

        // Extra routing keys per recipient (FR-ROUTE-05). The static provider gives every
        // recipient the same list; null means "no extra wraps". A host whose recipients each set
        // up their own keys implements IRecipientRoutingKeyProvider over its own store.
        IRecipientRoutingKeyProvider routingKeys = new StaticRecipientRoutingKeyProvider(null);
        narrator.Value("Extra routing wraps for Bob", (await routingKeys.GetAsync(bob.Did, CancellationToken.None)).Count);

        var processor = new ForwardProcessor(facade, keyService, routingOptions, routingKeys, didCommOptions);
        var registry = new InMemoryForwardRecipientRegistry();
        registry.Register(bob.Did);
        var sink = new InboxRelaySink(facade);
        sink.TryAddInbox(bob.Did, bobInbox);
        var handler = new ForwardHandler(processor, sink, registry);
        var handlers = new ProtocolHandlerRegistry();
        handlers.Register(handler);
        using var dispatcher = new ProtocolDispatcher(handlers, new InMemoryThreadStateStore());
        narrator.Step($"Composed: ForwardProcessor + InMemoryForwardRecipientRegistry ({registry.Count} recipient) + InboxRelaySink → ForwardHandler → ProtocolDispatcher.");

        // A forward packed by hand, as SendAsync does for Alice: her authcrypt envelope for Bob
        // rides as the attachment of a Routing 2.0 forward, anoncrypted to the mediator.
        var first = await HandPackForwardAsync(aliceClient, alice, mediator, bob, "Relayed by the hand-built mediator.");
        var unpacked = await facade.UnpackAsync(first);

        // Pre-screen: "is this a Routing 2.0 forward at all?" — false for any other message type.
        if (ForwardMessage.TryParse(unpacked.Message, out var next, out var payloads))
            narrator.Step($"[mediator] verified forward — next={Trunc(next, 48)}, payloads={payloads.Count}.");

        // What the handler's processor will produce, shown by running it yourself: the next hop,
        // the onward bytes and the forward's own scheduling hints.
        var preview = await processor.ProcessAsync(unpacked);
        narrator.Value("[mediator] next hop", Trunc(preview.NextHop, 48));
        narrator.Value("[mediator] onward payload bytes > 0", preview.OnwardPacked.Length > 0);
        narrator.Value("[mediator] expires_time set", preview.ExpiresTime is not null);
        narrator.Value("[mediator] requested delay", preview.Delay?.ToString() ?? "<none>");

        // Dispatch with "no acknowledgement to wait for" (Task.CompletedTask): the handler relays
        // at once. A transport with a wire acknowledgement passes a task that completes when it
        // has acknowledged the message instead (below).
        var outcome = await dispatcher.DispatchAsync(unpacked, facade, didCommOptions, Task.CompletedTask, CancellationToken.None);
        narrator.Value("[mediator] dispatch result", outcome.Result);
        NarrateRecord(narrator, await NextRecordAsync(sink, "the hand-built relay"), "acknowledged (nothing to wait for)");
        var firstDelivery = await AwaitOrFailAsync(bobDeliveries.ReadAsync().AsTask(), "the first hand-built delivery to Bob");
        narrator.Value("[bob] Content", firstDelivery.Unpacked.Message.Body?["content"]?.GetValue<string>());

        // The overload with a return-route session too (none for a forward: a forward never gets
        // one, FR-RR-02). Here our own "transport" acknowledges later: nothing is relayed until the
        // acknowledgement task completes — the same rule that holds the relay behind the 202.
        var second = await facade.UnpackAsync(
            await HandPackForwardAsync(aliceClient, alice, mediator, bob, "Held until the acknowledgement."));
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldOutcome = await dispatcher.DispatchAsync(
            second, facade, didCommOptions, acknowledged.Task, session: null, CancellationToken.None);
        narrator.Value("[mediator] dispatch result before the acknowledgement", heldOutcome.Result);
        narrator.Value("[mediator] relayed before the acknowledgement", sink.Records.Count > 0);
        acknowledged.SetResult(); // our transport has now acknowledged the message on the wire
        NarrateRecord(narrator, await NextRecordAsync(sink, "the relay after the acknowledgement"), "our transport acknowledged");
        var secondDelivery = await AwaitOrFailAsync(bobDeliveries.ReadAsync().AsTask(), "the second hand-built delivery to Bob");
        narrator.Value("[bob] Content", secondDelivery.Unpacked.Message.Body?["content"]?.GetValue<string>());

        // Shutting down: Dispose() stops admission at once and discards anything still queued
        // (fail closed) — call it from synchronous code. DisposeAsync() additionally waits for the
        // relay to finish its current forward (up to 5 seconds). Calling both, as here, is safe.
        handler.Dispose();
        await handler.DisposeAsync();
        narrator.Step("Hand-built handler stopped (Dispose, then DisposeAsync waited for its relay to finish).");
    }

    /// <summary>Alice's authcrypt message for Bob, wrapped in a forward for the mediator and anoncrypted to it.</summary>
    private static async Task<string> HandPackForwardAsync(
        DidCommClient aliceClient, PeerIdentity alice, PeerIdentity mediator, PeerIdentity bob, string content)
    {
        var inner = await aliceClient.PackEncryptedAsync(
            Hello(alice.Did, bob.Did, content),
            new PackEncryptedOptions(Recipients: new[] { bob.Did }, From: alice.Did));
        var forward = ForwardMessage.Create(
            mediator: mediator.Did,
            next: bob.Did,
            packedPayloads: new[] { inner.Message },
            idGenerator: UuidV4MessageIdGenerator.Instance,
            expiresTimeEpochSeconds: DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
        var outer = await aliceClient.PackEncryptedAsync(forward, new PackEncryptedOptions(Recipients: new[] { mediator.Did }));
        return outer.Message;
    }

    private static void NarrateRecord(Narrator narrator, RelayRecord record, string acknowledgement)
    {
        narrator.Value("[mediator] relay outcome", record.Outcome);
        narrator.Value($"[mediator] {acknowledgement} before the sink ran", record.AcknowledgedFirst);
        narrator.Value("[mediator] not before", record.NotBefore?.ToString("O") ?? "<none: no hold asked for>");
        if (record.Outcome == RelayOutcome.Relayed)
        {
            narrator.Step($"[mediator] relayed to Bob's inbox with SendPackedAsync (HTTP {record.HttpStatus}).");
            narrator.Value("[mediator] relay response discarded", record.ResponseDiscarded);
            narrator.Value("[mediator] relay response body", record.ResponseMediaType is null
                ? $"none ({record.ResponseBytes} bytes): a relay never reads one"
                : $"{record.ResponseMediaType}, {record.ResponseBytes} bytes");
        }
    }

    private static void NarrateDelivery(Narrator narrator, UnpackResult delivered, bool hadSession, string aliceDid)
    {
        narrator.Value("[bob] Content", delivered.Message.Body?["content"]?.GetValue<string>());
        narrator.Value("[bob] Authenticated", delivered.Authenticated);
        narrator.Value("[bob] From == alice", delivered.Message.From == aliceDid);
        narrator.Value("[bob] RecipientAddressing", delivered.RecipientAddressing);
        narrator.Value("[bob] return-route session", hadSession ? "open" : "none (nothing to answer on)");
    }

    private static Task<RelayRecord> NextRecordAsync(InboxRelaySink sink, string what)
        => AwaitOrFailAsync(sink.Records.ReadAsync().AsTask(), what);

    /// <summary>Alice's client container: her secrets + the HTTP transport, with or without the loopback allowlist entry.</summary>
    private static (ServiceProvider Provider, DidCommClient Client) BuildAliceClient(
        InMemorySecretsResolver aliceSecrets, bool allowLoopback)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver()
                .UseSecretsResolver(aliceSecrets)
                .UseHttpTransport(o =>
                {
                    o.AllowedSchemes = new[] { "http", "https" }; // loopback demo; production stays https-only
                    o.MaxRetryAttempts = 0;
                });
            if (allowLoopback)
            {
                // The narrow opt-in: this exact host, nothing else. The rest of the policy
                // (private ranges, link-local, metadata IPs) keeps blocking.
                b.Configure(o => o.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1"));
            }
        });
        var provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService<DidCommClient>());
    }

    private static async Task<PeerIdentity> MintAliceAsync(InMemorySecretsResolver aliceSecrets)
    {
        // Alice mints from her own container — parties don't share infrastructure, only DIDs.
        var services = new ServiceCollection();
        services.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(aliceSecrets));
        await using var sp = services.BuildServiceProvider();
        var alice = await PeerIdentityFactory.CreateAsync(
            sp.GetRequiredService<IDidManager>(),
            sp.GetRequiredService<IKeyGenerator>(),
            sp.GetRequiredService<ICryptoProvider>());
        foreach (var key in alice.Privates) aliceSecrets.Add(key);
        return alice;
    }

    private static Message Hello(string from, string to, string content) => new MessageBuilder()
        .WithType("https://didcomm.org/basicmessage/2.0/message")
        .WithFrom(from)
        .WithTo(to)
        .WithBody(new JsonObject { ["content"] = content })
        .Build();

    /// <summary>Await a signal with a hard upper bound — a hang fails loudly instead of forever (FR-DX-02).</summary>
    private static async Task<T> AwaitOrFailAsync<T>(Task<T> task, string what)
    {
        var winner = await Task.WhenAny(task, Task.Delay(HangGuard));
        if (winner != task)
            throw new TimeoutException($"Timed out waiting for {what}.");
        return await task;
    }

    /// <inheritdoc cref="AwaitOrFailAsync{T}(Task{T}, string)"/>
    private static async Task AwaitOrFailAsync(Task task, string what)
    {
        var winner = await Task.WhenAny(task, Task.Delay(HangGuard));
        if (winner != task)
            throw new TimeoutException($"Timed out waiting for {what}.");
        await task;
    }

    private static string Trunc(string? value, int max)
        => value is null ? "<null>" : value.Length <= max ? value : value[..(max - 1)] + "…";
}
