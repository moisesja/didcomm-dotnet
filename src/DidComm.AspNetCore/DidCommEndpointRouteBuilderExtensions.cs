using System.Buffers;
using System.Net.Mime;
using System.Net.WebSockets;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.OutOfBand;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// L-014: alias the static OutOfBand API class so the same-named namespace doesn't shadow it.
using OutOfBandApi = DidComm.Protocols.OutOfBand.OutOfBand;

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
    /// (FR-TRN-07). Returns <c>202 Accepted</c> on success, <c>415</c> for an unknown content type,
    /// and <c>413</c> when the body exceeds <see cref="DidCommOptions.MaxReceiveBytes"/> (FR-API-06).
    /// ANY envelope/processing failure — crypto, malformed structure, consistency, or DID resolution —
    /// returns a uniform <c>400 Bad Request</c> with an empty body and the reason logged server-side
    /// only, so the response cannot be used as a decryption / recipient-kid / resolution oracle
    /// (FR-API-07, issues #20/#28/#33).
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
            if (string.IsNullOrEmpty(contentType) || !MatchesMediaType(contentType, receiveOptions.AcceptedMediaTypes))
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

            var logger = httpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("DidComm.AspNetCore.Receive");

            UnpackResult unpacked;
            try
            {
                unpacked = await client.UnpackAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // client aborted — let cancellation propagate, do not mask as a rejection
            }
            catch (Exception ex)
            {
                // Every unpack failure (crypto / malformed / consistency / resolution / did:web /
                // missing-secret) collapses to one opaque 400 so the peer can't distinguish them.
                return NormalizedReceiveRejection(logger, ex);
            }

            // The message was received and unpacked; HTTP receive is one-way / fire-and-forget
            // (FR-TRN-10). A fault in the consumer callback is the receiver's own concern — log it but
            // still answer 202, so a throwing callback can't turn a successfully-unpacked message into a
            // distinguishable 500. Otherwise an attacker could probe "did this envelope decrypt for us?"
            // by inducing a callback throw — re-exposing the unpack-success oracle the uniform-400
            // rejection closes (#20 red-team).
            try
            {
                await onReceive(unpacked, httpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "DidComm receive: onReceive callback threw after a successful unpack; acknowledging anyway (one-way receive).");
            }

            return Results.StatusCode(StatusCodes.Status202Accepted);
        });
    }

    /// <summary>
    /// Registry-aware HTTP receive endpoint (FR-PROTO-03). Same shape as the
    /// <c>onReceive</c>-callback overload (validates content-type → 415, caps body → 413,
    /// returns 202 on success) but resolves the inbound message to a registered
    /// <see cref="IProtocolHandler"/> via <see cref="ProtocolDispatcher"/>. Any reply the
    /// handler produces is LOGGED (HTTP receive is one-way per FR-TRN-10) — operators that
    /// want to deliver the reply schedule an outbound send out of band, typically using
    /// <see cref="DidCommClient.SendAsync"/> from inside the handler itself.
    /// </summary>
    /// <param name="endpoints">The ASP.NET Core endpoint route builder.</param>
    /// <param name="pattern">URL pattern (e.g. <c>"/didcomm"</c>).</param>
    public static IEndpointConventionBuilder MapDidCommEndpoint(
        this IEndpointRouteBuilder endpoints,
        string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        return endpoints.MapPost(pattern, async (HttpContext httpContext) =>
        {
            var sp = httpContext.RequestServices;
            var client = sp.GetRequiredService<DidCommClient>();
            var coreOptions = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;
            var receiveOptions = sp.GetService<IOptions<DidCommReceiveOptions>>()?.Value ?? new DidCommReceiveOptions();
            var dispatcher = sp.GetRequiredService<ProtocolDispatcher>();
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("DidComm.AspNetCore.Dispatch");

            var contentType = httpContext.Request.ContentType;
            if (string.IsNullOrEmpty(contentType) || !MatchesMediaType(contentType, receiveOptions.AcceptedMediaTypes))
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

            var maxBytes = coreOptions.MaxReceiveBytes;
            if (httpContext.Request.ContentLength is long declared && declared > maxBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            string body;
            try
            {
                body = await ReadCappedAsync(httpContext.Request.Body, maxBytes, httpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (PayloadTooLargeException)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            // Unpack AND dispatch share one opaque exit: a handler bug (FR-THR-04 InvalidOperationException)
            // and an unpack rejection (crypto/malformed/consistency/resolution) all return the same 400
            // with no body, so the status code itself is not an oracle (no 400-vs-500 partition).
            try
            {
                var unpacked = await client.UnpackAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
                var outcome = await dispatcher.DispatchAsync(unpacked, client, coreOptions, httpContext.RequestAborted).ConfigureAwait(false);
                LogOutcome(logger, outcome, sameSocketDelivered: false);
                return Results.StatusCode(StatusCodes.Status202Accepted);
            }
            catch (OperationCanceledException)
            {
                throw; // client aborted — let cancellation propagate
            }
            catch (Exception ex)
            {
                return NormalizedReceiveRejection(logger, ex);
            }
        });
    }

    /// <summary>
    /// Map an HTTP GET endpoint that serves the short-form of an out-of-band invitation
    /// (FR-OOB-04). A recipient dereferences a <c>?_oobid=&lt;id&gt;</c> URL; this endpoint reads
    /// the id, looks it up in <paramref name="store"/>, and returns the full plaintext invitation
    /// (<c>200</c>, <c>application/didcomm-plain+json</c>). Returns <c>400</c> when the
    /// <c>_oobid</c> query value is absent and <c>404</c> when the id is unknown. The sender hosts
    /// this itself rather than using a public URL shortener, which the spec forbids for privacy.
    /// </summary>
    /// <param name="endpoints">The ASP.NET Core endpoint route builder.</param>
    /// <param name="pattern">URL pattern (e.g. <c>"/oob"</c>) — must match the path used in <c>OutOfBand.ToShortUrl</c>.</param>
    /// <param name="store">The invitation store the sender populated when it minted the short URL.</param>
    public static IEndpointConventionBuilder MapDidCommOobEndpoint(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        IOobInvitationStore store)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentNullException.ThrowIfNull(store);

        return endpoints.MapGet(pattern, (HttpContext httpContext) =>
        {
            // The short URL carries the id in the _oobid query parameter (OutOfBand.OobIdParameter).
            var oobId = httpContext.Request.Query[OutOfBandApi.OobIdParameter].ToString();
            if (string.IsNullOrEmpty(oobId))
                return Results.StatusCode(StatusCodes.Status400BadRequest);

            var invitation = store.Retrieve(oobId);
            if (invitation is null)
                return Results.StatusCode(StatusCodes.Status404NotFound);

            return Results.Text(invitation, MediaTypes.Plaintext);
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

    /// <summary>
    /// Registry-aware WebSocket overload (FR-PROTO-03). Same wire behavior as the inline-
    /// callback overload (FR-TRN-09/10/API-06 enforced); each reassembled envelope is unpacked
    /// and dispatched through <see cref="ProtocolDispatcher"/>. When the inbound endpoint's
    /// <see cref="DidCommReceiveOptions.AllowSameSocketReplies"/> is <c>true</c>, any handler
    /// reply is packed (authcrypt back to the original sender) and written on the same socket
    /// as a single binary message — the chat-style convenience. Defaults to <c>false</c> per
    /// FR-TRN-10's one-way reading.
    /// </summary>
    /// <param name="endpoints">The ASP.NET Core endpoint route builder.</param>
    /// <param name="pattern">URL pattern (e.g. <c>"/ws/didcomm"</c>).</param>
    public static IEndpointConventionBuilder MapDidCommWebSocket(
        this IEndpointRouteBuilder endpoints,
        string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        return endpoints.MapGet(pattern, async (HttpContext httpContext) =>
        {
            if (!httpContext.WebSockets.IsWebSocketRequest)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await httpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var sp = httpContext.RequestServices;
            var client = sp.GetRequiredService<DidCommClient>();
            var coreOptions = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;
            var receiveOptions = sp.GetService<IOptions<DidCommReceiveOptions>>()?.Value ?? new DidCommReceiveOptions();
            var dispatcher = sp.GetRequiredService<ProtocolDispatcher>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("DidComm.AspNetCore.WebSocket.Dispatch");

            await ReceiveLoopWithDispatchAsync(socket, client, coreOptions, receiveOptions, dispatcher, logger, httpContext.RequestAborted)
                .ConfigureAwait(false);
        });
    }

    private static async Task ReceiveLoopWithDispatchAsync(
        System.Net.WebSockets.WebSocket socket,
        DidCommClient client,
        DidCommOptions coreOptions,
        DidCommReceiveOptions receiveOptions,
        ProtocolDispatcher dispatcher,
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
                        await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "payload too large", CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var packed = System.Text.Encoding.UTF8.GetString(ms.ToArray());

                UnpackResult unpacked;
                try
                {
                    unpacked = await client.UnpackAsync(packed, ct).ConfigureAwait(false);
                }
                catch (MalformedMessageException ex)
                {
                    logger?.LogWarning(ex, "MapDidCommWebSocket: discarding malformed envelope");
                    continue;
                }
                catch (CryptoException ex)
                {
                    logger?.LogWarning(ex, "MapDidCommWebSocket: discarding undecryptable envelope");
                    continue;
                }

                DispatchOutcome outcome;
                try
                {
                    outcome = await dispatcher.DispatchAsync(unpacked, client, coreOptions, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    // FR-THR-04 rule 2 violation in a handler (a bug). Don't tear down the
                    // socket — log and keep serving the connection so a buggy protocol doesn't
                    // poison every other protocol sharing the connection.
                    logger?.LogWarning(ex, "MapDidCommWebSocket: handler bug (FR-THR-04 rule 2); dropping inbound and continuing.");
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Same posture as above for any other handler-thrown exception: log + keep
                    // the connection alive. Cancellation propagates so the loop can exit cleanly.
                    logger?.LogError(ex, "MapDidCommWebSocket: handler threw an unhandled exception; dropping inbound and continuing.");
                    continue;
                }

                var delivered = false;
                if (outcome is { Result: DispatchResult.ReplyProduced, Reply: not null } && receiveOptions.AllowSameSocketReplies)
                {
                    delivered = await TrySendReplyOnSocketAsync(socket, client, unpacked, outcome.Reply, logger, ct).ConfigureAwait(false);
                }
                LogOutcome(logger, outcome, sameSocketDelivered: delivered);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<bool> TrySendReplyOnSocketAsync(
        System.Net.WebSockets.WebSocket socket,
        DidCommClient client,
        UnpackResult inbound,
        DidComm.Messages.Message reply,
        ILogger? logger,
        CancellationToken ct)
    {
        // The handler owns identity selection (from/to); we only enforce what this socket
        // dictates — see TryRouteSameSocketReply for the rules.
        if (!TryRouteSameSocketReply(inbound, reply, out var from, out var peerDid, out var reason))
        {
            logger?.LogWarning("MapDidCommWebSocket: dropping same-socket reply — {Reason}", reason);
            return false;
        }

        // Defense-in-depth advisory: reply.From should normally be one of the identities the
        // inbound was addressed to. Log if not, but proceed — legitimate multi-DID setups may
        // legitimately rebind on reply, and authcrypt will still bind the chosen sender key.
        if (inbound.Message.To is { Count: > 0 } && !inbound.Message.To.Contains(from!, StringComparer.Ordinal))
        {
            logger?.LogWarning(
                "MapDidCommWebSocket: handler reply.from '{From}' is not among inbound.to ({InboundTo}); delivering anyway, but this may indicate a misconfigured handler.",
                from, string.Join(",", inbound.Message.To));
        }

        try
        {
            var packed = await client.PackEncryptedAsync(reply, new PackEncryptedOptions(
                Recipients: new[] { peerDid! }, From: from!), ct).ConfigureAwait(false);
            var bytes = System.Text.Encoding.UTF8.GetBytes(packed.Message);
            await socket.SendAsync(bytes, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "MapDidCommWebSocket: failed to deliver dispatcher reply on same socket.");
            return false;
        }
    }

    /// <summary>
    /// Decide whether a handler's reply can be delivered back on the SAME WebSocket as the
    /// inbound envelope. The handler owns <c>from</c>/<c>to</c> selection — the only thing this
    /// gate enforces is what the socket dictates: any envelope written here is consumed by the
    /// inbound peer, so the reply MUST be addressed to that peer (otherwise we'd be writing
    /// ciphertext the peer cannot decrypt onto its socket). Handlers fanning out to other
    /// recipients must use <c>ProtocolContext.Client.SendAsync</c> (out-of-band per FR-TRN-10).
    /// </summary>
    /// <param name="inbound">The unpack result for the inbound envelope.</param>
    /// <param name="reply">The handler-produced reply message.</param>
    /// <param name="from">The sender DID to pack authcrypt as (<c>reply.From</c>) on success.</param>
    /// <param name="peerDid">The single recipient DID to pack for (the inbound peer) on success.</param>
    /// <param name="reason">Human-readable failure reason on rejection; <c>null</c> on success.</param>
    /// <returns><c>true</c> when the reply is safe to deliver on this socket.</returns>
    internal static bool TryRouteSameSocketReply(
        UnpackResult inbound,
        DidComm.Messages.Message reply,
        out string? from,
        out string? peerDid,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(reply);
        from = null;
        peerDid = null;
        reason = null;

        if (string.IsNullOrEmpty(reply.From))
        {
            reason = "handler reply has no 'from'; cannot pack authcrypt for same-socket delivery.";
            return false;
        }
        if (string.IsNullOrEmpty(inbound.Message.From))
        {
            reason = "inbound envelope has no 'from'; same-socket reply has no addressable peer.";
            return false;
        }
        if (reply.To is null || !reply.To.Contains(inbound.Message.From, StringComparer.Ordinal))
        {
            reason = $"handler reply.to does not include the inbound peer '{inbound.Message.From}'; use ProtocolContext.Client.SendAsync for out-of-band recipients.";
            return false;
        }

        from = reply.From;
        peerDid = inbound.Message.From;
        return true;
    }

    private static void LogOutcome(ILogger? logger, DispatchOutcome outcome, bool sameSocketDelivered)
    {
        if (logger is null) return;
        switch (outcome.Result)
        {
            case DispatchResult.ReplyProduced:
                logger.LogInformation(
                    "Protocol dispatch produced a reply (handler={Handler}, delivered-on-same-socket={Delivered}).",
                    outcome.Handler?.ProtocolUri, sameSocketDelivered);
                break;
            case DispatchResult.NoReply:
                logger.LogDebug("Protocol dispatch ran handler '{Handler}' which produced no reply.", outcome.Handler?.ProtocolUri);
                break;
            case DispatchResult.NoHandler:
                logger.LogDebug("Protocol dispatch found no handler for inbound message.");
                break;
            case DispatchResult.DroppedAsAckLoop:
                logger.LogWarning("Protocol dispatch dropped inbound pure-ACK that also requested an ACK (FR-THR-04 rule 3).");
                break;
        }
    }

    /// <summary>
    /// FR-API-07 / FR-TRN-10: collapse every input- or handler-triggered failure on the HTTP receive
    /// path to one opaque rejection — a bare <c>400</c> with no body — so a remote peer cannot use the
    /// status code or response text as a decryption / recipient-kid / DID-resolution oracle (issues
    /// #20/#28/#33). The detailed reason is logged server-side only. The WebSocket paths already take
    /// this posture (log-and-discard, echo nothing); this brings the HTTP paths to parity.
    /// </summary>
    private static IResult NormalizedReceiveRejection(ILogger? logger, Exception ex)
    {
        if (ex is DidCommException)
        {
            // Expected, peer-triggered category (malformed / crypto / consistency / resolution /
            // unsupported-method / secret-missing) — it's the peer's envelope, not our bug.
            logger?.LogWarning(ex, "DidComm receive: rejecting inbound envelope ({Reason}).", ex.GetType().Name);
        }
        else
        {
            // A handler bug (FR-THR-04 InvalidOperationException) or any other unexpected throw.
            logger?.LogError(ex, "DidComm receive: inbound processing failed ({Reason}).", ex.GetType().Name);
        }

        return Results.StatusCode(StatusCodes.Status400BadRequest);
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
                        await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "payload too large", CancellationToken.None).ConfigureAwait(false);
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
        // base media type only. A malformed header throws FormatException; treat that as "no
        // match" so the endpoint answers 415 rather than letting a 500 escape.
        string? mediaType;
        try
        {
            mediaType = new ContentType(contentType).MediaType;
        }
        catch (FormatException)
        {
            return false;
        }

        foreach (var allowed in accepted)
        {
            if (string.Equals(mediaType, allowed, StringComparison.OrdinalIgnoreCase))
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
