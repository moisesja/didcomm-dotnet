namespace DidComm.Transports.Http;

/// <summary>
/// With <see cref="HttpTransportOptions.TrustedProxy"/> set, the connect callback sees only the
/// proxy's socket, so the checks that need no socket run here instead: on every request (each
/// attempt and each followed 307), before it is handed to the proxy. The refusal is the guard's
/// <see cref="Exceptions.TransportException"/>, which the resilience pipeline neither retries nor
/// counts. Built only when a proxy is set, in the same handler build as the proxy and the policy.
/// </summary>
internal sealed class ProxiedDestinationCheck : DelegatingHandler
{
    private readonly OutboundEndpointGuard _guard;

    /// <summary>Check every request against <paramref name="guard"/>'s policy.</summary>
    /// <param name="guard">The guard built from the transport's resolved policy.</param>
    public ProxiedDestinationCheck(OutboundEndpointGuard guard) => _guard = guard;

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _guard.Validate(request.RequestUri!);
        return base.SendAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _guard.Validate(request.RequestUri!);
        return base.Send(request, cancellationToken);
    }
}
