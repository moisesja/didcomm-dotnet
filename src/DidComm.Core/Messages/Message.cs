using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DidComm.Exceptions;

namespace DidComm.Messages;

/// <summary>
/// A DIDComm v2.1 plaintext message — the in-memory shape that <c>PackPlaintextAsync</c> emits,
/// <c>UnpackAsync</c> recovers, and protocols read/write. Members map 1:1 to the JOSE-style
/// headers documented in §Plaintext Message Structure / §Message Headers of the spec.
/// </summary>
/// <remarks>
/// <para>
/// The type is a mutable POCO (not a record) so the fluent <see cref="MessageBuilder"/> and the
/// unpack→repack round-trip required by FR-MSG-15 can mutate headers in place without
/// generating intermediate <c>with</c> copies. Unknown / extension headers land in
/// <see cref="AdditionalHeaders"/> via <see cref="JsonExtensionDataAttribute"/>; this satisfies
/// FR-MSG-12 (don't fail on unknown headers) and FR-MSG-15 (preserve them across a round-trip).
/// </para>
/// <para>
/// The model is JSON-shape-only; semantic validation (FR-MSG-02/05/07/08/11) is performed by
/// <see cref="Validate"/> and the consistency checks live in <c>DidComm.Consistency</c>.
/// </para>
/// </remarks>
public sealed class Message
{
    /// <summary>Message identifier (REQUIRED, FR-MSG-02). Lowercase, unreserved URI characters.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Message type as a Message Type URI (REQUIRED, FR-MSG-05). See <see cref="Protocols.MessageTypeUri"/>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Media type. Defaults to <see cref="MediaTypes.Plaintext"/> for plaintext (FR-MSG-06).</summary>
    [JsonPropertyName("typ")]
    public string? Typ { get; set; } = MediaTypes.Plaintext;

    /// <summary>Recipient DIDs / DID-URLs (without fragment). OPTIONAL on the wire (FR-MSG-07).</summary>
    [JsonPropertyName("to")]
    public IList<string>? To { get; set; }

    /// <summary>Sender DID / DID-URL (without fragment). OPTIONAL; REQUIRED for authcrypt (FR-MSG-08).</summary>
    [JsonPropertyName("from")]
    public string? From { get; set; }

    /// <summary>
    /// DID-rotation header (FR-ROT-01): a JWT signed by a key authorized in the <em>prior</em>
    /// DID's <c>authentication</c> relationship. When present, the inner <see cref="From"/> is
    /// the new DID and the JWT's <c>iss</c> claim is the prior DID. Per FR-ROT-03, a message
    /// carrying <c>from_prior</c> MUST be sent encrypted; the facade enforces that at pack
    /// time. The validated claims are surfaced on the unpack-side metadata.
    /// </summary>
    [JsonPropertyName("from_prior")]
    public string? FromPrior { get; set; }

    /// <summary>Thread identifier; same constraints as <see cref="Id"/> (FR-MSG-11).</summary>
    [JsonPropertyName("thid")]
    public string? Thid { get; set; }

    /// <summary>Parent-thread identifier; same constraints as <see cref="Id"/> (FR-MSG-11).</summary>
    [JsonPropertyName("pthid")]
    public string? Pthid { get; set; }

    /// <summary>Message creation time, UTC epoch seconds (FR-MSG-09).</summary>
    [JsonPropertyName("created_time")]
    public long? CreatedTime { get; set; }

    /// <summary>Message expiration time, UTC epoch seconds (FR-MSG-09).</summary>
    [JsonPropertyName("expires_time")]
    public long? ExpiresTime { get; set; }

    /// <summary>
    /// Application body. OPTIONAL on the wire (FR-MSG-10: 2.1 change); absence is equivalent
    /// to an empty <c>{}</c>. When sending, the library emits <c>{}</c> for v2.0 compatibility
    /// if the application leaves this null (see <see cref="MessageBuilder"/>).
    /// </summary>
    [JsonPropertyName("body")]
    public JsonObject? Body { get; set; }

    /// <summary>Attachments, OPTIONAL.</summary>
    [JsonPropertyName("attachments")]
    public IList<Attachment>? Attachments { get; set; }

    /// <summary>
    /// Unknown / extension headers preserved verbatim across an unpack→repack round-trip
    /// (FR-MSG-12, FR-MSG-15). Populated by <see cref="System.Text.Json"/> for any JSON
    /// member that does not bind to one of the strongly-typed properties.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalHeaders { get; set; }

    /// <summary>
    /// Structural validation. Throws <see cref="MalformedMessageException"/> on the first
    /// problem; callers receive one actionable failure per message.
    /// </summary>
    /// <remarks>
    /// Enforces FR-MSG-02 (<c>id</c> required, unreserved chars), FR-MSG-05 (<c>type</c>
    /// required and a parseable MTURI), FR-MSG-06 (<c>typ</c> media-type acceptance is
    /// case-insensitive after FR-MSG-06 prefix normalization), FR-MSG-07/08
    /// (<c>to</c>/<c>from</c> entries MUST NOT carry a fragment), FR-MSG-11
    /// (<c>thid</c>/<c>pthid</c> same constraints as <c>id</c>), and FR-ATT-02/03/04
    /// (attachment validation, delegated to <see cref="Attachment.Validate"/>).
    /// </remarks>
    /// <exception cref="MalformedMessageException">When the message violates a §4 / §4.2 structural rule.</exception>
    public void Validate()
    {
        if (string.IsNullOrEmpty(Id))
            throw new MalformedMessageException("Message 'id' is REQUIRED (FR-MSG-02).");
        if (!UnreservedUriChars.IsUnreserved(Id))
            throw new MalformedMessageException(
                $"Message 'id' must consist entirely of unreserved URI characters (RFC 3986 §2.3) (FR-MSG-02). Got: '{Id}'.");

        if (string.IsNullOrEmpty(Type))
            throw new MalformedMessageException("Message 'type' is REQUIRED (FR-MSG-05).");
        if (!Protocols.MessageTypeUri.IsValid(Type))
            throw new MalformedMessageException(
                $"Message 'type' is not a valid Message Type URI per FR-PROTO-01: '{Type}'.");

        if (Thid is not null && !UnreservedUriChars.IsUnreserved(Thid))
            throw new MalformedMessageException(
                $"Message 'thid' must consist entirely of unreserved URI characters (FR-MSG-11). Got: '{Thid}'.");

        if (Pthid is not null && !UnreservedUriChars.IsUnreserved(Pthid))
            throw new MalformedMessageException(
                $"Message 'pthid' must consist entirely of unreserved URI characters (FR-MSG-11). Got: '{Pthid}'.");

        if (To is not null)
        {
            foreach (var to in To)
            {
                if (string.IsNullOrEmpty(to))
                    throw new MalformedMessageException("Message 'to' entries must not be empty (FR-MSG-07).");
                if (to.IndexOf('#') >= 0)
                    throw new MalformedMessageException(
                        $"Message 'to' entries MUST NOT contain a fragment (FR-MSG-07). Got: '{to}'.");
            }
        }

        if (From is not null && From.IndexOf('#') >= 0)
            throw new MalformedMessageException(
                $"Message 'from' MUST NOT contain a fragment (FR-MSG-08). Got: '{From}'.");

        if (Attachments is not null)
        {
            foreach (var attachment in Attachments)
                attachment.Validate();
        }
    }
}
