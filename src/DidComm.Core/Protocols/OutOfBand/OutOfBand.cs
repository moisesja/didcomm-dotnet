using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Composition;
using DidComm.Jose;
using DidComm.Json;
using DidComm.Messages;

namespace DidComm.Protocols.OutOfBand;

/// <summary>
/// Out-of-Band 2.0 (FR-OOB-01..05): build an <c>invitation</c>, encode it into a URL (the form
/// behind a QR code), and decode it on the recipient side.
/// </summary>
/// <remarks>
/// <para>
/// An invitation is a plaintext message delivered out of band — over a URL, QR code, email, or
/// SMS — so it carries no private data (anything sensitive moves later over encrypted DIDComm).
/// The recipient decodes it and <em>initiates</em> a follow-up protocol whose <c>pthid</c> is the
/// invitation's <c>id</c> (<see cref="OutOfBandInvitation.Id"/>), which is why this protocol needs
/// no dispatcher handler: there is no inbound invitation message to route.
/// </para>
/// <para>
/// FR-OOB-02 encodes the whitespace-free plaintext JWM as base64url in the <c>_oob</c> query
/// parameter. JSON object key order is not canonical in the spec, so this library does not
/// reproduce the spec's illustrative base64url string byte-for-byte on <em>encode</em> (our
/// serializer emits members in declaration order and includes the <c>typ</c> media type);
/// interop holds because decoding is order-independent — <see cref="FromUrl"/> round-trips both
/// our own output and the spec example.
/// </para>
/// <para>Maps to PRD §14.2 task <strong>V</strong>.</para>
/// </remarks>
public static class OutOfBand
{
    /// <summary>Protocol identifier URI for Out-of-Band 2.0.</summary>
    public const string ProtocolUri = "https://didcomm.org/out-of-band/2.0";

    /// <summary>Message type URI for an out-of-band invitation.</summary>
    public const string InvitationType = "https://didcomm.org/out-of-band/2.0/invitation";

    /// <summary>The query parameter carrying the inline base64url invitation (FR-OOB-02).</summary>
    public const string OobParameter = "_oob";

    /// <summary>The query parameter carrying the short-form retrieval id (FR-OOB-04).</summary>
    public const string OobIdParameter = "_oobid";

    private const string GoalCodeField = "goal_code";
    private const string GoalField = "goal";
    private const string AcceptField = "accept";
    private const string WebRedirectHeader = "web_redirect";

    /// <summary>
    /// Build an out-of-band invitation (FR-OOB-01). <paramref name="goalCode"/>, <paramref name="goal"/>,
    /// and <paramref name="accept"/> are written into the message <c>body</c>; <paramref name="attachments"/>
    /// carry the alternative protocol messages a recipient may act on.
    /// </summary>
    /// <param name="from">Sender DID (REQUIRED for OOB usage) recipients use for future interactions.</param>
    /// <param name="goal">OPTIONAL self-attested human-readable goal.</param>
    /// <param name="goalCode">OPTIONAL self-attested goal code (e.g. <c>"issue-vc"</c>).</param>
    /// <param name="accept">OPTIONAL ordered DIDComm media-type profiles the sender accepts.</param>
    /// <param name="attachments">OPTIONAL alternative protocol messages; only one is chosen by the recipient.</param>
    /// <param name="id">OPTIONAL explicit invitation id; when omitted a UUID v4 is generated. Becomes the response <c>pthid</c>.</param>
    public static OutOfBandInvitation CreateInvitation(
        string from,
        string? goal = null,
        string? goalCode = null,
        IEnumerable<string>? accept = null,
        IEnumerable<Attachment>? attachments = null,
        string? id = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(from);

        var builder = new MessageBuilder().WithType(InvitationType).WithFrom(from);
        if (!string.IsNullOrEmpty(id))
            builder.WithId(id);

        var body = new JsonObject();
        if (goalCode is not null) body[GoalCodeField] = goalCode;
        if (goal is not null) body[GoalField] = goal;
        if (accept is not null)
        {
            var array = new JsonArray();
            foreach (var profile in accept)
                array.Add(profile);
            body[AcceptField] = array;
        }
        if (body.Count > 0)
            builder.WithBody(body);

        if (attachments is not null)
        {
            foreach (var attachment in attachments)
                builder.WithAttachment(attachment);
        }

        return new OutOfBandInvitation(builder.Build());
    }

    /// <summary>
    /// Encode an invitation into the inline URL form (FR-OOB-02):
    /// <c>{baseUrl}?_oob={base64url(plaintext-jwm)}</c>. An existing query string on
    /// <paramref name="baseUrl"/> is preserved (the parameter is appended with <c>&amp;</c>).
    /// </summary>
    /// <param name="invitation">The invitation to encode.</param>
    /// <param name="baseUrl">Destination URL whose path returns human-readable instructions in a browser.</param>
    public static string ToUrl(OutOfBandInvitation invitation, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        ArgumentException.ThrowIfNullOrEmpty(baseUrl);

        // PackPlaintext emits compact JSON (no whitespace) per FR-OOB-02's "eliminate whitespace".
        var json = EnvelopeWriter.PackPlaintext(invitation.Message);
        var encoded = Base64Url.EncodeUtf8(json);
        return $"{baseUrl}{Separator(baseUrl)}{OobParameter}={encoded}";
    }

    /// <summary>
    /// Decode an invitation from the inline URL form (FR-OOB-02). The reverse of
    /// <see cref="ToUrl"/>; also decodes a spec-conformant <c>_oob</c> URL produced by any
    /// implementation.
    /// </summary>
    /// <param name="url">A URL carrying an <c>_oob</c> query parameter.</param>
    /// <exception cref="FormatException">When the URL has no <c>_oob</c> parameter or the payload is not an invitation.</exception>
    public static OutOfBandInvitation FromUrl(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        if (!TryGetQueryParameter(url, OobParameter, out var encoded) || string.IsNullOrEmpty(encoded))
            throw new FormatException($"Out-of-band URL is missing the required '{OobParameter}' query parameter (FR-OOB-02).");

        var json = Base64Url.DecodeUtf8(encoded);
        return FromPlaintext(json);
    }

    /// <summary>
    /// Parse an invitation from its plaintext JSON form — used for the body returned by a
    /// short-form (<c>_oobid</c>) HTTP GET (FR-OOB-04).
    /// </summary>
    /// <param name="plaintextJson">The plaintext invitation JSON.</param>
    /// <exception cref="FormatException">When the JSON does not deserialize to an out-of-band invitation.</exception>
    public static OutOfBandInvitation FromPlaintext(string plaintextJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintextJson);

        Message? message;
        try
        {
            message = JsonSerializer.Deserialize<Message>(plaintextJson, DidCommJson.Default);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Out-of-band payload is not valid JSON.", ex);
        }

        if (message is null)
            throw new FormatException("Out-of-band payload deserialized to null.");
        if (!string.Equals(message.Type, InvitationType, StringComparison.OrdinalIgnoreCase))
            throw new FormatException(
                $"Out-of-band payload is not an invitation. Expected type '{InvitationType}', got '{message.Type}'.");

        // Surface structural problems (missing id, bad chars, malformed attachments) as the
        // library's standard malformed-message error.
        message.Validate();
        return new OutOfBandInvitation(message);
    }

    /// <summary>
    /// Build the short-form URL (FR-OOB-04): <c>{baseUrl}?_oobid={oobId}</c>. The sender stores
    /// the full invitation under <paramref name="oobId"/> (see <see cref="IOobInvitationStore"/>)
    /// and serves it on an HTTP GET. Keeps the QR code small for long invitations.
    /// </summary>
    /// <param name="baseUrl">Destination URL (same host that serves the retrieval GET).</param>
    /// <param name="oobId">The opaque short-form id (typically a GUID). Do NOT use a public URL shortener.</param>
    public static string ToShortUrl(string baseUrl, string oobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseUrl);
        ArgumentException.ThrowIfNullOrEmpty(oobId);
        return $"{baseUrl}{Separator(baseUrl)}{OobIdParameter}={Uri.EscapeDataString(oobId)}";
    }

    /// <summary>
    /// Extract the short-form id from a <c>?_oobid=</c> URL (FR-OOB-04). Returns <c>false</c>
    /// (without throwing) when the URL is not a short-form OOB URL.
    /// </summary>
    /// <param name="url">A candidate short-form URL.</param>
    /// <param name="oobId">The decoded id on success; empty otherwise.</param>
    public static bool TryGetShortFormId(string url, out string oobId)
    {
        oobId = string.Empty;
        if (string.IsNullOrEmpty(url))
            return false;
        if (TryGetQueryParameter(url, OobIdParameter, out var value) && !string.IsNullOrEmpty(value))
        {
            oobId = value;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Attach a <see cref="WebRedirect"/> (FR-OOB-05) as the top-level <c>web_redirect</c> header
    /// of a concluding ack / problem-report <paramref name="message"/>.
    /// </summary>
    /// <param name="message">The message to decorate (mutated in place).</param>
    /// <param name="redirect">The redirect status + URL.</param>
    public static void AddWebRedirect(Message message, WebRedirect redirect)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(redirect);
        message.AdditionalHeaders ??= new Dictionary<string, JsonElement>();
        // Property names are written verbatim ('status', 'redirectUrl') — no naming policy is
        // registered on DidCommJson, matching the aries-rfc 0700 web_redirect shape.
        message.AdditionalHeaders[WebRedirectHeader] =
            JsonSerializer.SerializeToElement(new { status = redirect.Status, redirectUrl = redirect.RedirectUrl });
    }

    /// <summary>
    /// Read a <c>web_redirect</c> block from <paramref name="message"/> (FR-OOB-05). Returns
    /// <c>null</c> when absent or malformed (missing <c>status</c> / <c>redirectUrl</c>).
    /// </summary>
    /// <param name="message">The message to inspect.</param>
    public static WebRedirect? ReadWebRedirect(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.AdditionalHeaders is null ||
            !message.AdditionalHeaders.TryGetValue(WebRedirectHeader, out var element) ||
            element.ValueKind != JsonValueKind.Object)
            return null;

        var status = element.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var redirectUrl = element.TryGetProperty("redirectUrl", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        if (string.IsNullOrEmpty(status) || string.IsNullOrEmpty(redirectUrl))
            return null;
        return new WebRedirect(status, redirectUrl);
    }

    private static char Separator(string baseUrl) => baseUrl.Contains('?') ? '&' : '?';

    private static bool TryGetQueryParameter(string url, string key, out string value)
    {
        value = string.Empty;
        var queryStart = url.IndexOf('?');
        if (queryStart < 0)
            return false;

        var query = url[(queryStart + 1)..];
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = eq >= 0 ? pair[..eq] : pair;
            if (!string.Equals(name, key, StringComparison.Ordinal))
                continue;
            value = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..]) : string.Empty;
            return true;
        }
        return false;
    }
}
