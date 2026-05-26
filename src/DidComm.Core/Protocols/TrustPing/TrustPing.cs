using System.Text.Json.Nodes;
using DidComm.Messages;

namespace DidComm.Protocols.TrustPing;

/// <summary>
/// Trust Ping 2.0 helpers (FR-PROTO-04). Construction-side mirrors of what
/// <see cref="TrustPingHandler"/> consumes on receive. Defaults track
/// <c>sicpa-dlab/didcomm-python</c>: <c>response_requested</c> default <c>true</c>; the
/// reply's <c>thid</c> = the ping's <c>id</c>.
/// </summary>
public static class TrustPing
{
    /// <summary>Protocol identifier URI for Trust Ping 2.0.</summary>
    public const string ProtocolUri = "https://didcomm.org/trust-ping/2.0";

    /// <summary>Message type URI for a ping.</summary>
    public const string PingType = "https://didcomm.org/trust-ping/2.0/ping";

    /// <summary>Message type URI for a ping-response.</summary>
    public const string ResponseType = "https://didcomm.org/trust-ping/2.0/ping-response";

    /// <summary>Body member that toggles whether a reply is expected (default <c>true</c>).</summary>
    public const string ResponseRequestedField = "response_requested";

    /// <summary>
    /// Build a <c>ping</c> message addressed from <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    /// <param name="from">Sender DID (REQUIRED — Trust Ping does not have an anonymous form).</param>
    /// <param name="to">Recipient DID.</param>
    /// <param name="responseRequested">When <c>false</c>, the recipient handler suppresses the auto-reply (FR-PROTO-04).</param>
    public static Message CreatePing(string from, string to, bool responseRequested = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentException.ThrowIfNullOrEmpty(to);
        var body = new JsonObject { [ResponseRequestedField] = responseRequested };
        return new MessageBuilder()
            .WithType(PingType)
            .WithFrom(from)
            .WithTo(to)
            .WithBody(body)
            .Build();
    }

    /// <summary>
    /// Build the canonical <c>ping-response</c> for a received <paramref name="ping"/>. The
    /// reply <c>thid</c> = ping <c>id</c>; <c>from</c>/<c>to</c> are flipped from the ping.
    /// </summary>
    /// <param name="ping">The received ping message. Must carry both <c>from</c> and at least one <c>to</c> entry.</param>
    /// <exception cref="ArgumentException">When <paramref name="ping"/> lacks a sender or recipient.</exception>
    public static Message CreateResponse(Message ping)
    {
        ArgumentNullException.ThrowIfNull(ping);
        if (string.IsNullOrEmpty(ping.From))
            throw new ArgumentException("TrustPing.CreateResponse requires the ping to carry a 'from' DID.", nameof(ping));
        if (ping.To is null || ping.To.Count == 0 || string.IsNullOrEmpty(ping.To[0]))
            throw new ArgumentException("TrustPing.CreateResponse requires the ping to carry at least one 'to' DID.", nameof(ping));

        return new MessageBuilder()
            .WithType(ResponseType)
            .WithFrom(ping.To[0])
            .WithTo(ping.From)
            .WithThid(ping.Id)
            .Build();
    }

    /// <summary>
    /// Read <c>response_requested</c> from a ping body, defaulting to <c>true</c> when the
    /// field is missing or not a boolean (matches the SICPA Python implementation).
    /// </summary>
    /// <param name="ping">The ping message to inspect.</param>
    public static bool IsResponseRequested(Message ping)
    {
        ArgumentNullException.ThrowIfNull(ping);
        if (ping.Body is null) return true;
        if (!ping.Body.TryGetPropertyValue(ResponseRequestedField, out var node) || node is null) return true;
        if (node is JsonValue value && value.TryGetValue<bool>(out var b)) return b;
        return true;
    }
}
