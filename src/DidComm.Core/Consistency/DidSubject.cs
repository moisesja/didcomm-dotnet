using NetDid.Core.Parsing;

namespace DidComm.Consistency;

/// <summary>
/// Implements the <c>DidSubjectOf(value)</c> primitive defined as normative in PRD §4.3:
/// parse the input string as a DID or DID URL and return the bare DID subject
/// (<c>did:&lt;method&gt;:&lt;method-specific-id&gt;</c>), discarding any path, query,
/// parameters, or fragment. This is the single comparison primitive every FR-CONSIST-*
/// check pivots on.
/// </summary>
/// <remarks>
/// Delegates to <see cref="DidParser.ParseDidUrl(string)"/> so the W3C DID-URL grammar is
/// owned by net-did rather than duplicated here.
/// </remarks>
internal static class DidSubject
{
    /// <summary>
    /// Return the bare DID subject of <paramref name="value"/>, or <c>null</c> if the input
    /// does not parse as a DID or DID URL, or is longer than
    /// <see cref="IdentifierLimits.MaxKeyIdLength"/> characters.
    /// </summary>
    /// <remarks>
    /// The length bound is a backstop (FR-API-09, #80). The receive path refuses over-long wire
    /// identifiers before they get here, but this primitive also parses values taken from resolved
    /// DID documents, such as a verification method's <c>id</c> or <c>controller</c>, and a
    /// did:webvh document comes off the network. Declining to parse those keeps every NetDid DID URL
    /// parse that didcomm itself requests bounded. Callers already treat <c>null</c> as "not a DID",
    /// so an over-long value can never compare equal to a DID subject.
    /// </remarks>
    /// <param name="value">A DID, DID URL, or any other string.</param>
    public static string? DidSubjectOf(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > IdentifierLimits.MaxKeyIdLength) return null;
        var parsed = DidParser.ParseDidUrl(value);
        return parsed?.Did.Value;
    }

    /// <summary>
    /// Equality of the DID subjects of two DID/DID-URL strings. Returns <c>false</c> when
    /// either input fails to parse.
    /// </summary>
    /// <param name="left">First DID or DID URL.</param>
    /// <param name="right">Second DID or DID URL.</param>
    public static bool SameDidSubject(string? left, string? right)
    {
        var l = DidSubjectOf(left);
        if (l is null) return false;
        var r = DidSubjectOf(right);
        return r is not null && string.Equals(l, r, StringComparison.Ordinal);
    }
}
