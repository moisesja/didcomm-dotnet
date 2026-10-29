using System.Text.Json;
using DidComm.Exceptions;
using DidComm.Json;

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
    /// <exception cref="MalformedMessageException">Input is empty, not JSON, or not a JSON object; or
    /// it holds a string that does not decode to Unicode text (an unpaired-surrogate escape).</exception>
    public static EnvelopeKind Detect(string packedJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(packedJson);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(packedJson, DidCommJson.StrictDocument);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // Besides JsonException, the parse throws two other types for input that is just as
            // malformed. Both must leave unpack as MalformedMessageException (FR-API-07):
            // - InvalidOperationException for a member NAME written as an unpaired-surrogate escape
            //   ({"\uD800":1}). Duplicate detection decodes every name, and System.Text.Json reports
            //   one that does not decode this way. It reaches every layer, before any authentication.
            // - ArgumentException for a caller's string that is not well-formed UTF-16. Decoding wire
            //   bytes cannot produce one, but the public API accepts any string.
            throw new MalformedMessageException("Packed message is not valid JSON.", ex);
        }

        using (document)
        {
            var kind = Detect(document.RootElement);
            if (kind == EnvelopeKind.Plaintext)
                RejectUndecodableStrings(document.RootElement);
            return kind;
        }
    }

    // didcomm and consumer code both decode strings from a plaintext message: typed members, body
    // values, and extension headers such as ~trace or web_redirect. Extension headers are kept as
    // JsonElement (Message.AdditionalHeaders) and decoded only when read. A string VALUE written as an
    // unpaired-surrogate escape ("\uD800") parses, but decoding it later throws
    // InvalidOperationException, wherever that happens. RFC 8259 §8.2 calls such text unpredictable
    // and I-JSON (RFC 7493 §2.1) forbids it, so the message is refused here, once. JWS and JWE layers
    // are not walked: DataProofsDotnet.Jose checks every string in them itself (since 1.4.0). The
    // recursion is bounded by the parse's maximum depth (64).
    private static void RejectUndecodableStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    RejectUndecodableStrings(property.Value);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    RejectUndecodableStrings(item);
                break;
            case JsonValueKind.String:
                try
                {
                    _ = element.GetString();
                }
                catch (InvalidOperationException ex)
                {
                    throw new MalformedMessageException(
                        "Plaintext message contains an unpaired UTF-16 surrogate escape.", ex);
                }
                break;
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
