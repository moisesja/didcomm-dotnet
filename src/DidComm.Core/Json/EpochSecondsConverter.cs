using System.Text.Json;
using System.Text.Json.Serialization;

namespace DidComm.Json;

/// <summary>
/// JSON converter for DIDComm <c>created_time</c> / <c>expires_time</c> headers. The spec
/// (FR-MSG-09) requires these to be emitted as JSON integers (UTC epoch seconds), not
/// strings. Reading tolerates either form to accept slightly off-spec senders; writing
/// always emits integers.
/// </summary>
internal sealed class EpochSecondsConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                return reader.GetInt64();
            case JsonTokenType.String:
                {
                    var s = reader.GetString();
                    if (string.IsNullOrEmpty(s)) return null;
                    return long.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
                }
            default:
                throw new JsonException("Expected integer or string for epoch-seconds field.");
        }
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteNumberValue(value.Value);
    }
}
