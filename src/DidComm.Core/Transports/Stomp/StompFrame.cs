namespace DidComm.Transports.Stomp;

/// <summary>
/// One STOMP 1.2 frame (FR-TRN-12): a command, ordered headers, and an opaque body. DIDComm
/// uses only the minimal client subset — <c>CONNECT</c>/<c>CONNECTED</c> for the session
/// handshake, <c>SEND</c> carrying exactly one packed DIDComm envelope
/// (<c>content-type: application/didcomm-encrypted+json</c>), and <c>DISCONNECT</c> — with no
/// broker semantics (no subscriptions, acks, receipts, or heart-beats;
/// <c>heart-beat:0,0</c> is advertised).
/// </summary>
/// <remarks>
/// Headers keep wire order and may repeat; per STOMP 1.2 only the FIRST occurrence of a header
/// name is meaningful (<see cref="TryGetHeader"/> honors that). Serialization rules —
/// escaping, NUL termination, <c>content-length</c> — live in <see cref="StompFrameCodec"/>.
/// </remarks>
/// <param name="Command">The frame command (e.g. <c>SEND</c>). Uppercase per the spec.</param>
/// <param name="Headers">Ordered header name/value pairs, unescaped values.</param>
/// <param name="Body">The frame body bytes. Empty for control frames.</param>
/// <example>
/// <code>
/// var frame = new StompFrame("SEND", new[]
/// {
///     KeyValuePair.Create("destination", "/didcomm"),
///     KeyValuePair.Create("content-type", "application/didcomm-encrypted+json"),
/// }, packedEnvelopeBytes);
/// byte[] wire = StompFrameCodec.Encode(frame); // codec appends content-length + NUL
/// </code>
/// </example>
public sealed record StompFrame(
    string Command,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    ReadOnlyMemory<byte> Body)
{
    /// <summary>
    /// Look up a header by name (case-sensitive, as STOMP header names are). Returns the FIRST
    /// occurrence per STOMP 1.2 §Repeated Header Entries; later repeats are ignored.
    /// </summary>
    /// <param name="name">Header name, e.g. <c>content-type</c>.</param>
    /// <param name="value">The first value found; <c>null</c> when absent.</param>
    public bool TryGetHeader(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        foreach (var (key, headerValue) in Headers)
        {
            if (string.Equals(key, name, StringComparison.Ordinal))
            {
                value = headerValue;
                return true;
            }
        }
        value = null;
        return false;
    }
}
