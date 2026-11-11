using DidComm.Facade;
using DidComm.Transports.ReturnRoute;

namespace DidComm.Transports;

/// <summary>
/// Inputs to <see cref="Facade.DidCommClient.SendAsync"/>. Mirrors
/// <see cref="PackEncryptedOptions"/> (the pack stage runs first, with
/// <c>Forward = true</c> so the facade resolves a transport URI). For senders that already know
/// the recipient's endpoint and want to skip DID resolution / forward wrapping, set
/// <see cref="ServiceEndpointOverride"/>.
/// </summary>
/// <param name="Recipients">At least one entry, as for <see cref="PackEncryptedOptions.Recipients"/>: a DID addresses all of its <c>keyAgreement</c> keys on the negotiated curve, and a DID URL <c>did#fragment</c> pins that one key. Unless <see cref="ServiceEndpointOverride"/> is set, every entry must name the same DID.</param>
/// <param name="From">Sender DID for authcrypt (1PU). <c>null</c> selects anoncrypt (anonymous sender).</param>
/// <param name="SignFrom">Signer DID for sign-then-encrypt composition. <c>null</c> packs without an inner JWS.</param>
/// <param name="Enc">Content-encryption algorithm; defaults to A256CBC-HS512.</param>
/// <param name="ProtectSender">When <c>true</c> with authcrypt, wraps the authcrypt envelope in an outer anoncrypt.</param>
/// <param name="ServiceEndpointOverride">Optional explicit endpoint URI. When set, the facade skips forward wrapping and sends the inner envelope directly. When <c>null</c>, the facade packs with <c>Forward = true</c> and uses <c>PackEncryptedResult.ServiceEndpoint</c>.</param>
/// <example>
/// Used by <c>samples/02-Cookbook</c> (section P — send over a transport).
/// </example>
public sealed record SendOptions(
    IReadOnlyList<string> Recipients,
    string? From = null,
    string? SignFrom = null,
    ContentEncryptionAlgorithm Enc = ContentEncryptionAlgorithm.A256CbcHs512,
    bool ProtectSender = false,
    Uri? ServiceEndpointOverride = null)
{
    /// <summary>
    /// The <c>return_route</c> header extension to send with the message (PRD §8a, FR-RR-07),
    /// asking the recipient to return messages over the same connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>null</c> (the default) adds nothing and leaves a header the message already carries (for
    /// example one set with <see cref="Messages.MessageBuilder.WithReturnRoute"/>) as it is.
    /// <see cref="ReturnRouteMode.None"/> sends an explicit <c>"none"</c>, the downgrade shape.
    /// <see cref="ReturnRouteMode.All"/> and <see cref="ReturnRouteMode.Thread"/> send those values
    /// and replace a header the message already carries. The header is written on a copy that is
    /// sent; the caller's <see cref="Messages.Message"/> is never modified.
    /// </para>
    /// <para>
    /// When the message goes out with <c>all</c> or <c>thread</c> (from this property or from the
    /// message itself), the send is refused for recipients naming more than one DID
    /// (<see cref="ArgumentException"/>, before anything is resolved) and for a recipient reached
    /// through routing keys or a mediator (<see cref="InvalidOperationException"/>, after the
    /// recipient's keys and service entry are read and before the message is packed): the
    /// extension applies only between directly connected agents. Such a message also carries
    /// <c>created_time</c> (FR-RR-09). Over HTTP, an authcrypt send then gets the recipient's reply
    /// in <see cref="SendResult.Returned"/>.
    /// </para>
    /// </remarks>
    public ReturnRouteMode? ReturnRoute { get; init; }

    /// <summary>
    /// The thread id for <see cref="ReturnRouteMode.Thread"/>, sent as <c>return_route_thread</c>.
    /// Required with <see cref="ReturnRouteMode.Thread"/> and invalid with any other value of
    /// <see cref="ReturnRoute"/> (including <c>null</c>). It must be a possible <c>thid</c>:
    /// RFC 3986 unreserved characters only (FR-MSG-11), at most
    /// <see cref="ReturnRouteHeader.MaxThreadLength"/> characters. A value that breaks these rules
    /// fails the send with <see cref="ArgumentException"/> before anything is resolved.
    /// </summary>
    public string? ReturnRouteThread { get; init; }
}
