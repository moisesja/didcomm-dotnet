using System.Text.Json.Serialization;

namespace DidComm.Protocols.DiscoverFeatures;

/// <summary>
/// One entry in a Discover Features 2.0 <c>disclose</c> body: an identifier (protocol PIURI,
/// goal-code, header name, constraint name) the responder supports, plus an optional
/// constraint <see cref="Value"/> that constraint-type disclosures carry (e.g.
/// <c>max_receive_bytes</c>).
/// </summary>
public sealed class FeatureDisclosure
{
    /// <summary>The feature-type this disclosure belongs to.</summary>
    [JsonPropertyName("feature-type")]
    public string FeatureType { get; init; } = string.Empty;

    /// <summary>The disclosed identifier (PIURI / goal-code / header name / constraint name).</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Optional numeric value for constraint disclosures (FR-PROTO-05: <c>max_receive_bytes</c>
    /// carries the receiver's byte cap). Omitted on the wire when <c>null</c>.
    /// </summary>
    [JsonPropertyName("value")]
    public long? Value { get; init; }
}
