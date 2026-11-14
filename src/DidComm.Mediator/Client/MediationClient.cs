using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
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
    /// <exception cref="InvalidOperationException">A WebSocket route this client cannot receive on, a mediator reached through routing, no service endpoint resolver, or too many requests already waiting; nothing was sent.</exception>
    /// <exception cref="TimeoutException">No answer within <see cref="MediatorClientOptions.RequestTimeout"/> (or none in the HTTP response); the message names the request id. Retry.</exception>
    /// <exception cref="MediationNotSupportedException">The mediator speaks neither Coordinate Mediation 3.0 nor 2.0.</exception>
    /// <exception cref="MediationProtocolException">The mediator answered with a problem report, or with an answer that is malformed or does not match the request.</exception>
    /// <exception cref="TransportException">The transport failed (over HTTP with status 200: the mediator accepted the request but its answer was too large, stalled or unreadable; retry).</exception>
    /// <exception cref="ObjectDisposedException">The container was disposed.</exception>
    public Task<MediationGrant?> RequestMediationAsync(string mediatorDid, string from, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
        => throw new NotImplementedException("#96 engineer B: MediationClient.RequestMediationAsync (design §4.4, §4.5).");

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
        => throw new NotImplementedException("#96 engineer B: MediationClient.UpdateRecipientsAsync (design §4.5).");

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
        => throw new NotImplementedException("#96 engineer B: MediationClient.QueryRecipientsAsync (design §4.5).");

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
        => throw new NotImplementedException("#96 engineer B: MediationClient.QueryAllRecipientsAsync (design §4.5, page walker).");
}
