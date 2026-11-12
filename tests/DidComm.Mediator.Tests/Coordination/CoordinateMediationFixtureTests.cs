using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Json;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using Xunit;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>
/// FR-MED-02 acceptance: byte-exact round trips against the didcomm.org coordinate-mediation 3.0
/// page examples. The fixtures are those examples normalized to valid JSON in the library's
/// canonical member order: trailing commas removed, <c>typ</c> added, <c>body: {}</c> added to
/// <c>mediate-request</c> / <c>mediate-deny</c> (the page omits it), and the response example's
/// placeholder strings replaced by <c>add</c> / <c>success</c>. Compact, no trailing newline.
/// </summary>
public sealed class CoordinateMediationFixtureTests
{
    private const string ExampleRecipient = "did:peer:0z6MkpTHR8VNsBxYAAWHut2Geadd9jSwuBV8xRoAnwWsdvktH";
    private const string ExampleRouting = "did:peer:0z6Mkfriq1MqLBoPWecGoDLjguo1sB9brj6wT3qZ5BxkKpuP6";
    private const string From = "did:example:from";
    private const string To = "did:example:to";

    private static readonly string[] Names =
    {
        "mediate-request", "mediate-grant", "mediate-deny", "recipient-update",
        "recipient-update-response", "recipient-query", "recipient",
    };

    public static TheoryData<string> Fixtures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Names)
                data.Add(name);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_round_trips_byte_exact(string name)
    {
        var bytes = ReadBytes(name);

        var message = JsonSerializer.Deserialize<Message>(bytes, DidCommJson.Default)!;
        var again = JsonSerializer.SerializeToUtf8Bytes(message, DidCommJson.Default);

        again.Should().Equal(bytes, "deserialize → serialize must reproduce the fixture byte for byte");
        message.Id.Should().Be("123456780");
        message.Type.Should().Be(CoordinateMediation.ProtocolUri + "/" + name);
    }

    [Fact]
    public void Fixture_directory_holds_exactly_the_seven_messages_of_the_protocol()
    {
        var names = Directory.GetFiles(FixtureDirectory, "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal);
        names.Should().Equal(Names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Mediate_request_fixture_parses_and_the_builder_emits_the_same_body()
    {
        var message = Read("mediate-request");
        CoordinateMediation.TryParseMediateRequest(message).Should().BeTrue();
        BodyOf(CoordinateMediation.CreateMediateRequest(From, To)).Should().Be(BodyOf(message)).And.Be("{}");
    }

    [Fact]
    public void Mediate_deny_fixture_parses_and_the_builder_emits_the_same_body()
    {
        var message = Read("mediate-deny");
        CoordinateMediation.TryParseDeny(message).Should().BeTrue();
        BodyOf(CoordinateMediation.CreateDeny(From, To, "t")).Should().Be(BodyOf(message)).And.Be("{}");
    }

    [Fact]
    public void Mediate_grant_fixture_parses_and_the_builder_emits_the_same_body()
    {
        var message = Read("mediate-grant");
        CoordinateMediation.TryParseGrant(message, out var routingDids).Should().BeTrue();
        routingDids.Should().Equal(ExampleRouting);
        BodyOf(CoordinateMediation.CreateGrant(From, To, routingDids!, "t")).Should().Be(BodyOf(message));
    }

    [Fact]
    public void Recipient_update_fixture_parses_and_the_builder_emits_the_same_body()
    {
        var message = Read("recipient-update");
        CoordinateMediation.TryParseRecipientUpdate(message, out var updates).Should().BeTrue();
        updates.Should().Equal(new RecipientUpdate(ExampleRecipient, RecipientUpdateAction.Add));
        BodyOf(CoordinateMediation.CreateRecipientUpdate(From, To, updates!)).Should().Be(BodyOf(message));
        // The page's example carries return_route: all; the round trip keeps it.
        JsonSerializer.Serialize(message, DidCommJson.Default).Should().Contain("\"return_route\":\"all\"");
    }

    [Fact]
    public void Recipient_update_response_fixture_parses_and_the_builder_emits_the_same_body()
    {
        var message = Read("recipient-update-response");
        CoordinateMediation.TryParseRecipientUpdateResponse(message, out var updated).Should().BeTrue();
        updated.Should().Equal(new RecipientUpdateResult(ExampleRecipient, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success));
        BodyOf(CoordinateMediation.CreateRecipientUpdateResponse(From, To, updated!, "t")).Should().Be(BodyOf(message));
    }

    [Fact]
    public void Recipient_query_fixture_parses_and_the_builder_emits_the_same_body()
    {
        var message = Read("recipient-query");
        CoordinateMediation.TryParseRecipientQuery(message, out var paginate).Should().BeTrue();
        paginate.Should().Be(new RecipientQueryPaging(Limit: 30, Offset: 0));
        BodyOf(CoordinateMediation.CreateRecipientQuery(From, To, paginate)).Should().Be(BodyOf(message));
    }

    [Fact]
    public void Recipient_fixture_parses_and_the_builder_emits_the_same_body()
    {
        var message = Read("recipient");
        CoordinateMediation.TryParseRecipient(message, out var dids, out var pagination).Should().BeTrue();
        dids.Should().Equal(ExampleRecipient);
        pagination.Should().Be(new RecipientPagination(Count: 30, Offset: 30, Remaining: 100));
        BodyOf(CoordinateMediation.CreateRecipient(From, To, dids!, pagination, "t")).Should().Be(BodyOf(message));
    }

    [Theory]
    [InlineData("mediate-request")]
    [InlineData("mediate-deny")]
    public void The_pages_own_shape_without_a_body_is_accepted(string name)
    {
        // The didcomm.org examples for these two carry no body at all.
        var json = $$"""{"id":"123456780","type":"{{CoordinateMediation.ProtocolUri}}/{{name}}"}""";
        var message = JsonSerializer.Deserialize<Message>(json, DidCommJson.Default)!;
        message.Body.Should().BeNull();

        var parsed = name == "mediate-request"
            ? CoordinateMediation.TryParseMediateRequest(message)
            : CoordinateMediation.TryParseDeny(message);
        parsed.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Each_fixture_is_claimed_by_exactly_one_parser(string name)
    {
        var message = Read(name);
        var claims = new[]
        {
            CoordinateMediation.TryParseMediateRequest(message),
            CoordinateMediation.TryParseGrant(message, out _),
            CoordinateMediation.TryParseDeny(message),
            CoordinateMediation.TryParseRecipientUpdate(message, out _),
            CoordinateMediation.TryParseRecipientUpdateResponse(message, out _),
            CoordinateMediation.TryParseRecipientQuery(message, out _),
            CoordinateMediation.TryParseRecipient(message, out _, out _),
        };
        claims.Count(c => c).Should().Be(1, "`recipient` must not be mistaken for `recipient-update`, nor the reverse");
    }

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "fixtures", "coordinate-mediation-3.0");

    private static byte[] ReadBytes(string name) => File.ReadAllBytes(Path.Combine(FixtureDirectory, name + ".json"));

    private static Message Read(string name) => JsonSerializer.Deserialize<Message>(ReadBytes(name), DidCommJson.Default)!;

    private static string BodyOf(Message message)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(message, DidCommJson.Default))!;
        return node["body"]!.ToJsonString();
    }
}
