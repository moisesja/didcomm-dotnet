using DidComm.Exceptions;

namespace DidComm.Transports.Stomp;

/// <summary>
/// Strict, allocation-conscious STOMP 1.2 frame codec (FR-TRN-12). Encodes/decodes exactly the
/// wire grammar: <c>COMMAND EOL (header EOL)* EOL body NUL</c>, with the 1.2 header escapes
/// (<c>\r</c> <c>\n</c> <c>\c</c> <c>\\</c>), a <c>content-length</c> that governs the body
/// length, and nothing after the NUL terminator but optional EOLs.
/// </summary>
/// <remarks>
/// <para>
/// Strictness is deliberate — the frame is remote input on the receive path: undefined escape
/// sequences, a lying <c>content-length</c>, a missing NUL, raw CR/LF inside a header line, or
/// trailing junk all throw <see cref="MalformedMessageException"/> (the library's canonical
/// pre-crypto shape rejection; transports wrap it into their own typed error where their
/// contract requires, per FR-API-07).
/// </para>
/// <para>
/// Per STOMP 1.2, <c>CONNECT</c>/<c>CONNECTED</c> frames do NOT use header escaping (1.0
/// compatibility); all other frames do. <see cref="Encode"/> always emits a
/// <c>content-length</c> computed from the actual body so the declared and real lengths can
/// never disagree on our side.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var wire = StompFrameCodec.Encode(new StompFrame("SEND",
///     new[] { KeyValuePair.Create("destination", "/didcomm") }, bodyBytes));
/// StompFrame parsed = StompFrameCodec.Decode(wire);
/// </code>
/// </example>
public static class StompFrameCodec
{
    /// <summary>Frame/NUL terminator octet.</summary>
    private const byte Nul = 0x00;
    private const byte Cr = (byte)'\r';
    private const byte Lf = (byte)'\n';
    private const byte Colon = (byte)':';
    private const byte Backslash = (byte)'\\';

    /// <summary>Hard cap on the command token length — the longest real command is 11 octets.</summary>
    private const int MaxCommandBytes = 32;

    /// <summary>Hard cap on header count; DIDComm frames use at most 4.</summary>
    private const int MaxHeaders = 32;

    /// <summary>Hard cap on one header line (name + value, escaped), keeping hostile frames cheap.</summary>
    private const int MaxHeaderLineBytes = 8 * 1024;

    /// <summary>
    /// Serialize <paramref name="frame"/> to STOMP 1.2 wire bytes. A <c>content-length</c>
    /// header computed from the actual body is always appended (so a caller-supplied one is
    /// rejected), and header escaping is applied except on <c>CONNECT</c>/<c>CONNECTED</c>.
    /// </summary>
    /// <param name="frame">The frame to serialize.</param>
    /// <exception cref="ArgumentException">When the command or a header would be unrepresentable (empty name, raw CR/LF, a caller-supplied <c>content-length</c>, or an unescapable octet in a CONNECT/CONNECTED header).</exception>
    public static byte[] Encode(StompFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (string.IsNullOrEmpty(frame.Command) || ContainsLineBreakOrNul(frame.Command) || frame.Command.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException("STOMP command must be a non-empty token without CR/LF/NUL/colon.", nameof(frame));

        var escaped = !IsConnectFamily(frame.Command);
        var sb = new StringBuilder(128);
        sb.Append(frame.Command).Append('\n');
        foreach (var (name, value) in frame.Headers)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("STOMP header names must be non-empty.", nameof(frame));
            if (string.Equals(name, "content-length", StringComparison.Ordinal))
                throw new ArgumentException("content-length is computed from the body; do not supply it as a header.", nameof(frame));
            AppendHeaderToken(sb, name, escaped, isName: true);
            sb.Append(':');
            AppendHeaderToken(sb, value ?? string.Empty, escaped, isName: false);
            sb.Append('\n');
        }
        sb.Append("content-length:").Append(frame.Body.Length).Append('\n');
        sb.Append('\n');

        var head = Encoding.UTF8.GetBytes(sb.ToString());
        var wire = new byte[head.Length + frame.Body.Length + 1];
        head.CopyTo(wire, 0);
        frame.Body.Span.CopyTo(wire.AsSpan(head.Length));
        wire[^1] = Nul;
        return wire;
    }

    /// <summary>
    /// Parse one STOMP 1.2 frame from <paramref name="wire"/>. The caller bounds the input size
    /// (the transports cap a WebSocket message at <c>MaxReceiveBytes</c> before decoding), so
    /// the codec's own caps only guard the header section.
    /// </summary>
    /// <param name="wire">Exactly one frame — optionally preceded/followed by EOL keep-alive octets.</param>
    /// <exception cref="MalformedMessageException">On any grammar violation: empty/oversized command or header section, a header line without a colon or with a raw CR, an undefined escape sequence, a non-numeric or lying <c>content-length</c>, a missing NUL terminator, or non-EOL bytes after the NUL.</exception>
    public static StompFrame Decode(ReadOnlySpan<byte> wire)
    {
        var pos = 0;
        // Tolerate leading EOLs — the octets STOMP permits between frames.
        while (pos < wire.Length && (wire[pos] == Cr || wire[pos] == Lf))
            pos++;
        if (pos >= wire.Length)
            throw new MalformedMessageException("STOMP frame is empty.");

        var command = ReadLine(wire, ref pos, MaxCommandBytes, "command");
        if (command.Length == 0)
            throw new MalformedMessageException("STOMP frame has an empty command.");
        var unescapeHeaders = !IsConnectFamily(command);

        var headers = new List<KeyValuePair<string, string>>(4);
        while (true)
        {
            var line = ReadLine(wire, ref pos, MaxHeaderLineBytes, "header");
            if (line.Length == 0)
                break; // blank line — body follows
            if (headers.Count >= MaxHeaders)
                throw new MalformedMessageException($"STOMP frame exceeds {MaxHeaders} headers.");
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
                throw new MalformedMessageException("STOMP header line has no name:value separator.");
            var name = line[..colon];
            var value = line[(colon + 1)..];
            if (unescapeHeaders)
            {
                name = Unescape(name);
                value = Unescape(value);
            }
            headers.Add(KeyValuePair.Create(name, value));
        }

        var frame = new StompFrame(command, headers, ReadOnlyMemory<byte>.Empty);
        int bodyLength;
        if (frame.TryGetHeader("content-length", out var declaredLength))
        {
            // content-length governs the body length (mandatory rule when present). Strict digits
            // only — no sign, no whitespace, no hex.
            if (!int.TryParse(declaredLength, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out bodyLength))
                throw new MalformedMessageException("STOMP content-length is not a non-negative integer.");
            if (bodyLength > wire.Length - pos - 1)
                throw new MalformedMessageException("STOMP content-length exceeds the frame's actual body.");
            if (wire[pos + bodyLength] != Nul)
                throw new MalformedMessageException("STOMP body is not NUL-terminated at content-length.");
        }
        else
        {
            // No content-length: the body runs to the first NUL.
            var nul = wire[pos..].IndexOf(Nul);
            if (nul < 0)
                throw new MalformedMessageException("STOMP frame is missing its NUL terminator.");
            bodyLength = nul;
        }

        var body = wire.Slice(pos, bodyLength).ToArray();
        pos += bodyLength + 1; // skip NUL
        // Only EOL keep-alive octets may follow the terminator; anything else is smuggled data
        // (e.g. an attempted second frame in one WebSocket message — DIDComm is one frame per
        // message, mirroring FR-TRN-09's one-envelope-per-message rule).
        for (; pos < wire.Length; pos++)
        {
            if (wire[pos] != Cr && wire[pos] != Lf)
                throw new MalformedMessageException("STOMP frame has trailing bytes after the NUL terminator.");
        }

        return frame with { Body = body };
    }

    /// <summary>True for the CONNECT / CONNECTED / STOMP commands, which do not use header escaping (STOMP 1.2 §Value Encoding).</summary>
    private static bool IsConnectFamily(string command)
        => command is "CONNECT" or "CONNECTED" or "STOMP";

    private static bool ContainsLineBreakOrNul(string token)
        => token.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0;

    /// <summary>Read one LF-terminated line as UTF-8, tolerating a single CR before the LF; rejects raw interior CR and NUL.</summary>
    private static string ReadLine(ReadOnlySpan<byte> wire, ref int pos, int maxBytes, string what)
    {
        var rest = wire[pos..];
        var lf = rest.IndexOf(Lf);
        if (lf < 0)
            throw new MalformedMessageException($"STOMP {what} line is not LF-terminated.");
        if (lf > maxBytes)
            throw new MalformedMessageException($"STOMP {what} line exceeds {maxBytes} bytes.");
        var line = rest[..lf];
        if (line.Length > 0 && line[^1] == Cr)
            line = line[..^1];
        // A raw CR that is not part of the EOL is a header-injection attempt (escaped CRs arrive
        // as the two-octet sequence \r); NUL can never appear inside the head section.
        if (line.IndexOf(Cr) >= 0 || line.IndexOf(Nul) >= 0)
            throw new MalformedMessageException($"STOMP {what} line contains a raw CR or NUL octet.");
        pos += lf + 1;
        return Encoding.UTF8.GetString(line);
    }

    /// <summary>Apply STOMP 1.2 header escaping: <c>\</c>→<c>\\</c>, CR→<c>\r</c>, LF→<c>\n</c>, <c>:</c>→<c>\c</c>.</summary>
    private static void AppendHeaderToken(StringBuilder sb, string token, bool escaped, bool isName)
    {
        foreach (var ch in token)
        {
            // NUL has no escape sequence in ANY frame (STOMP 1.2 defines only \r \n \c \\), and a
            // raw NUL in the head section truncates the frame for any NUL-scanning parser — our own
            // Decode rejects it. Unrepresentable → refuse at encode time rather than emit a frame
            // no compliant peer can read.
            if (ch == '\0')
                throw new ArgumentException("STOMP headers cannot carry a NUL octet (no escape sequence exists for it).");
            if (!escaped)
            {
                // CONNECT/CONNECTED headers have no escape mechanism, so these octets are
                // unrepresentable there; a colon is only a problem in the NAME (the parser
                // splits on the first colon, so values may carry them).
                if (ch is '\r' or '\n' || (isName && ch == ':'))
                    throw new ArgumentException("CONNECT/CONNECTED headers cannot carry CR, LF, or a colon in the name (STOMP 1.2 has no escaping there).");
                sb.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case ':': sb.Append("\\c"); break;
                default: sb.Append(ch); break;
            }
        }
    }

    /// <summary>Reverse header escaping; any undefined escape sequence (or a dangling backslash) is fatal per STOMP 1.2.</summary>
    private static string Unescape(string token)
    {
        if (!token.Contains('\\', StringComparison.Ordinal))
            return token;
        var sb = new StringBuilder(token.Length);
        for (var i = 0; i < token.Length; i++)
        {
            var ch = token[i];
            if (ch != '\\')
            {
                sb.Append(ch);
                continue;
            }
            if (++i >= token.Length)
                throw new MalformedMessageException("STOMP header ends with a dangling escape character.");
            sb.Append(token[i] switch
            {
                'r' => '\r',
                'n' => '\n',
                'c' => ':',
                '\\' => '\\',
                _ => throw new MalformedMessageException("STOMP header contains an undefined escape sequence."),
            });
        }
        return sb.ToString();
    }
}
