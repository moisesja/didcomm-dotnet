using DidComm.Consistency;
using DidComm.Messages;
using DidComm.Threading;

namespace DidComm.Protocols.DiscoverFeatures;

/// <summary>
/// Handler for Discover Features 2.0 (FR-PROTO-05). On receipt of a <c>queries</c> message,
/// routes each <see cref="FeatureQuery"/> to the <see cref="IFeatureProvider"/> whose
/// <see cref="IFeatureProvider.FeatureType"/> matches (case-insensitive), concatenates the
/// disclosures, and replies with a single <c>disclose</c> message in the query's thread: its
/// <c>thid</c> is the query's <c>thid</c> when the query declares one, else the query's <c>id</c>
/// (DIDComm v2.1 threading).
/// </summary>
/// <remarks>
/// <para>
/// FR-PROTO-05 semantics enforced here:
/// <list type="bullet">
///   <item><description>Unrecognized <c>feature-type</c> values are silently skipped.</description></item>
///   <item><description>An empty disclosures array is meaningful and SHOULD be returned when queries
///     produced no matches — it asserts "no matches", not "Discover Features unsupported".</description></item>
///   <item><description>A <c>disclose</c>-typed inbound (the reply) is the terminal leaf; we never reply.</description></item>
///   <item><description>When decrypt metadata is available, the reply's <c>from</c> is the DID
///     subject of the recipient key that actually decrypted the query. This prevents a different
///     tenant placed first in a multi-recipient <c>to</c> array from selecting the responder.
///     Plaintext/direct handler calls retain the construction-time first-recipient fallback. A
///     decrypted query without <c>to</c> is answered from the decrypting DID (DIDComm v2.1: without
///     <c>to</c>, each recipient SHOULD assume it is the only one); a query without <c>from</c>, or a
///     plaintext one without <c>to</c>, gets no reply.</description></item>
///   <item><description>A query passed in directly (not through unpack) that declares a <c>thid</c>
///     that is not a valid thread id is refused with <see cref="Exceptions.MalformedMessageException"/>,
///     never answered on another thread.</description></item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// Registered and queried in <c>samples/02-Cookbook</c> (section T) and <c>samples/05-WebSocketChat</c>.
/// </example>
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

        // A reply needs both ends: the querier's `from`, and our DID. Ours is the DID whose key
        // decrypted the query; without one (plaintext, a direct call) it is the first `to` entry.
        // A decrypted query without `to` is still ours: "If the header is omitted, each recipient
        // SHOULD assume they are the only recipient" (DIDComm v2.1 message_structure.md, `to`).
        if (string.IsNullOrEmpty(message.From))
            return Task.FromResult<Message?>(null);
        var responder = DidSubject.DidSubjectOf(context.Received.RecipientKid)
            ?? (message.To is { Count: > 0 } && !string.IsNullOrEmpty(message.To[0]) ? message.To[0] : null);
        if (responder is null)
            return Task.FromResult<Message?>(null);

        var queries = DiscoverFeatures.ReadQueries(message);
        var disclosures = new List<FeatureDisclosure>();
        // De-duplicate by (feature-type, id): overlapping queries (e.g. several `match:"*"`) would
        // otherwise repeat the same disclosure many times, inflating the reply far beyond the request.
        var seen = new HashSet<(string FeatureType, string Id)>();

        foreach (var query in queries)
        {
            if (string.IsNullOrEmpty(query.FeatureType) || string.IsNullOrEmpty(query.Match))
                continue;
            foreach (var provider in _providers)
            {
                if (!string.Equals(provider.FeatureType, query.FeatureType, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var disclosure in provider.Disclose(query.Match, context))
                {
                    if (seen.Add((disclosure.FeatureType, disclosure.Id)))
                        disclosures.Add(disclosure);
                }
            }
        }

        var reply = DiscoverFeatures.CreateDisclose(
            from: responder,
            to: message.From,
            thid: ThreadIds.ResponseThreadId(message),
            disclosures: disclosures.ToArray());

        return Task.FromResult<Message?>(reply);
    }
}
