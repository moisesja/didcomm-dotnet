using DidComm.Jose.Encryption;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Envelopes.Encryption;

public sealed class ApvComputerTests
{
    [Fact]
    public void Sorts_kids_before_joining_and_hashing()
    {
        var a = ApvComputer.Compute(new[] { "kid-b", "kid-a", "kid-c" });
        var b = ApvComputer.Compute(new[] { "kid-c", "kid-a", "kid-b" });
        var c = ApvComputer.Compute(new[] { "kid-a", "kid-b", "kid-c" });

        a.Should().Be(b).And.Be(c);
    }

    [Fact]
    public void Result_is_base64url_no_pad_32_byte_hash()
    {
        var v = ApvComputer.Compute(new[] { "did:example:alice#k" });
        // SHA-256 = 32 bytes → base64url no-pad = 43 chars.
        v.Length.Should().Be(43);
        v.Should().NotContain("=");
    }

    [Fact]
    public void Single_kid_known_answer()
    {
        // sha256("did:example:bob#k") base64url-no-pad.
        var bytes = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes("did:example:bob#k"));
        var expected = DidComm.Jose.Base64Url.Encode(bytes);
        ApvComputer.Compute(new[] { "did:example:bob#k" }).Should().Be(expected);
    }

    [Fact]
    public void Two_kids_known_answer()
    {
        var sorted = string.Join('.', new[] { "did:example:bob#1", "did:example:bob#2" }.OrderBy(x => x, StringComparer.Ordinal));
        var bytes = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(sorted));
        var expected = DidComm.Jose.Base64Url.Encode(bytes);
        ApvComputer.Compute(new[] { "did:example:bob#2", "did:example:bob#1" }).Should().Be(expected);
    }

    [Fact]
    public void Empty_input_throws()
    {
        Action act = () => ApvComputer.Compute(Array.Empty<string>());
        act.Should().Throw<ArgumentException>();
    }
}
