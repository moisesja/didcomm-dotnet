using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Messages;

namespace DidComm.Protocols.Routing;

/// <summary>
/// Typed builder and parser for the DIDComm Routing Protocol 2.0 <c>forward</c> message
/// (PRD §8, FR-ROUTE-01). The <c>forward</c> message wraps a packed payload — typically a JWE
/// JSON object representing the next hop's encrypted envelope — and tells a mediator who to
/// relay that payload to via <c>body.next</c>.
/// </summary>
/// <remarks>
/// <para>
/// The spec's canonical shape (spec §Routing Protocol 2.0 / Messages) is:
/// </para>
/// <code>
/// {
///   "type": "https://didcomm.org/routing/2.0/forward",
///   "id": "abc123xyz456",
///   "to": ["did:example:mediator"],
///   "expires_time": 1516385931,
///   "body": { "next": "did:foo:1234abcd" },
///   "attachments": [ /* packed payload(s) */ ]
/// }
/// </code>
/// <para>
/// Each attachment carries the onward payload using
/// <see cref="ForwardConstants.PayloadMediaType"/>; the payload itself rides in the inline
/// <see cref="AttachmentData.Json"/> slot (JWE JSON Serialization). FR-ROUTE-01 mandates the
/// REQUIRED status of both <c>body.next</c> and the <c>attachments</c> array.
/// </para>
/// </remarks>
public static class ForwardMessage
{
    /// <summary>
    /// Build a <c>forward</c> message addressed to <paramref name="mediator"/> that instructs
    /// the mediator to relay <paramref name="packedPayloads"/> on to <paramref name="next"/>.
    /// </summary>
    /// <param name="mediator">The mediator DID that will receive (and unwrap) this forward.</param>
    /// <param name="next">The DID or key URL the mediator must forward the payload to (FR-ROUTE-01 REQUIRED).</param>
    /// <param name="packedPayloads">One or more packed DIDComm envelopes (typically a single JWE JSON document) to attach (FR-ROUTE-01 REQUIRED).</param>
    /// <param name="idGenerator">Id generator for the outer forward message; defaults to <see cref="UuidV4MessageIdGenerator.Instance"/>.</param>
    /// <param name="expiresTimeEpochSeconds">Optional <c>expires_time</c> for the forward envelope (FR-ROUTE-06 supports it on forwards as well).</param>
    /// <returns>A <see cref="Message"/> ready to hand to <c>PackEncryptedAsync</c> for the anoncrypt wrap to the mediator.</returns>
    public static Message Create(
        string mediator,
        string next,
        IReadOnlyList<string> packedPayloads,
        IMessageIdGenerator? idGenerator = null,
        long? expiresTimeEpochSeconds = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(mediator);
        ArgumentException.ThrowIfNullOrEmpty(next);
        ArgumentNullException.ThrowIfNull(packedPayloads);
        if (packedPayloads.Count == 0)
            throw new ArgumentException("A forward message MUST carry at least one packed payload (FR-ROUTE-01).", nameof(packedPayloads));

        var builder = new MessageBuilder(idGenerator ?? UuidV4MessageIdGenerator.Instance)
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithTo(mediator)
            .WithBody(new JsonObject { ["next"] = next });

        if (expiresTimeEpochSeconds is long expires)
            builder.WithExpiresTime(expires);

        foreach (var packed in packedPayloads)
        {
            if (string.IsNullOrEmpty(packed))
                throw new ArgumentException("Forward payload entries must not be null or empty.", nameof(packedPayloads));

            builder.WithAttachment(new Attachment
            {
                MediaType = ForwardConstants.PayloadMediaType,
                Data = new AttachmentData
                {
                    Json = ParsePackedAsJsonNode(packed),
                },
            });
        }

        return builder.Build();
    }

    /// <summary>
    /// Recognise <paramref name="message"/> as a Routing Protocol 2.0 <c>forward</c>. When
    /// successful, returns the <c>next</c> hop and the attached payloads (in attachment order).
    /// Returns <c>false</c> for non-forward messages so callers can branch without exceptions
    /// in their happy path. Throws <see cref="MalformedMessageException"/> when the message
    /// claims to be a forward but violates FR-ROUTE-01 (missing <c>next</c> or missing
    /// attachments) — that is a protocol violation, not a routing miss.
    /// </summary>
    /// <param name="message">A plaintext DIDComm message recovered from an unpack.</param>
    /// <param name="next">When this method returns <c>true</c>, the value of <c>body.next</c>.</param>
    /// <param name="payloads">When this method returns <c>true</c>, the ordered list of attached payloads.</param>
    /// <returns><c>true</c> when <paramref name="message"/> is a well-formed forward; <c>false</c> for any other message type.</returns>
    /// <exception cref="MalformedMessageException">When the message's <c>type</c> is the forward MTURI but the structural rules are violated.</exception>
    public static bool TryParse(
        Message message,
        out string next,
        out IReadOnlyList<Attachment> payloads)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!string.Equals(message.Type, ForwardConstants.ForwardTypeUri, StringComparison.Ordinal))
        {
            next = string.Empty;
            payloads = Array.Empty<Attachment>();
            return false;
        }

        // Read `next` defensively: a non-string token (number/object/array) is a protocol
        // violation, not a CLR error, so surface MalformedMessageException rather than letting
        // JsonValue.GetValue<string>() throw InvalidOperationException.
        string? nextValue = message.Body?["next"] is JsonValue nextToken && nextToken.TryGetValue(out string? s)
            ? s
            : null;
        if (string.IsNullOrEmpty(nextValue))
            throw new MalformedMessageException(
                "Forward message body is missing the REQUIRED 'next' field, or it is not a string (FR-ROUTE-01).");

        if (message.Attachments is null || message.Attachments.Count == 0)
            throw new MalformedMessageException(
                "Forward message is missing the REQUIRED 'attachments' array (FR-ROUTE-01).");

        next = nextValue;
        payloads = message.Attachments.ToArray();
        return true;
    }

    private static JsonNode ParsePackedAsJsonNode(string packed)
    {
        try
        {
            return JsonNode.Parse(packed)
                ?? throw new ArgumentException("Forward payload parsed to a null JSON node.", nameof(packed));
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                "Forward payload is not valid JSON. Routing Protocol 2.0 expects packed DIDComm envelopes (JWE JSON Serialization) as attachment payloads.",
                nameof(packed),
                ex);
        }
    }
}
