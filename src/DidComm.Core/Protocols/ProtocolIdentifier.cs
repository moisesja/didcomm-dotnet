using System.Text.RegularExpressions;
using DidComm.Exceptions;

namespace DidComm.Protocols;

/// <summary>
/// A parsed Protocol Identifier URI (PIURI), the prefix shape
/// <c>&lt;doc-uri&gt;/&lt;protocol-name&gt;/&lt;major.minor&gt;</c> — the same shape as
/// <see cref="MessageTypeUri"/> minus the trailing <c>/&lt;message-type&gt;</c>. Used by
/// <see cref="ProtocolHandlerRegistry"/> to index handlers and dispatch inbound messages.
/// </summary>
/// <param name="DocUri">The protocol's documentation URI (e.g. <c>https://didcomm.org</c>).</param>
/// <param name="ProtocolName">The protocol short name (e.g. <c>trust-ping</c>).</param>
/// <param name="Version">The protocol version.</param>
public sealed partial record class ProtocolIdentifier(
    string DocUri,
    string ProtocolName,
    ProtocolVersion Version)
{
    [GeneratedRegex(@"^(?<docUri>.+?)/(?<protocol>[^/]+)/(?<version>\d+\.\d+)$", RegexOptions.Compiled)]
    private static partial Regex PiuriRegex();

    /// <summary>Render as <c>doc-uri/protocol/major.minor</c>.</summary>
    public string Value => $"{DocUri}/{ProtocolName}/{Version}";

    /// <summary>Parse a PIURI; throws on malformed input.</summary>
    /// <param name="value">The PIURI to parse.</param>
    /// <exception cref="ProtocolException">When <paramref name="value"/> does not match the spec shape.</exception>
    public static ProtocolIdentifier Parse(string value)
    {
        if (!TryParse(value, out var id))
            throw new ProtocolException($"Invalid Protocol Identifier URI per FR-PROTO-01: '{value}'.");
        return id!;
    }

    /// <summary>Try-parse a PIURI string.</summary>
    /// <param name="value">Candidate PIURI string.</param>
    /// <param name="id">Parsed result on success; <c>null</c> otherwise.</param>
    public static bool TryParse(string? value, out ProtocolIdentifier? id)
    {
        id = null;
        if (string.IsNullOrEmpty(value)) return false;
        var match = PiuriRegex().Match(value);
        if (!match.Success) return false;
        if (!ProtocolVersion.TryParse(match.Groups["version"].Value, out var version)) return false;
        id = new ProtocolIdentifier(match.Groups["docUri"].Value, match.Groups["protocol"].Value, version);
        return true;
    }

    /// <summary>
    /// Same-protocol predicate per FR-PROTO-01 (case- and punctuation-insensitive name match) +
    /// FR-PROTO-02 (same major version). <see cref="ProtocolName"/> punctuation strips
    /// <c>-</c> and <c>_</c> before comparing.
    /// </summary>
    /// <param name="other">The other PIURI to test against.</param>
    public bool MatchesProtocolAndMajor(ProtocolIdentifier other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(DocUri, other.DocUri, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Normalize(ProtocolName), Normalize(other.ProtocolName), StringComparison.OrdinalIgnoreCase)
            && Version.IsCompatibleWith(other.Version);
    }

    private static string Normalize(string s)
        => s.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
}
