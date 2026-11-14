using System.Runtime.CompilerServices;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Internal;
using DidComm.Resolution;
using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Client;

/// <summary>
/// The recipient side of Coordinate Mediation (FR-MED-09): ask a mediator to mediate for you,
/// register the DIDs whose messages it should accept for you, and list them. It speaks
/// Coordinate Mediation 3.0 or 2.0 (FR-MED-10), chosen by asking the mediator first unless you pin a
/// version (<see cref="MediatorClientOptions.CoordinateMediationVersion"/>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Setting up.</strong> With dependency injection, call <c>AddMediatorClient()</c> on the
/// <c>AddDidComm</c> builder and resolve this class. Over HTTP the mediator's answer comes back in
/// the response. Over a WebSocket it arrives on the connection and is handed to this client by the
/// inbound dispatcher, so the container also needs
/// <c>UseWebSocketTransport(o =&gt; o.ReceiveInbound = true)</c> and <c>UseInboundDispatch()</c>;
/// without them a WebSocket route is refused before anything is sent. Constructed without
/// dependency injection (<c>new MediationClient(didComm)</c>), the client works over HTTP only.
/// </para>
/// <para>
/// <strong>Which endpoint.</strong> The first <c>DIDCommMessaging</c> service entry of the
/// mediator's DID decides the address and so the transport (<c>http</c>/<c>https</c> or
/// <c>ws</c>/<c>wss</c>). Before anything is sent, a mediator reached through routing keys or
/// another mediator is refused with <see cref="InvalidOperationException"/>, a DID without a
/// <c>DIDCommMessaging</c> entry with <see cref="DidResolutionException"/>, and an address that is a
/// queue URI, not absolute, or blocked by <c>DidCommOptions.OutboundEndpointPolicy</c> with
/// <see cref="TransportException"/>. Pass <c>serviceEndpointOverride</c> to use another of its
/// addresses, for example its <c>wss</c> one when its DID document lists <c>https</c> first.
/// </para>
/// <para>
/// <strong>Which version.</strong> Unless you pin one, the first call to a mediator sends a
/// Discover Features query for <c>coordinate-mediation/*</c> on the same route and uses 3.0 when the
/// mediator discloses it in the mediator role, else 2.0; the answer is remembered for an hour (for
/// up to 64 mediators). A mediator that discloses neither fails the call with
/// <see cref="MediationNotSupportedException"/>. A mediator that does not answer Discover Features
/// at all (Identus, for example) fails it with <see cref="TimeoutException"/>: the client does not
/// guess. For such a mediator set <see cref="MediatorClientOptions.CoordinateMediationVersion"/> to
/// the version it speaks, and no query is sent.
/// </para>
/// <para>
/// <strong>Timeouts.</strong> A call has up to three steps, and each has its own
/// <see cref="MediatorClientOptions.RequestTimeout"/>: finding the mediator's endpoint in its DID
/// document (skipped when you pass <c>serviceEndpointOverride</c>), the Discover Features query (only
/// when the version is neither pinned nor remembered), and the request itself
/// (<see cref="QueryAllRecipientsAsync"/> sends one request per page, each with its own timeout). A
/// step that runs out of time fails the call with <see cref="TimeoutException"/>. While the endpoint
/// is being found nothing has been sent yet, so that message names no request id; for the query or
/// the request it names that message's id. Over HTTP an answer missing from the response fails the
/// step at once.
/// </para>
/// <para>
/// <strong>Retrying is safe.</strong> A <see cref="TimeoutException"/> for a request means its
/// answer did not arrive, not that nothing happened: the mediator may have acted. Repeat the call.
/// A repeated <c>mediate-request</c> is answered with the same grant and a repeated <c>add</c> with
/// <c>no_change</c>; a repeated <c>remove</c> answers <c>client_error</c> because the DID is already
/// gone.
/// </para>
/// <para>
/// <strong>Clocks.</strong> Every request carries the time from <c>DidCommOptions.Clock</c>
/// (FR-RR-09). Answers over a WebSocket are checked against this agent's clock: a mediator whose
/// clock runs more than a minute ahead (the default <c>ReplayProtectionOptions.ClockSkew</c>) has its
/// answers dropped and the call times out. Keep the two clocks within a minute of each other.
/// </para>
/// <para>
/// <strong>Never from a handler on the same connection.</strong> Do not call this client from a
/// protocol handler or observer that is running for a message received on the same WebSocket
/// connection to the mediator: that connection reads its next message only after the handler
/// returns, so the answer would wait behind the handler until the call times out.
/// </para>
/// <para>Thread-safe; one instance per container (or per <see cref="DidCommClient"/>) is enough.</para>
/// </remarks>
/// <example>
/// <code>
/// var grant = await mediation.RequestMediationAsync(mediatorDid, myDid)
///     ?? throw new InvalidOperationException("The mediator refused.");
/// // Mint the DID others will write to, with this service entry:
/// var service = DidCommServiceBuilder.ForMediation(grant).Build();
/// // Register it; the mediator answers each update with its own result.
/// var results = await mediation.UpdateRecipientsAsync(mediatorDid, myDid,
///     new[] { new RecipientUpdate(publicDid, RecipientUpdateAction.Add) });
/// if (results[0].Result is not (RecipientUpdateOutcome.Success or RecipientUpdateOutcome.NoChange))
///     throw new InvalidOperationException("The mediator did not register the DID.");
/// </code>
/// </example>
public sealed class MediationClient
{
    /// <summary>
    /// Create a client over <paramref name="client"/> without dependency injection. It works over
    /// HTTP only: a mediator reached over a WebSocket is refused with
    /// <see cref="InvalidOperationException"/>, because nothing hands this client the answers that
    /// arrive on the connection. Use <c>AddMediatorClient()</c> for WebSocket routes.
    /// </summary>
    /// <param name="client">The facade to send through; it needs a transport and a service endpoint resolver (as <c>AddDidComm</c> builds it).</param>
    /// <param name="options">The settings; the defaults when <c>null</c>. Copied and checked here.</param>
    /// <param name="logger">Optional logger. Lines carry request ids, message names and counts, never a DID, key id or message content.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A setting of <paramref name="options"/> is outside its range (<see cref="MediatorClientOptions.Validate"/>).</exception>
    public MediationClient(DidCommClient client, MediatorClientOptions? options = null, ILogger<MediationClient>? logger = null)
        : this(new MediatorClientChannel(client, options ?? new MediatorClientOptions(), receivesInbound: false, TimeProvider.System, loggerFactory: null), logger)
    {
    }

    /// <summary>The DI constructor (and the test seam): a client over a shared exchange.</summary>
    /// <param name="exchange">The container's channel, or a fake.</param>
    /// <param name="logger">Optional logger.</param>
    internal MediationClient(IMediatorExchange exchange, ILogger<MediationClient>? logger)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        Exchange = exchange;
        Logger = logger;
    }

    /// <summary>The exchange this client sends through.</summary>
    internal IMediatorExchange Exchange { get; }

    /// <summary>The client's logger, or <c>null</c>.</summary>
    internal ILogger<MediationClient>? Logger { get; }

    /// <summary>
    /// Ask <paramref name="mediatorDid"/> to mediate for <paramref name="from"/>
    /// (<c>mediate-request</c>). The grant names the DIDs to publish as your service's address:
    /// build the service entry with <see cref="DidCommServiceBuilder"/><c>.ForMediation(grant)</c>.
    /// Then register the DIDs others will write to with <see cref="UpdateRecipientsAsync"/>.
    /// </summary>
    /// <param name="mediatorDid">The mediator's DID: a bare DID (no path, query or fragment).</param>
    /// <param name="from">Your DID, the one you enrol as: a bare DID whose keys this agent holds. The mediator answers it, encrypted to it.</param>
    /// <param name="serviceEndpointOverride">The mediator address to use instead of the first one in its DID document: an absolute URI without user information. Trusted as given (not checked against the outbound endpoint policy), as with <c>SendOptions.ServiceEndpointOverride</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The grant (<see cref="MediationGrant.PeerDid"/> is <paramref name="from"/>), or <c>null</c> when the mediator refused (<c>mediate-deny</c>).</returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, or <paramref name="serviceEndpointOverride"/> is relative or carries user information (<see cref="ArgumentNullException"/> for a <c>null</c> DID).</exception>
    /// <exception cref="InvalidOperationException">A WebSocket route this client cannot receive on, a mediator reached through routing, or no service endpoint resolver: nothing was sent. Or too many requests already waiting (<see cref="MediatorClientOptions.MaxPendingRequests"/>): that request was not sent, though the Discover Features query before it may have been.</exception>
    /// <exception cref="TimeoutException">A step did not finish within its own <see cref="MediatorClientOptions.RequestTimeout"/> (see Timeouts in the class remarks). Finding the endpoint: nothing was sent, and the message names no request id. The Discover Features query or the request: no answer in time, or over HTTP none in the response; the message names that message's id, and the mediator may have acted, so retry. A mediator that never answers the query needs a pinned <see cref="MediatorClientOptions.CoordinateMediationVersion"/>.</exception>
    /// <exception cref="MediationNotSupportedException">The mediator's Discover Features answer discloses neither Coordinate Mediation 3.0 nor 2.0 in the mediator role (only when the version is negotiated).</exception>
    /// <exception cref="MediationProtocolException">The mediator answered with a problem report, or with an answer that is malformed or does not match the request.</exception>
    /// <exception cref="DidResolutionException">The mediator's DID names no <c>DIDCommMessaging</c> service entry; nothing was sent.</exception>
    /// <exception cref="TransportException">The transport failed (over HTTP with status 200: the mediator accepted the request but its answer was too large, stalled or unreadable; retry). Or the mediator's address is a queue URI, not absolute, or blocked by the outbound endpoint policy: nothing was sent.</exception>
    /// <exception cref="ObjectDisposedException">The container was disposed.</exception>
    public Task<MediationGrant?> RequestMediationAsync(string mediatorDid, string from, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
    {
        RequireDids(mediatorDid, from);
        RequireOverride(serviceEndpointOverride);
        return RequestMediationCoreAsync(mediatorDid, from, serviceEndpointOverride, ct);
    }

    /// <summary>
    /// Add or remove DIDs the mediator accepts messages for under your grant
    /// (<c>recipient-update</c>; <c>keylist-update</c> in 2.0). Everything goes in one message; there
    /// is no splitting, so up to 1024 updates (this library's mediator's limit).
    /// </summary>
    /// <param name="mediatorDid">The mediator's DID: a bare DID.</param>
    /// <param name="from">Your DID, the one you enrolled as: a bare DID whose keys this agent holds.</param>
    /// <param name="updates">1 to 1024 updates, each a bare DID with <see cref="RecipientUpdateAction.Add"/> or <see cref="RecipientUpdateAction.Remove"/>.</param>
    /// <param name="serviceEndpointOverride">The mediator address to use instead of the first one in its DID document (see <see cref="RequestMediationAsync"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// One result per update, in the order given (the client checks that the mediator answered each
    /// one, naming its DID and action). Check each <see cref="RecipientUpdateResult.Result"/>: the
    /// mediator refuses an entry with <c>client_error</c> rather than failing the message. This
    /// library's mediator answers <c>client_error</c> for every entry when you hold no grant, and for
    /// an <c>add</c> beyond its limit of DIDs per grant (256 by default).
    /// </returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, <paramref name="updates"/> is empty or longer than 1024 or has an invalid entry (the message names its index), or <paramref name="serviceEndpointOverride"/> is relative or carries user information.</exception>
    /// <exception cref="InvalidOperationException">As for <see cref="RequestMediationAsync"/>.</exception>
    /// <exception cref="TimeoutException">As for <see cref="RequestMediationAsync"/>; retry (the class remarks say what a repeat answers).</exception>
    /// <exception cref="MediationNotSupportedException">As for <see cref="RequestMediationAsync"/>.</exception>
    /// <exception cref="MediationProtocolException">A problem report, or an answer that is malformed or does not match the updates sent.</exception>
    /// <exception cref="DidResolutionException">As for <see cref="RequestMediationAsync"/>.</exception>
    /// <exception cref="TransportException">As for <see cref="RequestMediationAsync"/>.</exception>
    /// <exception cref="ObjectDisposedException">The container was disposed.</exception>
    public Task<IReadOnlyList<RecipientUpdateResult>> UpdateRecipientsAsync(string mediatorDid, string from, IReadOnlyList<RecipientUpdate> updates, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
    {
        RequireDids(mediatorDid, from);
        var sent = CopyUpdates(updates);
        RequireOverride(serviceEndpointOverride);
        return UpdateRecipientsCoreAsync(mediatorDid, from, sent, serviceEndpointOverride, ct);
    }

    /// <summary>
    /// One page of the DIDs registered under your grant (<c>recipient-query</c>; <c>keylist-query</c>
    /// in 2.0). <see cref="QueryAllRecipientsAsync"/> walks every page for you.
    /// </summary>
    /// <param name="mediatorDid">The mediator's DID: a bare DID.</param>
    /// <param name="from">Your DID, the one you enrolled as: a bare DID whose keys this agent holds.</param>
    /// <param name="paginate">Which page: <see cref="RecipientQueryPaging.Limit"/> 0 to 1000, <see cref="RecipientQueryPaging.Offset"/> 0 or more; <c>null</c> for the mediator's default page. The mediator may send fewer DIDs than the limit, or more (up to 1000 are accepted, as from a mediator that ignores <c>paginate</c>).</param>
    /// <param name="serviceEndpointOverride">The mediator address to use instead of the first one in its DID document (see <see cref="RequestMediationAsync"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The page. A position the mediator reports is checked against the page and the request; a page
    /// without one is returned as it came, and nothing then says where it sits in the list. This
    /// library's mediator answers an empty page when you hold no grant.
    /// </returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, <paramref name="paginate"/> is out of range, or <paramref name="serviceEndpointOverride"/> is relative or carries user information.</exception>
    /// <exception cref="InvalidOperationException">As for <see cref="RequestMediationAsync"/>.</exception>
    /// <exception cref="TimeoutException">As for <see cref="RequestMediationAsync"/>; retry.</exception>
    /// <exception cref="MediationNotSupportedException">As for <see cref="RequestMediationAsync"/>.</exception>
    /// <exception cref="MediationProtocolException">A problem report, or a page that is malformed, holds more than 1000 DIDs, or reports a position that does not agree with the page or with the offset asked for.</exception>
    /// <exception cref="DidResolutionException">As for <see cref="RequestMediationAsync"/>.</exception>
    /// <exception cref="TransportException">As for <see cref="RequestMediationAsync"/>.</exception>
    /// <exception cref="ObjectDisposedException">The container was disposed.</exception>
    public Task<RecipientQueryResult> QueryRecipientsAsync(string mediatorDid, string from, RecipientQueryPaging? paginate = null, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
    {
        RequireDids(mediatorDid, from);
        RequirePaging(paginate);
        RequireOverride(serviceEndpointOverride);
        return QueryRecipientsCoreAsync(mediatorDid, from, paginate, serviceEndpointOverride, ct);
    }

    /// <summary>
    /// Every DID registered under your grant, fetched page by page while you enumerate: 100 asked for
    /// per page (a mediator may send more, up to 1000), the next page starting after the DIDs
    /// received, until the mediator reports none remaining or reports no position at all (a mediator
    /// that ignores paging sends its whole list in the first page, and the walk ends there). The
    /// arguments are checked when you call this method; nothing is sent before the first
    /// <c>MoveNextAsync</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The walk never ends early in silence. When it cannot list everything — an empty page while the
    /// mediator reports DIDs remaining, a page of more than 1000 DIDs or whose position disagrees with
    /// the request, or DIDs still remaining after 1000 pages — it throws
    /// <see cref="MediationProtocolException"/> after the DIDs already yielded, and those are then not
    /// the whole list.
    /// </para>
    /// <para>
    /// The pages are separate requests, not one snapshot: DIDs added or removed during the walk can
    /// shift the pages, so a DID can be missed or listed twice.
    /// </para>
    /// </remarks>
    /// <param name="mediatorDid">The mediator's DID: a bare DID.</param>
    /// <param name="from">Your DID, the one you enrolled as: a bare DID whose keys this agent holds.</param>
    /// <param name="serviceEndpointOverride">The mediator address to use instead of the first one in its DID document (see <see cref="RequestMediationAsync"/>).</param>
    /// <param name="ct">Cancellation token (also reachable through <c>WithCancellation</c>).</param>
    /// <returns>The DIDs, page by page, in the order the mediator lists them.</returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, or <paramref name="serviceEndpointOverride"/> is relative or carries user information.</exception>
    /// <exception cref="InvalidOperationException">While enumerating: as for <see cref="RequestMediationAsync"/>.</exception>
    /// <exception cref="TimeoutException">While enumerating: a step did not finish in time — finding the endpoint, the Discover Features query, or one page's request, each with its own <see cref="MediatorClientOptions.RequestTimeout"/> (as for <see cref="RequestMediationAsync"/>). The DIDs already yielded are not the whole list.</exception>
    /// <exception cref="MediationNotSupportedException">While enumerating: as for <see cref="RequestMediationAsync"/>.</exception>
    /// <exception cref="MediationProtocolException">While enumerating: a problem report, a malformed or inconsistent page, or a walk that cannot list everything (see the remarks). The DIDs already yielded are not the whole list.</exception>
    /// <exception cref="DidResolutionException">While enumerating: as for <see cref="RequestMediationAsync"/>.</exception>
    /// <exception cref="TransportException">While enumerating: as for <see cref="RequestMediationAsync"/>.</exception>
    /// <exception cref="ObjectDisposedException">While enumerating: the container was disposed.</exception>
    public IAsyncEnumerable<string> QueryAllRecipientsAsync(string mediatorDid, string from, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
    {
        RequireDids(mediatorDid, from);
        RequireOverride(serviceEndpointOverride);
        return WalkRecipientsAsync(mediatorDid, from, serviceEndpointOverride, ct);
    }

    // ---- operations -----------------------------------------------------------------------

    private async Task<MediationGrant?> RequestMediationCoreAsync(string mediatorDid, string from, Uri? serviceEndpointOverride, CancellationToken ct)
    {
        var (route, version) = await StartAsync(mediatorDid, from, serviceEndpointOverride, ct).ConfigureAwait(false);
        var (request, spec) = CoordinateMediationWire.MediateRequest(version, from, mediatorDid);
        var answer = await Exchange.ExchangeAsync(route, request, spec, mediatorDid, from, ct).ConfigureAwait(false);
        var grant = CoordinateMediationWire.ReadMediateAnswer(version, CoordinateMediationWire.Deserialize(answer, request.Id, spec), request.Id, from);
        Log(LogLevel.Debug, "Mediation request {RequestId} answered: {Outcome}.", MediatorLog.LogId(request.Id), grant is null ? "denied" : "granted");
        return grant;
    }

    private async Task<IReadOnlyList<RecipientUpdateResult>> UpdateRecipientsCoreAsync(string mediatorDid, string from, RecipientUpdate[] sent, Uri? serviceEndpointOverride, CancellationToken ct)
    {
        var (route, version) = await StartAsync(mediatorDid, from, serviceEndpointOverride, ct).ConfigureAwait(false);
        var (request, spec) = CoordinateMediationWire.UpdateRequest(version, from, mediatorDid, sent);
        var answer = await Exchange.ExchangeAsync(route, request, spec, mediatorDid, from, ct).ConfigureAwait(false);
        return CoordinateMediationWire.ReadUpdateAnswer(version, CoordinateMediationWire.Deserialize(answer, request.Id, spec), request.Id, sent);
    }

    private async Task<RecipientQueryResult> QueryRecipientsCoreAsync(string mediatorDid, string from, RecipientQueryPaging? paginate, Uri? serviceEndpointOverride, CancellationToken ct)
    {
        var (route, version) = await StartAsync(mediatorDid, from, serviceEndpointOverride, ct).ConfigureAwait(false);
        return (await QueryPageAsync(route, version, mediatorDid, from, paginate, ct).ConfigureAwait(false)).Page;
    }

    // One route and one version for the whole walk (one operation, design D-3); one page per
    // request, advancing by what the page held. It ends normally only when the mediator says the
    // list is complete: no position reported, or nothing remaining. Anything else that stops it —
    // an empty page while DIDs remain (no progress), MaxWalkerPages reached while DIDs remain — is
    // a MediationProtocolException after the DIDs already yielded, never a silent end.
    private async IAsyncEnumerable<string> WalkRecipientsAsync(string mediatorDid, string from, Uri? serviceEndpointOverride, [EnumeratorCancellation] CancellationToken ct)
    {
        var (route, version) = await StartAsync(mediatorDid, from, serviceEndpointOverride, ct).ConfigureAwait(false);
        var offset = 0;
        for (var page = 1; ; page++)
        {
            var (result, requestId) = await QueryPageAsync(route, version, mediatorDid, from, new RecipientQueryPaging(CoordinateMediationWire.WalkerPageSize, offset), ct).ConfigureAwait(false);
            foreach (var did in result.Dids)
                yield return did;
            if (result.Pagination is not { Remaining: > 0 })
                yield break;
            if (result.Dids.Count == 0)
                throw CoordinateMediationWire.IncompleteWalk(version, requestId, "the mediator sent an empty page but reports DIDs remaining");
            if (page == CoordinateMediationWire.MaxWalkerPages)
                throw CoordinateMediationWire.IncompleteWalk(version, requestId, $"the mediator still reports DIDs remaining after {CoordinateMediationWire.MaxWalkerPages} pages, the most the walk fetches");
            // At most MaxWalkerPages pages of at most MaxPageLimit DIDs each: no overflow.
            offset += result.Dids.Count;
        }
    }

    private async Task<(RecipientQueryResult Page, string RequestId)> QueryPageAsync(MediatorRoute route, CoordinateMediationVersion version, string mediatorDid, string from, RecipientQueryPaging? paginate, CancellationToken ct)
    {
        var (request, spec) = CoordinateMediationWire.QueryRequest(version, from, mediatorDid, paginate);
        var answer = await Exchange.ExchangeAsync(route, request, spec, mediatorDid, from, ct).ConfigureAwait(false);
        return (CoordinateMediationWire.ReadQueryAnswer(version, CoordinateMediationWire.Deserialize(answer, request.Id, spec), request.Id, paginate), request.Id);
    }

    // The route of the operation, resolved once, then the version to speak on it (design §4.4):
    // the pinned one; else the one negotiated with this mediator within the hour; else a Discover
    // Features query on the same route. A query that is not answered fails the call (no guessing).
    private async Task<(MediatorRoute Route, CoordinateMediationVersion Version)> StartAsync(string mediatorDid, string from, Uri? serviceEndpointOverride, CancellationToken ct)
    {
        var route = await Exchange.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket: false, ct).ConfigureAwait(false);
        var pinned = Exchange.Options.CoordinateMediationVersion;
        if (pinned != CoordinateMediationVersion.Negotiate)
            return (route, pinned);
        if (Exchange.Versions.TryGet(mediatorDid, out var remembered))
            return (route, remembered);

        var query = FeatureNegotiation.CreateQuery(from, mediatorDid);
        var answer = await Exchange.ExchangeAsync(route, query, FeatureNegotiation.QuerySpec, mediatorDid, from, ct).ConfigureAwait(false);
        var version = FeatureNegotiation.Choose(CoordinateMediationWire.Deserialize(answer, query.Id, FeatureNegotiation.QuerySpec), query.Id);
        Exchange.Versions.Set(mediatorDid, version);
        Log(LogLevel.Debug, "Coordinate Mediation version negotiated by query {RequestId}: {Version}.", MediatorLog.LogId(query.Id), version);
        return (route, version);
    }

    // ---- arguments (checked before anything is resolved or sent; never quoted) ------------

    private static void RequireDids(string mediatorDid, string from)
    {
        RequireBareDid(mediatorDid, nameof(mediatorDid));
        RequireBareDid(from, nameof(from));
    }

    private static void RequireBareDid(string value, string parameter)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if (!BareDid.IsBareDid(value))
            throw new ArgumentException("The value must be a bare DID (no path, query or fragment) of at most 2048 characters.", parameter);
    }

    private static void RequireOverride(Uri? serviceEndpointOverride)
    {
        if (serviceEndpointOverride is null)
            return;
        if (!serviceEndpointOverride.IsAbsoluteUri)
            throw new ArgumentException("The endpoint override must be an absolute URI.", nameof(serviceEndpointOverride));
        if (!string.IsNullOrEmpty(serviceEndpointOverride.UserInfo))
            throw new ArgumentException("The endpoint override must not carry user information.", nameof(serviceEndpointOverride));
    }

    private static void RequirePaging(RecipientQueryPaging? paginate)
    {
        if (paginate is null)
            return;
        if (paginate.Limit is < 0 or > CoordinateMediationWire.MaxPageLimit)
            throw new ArgumentOutOfRangeException(nameof(paginate), $"The page limit must be between 0 and {CoordinateMediationWire.MaxPageLimit}.");
        if (paginate.Offset < 0)
            throw new ArgumentOutOfRangeException(nameof(paginate), "The page offset must not be negative.");
    }

    // The bound first, from the count alone (L-055); then a private copy, so what is checked is what
    // is sent and what the answer is compared with, whatever the caller does with its list.
    private static RecipientUpdate[] CopyUpdates(IReadOnlyList<RecipientUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var count = updates.Count;
        if (count is < 1 or > CoordinateMediationWire.MaxUpdates)
            throw new ArgumentException($"Between 1 and {CoordinateMediationWire.MaxUpdates} updates are sent in one request.", nameof(updates));

        var copy = new RecipientUpdate[count];
        for (var i = 0; i < count; i++)
            copy[i] = updates[i];
        for (var i = 0; i < copy.Length; i++)
        {
            var update = copy[i];
            if (update is null)
                throw new ArgumentException($"Update {i} is null.", nameof(updates));
            if (!BareDid.IsBareDid(update.RecipientDid))
                throw new ArgumentException($"Update {i} must name a bare DID (no path, query or fragment) of at most 2048 characters.", nameof(updates));
            if (!Enum.IsDefined(update.Action))
                throw new ArgumentException($"Update {i} has an action the protocol does not define.", nameof(updates));
        }
        return copy;
    }

    private void Log(LogLevel level, string message, params object?[] args) => MediatorLog.Write(Logger, level, message, args);
}
