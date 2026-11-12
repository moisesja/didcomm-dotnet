using System.Text.Json;
using AwesomeAssertions;
using DidComm.Json;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using Xunit;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>The <c>Create…</c> helpers of <see cref="CoordinateMediation"/> (FR-MED-02/04, FR-API-09 on the way out).</summary>
public sealed class CoordinateMediationBuilderTests
{
    private const string Alice = "did:example:alice";
    private const string Mediator = "did:example:mediator";
    private static readonly string OverLong = "did:example:SENTINEL" + new string('a', 2049);
    private static readonly string AtLimit = "did:example:" + new string('a', 2048 - "did:example:".Length);

    [Fact]
    public void Constants_are_the_didcomm_org_identifiers()
    {
        CoordinateMediation.ProtocolUri.Should().Be("https://didcomm.org/coordinate-mediation/3.0");
        CoordinateMediation.MediateRequestType.Should().Be("https://didcomm.org/coordinate-mediation/3.0/mediate-request");
        CoordinateMediation.MediateGrantType.Should().Be("https://didcomm.org/coordinate-mediation/3.0/mediate-grant");
        CoordinateMediation.MediateDenyType.Should().Be("https://didcomm.org/coordinate-mediation/3.0/mediate-deny");
        CoordinateMediation.RecipientUpdateType.Should().Be("https://didcomm.org/coordinate-mediation/3.0/recipient-update");
        CoordinateMediation.RecipientUpdateResponseType.Should().Be("https://didcomm.org/coordinate-mediation/3.0/recipient-update-response");
        CoordinateMediation.RecipientQueryType.Should().Be("https://didcomm.org/coordinate-mediation/3.0/recipient-query");
        CoordinateMediation.RecipientType.Should().Be("https://didcomm.org/coordinate-mediation/3.0/recipient");
        CoordinateMediation.MediatorRole.Should().Be("mediator");
        CoordinateMediation.RecipientRole.Should().Be("recipient");
    }

    [Fact]
    public void Requests_carry_from_to_no_thid_and_the_expected_bodies()
    {
        var request = CoordinateMediation.CreateMediateRequest(Alice, Mediator);
        request.Type.Should().Be(CoordinateMediation.MediateRequestType);
        request.From.Should().Be(Alice);
        request.To.Should().Equal(Mediator);
        request.Thid.Should().BeNull();
        Wire(request).Should().Contain("\"body\":{}", "mediate-request emits an empty body object, never omits it");

        var update = CoordinateMediation.CreateRecipientUpdate(Alice, Mediator, new[]
        {
            new RecipientUpdate("did:example:r1", RecipientUpdateAction.Add),
            new RecipientUpdate("did:example:r2", RecipientUpdateAction.Remove),
        });
        update.Thid.Should().BeNull();
        Wire(update).Should().Contain("\"body\":{\"updates\":[{\"recipient_did\":\"did:example:r1\",\"action\":\"add\"},{\"recipient_did\":\"did:example:r2\",\"action\":\"remove\"}]}");

        var bare = CoordinateMediation.CreateRecipientQuery(Alice, Mediator);
        bare.Thid.Should().BeNull();
        Wire(bare).Should().Contain("\"body\":{}", "a query without paginate has an empty body");

        var paged = CoordinateMediation.CreateRecipientQuery(Alice, Mediator, new RecipientQueryPaging(Limit: 5, Offset: 10));
        Wire(paged).Should().Contain("\"body\":{\"paginate\":{\"limit\":5,\"offset\":10}}");
    }

    [Fact]
    public void Responses_carry_the_thid_and_the_expected_bodies()
    {
        var grant = CoordinateMediation.CreateGrant(Mediator, Alice, new[] { "did:example:r1" }, "thread-1");
        grant.Thid.Should().Be("thread-1");
        grant.From.Should().Be(Mediator);
        grant.To.Should().Equal(Alice);
        Wire(grant).Should().Contain("\"body\":{\"routing_did\":[\"did:example:r1\"]}", "routing_did is always an array, also for one DID");

        var two = CoordinateMediation.CreateGrant(Mediator, Alice, new[] { "did:example:r1", "did:example:r2" }, "t");
        Wire(two).Should().Contain("\"routing_did\":[\"did:example:r1\",\"did:example:r2\"]");

        var deny = CoordinateMediation.CreateDeny(Mediator, Alice, "thread-2");
        deny.Thid.Should().Be("thread-2");
        Wire(deny).Should().Contain("\"body\":{}");

        var response = CoordinateMediation.CreateRecipientUpdateResponse(Mediator, Alice, new[]
        {
            new RecipientUpdateResult("did:example:a", RecipientUpdateAction.Add, RecipientUpdateOutcome.Success),
            new RecipientUpdateResult("did:example:b", RecipientUpdateAction.Add, RecipientUpdateOutcome.NoChange),
            new RecipientUpdateResult("did:example:c", RecipientUpdateAction.Remove, RecipientUpdateOutcome.ClientError),
            new RecipientUpdateResult("did:example:d", RecipientUpdateAction.Remove, RecipientUpdateOutcome.ServerError),
        }, "thread-3");
        response.Thid.Should().Be("thread-3");
        Wire(response).Should().Contain(
            "\"updated\":[{\"recipient_did\":\"did:example:a\",\"action\":\"add\",\"result\":\"success\"}," +
            "{\"recipient_did\":\"did:example:b\",\"action\":\"add\",\"result\":\"no_change\"}," +
            "{\"recipient_did\":\"did:example:c\",\"action\":\"remove\",\"result\":\"client_error\"}," +
            "{\"recipient_did\":\"did:example:d\",\"action\":\"remove\",\"result\":\"server_error\"}]");

        var page = CoordinateMediation.CreateRecipient(Mediator, Alice, new[] { "did:example:a" }, new RecipientPagination(1, 2, 3), "thread-4");
        page.Thid.Should().Be("thread-4");
        Wire(page).Should().Contain("\"body\":{\"dids\":[{\"recipient_did\":\"did:example:a\"}],\"pagination\":{\"count\":1,\"offset\":2,\"remaining\":3}}");

        var noPagination = CoordinateMediation.CreateRecipient(Mediator, Alice, Array.Empty<string>(), pagination: null, "thread-5");
        Wire(noPagination).Should().Contain("\"body\":{\"dids\":[]}");
    }

    [Fact]
    public void Everything_a_builder_emits_parses_back_to_the_same_values()
    {
        var updates = new[] { new RecipientUpdate("did:example:r1", RecipientUpdateAction.Add), new RecipientUpdate("did:example:r2", RecipientUpdateAction.Remove) };
        CoordinateMediation.TryParseRecipientUpdate(RoundTrip(CoordinateMediation.CreateRecipientUpdate(Alice, Mediator, updates)), out var parsedUpdates).Should().BeTrue();
        parsedUpdates.Should().Equal(updates);

        var results = Enum.GetValues<RecipientUpdateOutcome>().Select(o => new RecipientUpdateResult("did:example:" + o, RecipientUpdateAction.Add, o)).ToArray();
        CoordinateMediation.TryParseRecipientUpdateResponse(RoundTrip(CoordinateMediation.CreateRecipientUpdateResponse(Mediator, Alice, results, "t")), out var parsedResults).Should().BeTrue();
        parsedResults.Should().Equal(results);

        CoordinateMediation.TryParseRecipientQuery(RoundTrip(CoordinateMediation.CreateRecipientQuery(Alice, Mediator, new RecipientQueryPaging(-3, 7))), out var paginate).Should().BeTrue();
        paginate.Should().Be(new RecipientQueryPaging(-3, 7), "a negative limit is legal: it asks for an empty page");

        CoordinateMediation.TryParseRecipient(RoundTrip(CoordinateMediation.CreateRecipient(Mediator, Alice, new[] { "did:example:a", "did:example:b" }, new RecipientPagination(2, 0, 5), "t")), out var dids, out var pagination).Should().BeTrue();
        dids.Should().Equal("did:example:a", "did:example:b");
        pagination.Should().Be(new RecipientPagination(2, 0, 5));

        CoordinateMediation.TryParseGrant(RoundTrip(CoordinateMediation.CreateGrant(Mediator, Alice, new[] { "did:example:r1", "did:example:r2" }, "t")), out var routing).Should().BeTrue();
        routing.Should().Equal("did:example:r1", "did:example:r2");
    }

    [Fact]
    public void A_did_at_the_limit_is_accepted_everywhere()
    {
        AtLimit.Length.Should().Be(2048);
        CoordinateMediation.CreateMediateRequest(AtLimit, AtLimit).From.Should().Be(AtLimit);
        CoordinateMediation.CreateGrant(Mediator, Alice, new[] { AtLimit }, "t").Should().NotBeNull();
        CoordinateMediation.CreateRecipientUpdate(Alice, Mediator, new[] { new RecipientUpdate(AtLimit, RecipientUpdateAction.Add) }).Should().NotBeNull();
        CoordinateMediation.CreateRecipient(Mediator, Alice, new[] { AtLimit }, null, "t").Should().NotBeNull();
    }

    public static TheoryData<string, Func<object>> OverLongCases => new()
    {
        { "request from", () => CoordinateMediation.CreateMediateRequest(OverLong, Mediator) },
        { "request mediator", () => CoordinateMediation.CreateMediateRequest(Alice, OverLong) },
        { "grant from", () => CoordinateMediation.CreateGrant(OverLong, Alice, new[] { "did:example:r" }, "t") },
        { "grant to", () => CoordinateMediation.CreateGrant(Mediator, OverLong, new[] { "did:example:r" }, "t") },
        { "grant routing", () => CoordinateMediation.CreateGrant(Mediator, Alice, new[] { "did:example:r", OverLong }, "t") },
        { "deny to", () => CoordinateMediation.CreateDeny(Mediator, OverLong, "t") },
        { "update entry", () => CoordinateMediation.CreateRecipientUpdate(Alice, Mediator, new[] { new RecipientUpdate(OverLong, RecipientUpdateAction.Add) }) },
        { "response entry", () => CoordinateMediation.CreateRecipientUpdateResponse(Mediator, Alice, new[] { new RecipientUpdateResult(OverLong, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success) }, "t") },
        { "query from", () => CoordinateMediation.CreateRecipientQuery(OverLong, Mediator) },
        { "recipient did", () => CoordinateMediation.CreateRecipient(Mediator, Alice, new[] { OverLong }, null, "t") },
    };

    [Theory]
    [MemberData(nameof(OverLongCases))]
    public void An_over_long_did_is_refused_without_being_quoted(string what, Func<object> build)
    {
        var ex = build.Should().Throw<ArgumentException>(what).Which;
        ex.Message.Should().NotContain("SENTINEL").And.Contain("2048");
    }

    public static TheoryData<string, Func<object>> MissingCases => new()
    {
        { "request from empty", () => CoordinateMediation.CreateMediateRequest("", Mediator) },
        { "request from null", () => CoordinateMediation.CreateMediateRequest(null!, Mediator) },
        { "request mediator empty", () => CoordinateMediation.CreateMediateRequest(Alice, "") },
        { "grant no routing DIDs", () => CoordinateMediation.CreateGrant(Mediator, Alice, Array.Empty<string>(), "t") },
        { "grant null routing DIDs", () => CoordinateMediation.CreateGrant(Mediator, Alice, null!, "t") },
        { "grant empty routing DID", () => CoordinateMediation.CreateGrant(Mediator, Alice, new[] { "" }, "t") },
        { "grant null routing DID", () => CoordinateMediation.CreateGrant(Mediator, Alice, new string[] { null! }, "t") },
        { "grant empty thid", () => CoordinateMediation.CreateGrant(Mediator, Alice, new[] { "did:example:r" }, "") },
        { "grant null thid", () => CoordinateMediation.CreateGrant(Mediator, Alice, new[] { "did:example:r" }, null!) },
        { "deny empty thid", () => CoordinateMediation.CreateDeny(Mediator, Alice, "") },
        { "deny null thid", () => CoordinateMediation.CreateDeny(Mediator, Alice, null!) },
        { "update null list", () => CoordinateMediation.CreateRecipientUpdate(Alice, Mediator, null!) },
        { "update null entry", () => CoordinateMediation.CreateRecipientUpdate(Alice, Mediator, new RecipientUpdate[] { null! }) },
        { "update empty did", () => CoordinateMediation.CreateRecipientUpdate(Alice, Mediator, new[] { new RecipientUpdate("", RecipientUpdateAction.Add) }) },
        { "update undefined action", () => CoordinateMediation.CreateRecipientUpdate(Alice, Mediator, new[] { new RecipientUpdate("did:example:r", (RecipientUpdateAction)7) }) },
        { "response null list", () => CoordinateMediation.CreateRecipientUpdateResponse(Mediator, Alice, null!, "t") },
        { "response undefined action", () => CoordinateMediation.CreateRecipientUpdateResponse(Mediator, Alice, new[] { new RecipientUpdateResult("did:example:r", (RecipientUpdateAction)7, RecipientUpdateOutcome.Success) }, "t") },
        { "response undefined result", () => CoordinateMediation.CreateRecipientUpdateResponse(Mediator, Alice, new[] { new RecipientUpdateResult("did:example:r", RecipientUpdateAction.Add, (RecipientUpdateOutcome)9) }, "t") },
        { "response null thid", () => CoordinateMediation.CreateRecipientUpdateResponse(Mediator, Alice, Array.Empty<RecipientUpdateResult>(), null!) },
        { "query negative offset", () => CoordinateMediation.CreateRecipientQuery(Alice, Mediator, new RecipientQueryPaging(1, -1)) },
        { "recipient null list", () => CoordinateMediation.CreateRecipient(Mediator, Alice, null!, null, "t") },
        { "recipient empty did", () => CoordinateMediation.CreateRecipient(Mediator, Alice, new[] { "" }, null, "t") },
        { "recipient null thid", () => CoordinateMediation.CreateRecipient(Mediator, Alice, Array.Empty<string>(), null, null!) },
    };

    [Theory]
    [MemberData(nameof(MissingCases))]
    public void Missing_or_undefined_arguments_are_argument_errors(string what, Func<object> build)
        => build.Should().Throw<ArgumentException>(what); // ArgumentNull / ArgumentOutOfRange derive from it

    [Fact]
    public void Empty_update_and_result_lists_are_allowed()
    {
        Wire(CoordinateMediation.CreateRecipientUpdate(Alice, Mediator, Array.Empty<RecipientUpdate>())).Should().Contain("\"updates\":[]");
        Wire(CoordinateMediation.CreateRecipientUpdateResponse(Mediator, Alice, Array.Empty<RecipientUpdateResult>(), "t")).Should().Contain("\"updated\":[]");
    }

    [Fact]
    public void ResponseThreadId_is_the_request_thid_else_its_id()
    {
        var plain = new MessageBuilder().WithId("request-id").WithType(CoordinateMediation.MediateRequestType).Build();
        CoordinateMediation.ResponseThreadId(plain).Should().Be("request-id");

        var threaded = new MessageBuilder().WithId("request-id").WithThid("existing-thread").WithType(CoordinateMediation.MediateRequestType).Build();
        CoordinateMediation.ResponseThreadId(threaded).Should().Be("existing-thread");

        plain.Thid = string.Empty;
        CoordinateMediation.ResponseThreadId(plain).Should().Be("request-id", "an empty thid is no thid");

        var act = () => CoordinateMediation.ResponseThreadId(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void A_custom_id_generator_is_honoured_by_every_builder()
    {
        var ids = new FixedIds("fixed-id");
        CoordinateMediation.CreateMediateRequest(Alice, Mediator, ids).Id.Should().Be("fixed-id");
        CoordinateMediation.CreateGrant(Mediator, Alice, new[] { "did:example:r" }, "t", ids).Id.Should().Be("fixed-id");
        CoordinateMediation.CreateDeny(Mediator, Alice, "t", ids).Id.Should().Be("fixed-id");
        CoordinateMediation.CreateRecipientUpdate(Alice, Mediator, Array.Empty<RecipientUpdate>(), ids).Id.Should().Be("fixed-id");
        CoordinateMediation.CreateRecipientUpdateResponse(Mediator, Alice, Array.Empty<RecipientUpdateResult>(), "t", ids).Id.Should().Be("fixed-id");
        CoordinateMediation.CreateRecipientQuery(Alice, Mediator, null, ids).Id.Should().Be("fixed-id");
        CoordinateMediation.CreateRecipient(Mediator, Alice, Array.Empty<string>(), null, "t", ids).Id.Should().Be("fixed-id");

        // The default generates distinct ids.
        CoordinateMediation.CreateMediateRequest(Alice, Mediator).Id.Should().NotBe(CoordinateMediation.CreateMediateRequest(Alice, Mediator).Id);
    }

    private static string Wire(Message message) => JsonSerializer.Serialize(message, DidCommJson.Default);

    private static Message RoundTrip(Message message) => JsonSerializer.Deserialize<Message>(Wire(message), DidCommJson.Default)!;

    private sealed class FixedIds(string id) : IMessageIdGenerator
    {
        public string NewId() => id;
    }
}
