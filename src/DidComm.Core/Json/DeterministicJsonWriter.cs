using System.Text.Json;
using System.Text.Json.Nodes;

namespace DidComm.Json;

/// <summary>
/// Writes a <see cref="JsonNode"/> (or a DIDComm value serialized through it) to a UTF-8
/// byte sequence in a reproducible canonical form: no whitespace, object members sorted
/// ASCII-lexicographically by key at every nesting level.
/// </summary>
/// <remarks>
/// <para>
/// NFR-10 mandates that the bytes fed into a JWS signature input or the recipient-kid
/// hash for <c>apv</c> (FR-ENC-13) must be reproducible given the same logical content,
/// regardless of original key order or incidental whitespace. <see cref="System.Text.Json"/>
/// preserves insertion order, so we walk the tree and emit a sorted copy here.
/// </para>
/// <para>
/// This is JCS-flavored but deliberately not full RFC 8785: number canonicalization is
/// not done (DIDComm headers that take numbers — <c>created_time</c>, <c>expires_time</c>,
/// <c>byte_count</c> — are always JSON integers, so no float normalization is needed).
/// If a future header forces fractional numbers, swap to a JCS library at that point.
/// </para>
/// </remarks>
internal static class DeterministicJsonWriter
{
    /// <summary>Serialize <paramref name="node"/> into a canonical UTF-8 byte sequence.</summary>
    public static byte[] WriteUtf8(JsonNode? node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            WriteNode(writer, node);
        }
        return stream.ToArray();
    }

    /// <summary>Serialize <paramref name="node"/> into a canonical UTF-8 string.</summary>
    public static string WriteString(JsonNode? node)
        => Encoding.UTF8.GetString(WriteUtf8(node));

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var kvp in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(kvp.Key);
                    WriteNode(writer, kvp.Value);
                }
                writer.WriteEndObject();
                return;
            case JsonArray arr:
                writer.WriteStartArray();
                foreach (var item in arr)
                    WriteNode(writer, item);
                writer.WriteEndArray();
                return;
            case JsonValue val:
                WriteValue(writer, val);
                return;
            default:
                throw new InvalidOperationException($"Unsupported JsonNode kind: {node.GetType().Name}");
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonValue val)
    {
        val.WriteTo(writer);
    }
}
