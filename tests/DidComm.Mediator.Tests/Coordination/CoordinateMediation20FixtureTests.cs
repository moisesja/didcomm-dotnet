using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Json;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Xunit;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>
/// FR-MED-10, both directions: byte-exact checks of the internal Coordinate Mediation 2.0 codec
/// (the three requests a recipient builds and the four answers a mediator builds, each parsed by
/// the side that receives it) against the seven messages of the didcomm.org coordinate-mediation 2.0 page
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
    private const string Thread = "thread-of-the-request";

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
        // The writer the 2.0 answer builder uses (byte-exact below, Keylist_update_response_builder_…).
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

    // ---- the mediator direction (#99): the four answers built, the three requests read -------

    [Fact]
    public void Mediate_grant_builder_emits_the_fixture_byte_exact()
    {
        var built = CoordinateMediation20.CreateGrant(From, To, ExampleRouting, Thread, new FixedId());

        AsAnswered(built).Should().Be(ReadText("mediate-grant"), "a 2.0 grant names one routing DID, as a JSON string");
    }

    [Fact]
    public void Mediate_deny_builder_emits_the_fixture_byte_exact()
    {
        var built = CoordinateMediation20.CreateDeny(From, To, Thread, new FixedId());

        AsAnswered(built).Should().Be(ReadText("mediate-deny"));
    }

    [Fact]
    public void Keylist_update_response_builder_emits_the_fixture_byte_exact()
    {
        var built = CoordinateMediation20.CreateKeylistUpdateResponse(From, To,
            new[] { new RecipientUpdateResult(ExampleRecipient, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success) }, Thread, new FixedId());

        AsAnswered(built).Should().Be(ReadText("keylist-update-response"));
    }

    [Fact]
    public void Keylist_builder_emits_the_fixture_byte_exact()
    {
        var built = CoordinateMediation20.CreateKeylist(From, To, new[] { ExampleRecipient },
            new RecipientPagination(Count: 30, Offset: 30, Remaining: 100), Thread, new FixedId());

        AsAnswered(built).Should().Be(ReadText("keylist"), "2.0 lists the DIDs as `keys`, never the 3.0 `dids`");
    }

    [Fact]
    public void Mediate_request_fixture_is_read_by_the_request_parser_and_so_is_the_pages_own_shape_without_a_body()
    {
        CoordinateMediation20.TryParseMediateRequest(Read("mediate-request")).Should().BeTrue();

        var pageShape = JsonSerializer.Deserialize<Message>(
            $$"""{"id":"123456780","type":"{{CoordinateMediation20.MediateRequestType}}","return_route":"all"}""", DidCommJson.Default)!;
        pageShape.Body.Should().BeNull();
        CoordinateMediation20.TryParseMediateRequest(pageShape).Should().BeTrue();
    }

    [Fact]
    public void Keylist_update_fixture_is_read_by_the_request_parser_with_a_bound_it_just_meets()
    {
        CoordinateMediation20.TryParseKeylistUpdate(Read("keylist-update"), maxUpdates: 1, out var updates).Should().BeTrue();

        updates.Should().Equal(new RecipientUpdate(ExampleRecipient, RecipientUpdateAction.Add));
    }

    [Fact]
    public void Keylist_query_fixture_is_read_by_the_request_parser()
    {
        CoordinateMediation20.TryParseKeylistQuery(Read("keylist-query"), out var paginate).Should().BeTrue();

        paginate.Should().Be(new RecipientQueryPaging(Limit: 30, Offset: 0));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Each_fixture_is_claimed_by_exactly_one_2_0_parser_its_own(string name)
    {
        var claims = ClaimsOf2_0Parsers(Read(name));

        claims.Where(c => c.Value).Select(c => c.Key).Should().Equal(new[] { name },
            "`keylist` must not be mistaken for `keylist-update` or `keylist-update-response`, nor a request for an answer");
    }

    public static TheoryData<string> ThreeZeroFixtures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in new[] { "mediate-request", "mediate-grant", "mediate-deny", "recipient-update", "recipient-update-response", "recipient-query", "recipient" })
                data.Add(name);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ThreeZeroFixtures))]
    public void No_2_0_parser_claims_a_3_0_fixture(string name)
    {
        var message = JsonSerializer.Deserialize<Message>(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "coordinate-mediation-3.0", name + ".json")), DidCommJson.Default)!;
        message.Type.Should().Be(CoordinateMediation.ProtocolUri + "/" + name, "the fixture is the 3.0 message");

        ClaimsOf2_0Parsers(message).Where(c => c.Value).Select(c => c.Key).Should().BeEmpty(
            "a 3.0 request reaches the 3.0 handler only: the versions differ in their major, which message types must agree on");
    }

    // Every 2.0 parser's answer to "is this yours?": true when it returned true, or when it
    // recognised the type and refused the body (which is still a claim).
    private static Dictionary<string, bool> ClaimsOf2_0Parsers(Message message) => new()
    {
        ["mediate-request"] = Claims(() => CoordinateMediation20.TryParseMediateRequest(message)),
        ["mediate-grant"] = Claims(() => CoordinateMediation20.TryParseGrant(message, out _)),
        ["mediate-deny"] = Claims(() => CoordinateMediation20.TryParseDeny(message)),
        ["keylist-update"] = Claims(() => CoordinateMediation20.TryParseKeylistUpdate(message, int.MaxValue, out _)),
        ["keylist-update-response"] = Claims(() => CoordinateMediation20.TryParseKeylistUpdateResponse(message, out _)),
        ["keylist-query"] = Claims(() => CoordinateMediation20.TryParseKeylistQuery(message, out _)),
        ["keylist"] = Claims(() => CoordinateMediation20.TryParseKeylist(message, out _, out _)),
    };

    private static bool Claims(Func<bool> parse)
    {
        try
        {
            return parse();
        }
        catch (MalformedMessageException)
        {
            return true;
        }
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

    // What the mediator puts on the wire for an answer: the builder's message with the request's
    // thread id and no return_route; `from`, `to` and `thid` are checked, then left out to compare
    // with the page's examples, which carry none of them.
    private static string AsAnswered(Message built)
    {
        built.From.Should().Be(From);
        built.To.Should().Equal(To);
        built.Thid.Should().Be(Thread, "an answer is threaded on the request (FR-MED-04, DD-11)");
        built.CreatedTime.Should().BeNull("the builder never stamps; the send does (FR-RR-09)");
        built.AdditionalHeaders.Should().BeNull("an answer asks for no return route");
        built.From = null;
        built.To = null;
        built.Thid = null;
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
