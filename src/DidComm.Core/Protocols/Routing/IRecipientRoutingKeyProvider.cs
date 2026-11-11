using DidComm.Jose;

namespace DidComm.Protocols.Routing;

/// <summary>
/// Supplies the extra routing keys a recipient configured out of band with this mediator
/// (FR-ROUTE-05 second paragraph), looked up per recipient. <see cref="ForwardProcessor"/> asks
/// it once per forward, in <see cref="RewrapMode.PassThrough"/> only, and wraps the onward
/// payload once per returned key (outer to inner) before relaying.
/// </summary>
/// <remarks>
/// <para>
/// The argument is the DID subject of the forward's <c>body.next</c> (fragment, path and query
/// stripped per PRD §4.3 <c>DidSubjectOf</c>), so a provider keyed by bare DID matches a
/// <c>next</c> that names a routing key URL. A <c>next</c> that is not a DID or DID URL is never
/// looked up: the payload passes through verbatim.
/// </para>
/// <para>
/// Rewrap mode (<see cref="RewrapMode.ReanoncryptToNext"/>, FR-ROUTE-06) never consults the
/// provider — the two transformations are mutually exclusive, cf.
/// <see cref="ForwardProcessorOptions.Validate"/>. Returning an empty list means "relay the
/// attachment verbatim". Implementations are singletons and MUST be thread-safe.
/// </para>
/// </remarks>
public interface IRecipientRoutingKeyProvider
{
    /// <summary>
    /// The routing keys to wrap the onward payload for before relaying it to
    /// <paramref name="recipientDid"/>, outer to inner. Empty for none.
    /// </summary>
    /// <param name="recipientDid">The DID subject of the forward's <c>body.next</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyList<Jwk>> GetAsync(string recipientDid, CancellationToken ct);
}
