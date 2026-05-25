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
        "application/didcomm-encrypted+json",
        "application/didcomm-signed+json",
        "application/didcomm-plain+json",
    };
}
