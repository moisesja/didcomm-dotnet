using System.Diagnostics.CodeAnalysis;
using DidComm.Exceptions;

namespace DidComm.Protocols.ProblemReport;

/// <summary>
/// Parsed Report Problem 2.0 problem-code per FR-PROTO-08:
/// <c>&lt;sorter&gt;.&lt;scope&gt;.&lt;descriptor[.sub…]&gt;</c>. Examples:
/// <c>e.p.xfer.cant-use-endpoint</c>, <c>w.m.req.expired</c>,
/// <c>e.p.me.res.net.unreachable</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Sorter"/> is a single character: <c>e</c> (error — terminal for the affected
/// state) or <c>w</c> (warning — protocol may continue). <see cref="Scope"/> is one of
/// <c>p</c> (protocol — the whole protocol is dead), <c>m</c> (message — only the specific
/// message is bad), or a free-form state-name (the affected state name within the protocol).
/// <see cref="Descriptor"/> is the dot-joined remainder; the spec defines a tree
/// (<c>trust[.crypto]</c>, <c>xfer</c>, <c>did</c>, <c>msg</c>,
/// <c>me[.res[.net/.memory/.storage/.compute/.money]]</c>, <c>req[.time]</c>, <c>legal</c>)
/// but the parser does NOT validate descriptor membership — implementations MUST tolerate
/// extension descriptors per the spec.
/// </para>
/// <para>
/// <see cref="StartsWith"/> implements the FR-PROTO-08 prefix-matching rule: a code
/// <c>e.p.xfer.cant-use-endpoint</c> matches the prefix <c>e.p.xfer</c>.
/// </para>
/// </remarks>
/// <param name="Sorter">Single-character severity sorter — <c>e</c> or <c>w</c>.</param>
/// <param name="Scope">Scope token — <c>p</c> / <c>m</c> / state-name.</param>
/// <param name="Descriptor">Dot-joined descriptor tail (e.g. <c>xfer.cant-use-endpoint</c>).</param>
public sealed record ProblemCode(string Sorter, string Scope, string Descriptor)
{
    /// <summary>The full code string (<see cref="Sorter"/>.<see cref="Scope"/>.<see cref="Descriptor"/>).</summary>
    public string Value => $"{Sorter}.{Scope}.{Descriptor}";

    /// <summary>True when the sorter is <c>e</c> — the affected state is dead.</summary>
    public bool IsError => string.Equals(Sorter, "e", StringComparison.Ordinal);

    /// <summary>True when the sorter is <c>w</c> — protocol may continue.</summary>
    public bool IsWarning => string.Equals(Sorter, "w", StringComparison.Ordinal);

    /// <summary>True when the scope is <c>p</c> — the entire protocol is unrecoverable.</summary>
    public bool IsProtocolScoped => string.Equals(Scope, "p", StringComparison.Ordinal);

    /// <summary>True when the scope is <c>m</c> — only the specific message is bad.</summary>
    public bool IsMessageScoped => string.Equals(Scope, "m", StringComparison.Ordinal);

    /// <summary>Parse a problem-code string; throws on malformed input.</summary>
    /// <param name="value">The code as it appears in <c>body.code</c>.</param>
    /// <exception cref="ProtocolException">When <paramref name="value"/> does not match the spec shape.</exception>
    public static ProblemCode Parse(string value)
    {
        if (!TryParse(value, out var code))
            throw new ProtocolException($"Invalid Report Problem 2.0 code per FR-PROTO-08: '{value}'.");
        return code!;
    }

    /// <summary>Try-parse a problem-code string.</summary>
    /// <param name="value">Candidate code string.</param>
    /// <param name="code">Parsed result on success; <c>null</c> otherwise.</param>
    public static bool TryParse(string? value, [NotNullWhen(true)] out ProblemCode? code)
    {
        code = null;
        if (string.IsNullOrEmpty(value)) return false;
        var parts = value.Split('.');
        if (parts.Length < 3) return false;
        var sorter = parts[0];
        if (sorter is not ("e" or "w")) return false;
        var scope = parts[1];
        if (string.IsNullOrEmpty(scope)) return false;
        // Descriptor parts join the remainder with `.`; reject empty inner segments which would
        // signal a malformed code like `e.p..xfer` (consecutive dots).
        for (var i = 2; i < parts.Length; i++)
            if (string.IsNullOrEmpty(parts[i])) return false;
        var descriptor = string.Join('.', parts, 2, parts.Length - 2);
        code = new ProblemCode(sorter, scope, descriptor);
        return true;
    }

    /// <summary>
    /// FR-PROTO-08 prefix match: <paramref name="prefix"/> is a structural prefix of this
    /// code's <see cref="Value"/> when split at the dots — so <c>"e.p.xfer"</c> matches
    /// <c>"e.p.xfer.cant-use-endpoint"</c> but NOT <c>"e.p.xferable"</c>.
    /// </summary>
    /// <param name="prefix">The prefix code to test (e.g. <c>"e.p.xfer"</c>).</param>
    public bool StartsWith(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return false;
        var prefixParts = prefix.Split('.');
        var ourParts = Value.Split('.');
        if (prefixParts.Length > ourParts.Length) return false;
        for (var i = 0; i < prefixParts.Length; i++)
        {
            if (!string.Equals(prefixParts[i], ourParts[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
