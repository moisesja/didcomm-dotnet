using System.Text.Json.Nodes;
using DidComm.AspNetCore;
using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using DidComm.Transports.ReturnRoute;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCrypto;
using NetDid.Core;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// A wallet that has no public address of its own (a phone, a browser) enrolls with a mediator:
/// it asks for mediation, gets the mediator's routing DID, mints the DID other people will write
/// to with a service entry that points at the mediator, and registers that DID so the mediator
/// accepts messages for it. Everything runs over HTTP against a real mediator on 127.0.0.1.
/// </summary>
/// <remarks>
/// <para>
/// A wallet installs <c>DidComm.Mediator.Client</c> plus a transport: <c>DidComm.Transports.Http</c>
/// for asking and picking up, or <c>DidComm.Transports.WebSocket</c> for live delivery (section EE).
/// It needs nothing of the mediator's own packages. The mediator side is <c>DidComm.Mediator</c> and
/// <c>DidComm.Mediator.AspNetCore</c> (here <see cref="CookbookMediatorHost"/>; section FF builds one).
/// </para>
/// <para>
/// The section also shows a wallet that speaks the older protocol version, 2.0, enrolling with the
/// same mediator; what happens when the peer is not a mediator at all; how to recognise the
/// "no address, hold my messages" queue URI; and every Coordinate Mediation message as it travels —
/// for anyone writing a handler of their own or a fake mediator in tests.
/// </para>
/// <para>Maps to PRD §14.2 task <strong>CC</strong> (FR-MED-01..10, FR-RR-06).</para>
/// </remarks>
public static class Section_CC_EnrollWithAMediator
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("CC", "Enroll with a mediator");

        await using var mediator = await CookbookMediatorHost.StartAsync(ctx);
        ctx.Narrator.Step($"[mediator] listening on {mediator.Endpoint}");
        ctx.Narrator.Value("Mediator DID", Trunc(mediator.Did));

        // ── Bob's wallet: the mediator client plus an HTTP transport, in its own container ──
        // The wallet holds its own keys; it shares nothing with the mediator but DIDs. Bob enrolls
        // with the cookbook's Bob identity. (Alice's keys are here too, only so that a second wallet
        // can try something below without a second container.)
        var walletSecrets = new InMemorySecretsResolver();
        foreach (var key in ctx.Bob.Privates) walletSecrets.Add(key);
        foreach (var key in ctx.Alice.Privates) walletSecrets.Add(key);
        var services = new ServiceCollection();
        services.AddDidComm(b => b
            .UseNetDidResolver()
            .UseSecretsResolver(walletSecrets)
            // Plain http only because everything here is on 127.0.0.1; production stays https.
            .UseHttpTransport(o => o.AllowedSchemes = ["http", "https"])
            // The outbound-address guard blocks loopback by default. Open this one host (on every
            // port), nothing else.
            .Configure(o => o.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1"))
            .AddMediatorClient());
        await using var wallet = services.BuildServiceProvider();
        var mediation = wallet.GetRequiredService<MediationClient>();
        ctx.Narrator.Note("A wallet installs DidComm.Mediator.Client plus DidComm.Transports.Http (or DidComm.Transports.WebSocket for live delivery, section EE); the mediator's packages stay on the server.");

        // 1. Ask for mediation. The client first asks the mediator which Coordinate Mediation
        //    version it speaks (Discover Features), then sends mediate-request; the answer comes
        //    back in the HTTP response. A refusal (mediate-deny) is a null grant, not an exception.
        ctx.Narrator.Step("Bob asks for mediation (RequestMediationAsync).");
        var grant = await mediation.RequestMediationAsync(mediator.Did, ctx.Bob.Did)
            ?? throw new InvalidOperationException("The mediator refused to mediate (mediate-deny).");
        ctx.Narrator.Value("[bob] granted: routing DIDs", grant.RoutingDids.Count);
        ctx.Narrator.Value("Grant.PeerDid is Bob", grant.PeerDid == ctx.Bob.Did);
        ctx.Narrator.Value("Routing DID [0] is the mediator", grant.RoutingDids[0] == mediator.Did);
        ctx.Narrator.Note("A mediate-deny comes back as null: always check the grant before using it.");

        // 2. Mint the DID others will write to. Its service entry names the mediator's routing
        //    DID instead of an address, so senders wrap their message in a forward for the mediator
        //    automatically (section O).
        var service = DidCommServiceBuilder.ForMediation(grant).Build();
        var bobPublic = await PeerIdentityFactory.CreateWithServiceEntryAsync(
            wallet.GetRequiredService<IDidManager>(),
            wallet.GetRequiredService<IKeyGenerator>(),
            wallet.GetRequiredService<ICryptoProvider>(),
            service);
        foreach (var key in bobPublic.Privates) walletSecrets.Add(key);
        ctx.Narrator.Value("Bob's public DID (service via the mediator)", Trunc(bobPublic.Did));

        // 3. Register it, so the mediator accepts forwards for it. Each entry gets its own result.
        ctx.Narrator.Step("Bob registers his public DID (UpdateRecipientsAsync).");
        var results = await mediation.UpdateRecipientsAsync(mediator.Did, ctx.Bob.Did,
            [new RecipientUpdate(bobPublic.Did, RecipientUpdateAction.Add)]);
        foreach (var result in results)
            ctx.Narrator.Value($"[bob] {result.Action} {(result.RecipientDid == bobPublic.Did ? "public DID" : "?")}", result.Result);

        // Retrying is safe: the same add again changes nothing.
        var again = await mediation.UpdateRecipientsAsync(mediator.Did, ctx.Bob.Did,
            [new RecipientUpdate(bobPublic.Did, RecipientUpdateAction.Add)]);
        ctx.Narrator.Value("[bob] the same add again", again[0].Result);

        // The first peer to register a DID gets it. Alice enrolls too and tries to claim Bob's DID.
        ctx.Narrator.Step("Alice enrolls and tries to register Bob's public DID for herself.");
        _ = await mediation.RequestMediationAsync(mediator.Did, ctx.Alice.Did)
            ?? throw new InvalidOperationException("The mediator refused to mediate (mediate-deny).");
        var claim = await mediation.UpdateRecipientsAsync(mediator.Did, ctx.Alice.Did,
            [new RecipientUpdate(bobPublic.Did, RecipientUpdateAction.Add)]);
        ctx.Narrator.Value("[alice] add Bob's DID", claim[0].Result);
        ctx.Narrator.Note("The first peer to register a DID gets it: the protocol asks for no proof that you own it. A mediator that wants proof checks it in its IMediationPolicy.MayRegisterAsync.");

        // 4. List what is registered. A long list comes in pages; QueryAllRecipientsAsync walks them.
        var page = await mediation.QueryRecipientsAsync(mediator.Did, ctx.Bob.Did, new RecipientQueryPaging(Limit: 10, Offset: 0));
        ctx.Narrator.Value("[bob] registered DIDs", page.Dids.Count);
        if (page.Pagination is { } position)
            ctx.Narrator.Value("Page", $"count={position.Count}, offset={position.Offset}, remaining={position.Remaining}");

        await OlderWalletAsync(ctx, mediator, walletSecrets, bobPublic.Did);
        await WrongPeerAsync(ctx, mediation);
        await QueueUriAsync(ctx, mediator);
        await MessagesOnTheWireAsync(ctx);
    }

    /// <summary>
    /// A wallet built for an older mediator speaks Coordinate Mediation 2.0. The same mediator
    /// answers it from the same registrations: Bob, asking again in 2.0, gets the grant he already
    /// holds and sees the DID he registered in 3.0.
    /// </summary>
    private static async Task OlderWalletAsync(
        CookbookContext ctx, CookbookMediatorHost mediator, InMemorySecretsResolver walletSecrets, string bobPublicDid)
    {
        ctx.Narrator.Step("An older wallet speaks Coordinate Mediation 2.0: Bob asks the same mediator again, pinned to 2.0.");

        // A second wallet container with Bob's keys, pinned to 2.0: the client asks no version
        // question first and sends 2.0 messages only (keylist-update, keylist-query).
        var services = new ServiceCollection();
        services.AddDidComm(b => b
            .UseNetDidResolver()
            .UseSecretsResolver(walletSecrets)
            .UseHttpTransport(o => o.AllowedSchemes = ["http", "https"]) // loopback demo; production stays https
            .Configure(o => o.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1"))
            .AddMediatorClient(o => o.CoordinateMediationVersion = CoordinateMediationVersion.V2));
        await using var olderWallet = services.BuildServiceProvider();
        var mediation20 = olderWallet.GetRequiredService<MediationClient>();

        // Bob already holds a grant, so asking again (in either version) returns that grant.
        var grant = await mediation20.RequestMediationAsync(mediator.Did, ctx.Bob.Did)
            ?? throw new InvalidOperationException("The mediator refused to mediate (mediate-deny).");
        ctx.Narrator.Value("[bob, 2.0] routing DIDs in the grant", grant.RoutingDids.Count);
        ctx.Narrator.Value("[bob, 2.0] it is the mediator's first routing DID", grant.RoutingDids[0] == mediator.Did);

        // The DIDs Bob registered in 3.0 are his in 2.0 too: one set of registrations.
        var page = await mediation20.QueryRecipientsAsync(mediator.Did, ctx.Bob.Did);
        ctx.Narrator.Value("[bob, 2.0] keylist shows the DID he registered in 3.0", page.Dids.Contains(bobPublicDid));

        ctx.Narrator.Note("An older wallet, such as those built for the Identus or Blocktrust mediators, speaks version 2.0; the mediator serves both versions from one set of registrations.");
        ctx.Narrator.Note("A 2.0 grant has room for one routing DID: the mediator's first. So a mediator lists its primary routing DID first; to move to a new one, it lists the new DID first and keeps the old one, with its keys, so mail sent to either keeps arriving.");
        ctx.Narrator.Note("A wallet that talks to an Identus or Blocktrust mediator pins 2.0 this way: those mediators do not describe themselves in a form the version question can read.");
    }

    /// <summary>
    /// Enroll with an agent that is not a mediator: a plain DIDComm endpoint with the built-in
    /// protocols. Its Discover Features answer lists no Coordinate Mediation, so the client stops
    /// before sending a mediate-request.
    /// </summary>
    private static async Task WrongPeerAsync(CookbookContext ctx, MediationClient mediation)
    {
        ctx.Narrator.Step("Bob points the client at a plain agent that is not a mediator.");

        var agentSecrets = new InMemorySecretsResolver();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); // port 0: the OS picks a free one
        builder.Services.AddDidComm(b => b
            .UseNetDidResolver()
            .UseSecretsResolver(agentSecrets)
            .AddBuiltInProtocols()); // Trust Ping, Discover Features, Empty, Report Problem — no mediation
        var app = builder.Build();
        app.MapDidCommEndpoint("/didcomm"); // unpack, then dispatch to the registered handlers
        try
        {
            await app.StartAsync();

            // The agent's DID names the address it got; its keys go into its own resolver.
            var agent = await PeerIdentityFactory.CreateAsync(
                ctx.ServiceProvider.GetRequiredService<IDidManager>(),
                ctx.ServiceProvider.GetRequiredService<IKeyGenerator>(),
                ctx.ServiceProvider.GetRequiredService<ICryptoProvider>(),
                new DidCommServiceSpec(app.Urls.Single() + "/didcomm", Accept: ["didcomm/v2"]));
            foreach (var key in agent.Privates) agentSecrets.Add(key);

            try
            {
                await mediation.RequestMediationAsync(agent.Did, ctx.Bob.Did);
                ctx.Narrator.Note("UNEXPECTED: the plain agent was taken for a mediator.");
            }
            catch (MediationNotSupportedException ex)
            {
                // The peer answered Discover Features, and named no Coordinate Mediation version in
                // the mediator role. Nothing else was sent.
                ctx.Narrator.Value("Refused", nameof(MediationNotSupportedException));
                ctx.Narrator.Value("RequestId (our Discover Features query)", ex.RequestId);
                ctx.Narrator.Value("ProblemCode", ex.ProblemCode);
                ctx.Narrator.Value("DisclosedProtocols", ex.DisclosedProtocols.Count == 0 ? "none" : string.Join(", ", ex.DisclosedProtocols));
            }
            catch (MediationProtocolException ex)
            {
                // The base type: the peer answered, but with a problem report (ProblemCode) or with
                // an answer that does not fit the request.
                ctx.Narrator.Note($"The peer answered, but not as a mediator: problem code {ex.ProblemCode ?? "none"}, request {ex.RequestId}.");
            }
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        ctx.Narrator.Note("A mediator that does not answer Discover Features at all makes the call time out instead: pin MediatorClientOptions.CoordinateMediationVersion for it, and no question is asked.");
    }

    /// <summary>
    /// The queue URI: a DID that has no address at all publishes <c>didcomm:transport/queue</c>, and
    /// messages for it can only be held for pickup. <c>SendAsync</c> refuses it before anything is sent.
    /// </summary>
    private static async Task QueueUriAsync(CookbookContext ctx, CookbookMediatorHost mediator)
    {
        ctx.Narrator.Step("The queue URI: \"I have no address; hold my messages\" (FR-RR-06).");
        ctx.Narrator.Value("QueueTransport.Uri", QueueTransport.Uri);
        ctx.Narrator.Value("IsQueueTransport(string)", QueueTransport.IsQueueTransport(QueueTransport.Uri));
        ctx.Narrator.Value("IsQueueTransport(Uri)", QueueTransport.IsQueueTransport(new Uri(QueueTransport.Uri)));
        ctx.Narrator.Value("IsQueueTransport(the mediator's endpoint)", QueueTransport.IsQueueTransport(mediator.Endpoint));

        var hello = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(new JsonObject { ["content"] = "Hello, queue." })
            .Build();
        try
        {
            await ctx.Client.SendAsync(hello, new SendOptions(
                Recipients: [ctx.Bob.Did],
                From: ctx.Alice.Did,
                ServiceEndpointOverride: new Uri(QueueTransport.Uri)));
            ctx.Narrator.Note("UNEXPECTED: a send to the queue URI was not refused.");
        }
        catch (TransportException ex)
        {
            ctx.Narrator.Note($"SendAsync refused: {ex.Message}");
        }
    }

    /// <summary>
    /// Every Coordinate Mediation message, built with <see cref="CoordinateMediation"/>, sent as
    /// plaintext, and read back with the matching parser. <see cref="MediationClient"/> and the
    /// mediator do all of this for you; this is for a handler of your own or a fake mediator in
    /// tests. (The 2.0 messages are not public API: the client and the mediator speak 2.0 for you.)
    /// </summary>
    private static async Task MessagesOnTheWireAsync(CookbookContext ctx)
    {
        ctx.Narrator.Step("The Coordinate Mediation messages on the wire (build → send → parse).");
        const string wallet = "did:example:bob";
        const string mediatorDid = "did:example:mediator";
        const string bobPublic = "did:example:bob-public";
        const string bobOld = "did:example:bob-old";

        // mediate-request → mediate-grant | mediate-deny
        var request = await OverTheWireAsync(ctx, CoordinateMediation.CreateMediateRequest(wallet, mediatorDid));
        ctx.Narrator.Value("mediate-request → TryParseMediateRequest", CoordinateMediation.TryParseMediateRequest(request));

        // An answer carries the request's thid, or its id when it has none.
        var thid = CoordinateMediation.ResponseThreadId(request);
        var grantMessage = await OverTheWireAsync(ctx, CoordinateMediation.CreateGrant(mediatorDid, wallet, [mediatorDid], thid));
        if (CoordinateMediation.TryParseGrant(grantMessage, out var routingDids))
        {
            // MediationClient returns this record; code of your own builds it from the parsed grant.
            var parsed = new MediationGrant(PeerDid: wallet, RoutingDids: routingDids);
            ctx.Narrator.Value("mediate-grant → TryParseGrant", $"routing_did [{string.Join(", ", parsed.RoutingDids)}], thid is the request's id: {grantMessage.Thid == request.Id}");
        }

        var deny = await OverTheWireAsync(ctx, CoordinateMediation.CreateDeny(mediatorDid, wallet, thid));
        ctx.Narrator.Value("mediate-deny → TryParseDeny", $"{CoordinateMediation.TryParseDeny(deny)} (and TryParseGrant: {CoordinateMediation.TryParseGrant(deny, out _)})");

        // recipient-update → recipient-update-response
        var update = await OverTheWireAsync(ctx, CoordinateMediation.CreateRecipientUpdate(wallet, mediatorDid,
            [new RecipientUpdate(bobPublic, RecipientUpdateAction.Add), new RecipientUpdate(bobOld, RecipientUpdateAction.Remove)]));
        if (CoordinateMediation.TryParseRecipientUpdate(update, out var updates))
            ctx.Narrator.Value("recipient-update → TryParseRecipientUpdate", string.Join("; ", updates.Select(u => $"{u.Action} {u.RecipientDid}")));

        var response = await OverTheWireAsync(ctx, CoordinateMediation.CreateRecipientUpdateResponse(mediatorDid, wallet,
            [
                new RecipientUpdateResult(bobPublic, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success),
                new RecipientUpdateResult(bobOld, RecipientUpdateAction.Remove, RecipientUpdateOutcome.ClientError),
            ],
            CoordinateMediation.ResponseThreadId(update)));
        if (CoordinateMediation.TryParseRecipientUpdateResponse(response, out var updated))
            ctx.Narrator.Value("recipient-update-response → TryParseRecipientUpdateResponse", string.Join("; ", updated.Select(r => $"{r.Action} {r.RecipientDid} = {r.Result}")));

        // recipient-query → recipient
        var query = await OverTheWireAsync(ctx, CoordinateMediation.CreateRecipientQuery(wallet, mediatorDid, new RecipientQueryPaging(Limit: 10, Offset: 0)));
        if (CoordinateMediation.TryParseRecipientQuery(query, out var paginate))
            ctx.Narrator.Value("recipient-query → TryParseRecipientQuery", paginate is null ? "first page" : $"limit={paginate.Limit}, offset={paginate.Offset}");

        var recipient = await OverTheWireAsync(ctx, CoordinateMediation.CreateRecipient(mediatorDid, wallet,
            [bobPublic], new RecipientPagination(Count: 1, Offset: 0, Remaining: 0), CoordinateMediation.ResponseThreadId(query)));
        if (CoordinateMediation.TryParseRecipient(recipient, out var dids, out var pagination))
            ctx.Narrator.Value("recipient → TryParseRecipient", $"[{string.Join(", ", dids)}], pagination {(pagination is null ? "none" : $"count={pagination.Count}, offset={pagination.Offset}, remaining={pagination.Remaining}")}");

        ctx.Narrator.Note("A TryParse… method answers false for a message of another type and throws MalformedMessageException for one of its type with a bad body; its text never quotes the message.");
    }

    // Plaintext is enough to show the shapes; real traffic is authcrypted (section F).
    private static async Task<Message> OverTheWireAsync(CookbookContext ctx, Message message)
        => (await ctx.Client.UnpackAsync(await ctx.Client.PackPlaintextAsync(message))).Message;

    private static string Trunc(string value) => value.Length <= 56 ? value : value[..55] + "…";
}
