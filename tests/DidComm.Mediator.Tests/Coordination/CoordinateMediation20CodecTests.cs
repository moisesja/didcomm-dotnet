using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Json;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using Xunit;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>
/// The internal Coordinate Mediation 2.0 codec (#96 design D-12; the mediator direction, #99,
/// FR-MED-10): what its builders refuse and emit (requests and answers), what its parsers accept
/// and refuse (answers and requests), the list bounds applied from the length before any
/// entry is read (L-055, poisoned-beyond-the-limit), and the additive bounds on
/// <see cref="MediationBodies"/>' readers leaving their unbounded behaviour unchanged.
/// </summary>
public sealed class CoordinateMediation20CodecTests
{
    private const string From = "did:example:alice";
    private const string Mediator = "did:example:mediator";
    private const string Recipient = "did:example:bob";

    // ---- builders -------------------------------------------------------------------------

    [Fact]
    public void Builders_address_the_mediator_from_the_recipient_with_a_fresh_uuid_and_no_timestamp()
    {
        var messages = new[]
        {
            CoordinateMediation20.CreateMediateRequest(From, Mediator),
            CoordinateMediation20.CreateKeylistUpdate(From, Mediator, new[] { new RecipientUpdate(Recipient, RecipientUpdateAction.Remove) }),
            CoordinateMediation20.CreateKeylistQuery(From, Mediator),
        };

        foreach (var message in messages)
        {
            message.From.Should().Be(From);
            message.To.Should().Equal(Mediator);
            Guid.TryParse(message.Id, out _).Should().BeTrue();
            message.CreatedTime.Should().BeNull();
            message.Thid.Should().BeNull();
        }
        messages.Select(m => m.Type).Should().Equal(
            CoordinateMediation20.MediateRequestType, CoordinateMediation20.KeylistUpdateType, CoordinateMediation20.KeylistQueryType);
        messages.Select(m => m.Id).Distinct().Should().HaveCount(3);
    }

    [Fact]
    public void Keylist_update_writes_remove_and_a_query_without_paginate_has_an_empty_body()
    {
        var update = CoordinateMediation20.CreateKeylistUpdate(From, Mediator, new[] { new RecipientUpdate(Recipient, RecipientUpdateAction.Remove) });
        update.Body!.ToJsonString().Should().Be($$"""{"updates":[{"recipient_did":"{{Recipient}}","action":"remove"}]}""");

        CoordinateMediation20.CreateKeylistQuery(From, Mediator).Body!.ToJsonString().Should().Be("{}");
    }

    [Theory]
    [InlineData(null, Mediator)]
    [InlineData("", Mediator)]
    [InlineData(From, null)]
    [InlineData(From, "")]
    public void Builders_refuse_a_missing_did(string? from, string? mediator)
    {
        var act = () => CoordinateMediation20.CreateMediateRequest(from!, mediator!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Builders_refuse_an_over_long_did_without_quoting_it()
    {
        var longDid = "did:example:" + new string('x', 2100);

        var act = () => CoordinateMediation20.CreateKeylistQuery(longDid, Mediator);

        act.Should().Throw<ArgumentException>().Which.Message.Should().NotContain("xxxx");
    }

    [Fact]
    public void Keylist_update_refuses_a_null_entry_an_undefined_action_and_an_over_long_did()
    {
        FluentActions.Invoking(() => CoordinateMediation20.CreateKeylistUpdate(From, Mediator, new RecipientUpdate[] { null! }))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => CoordinateMediation20.CreateKeylistUpdate(From, Mediator, new[] { new RecipientUpdate(Recipient, (RecipientUpdateAction)9) }))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => CoordinateMediation20.CreateKeylistUpdate(From, Mediator, new[] { new RecipientUpdate("did:example:" + new string('y', 2100), RecipientUpdateAction.Add) }))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => CoordinateMediation20.CreateKeylistUpdate(From, Mediator, null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Keylist_query_refuses_a_negative_offset()
    {
        var act = () => CoordinateMediation20.CreateKeylistQuery(From, Mediator, new RecipientQueryPaging(10, -1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- parsers --------------------------------------------------------------------------

    [Fact]
    public void Grant_accepts_the_2_0_string_and_the_3_0_array()
    {
        CoordinateMediation20.TryParseGrant(Answer("mediate-grant", new JsonObject { ["routing_did"] = Mediator }), out var single).Should().BeTrue();
        single.Should().Equal(Mediator);

        CoordinateMediation20.TryParseGrant(Answer("mediate-grant", new JsonObject { ["routing_did"] = new JsonArray(Mediator, "did:example:m2") }), out var many).Should().BeTrue();
        many.Should().Equal(Mediator, "did:example:m2");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"routing_did":5}""")]
    [InlineData("""{"routing_did":""}""")]
    [InlineData("""{"routing_did":[]}""")]
    [InlineData("""{"routing_did":[5]}""")]
    public void Grant_refuses_a_missing_mistyped_or_empty_routing_did(string body)
    {
        var act = () => CoordinateMediation20.TryParseGrant(Answer("mediate-grant", JsonNode.Parse(body)!.AsObject()), out _);

        act.Should().Throw<MalformedMessageException>();
    }

    [Fact]
    public void Grant_refuses_an_over_long_routing_did_without_quoting_it()
    {
        var act = () => CoordinateMediation20.TryParseGrant(Answer("mediate-grant", new JsonObject { ["routing_did"] = "did:example:" + new string('z', 2100) }), out _);

        act.Should().Throw<MalformedMessageException>().Which.Message.Should().NotContain("zzzz");
    }

    [Fact]
    public void Grant_bound_refuses_from_the_count_before_reading_a_poisoned_entry_beyond_it()
    {
        // 3 entries over a bound of 2; the third is not even a string. The refusal is the count.
        var body = new JsonObject { ["routing_did"] = new JsonArray(Mediator, "did:example:m2", 42) };

        var act = () => CoordinateMediation20.TryParseGrant(Answer("mediate-grant", body), out _, maxRoutingDids: 2);

        act.Should().Throw<MalformedMessageException>().WithMessage("*more than 2 entries*");
    }

    [Fact]
    public void Keylist_update_response_bound_refuses_from_the_count_before_reading_a_poisoned_entry_beyond_it()
    {
        var body = JsonNode.Parse($$"""{"updated":[{"recipient_did":"{{Recipient}}","action":"add","result":"success"},{"recipient_did":7}]}""")!.AsObject();

        var act = () => CoordinateMediation20.TryParseKeylistUpdateResponse(Answer("keylist-update-response", body), out _, maxEntries: 1);

        act.Should().Throw<MalformedMessageException>().WithMessage("*'updated' has more than 1 entries*");
    }

    [Fact]
    public void Keylist_bound_refuses_from_the_count_before_reading_a_poisoned_entry_beyond_it()
    {
        var body = JsonNode.Parse($$"""{"keys":[{"recipient_did":"{{Recipient}}"},"poison"]}""")!.AsObject();

        var act = () => CoordinateMediation20.TryParseKeylist(Answer("keylist", body), out _, out _, maxEntries: 1);

        act.Should().Throw<MalformedMessageException>().WithMessage("*'keys' has more than 1 entries*");
    }

    [Fact]
    public void Keylist_refuses_a_dids_list_the_3_0_name_and_reads_a_keys_page_without_pagination()
    {
        var threeShape = JsonNode.Parse($$"""{"dids":[{"recipient_did":"{{Recipient}}"}]}""")!.AsObject();
        FluentActions.Invoking(() => CoordinateMediation20.TryParseKeylist(Answer("keylist", threeShape), out _, out _))
            .Should().Throw<MalformedMessageException>().WithMessage("*'keys'*");

        var page = JsonNode.Parse($$"""{"keys":[{"recipient_did":"{{Recipient}}"}]}""")!.AsObject();
        CoordinateMediation20.TryParseKeylist(Answer("keylist", page), out var keys, out var pagination).Should().BeTrue();
        keys.Should().Equal(Recipient);
        pagination.Should().BeNull();
    }

    [Fact]
    public void Keylist_update_response_refuses_an_unknown_result()
    {
        var body = JsonNode.Parse($$"""{"updated":[{"recipient_did":"{{Recipient}}","action":"add","result":"maybe"}]}""")!.AsObject();

        var act = () => CoordinateMediation20.TryParseKeylistUpdateResponse(Answer("keylist-update-response", body), out _);

        act.Should().Throw<MalformedMessageException>().Which.Message.Should().NotContain("maybe");
    }

    [Theory]
    [InlineData("https://didcomm.org/coordinate-mediation/2.1/mediate-grant", true)]
    [InlineData("https://DIDComm.org/coordinate_mediation/2.0/mediate_grant", true)]
    [InlineData("https://didcomm.org/coordinate-mediation/3.0/mediate-grant", false)]
    [InlineData("https://didcomm.org/coordinate-mediation/1.0/mediate-grant", false)]
    public void Types_match_any_2_x_minor_ignoring_case_and_punctuation_but_no_other_major(string type, bool claimed)
    {
        var message = new MessageBuilder().WithType(type).WithFrom(Mediator).WithTo(From).WithThid("t")
            .WithBody(new JsonObject { ["routing_did"] = Mediator }).Build();

        CoordinateMediation20.TryParseGrant(message, out _).Should().Be(claimed);
    }

    [Fact]
    public void Answer_parsers_say_false_for_another_type()
    {
        var request = CoordinateMediation20.CreateMediateRequest(From, Mediator);

        CoordinateMediation20.TryParseGrant(request, out _).Should().BeFalse();
        CoordinateMediation20.TryParseDeny(request).Should().BeFalse();
        CoordinateMediation20.TryParseKeylistUpdateResponse(request, out _).Should().BeFalse();
        CoordinateMediation20.TryParseKeylist(request, out _, out _).Should().BeFalse();
    }

    // ---- request parsers (the mediator direction, #99) ------------------------------------

    private static readonly string[] All2_0Types =
    {
        CoordinateMediation20.MediateRequestType, CoordinateMediation20.MediateGrantType, CoordinateMediation20.MediateDenyType,
        CoordinateMediation20.KeylistUpdateType, CoordinateMediation20.KeylistUpdateResponseType,
        CoordinateMediation20.KeylistQueryType, CoordinateMediation20.KeylistType,
    };

    private static readonly string[] All3_0Types =
    {
        CoordinateMediation.MediateRequestType, CoordinateMediation.MediateGrantType, CoordinateMediation.MediateDenyType,
        CoordinateMediation.RecipientUpdateType, CoordinateMediation.RecipientUpdateResponseType,
        CoordinateMediation.RecipientQueryType, CoordinateMediation.RecipientType,
    };

    [Fact]
    public void Request_parsers_say_false_for_every_other_type_and_leave_their_outputs_null()
    {
        // A body each parser could read, so a type match would show as true, never as a throw.
        const string readable = """{"updates":[{"recipient_did":"did:example:bob","action":"add"}],"paginate":{"limit":1,"offset":0}}""";

        foreach (var type in All2_0Types.Concat(All3_0Types).Append("https://example.com/protocols/other/2.0/keylist-update"))
        {
            var message = Wire(type, readable);
            if (type != CoordinateMediation20.MediateRequestType)
                CoordinateMediation20.TryParseMediateRequest(message).Should().BeFalse(type);
            if (type != CoordinateMediation20.KeylistUpdateType)
            {
                CoordinateMediation20.TryParseKeylistUpdate(message, 10, out var updates).Should().BeFalse(type);
                updates.Should().BeNull();
            }
            if (type != CoordinateMediation20.KeylistQueryType)
            {
                CoordinateMediation20.TryParseKeylistQuery(message, out var paginate).Should().BeFalse(type);
                paginate.Should().BeNull();
            }
        }
    }

    [Theory]
    [InlineData("an invalid action", """{"recipient_did":"did:example:second","action":"explode"}""")]
    [InlineData("a non-object entry", "\"poison\"")]
    [InlineData("a number for recipient_did", """{"recipient_did":7,"action":"add"}""")]
    [InlineData("an entry without members", "{}")]
    public void Keylist_update_bound_refuses_from_the_count_before_reading_a_poisoned_entry_beyond_it(string what, string poison)
    {
        // L-055: bound 1, two entries, the second poisoned. The refusal is the count, so nothing
        // beyond the bound was decoded.
        var message = Wire(CoordinateMediation20.KeylistUpdateType, $$"""{"updates":[{"recipient_did":"{{Recipient}}","action":"add"},{{poison}}]}""");

        var act = () => CoordinateMediation20.TryParseKeylistUpdate(message, maxUpdates: 1, out _);

        var refusal = act.Should().Throw<MalformedMessageException>(what).Which.Message;
        refusal.Should().Match("*'updates' has more than 1 entries*", what);
        refusal.Should().NotContain("action").And.NotContain("must be").And.NotContain("recipient_did").And.NotContain("did:example");
    }

    [Fact]
    public void Keylist_update_refuses_a_2049_character_recipient_did_without_quoting_it_and_reads_one_of_2048()
    {
        var over = "did:example:SENTINEL" + new string('q', 2049 - "did:example:SENTINEL".Length);
        var atLimit = "did:example:" + new string('a', 2048 - "did:example:".Length);
        over.Should().HaveLength(2049);
        atLimit.Should().HaveLength(2048);

        var act = () => CoordinateMediation20.TryParseKeylistUpdate(
            Wire(CoordinateMediation20.KeylistUpdateType, $$"""{"updates":[{"recipient_did":"{{over}}","action":"add"}]}"""), 10, out _);

        act.Should().Throw<MalformedMessageException>().Which.Message.Should().Contain("2048").And.NotContain("SENTINEL").And.NotContain("qqqq");
        CoordinateMediation20.TryParseKeylistUpdate(
            Wire(CoordinateMediation20.KeylistUpdateType, $$"""{"updates":[{"recipient_did":"{{atLimit}}","action":"remove"}]}"""), 10, out var updates).Should().BeTrue();
        updates.Should().Equal(new RecipientUpdate(atLimit, RecipientUpdateAction.Remove));
    }

    [Theory]
    [InlineData("did:example:bob#key-1")]
    [InlineData("did:example:bob?service=didcomm")]
    [InlineData("did:example:bob/path")]
    [InlineData("did:example:bob;param=1")]
    [InlineData("not-a-did")]
    public void Keylist_update_returns_a_recipient_did_that_is_not_a_bare_did_unchanged_for_the_handler_to_refuse(string value)
    {
        // The parser bounds and types the member; whether it is a bare DID is the handler's call
        // (client_error, never normalized), so the value must reach it exactly as sent.
        var message = Wire(CoordinateMediation20.KeylistUpdateType, $$"""{"updates":[{"recipient_did":"{{value}}","action":"add"}]}""");

        CoordinateMediation20.TryParseKeylistUpdate(message, 10, out var updates).Should().BeTrue();

        updates.Should().Equal(new RecipientUpdate(value, RecipientUpdateAction.Add));
    }

    [Fact]
    public void An_absent_body_is_malformed_for_keylist_update_and_fine_for_mediate_request_and_keylist_query()
    {
        var update = Wire(CoordinateMediation20.KeylistUpdateType, bodyJson: null);
        update.Body.Should().BeNull();
        FluentActions.Invoking(() => CoordinateMediation20.TryParseKeylistUpdate(update, 10, out _))
            .Should().Throw<MalformedMessageException>().WithMessage("*'updates'*");

        // The protocol page's mediate-request has no body; a keylist-query without one asks for the first page.
        CoordinateMediation20.TryParseMediateRequest(Wire(CoordinateMediation20.MediateRequestType, bodyJson: null)).Should().BeTrue();
        CoordinateMediation20.TryParseKeylistQuery(Wire(CoordinateMediation20.KeylistQueryType, bodyJson: null), out var paginate).Should().BeTrue();
        paginate.Should().BeNull();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"updates":null}""")]
    [InlineData("""{"updates":{}}""")]
    [InlineData("""{"updates":"did:example:SENTINEL"}""")]
    [InlineData("""{"updates":[{"action":"add"}]}""")]
    [InlineData("""{"updates":[{"recipient_did":"did:example:SENTINEL"}]}""")]
    [InlineData("""{"updates":[{"recipient_did":"","action":"add"}]}""")]
    [InlineData("""{"updates":[{"recipient_did":null,"action":"add"}]}""")]
    [InlineData("""{"updates":[{"recipient_did":"did:example:SENTINEL","action":"Add"}]}""")]
    [InlineData("""{"updates":[{"recipient_did":"did:example:SENTINEL","action":"SENTINEL"}]}""")]
    public void Keylist_update_refuses_a_body_of_the_wrong_shape_without_quoting_a_value(string body)
    {
        var act = () => CoordinateMediation20.TryParseKeylistUpdate(Wire(CoordinateMediation20.KeylistUpdateType, body), 10, out _);

        act.Should().Throw<MalformedMessageException>().Which.Message.Should().NotContain("SENTINEL");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"paginate":null}""")]
    [InlineData("""{"other":1}""")]
    public void Keylist_query_without_paginate_or_with_an_explicit_null_asks_for_the_first_page(string body)
    {
        CoordinateMediation20.TryParseKeylistQuery(Wire(CoordinateMediation20.KeylistQueryType, body), out var paginate).Should().BeTrue();

        paginate.Should().BeNull();
    }

    [Theory]
    [InlineData("negative offset", """{"paginate":{"limit":10,"offset":-1}}""")]
    [InlineData("limit a string", """{"paginate":{"limit":"10","offset":0}}""")]
    [InlineData("limit a fraction", """{"paginate":{"limit":1.5,"offset":0}}""")]
    [InlineData("limit true", """{"paginate":{"limit":true,"offset":0}}""")]
    [InlineData("limit null", """{"paginate":{"limit":null,"offset":0}}""")]
    [InlineData("limit an array", """{"paginate":{"limit":[10],"offset":0}}""")]
    [InlineData("offset a string", """{"paginate":{"limit":10,"offset":"0"}}""")]
    [InlineData("limit missing", """{"paginate":{"offset":0}}""")]
    [InlineData("offset missing", """{"paginate":{"limit":10}}""")]
    [InlineData("paginate a number", """{"paginate":5}""")]
    [InlineData("paginate an array", """{"paginate":[]}""")]
    public void Keylist_query_refuses_a_paginate_of_the_wrong_shape(string what, string body)
    {
        var act = () => CoordinateMediation20.TryParseKeylistQuery(Wire(CoordinateMediation20.KeylistQueryType, body), out _);

        act.Should().Throw<MalformedMessageException>(what).Which.Message.Should().Contain("paginate", what);
    }

    [Fact]
    public void Keylist_query_reads_a_page_and_clamps_an_integer_beyond_32_bits()
    {
        CoordinateMediation20.TryParseKeylistQuery(Wire(CoordinateMediation20.KeylistQueryType, """{"paginate":{"limit":5,"offset":10}}"""), out var page).Should().BeTrue();
        page.Should().Be(new RecipientQueryPaging(Limit: 5, Offset: 10));

        CoordinateMediation20.TryParseKeylistQuery(Wire(CoordinateMediation20.KeylistQueryType, """{"paginate":{"limit":99999999999,"offset":1e1}}"""), out var clamped).Should().BeTrue();
        clamped.Should().Be(new RecipientQueryPaging(Limit: int.MaxValue, Offset: 10));
    }

    [Theory]
    [InlineData("https://didcomm.org/coordinate-mediation/2.0", true)]
    [InlineData("https://didcomm.org/coordinate-mediation/2.1", true)]
    [InlineData("https://DIDComm.org/Coordinate_Mediation/2.0", true)]
    [InlineData("https://didcomm.org/coordinate_mediation/2.7", true)]
    [InlineData("https://didcomm.org/coordinate-mediation/3.0", false)]
    [InlineData("https://didcomm.org/coordinate-mediation/1.0", false)]
    [InlineData("https://example.com/coordinate-mediation/2.0", false)]
    public void Request_types_match_any_2_x_minor_ignoring_case_and_punctuation_but_no_other_major(string piuri, bool claimed)
    {
        foreach (var spell in new Func<string, string>[] { n => n, n => n.ToUpperInvariant(), n => n.Replace('-', '_') })
        {
            const string body = """{"updates":[{"recipient_did":"did:example:bob","action":"add"}],"paginate":{"limit":1,"offset":0}}""";
            CoordinateMediation20.TryParseMediateRequest(Wire(piuri + "/" + spell("mediate-request"), body)).Should().Be(claimed, spell("mediate-request"));
            CoordinateMediation20.TryParseKeylistUpdate(Wire(piuri + "/" + spell("keylist-update"), body), 10, out _).Should().Be(claimed, spell("keylist-update"));
            CoordinateMediation20.TryParseKeylistQuery(Wire(piuri + "/" + spell("keylist-query"), body), out _).Should().Be(claimed, spell("keylist-query"));
        }
    }

    // ---- answer builders (the mediator direction, #99) ------------------------------------

    private static readonly string OverLongDid = "did:example:SENTINEL" + new string('w', 2049 - "did:example:SENTINEL".Length);

    [Fact]
    public void Answer_builders_address_the_recipient_from_the_mediator_on_the_request_thread_with_the_canonical_type()
    {
        var answers = new[]
        {
            CoordinateMediation20.CreateGrant(Mediator, From, Mediator, "request-1"),
            CoordinateMediation20.CreateDeny(Mediator, From, "request-1"),
            CoordinateMediation20.CreateKeylistUpdateResponse(Mediator, From,
                new[] { new RecipientUpdateResult(Recipient, RecipientUpdateAction.Add, RecipientUpdateOutcome.NoChange) }, "request-1"),
            CoordinateMediation20.CreateKeylist(Mediator, From, new[] { Recipient }, new RecipientPagination(1, 0, 0), "request-1"),
        };

        foreach (var answer in answers)
        {
            answer.From.Should().Be(Mediator);
            answer.To.Should().Equal(From);
            answer.Thid.Should().Be("request-1");
            Guid.TryParse(answer.Id, out _).Should().BeTrue();
            answer.CreatedTime.Should().BeNull();
        }
        answers.Select(m => m.Type).Should().Equal(
            CoordinateMediation20.MediateGrantType, CoordinateMediation20.MediateDenyType,
            CoordinateMediation20.KeylistUpdateResponseType, CoordinateMediation20.KeylistType);
    }

    [Fact]
    public void Grant_body_on_the_wire_is_exactly_one_routing_did_as_a_json_string()
    {
        var grant = CoordinateMediation20.CreateGrant(Mediator, From, "did:example:routing", "request-1");

        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(grant, DidCommJson.Default));
        var body = wire.RootElement.GetProperty("body");
        body.GetRawText().Should().Be("""{"routing_did":"did:example:routing"}""");
        body.GetProperty("routing_did").ValueKind.Should().Be(JsonValueKind.String, "both 2.0 mediators in the field and their wallets read a string (I-1)");
    }

    [Fact]
    public void Keylist_without_pagination_omits_the_member_and_an_empty_page_is_an_empty_keys_list()
    {
        var page = CoordinateMediation20.CreateKeylist(Mediator, From, new[] { Recipient }, pagination: null, "request-1");
        var empty = CoordinateMediation20.CreateKeylist(Mediator, From, Array.Empty<string>(), pagination: null, "request-1");

        BodyOnTheWire(page).Should().Be($$"""{"keys":[{"recipient_did":"{{Recipient}}"}]}""");
        BodyOnTheWire(empty).Should().Be("""{"keys":[]}""");
    }

    public static IEnumerable<object?[]> AnswerBuilderDidArguments()
    {
        var values = new (string What, string? Value)[] { ("null", null), ("empty", ""), ("2049 characters", OverLongDid) };
        var arguments = new[]
        {
            "grant.from", "grant.to", "grant.routingDid", "deny.from", "deny.to",
            "update-response.from", "update-response.to", "update-response.recipient_did",
            "keylist.from", "keylist.to", "keylist.keys",
        };
        foreach (var argument in arguments)
        {
            foreach (var (what, value) in values)
                yield return new object?[] { argument, what, value };
        }
    }

    [Theory]
    [MemberData(nameof(AnswerBuilderDidArguments))]
    public void Answer_builders_refuse_a_missing_empty_or_over_long_did_without_quoting_it(string argument, string what, string? value)
    {
        RecipientUpdateResult[] Results(string did) => new[] { new RecipientUpdateResult(did, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success) };
        (Action Act, string Parameter) target = argument switch
        {
            "grant.from" => (() => CoordinateMediation20.CreateGrant(value!, From, Mediator, "t"), "from"),
            "grant.to" => (() => CoordinateMediation20.CreateGrant(Mediator, value!, Mediator, "t"), "to"),
            "grant.routingDid" => (() => CoordinateMediation20.CreateGrant(Mediator, From, value!, "t"), "routingDid"),
            "deny.from" => (() => CoordinateMediation20.CreateDeny(value!, From, "t"), "from"),
            "deny.to" => (() => CoordinateMediation20.CreateDeny(Mediator, value!, "t"), "to"),
            "update-response.from" => (() => CoordinateMediation20.CreateKeylistUpdateResponse(value!, From, Results(Recipient), "t"), "from"),
            "update-response.to" => (() => CoordinateMediation20.CreateKeylistUpdateResponse(Mediator, value!, Results(Recipient), "t"), "to"),
            "update-response.recipient_did" => (() => CoordinateMediation20.CreateKeylistUpdateResponse(Mediator, From, Results(value!), "t"), "updated"),
            "keylist.from" => (() => CoordinateMediation20.CreateKeylist(value!, From, new[] { Recipient }, null, "t"), "from"),
            "keylist.to" => (() => CoordinateMediation20.CreateKeylist(Mediator, value!, new[] { Recipient }, null, "t"), "to"),
            "keylist.keys" => (() => CoordinateMediation20.CreateKeylist(Mediator, From, new[] { value! }, null, "t"), "keys"),
            _ => throw new ArgumentOutOfRangeException(nameof(argument)),
        };

        var refusal = target.Act.Should().Throw<ArgumentException>($"{argument} {what}").Which;
        refusal.ParamName.Should().Be(target.Parameter);
        refusal.Message.Should().NotContain("SENTINEL").And.NotContain("wwww");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Answer_builders_refuse_a_missing_thid(string? thid)
    {
        var results = new[] { new RecipientUpdateResult(Recipient, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success) };
        var builders = new Action[]
        {
            () => CoordinateMediation20.CreateGrant(Mediator, From, Mediator, thid!),
            () => CoordinateMediation20.CreateDeny(Mediator, From, thid!),
            () => CoordinateMediation20.CreateKeylistUpdateResponse(Mediator, From, results, thid!),
            () => CoordinateMediation20.CreateKeylist(Mediator, From, new[] { Recipient }, null, thid!),
        };

        foreach (var build in builders)
            build.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("thid");
    }

    [Fact]
    public void Answer_builders_refuse_undefined_enum_values_and_null_lists_or_entries_without_quoting_a_did()
    {
        const string sentinel = "did:example:SENTINEL-bob";

        var badAction = () => CoordinateMediation20.CreateKeylistUpdateResponse(Mediator, From,
            new[] { new RecipientUpdateResult(sentinel, (RecipientUpdateAction)9, RecipientUpdateOutcome.Success) }, "t");
        var badResult = () => CoordinateMediation20.CreateKeylistUpdateResponse(Mediator, From,
            new[] { new RecipientUpdateResult(sentinel, RecipientUpdateAction.Remove, (RecipientUpdateOutcome)9) }, "t");
        foreach (var act in new[] { badAction, badResult })
        {
            var refusal = act.Should().Throw<ArgumentOutOfRangeException>().Which;
            refusal.ParamName.Should().Be("updated", "the builder checks before it writes");
            refusal.Message.Should().NotContain("SENTINEL");
            refusal.ActualValue.Should().BeNull("the value is not echoed");
        }

        FluentActions.Invoking(() => CoordinateMediation20.CreateKeylistUpdateResponse(Mediator, From, null!, "t"))
            .Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("updated");
        FluentActions.Invoking(() => CoordinateMediation20.CreateKeylistUpdateResponse(Mediator, From, new RecipientUpdateResult[] { null! }, "t"))
            .Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("updated");
        FluentActions.Invoking(() => CoordinateMediation20.CreateKeylist(Mediator, From, null!, null, "t"))
            .Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("keys");
        FluentActions.Invoking(() => CoordinateMediation20.CreateKeylist(Mediator, From, new[] { sentinel, null! }, null, "t"))
            .Should().Throw<ArgumentException>().Which.Message.Should().NotContain("SENTINEL");
    }

    // ---- the additive MediationBodies bounds leave the unbounded readers unchanged --------

    [Fact]
    public void Unbounded_readers_still_read_long_lists()
    {
        var dids = Enumerable.Range(0, 40).Select(i => $"did:example:r{i}").ToArray();
        var routing = new JsonObject { ["routing_did"] = new JsonArray(dids.Select(d => (JsonNode)d!).ToArray()) };
        MediationBodies.ReadRoutingDids(MediationBodies.ToElement(routing), "mediate-grant").Should().Equal(dids);

        var recipients = MediationBodies.WriteRecipients("dids", dids, null);
        MediationBodies.ReadRecipients(MediationBodies.ToElement(recipients), "dids", "recipient").Dids.Should().Equal(dids);

        var results = dids.Select(d => new RecipientUpdateResult(d, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success)).ToArray();
        MediationBodies.ReadUpdated(MediationBodies.ToElement(MediationBodies.WriteUpdated(results)), "recipient-update-response").Should().Equal(results);
    }

    [Fact]
    public void A_list_exactly_at_the_bound_is_read()
    {
        var body = new JsonObject { ["routing_did"] = new JsonArray(Mediator, "did:example:m2") };

        MediationBodies.ReadRoutingDids(MediationBodies.ToElement(body), "mediate-grant", maxEntries: 2).Should().HaveCount(2);
    }

    // A request as it arrives: parsed from JSON, so the body may be absent or of any shape.
    private static Message Wire(string type, string? bodyJson)
    {
        var body = bodyJson is null ? string.Empty : ",\"body\":" + bodyJson;
        var json = $$"""{"id":"request-1","type":"{{type}}","from":"{{From}}","to":["{{Mediator}}"]{{body}}}""";
        return JsonSerializer.Deserialize<Message>(json, DidCommJson.Default)!;
    }

    private static string BodyOnTheWire(Message message)
    {
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(message, DidCommJson.Default));
        return wire.RootElement.GetProperty("body").GetRawText();
    }

    private static Message Answer(string name, JsonObject body)
    {
        // Through JSON, as an answer arrives.
        var message = new MessageBuilder()
            .WithType(CoordinateMediation20.ProtocolUri + "/" + name)
            .WithFrom(Mediator)
            .WithTo(From)
            .WithThid("t")
            .WithBody(body)
            .Build();
        return JsonSerializer.Deserialize<Message>(JsonSerializer.Serialize(message, DidCommJson.Default), DidCommJson.Default)!;
    }
}
