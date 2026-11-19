using DidComm.Exceptions;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Mediator.Coordination;

/// <summary>
/// The mediator role of Coordinate Mediation 2.0 (<c>https://didcomm.org/coordinate-mediation/2.0</c>,
/// FR-MED-10), the version the Identus and Blocktrust mediators and their wallets speak. Answers
/// <c>mediate-request</c> with <c>mediate-grant</c> or <c>mediate-deny</c>, <c>keylist-update</c>
/// with <c>keylist-update-response</c>, and <c>keylist-query</c> with <c>keylist</c>. Registered by
/// <c>AddCoordinateMediation()</c> next to <see cref="CoordinateMediationHandler"/> unless
/// <see cref="CoordinateMediationOptions.EnableV2"/> is <c>false</c>; Discover Features then
/// discloses both versions with <c>roles: ["mediator"]</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One tenant in both versions.</strong> The handler works over the same
/// <see cref="IMediationStore"/>, <see cref="IMediationPolicy"/> and <see cref="IMediatorIdentity"/>
/// as the 3.0 handler, with the same rules. A peer that enrolled in one version holds the same
/// grant in the other, and a DID it registered through <c>keylist-update</c> is listed by a 3.0
/// <c>recipient-query</c> and removed by a 3.0 <c>recipient-update</c>, and the other way round.
/// Only the message names, the list a query returns (<c>keys</c>) and the shape of
/// <c>routing_did</c> differ.
/// </para>
/// <para>
/// <strong>One routing DID.</strong> A 2.0 <c>mediate-grant</c> carries <c>routing_did</c> as a
/// single string: the <em>first</em> routing DID of the grant, which is the first of
/// <see cref="IMediatorIdentity.RoutingDids"/> when the grant was made. Order those DIDs with the
/// primary one first (see <see cref="CoordinateMediationOptions.RoutingDids"/>). A peer that
/// already holds a grant gets it again unchanged, so its first DID stays the one it was granted
/// even after the configuration is reordered. Forwards addressed to any of the mediator's routing
/// DIDs are relayed whichever one a grant named.
/// </para>
/// <para>
/// <strong>Who is a peer.</strong> Only a request with an authcrypt layer is acted on, and the peer
/// is the DID of its authcrypt sender key, never the plaintext <c>from</c>. A plaintext, anoncrypt,
/// signed-only or <c>anoncrypt(sign)</c> request is dropped before its body is read: nothing is
/// granted or changed, no reply is produced, and one rate-limited log line records the id.
/// </para>
/// <para>
/// <strong>Replies.</strong> A reply is from the DID that decrypted the request, to the peer, on
/// the request's thread (<c>thid</c> = the request's <c>thid</c>, else its <c>id</c>; FR-MED-04),
/// and always uses the canonical type string. The handler returns it whether or not the request
/// carried <c>return_route</c>: the endpoint decides how it travels, and without a return route
/// over HTTP it has nowhere to go, though the update has been applied (some 2.0 wallets send
/// <c>keylist-update</c> that way and ignore the answer). <c>keylist-update</c> results are
/// <c>success</c>, <c>no_change</c>, <c>client_error</c> and <c>server_error</c>, decided as for
/// 3.0's <c>recipient-update</c> (see <see cref="CoordinateMediationHandler"/>); a <c>keylist</c>
/// page is clamped to <see cref="CoordinateMediationOptions.MaxPageSize"/> and always carries
/// <c>pagination</c>. The protocol defines no problem reports, and none is sent.
/// </para>
/// <para>
/// <strong>Errors.</strong> A malformed body from an authenticated peer throws
/// <see cref="MalformedMessageException"/> out of dispatch, like any malformed message; its text
/// names the member and the rule and never a value from the message. A store or policy fault never
/// leaves the handler. Messages of the protocol that a mediator does not act on (the four answers,
/// or a name it does not know) are ignored.
/// </para>
/// <para>
/// <strong>Replay.</strong> 2.0 is a mediator-service protocol (FR-RR-08), so its requests must be
/// authcrypted and carry a fresh <c>created_time</c>. The handler does not check for replayed
/// requests itself: front it with an endpoint that runs replay admission (the library's
/// <c>MapDidCommMediator</c> and the registry-aware <c>MapDidCommEndpoint</c> do), or call
/// <see cref="DidComm.Transports.ReturnRoute.IInboundReplayGuard.TryAdmit"/> yourself before dispatch
/// (see <see cref="ProtocolDispatcher"/>).
/// </para>
/// </remarks>
public sealed class CoordinateMediation20Handler : IProtocolHandler, IProtocolHandlerRoles
{
    private readonly MediationCoordinator _coordinator;

    /// <summary>
    /// Create the handler. Called by the container; hosts register it with
    /// <c>AddCoordinateMediation()</c>. A host that builds its handlers by hand passes the same store,
    /// policy and identity it gives <see cref="CoordinateMediationHandler"/>, so both versions serve
    /// one set of grants.
    /// </summary>
    /// <param name="store">The grant and recipient registry, shared with the 3.0 handler.</param>
    /// <param name="policy">Decides grants and registrations.</param>
    /// <param name="identity">Supplies the routing DIDs; a 2.0 grant carries the first.</param>
    /// <param name="options">Page size and message bounds.</param>
    /// <param name="logger">Optional logger. Lines carry message ids and type names only.</param>
    /// <exception cref="ArgumentException"><paramref name="options"/> fail <see cref="CoordinateMediationOptions.Validate"/>.</exception>
    public CoordinateMediation20Handler(
        IMediationStore store,
        IMediationPolicy policy,
        IMediatorIdentity identity,
        IOptions<CoordinateMediationOptions> options,
        ILogger<CoordinateMediation20Handler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _coordinator = new MediationCoordinator(store, policy, identity, options.Value, logger);
    }

    /// <summary>The Protocol Identifier URI: <c>https://didcomm.org/coordinate-mediation/2.0</c>.</summary>
    public string ProtocolUri => CoordinateMediation20.ProtocolUri;

    /// <inheritdoc />
    public IReadOnlyList<string> Roles { get; } = Array.AsReadOnly(new[] { CoordinateMediation.MediatorRole });

    /// <inheritdoc />
    public async Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        // (a) Authcrypt or nothing (FR-MED-05). Before the body is read, so every unauthenticated
        //     message gets the same treatment whatever it contains.
        if (!_coordinator.TryGetPeer(context.Received, message.Id, out var peerDid, out var localDid))
            return null;

        var thid = CoordinateMediation.ResponseThreadId(message);

        // (b) mediate-request → mediate-grant (the grant's first routing DID, as a string) | mediate-deny.
        if (CoordinateMediation20.TryParseMediateRequest(message))
        {
            var (outcome, routingDids) = await _coordinator.RequestAsync(peerDid, localDid, message.Id, context, ct).ConfigureAwait(false);
            return outcome switch
            {
                // Chosen inside the reply's fault boundary: a grant a host store returns without
                // any routing DID fails the builder (logged by type, no reply), never dispatch.
                MediateOutcome.Granted => Reply(message, () => CoordinateMediation20.CreateGrant(localDid, peerDid, First(routingDids), thid)),
                MediateOutcome.Denied => Reply(message, () => CoordinateMediation20.CreateDeny(localDid, peerDid, thid)),
                _ => null,
            };
        }

        // (c) keylist-update → keylist-update-response, one result per entry in input order. The
        //     entry bound is part of the parse: a longer list is refused from its length, before
        //     any entry is read.
        if (CoordinateMediation20.TryParseKeylistUpdate(message, _coordinator.MaxUpdatesPerMessage, out var updates))
        {
            var updated = await _coordinator.UpdateAsync(peerDid, localDid, updates, message.Id, context, ct).ConfigureAwait(false);
            return Reply(message, () => CoordinateMediation20.CreateKeylistUpdateResponse(localDid, peerDid, updated, thid));
        }

        // (d) keylist-query → keylist.
        if (CoordinateMediation20.TryParseKeylistQuery(message, out var paginate))
        {
            var page = await _coordinator.QueryAsync(peerDid, paginate, message.Id, ct).ConfigureAwait(false);
            return page is { } p
                ? Reply(message, () => CoordinateMediation20.CreateKeylist(localDid, peerDid, p.Dids, p.Pagination, thid))
                : null;
        }

        // (e) An answer type, or a name this version does not define: not for the mediator role.
        _coordinator.Log(LogLevel.Debug, "Coordinate-mediation 2.0 message {MessageId} ignored: not a request the mediator role answers.", MediationCoordinator.LogId(message.Id));
        return null;
    }

    // The 2.0 grant names one routing DID: the first. An empty list is a host store's fault; the
    // builder refuses the null with an ArgumentException, which Reply logs by type.
    private static string First(IReadOnlyList<string> routingDids) => routingDids.Count > 0 ? routingDids[0] : null!;

    // Building a reply validates what goes on the wire (lengths, enum values). Values that fail
    // came from a host store or identity, not from the peer, so any failure here is a server
    // fault: logged by type, no reply, never thrown through dispatch.
    private Message? Reply(Message request, Func<Message> build)
    {
        try
        {
            return build();
        }
        catch (Exception ex)
        {
            _coordinator.Log(LogLevel.Error, "Coordinate-mediation 2.0 message {MessageId} not answered: the reply could not be built ({ExceptionType}).", MediationCoordinator.LogId(request.Id), ex.GetType().Name);
            return null;
        }
    }
}
