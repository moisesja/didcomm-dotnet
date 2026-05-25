using DidComm.Exceptions;

namespace DidComm.Resolution;

/// <summary>
/// DID-Document → DIDComm-service projection used by the routing layer (PRD §8, FR-ROUTE-03/04).
/// Separated from <see cref="IDidKeyService"/> so the key-graph and the service-graph stay
/// independently substitutable — a host that brings its own service discovery can implement
/// this without touching key resolution.
/// </summary>
/// <remarks>
/// <para>
/// Implementations resolve the supplied DID (typically via the same underlying
/// <c>NetDid.Core.IDidResolver</c> that <see cref="NetDidKeyService"/> uses), filter the
/// document's <c>service</c> array for entries of type <c>DIDCommMessaging</c>, and project
/// each into one or more <see cref="DidCommServiceInfo"/> records. Per spec the
/// <c>serviceEndpoint</c> MAY be a single object or an array of objects — the returned list
/// flattens that into a preference-ordered enumeration so the sender side can iterate
/// candidates for FR-ROUTE-08 failover.
/// </para>
/// <para>
/// The contract permits an empty result when the document has no <c>DIDCommMessaging</c>
/// service — that is a "no routing service on file" signal, not an error. Callers that need a
/// service raise the operation-specific failure themselves.
/// </para>
/// </remarks>
public interface IServiceEndpointResolver
{
    /// <summary>
    /// Resolve <paramref name="did"/> and return every <c>DIDCommMessaging</c> service entry
    /// in preference order. The list MAY be empty.
    /// </summary>
    /// <param name="did">A DID (no fragment) — the subject whose service block to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="UnsupportedDidMethodException">When <paramref name="did"/> uses an intentionally-unsupported method (e.g. <c>did:web</c>).</exception>
    /// <exception cref="DidResolutionException">When the underlying resolver fails or returns no document.</exception>
    Task<IReadOnlyList<DidCommServiceInfo>> ResolveAsync(string did, CancellationToken ct = default);
}
