using System.Buffers;
using System.Net.Mime;
using System.Net.WebSockets;
using DidComm.Exceptions;
using DidComm.Facade;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.AspNetCore;

/// <summary>
/// Minimal-API extensions for receiving DIDComm v2.1 envelopes over ASP.NET Core (PRD §9.2/§9.3,
/// FR-TRN-07/09/10, FR-API-06).
/// </summary>
public static class DidCommEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Map an HTTP POST endpoint that accepts DIDComm envelopes, unpacks them via
    /// <see cref="DidCommClient.UnpackAsync"/>, and dispatches to <paramref name="onReceive"/>
    /// (FR-TRN-07). Returns <c>202 Accepted</c> on success. Returns <c>415</c> for an unknown
    /// content type, <c>413</c> when the body exceeds <see cref="DidCommOptions.MaxReceiveBytes"/>
    /// (FR-API-06), <c>400</c> for malformed envelopes, and <c>500</c> for unexpected errors.
    /// </summary>
    /// <param name="endpoints">The ASP.NET Core endpoint route builder.</param>
    /// <param name="pattern">URL pattern (e.g. <c>"/didcomm"</c>).</param>
    /// <param name="onReceive">Inline handler invoked with the unpacked message.</param>
    public static IEndpointConventionBuilder MapDidCommEndpoint(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<UnpackResult, CancellationToken, Task> onReceive)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentNullException.ThrowIfNull(onReceive);

        return endpoints.MapPost(pattern, async (HttpContext httpContext) =>
        {
            var client = httpContext.RequestServices.GetRequiredService<DidCommClient>();
            var coreOptions = httpContext.RequestServices.GetRequiredService<IOptions<DidCommOptions>>().Value;
            var receiveOptions = httpContext.RequestServices.GetService<IOptions<DidCommReceiveOptions>>()?.Value
                                 ?? new DidCommReceiveOptions();

            // FR-TRN-02: validate the inbound Content-Type matches one of the DIDComm media
            // types we know how to unpack. Mismatched → 415 (the standard HTTP code for an
            // unsupported request body shape).
            var contentType = httpContext.Request.ContentType;
            if (contentType is null || !MatchesMediaType(contentType, receiveOptions.AcceptedMediaTypes))
            {
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            }

            // FR-API-06: cap body at MaxReceiveBytes before we even allocate the buffer; the
            // declared Content-Length is the fast path. The streaming read below enforces the
            // same cap even when the client lies about Content-Length.
            var maxBytes = coreOptions.MaxReceiveBytes;
            if (httpContext.Request.ContentLength is long declared && declared > maxBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            string body;
            try
            {
                body = await ReadCappedAsync(httpContext.Request.Body, maxBytes, httpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (PayloadTooLargeException)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            UnpackResult unpacked;
            try
            {
                unpacked = await client.UnpackAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (MalformedMessageException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (CryptoException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (TransportException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }

            await onReceive(unpacked, httpContext.RequestAborted).ConfigureAwait(false);
            return Results.StatusCode(StatusCodes.Status202Accepted);
        });
    }

    /// <summary>
    /// Map a WebSocket endpoint that accepts DIDComm envelopes per FR-TRN-09 (one logical
    /// WebSocket message per packed envelope; multi-frame messages are reassembled before
    /// processing) and dispatches each to <paramref name="onReceive"/>. One-way per FR-TRN-10:
    /// the server does NOT send protocol replies back on the same socket. Oversize messages
    /// (per <see cref="DidCommOptions.MaxReceiveBytes"/>, FR-API-06) trigger a 1009 close.
    /// </summary>
    /// <param name="endpoints">The ASP.NET Core endpoint route builder.</param>
    /// <param name="pattern">URL pattern (e.g. <c>"/ws/didcomm"</c>).</param>
    /// <param name="onReceive">Inline handler invoked with each unpacked message.</param>
    public static IEndpointConventionBuilder MapDidCommWebSocket(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<UnpackResult, CancellationToken, Task> onReceive)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentNullException.ThrowIfNull(onReceive);

        return endpoints.MapGet(pattern, async (HttpContext httpContext) =>
        {
            if (!httpContext.WebSockets.IsWebSocketRequest)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await httpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var client = httpContext.RequestServices.GetRequiredService<DidCommClient>();
            var coreOptions = httpContext.RequestServices.GetRequiredService<IOptions<DidCommOptions>>().Value;
            var loggerFactory = httpContext.RequestServices.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("DidComm.AspNetCore.WebSocket");

            await ReceiveLoopAsync(socket, client, coreOptions, onReceive, logger, httpContext.RequestAborted).ConfigureAwait(false);
        });
    }

    private static async Task ReceiveLoopAsync(
        System.Net.WebSockets.WebSocket socket,
        DidCommClient client,
        DidCommOptions coreOptions,
        Func<UnpackResult, CancellationToken, Task> onReceive,
        ILogger? logger,
        CancellationToken ct)
    {
        var maxBytes = coreOptions.MaxReceiveBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                long total = 0;
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client closed", CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                    total += result.Count;
                    if (total > maxBytes)
                    {
                        // FR-API-06 + RFC 6455 §7.4.1 close code 1009 "Message Too Big".
                        await socket.CloseAsync((WebSocketCloseStatus)1009, "payload too large", CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                // FR-TRN-09: the reassembled bytes are exactly one packed envelope.
                var packed = System.Text.Encoding.UTF8.GetString(ms.ToArray());

                try
                {
                    var unpacked = await client.UnpackAsync(packed, ct).ConfigureAwait(false);
                    await onReceive(unpacked, ct).ConfigureAwait(false);
                }
                catch (MalformedMessageException ex)
                {
                    logger?.LogWarning(ex, "MapDidCommWebSocket: discarding malformed envelope");
                }
                catch (CryptoException ex)
                {
                    logger?.LogWarning(ex, "MapDidCommWebSocket: discarding undecryptable envelope");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool MatchesMediaType(string contentType, IReadOnlyList<string> accepted)
    {
        // ContentType can include parameters (e.g. "; charset=utf-8") — strip and compare the
        // base media type only.
        var parsed = new ContentType(contentType);
        foreach (var allowed in accepted)
        {
            if (string.Equals(parsed.MediaType, allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Read the request body into a UTF-8 string but throw <see cref="PayloadTooLargeException"/>
    /// the moment the cumulative read exceeds <paramref name="maxBytes"/>. Prevents the
    /// declared <c>Content-Length</c> from being trusted as the only gate.
    /// </summary>
    internal static async Task<string> ReadCappedAsync(Stream body, long maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        long total = 0;
        while (true)
        {
            var read = await body.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
            if (total > maxBytes)
                throw new PayloadTooLargeException(maxBytes);
            ms.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    internal sealed class PayloadTooLargeException : Exception
    {
        public PayloadTooLargeException(long maxBytes) : base($"Payload exceeded {maxBytes} bytes.") { }
    }
}
