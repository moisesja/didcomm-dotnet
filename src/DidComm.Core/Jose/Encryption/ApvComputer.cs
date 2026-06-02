namespace DidComm.Jose.Encryption;

/// <summary>
/// Implements FR-ENC-13: <c>apv = base64url-no-pad( SHA-256( sorted-recipient-kids-joined-with('.') ) )</c>.
/// The protected header's <c>apv</c> commits the JWE to its recipient list — a downstream
/// peer that re-encodes the envelope without updating <c>apv</c> will fail the receive-side
/// re-derivation check.
/// </summary>
internal static class ApvComputer
{
    /// <summary>Compute the base64url-no-pad string form of <c>apv</c>.</summary>
    /// <param name="recipientKids">Recipient kids (DID URLs) in any order; sorted lexicographically before hashing.</param>
    public static string Compute(IEnumerable<string> recipientKids)
        => Base64Url.Encode(ComputeBytes(recipientKids));

    /// <summary>Compute the raw 32-byte hash form of <c>apv</c> (used as the KDF <c>PartyVInfo</c>).</summary>
    /// <param name="recipientKids">Recipient kids in any order.</param>
    public static byte[] ComputeBytes(IEnumerable<string> recipientKids)
    {
        ArgumentNullException.ThrowIfNull(recipientKids);
        var sorted = recipientKids.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        if (sorted.Length == 0)
            throw new ArgumentException("apv requires at least one recipient kid.", nameof(recipientKids));
        var joined = string.Join('.', sorted);
        // UTF-8 (not ASCII): recipient kids are DID URLs whose grammar is ASCII, but Encoding.ASCII
        // silently maps any non-ASCII byte to '?', which would alias two distinct kid lists onto the
        // same apv commitment. UTF-8 preserves every byte; identical for the ASCII case, so the
        // send/receive re-derivation still matches.
        return SHA256.HashData(Encoding.UTF8.GetBytes(joined));
    }
}
