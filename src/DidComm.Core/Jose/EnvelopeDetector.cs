using System.Text.Json;
using DidComm.Exceptions;

namespace DidComm.Jose;

/// <summary>
/// Structural sniff that classifies a packed DIDComm message string into the right
/// <see cref="EnvelopeKind"/> per FR-API-03. The rules are:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>JSON object with a <c>ciphertext</c> member → <see cref="EnvelopeKind.Encrypted"/> (JWE).</item>
///   <item>JSON object with a <c>signatures</c> array or with both <c>signature</c> and <c>payload</c>
///         members → <see cref="EnvelopeKind.Signed"/> (JWS, General or Flattened).</item>
///   <item>Anything else that parses as JSON object → <see cref="EnvelopeKind.Plaintext"/>.</item>
///   <item>Non-JSON / non-object input → <see cref="MalformedMessageException"/>.</item>
/// </list>
/// <para>
/// The sniff is media-type independent. DIDComm transports MAY carry the IANA media type
/// (FR-TRN-02), but the structural rule is the authoritative one — for example, a
/// <c>didcomm-encrypted+json</c> body with no <c>ciphertext</c> is invalid regardless of
/// what its <c>Content-Type</c> claims.
/// </para>
/// </remarks>
internal static class EnvelopeDetector
{
    /// <summary>Classify <paramref name="packedJson"/>.</summary>
    /// <param name="packedJson">UTF-8 string holding a JSON object.</param>
    /// <exception cref="MalformedMessageException">Input is empty, not JSON, or not a JSON object.</exception>
    public static EnvelopeKind Detect(string packedJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(packedJson);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(packedJson);
        }
        catch (JsonException ex)
        {
            throw new MalformedMessageException("Packed message is not valid JSON.", ex);
        }

        using (document)
        {
            return Detect(document.RootElement);
        }
    }

    /// <summary>Classify a pre-parsed JSON element (overload used by recursive unwrap).</summary>
    /// <param name="root">Parsed JSON root.</param>
    /// <exception cref="MalformedMessageException">The root is not a JSON object.</exception>
    public static EnvelopeKind Detect(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new MalformedMessageException(
                $"Packed message must be a JSON object; got {root.ValueKind}.");

        if (root.TryGetProperty("ciphertext", out _))
            return EnvelopeKind.Encrypted;

        var hasSignatures = root.TryGetProperty("signatures", out var sigs)
            && sigs.ValueKind == JsonValueKind.Array;
        var hasFlattenedSig = root.TryGetProperty("signature", out _)
            && root.TryGetProperty("payload", out _);
        if (hasSignatures || hasFlattenedSig)
            return EnvelopeKind.Signed;

        return EnvelopeKind.Plaintext;
    }
}
