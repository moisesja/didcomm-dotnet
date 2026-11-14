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
/// The internal Coordinate Mediation 2.0 codec (#96 design D-12): what its builders refuse and
/// emit, what its parsers accept and refuse, the list bounds applied from the length before any
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
