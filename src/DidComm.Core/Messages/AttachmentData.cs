using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DidComm.Exceptions;

namespace DidComm.Messages;

/// <summary>
/// The <c>data</c> object inside a DIDComm attachment. Per the v2.1 §Attachments section
/// and FR-ATT-02 exactly one of <see cref="Jws"/>, <see cref="Hash"/>, <see cref="Links"/>,
/// <see cref="Base64"/>, <see cref="Json"/> SHOULD be populated (at least one MUST be).
/// When <see cref="Links"/> is set, <see cref="Hash"/> MUST also be set for integrity
/// (FR-ATT-03).
/// </summary>
public sealed class AttachmentData
{
    /// <summary>Signature over the attachment payload (JWS, detached-content allowed; FR-ATT-05).</summary>
    [JsonPropertyName("jws")]
    public JsonNode? Jws { get; set; }

    /// <summary>Multi-base / multi-hash digest of the underlying content (REQUIRED when <see cref="Links"/> is set).</summary>
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    /// <summary>Alternate URLs where the content can be fetched.</summary>
    [JsonPropertyName("links")]
    public IList<string>? Links { get; set; }

    /// <summary>Inline base64-encoded payload.</summary>
    [JsonPropertyName("base64")]
    public string? Base64 { get; set; }

    /// <summary>Inline JSON payload.</summary>
    [JsonPropertyName("json")]
    public JsonNode? Json { get; set; }

    /// <summary>Forward-compatible bag for unknown <c>data</c> members (FR-MSG-15).</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; set; }

    /// <summary>
    /// Enforces FR-ATT-02 (at least one payload member) and FR-ATT-03 (<c>links</c> requires
    /// <c>hash</c>). Called by <see cref="Attachment.Validate"/> during unpack and by the
    /// builder on send.
    /// </summary>
    /// <exception cref="MalformedMessageException">When neither validation rule is satisfied.</exception>
    public void Validate()
    {
        var hasAny =
            Jws is not null
            || Hash is not null
            || (Links is not null && Links.Count > 0)
            || Base64 is not null
            || Json is not null;

        if (!hasAny)
            throw new MalformedMessageException("Attachment 'data' must contain at least one of jws, hash, links, base64, json (FR-ATT-02).");

        if (Links is { Count: > 0 } && Hash is null)
            throw new MalformedMessageException("Attachment 'data' must include 'hash' when 'links' is present (FR-ATT-03).");
    }
}
