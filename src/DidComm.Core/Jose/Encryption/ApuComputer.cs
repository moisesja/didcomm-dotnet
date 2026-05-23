namespace DidComm.Jose.Encryption;

/// <summary>
/// Implements FR-ENC-14: <c>apu = base64url-no-pad( utf8( skid ) )</c> for the authcrypt
/// (ECDH-1PU) path. Absent for anoncrypt (ECDH-ES).
/// </summary>
internal static class ApuComputer
{
    /// <summary>Compute the protected-header <c>apu</c> string form for the supplied <paramref name="skid"/>.</summary>
    /// <param name="skid">The sender key identifier (DID URL with fragment).</param>
    public static string Compute(string skid)
    {
        ArgumentException.ThrowIfNullOrEmpty(skid);
        return Base64Url.EncodeUtf8(skid);
    }
}
