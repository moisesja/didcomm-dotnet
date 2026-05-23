using System.Text.Json;
using System.Text.Json.Serialization;

namespace DidComm.Json;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for DIDComm message-model serialization.
/// </summary>
/// <remarks>
/// <para>
/// Two instances are exposed: <see cref="Default"/> for general use (compact, ignores
/// null members so unset headers vanish from the wire) and the deterministic byte form
/// used for signing inputs and <c>apv</c> hashing — that one is produced by
/// <see cref="DeterministicJsonWriter"/> rather than a <see cref="JsonSerializerOptions"/>
/// instance, because <see cref="System.Text.Json"/> has no built-in member-sort hook.
/// </para>
/// <para>
/// Property naming is <c>snake_case</c>-ish but DIDComm header names are themselves
/// lowercase with underscores, so explicit <see cref="JsonPropertyNameAttribute"/> on
/// every member is sufficient — no naming policy is registered here.
/// </para>
/// </remarks>
internal static class DidCommJson
{
    /// <summary>Default options for serializing/deserializing a DIDComm <c>Message</c> / <c>Attachment</c>.</summary>
    public static readonly JsonSerializerOptions Default = CreateDefault();

    private static JsonSerializerOptions CreateDefault()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            PropertyNameCaseInsensitive = false,
        };
        options.Converters.Add(new EpochSecondsConverter());
        return options;
    }
}
