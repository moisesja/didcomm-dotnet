using DidComm.Exceptions;
using DidComm.Jose;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Envelopes.Encryption;

public sealed class EnvelopeDetectorTests
{
    [Fact]
    public void Detects_plaintext_jwm()
    {
        EnvelopeDetector.Detect("{\"id\":\"m1\",\"type\":\"https://didcomm.org/empty/1.0/empty\"}")
            .Should().Be(EnvelopeKind.Plaintext);
    }

    [Fact]
    public void Detects_flattened_jws_by_payload_plus_signature()
    {
        EnvelopeDetector.Detect("{\"payload\":\"xx\",\"protected\":\"yy\",\"signature\":\"zz\"}")
            .Should().Be(EnvelopeKind.Signed);
    }

    [Fact]
    public void Detects_general_jws_by_signatures_array()
    {
        EnvelopeDetector.Detect("{\"payload\":\"xx\",\"signatures\":[{\"protected\":\"y\",\"signature\":\"z\"}]}")
            .Should().Be(EnvelopeKind.Signed);
    }

    [Fact]
    public void Detects_jwe_by_ciphertext_member()
    {
        EnvelopeDetector.Detect("{\"protected\":\"p\",\"recipients\":[],\"iv\":\"i\",\"ciphertext\":\"c\",\"tag\":\"t\"}")
            .Should().Be(EnvelopeKind.Encrypted);
    }

    [Fact]
    public void Non_json_input_throws()
    {
        Action act = () => EnvelopeDetector.Detect("not-json");
        act.Should().Throw<MalformedMessageException>();
    }

    [Fact]
    public void Json_array_at_root_throws()
    {
        Action act = () => EnvelopeDetector.Detect("[1, 2, 3]");
        act.Should().Throw<MalformedMessageException>();
    }

    [Fact]
    public void Empty_string_throws()
    {
        Action act = () => EnvelopeDetector.Detect(string.Empty);
        act.Should().Throw<ArgumentException>();
    }
}
