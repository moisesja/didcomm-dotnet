namespace DidComm.Profiles;

/// <summary>
/// Given the <c>accept</c> array a peer advertises on its service endpoint (or its OOB
/// invitation), choose a profile this library can speak. Today the only profile we emit is
/// <see cref="Profiles.DidCommV2"/>, so negotiation reduces to: "is v2 (or its known
/// aliases) in the peer's list?" (FR-PROF-01/02).
/// </summary>
public static class ProfileNegotiator
{
    private static readonly string[] SupportedProfiles =
    {
        Profiles.DidCommV2,
    };

    /// <summary>
    /// Choose a mutually-supported profile, preferring entries earlier in the peer's list
    /// (FR-PROF-01). Returns <c>null</c> when no overlap exists; callers MAY emit a
    /// problem-report under FR-PROF-02 in that case.
    /// </summary>
    /// <param name="peerAccept">The peer's advertised <c>accept</c> array. May be <c>null</c> or empty.</param>
    /// <returns>The chosen profile string, or <c>null</c> when no compatible profile is offered.</returns>
    public static string? Choose(IEnumerable<string>? peerAccept)
    {
        if (peerAccept is null)
        {
            // Per the spec, an absent `accept` array means the peer makes no profile claim;
            // assume v2 is acceptable (this library emits nothing else).
            return Profiles.DidCommV2;
        }

        foreach (var advertised in peerAccept)
        {
            if (string.IsNullOrEmpty(advertised))
                continue;

            if (Matches(advertised, Profiles.DidCommV2))
                return Profiles.DidCommV2;
        }

        return null;
    }

    /// <summary>
    /// Returns <c>true</c> when this library supports the given profile identifier
    /// (case-insensitive, matching the spec's MTURI normalization).
    /// </summary>
    /// <param name="profile">The profile identifier to test.</param>
    public static bool IsSupported(string profile)
    {
        if (string.IsNullOrEmpty(profile))
            return false;
        foreach (var supported in SupportedProfiles)
        {
            if (Matches(profile, supported))
                return true;
        }
        return false;
    }

    private static bool Matches(string advertised, string supported)
        => string.Equals(advertised.Trim(), supported, StringComparison.OrdinalIgnoreCase);
}
