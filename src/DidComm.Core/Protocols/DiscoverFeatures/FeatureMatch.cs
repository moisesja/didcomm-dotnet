namespace DidComm.Protocols.DiscoverFeatures;

/// <summary>
/// Shared spec-conformant matching for Discover Features 2.0 <c>match</c> strings. Trailing
/// <c>*</c> is the prefix wildcard; the bare <c>*</c> matches anything; otherwise the
/// comparison is exact. Comparison is <see cref="StringComparison.Ordinal"/> on
/// <c>protocol</c> / PIURI identifiers (DIDComm URIs are case-sensitive at the URI level even
/// though FR-PROTO-01's MTURI normalization is case-insensitive — Discover Features only
/// discloses fully-qualified PIURIs, which are spelled consistently within an org).
/// </summary>
public static class FeatureMatch
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="value"/> satisfies the spec's <paramref name="match"/>
    /// pattern. An empty match string matches nothing; <c>"*"</c> matches everything; any
    /// other trailing <c>*</c> is a prefix; otherwise exact equality.
    /// </summary>
    /// <param name="match">The spec <c>match</c> string from a <see cref="FeatureQuery"/>.</param>
    /// <param name="value">The disclosable identifier being tested (PIURI / goal-code / etc.).</param>
    public static bool Matches(string match, string value)
    {
        if (string.IsNullOrEmpty(match) || value is null) return false;
        if (match == "*") return true;
        if (match.EndsWith('*'))
        {
            var prefix = match[..^1];
            return value.StartsWith(prefix, StringComparison.Ordinal);
        }
        return string.Equals(match, value, StringComparison.Ordinal);
    }
}
