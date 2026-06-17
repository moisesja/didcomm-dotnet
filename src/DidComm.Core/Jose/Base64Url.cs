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
    /// <param name="value">Strict base64url string: no padding, no whitespace, alphabet <c>[A-Za-z0-9-_]</c>.</param>
    /// <exception cref="FormatException">When <paramref name="value"/> contains a non-base64url character or non-canonical trailing bits.</exception>
    public static byte[] Decode(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);

        // Strict JOSE base64url (RFC 7515 §2 / RFC 4648 §3.2-3.3): no padding, no line breaks, no
        // whitespace, and not standard-base64 '+'/'/'. The BCL codec tolerates trailing '=' padding
        // and silently strips embedded ASCII whitespace, so pre-validate the alphabet here and reject
        // anything outside [A-Za-z0-9-_] before delegating (#24). The BCL still enforces the
        // non-canonical-trailing-bits check.
        foreach (var c in value)
        {
            if (!IsBase64UrlChar(c))
            {
                throw new FormatException(
                    "Input is not strict base64url: a character outside the [A-Za-z0-9-_] alphabet was found " +
                    "(RFC 7515 §2 forbids padding, whitespace, and '+'/'/').");
            }
        }

        return SystemBase64Url.DecodeFromChars(value.AsSpan());
    }

    private static bool IsBase64UrlChar(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';

    /// <summary>
    /// Decode <paramref name="value"/> tolerating <c>'='</c> padding and ASCII whitespace (the BCL
    /// codec's lenient behavior). Use ONLY for transport/attachment payloads where padding is
    /// historically permitted and not spec-forbidden — DIDComm attachment <c>data.base64</c>
    /// (FR-ATT-02; cf. Aries RFC 0017), which the mediator merely relays for the recipient to
    /// re-parse. JOSE fields (JWS/JWE segments, JWK material), <c>from_prior</c> JWTs, and OOB URLs
    /// have an explicit no-pad requirement and MUST use the strict <see cref="Decode"/>.
    /// </summary>
    /// <param name="value">Base64url string, possibly padded.</param>
    /// <exception cref="FormatException">When the input is not valid base64url even allowing padding/whitespace.</exception>
    public static byte[] DecodeRelaxed(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return SystemBase64Url.DecodeFromChars(value.AsSpan());
    }

    /// <summary>Decode a base64url string and return the bytes interpreted as a UTF-8 string.</summary>
    /// <param name="value">Base64url string.</param>
    public static string DecodeUtf8(string value) => Encoding.UTF8.GetString(Decode(value));
}
