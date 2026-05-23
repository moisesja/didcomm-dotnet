using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DidComm.Exceptions;
using DidComm.Json;

namespace DidComm.Jose.Signing;

/// <summary>
/// DTO for the JWS protected header used by DIDComm signed messages. Carries
/// <c>typ</c> (typically <c>"application/didcomm-signed+json"</c>; the inner-construction
/// header MAY use <c>"JWM"</c> per FR-SIG-04), <c>alg</c> (the JOSE signing algorithm),
/// <c>kid</c> (the signer key identifier — DID URL with fragment, FR-SIG-03), plus an
/// extension-data bag so unknown header members survive an unpack→repack round-trip
/// (FR-MSG-15).
/// </summary>
internal sealed class JwsProtectedHeader
{
    [JsonPropertyName("alg")]
    public string Alg { get; set; } = string.Empty;

    [JsonPropertyName("kid")]
    public string Kid { get; set; } = string.Empty;

    [JsonPropertyName("typ")]
    public string? Typ { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalMembers { get; set; }

    /// <summary>Emit the header as a canonical UTF-8 byte sequence and then base64url it for use in the JWS signing input.</summary>
    public string EncodeBase64Url()
    {
        var node = (JsonObject)JsonSerializer.SerializeToNode(this, DidCommJson.Default)!;
        var bytes = DeterministicJsonWriter.WriteUtf8(node);
        return Base64Url.Encode(bytes);
    }

    /// <summary>Parse a base64url-encoded protected header back into a <see cref="JwsProtectedHeader"/>.</summary>
    /// <param name="encoded">Base64url string (no padding) carrying the JSON header.</param>
    /// <exception cref="MalformedMessageException">When <paramref name="encoded"/> is not valid base64url-encoded JSON.</exception>
    public static JwsProtectedHeader Decode(string encoded)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoded);
        byte[] bytes;
        try
        {
            bytes = Base64Url.Decode(encoded);
        }
        catch (FormatException ex)
        {
            throw new MalformedMessageException("JWS protected header is not valid base64url.", ex);
        }

        try
        {
            return JsonSerializer.Deserialize<JwsProtectedHeader>(bytes, DidCommJson.Default)
                ?? throw new MalformedMessageException("JWS protected header decoded to null.");
        }
        catch (JsonException ex)
        {
            throw new MalformedMessageException("JWS protected header is not valid JSON.", ex);
        }
    }
}
