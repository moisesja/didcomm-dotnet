using System.Text.Json.Serialization;

namespace DidComm.Protocols.DiscoverFeatures;

/// <summary>
/// One entry in a Discover Features 2.0 <c>queries</c> body: the kind of thing being asked
/// about (<see cref="FeatureType"/> ∈ <c>protocol</c> / <c>goal-code</c> / <c>header</c> /
/// <c>constraint</c>) and a <see cref="Match"/> pattern with <c>*</c> as a tail wildcard.
/// </summary>
public sealed class FeatureQuery
{
    /// <summary>Spec feature-type identifier. Implementations MUST ignore unrecognized values (FR-PROTO-05).</summary>
    [JsonPropertyName("feature-type")]
    public string FeatureType { get; init; } = string.Empty;

    /// <summary>
    /// Match pattern. Supports a trailing <c>*</c> as the spec-defined wildcard
    /// (<c>"https://didcomm.org/*"</c> = "anything under that root"); without the <c>*</c> the
    /// match is exact.
    /// </summary>
    [JsonPropertyName("match")]
    public string Match { get; init; } = string.Empty;
}
