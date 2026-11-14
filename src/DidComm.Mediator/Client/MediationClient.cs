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
/// <c>ws</c>/<c>wss</c>); a mediator reached through routing keys or another mediator is refused.
/// Pass <c>serviceEndpointOverride</c> to use another of its addresses, for example its
/// <c>wss</c> one when its DID document lists <c>https</c> first.
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
/// <strong>Retrying is safe.</strong> A <see cref="TimeoutException"/> means the answer did not
/// arrive, not that nothing happened: the mediator may have acted. Repeat the call. A repeated
/// <c>mediate-request</c> is answered with the same grant and a repeated <c>add</c> with
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
/// await mediation.UpdateRecipientsAsync(mediatorDid, myDid,
///     new[] { new RecipientUpdate(publicDid, RecipientUpdateAction.Add) });
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
        : this(new MediatorClientChannel(client, options ?? new MediatorClientOptions(), receivesInbound: false, TimeProvider.System, SingleLoggerFactory.For(logger)), logger)
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
    /// <exception cref="InvalidOperationException">A WebSocket route this client cannot receive on, a mediator reached through routing, no service endpoint resolver, or too many requests already waiting; nothing was sent.</exception>
    /// <exception cref="TimeoutException">No answer within <see cref="MediatorClientOptions.RequestTimeout"/> (or none in the HTTP response); the message names the request id. Retry. (Running out of time while finding the mediator's endpoint names none: nothing was sent.) The unanswered request may be the Discover Features query: a mediator that never answers one needs a pinned <see cref="MediatorClientOptions.CoordinateMediationVersion"/>.</exception>
    /// <exception cref="MediationNotSupportedException">The mediator speaks neither Coordinate Mediation 3.0 nor 2.0.</exception>
    /// <exception cref="MediationProtocolException">The mediator answered with a problem report, or with an answer that is malformed or does not match the request.</exception>
    /// <exception cref="TransportException">The transport failed (over HTTP with status 200: the mediator accepted the request but its answer was too large, stalled or unreadable; retry).</exception>
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
    /// <returns>One result per update, in the order given (the client checks that the mediator answered each one).</returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, <paramref name="updates"/> is empty or longer than 1024 or has an invalid entry (the message names its index), or <paramref name="serviceEndpointOverride"/> is relative or carries user information.</exception>
    /// <exception cref="InvalidOperationException">As for <see cref="RequestMediationAsync"/>; nothing was sent.</exception>
    /// <exception cref="TimeoutException">No answer in time; retry (see the class remarks for what a repeat answers).</exception>
    /// <exception cref="MediationNotSupportedException">The mediator speaks neither Coordinate Mediation 3.0 nor 2.0.</exception>
    /// <exception cref="MediationProtocolException">A problem report (for example: you hold no grant), or an answer that does not match the updates sent.</exception>
    /// <exception cref="TransportException">The transport failed.</exception>
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
    /// <param name="paginate">Which page: <see cref="RecipientQueryPaging.Limit"/> 0 to 1000, <see cref="RecipientQueryPaging.Offset"/> 0 or more; <c>null</c> for the mediator's default page.</param>
    /// <param name="serviceEndpointOverride">The mediator address to use instead of the first one in its DID document (see <see cref="RequestMediationAsync"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The page; its position is checked against the request.</returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, <paramref name="paginate"/> is out of range, or <paramref name="serviceEndpointOverride"/> is relative or carries user information.</exception>
    /// <exception cref="InvalidOperationException">As for <see cref="RequestMediationAsync"/>; nothing was sent.</exception>
    /// <exception cref="TimeoutException">No answer in time; retry.</exception>
    /// <exception cref="MediationNotSupportedException">The mediator speaks neither Coordinate Mediation 3.0 nor 2.0.</exception>
    /// <exception cref="MediationProtocolException">A problem report, or a page that is larger than asked for or whose position does not match the request.</exception>
    /// <exception cref="TransportException">The transport failed.</exception>
    /// <exception cref="ObjectDisposedException">The container was disposed.</exception>
    public Task<RecipientQueryResult> QueryRecipientsAsync(string mediatorDid, string from, RecipientQueryPaging? paginate = null, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
    {
        RequireDids(mediatorDid, from);
        RequirePaging(paginate);
        RequireOverride(serviceEndpointOverride);
        return QueryRecipientsCoreAsync(mediatorDid, from, paginate, serviceEndpointOverride, ct);
    }

    /// <summary>
    /// Every DID registered under your grant, fetched 100 per page while you enumerate, until the
    /// mediator reports none remaining (at most 1000 pages). The arguments are checked when you call
    /// this method; nothing is sent before the first <c>MoveNextAsync</c>.
    /// </summary>
    /// <param name="mediatorDid">The mediator's DID: a bare DID.</param>
    /// <param name="from">Your DID, the one you enrolled as: a bare DID whose keys this agent holds.</param>
    /// <param name="serviceEndpointOverride">The mediator address to use instead of the first one in its DID document (see <see cref="RequestMediationAsync"/>).</param>
    /// <param name="ct">Cancellation token (also reachable through <c>WithCancellation</c>).</param>
    /// <returns>The DIDs, page by page, in the order the mediator lists them.</returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, or <paramref name="serviceEndpointOverride"/> is relative or carries user information.</exception>
    /// <exception cref="InvalidOperationException">While enumerating: as for <see cref="RequestMediationAsync"/>.</exception>
    /// <exception cref="TimeoutException">While enumerating: a page did not arrive in time.</exception>
    /// <exception cref="MediationProtocolException">While enumerating: a problem report or an inconsistent page.</exception>
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
        return await QueryPageAsync(route, version, mediatorDid, from, paginate, ct).ConfigureAwait(false);
    }

    // One route and one version for the whole walk (one operation, design D-3); one page per
    // request. Stops when the mediator reports no position or nothing remaining, on an empty page
    // (no progress, whatever it reports), or after MaxWalkerPages.
    private async IAsyncEnumerable<string> WalkRecipientsAsync(string mediatorDid, string from, Uri? serviceEndpointOverride, [EnumeratorCancellation] CancellationToken ct)
    {
        var (route, version) = await StartAsync(mediatorDid, from, serviceEndpointOverride, ct).ConfigureAwait(false);
        var offset = 0;
        for (var page = 0; page < CoordinateMediationWire.MaxWalkerPages; page++)
        {
            var result = await QueryPageAsync(route, version, mediatorDid, from, new RecipientQueryPaging(CoordinateMediationWire.WalkerPageSize, offset), ct).ConfigureAwait(false);
            foreach (var did in result.Dids)
                yield return did;
            if (result.Pagination is not { Remaining: > 0 } || result.Dids.Count == 0)
                yield break;
            offset += result.Dids.Count;
        }
        Log(LogLevel.Warning, "Recipient listing stopped after {Pages} pages; the mediator reports more.", CoordinateMediationWire.MaxWalkerPages);
    }

    private async Task<RecipientQueryResult> QueryPageAsync(MediatorRoute route, CoordinateMediationVersion version, string mediatorDid, string from, RecipientQueryPaging? paginate, CancellationToken ct)
    {
        var (request, spec) = CoordinateMediationWire.QueryRequest(version, from, mediatorDid, paginate);
        var answer = await Exchange.ExchangeAsync(route, request, spec, mediatorDid, from, ct).ConfigureAwait(false);
        return CoordinateMediationWire.ReadQueryAnswer(version, CoordinateMediationWire.Deserialize(answer, request.Id, spec), request.Id, paginate);
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
