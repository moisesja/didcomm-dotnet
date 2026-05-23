using SystemBase64Url = System.Buffers.Text.Base64Url;

namespace DidComm.Jose;

/// <summary>
/// Base64url-no-pad codec used throughout JOSE / DIDComm: protected-header serialization,
/// JWS signature/payload bytes, JWE encrypted-key/iv/ciphertext/tag, JWK key fields,
/// <c>apv</c> and <c>apu</c> values.
/// </summary>
/// <remarks>
/// Thin wrapper over <c>System.Buffers.Text.Base64Url</c> (.NET 9+ BCL). One source of truth
/// so every site that handles JOSE bytes encodes and decodes identically; ad-hoc
/// <c>Convert.ToBase64String(...).Replace(...)</c> calls would each drift.
/// </remarks>
internal static class Base64Url
{
    /// <summary>Encode <paramref name="bytes"/> to base64url without padding.</summary>
    /// <param name="bytes">Bytes to encode.</param>
    public static string Encode(ReadOnlySpan<byte> bytes) => SystemBase64Url.EncodeToString(bytes);

    /// <summary>Encode a UTF-8 string's bytes to base64url without padding.</summary>
    /// <param name="utf8String">String to UTF-8-encode and then base64url-encode.</param>
    public static string EncodeUtf8(string utf8String)
    {
        ArgumentNullException.ThrowIfNull(utf8String);
        return Encode(Encoding.UTF8.GetBytes(utf8String));
    }

    /// <summary>Decode <paramref name="value"/> from base64url without padding to bytes.</summary>
    /// <param name="value">Base64url string (no padding).</param>
    public static byte[] Decode(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return SystemBase64Url.DecodeFromChars(value.AsSpan());
    }

    /// <summary>Decode a base64url string and return the bytes interpreted as a UTF-8 string.</summary>
    /// <param name="value">Base64url string.</param>
    public static string DecodeUtf8(string value) => Encoding.UTF8.GetString(Decode(value));
}
