using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports.Http;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.DependencyInjection;
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
            // The outbound guard refuses loopback and private addresses by default; open exactly
            // the one host this demo uses, and nothing else.
            .Configure(o => o.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1")));
        var provider = services.BuildServiceProvider();
        try
        {
            var identity = await MintAsync(provider, secrets, service: null).ConfigureAwait(false);
            narrator.Step($"[{name}] wallet created; its own DID = {Text.Trunc(identity, 48)}");
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

        // The short URL is fetched with a plain GET; the body is the invitation itself. Plain http
        // only because this demo runs on loopback: production invitation URLs are https.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await http.GetAsync(new Uri(invitationUrl), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var invitation = OutOfBandApi.FromPlaintext(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        // Only follow an invitation that asks for what this wallet wants: to request mediation.
        if (!string.Equals(invitation.GoalCode, "request-mediate", StringComparison.Ordinal) || invitation.From is null)
            throw new InvalidOperationException("The invitation does not offer mediation.");
        _mediatorDid = invitation.From;

        _narrator.Step($"[{_name}] invitation from {Text.Trunc(_mediatorDid, 48)}");
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
        _narrator.Value($"[{_name}] {label}", Text.Trunc(did, 48));
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
        _narrator.Value($"[{_name}] waiting times reported", status.LongestWaitedSeconds is not null && status.OldestReceivedTime is not null && status.NewestReceivedTime is not null);
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
                if (item.Unpacked is { } unpacked)
                {
                    var text = unpacked.Message.Body?["content"]?.GetValue<string>() ?? "<no content>";
                    texts.Add(text);
                    _narrator.Step($"[{_name}] fetched: {text}");
                    _narrator.Value($"[{_name}] sender authenticated", unpacked.Authenticated);
                    _narrator.Value($"[{_name}] mediator's id for it", Text.Trunc(item.Id, 16));
                }
                else
                {
                    // Log the error's type, not its text: the text can quote DIDs and key ids.
                    _narrator.Note($"[{_name}] a message did not open ({item.Error?.GetType().Name}); it stays queued until acknowledged with AckAsync.");
                }
            }
        }
        while (!batch.Drained);

        // A drained fetch carries the mediator's status.
        _narrator.Step($"[{_name}] status after fetch: {batch.Status?.MessageCount} message(s) (the fetch acknowledged what it opened)");
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
