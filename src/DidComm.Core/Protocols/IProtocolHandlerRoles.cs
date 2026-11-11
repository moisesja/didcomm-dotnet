namespace DidComm.Protocols;

/// <summary>
/// Optional companion to <see cref="IProtocolHandler"/>: the roles this agent plays in the
/// protocol. Discover Features 2.0 <c>disclose</c> entries carry an optional <c>roles</c> array
/// (<c>{"feature-type":"protocol","id":"…","roles":["…"]}</c>); the default protocol feature
/// provider copies this list into the disclosure for handlers that implement the interface and
/// omits <c>roles</c> for the rest. Example: <see cref="Routing.ForwardHandler"/> discloses
/// <c>routing/2.0</c> with <c>roles: ["mediator"]</c> (FR-ROUTE-11).
/// </summary>
public interface IProtocolHandlerRoles
{
    /// <summary>The roles this handler fulfils, as the protocol names them (e.g. <c>mediator</c>).</summary>
    IReadOnlyList<string> Roles { get; }
}
