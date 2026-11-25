using System.Net.Sockets;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetCrypto;
using NetDid.Core;

// The static OutOfBand API class shares its name with its namespace: alias it.
using OutOfBandApi = DidComm.Protocols.OutOfBand.OutOfBand;

namespace DidComm.Samples.MediatedAgent.Wallet;

/// <summary>
/// A wallet with no endpoint of its own, such as a phone app. It asks a mediator to hold its
/// messages, publishes a DID that tells senders "reach me through that mediator", and picks its
/// messages up: over HTTP whenever it likes, or live over a WebSocket while it is online.
/// </summary>
/// <remarks>
/// This project references <c>DidComm.Mediator.Client</c>, the HTTP and WebSocket transports and the
/// dependency-injection package, and nothing from the mediator side: a wallet never installs
/// <c>DidComm.Mediator</c> or ASP.NET Core.
/// </remarks>
public sealed class RecipientWallet : IAsyncDisposable
{
    /// <summary>The largest invitation the wallet reads; an invitation is a few kilobytes.</summary>
    private const int MaxInvitationBytes = 64 * 1024;

    /// <summary>
    /// How long the wallet waits before it fetches again after a fetch that brought only
    /// inconclusive items: the time this library's mediator sets delivered messages aside (its
    /// lease, 60 seconds by default), after which it delivers them again.
    /// </summary>
    private static readonly TimeSpan WaitAfterOnlyInconclusiveItems = TimeSpan.FromSeconds(60);

    private readonly string _name;
    private readonly Narrator _narrator;
    private readonly InMemorySecretsResolver _secrets;
    private readonly ServiceProvider _services;
    private readonly MediationClient _mediation;
    private readonly PickupClient _pickup;
    private MediationGrant? _grant;
    private string? _mediatorDid;

    private RecipientWallet(string name, Narrator narrator, InMemorySecretsResolver secrets, ServiceProvider services, string did)
    {
        _name = name;
        _narrator = narrator;
        _secrets = secrets;
        _services = services;
        Did = did;

        // Built by hand, without dependency injection: clients made this way work over HTTP only,
        // which is all this part of the wallet needs. Live delivery needs the WebSocket set-up in
        // ConnectLiveAsync. Both clients get the same settings (see ConfigureClient).
        var options = new MediatorClientOptions();
        ConfigureClient(options);
        options.Validate(); // the constructors check them too; this fails earlier, with the setting's name
        var client = services.GetRequiredService<DidCommClient>();
        _mediation = new MediationClient(client, options);
        _pickup = new PickupClient(client, options);
    }

    /// <summary>
    /// The DID this wallet enrolls with and picks up as. It is private to the wallet and the
    /// mediator: senders never see it.
    /// </summary>
    public string Did { get; }

    /// <summary>The mediator's DID, read from its invitation.</summary>
    public string MediatorDid => _mediatorDid ?? throw new InvalidOperationException("Read the mediator's invitation first.");

    /// <summary>
    /// The settings both mediator clients use, whether built by hand or by
    /// <c>AddMediatorClient()</c>. The method only assigns values: <c>AddMediatorClient()</c> may run
    /// it more than once.
    /// </summary>
    /// <param name="options">The settings to fill in.</param>
    public static void ConfigureClient(MediatorClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // How long each step of a call waits for the mediator's answer.
        options.RequestTimeout = TimeSpan.FromSeconds(15);
        // How many requests may wait for an answer at once, both clients together.
        options.MaxPendingRequests = 16;
        // Ask the mediator which Coordinate Mediation version it speaks (3.0 preferred). Pin V3 or
        // V2 for a mediator that does not answer Discover Features queries.
        options.CoordinateMediationVersion = CoordinateMediationVersion.Negotiate;
        // The same for Message Pickup (4.0 preferred, else 3.0). Pin V4 or V3 for a mediator that
        // does not answer Discover Features queries.
        options.MessagePickupVersion = MessagePickupVersion.Negotiate;
        // Delivery is at-least-once: remember this many delivered messages so a repeat is not
        // handed to the app twice.
        options.DedupeCapacity = 1024;
        // A live stream also asks for queued messages this often (the only way it gets a message
        // whose sender delayed it). Short for the demo; the default is a minute.
        options.LivePollInterval = TimeSpan.FromSeconds(10);
        // How long opening one delivery may take; keep it well below the mediator's lease (60 s).
        options.UnpackTimeout = TimeSpan.FromSeconds(20);
    }

    /// <summary>Create a wallet with a fresh DID of its own (keys only, no service).</summary>
    /// <param name="name">The name used in the narration (<c>bob</c>, <c>carol</c>).</param>
    /// <param name="narrator">Where the wallet narrates what it does.</param>
    public static async Task<RecipientWallet> CreateAsync(string name, Narrator narrator)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(narrator);
        var secrets = new InMemorySecretsResolver();
        var services = new ServiceCollection();
        services.AddDidComm(b => b
            .UseNetDidResolver()
            .UseSecretsResolver(secrets)
            // Plain http only because this demo runs on loopback; production stays on https (the
            // transport's default).
            .UseHttpTransport(o => o.AllowedSchemes = new[] { "http", "https" })
            // The outbound guard refuses loopback and private addresses by default; open the one
            // host this demo uses (on every port), nothing else.
            .Configure(o => o.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1")));
        var provider = services.BuildServiceProvider();
        try
        {
            var identity = await MintAsync(provider, secrets, service: null).ConfigureAwait(false);
            narrator.Step($"[{name}] wallet created; its own DID = {Display.Trunc(identity, 48)}");
            return new RecipientWallet(name, narrator, secrets, provider, identity);
        }
        catch
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Read the mediator's out-of-band invitation from its short URL (the one in a QR code, say)
    /// and take the mediator's DID from it.
    /// </summary>
    /// <param name="invitationUrl">The mediator's invitation URL (<c>…/oob?_oobid=mediator</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ReadInvitationAsync(string invitationUrl, CancellationToken ct = default)
    {
        if (!OutOfBandApi.TryGetShortFormId(invitationUrl, out var oobId))
            throw new InvalidOperationException("Not a short-form invitation URL.");

        // The short URL is fetched with a plain GET; the body is the invitation itself. The URL came
        // from outside (a QR code, a web page), so the fetch gets the protection a send gets. In
        // production an invitation URL is https; plain http is accepted here only for a loopback
        // address, because this demo runs on 127.0.0.1.
        var url = new Uri(invitationUrl);
        if (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))
            throw new InvalidOperationException("The invitation URL must be https.");

        // The wallet's own outbound guard checks every address the client connects to (the same
        // policy its transports use), redirects are not followed, and the body is capped. No proxy:
        // through a proxy the client connects to the proxy, so the guard would check the proxy's
        // address and the proxy would then reach whatever host the URL names. A wallet behind a
        // proxy must vet the invitation's host itself before handing the request to the proxy.
        var guard = new OutboundEndpointGuard(_services.GetRequiredService<IOptions<DidCommOptions>>().Value.OutboundEndpointPolicy);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = async (context, token) =>
                new NetworkStream(await guard.ConnectAsync(context.DnsEndPoint, token).ConfigureAwait(false), ownsSocket: true),
        };
        using var http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = MaxInvitationBytes,
        };
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var invitation = OutOfBandApi.FromPlaintext(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        // Only follow an invitation that asks for what this wallet wants: to request mediation.
        if (!string.Equals(invitation.GoalCode, "request-mediate", StringComparison.Ordinal) || invitation.From is null)
            throw new InvalidOperationException("The invitation does not offer mediation.");
        _mediatorDid = invitation.From;

        _narrator.Step($"[{_name}] invitation from {Display.Trunc(_mediatorDid, 48)}");
        _narrator.Value($"[{_name}] invitation id (_oobid)", oobId);
        _narrator.Value($"[{_name}] goal code", invitation.GoalCode);
    }

    /// <summary>
    /// Ask the mediator to hold messages for this wallet (Coordinate Mediation <c>mediate-request</c>).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> for a grant; <c>false</c> when the mediator answered <c>mediate-deny</c>.</returns>
    public async Task<bool> RequestMediationAsync(CancellationToken ct = default)
    {
        // The first call to a mediator asks it, with Discover Features, which Coordinate Mediation
        // version it speaks; the answer is remembered for an hour.
        var grant = await _mediation.RequestMediationAsync(MediatorDid, Did, ct: ct).ConfigureAwait(false);
        if (grant is null)
        {
            // A deny is an answer, not an error: RequestMediationAsync returns null.
            _narrator.Step($"[{_name}] mediate-deny: RequestMediationAsync returned null (a deny is an answer, not an exception)");
            return false;
        }

        _grant = grant;
        _narrator.Step($"[{_name}] granted: routing DIDs = {grant.RoutingDids.Count}");
        _narrator.Value($"[{_name}] grant is for my DID", string.Equals(grant.PeerDid, Did, StringComparison.Ordinal));
        _narrator.Value($"[{_name}] first routing DID is the mediator's", string.Equals(grant.RoutingDids[0], MediatorDid, StringComparison.Ordinal));
        return true;
    }

    /// <summary>
    /// Mint a DID that senders reach through the mediator, and register it with the mediator so it
    /// accepts forwards for it (Coordinate Mediation <c>recipient-update</c>).
    /// </summary>
    /// <param name="label">What the DID is for, for the narration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new DID.</returns>
    public async Task<string> CreateRoutedDidAsync(string label, CancellationToken ct = default)
    {
        var grant = _grant ?? throw new InvalidOperationException("Request mediation first.");

        // The DID's service entry says "reach me through this mediator": its uri is the mediator's
        // routing DID, and a sender wraps one forward for the mediator's keys (FR-ROUTE-04).
        var service = DidCommServiceBuilder.ForMediation(grant).Build();
        var did = await MintAsync(_services, _secrets, service).ConfigureAwait(false);

        // Register it. The mediator accepts forwards only for registered DIDs, and a DID belongs to
        // the first peer that registers it: register before you hand the DID out.
        var update = new RecipientUpdate(did, RecipientUpdateAction.Add);
        var results = await _mediation.UpdateRecipientsAsync(MediatorDid, Did, new[] { update }, ct: ct).ConfigureAwait(false);
        var result = results[0];
        _narrator.Step($"[{_name}] registered {label}: {result.Result}");
        _narrator.Value($"[{_name}] {label}", Display.Trunc(did, 48));
        _narrator.Value($"[{_name}] answer is for that DID ({result.Action})", string.Equals(result.RecipientDid, update.RecipientDid, StringComparison.Ordinal) && result.Action == update.Action);
        if (result.Result is not (RecipientUpdateOutcome.Success or RecipientUpdateOutcome.NoChange))
            throw new InvalidOperationException($"The mediator did not register the DID: {result.Result}.");
        return did;
    }

    /// <summary>List the DIDs registered with the mediator: one page, then all of them page by page.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many DIDs are registered.</returns>
    public async Task<int> ListRoutedDidsAsync(CancellationToken ct = default)
    {
        var paging = new RecipientQueryPaging(Limit: 1, Offset: 0);
        var page = await _mediation.QueryRecipientsAsync(MediatorDid, Did, paging, ct: ct).ConfigureAwait(false);
        _narrator.Step($"[{_name}] one page (limit {paging.Limit}, offset {paging.Offset}): {page.Dids.Count} DID(s)");
        if (page.Pagination is { } pagination)
            _narrator.Value($"[{_name}] pagination", $"count {pagination.Count}, offset {pagination.Offset}, remaining {pagination.Remaining}");

        // The walker asks page after page until the mediator says nothing remains.
        var all = 0;
        await foreach (var _ in _mediation.QueryAllRecipientsAsync(MediatorDid, Did, ct: ct).ConfigureAwait(false))
            all++;
        _narrator.Step($"[{_name}] all registered DIDs: {all}");
        return all;
    }

    /// <summary>
    /// Pick up over HTTP: ask what is waiting, then fetch until the mediator has nothing more.
    /// </summary>
    /// <param name="recipientDid">One of this wallet's routed DIDs, to ask about in the status.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The text of each message that opened.</returns>
    public async Task<IReadOnlyList<string>> PickUpOverHttpAsync(string recipientDid, CancellationToken ct = default)
    {
        var status = await _pickup.GetStatusAsync(MediatorDid, Did, recipientDid, ct: ct).ConfigureAwait(false);
        _narrator.Step($"[{_name}] status: {status.MessageCount} message(s)");
        _narrator.Value($"[{_name}] status is about the DID asked for", string.Equals(status.RecipientDid, recipientDid, StringComparison.Ordinal));
        _narrator.Value($"[{_name}] total bytes waiting", status.TotalBytes);
        // When the oldest and the newest waiting message arrived, in UTC epoch seconds. (Message
        // Pickup 3.0 also reports how long the oldest has waited, LongestWaitedSeconds; 4.0 does not.)
        _narrator.Value($"[{_name}] arrival times reported (oldest, newest)", status.OldestReceivedTime is not null && status.NewestReceivedTime is not null);
        _narrator.Value($"[{_name}] live delivery on this connection", status.LiveDelivery?.ToString() ?? "<not reported>");

        // Fetch until drained. Each fetch acknowledges the messages that opened before it returns,
        // so the mediator deletes them; one that does not open is reported and stays queued.
        var texts = new List<string>();
        PickupBatch batch;
        do
        {
            batch = await _pickup.FetchAsync(MediatorDid, Did, limit: 10, ct: ct).ConfigureAwait(false);
            foreach (var item in batch.Items)
            {
                if (item.Error is { } error)
                {
                    // A message that did not open stays queued. When the item's Inconclusive is
                    // true, this fetch did not give the message a fair chance (the client did not
                    // get to it in time, had no room for it, or started it late): leave it alone,
                    // it comes again and is started again then. Otherwise it had its chance:
                    // discard it with AckAsync once you have given up on it. That an item is not
                    // inconclusive is not by itself a reason to discard it. Log the error's type,
                    // not its text: the text can quote DIDs and key ids.
                    _narrator.Note(item.Inconclusive
                        ? $"[{_name}] a message was not given its chance in this fetch ({error.GetType().Name}); it stays queued and comes again."
                        : $"[{_name}] a message did not open ({error.GetType().Name}); it stays queued.");
                    continue;
                }

                var unpacked = item.Unpacked!;
                var text = unpacked.Message.Body?["content"]?.GetValue<string>() ?? "<no content>";
                texts.Add(text);
                _narrator.Step($"[{_name}] fetched: {text}");
                // Check who sent it before trusting it: a queued message may be anonymous.
                _narrator.Value($"[{_name}] sender authenticated", unpacked.Authenticated);
                _narrator.Value($"[{_name}] mediator's id for it", Display.Trunc(item.Id, 16));
            }

            // A fetch that brought only inconclusive items settled nothing: wait before fetching
            // again. Without the wait, a mediator that does not set delivered messages aside hands
            // the same items back at once, and this loop would spin.
            if (batch.Items.Count > 0 && batch.Items.All(item => item.Inconclusive))
                await Task.Delay(WaitAfterOnlyInconclusiveItems, ct).ConfigureAwait(false);
        }
        while (!batch.Drained);

        // The drained fetch shows which Message Pickup version the client negotiated: under 3.0 the
        // mediator answers "nothing more" with its status, under 4.0 with an empty delivery and no
        // status. Under 4.0, ask for the status when you want it.
        _narrator.Value($"[{_name}] Message Pickup version negotiated with the mediator", batch.Status is null ? "4.0" : "3.0");
        var after = batch.Status ?? await _pickup.GetStatusAsync(MediatorDid, Did, ct: ct).ConfigureAwait(false);
        _narrator.Step($"[{_name}] status after fetch: {after.MessageCount} message(s) (the fetch acknowledged what it opened)");
        return texts;
    }

    /// <summary>
    /// Open the wallet's WebSocket connection to the mediator, for live delivery. It is a second
    /// container with the three set-up calls the WebSocket needs.
    /// </summary>
    /// <param name="webSocketEndpoint">The mediator's WebSocket address.</param>
    public LiveConnection ConnectLive(Uri webSocketEndpoint)
    {
        ArgumentNullException.ThrowIfNull(webSocketEndpoint);
        var services = new ServiceCollection();
        services.AddDidComm(b => b
            .UseNetDidResolver()
            .UseSecretsResolver(_secrets)
            // 1. The WebSocket transport, receiving: the mediator's answers and live pushes arrive on
            //    the same connection. Plain ws only because this demo runs on loopback; production
            //    stays on wss (the transport's default).
            .UseWebSocketTransport(o =>
            {
                o.AllowedSchemes = new[] { "ws", "wss" };
                o.ReceiveInbound = true;
            })
            // 2. Hand what arrives on the connection to the dispatcher, which hands the mediator's
            //    answers to the clients.
            .UseInboundDispatch()
            // 3. The clients, from the container, with the same settings as the hand-built ones.
            .AddMediatorClient(ConfigureClient)
            .Configure(o => o.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1")));
        var provider = services.BuildServiceProvider();
        return new LiveConnection(_name, _narrator, provider, MediatorDid, Did, webSocketEndpoint);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private static async Task<string> MintAsync(IServiceProvider services, InMemorySecretsResolver secrets, NetDid.Core.Model.Service? service)
    {
        var manager = services.GetRequiredService<IDidManager>();
        var keyGenerator = services.GetRequiredService<IKeyGenerator>();
        var crypto = services.GetRequiredService<ICryptoProvider>();
        var identity = service is null
            ? await PeerIdentityFactory.CreateAsync(manager, keyGenerator, crypto).ConfigureAwait(false)
            : await PeerIdentityFactory.CreateWithServiceEntryAsync(manager, keyGenerator, crypto, service).ConfigureAwait(false);
        foreach (var key in identity.Privates)
            secrets.Add(key);
        return identity.Did;
    }
}
