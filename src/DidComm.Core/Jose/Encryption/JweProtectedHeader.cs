using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DidComm.Exceptions;
using DidComm.Json;

namespace DidComm.Jose.Encryption;

/// <summary>
/// DTO for the JWE protected header used by DIDComm anon/auth-crypt envelopes. Anoncrypt
/// uses <c>{ typ, alg=ECDH-ES+A256KW, enc, epk, apv }</c>; authcrypt adds <c>{ apu, skid }</c>
/// per FR-ENC-12/14. Both forms keep this single DTO so JWE construction and parsing
/// share one shape.
/// </summary>
internal sealed class JweProtectedHeader
{
    [JsonPropertyName("typ")]
    public string? Typ { get; set; }

    [JsonPropertyName("alg")]
    public string Alg { get; set; } = string.Empty;

    [JsonPropertyName("enc")]
    public string Enc { get; set; } = string.Empty;

    [JsonPropertyName("epk")]
    public Jwk Epk { get; set; } = new();

    [JsonPropertyName("apv")]
    public string Apv { get; set; } = string.Empty;

    [JsonPropertyName("apu")]
    public string? Apu { get; set; }

    [JsonPropertyName("skid")]
    public string? Skid { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalMembers { get; set; }

    /// <summary>Canonical base64url encoding used for the JWS-style signing/AAD input.</summary>
    public string EncodeBase64Url()
    {
        var node = (JsonObject)JsonSerializer.SerializeToNode(this, DidCommJson.Default)!;
        var bytes = DeterministicJsonWriter.WriteUtf8(node);
        return Base64Url.Encode(bytes);
    }

    /// <summary>Inverse of <see cref="EncodeBase64Url"/>; used by the parser on receive.</summary>
    /// <param name="encoded">Base64url-encoded JSON header.</param>
    /// <exception cref="MalformedMessageException">When <paramref name="encoded"/> is not valid base64url or not valid JSON.</exception>
    public static JweProtectedHeader Decode(string encoded)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoded);
        byte[] bytes;
        try
        {
            bytes = Base64Url.Decode(encoded);
        }
        catch (FormatException ex)
        {
            throw new MalformedMessageException("JWE protected header is not valid base64url.", ex);
        }

        try
        {
            return JsonSerializer.Deserialize<JweProtectedHeader>(bytes, DidCommJson.Default)
                ?? throw new MalformedMessageException("JWE protected header decoded to null.");
        }
        catch (JsonException ex)
        {
            throw new MalformedMessageException("JWE protected header is not valid JSON.", ex);
        }
    }
}
