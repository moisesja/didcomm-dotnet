using DidComm.Consistency;

namespace DidComm.Mediator.Coordination;

/// <summary>The "bare DID" rule of FR-MED-05: a DID with no path, query or fragment.</summary>
internal static class BareDid
{
    /// <summary>
    /// Whether <paramref name="value"/> is a DID and nothing more: non-empty, at most
    /// <see cref="IdentifierLimits.MaxDidLength"/> characters (checked first, so an over-long value
    /// is never parsed), and equal to its own DID subject (PRD §4.3). A DID URL is not normalized
    /// into its DID; it is simply not a bare DID.
    /// </summary>
    public static bool IsBareDid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > IdentifierLimits.MaxDidLength)
            return false;
        return string.Equals(DidSubject.DidSubjectOf(value), value, StringComparison.Ordinal);
    }
}
