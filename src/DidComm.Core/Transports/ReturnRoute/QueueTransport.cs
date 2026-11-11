namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// The <c>didcomm:transport/queue</c> service endpoint (PRD §8a, FR-RR-06): a recipient that
/// publishes it has no inbound transport and expects messages to be held for pickup
/// (return-route / message-pickup). <c>DidCommClient.SendAsync</c> and <see cref="TransportRouter"/>
/// refuse to send to it with a <see cref="Exceptions.TransportException"/> whose message is
/// <see cref="RefusalMessage"/>; the message is packed first because the endpoint lives in the
/// recipient's resolved service entry.
/// </summary>
public static class QueueTransport
{
    /// <summary>The queue transport URI exactly as it appears in a DID document: <c>didcomm:transport/queue</c>.</summary>
    public const string Uri = "didcomm:transport/queue";

    /// <summary>
    /// The <see cref="Exceptions.TransportException"/> text for a send refused because the recipient's
    /// endpoint is <see cref="Uri"/>. Names no DID.
    /// </summary>
    internal const string RefusalMessage =
        "The recipient's DIDCommMessaging service endpoint is 'didcomm:transport/queue': no transport exists for it; the sender holds the message for pickup by the recipient (return-route / message-pickup, FR-RR-06).";

    /// <summary>Whether <paramref name="uri"/> is exactly <see cref="Uri"/> (ordinal).</summary>
    /// <param name="uri">A service endpoint URI string, or <c>null</c>.</param>
    public static bool IsQueueTransport(string? uri)
        => string.Equals(uri, Uri, StringComparison.Ordinal);

    /// <summary>Whether <paramref name="uri"/> is an absolute URI whose original text is exactly <see cref="Uri"/>.</summary>
    /// <param name="uri">A service endpoint URI, or <c>null</c>.</param>
    public static bool IsQueueTransport(System.Uri? uri)
        => uri is { IsAbsoluteUri: true } && IsQueueTransport(uri.OriginalString);
}
