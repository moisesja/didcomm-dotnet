namespace DidComm.Messages;

/// <summary>
/// IANA media type constants used by DIDComm v2.1 (FR-MSG-06, FR-ENV-01).
/// </summary>
internal static class MediaTypes
{
    /// <summary>Plaintext (unprotected) DIDComm message media type.</summary>
    public const string Plaintext = "application/didcomm-plain+json";

    /// <summary>Signed DIDComm envelope media type.</summary>
    public const string Signed = "application/didcomm-signed+json";

    /// <summary>Encrypted DIDComm envelope media type (used for both anoncrypt and authcrypt; FR-ENV-01).</summary>
    public const string Encrypted = "application/didcomm-encrypted+json";

    /// <summary>
    /// Normalize a media type by prepending <c>application/</c> when absent. Per FR-MSG-06 the
    /// library accepts <c>didcomm-plain+json</c> as equivalent to <c>application/didcomm-plain+json</c>
    /// so that off-spec senders that strip the <c>application/</c> prefix still interoperate.
    /// </summary>
    /// <param name="mediaType">The media type as received on the wire.</param>
    public static string Normalize(string mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        return mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase)
            ? mediaType
            : "application/" + mediaType;
    }

    /// <summary>True if <paramref name="mediaType"/> matches <paramref name="canonical"/> after the FR-MSG-06 normalization.</summary>
    /// <param name="mediaType">Candidate media type from the wire.</param>
    /// <param name="canonical">One of the constants on this type.</param>
    public static bool Matches(string? mediaType, string canonical)
    {
        if (string.IsNullOrEmpty(mediaType)) return false;
        return string.Equals(Normalize(mediaType), canonical, StringComparison.OrdinalIgnoreCase);
    }
}
