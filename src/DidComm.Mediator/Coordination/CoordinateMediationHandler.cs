using DidComm.Exceptions;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Mediator.Coordination;

/// <summary>
/// The mediator role of Coordinate Mediation 3.0 (FR-MED-01..06, -08). Answers
/// <c>mediate-request</c> with <c>mediate-grant</c> or <c>mediate-deny</c>,
/// <c>recipient-update</c> with <c>recipient-update-response</c>, and <c>recipient-query</c> with
/// <c>recipient</c>. Registered by <c>AddCoordinateMediation()</c>; Discover Features then
/// discloses the protocol with <c>roles: ["mediator"]</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Who is a peer.</strong> Only a request with an authcrypt layer is acted on, and the peer
/// is the DID of its authcrypt sender key, never the plaintext <c>from</c>. A plaintext, anoncrypt,
/// signed-only or <c>anoncrypt(sign)</c> request is dropped before its body is read: nothing is
/// granted or changed, no reply is produced, and one rate-limited log line records the id.
/// </para>
/// <para>
/// <strong>Replies.</strong> A reply is from the DID that decrypted the request, to the peer, on
/// the request's thread (<c>thid</c> = the request's <c>thid</c>, else its <c>id</c>; FR-MED-04).
/// The handler returns it whether or not the request opened a return-route session
/// (<see cref="ProtocolContext.Session"/>): the endpoint decides how it travels (FR-MED-03).
/// </para>
/// <para>
/// <strong>Rules.</strong> A second <c>mediate-request</c> from a peer that holds a grant returns
/// the same grant; <c>mediate-deny</c> comes from the <see cref="IMediationPolicy"/> or from a full
/// store. In a <c>recipient-update</c> each entry gets its own result, in input order:
/// <c>success</c>; <c>no_change</c> (already registered by this peer); <c>client_error</c> (not a
/// bare DID, held by another grant, a removal of something this peer does not hold, one of the
/// mediator's own DIDs, refused by the policy, over the per-grant limit, or no grant);
/// <c>server_error</c> (a store, policy or identity-provider fault, or the mediator-wide limit; when
/// the mediator's own DIDs cannot be read, every entry gets it and nothing is applied). Because the protocol
/// fixes those values, a peer with a grant can learn that a DID is registered by someone else; a
/// policy that demands proof of ownership removes that. <c>recipient-query</c> pages are clamped
/// to <see cref="CoordinateMediationOptions.MaxPageSize"/> and always carry <c>pagination</c>.
/// </para>
/// <para>
/// <strong>Errors.</strong> A malformed body from an authenticated peer throws
/// <see cref="MalformedMessageException"/> out of dispatch, like any malformed message; its text
/// names the member and the rule and never a value from the message. A store or policy fault never
/// leaves the handler. Messages of the protocol that a mediator does not act on (the three
/// responses, or a name it does not know) are ignored.
/// </para>
/// </remarks>
public sealed class CoordinateMediationHandler : IProtocolHandler, IProtocolHandlerRoles
{
    private readonly MediationCoordinator _coordinator;

    /// <summary>Create the handler. Called by the container; hosts register it with <c>AddCoordinateMediation()</c>.</summary>
    /// <param name="store">The grant and recipient registry.</param>
    /// <param name="policy">Decides grants and registrations.</param>
    /// <param name="identity">Supplies the routing DIDs a grant carries.</param>
    /// <param name="options">Page size and message bounds.</param>
    /// <param name="logger">Optional logger. Lines carry message ids and type names only.</param>
    /// <exception cref="ArgumentException"><paramref name="options"/> fail <see cref="CoordinateMediationOptions.Validate"/>.</exception>
    public CoordinateMediationHandler(
        IMediationStore store,
        IMediationPolicy policy,
        IMediatorIdentity identity,
        IOptions<CoordinateMediationOptions> options,
        ILogger<CoordinateMediationHandler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _coordinator = new MediationCoordinator(store, policy, identity, options.Value, logger);
    }

    /// <inheritdoc />
    public string ProtocolUri => CoordinateMediation.ProtocolUri;

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

        // (b) mediate-request → mediate-grant | mediate-deny.
        if (CoordinateMediation.TryParseMediateRequest(message))
        {
            var (outcome, routingDids) = await _coordinator.RequestAsync(peerDid, localDid, message.Id, context, ct).ConfigureAwait(false);
            return outcome switch
            {
                MediateOutcome.Granted => Reply(message, () => CoordinateMediation.CreateGrant(localDid, peerDid, routingDids, thid)),
                MediateOutcome.Denied => Reply(message, () => CoordinateMediation.CreateDeny(localDid, peerDid, thid)),
                _ => null,
            };
        }

        // (c) recipient-update → recipient-update-response, one result per entry in input order.
        //     The entry bound is part of the parse: a longer list is refused from its length,
        //     before any entry is read.
        if (CoordinateMediation.TryParseRecipientUpdate(message, _coordinator.MaxUpdatesPerMessage, out var updates))
        {
            var updated = await _coordinator.UpdateAsync(peerDid, localDid, updates, message.Id, context, ct).ConfigureAwait(false);
            return Reply(message, () => CoordinateMediation.CreateRecipientUpdateResponse(localDid, peerDid, updated, thid));
        }

        // (d) recipient-query → recipient.
        if (CoordinateMediation.TryParseRecipientQuery(message, out var paginate))
        {
            var page = await _coordinator.QueryAsync(peerDid, paginate, message.Id, ct).ConfigureAwait(false);
            return page is { } p
                ? Reply(message, () => CoordinateMediation.CreateRecipient(localDid, peerDid, p.Dids, p.Pagination, thid))
                : null;
        }

        // (e) A response type, or a name this version does not define: not for the mediator role.
        _coordinator.Log(LogLevel.Debug, "Coordinate-mediation message {MessageId} ignored: not a request the mediator role answers.", MediationCoordinator.LogId(message.Id));
        return null;
    }

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
            _coordinator.Log(LogLevel.Error, "Coordinate-mediation message {MessageId} not answered: the reply could not be built ({ExceptionType}).", MediationCoordinator.LogId(request.Id), ex.GetType().Name);
            return null;
        }
    }
}
