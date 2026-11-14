using DidComm.AspNetCore;
using DidComm.Mediator.Pipeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// L-014: alias the static OutOfBand API class so the same-named namespace doesn't shadow it.
using OutOfBandApi = DidComm.Protocols.OutOfBand.OutOfBand;

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

    /// <summary>The path segment, below the mapped pattern, of the invitation endpoint.</summary>
    private const string InvitationSegment = "/oob";

    /// <summary>
    /// Map the endpoints of the mediator registered with <c>AddMediator()</c> under one route
    /// pattern:
    /// <list type="bullet">
    /// <item><description><c>POST {pattern}</c> receives DIDComm messages over HTTP. It is the
    /// registry-aware <c>MapDidCommEndpoint(pattern)</c>, unchanged: a forward is answered
    /// <c>202</c> before anything is relayed or queued (FR-ROUTE-11), and Coordinate Mediation and
    /// pickup requests sent with <c>return_route: all</c> get their answer as the <c>200</c> body
    /// (FR-RR-04).</description></item>
    /// <item><description><c>GET {pattern}</c> accepts WebSocket connections. Replies and pickup
    /// deliveries — including live delivery — come back on the same socket (FR-RR-05).</description></item>
    /// <item><description><c>GET {base}/oob</c>, where <c>{base}</c> is the pattern without its
    /// trailing <c>/</c> (so <c>"/"</c> gives <c>/oob</c>), serves the mediator's out-of-band
    /// invitation for <c>?_oobid=</c><see cref="InvitationOobId"/> (FR-OOB-04).</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Before mapping.</b> Register the mediator with <c>services.AddDidComm(b =&gt; b.AddMediator(…))</c>
    /// — without it this method throws — and call <c>app.UseWebSockets()</c>, or the WebSocket
    /// endpoint answers every connection attempt with <c>400</c>. Map each pattern once. The
    /// <c>DIDCommMessaging</c> service of each routing DID names where senders reach these
    /// endpoints: the public address followed by <paramref name="pattern"/> (<c>https://</c> for
    /// HTTP, <c>wss://</c> for the WebSocket).
    /// </para>
    /// <para>
    /// <b>Put rate limiting or other admission control in front.</b> Anyone who can reach these
    /// endpoints can post forwards and fetch the invitation, and any authcrypt sender with a fresh
    /// DID can ask for mediation, so a flood of new DIDs can use up the replay cache and the
    /// mediation and queue bounds (an availability limit, not a confidentiality one). Conventions
    /// applied to the returned builder apply to all three endpoints:
    /// <code>
    /// builder.Services.AddRateLimiter(o =&gt; o.AddFixedWindowLimiter("didcomm", w =&gt;
    /// {
    ///     w.PermitLimit = 100;
    ///     w.Window = TimeSpan.FromSeconds(1);
    /// }));
    /// // …
    /// app.UseWebSockets();
    /// app.UseRateLimiter();
    /// app.MapDidCommMediator("/didcomm").RequireRateLimiting("didcomm");
    /// </code>
    /// A rate limiter counts HTTP requests, so on the WebSocket endpoint it counts connections, not
    /// the messages sent on one. Do not apply a convention that must be unique per endpoint (such as
    /// an endpoint name): the three endpoints would all carry it.
    /// </para>
    /// <para>
    /// <b>The WebSocket endpoint.</b> It turns same-socket replies on for itself only — the host's
    /// <c>DidCommReceiveOptions.AllowSameSocketReplies</c> is not changed, so the host's other
    /// WebSocket endpoints stay one-way — because without them pickup over a WebSocket is silent.
    /// For the same reason it always takes one bare packed message per WebSocket message and ignores
    /// a global <c>DidCommReceiveOptions.UseStomp</c>: a STOMP connection can never carry a reply.
    /// A host that wants STOMP maps <c>MapDidCommWebSocket</c> on another pattern. The rules for
    /// replies are those of every registry-aware endpoint: a connection becomes a session only at an
    /// authcrypted message with <c>return_route: all</c> that passed replay admission, and its
    /// replies go only to the DID that opened it.
    /// </para>
    /// <para>
    /// <b>The invitation URL.</b> Publish
    /// <c>OutOfBand.ToShortUrl($"{publicBase}{base}/oob", MediatorEndpointRouteBuilderExtensions.InvitationOobId)</c>,
    /// where <c>publicBase</c> is the address the mediator is reached at, without a trailing
    /// <c>/</c> — for <c>https://mediator.example</c> and the pattern <c>"/didcomm"</c> that is
    /// <c>https://mediator.example/didcomm/oob?_oobid=mediator</c>. The invitation (from the primary
    /// routing DID, goal code <c>request-mediate</c>) is built when the mediator starts, after its
    /// routing DIDs passed the startup checks; until then the endpoint answers <c>404</c>. It answers
    /// <c>400</c> without an <c>_oobid</c> and <c>404</c> for any other id.
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The ASP.NET Core endpoint route builder.</param>
    /// <param name="pattern">The route pattern, starting with <c>/</c> (for example <c>"/didcomm"</c>, or <c>"/"</c>).</param>
    /// <returns>A builder whose conventions apply to every endpoint mapped here.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> or <paramref name="pattern"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="pattern"/> does not start with <c>/</c>, or it (or the invitation path below it) is not a valid route pattern.</exception>
    /// <exception cref="InvalidOperationException"><c>AddMediator()</c> was not called on the services.</exception>
    public static IEndpointConventionBuilder MapDidCommMediator(this IEndpointRouteBuilder endpoints, string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Length == 0 || pattern[0] != '/')
            throw new ArgumentException("The mediator's route pattern must start with '/'.", nameof(pattern));

        var basePath = pattern.TrimEnd('/');
        var invitationPath = basePath + InvitationSegment;

        // Parse both patterns before mapping anything: a pattern routing refuses leaves no endpoint
        // of the mediator mapped (the ASP.NET Core Map* methods parse the same way).
        EnsureRoutePattern(pattern);
        EnsureRoutePattern(invitationPath);

        var services = endpoints.ServiceProvider;
        if (services.GetService<MediatorMarker>() is null)
        {
            throw new InvalidOperationException(
                "MapDidCommMediator requires AddMediator(): register the mediator with services.AddDidComm(b => b.AddMediator(...)) " +
                "before mapping its endpoints.");
        }
        var invitation = new MediatorInvitationStore(services.GetRequiredService<MediatorInvitationHolder>());

        var mapped = new CompositeEndpointConventionBuilder(new[]
        {
            // HTTP: the public registry-aware endpoint, unchanged — the transport acknowledgement
            // (202 committed before the forward is relayed), InboundAdmission and the 6-argument
            // dispatch with the request's session.
            endpoints.MapDidCommEndpoint(pattern),
            // WebSocket: the same receive loop as MapDidCommWebSocket(pattern), with same-socket
            // replies on and STOMP off for this endpoint only.
            DidCommEndpointRouteBuilderExtensions.MapDidCommWebSocketCore(endpoints, pattern, mediatorEndpoint: true),
            endpoints.MapDidCommOobEndpoint(invitationPath, invitation),
        });

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("DidComm.Mediator.AspNetCore");
        SafeLog.Write(logger, LogLevel.Information,
            "Mediator endpoints mapped: POST {HttpPattern} (HTTP), GET {WebSocketPattern} (WebSocket, same-socket replies on, STOMP off), GET {InvitationPath}?{OobIdParameter}={InvitationOobId} (out-of-band invitation).",
            pattern, pattern, invitationPath, OutOfBandApi.OobIdParameter, InvitationOobId);

        return mapped;
    }

    private static void EnsureRoutePattern(string pattern)
    {
        try
        {
            RoutePatternFactory.Parse(pattern);
        }
        catch (RoutePatternException ex)
        {
            throw new ArgumentException("The mediator's route pattern, or the invitation path below it, is not a valid route pattern.", nameof(pattern), ex);
        }
    }
}
