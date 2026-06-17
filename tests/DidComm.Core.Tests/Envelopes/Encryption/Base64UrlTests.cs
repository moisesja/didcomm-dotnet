using System.Text;
using DidComm.Jose;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Envelopes.Encryption;

/// <summary>
/// Issue #24: <see cref="Base64Url.Decode"/> must enforce strict JOSE base64url (RFC 7515 §2):
/// no padding, no whitespace, alphabet <c>[A-Za-z0-9-_]</c> — diverging inputs throw
/// <see cref="FormatException"/>, which the receive call sites map to a <c>DidCommException</c>.
/// </summary>
public sealed class Base64UrlTests
{
    [Theory]
    [InlineData("Mw==")]        // trailing '=' padding (RFC 4648 §3.2 forbids it for base64url-no-pad)
    [InlineData("SGVsbG8=")]    // single '=' pad
    [InlineData("M w")]         // embedded space
    [InlineData(" SGVsbG8")]    // leading space
    [InlineData("SGVs\tbG8")]   // embedded tab
    [InlineData("SGVs\r\nbG8")] // embedded CRLF (line break — RFC 4648 §3.3)
    [InlineData("a+b/")]        // standard-base64 '+' and '/'
    [InlineData("SGVsbG8%3D")]  // percent-encoded padding (non-alphabet)
    public void Decode_rejects_non_strict_base64url(string input)
    {
        ((Action)(() => Base64Url.Decode(input))).Should().Throw<FormatException>();
    }

    [Fact]
    public void Decode_accepts_clean_no_pad_base64url_and_round_trips()
    {
        // The whole base64url alphabet plus a value that encodes to '-'/'_' must still decode.
        foreach (var original in new[] { "Hello, DIDComm v2.1!", "f", "fo", "foo", "ÿþýü" })
        {
            var bytes = Encoding.UTF8.GetBytes(original);
            var encoded = Base64Url.Encode(bytes);
            encoded.Should().NotContain("=").And.NotContain("+").And.NotContain("/");

            var decoded = Base64Url.Decode(encoded);
            Encoding.UTF8.GetString(decoded).Should().Be(original);
        }
    }

    [Fact]
    public void Decode_accepts_values_containing_dash_and_underscore()
    {
        // 0xFB 0xFF encodes to "-_8" in base64url — proves the '-' and '_' alphabet chars are allowed.
        var decoded = Base64Url.Decode("-_8");
        decoded.Should().Equal(0xFB, 0xFF);
    }
}
