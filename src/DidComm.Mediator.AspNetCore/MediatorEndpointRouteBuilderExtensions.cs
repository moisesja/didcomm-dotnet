using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace DidComm.Mediator.AspNetCore;

/// <summary>Maps the endpoints of a mediator registered with <c>AddMediator()</c>.</summary>
public static class MediatorEndpointRouteBuilderExtensions
{
    /// <summary>
    /// The fixed out-of-band id under which the invitation endpoint serves the mediator's
    /// invitation: the <c>_oobid</c> query value of the invitation URL. It is public and reveals only
    /// the mediator's primary routing DID, which every recipient's DID document names anyway.
    /// </summary>
    public const string InvitationOobId = "mediator";

    /// <summary>
    /// Map the mediator's endpoints (FR-MED-07): <c>POST {pattern}</c> receives DIDComm messages
    /// over HTTP, <c>GET {pattern}</c> accepts WebSocket connections — with same-socket replies on
    /// and STOMP off for this endpoint only, so pickup and live delivery work over it — and
    /// <c>GET {pattern}/oob</c> serves the mediator's out-of-band invitation under
    /// <see cref="InvitationOobId"/>. Conventions applied to the result (rate limiting, host
    /// filters) apply to all three. Call <c>UseWebSockets()</c> before mapping.
    /// </summary>
    /// <param name="endpoints">The ASP.NET Core endpoint route builder.</param>
    /// <param name="pattern">The route pattern, starting with <c>/</c> (for example <c>"/didcomm"</c>).</param>
    /// <returns>A builder whose conventions apply to every endpoint mapped here.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> or <paramref name="pattern"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException"><c>AddMediator()</c> was not called on the services.</exception>
    public static IEndpointConventionBuilder MapDidCommMediator(this IEndpointRouteBuilder endpoints, string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(pattern);
        throw new NotImplementedException("#95 D2");
    }
}
