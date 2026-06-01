using System.Text.Json;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Json;
using DidComm.Messages;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Json;

/// <summary>
/// Strict-parsing hardening: duplicate JSON member names are rejected rather than silently resolved
/// last-wins (the .NET 10 default). A repeated <c>from</c>/<c>to</c>/<c>type</c> is a parser-
/// differential smuggling vector — the library would act on one value while a strict / first-wins
/// peer or audit log observes another — so DIDComm parsing fails closed.
/// </summary>
public sealed class StrictJsonParsingTests
{
    [Fact]
    public void DidCommJson_default_rejects_duplicate_member()
    {
        const string json = """
            {"id":"m1","type":"https://didcomm.org/x/1.0/y","from":"did:example:victim","from":"did:example:attacker"}
            """;

        Action act = () => JsonSerializer.Deserialize<Message>(json, DidCommJson.Default);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void EnvelopeDetector_rejects_duplicate_member()
    {
        Action act = () => EnvelopeDetector.Detect("""{"id":"a","id":"b"}""");

        act.Should().Throw<MalformedMessageException>();
    }

    [Fact]
    public void Epoch_field_out_of_int64_range_is_a_clean_json_error()
    {
        // An out-of-range epoch-seconds value must surface as JsonException (mapped to
        // MalformedMessageException upstream), not a raw OverflowException from the converter.
        const string json = """
            {"id":"m1","type":"https://didcomm.org/x/1.0/y","expires_time":"99999999999999999999999"}
            """;

        Action act = () => JsonSerializer.Deserialize<Message>(json, DidCommJson.Default);

        act.Should().Throw<JsonException>();
    }
}
