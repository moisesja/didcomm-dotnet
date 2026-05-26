namespace DidComm.Protocols.DiscoverFeatures;

/// <summary>
/// Pluggable source of disclosures for one Discover Features 2.0 <c>feature-type</c>
/// (FR-PROTO-05). The handler routes each inbound <see cref="FeatureQuery"/> to the
/// provider whose <see cref="FeatureType"/> matches; consumer apps add providers for
/// goal-codes, custom headers, or app-specific constraints via DI.
/// </summary>
/// <remarks>
/// Implementations are singletons in the DI graph and MUST be thread-safe. Built-in
/// providers ship for <c>protocol</c> (reflects <see cref="ProtocolHandlerRegistry"/>) and
/// <c>constraint</c> (advertises <c>max_receive_bytes</c> from <c>DidCommOptions</c>);
/// goal-code / header providers are consumer-supplied — there are no defaults.
/// </remarks>
public interface IFeatureProvider
{
    /// <summary>
    /// Which Discover Features 2.0 <c>feature-type</c> this provider answers for. Compared
    /// case-insensitively at dispatch time so e.g. <c>"Protocol"</c> and <c>"protocol"</c>
    /// resolve to the same provider.
    /// </summary>
    string FeatureType { get; }

    /// <summary>
    /// Produce the disclosures that match <paramref name="match"/> for the inbound query.
    /// Returns an empty enumerable when nothing matches (the handler concatenates results
    /// across all providers — empty is meaningful, not an error).
    /// </summary>
    /// <param name="match">The spec's <c>match</c> string from the query. Trailing <c>*</c> is the wildcard.</param>
    /// <param name="context">Dispatcher context — providers MAY read thread state / options.</param>
    IEnumerable<FeatureDisclosure> Disclose(string match, ProtocolContext context);
}
