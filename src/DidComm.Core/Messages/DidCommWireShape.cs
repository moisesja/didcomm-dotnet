using System.Text.Json;
using System.Text.Unicode;

namespace DidComm.Messages;

/// <summary>
/// The shape every DIDComm message has on the wire, whatever its envelope (#98): UTF-8 JSON whose
/// top level is one JSON object (a plaintext message, or a JWS or JWE in JSON serialization).
/// DIDComm v2.1 defines only <c>+json</c> encodings (message structure, "DIDComm Messaging
/// encodings"; binary ones only "if future versions of this spec allow"), and JSON exchanged
/// between systems MUST be UTF-8 (RFC 8259 §8.1). The one rule shared by the places that pass a
/// message on without packing it themselves: a mediator relaying a forward's attachment and
/// <see cref="Facade.DidCommClient.SendPackedAsync"/>.
/// </summary>
internal static class DidCommWireShape
{
    /// <summary>
    /// Whether <paramref name="bytes"/> are valid UTF-8 and exactly one JSON object (default reader
    /// rules: no comments, no trailing commas, no byte order mark, the default depth limit the
    /// unpack path also uses, nothing but white space after the object). Never throws.
    /// </summary>
    /// <param name="bytes">The bytes to judge.</param>
    internal static bool IsUtf8JsonObject(ReadOnlySpan<byte> bytes)
    {
        if (!Utf8.IsValid(bytes))
            return false;
        var reader = new Utf8JsonReader(bytes, default(JsonReaderOptions));
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;
            reader.Skip();
            return !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
