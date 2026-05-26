using DidComm.Messages;

namespace DidComm.Protocols.DiscoverFeatures;

/// <summary>
/// Handler for Discover Features 2.0 (FR-PROTO-05). On receipt of a <c>queries</c> message,
/// routes each <see cref="FeatureQuery"/> to the <see cref="IFeatureProvider"/> whose
/// <see cref="IFeatureProvider.FeatureType"/> matches (case-insensitive), concatenates the
/// disclosures, and replies with a single <c>disclose</c> message threaded to the query.
/// </summary>
/// <remarks>
/// <para>
/// FR-PROTO-05 semantics enforced here:
/// <list type="bullet">
///   <item><description>Unrecognized <c>feature-type</c> values are silently skipped.</description></item>
///   <item><description>An empty disclosures array is meaningful and SHOULD be returned when queries
///     produced no matches — it asserts "no matches", not "Discover Features unsupported".</description></item>
///   <item><description>A <c>disclose</c>-typed inbound (the reply) is the terminal leaf; we never reply.</description></item>
///   <item><description>For multi-recipient queries (the inbound <c>to</c> array has more than one
///     entry — uncommon for Discover Features, which is point-to-point in practice), the reply's
///     <c>from</c> is the first <c>to</c> entry. Single-recipient queries — the typical case —
///     trivially round-trip.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class DiscoverFeaturesHandler : IProtocolHandler
{
    private readonly IReadOnlyList<IFeatureProvider> _providers;

    /// <summary>Construct the handler with the set of registered providers.</summary>
    /// <param name="providers">The DI-resolved feature providers. Order is not significant.</param>
    public DiscoverFeaturesHandler(IEnumerable<IFeatureProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToArray();
    }

    /// <inheritdoc />
    public string ProtocolUri => DiscoverFeatures.ProtocolUri;

    /// <inheritdoc />
    public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(message.Type, DiscoverFeatures.QueriesType, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<Message?>(null);

        // Replies need both ends present so we can flip from/to.
        if (string.IsNullOrEmpty(message.From) || message.To is not { Count: > 0 } || string.IsNullOrEmpty(message.To[0]))
            return Task.FromResult<Message?>(null);

        var queries = DiscoverFeatures.ReadQueries(message);
        var disclosures = new List<FeatureDisclosure>();

        foreach (var query in queries)
        {
            if (string.IsNullOrEmpty(query.FeatureType) || string.IsNullOrEmpty(query.Match))
                continue;
            foreach (var provider in _providers)
            {
                if (!string.Equals(provider.FeatureType, query.FeatureType, StringComparison.OrdinalIgnoreCase))
                    continue;
                disclosures.AddRange(provider.Disclose(query.Match, context));
            }
        }

        var reply = DiscoverFeatures.CreateDisclose(
            from: message.To[0],
            to: message.From,
            thid: message.Id,
            disclosures: disclosures.ToArray());

        return Task.FromResult<Message?>(reply);
    }
}
