using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Json;
using DidComm.Messages;

namespace DidComm.Protocols.DiscoverFeatures;

/// <summary>
/// Discover Features 2.0 (FR-PROTO-05) construction-side helpers + the standard feature-type
/// identifiers. Mirror of what <see cref="DiscoverFeaturesHandler"/> consumes on receive.
/// </summary>
public static class DiscoverFeatures
{
    /// <summary>Protocol identifier URI for Discover Features 2.0.</summary>
    public const string ProtocolUri = "https://didcomm.org/discover-features/2.0";

    /// <summary>Message type URI for a <c>queries</c> request.</summary>
    public const string QueriesType = "https://didcomm.org/discover-features/2.0/queries";

    /// <summary>Message type URI for the <c>disclose</c> response.</summary>
    public const string DiscloseType = "https://didcomm.org/discover-features/2.0/disclose";

    /// <summary>Spec-defined <c>feature-type</c> for protocol identifiers (PIURIs).</summary>
    public const string FeatureTypeProtocol = "protocol";

    /// <summary>Spec-defined <c>feature-type</c> for goal-codes.</summary>
    public const string FeatureTypeGoalCode = "goal-code";

    /// <summary>Spec-defined <c>feature-type</c> for header names.</summary>
    public const string FeatureTypeHeader = "header";

    /// <summary>Spec-defined <c>feature-type</c> for constraints (e.g. <c>max_receive_bytes</c>).</summary>
    public const string FeatureTypeConstraint = "constraint";

    /// <summary>The <see cref="FeatureTypeConstraint"/>-scoped identifier for the byte-cap constraint (FR-PROTO-05).</summary>
    public const string ConstraintMaxReceiveBytes = "max_receive_bytes";

    /// <summary>
    /// Build a Discover Features 2.0 <c>queries</c> message.
    /// </summary>
    /// <param name="from">Sender DID.</param>
    /// <param name="to">Recipient DID.</param>
    /// <param name="queries">One or more <see cref="FeatureQuery"/> rows.</param>
    public static Message CreateQuery(string from, string to, params FeatureQuery[] queries)
    {
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentException.ThrowIfNullOrEmpty(to);
        ArgumentNullException.ThrowIfNull(queries);
        if (queries.Length == 0)
            throw new ArgumentException("At least one FeatureQuery is required.", nameof(queries));

        return new MessageBuilder()
            .WithType(QueriesType)
            .WithFrom(from)
            .WithTo(to)
            .WithBody(BodyOf("queries", queries))
            .Build();
    }

    /// <summary>
    /// Build a Discover Features 2.0 <c>disclose</c> reply.
    /// </summary>
    /// <param name="from">Responder DID (the disclosure source).</param>
    /// <param name="to">Original querier DID.</param>
    /// <param name="thid">Thread id — the queries message's <c>id</c>.</param>
    /// <param name="disclosures">Zero or more <see cref="FeatureDisclosure"/> rows. Per FR-PROTO-05, an empty array is meaningful: it asserts "no matches for any query" but is NOT equivalent to "the responder does not support Discover Features".</param>
    public static Message CreateDisclose(string from, string to, string thid, params FeatureDisclosure[] disclosures)
    {
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentException.ThrowIfNullOrEmpty(to);
        ArgumentException.ThrowIfNullOrEmpty(thid);
        ArgumentNullException.ThrowIfNull(disclosures);

        return new MessageBuilder()
            .WithType(DiscloseType)
            .WithFrom(from)
            .WithTo(to)
            .WithThid(thid)
            .WithBody(BodyOf("disclosures", disclosures))
            .Build();
    }

    /// <summary>
    /// Parse the <c>queries</c> array out of a <c>queries</c> message body. Returns an empty
    /// array when the body is absent / malformed; the dispatcher's handler treats that as "no
    /// queries, no disclosures" rather than as an error.
    /// </summary>
    /// <param name="message">A message of type <see cref="QueriesType"/>.</param>
    public static IReadOnlyList<FeatureQuery> ReadQueries(Message message)
        => ReadArray<FeatureQuery>(message, "queries");

    /// <summary>Parse the <c>disclosures</c> array out of a <c>disclose</c> message body.</summary>
    /// <param name="message">A message of type <see cref="DiscloseType"/>.</param>
    public static IReadOnlyList<FeatureDisclosure> ReadDisclosures(Message message)
        => ReadArray<FeatureDisclosure>(message, "disclosures");

    private static JsonObject BodyOf<T>(string arrayMember, T[] items)
    {
        // Serialize → parse so JsonPropertyName attributes (e.g. "feature-type" with hyphen)
        // map correctly; building the JsonObject by hand would risk drifting from the spec
        // wire shape.
        var bodyJson = JsonSerializer.Serialize(new Dictionary<string, T[]> { [arrayMember] = items }, DidCommJson.Default);
        return JsonNode.Parse(bodyJson)!.AsObject();
    }

    private static IReadOnlyList<T> ReadArray<T>(Message message, string arrayMember)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Body is null) return Array.Empty<T>();
        if (!message.Body.TryGetPropertyValue(arrayMember, out var arr) || arr is not JsonArray array)
            return Array.Empty<T>();
        var parsed = new List<T>(array.Count);
        foreach (var node in array)
        {
            if (node is null) continue;
            try
            {
                var item = node.Deserialize<T>(DidCommJson.Default);
                if (item is not null) parsed.Add(item);
            }
            catch (JsonException)
            {
                // FR-PROTO-05: ignore unrecognized / malformed entries rather than fail the
                // whole message — the spec wants Discover Features to be permissive.
            }
        }
        return parsed;
    }
}
