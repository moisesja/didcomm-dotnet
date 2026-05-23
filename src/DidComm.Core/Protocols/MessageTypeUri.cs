using System.Text.RegularExpressions;
using DidComm.Exceptions;

namespace DidComm.Protocols;

/// <summary>
/// A parsed DIDComm Message Type URI (MTURI) per §Message Type URI / §Semver Rules:
/// <c>&lt;doc-uri&gt;/&lt;protocol-name&gt;/&lt;major.minor&gt;/&lt;message-type&gt;</c>.
/// Exposes the four named components called for by FR-PROTO-01 and uses the punctuation /
/// case-insensitive comparison rule from the same FR for <see cref="Matches"/>.
/// </summary>
internal sealed partial record class MessageTypeUri(
    string DocUri,
    string ProtocolName,
    ProtocolVersion Version,
    string MessageType)
{
    // Spec MTURI shape: <doc-uri>/<protocol-name>/<major.minor>/<message-type>
    // doc-uri itself contains slashes (it's a URL), so we anchor on the
    // "/<protocol-name>/<x.y>/<message-type>$" tail and treat everything before as the doc URI.
    [GeneratedRegex(@"^(?<docUri>.+?)/(?<protocol>[^/]+)/(?<version>\d+\.\d+)/(?<message>[^/]+)$", RegexOptions.Compiled)]
    private static partial Regex MturiRegex();

    /// <summary>The full URI as it appeared on the wire.</summary>
    public string Value => $"{DocUri}/{ProtocolName}/{Version}/{MessageType}";

    /// <summary>The protocol identifier URI (PIURI) = MTURI minus the trailing <c>/&lt;message-type&gt;</c>.</summary>
    public string ProtocolIdentifier => $"{DocUri}/{ProtocolName}/{Version}";

    /// <summary>
    /// Parse a Message Type URI; throws <see cref="ProtocolException"/> when the input does
    /// not match the spec shape. Use <see cref="TryParse"/> if a thrown exception is the
    /// wrong control-flow tool.
    /// </summary>
    /// <param name="value">The MTURI from the wire.</param>
    /// <exception cref="ProtocolException">When <paramref name="value"/> is not a valid MTURI.</exception>
    public static MessageTypeUri Parse(string value)
    {
        if (!TryParse(value, out var uri))
            throw new ProtocolException($"Invalid Message Type URI per FR-PROTO-01: '{value}'.");
        return uri!;
    }

    /// <summary>Try-parse variant of <see cref="Parse"/>.</summary>
    /// <param name="value">Candidate MTURI string.</param>
    /// <param name="uri">The parsed MTURI on success; <c>null</c> otherwise.</param>
    public static bool TryParse(string? value, out MessageTypeUri? uri)
    {
        uri = null;
        if (string.IsNullOrEmpty(value)) return false;

        var match = MturiRegex().Match(value);
        if (!match.Success) return false;

        if (!ProtocolVersion.TryParse(match.Groups["version"].Value, out var version)) return false;

        uri = new MessageTypeUri(
            match.Groups["docUri"].Value,
            match.Groups["protocol"].Value,
            version,
            match.Groups["message"].Value);
        return true;
    }

    /// <summary>Fast validity check used by <see cref="DidComm.Messages.Message.Validate"/>.</summary>
    /// <param name="value">Candidate MTURI string.</param>
    public static bool IsValid(string? value) => TryParse(value, out _);

    /// <summary>
    /// FR-PROTO-01 case- and punctuation-insensitive comparison: compare protocol name and
    /// message type after stripping <c>-</c> / <c>_</c> and lowering case. The doc URI is
    /// compared <c>OrdinalIgnoreCase</c>; the version is compared via
    /// <see cref="ProtocolVersion.IsCompatibleWith"/>.
    /// </summary>
    /// <param name="other">The other MTURI to compare against.</param>
    public bool Matches(MessageTypeUri other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(DocUri, other.DocUri, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Normalize(ProtocolName), Normalize(other.ProtocolName), StringComparison.OrdinalIgnoreCase)
            && Version.IsCompatibleWith(other.Version)
            && string.Equals(Normalize(MessageType), Normalize(other.MessageType), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string s)
    {
        // FR-PROTO-01: match ignoring case and punctuation. Strip the two punctuation marks
        // that real protocols use: '-' and '_'. We intentionally do not strip other URI-safe
        // chars (letters, digits, '.'), since they are semantically meaningful.
        return s.Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal);
    }
}
