using DidComm.Exceptions;
using DidComm.Facade;
using NetCoreIDidResolver = NetDid.Core.IDidResolver;

namespace DidComm.Resolution;

/// <summary>
/// <see cref="IServiceEndpointResolver"/> backed by a <see cref="NetCoreIDidResolver"/>
/// (PRD §8, FR-ROUTE-03). Resolves the supplied DID via the registered net-did resolver
/// chain, filters the document's <c>service</c> array for <c>DIDCommMessaging</c> entries,
/// and projects each into a <see cref="DidCommServiceInfo"/> preserving the spec's preference
/// order.
/// </summary>
/// <remarks>
/// <para>
/// The adapter delegates DID-method support and resolution caching to net-did (FR-DID-01,
/// FR-DID-04 — no double-caching). The DD-10 bare-string tolerance is consulted from
/// <see cref="DidCommOptions.AllowBareStringServiceEndpoint"/>, so a single options instance
/// controls both the strict parsing default and any opt-in compatibility.
/// </para>
/// <para>
/// did:web is rejected at the perimeter for symmetry with
/// <see cref="NetDidKeyService.RejectUnsupportedMethod"/> (FR-DID-06 / DD-08); refusing here
/// means a routing lookup against a did:web subject never silently succeeds while the
/// matching key lookup would have thrown.
/// </para>
/// </remarks>
public sealed class NetDidServiceEndpointResolver : IServiceEndpointResolver
{
    private readonly NetCoreIDidResolver _resolver;
    private readonly IDidKeyService _keyService;
    private readonly DidCommOptions _options;

    /// <summary>Initialise the adapter.</summary>
    /// <param name="resolver">The composite (or single-method) net-did resolver.</param>
    /// <param name="keyService">Reused only for <see cref="IDidKeyService.RejectUnsupportedMethod"/> — keeps the unsupported-method policy in one place.</param>
    /// <param name="options">Process-wide options; supplies <see cref="DidCommOptions.AllowBareStringServiceEndpoint"/>.</param>
    public NetDidServiceEndpointResolver(
        NetCoreIDidResolver resolver,
        IDidKeyService keyService,
        DidCommOptions options)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(keyService);
        ArgumentNullException.ThrowIfNull(options);
        _resolver = resolver;
        _keyService = keyService;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DidCommServiceInfo>> ResolveAsync(
        string did,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(did);
        _keyService.RejectUnsupportedMethod(did);

        var result = await _resolver.ResolveAsync(did, ct: ct).ConfigureAwait(false);
        if (result.DidDocument is null)
        {
            var error = result.ResolutionMetadata?.Error ?? "resolver returned no document";
            throw new DidResolutionException(did, error);
        }

        return ServiceEndpointParser.Parse(
            result.DidDocument.Service,
            _options.AllowBareStringServiceEndpoint);
    }
}
