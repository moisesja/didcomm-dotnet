namespace DidComm.Protocols.Routing;

/// <summary>
/// The mediator's directory of recipients it relays for (FR-ROUTE-11). <see cref="ForwardHandler"/>
/// consults it for every forward and silently drops one whose <c>body.next</c> is not registered.
/// </summary>
/// <remarks>
/// <para>
/// Keys are DID subjects (PRD §4.3 <c>DidSubjectOf</c>): the handler reduces <c>next</c> — which
/// may be a DID URL naming a routing key — to its bare DID before calling, so implementations
/// compare bare DIDs and need no parsing of their own.
/// </para>
/// <para>
/// The default is <see cref="InMemoryForwardRecipientRegistry"/>, a host-filled directory. A
/// Coordinate Mediation 3.0 grant store (FR-MED-05) replaces it by registering its own
/// implementation before <c>AddRoutingMediator</c>. Implementations are singletons, MUST be
/// thread-safe, and SHOULD answer in work that does not depend on whether the DID is registered
/// (the lookup must not become a registration oracle).
/// </para>
/// </remarks>
public interface IForwardRecipientRegistry
{
    /// <summary>Whether this mediator relays forwards addressed to <paramref name="recipientDid"/>.</summary>
    /// <param name="recipientDid">A bare DID — the DID subject of the forward's <c>body.next</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<bool> IsRegisteredAsync(string recipientDid, CancellationToken ct);
}
