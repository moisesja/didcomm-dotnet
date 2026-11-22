using System.Net.WebSockets;
using DidComm.Protocols.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DidComm.AspNetCore;

/// <summary>
/// The first step of both <c>MapDidCommWebSocket</c> endpoints: the WebSocket handshake. A handshake
/// that fails, before the endpoint has done anything with the connection, is refused with
/// <c>503 Service Unavailable</c> (RFC 6455 §4.2.2 lets a server refuse the handshake with an HTTP
/// error) instead of escaping to the server, which would answer 500 and log an Error line with a
/// stack trace for every attempt. The usual cause is a server limit, such as Kestrel's
/// <c>Limits.MaxConcurrentUpgradedConnections</c>, which any client can reach by opening
/// connections, so the refusal line is rate-limited (<see cref="WebSocketRefusalLog"/>). A client
/// that went away during the handshake gets nothing and one Debug line. Every fault after a
/// successful accept stays on the receive loop's own path.
/// </summary>
internal static class WebSocketAccepting
{
    /// <summary>
    /// Accept the WebSocket of <paramref name="httpContext"/>, or refuse it: <c>null</c> when the
    /// handshake failed (answered 503 while the response had not started) or the client went away
    /// during it (nothing answered). The caller then returns.
    /// </summary>
    /// <param name="httpContext">The WebSocket request.</param>
    /// <param name="refusals">The mapped endpoint's refusal line.</param>
    /// <param name="logger">Optional logger.</param>
    internal static async Task<WebSocket?> AcceptAsync(HttpContext httpContext, WebSocketRefusalLog refusals, ILogger? logger)
    {
        try
        {
            return await httpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            // The client went away mid-handshake: there is nobody to answer.
            SafeLog.Write(logger, LogLevel.Debug, "MapDidCommWebSocket: the client went away during the WebSocket handshake ({ExceptionType}); nothing is answered.", ex.GetType().Name);
            return null;
        }
        catch (Exception ex)
        {
            // The handshake never completed and nothing of the connection was used. The exception is
            // the server's (a limit reached, most often), not the client's, so the client is told to
            // try later; its text and stack stay out of the logs. The handshake had already set its
            // own response headers (Connection: Upgrade, Upgrade, Sec-WebSocket-Accept): they are
            // cleared, so the refusal is a plain error response, as the server's own 500 was.
            var answered = !httpContext.Response.HasStarted;
            if (answered)
            {
                httpContext.Response.Clear();
                httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
            refusals.Refused(logger, ex.GetType().Name, answered);
            return null;
        }
    }
}

/// <summary>
/// The handshake-refusal line of one mapped WebSocket endpoint. Any client can make the handshake
/// fail once a server limit is reached, so the line is rate-limited: the first and every 1000th at
/// Warning, the others at Debug. It carries the exception's type name only, no wire value.
/// </summary>
internal sealed class WebSocketRefusalLog
{
    private long _refused;

    /// <summary>Count one refused handshake and write its line.</summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="exceptionType">The type name of what the handshake threw.</param>
    /// <param name="answered"><c>true</c> when the 503 was set; <c>false</c> when the response had already started.</param>
    internal void Refused(ILogger? logger, string exceptionType, bool answered)
    {
        var level = RateLimitedLog.Level(ref _refused, LogLevel.Warning, out var count);
        if (answered)
        {
            SafeLog.Write(logger, level,
                "MapDidCommWebSocket: a WebSocket handshake failed ({ExceptionType}) and was refused with 503 (Service Unavailable); a server limit such as Kestrel's MaxConcurrentUpgradedConnections may have been reached; {Count} so far on this endpoint.",
                exceptionType, count);
        }
        else
        {
            SafeLog.Write(logger, level,
                "MapDidCommWebSocket: a WebSocket handshake failed ({ExceptionType}) after its response had started, so no status could be sent; {Count} so far on this endpoint.",
                exceptionType, count);
        }
    }
}
