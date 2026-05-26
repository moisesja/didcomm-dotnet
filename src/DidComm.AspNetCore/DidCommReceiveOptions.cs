namespace DidComm.AspNetCore;

/// <summary>
/// Per-endpoint configuration for the <c>MapDidCommEndpoint</c> / <c>MapDidCommWebSocket</c>
/// extensions on <see cref="DidCommEndpointRouteBuilderExtensions"/>. Defaults match the
/// DIDComm v2.1 spec.
/// </summary>
public sealed class DidCommReceiveOptions
{
    /// <summary>
    /// Media types accepted by the endpoint. The HTTP receive path returns 415 when the request
    /// <c>Content-Type</c> does not match any of these. Defaults cover the three DIDComm v2.1
    /// envelope flavors.
    /// </summary>
    public IReadOnlyList<string> AcceptedMediaTypes { get; set; } = new[]
    {
        DidCommMediaTypes.Encrypted,
        DidCommMediaTypes.Signed,
        DidCommMediaTypes.Plain,
    };

    /// <summary>
    /// When the registry-aware <c>MapDidCommWebSocket</c> overload's dispatcher produces a
    /// reply (e.g. a Trust Ping response), should it be sent back over the SAME WebSocket the
    /// inbound message arrived on? Defaults to <c>false</c>: DIDComm is one-way per FR-TRN-10
    /// and replies travel out of band by default. Operators that want the in-band convenience
    /// (e.g. chat samples) set this to <c>true</c> per endpoint.
    /// </summary>
    public bool AllowSameSocketReplies { get; set; }
}
