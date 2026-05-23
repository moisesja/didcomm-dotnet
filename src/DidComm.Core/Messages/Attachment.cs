using System.Text.Json;
using System.Text.Json.Serialization;
using DidComm.Exceptions;

namespace DidComm.Messages;

/// <summary>
/// A DIDComm message attachment per §Attachments. Carries metadata describing the payload
/// plus a <see cref="Data"/> envelope (one of inline base64, inline JSON, links + hash, or
/// a JWS). All members are optional except <see cref="Data"/>; when <see cref="Id"/> is
/// present it MUST consist of unreserved URI characters because attachment ids compose URIs
/// (FR-ATT-04).
/// </summary>
public sealed class Attachment
{
    /// <summary>Attachment identifier (OPTIONAL); when present MUST be unreserved URI characters per FR-ATT-04.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Human-readable description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Original filename.</summary>
    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    /// <summary>Media type of the underlying content (e.g. <c>image/png</c>).</summary>
    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    /// <summary>Schema/format identifier of the content (e.g. a DIF presentation-exchange profile URI).</summary>
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    /// <summary>Last-modified time in UTC epoch seconds.</summary>
    [JsonPropertyName("lastmod_time")]
    public long? LastModifiedTime { get; set; }

    /// <summary>Declared byte count of the content (informational; allows progress / quota checks).</summary>
    [JsonPropertyName("byte_count")]
    public long? ByteCount { get; set; }

    /// <summary>Payload envelope. REQUIRED.</summary>
    [JsonPropertyName("data")]
    public AttachmentData Data { get; set; } = new();

    /// <summary>Forward-compatible bag for unknown attachment members (FR-MSG-15).</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; set; }

    /// <summary>
    /// Run the structural validation rules from PRD §4.2 (FR-ATT-02/03/04). Throws on the
    /// first violation; callers receive a single actionable error per attachment.
    /// </summary>
    /// <exception cref="MalformedMessageException">When the attachment is malformed.</exception>
    public void Validate()
    {
        if (Data is null)
            throw new MalformedMessageException("Attachment is missing the required 'data' member.");

        if (Id is not null && !UnreservedUriChars.IsUnreserved(Id))
            throw new MalformedMessageException(
                $"Attachment 'id' must consist entirely of unreserved URI characters (RFC 3986 §2.3) (FR-ATT-04). Got: '{Id}'.");

        Data.Validate();
    }
}
