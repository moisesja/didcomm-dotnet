using DidComm.Exceptions;

namespace DidComm.Transports;

/// <summary>
/// Default <see cref="ITransportRouter"/>. Iterates the registered <see cref="IDidCommTransport"/>
/// set in registration order, picks the first one whose <see cref="IDidCommTransport.CanHandle"/>
/// accepts the endpoint, and delegates to its <see cref="IDidCommTransport.SendAsync"/>.
/// Throws <see cref="TransportException"/> with the offending scheme when no transport matches.
/// </summary>
public sealed class TransportRouter : ITransportRouter
{
    private readonly IReadOnlyList<IDidCommTransport> _transports;

    /// <summary>Initialize with the set of registered transports (DI passes the enumerable).</summary>
    /// <param name="transports">All registered <see cref="IDidCommTransport"/> implementations.</param>
    public TransportRouter(IEnumerable<IDidCommTransport> transports)
    {
        ArgumentNullException.ThrowIfNull(transports);
        _transports = transports.ToArray();
    }

    /// <inheritdoc />
    public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Endpoint);

        foreach (var transport in _transports)
        {
            if (transport.CanHandle(request.Endpoint))
                return transport.SendAsync(request, ct);
        }

        throw new TransportException(
            $"No registered IDidCommTransport handles scheme '{request.Endpoint.Scheme}' (FR-TRN-01). " +
            $"Register one via builder.UseHttpTransport()/UseWebSocketTransport()/UseTransport<T>().",
            httpStatusCode: null,
            scheme: request.Endpoint.Scheme);
    }
}
