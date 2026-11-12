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
/// The strict parsers of <see cref="CoordinateMediation"/> (FR-MED-02/06/08): the rejection table.
/// Every refusal is a <see cref="MalformedMessageException"/> whose text names the member and the
/// rule and quotes nothing from the message (each bad body below carries the word SENTINEL).
/// </summary>
public sealed class CoordinateMediationParserTests
{
    private const string P = CoordinateMediation.ProtocolUri;
    private const string Did = "did:example:SENTINEL";
    private static readonly string AtLimit = "did:example:" + new string('a', 2048 - "did:example:".Length);
    private static readonly string OverLimit = "did:example:SENTINEL" + new string('a', 2049);

    // ---- type matching --------------------------------------------------------------------

    [Theory]
    [InlineData("https://didcomm.org/coordinate-mediation/3.0/mediate-request", true)]
    [InlineData("https://didcomm.org/coordinate-mediation/3.1/mediate-request", true)]   // any 3.x (FR-PROTO-02)
    [InlineData("https://didcomm.org/coordinate-mediation/3.0/MEDIATE-REQUEST", true)]   // case (FR-PROTO-01)
    [InlineData("https://didcomm.org/coordinate-mediation/3.0/mediate_request", true)]   // punctuation
    [InlineData("https://didcomm.org/coordinate_mediation/3.0/mediaterequest", true)]
    [InlineData("https://didcomm.org/coordinate-mediation/2.0/mediate-request", false)]  // other major
    [InlineData("https://didcomm.org/coordinate-mediation/4.0/mediate-request", false)]
    [InlineData("https://example.org/coordinate-mediation/3.0/mediate-request", false)]  // other doc URI
    [InlineData("https://didcomm.org/coordinate-mediation/3.0/mediate-grant", false)]
    [InlineData("https://didcomm.org/coordinate-mediation/3.0/", false)]
    [InlineData("mediate-request", false)]
    [InlineData("", false)]
    public void Types_match_as_the_handler_registry_matches_them(string type, bool expected)
        => CoordinateMediation.TryParseMediateRequest(Raw(type, "{}")).Should().Be(expected);

    [Fact]
    public void A_parser_returns_false_for_another_type_whatever_the_body()
    {
        // Garbage that would be malformed for every parser; none may throw for a type it does not own.
        var other = Raw("https://didcomm.org/trust-ping/2.0/ping", """{"updates":7,"updated":7,"routing_did":7,"paginate":7,"dids":7}""");

        CoordinateMediation.TryParseMediateRequest(other).Should().BeFalse();
        CoordinateMediation.TryParseDeny(other).Should().BeFalse();
        CoordinateMediation.TryParseGrant(other, out var routing).Should().BeFalse();
        routing.Should().BeNull();
        CoordinateMediation.TryParseRecipientUpdate(other, out var updates).Should().BeFalse();
        updates.Should().BeNull();
        CoordinateMediation.TryParseRecipientUpdateResponse(other, out var updated).Should().BeFalse();
        updated.Should().BeNull();
        CoordinateMediation.TryParseRecipientQuery(other, out var paginate).Should().BeFalse();
        paginate.Should().BeNull();
        CoordinateMediation.TryParseRecipient(other, out var dids, out var pagination).Should().BeFalse();
        dids.Should().BeNull();
        pagination.Should().BeNull();
    }

    [Fact]
    public void Recipient_and_recipient_update_are_not_confused()
    {
        var recipient = Raw(P + "/recipient", """{"dids":[]}""");
        CoordinateMediation.TryParseRecipientUpdate(recipient, out _).Should().BeFalse();
        CoordinateMediation.TryParseRecipientQuery(recipient, out _).Should().BeFalse();
        CoordinateMediation.TryParseRecipientUpdateResponse(recipient, out _).Should().BeFalse();
        CoordinateMediation.TryParseRecipient(recipient, out _, out _).Should().BeTrue();
    }

    [Fact]
    public void A_null_message_is_an_argument_error()
    {
        var act = () => CoordinateMediation.TryParseMediateRequest(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ---- accepted shapes ------------------------------------------------------------------

    [Fact]
    public void Mediate_request_and_deny_accept_an_absent_or_any_object_body()
    {
        CoordinateMediation.TryParseMediateRequest(Raw(P + "/mediate-request", null)).Should().BeTrue();
        CoordinateMediation.TryParseMediateRequest(Raw(P + "/mediate-request", "{}")).Should().BeTrue();
        CoordinateMediation.TryParseMediateRequest(Raw(P + "/mediate-request", """{"future":"member"}""")).Should().BeTrue();
        CoordinateMediation.TryParseDeny(Raw(P + "/mediate-deny", null)).Should().BeTrue();
        CoordinateMediation.TryParseDeny(Raw(P + "/mediate-deny", """{"reason":"ignored"}""")).Should().BeTrue();
    }

    [Fact]
    public void Routing_did_is_accepted_as_an_array_and_as_the_2_0_string()
    {
        CoordinateMediation.TryParseGrant(Raw(P + "/mediate-grant", """{"routing_did":["did:example:a","did:example:b"]}"""), out var array).Should().BeTrue();
        array.Should().Equal("did:example:a", "did:example:b");

        CoordinateMediation.TryParseGrant(Raw(P + "/mediate-grant", """{"routing_did":"did:example:a"}"""), out var single).Should().BeTrue();
        single.Should().Equal("did:example:a");

        CoordinateMediation.TryParseGrant(Raw(P + "/mediate-grant", $$"""{"routing_did":["{{AtLimit}}"],"endpoint":"ignored"}"""), out var atLimit).Should().BeTrue();
        atLimit.Should().Equal(AtLimit);
    }

    [Fact]
    public void Updates_accept_the_limit_length_unknown_members_and_an_empty_list()
    {
        var body = $$"""{"updates":[{"recipient_did":"{{AtLimit}}","action":"add","note":"ignored"},{"action":"remove","recipient_did":"did:example:b"}],"extra":1}""";
        CoordinateMediation.TryParseRecipientUpdate(Raw(P + "/recipient-update", body), out var updates).Should().BeTrue();
        updates.Should().Equal(
            new RecipientUpdate(AtLimit, RecipientUpdateAction.Add),
            new RecipientUpdate("did:example:b", RecipientUpdateAction.Remove));

        CoordinateMediation.TryParseRecipientUpdate(Raw(P + "/recipient-update", """{"updates":[]}"""), out var none).Should().BeTrue();
        none.Should().BeEmpty();
    }

    [Fact]
    public void A_recipient_did_that_is_not_a_did_is_still_parsed()
    {
        // Whether the value is a DID is the handler's question (client_error), not the parser's.
        CoordinateMediation.TryParseRecipientUpdate(Raw(P + "/recipient-update", """{"updates":[{"recipient_did":"not a did","action":"add"},{"recipient_did":"did:example:a#key-1","action":"remove"}]}"""), out var updates).Should().BeTrue();
        updates!.Select(u => u.RecipientDid).Should().Equal("not a did", "did:example:a#key-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"other":1}""")]
    [InlineData("""{"paginate":null}""")] // an explicit null is "not present" (serializers that write unset members)
    public void A_query_without_paginate_parses_to_null(string? body)
    {
        CoordinateMediation.TryParseRecipientQuery(Raw(P + "/recipient-query", body), out var paginate).Should().BeTrue();
        paginate.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"paginate":{"limit":30,"offset":0}}""", 30, 0)]
    [InlineData("""{"paginate":{"offset":5,"limit":1,"extra":true}}""", 1, 5)]
    [InlineData("""{"paginate":{"limit":0,"offset":0}}""", 0, 0)]
    [InlineData("""{"paginate":{"limit":-5,"offset":0}}""", -5, 0)]                      // empty page, not malformed
    [InlineData("""{"paginate":{"limit":10.0,"offset":2.0}}""", 10, 2)]                  // JSON has one number type
    [InlineData("""{"paginate":{"limit":1e1,"offset":0}}""", 10, 0)]
    [InlineData("""{"paginate":{"limit":99999999999,"offset":0}}""", int.MaxValue, 0)]   // clamped
    [InlineData("""{"paginate":{"limit":-99999999999,"offset":0}}""", int.MinValue, 0)]
    [InlineData("""{"paginate":{"limit":1,"offset":99999999999}}""", 1, int.MaxValue)]
    [InlineData("""{"paginate":{"limit":123456789012345678901234567,"offset":0}}""", int.MaxValue, 0)] // beyond Int64
    public void Paginate_integers_are_read_and_clamped(string body, int limit, int offset)
    {
        CoordinateMediation.TryParseRecipientQuery(Raw(P + "/recipient-query", body), out var paginate).Should().BeTrue();
        paginate.Should().Be(new RecipientQueryPaging(limit, offset));
    }

    [Fact]
    public void Recipient_accepts_a_missing_pagination_and_an_empty_list()
    {
        CoordinateMediation.TryParseRecipient(Raw(P + "/recipient", """{"dids":[]}"""), out var dids, out var pagination).Should().BeTrue();
        dids.Should().BeEmpty();
        pagination.Should().BeNull();

        CoordinateMediation.TryParseRecipient(Raw(P + "/recipient", """{"dids":[],"pagination":null}"""), out dids, out pagination).Should().BeTrue();
        pagination.Should().BeNull("an explicit null is \"not present\"");

        CoordinateMediation.TryParseRecipient(Raw(P + "/recipient", """{"dids":[{"recipient_did":"did:example:a","x":1}],"pagination":{"count":1,"offset":0,"remaining":0,"x":1}}"""), out dids, out pagination).Should().BeTrue();
        dids.Should().Equal("did:example:a");
        pagination.Should().Be(new RecipientPagination(1, 0, 0));
    }

    [Fact]
    public void Bodies_built_in_code_parse_like_bodies_read_from_text()
    {
        // JsonObject values created from CLR numbers are not backed by a JsonElement; the parser
        // must not depend on which kind it gets.
        var query = new MessageBuilder().WithType(P + "/recipient-query").WithBody(new JsonObject
        {
            ["paginate"] = new JsonObject { ["limit"] = 7L, ["offset"] = (short)3 },
        }).Build();
        CoordinateMediation.TryParseRecipientQuery(query, out var paginate).Should().BeTrue();
        paginate.Should().Be(new RecipientQueryPaging(7, 3));

        var fractional = new MessageBuilder().WithType(P + "/recipient-query").WithBody(new JsonObject
        {
            ["paginate"] = new JsonObject { ["limit"] = 1.5, ["offset"] = 0 },
        }).Build();
        Refused(() => CoordinateMediation.TryParseRecipientQuery(fractional, out _));

        var whole = new MessageBuilder().WithType(P + "/recipient-query").WithBody(new JsonObject
        {
            ["paginate"] = new JsonObject { ["limit"] = 4.0, ["offset"] = 0m },
        }).Build();
        CoordinateMediation.TryParseRecipientQuery(whole, out paginate).Should().BeTrue();
        paginate.Should().Be(new RecipientQueryPaging(4, 0));

        var update = new MessageBuilder().WithType(P + "/recipient-update").WithBody(new JsonObject
        {
            ["updates"] = new JsonArray(new JsonObject { ["recipient_did"] = "did:example:a", ["action"] = "add" }),
        }).Build();
        CoordinateMediation.TryParseRecipientUpdate(update, out var updates).Should().BeTrue();
        updates.Should().Equal(new RecipientUpdate("did:example:a", RecipientUpdateAction.Add));
    }

    // ---- the rejection table --------------------------------------------------------------

    public static TheoryData<string, string?> Rejections
    {
        get
        {
            var over = OverLimit;
            var data = new TheoryData<string, string?>
            {
                // mediate-grant
                { "mediate-grant", null },
                { "mediate-grant", "{}" },
                { "mediate-grant", """{"routing_did":[]}""" },
                { "mediate-grant", """{"routing_did":7}""" },
                { "mediate-grant", """{"routing_did":null}""" },
                { "mediate-grant", """{"routing_did":{"did":"did:example:SENTINEL"}}""" },
                { "mediate-grant", """{"routing_did":[7]}""" },
                { "mediate-grant", """{"routing_did":["did:example:SENTINEL",null]}""" },
                { "mediate-grant", """{"routing_did":[""]}""" },
                { "mediate-grant", """{"routing_did":""}""" },
                { "mediate-grant", $$"""{"routing_did":["{{over}}"]}""" },
                { "mediate-grant", $$"""{"routing_did":"{{over}}"}""" },
                { "mediate-grant", """{"ROUTING_DID":["did:example:SENTINEL"]}""" },   // member names are case-sensitive

                // recipient-update
                { "recipient-update", null },
                { "recipient-update", "{}" },
                { "recipient-update", """{"updates":null}""" },
                { "recipient-update", """{"updates":"did:example:SENTINEL"}""" },
                { "recipient-update", """{"updates":{"recipient_did":"did:example:SENTINEL","action":"add"}}""" },
                { "recipient-update", """{"updates":["did:example:SENTINEL"]}""" },
                { "recipient-update", """{"updates":[null]}""" },
                { "recipient-update", """{"updates":[[]]}""" },
                { "recipient-update", """{"updates":[{"action":"add"}]}""" },
                { "recipient-update", """{"updates":[{"recipient_did":"did:example:SENTINEL"}]}""" },
                { "recipient-update", """{"updates":[{"recipient_did":7,"action":"add"}]}""" },
                { "recipient-update", """{"updates":[{"recipient_did":null,"action":"add"}]}""" },
                { "recipient-update", """{"updates":[{"recipient_did":["did:example:SENTINEL"],"action":"add"}]}""" },
                { "recipient-update", """{"updates":[{"recipient_did":"","action":"add"}]}""" },
                { "recipient-update", $$"""{"updates":[{"recipient_did":"{{over}}","action":"add"}]}""" },
                { "recipient-update", """{"updates":[{"recipient_did":"did:example:SENTINEL","action":"SENTINEL-upsert"}]}""" },
                { "recipient-update", """{"updates":[{"recipient_did":"did:example:SENTINEL","action":"ADD"}]}""" },
                { "recipient-update", """{"updates":[{"recipient_did":"did:example:SENTINEL","action":""}]}""" },
                { "recipient-update", """{"updates":[{"recipient_did":"did:example:SENTINEL","action":1}]}""" },
                { "recipient-update", """{"updates":[{"recipient_did":"did:example:SENTINEL","action":null}]}""" },
                { "recipient-update", """{"updates":[{"recipient_did":"did:example:ok","action":"add"},{"recipient_did":"did:example:SENTINEL","action":"nope-SENTINEL"}]}""" },

                // recipient-update-response
                { "recipient-update-response", null },
                { "recipient-update-response", "{}" },
                { "recipient-update-response", """{"updated":7}""" },
                { "recipient-update-response", """{"updated":[7]}""" },
                { "recipient-update-response", """{"updated":[{"recipient_did":"did:example:SENTINEL","action":"add"}]}""" },
                { "recipient-update-response", """{"updated":[{"recipient_did":"did:example:SENTINEL","result":"success"}]}""" },
                { "recipient-update-response", """{"updated":[{"action":"add","result":"success"}]}""" },
                { "recipient-update-response", """{"updated":[{"recipient_did":"did:example:SENTINEL","action":"add","result":"SENTINEL-ok"}]}""" },
                { "recipient-update-response", """{"updated":[{"recipient_did":"did:example:SENTINEL","action":"add","result":"Success"}]}""" },
                { "recipient-update-response", """{"updated":[{"recipient_did":"did:example:SENTINEL","action":"add","result":""}]}""" },   // the page's placeholder
                { "recipient-update-response", """{"updated":[{"recipient_did":"did:example:SENTINEL","action":"","result":"success"}]}""" },
                { "recipient-update-response", """{"updated":[{"recipient_did":"did:example:SENTINEL","action":"add","result":3}]}""" },
                { "recipient-update-response", $$"""{"updated":[{"recipient_did":"{{over}}","action":"add","result":"success"}]}""" },

                // recipient-query
                { "recipient-query", """{"paginate":7}""" },
                { "recipient-query", """{"paginate":[30,0]}""" },
                { "recipient-query", """{"paginate":"SENTINEL"}""" },
                { "recipient-query", """{"paginate":{}}""" },
                { "recipient-query", """{"paginate":{"limit":30}}""" },
                { "recipient-query", """{"paginate":{"offset":0}}""" },
                { "recipient-query", """{"paginate":{"limit":"10","offset":0}}""" },
                { "recipient-query", """{"paginate":{"limit":10,"offset":"0"}}""" },
                { "recipient-query", """{"paginate":{"limit":1.5,"offset":0}}""" },
                { "recipient-query", """{"paginate":{"limit":10,"offset":0.5}}""" },
                { "recipient-query", """{"paginate":{"limit":10,"offset":-1}}""" },
                { "recipient-query", """{"paginate":{"limit":10,"offset":-99999999999}}""" },
                { "recipient-query", """{"paginate":{"limit":null,"offset":0}}""" },
                { "recipient-query", """{"paginate":{"limit":true,"offset":0}}""" },
                { "recipient-query", """{"paginate":{"limit":[10],"offset":0}}""" },
                { "recipient-query", """{"paginate":{"limit":1e400,"offset":0}}""" },
                { "recipient-query", """{"paginate":{"limit":1e-3,"offset":0}}""" },
                { "recipient-query", """{"paginate":{"Limit":10,"Offset":0}}""" },

                // recipient
                { "recipient", null },
                { "recipient", "{}" },
                { "recipient", """{"dids":"did:example:SENTINEL"}""" },
                { "recipient", """{"dids":["did:example:SENTINEL"]}""" },
                { "recipient", """{"dids":[{}]}""" },
                { "recipient", """{"dids":[{"recipient_did":7}]}""" },
                { "recipient", """{"dids":[{"recipient_did":""}]}""" },
                { "recipient", $$"""{"dids":[{"recipient_did":"{{over}}"}]}""" },
                { "recipient", """{"keys":[{"recipient_did":"did:example:SENTINEL"}]}""" },   // the 2.0 list name
                { "recipient", """{"dids":[],"pagination":7}""" },
                { "recipient", """{"dids":[],"pagination":{}}""" },
                { "recipient", """{"dids":[],"pagination":{"count":1,"offset":0}}""" },
                { "recipient", """{"dids":[],"pagination":{"count":1,"remaining":0}}""" },
                { "recipient", """{"dids":[],"pagination":{"offset":0,"remaining":0}}""" },
                { "recipient", """{"dids":[],"pagination":{"count":"1","offset":0,"remaining":0}}""" },
                { "recipient", """{"dids":[],"pagination":{"count":1,"offset":0,"remaining":0.5}}""" },
            };
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public void A_malformed_body_is_refused_without_quoting_it(string name, string? body)
    {
        var message = Raw(P + "/" + name, body);
        Action act = name switch
        {
            "mediate-grant" => () => CoordinateMediation.TryParseGrant(message, out _),
            "recipient-update" => () => CoordinateMediation.TryParseRecipientUpdate(message, out _),
            "recipient-update-response" => () => CoordinateMediation.TryParseRecipientUpdateResponse(message, out _),
            "recipient-query" => () => CoordinateMediation.TryParseRecipientQuery(message, out _),
            "recipient" => () => CoordinateMediation.TryParseRecipient(message, out _, out _),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        var ex = Refused(act);
        ex.Message.Should().StartWith(name + " ", "the text names the message");
    }

    [Fact]
    public void An_over_long_did_is_refused_by_length_and_names_the_bound()
    {
        var ex = Refused(() => CoordinateMediation.TryParseRecipientUpdate(
            Raw(P + "/recipient-update", $$"""{"updates":[{"recipient_did":"{{OverLimit}}","action":"add"}]}"""), out _));
        ex.Message.Should().Contain("recipient_did").And.Contain("2048").And.Contain("FR-API-09");

        ex = Refused(() => CoordinateMediation.TryParseGrant(Raw(P + "/mediate-grant", $$"""{"routing_did":["{{OverLimit}}"]}"""), out _));
        ex.Message.Should().Contain("routing_did").And.Contain("2048");
    }

    [Fact]
    public void A_megabyte_recipient_did_is_refused_as_cheaply_as_a_short_one()
    {
        // FR-API-09: one length comparison, never a parse. A pathological value must not be scanned
        // by a DID parser; the assertion is on the result, the point is that this returns at once.
        var huge = new string('x', 1024 * 1024);
        var message = new MessageBuilder().WithType(P + "/recipient-update").WithBody(new JsonObject
        {
            ["updates"] = new JsonArray(new JsonObject { ["recipient_did"] = huge, ["action"] = "add" }),
        }).Build();
        var ex = Refused(() => CoordinateMediation.TryParseRecipientUpdate(message, out _));
        ex.Message.Length.Should().BeLessThan(200);
    }

    // ---- BareDid (FR-MED-05) --------------------------------------------------------------

    [Theory]
    [InlineData("did:example:alice", true)]
    [InlineData("did:example:a:b:c", true)]
    [InlineData("did:web:example.com%3A8080", true)]
    [InlineData("did:peer:2.Ez6LSabc.Vz6Mkabc", true)]
    [InlineData("did:example:alice#key-1", false)]
    [InlineData("did:example:alice?service=x", false)]
    [InlineData("did:example:alice/path", false)]
    [InlineData("did:example:alice;p=1", false)]
    [InlineData("did:example:alice#", false)]
    [InlineData("did:example:alice?", false)]
    [InlineData("did:example:alice/", false)]
    [InlineData(" did:example:alice", false)]
    [InlineData("did:example:alice ", false)]
    [InlineData("did:example:alice\n", false)]
    [InlineData("DID:example:alice", false)]
    [InlineData("did:example", false)]
    [InlineData("did:example:", false)]
    [InlineData("not-a-did", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Bare_did_means_a_did_and_nothing_more(string? value, bool expected)
        => BareDid.IsBareDid(value).Should().Be(expected);

    [Fact]
    public void Bare_did_respects_the_length_bound()
    {
        BareDid.IsBareDid(AtLimit).Should().BeTrue();
        BareDid.IsBareDid(AtLimit + "a").Should().BeFalse("2049 characters is over the FR-API-09 bound even though it is a syntactically valid DID");
    }

    // ---- helpers --------------------------------------------------------------------------

    private static MalformedMessageException Refused(Action act)
    {
        Exception? caught = null;
        try
        {
            act();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull("the body is malformed");
        caught.Should().BeOfType<MalformedMessageException>("a malformed body is never a raw JSON, format or CLR exception");
        caught!.Message.Should().NotContain("SENTINEL", "an error never quotes a value from the message");
        caught.InnerException.Should().BeNull("an inner exception could carry the value the outer text withholds");
        return (MalformedMessageException)caught;
    }

    /// <summary>A message of <paramref name="type"/> whose body is the JSON text given (or absent), read as the receive path reads it.</summary>
    private static Message Raw(string type, string? body)
    {
        var json = body is null
            ? $$"""{"id":"m-1","type":{{JsonSerializer.Serialize(type)}},"from":"{{Did}}","to":["did:example:mediator"]}"""
            : $$"""{"id":"m-1","type":{{JsonSerializer.Serialize(type)}},"from":"{{Did}}","to":["did:example:mediator"],"body":{{body}}}""";
        return JsonSerializer.Deserialize<Message>(json, DidCommJson.Default)!;
    }
}
