using System.Net.WebSockets;
using DidComm.Exceptions;
using DidComm.Transports.Stomp;
using Microsoft.Extensions.Logging;

namespace DidComm.AspNetCore;

/// <summary>
/// Per-connection state machine for the opt-in STOMP 1.2 receive mode of
/// <c>MapDidCommWebSocket</c> (FR-TRN-12, <see cref="DidCommReceiveOptions.UseStomp"/>).
/// Handles the minimal subset only: <c>CONNECT</c>/<c>STOMP</c> → <c>CONNECTED</c>, one packed
/// DIDComm message per <c>SEND</c> frame body, <c>DISCONNECT</c> → normal close. Anything else
/// — malformed frames, out-of-order commands, broker verbs (SUBSCRIBE/ACK/…) — closes the
/// connection with 1002 (protocol error): STOMP framing faults belong to the connection,
/// unlike a bad DIDComm envelope, which stays a per-message drop.
/// </summary>
internal sealed class StompServerSession
{
    private bool _connected;

    /// <summary>
    /// Process one reassembled WebSocket message as a STOMP frame. Returns the packed DIDComm
    /// envelope carried by an acceptable SEND, or <c>null</c> with <c>CloseConnection</c>
    /// telling the receive loop whether to end (the socket is already closed by then).
    /// </summary>
    public async Task<(string? Envelope, bool CloseConnection)> ProcessAsync(
        WebSocket socket,
        byte[] rawMessage,
        IReadOnlyList<string> acceptedMediaTypes,
        ILogger? logger,
        CancellationToken ct)
    {
        StompFrame frame;
        try
        {
            frame = StompFrameCodec.Decode(rawMessage);
        }
        catch (MalformedMessageException ex)
        {
            // Type name only: the parser's message can quote the peer's frame.
            SafeLog.Write(logger, LogLevel.Warning, "MapDidCommWebSocket(STOMP): malformed STOMP frame ({ExceptionType}); closing with 1002.", ex.GetType().Name);
            await CloseAsync(socket, WebSocketCloseStatus.ProtocolError, "malformed STOMP frame").ConfigureAwait(false);
            return (null, true);
        }

        switch (frame.Command)
        {
            // STOMP 1.2 clients may open with either verb; both take the same handshake.
            case "CONNECT" or "STOMP":
                return (null, await HandleConnectAsync(socket, frame, logger, ct).ConfigureAwait(false));

            case "SEND" when !_connected:
                logger?.LogWarning("MapDidCommWebSocket(STOMP): SEND before CONNECT; closing with 1002.");
                await CloseAsync(socket, WebSocketCloseStatus.ProtocolError, "SEND before CONNECT").ConfigureAwait(false);
                return (null, true);

            case "SEND":
                return (HandleSend(frame, acceptedMediaTypes, logger), false);

            case "DISCONNECT":
                // Receipts are outside the minimal subset, so no RECEIPT frame is emitted even
                // when requested — the normal close is the acknowledgment.
                await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, "STOMP DISCONNECT").ConfigureAwait(false);
                return (null, true);

            default:
                // Broker verbs (SUBSCRIBE, UNSUBSCRIBE, ACK, NACK, BEGIN, …) are deliberately
                // unsupported — DIDComm's STOMP profile is send-only framing, not a broker. The
                // command is the peer's text (it can hold terminal escapes), so it is not written.
                logger?.LogWarning("MapDidCommWebSocket(STOMP): unsupported STOMP command; closing with 1002.");
                await CloseAsync(socket, WebSocketCloseStatus.ProtocolError, "unsupported STOMP command").ConfigureAwait(false);
                return (null, true);
        }
    }

    /// <summary>Validate CONNECT and answer CONNECTED; returns <c>true</c> when the connection must close.</summary>
    private async Task<bool> HandleConnectAsync(WebSocket socket, StompFrame frame, ILogger? logger, CancellationToken ct)
    {
        if (_connected)
        {
            logger?.LogWarning("MapDidCommWebSocket(STOMP): repeated CONNECT; closing with 1002.");
            await CloseAsync(socket, WebSocketCloseStatus.ProtocolError, "repeated CONNECT").ConfigureAwait(false);
            return true;
        }
        // STOMP 1.2 CONNECT MUST carry accept-version (listing 1.2 for this server) and host.
        if (!frame.TryGetHeader("accept-version", out var versions)
            || !AcceptsVersion12(versions)
            || !frame.TryGetHeader("host", out _))
        {
            logger?.LogWarning("MapDidCommWebSocket(STOMP): CONNECT without accept-version 1.2 + host; closing with 1002.");
            await CloseAsync(socket, WebSocketCloseStatus.ProtocolError, "unacceptable CONNECT").ConfigureAwait(false);
            return true;
        }

        _connected = true;
        var connected = new StompFrame(
            "CONNECTED",
            new[]
            {
                KeyValuePair.Create("version", "1.2"),
                // No heart-beating in the minimal subset — advertise 0,0 explicitly.
                KeyValuePair.Create("heart-beat", "0,0"),
            },
            ReadOnlyMemory<byte>.Empty);
        await socket.SendAsync(StompFrameCodec.Encode(connected), WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>Extract the packed envelope from a SEND, or <c>null</c> (logged) when the frame is not acceptable as a DIDComm message.</summary>
    private static string? HandleSend(StompFrame frame, IReadOnlyList<string> acceptedMediaTypes, ILogger? logger)
    {
        if (!frame.TryGetHeader("destination", out _))
        {
            // destination is REQUIRED on SEND. A per-message shape fault — drop the message,
            // keep the session, mirroring how a bad envelope is a per-message drop.
            logger?.LogWarning("MapDidCommWebSocket(STOMP): SEND without destination; discarding message.");
            return null;
        }
        // FR-TRN-12 / FR-TRN-02: the SEND content-type must be an accepted DIDComm media type
        // (application/didcomm-encrypted+json et al.) — the WebSocket analog of the HTTP 415 gate.
        // Read once, as message_structure.md asks of a recipient (the short form included).
        if (!frame.TryGetHeader("content-type", out var contentType)
            || DidCommEndpointRouteBuilderExtensions.BaseMediaType(contentType) is not { } declaredType
            || !DidCommEndpointRouteBuilderExtensions.IsAcceptedMediaType(declaredType, acceptedMediaTypes))
        {
            logger?.LogWarning("MapDidCommWebSocket(STOMP): SEND with missing/unaccepted content-type; discarding message.");
            return null;
        }
        if (frame.Body.IsEmpty)
        {
            logger?.LogWarning("MapDidCommWebSocket(STOMP): SEND with an empty body; discarding message.");
            return null;
        }
        // The DIDComm rule: one packed message per STOMP SEND frame body, whose content-type is the
        // media type of the envelope it is (transports.md): refused as an unaccepted content-type.
        var packed = System.Text.Encoding.UTF8.GetString(frame.Body.Span);
        if (!DidCommEndpointRouteBuilderExtensions.EnvelopeIsOfType(declaredType, packed))
        {
            logger?.LogWarning("MapDidCommWebSocket(STOMP): SEND with missing/unaccepted content-type; discarding message.");
            return null;
        }
        return packed;
    }

    /// <summary>True when the comma-separated accept-version list contains 1.2.</summary>
    private static bool AcceptsVersion12(string versions)
    {
        foreach (var range in versions.Split(','))
        {
            if (string.Equals(range.Trim(), "1.2", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // CloseAsync waits for the peer's close frame. Bound the handshake by the close grace: a peer
    // that never answers is aborted instead of holding the connection. The receive loop calls this
    // between messages, so no receive of its own is outstanding next to CloseAsync's.
    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        using var grace = new CancellationTokenSource(WebSocketReturnRouteSession.DefaultCloseGrace);
        try
        {
            await socket.CloseAsync(status, description, grace.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The peer is gone or did not answer in time; the loop is ending either way.
            socket.Abort();
        }
    }
}
