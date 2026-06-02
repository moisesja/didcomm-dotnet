using System.Text.Encodings.Web;
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

    /// <summary>
    /// Parse options that reject duplicate member names, for the raw <see cref="JsonDocument"/> /
    /// <see cref="System.Text.Json.Nodes.JsonNode"/> parses (JWE/JWS structure, the <c>from_prior</c>
    /// JWT, forward payloads) that don't flow through <see cref="Default"/>. Mirrors
    /// <c>AllowDuplicateProperties = false</c> on <see cref="Default"/>.
    /// </summary>
    public static readonly JsonDocumentOptions StrictDocument = new() { AllowDuplicateProperties = false };

    private static JsonSerializerOptions CreateDefault()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            PropertyNameCaseInsensitive = false,
            // Reject duplicate JSON member names rather than silently taking the last (the .NET 10
            // default is to allow them). DIDComm headers are a fixed namespace; a repeated `from` /
            // `to` / `type` is a parser-differential smuggling vector (the library would act on one
            // value while a strict/first-wins peer or audit log sees another), so fail closed.
            AllowDuplicateProperties = false,
            // Use the relaxed JSON encoder so characters like '+' inside DIDComm media types
            // ("application/didcomm-plain+json") emit as the literal character rather than the
            // \u002B JavaScript-safe escape. The JOSE / DIDComm spec vectors carry the literal
            // form; the deterministic-JSON bytes feeding apv / JWS-signing input must match.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new EpochSecondsConverter());
        return options;
    }
}
