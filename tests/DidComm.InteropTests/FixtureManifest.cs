using System.Text.Json;
using System.Text.Json.Serialization;

namespace DidComm.InteropTests;

/// <summary>Manifest record matching <c>fixtures/schema/didcomm-fixture.v1.schema.json</c>.</summary>
/// <remarks>
/// Phase 0 deserializes enough of the manifest to drive the data-driven runner and assert
/// the smoke case loads. Field coverage expands in Phase 2 when real spec/SICPA fixtures
/// arrive — additional members survive deserialization via <see cref="AdditionalData"/>.
/// </remarks>
internal sealed class FixtureManifest
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("source_ref")]
    public string? SourceRef { get; set; }

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = string.Empty;

    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; set; }

    [JsonPropertyName("refs")]
    public FixtureRefs? Refs { get; set; }

    [JsonPropertyName("input")]
    public JsonElement? Input { get; set; }

    [JsonPropertyName("expected")]
    public FixtureExpected Expected { get; set; } = new();

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; set; }
}

internal sealed class FixtureRefs
{
    [JsonPropertyName("secrets")]
    public string? Secrets { get; set; }

    [JsonPropertyName("diddocs")]
    public IReadOnlyList<string>? DidDocs { get; set; }

    [JsonPropertyName("plaintext")]
    public string? Plaintext { get; set; }
}

internal sealed class FixtureExpected
{
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;

    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("plaintext")]
    public string? Plaintext { get; set; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; set; }

    [JsonPropertyName("match")]
    public string? Match { get; set; }
}
