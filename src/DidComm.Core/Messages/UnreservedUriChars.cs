namespace DidComm.Messages;

/// <summary>
/// RFC 3986 §2.3 "unreserved" character classifier. Used to validate DIDComm message
/// <c>id</c> / <c>thid</c> / <c>pthid</c> (FR-MSG-02, FR-MSG-11) and attachment <c>id</c>
/// (FR-ATT-04) values, all of which must consist entirely of these characters because the
/// spec composes them into URIs.
/// </summary>
internal static class UnreservedUriChars
{
    /// <summary>True iff every character in <paramref name="value"/> is in the RFC 3986 unreserved set.</summary>
    /// <param name="value">String to inspect; <c>null</c> and empty return <c>false</c>.</param>
    public static bool IsUnreserved(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var c in value)
        {
            if (!IsUnreservedChar(c)) return false;
        }
        return true;
    }

    /// <summary>True iff <paramref name="c"/> is in the RFC 3986 unreserved set: ALPHA / DIGIT / "-" / "." / "_" / "~".</summary>
    /// <param name="c">Character to test.</param>
    public static bool IsUnreservedChar(char c)
        => (c >= 'A' && c <= 'Z')
        || (c >= 'a' && c <= 'z')
        || (c >= '0' && c <= '9')
        || c == '-' || c == '.' || c == '_' || c == '~';
}
