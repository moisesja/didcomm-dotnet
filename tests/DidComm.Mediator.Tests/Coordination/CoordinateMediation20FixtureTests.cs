using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Json;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Xunit;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>
/// FR-MED-10 (client side): byte-exact checks of the internal Coordinate Mediation 2.0 codec
/// against the seven messages of the didcomm.org coordinate-mediation 2.0 page
/// (https://didcomm.org/coordinate-mediation/2.0/, source
/// <c>site/content/protocols/coordinate-mediation/2.0/readme.md</c> of
/// decentralized-identity/didcomm.org, read 2026-11-14).
/// </summary>
/// <remarks>
/// The fixtures are those examples normalized the way the 3.0 set
/// (<see cref="CoordinateMediationFixtureTests"/>) is: valid JSON in the library's canonical member
/// order, compact, no trailing newline; <c>typ</c> added; trailing commas removed (<c>mediate-deny</c>);
/// <c>body: {}</c> added to <c>mediate-request</c> and <c>mediate-deny</c>, which the page shows
/// without a body; the page's <c>return_route: "all"</c> kept where it shows it (the three requests);
/// the response example's empty <c>action</c> / <c>result</c> placeholders and their <c>//</c>
/// comments replaced by <c>add</c> / <c>success</c>. The DIDs differ from the page because its own
/// are not usable: its routing DID <c>did:peer:z6Mkfriq…</c> lacks the did:peer numalgo digit, and
/// its <c>recipient_did</c> values are backtick-quoted, not JSON strings (the site renders them as
/// images). The fixtures carry the same keys in the form the 3.0 page uses,
/// <c>did:peer:0z6Mkfriq1MqLBoPWecGoDLjguo1sB9brj6wT3qZ5BxkKpuP6</c> (routing, here a single string
/// as 2.0 defines it) and <c>did:peer:0z6MkpTHR8VNsBxYAAWHut2Geadd9jSwuBV8xRoAnwWsdvktH</c>
/// (recipient).
/// </remarks>
public sealed class CoordinateMediation20FixtureTests
{
    private const string ExampleRecipient = "did:peer:0z6MkpTHR8VNsBxYAAWHut2Geadd9jSwuBV8xRoAnwWsdvktH";
    private const string ExampleRouting = "did:peer:0z6Mkfriq1MqLBoPWecGoDLjguo1sB9brj6wT3qZ5BxkKpuP6";
    private const string From = "did:example:from";
    private const string To = "did:example:to";

    private static readonly string[] Names =
    {
        "mediate-request", "mediate-grant", "mediate-deny", "keylist-update",
        "keylist-update-response", "keylist-query", "keylist",
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
        message.Type.Should().Be(CoordinateMediation20.ProtocolUri + "/" + name);
    }

    [Fact]
    public void Fixture_directory_holds_exactly_the_seven_messages_of_the_protocol()
    {
        var names = Directory.GetFiles(FixtureDirectory, "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal);
        names.Should().Equal(Names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Mediate_request_builder_emits_the_fixture_byte_exact()
    {
        var built = CoordinateMediation20.CreateMediateRequest(From, To, new FixedId());

        AsSent(built).Should().Be(ReadText("mediate-request"));
    }

    [Fact]
    public void Keylist_update_builder_emits_the_fixture_byte_exact()
    {
        var built = CoordinateMediation20.CreateKeylistUpdate(From, To,
            new[] { new RecipientUpdate(ExampleRecipient, RecipientUpdateAction.Add) }, new FixedId());

        AsSent(built).Should().Be(ReadText("keylist-update"));
    }

    [Fact]
    public void Keylist_query_builder_emits_the_fixture_byte_exact()
    {
        var built = CoordinateMediation20.CreateKeylistQuery(From, To, new RecipientQueryPaging(Limit: 30, Offset: 0), new FixedId());

        AsSent(built).Should().Be(ReadText("keylist-query"));
    }

    [Fact]
    public void Mediate_grant_fixture_parses_to_its_single_routing_did()
    {
        var message = Read("mediate-grant");

        CoordinateMediation20.TryParseGrant(message, out var routingDids).Should().BeTrue();
        routingDids.Should().Equal(ExampleRouting);
        BodyOf(message).Should().Be($$"""{"routing_did":"{{ExampleRouting}}"}""", "2.0 names one routing DID, as a string");
    }

    [Fact]
    public void Mediate_deny_fixture_parses_and_so_does_the_pages_own_shape_without_a_body()
    {
        CoordinateMediation20.TryParseDeny(Read("mediate-deny")).Should().BeTrue();

        var pageShape = JsonSerializer.Deserialize<Message>(
            $$"""{"id":"123456780","type":"{{CoordinateMediation20.MediateDenyType}}"}""", DidCommJson.Default)!;
        pageShape.Body.Should().BeNull();
        CoordinateMediation20.TryParseDeny(pageShape).Should().BeTrue();
    }

    [Fact]
    public void Keylist_update_response_fixture_parses_and_the_shared_writer_emits_its_body()
    {
        var message = Read("keylist-update-response");

        CoordinateMediation20.TryParseKeylistUpdateResponse(message, out var updated).Should().BeTrue();
        var expected = new RecipientUpdateResult(ExampleRecipient, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success);
        updated.Should().Equal(expected);
        // What #99 reuses to build the answer.
        MediationBodies.WriteUpdated(updated!).ToJsonString().Should().Be(BodyOf(message));
    }

    [Fact]
    public void Keylist_fixture_parses_and_the_shared_writer_emits_its_body()
    {
        var message = Read("keylist");

        CoordinateMediation20.TryParseKeylist(message, out var keys, out var pagination).Should().BeTrue();
        keys.Should().Equal(ExampleRecipient);
        pagination.Should().Be(new RecipientPagination(Count: 30, Offset: 30, Remaining: 100));
        MediationBodies.WriteRecipients(CoordinateMediation20.KeysList, keys!, pagination).ToJsonString().Should().Be(BodyOf(message));
    }

    [Fact]
    public void Keylist_update_and_keylist_query_fixtures_read_through_the_shared_readers()
    {
        // The request bodies #99's mediator-direction parsers will read with the same readers.
        MediationBodies.ReadUpdates(MediationBodies.ToElement(Read("keylist-update").Body), "keylist-update")
            .Should().Equal(new RecipientUpdate(ExampleRecipient, RecipientUpdateAction.Add));
        MediationBodies.ReadPaginate(MediationBodies.ToElement(Read("keylist-query").Body), "keylist-query")
            .Should().Be(new RecipientQueryPaging(Limit: 30, Offset: 0));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void At_most_one_answer_parser_claims_each_fixture(string name)
    {
        var message = Read(name);
        var claims = new[]
        {
            CoordinateMediation20.TryParseGrant(message, out _),
            CoordinateMediation20.TryParseDeny(message),
            CoordinateMediation20.TryParseKeylistUpdateResponse(message, out _),
            CoordinateMediation20.TryParseKeylist(message, out _, out _),
        };
        var isAnswer = name is "mediate-grant" or "mediate-deny" or "keylist-update-response" or "keylist";
        claims.Count(c => c).Should().Be(isAnswer ? 1 : 0, "`keylist` must not be mistaken for `keylist-update-response`, nor a request for an answer");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void No_3_0_parser_claims_a_2_0_fixture(string name)
    {
        var message = Read(name);
        var claims = new[]
        {
            CoordinateMediation.TryParseMediateRequest(message),
            CoordinateMediation.TryParseGrant(message, out _),
            CoordinateMediation.TryParseDeny(message),
            CoordinateMediation.TryParseRecipientUpdateResponse(message, out _),
            CoordinateMediation.TryParseRecipient(message, out _, out _),
        };
        claims.Should().AllBeEquivalentTo(false, "the versions differ in their major, which message types must agree on");
    }

    // What the exchange puts on the wire for a request (design D-5): the builder's message with the
    // `return_route: all` header the send adds; `from` and `to` are left out to compare with the
    // page's examples, which carry neither.
    private static string AsSent(Message built)
    {
        built.From.Should().Be(From);
        built.To.Should().Equal(To);
        built.CreatedTime.Should().BeNull("the builder never stamps; the send does (FR-RR-09)");
        built.From = null;
        built.To = null;
        ReturnRouteHeaderWriter.Write(built, ReturnRouteMode.All, thread: null);
        return JsonSerializer.Serialize(built, DidCommJson.Default);
    }

    private sealed class FixedId : IMessageIdGenerator
    {
        public string NewId() => "123456780";
    }

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "fixtures", "coordinate-mediation-2.0");

    private static byte[] ReadBytes(string name) => File.ReadAllBytes(Path.Combine(FixtureDirectory, name + ".json"));

    private static string ReadText(string name) => File.ReadAllText(Path.Combine(FixtureDirectory, name + ".json"));

    private static Message Read(string name) => JsonSerializer.Deserialize<Message>(ReadBytes(name), DidCommJson.Default)!;

    private static string BodyOf(Message message)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(message, DidCommJson.Default))!;
        return node["body"]!.ToJsonString();
    }
}
