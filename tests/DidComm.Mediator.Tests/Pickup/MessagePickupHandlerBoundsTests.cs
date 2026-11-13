using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The bounds on one inbound pickup message, through the real dispatcher (FR-PICK-10, FR-API-09;
/// JC-8, L-055): how many ids a <c>messages-received</c> may carry, how long a thread id a reply
/// will echo, and what a malformed body from an authenticated peer turns into. Each refusal is a
/// <see cref="MalformedMessageException"/> whose text names the member and the rule and never a
/// value from the message, raised before any store is called.
/// </summary>
public sealed class MessagePickupHandlerBoundsTests
{
    // ---- message_id_list ----------------------------------------------------------------------

    [Theory]
    [InlineData(null, 10, 100)] // the default: ten batches' worth
    [InlineData(null, 3, 30)]
    [InlineData(null, 1, 10)]
    [InlineData(5, 5, 5)]
    [InlineData(7, 2, 7)]
    public async Task A_message_id_list_may_carry_MaxAckIds_entries_and_not_one_more(int? maxAckIds, int maxDeliveryBatch, int bound)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync(o =>
        {
            o.MaxDeliveryBatch = maxDeliveryBatch;
            o.MaxAckIds = maxAckIds;
        });
        await h.EnrolAsync(h.Alice, h.Alice);
        var first = await h.QueueAsync(h.Alice, 10, 1);
        var second = await h.QueueAsync(h.Alice, 20, 2);
        h.Fixture.Queue.Coordinator.MaxAckIds.Should().Be(bound);

        // Exactly the bound, the real id last: answered, and the message is gone.
        var atTheBound = Enumerable.Range(0, bound - 1).Select(i => "filler-" + i).Append(first).ToArray();
        (await h.SendAsync(h.MessagesReceived(h.Alice, atTheBound))).Status().MessageCount.Should().Be(1);

        // One more: malformed from the count, the store not called, nothing removed.
        h.ResetTraces();
        var overTheBound = Enumerable.Range(0, bound).Select(i => "filler-" + i).Prepend(second).ToArray();
        var thrown = await Record.ExceptionAsync(() => h.SendAsync(h.MessagesReceived(h.Alice, overTheBound)));

        thrown.Should().BeOfType<MalformedMessageException>()
            .Which.Message.Should().Contain("message_id_list").And.Contain(bound.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .And.NotContain(second).And.NotContain("filler");
        h.StoreTrace.Should().BeEmpty();
        h.MediationTrace.Should().BeEmpty();
        (await h.DeliverableAsync(h.Alice)).Should().Equal(second);
    }

    public static TheoryData<string, int, string> PoisonedLists => new()
    {
        // which entry is itself invalid, where it sits (0-based) in a list of bound + 1 = 11, and its JSON
        { "the entry beyond the bound is a number", 10, "7" },
        { "the entry beyond the bound is an empty string", 10, "\"\"" },
        { "the entry beyond the bound is null", 10, "null" },
        { "the entry beyond the bound is an object", 10, "{\"id\":\"x\"}" },
        { "the entry beyond the bound is over 2048 characters", 10, "<LONG>" },
        { "the first entry is a number", 0, "7" },
        { "the first entry is over 2048 characters", 0, "<LONG>" },
        { "an entry in the middle is an array", 5, "[\"x\"]" },
    };

    [Theory]
    [MemberData(nameof(PoisonedLists))]
    public async Task An_over_long_message_id_list_is_refused_from_its_length_whatever_its_entries_hold(string what, int position, string poisonJson)
    {
        // L-055: the bound is applied before the work it bounds. The proof is a list over the bound
        // with an entry that would fail on its own: the refusal must be the count, so no entry —
        // not even one inside the bound — was read first.
        await using var h = await MessagePickupHandlerHarness.CreateAsync(o => o.MaxDeliveryBatch = 1);
        await h.EnrolAsync(h.Alice, h.Alice);
        var id = await h.QueueAsync(h.Alice, 10);
        var poison = poisonJson == "<LONG>" ? "\"" + new string('S', 2049) + "\"" : poisonJson;
        h.ResetTraces();

        JsonObject List(int length)
        {
            var entries = Enumerable.Range(0, length).Select(i => i == Math.Min(position, length - 1) ? poison : "\"" + (i == 1 ? id : "id-" + i) + "\"");
            return JsonNode.Parse("{\"message_id_list\":[" + string.Join(",", entries) + "]}")!.AsObject();
        }

        var overTheBound = await Record.ExceptionAsync(() => h.SendAsync(h.Raw("messages-received", h.Alice, List(11))));
        overTheBound.Should().BeOfType<MalformedMessageException>(what)
            .Which.Message.Should().Be("messages-received 'message_id_list' has more than 10 entries.");

        // The control: the same poisoned entry in a list within the bound is refused for what it is.
        var withinTheBound = await Record.ExceptionAsync(() => h.SendAsync(h.Raw("messages-received", h.Alice, List(10))));
        withinTheBound.Should().BeOfType<MalformedMessageException>(what)
            .Which.Message.Should().Contain("message_id_list").And.NotContain("more than").And.NotContain("SSSS").And.NotContain(id);

        h.StoreTrace.Should().BeEmpty("neither refusal reaches the store");
        (await h.DeliverableAsync(h.Alice)).Should().Equal(id);
    }

    [Fact]
    public async Task Twenty_thousand_ids_are_refused_like_eleven()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var id = await h.QueueAsync(h.Alice, 10);
        var ids = Enumerable.Range(0, 20_000).Select(i => i == 0 ? id : "x").ToArray();
        h.ResetTraces();

        var thrown = await Record.ExceptionAsync(() => h.SendAsync(h.MessagesReceived(h.Alice, ids)));

        thrown.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Be("messages-received 'message_id_list' has more than 100 entries.");
        h.StoreTrace.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task The_id_bound_applies_to_a_peer_without_a_grant_too()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        h.ResetTraces();

        var thrown = await Record.ExceptionAsync(() => h.SendAsync(h.MessagesReceived(h.Alice, Enumerable.Repeat("x", 101).ToArray())));

        thrown.Should().BeOfType<MalformedMessageException>();
        h.StoreTrace.Should().BeEmpty();
        h.MediationTrace.Should().BeEmpty();
    }

    // ---- the thread id a reply echoes ---------------------------------------------------------

    public static TheoryData<string, bool> ThreadCells
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var request in new[] { "status-request", "delivery-request", "messages-received", "live-delivery-change{true}", "live-delivery-change{false}" })
            {
                data.Add(request, true);
                data.Add(request, false);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ThreadCells))]
    public async Task A_thread_id_of_512_characters_is_echoed_and_one_of_513_makes_the_request_malformed(string request, bool threadIsItsOwnThid)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2) };
        var http = h.Http(h.Alice);
        var t0 = h.Start.ToUnixTimeSeconds();
        var times = new MessagePickupHandlerTimes(0, t0, t0);

        // The thread of a request is its thid if it has one, else its id (JC-8).
        Message Build(string thread)
        {
            var (name, body) = request switch
            {
                "status-request" => ("status-request", new JsonObject()),
                "delivery-request" => ("delivery-request", new JsonObject { ["limit"] = 1 }),
                "messages-received" => ("messages-received", new JsonObject { ["message_id_list"] = new JsonArray(ids[1]) }),
                "live-delivery-change{true}" => ("live-delivery-change", new JsonObject { ["live_delivery"] = true }),
                _ => ("live-delivery-change", new JsonObject { ["live_delivery"] = false }),
            };
            return threadIsItsOwnThid ? h.Raw(name, h.Alice, body, thid: thread) : h.Raw(name, h.Alice, body, id: thread);
        }

        // 513: malformed, the value not echoed, and nothing done — the store is not even read.
        h.ResetTraces();
        var tooLong = new string('T', 513);
        var socket = h.Live(h.Alice);
        var thrown = await Record.ExceptionAsync(() => h.SendAsync(Build(tooLong), request.StartsWith("live", StringComparison.Ordinal) ? socket : http));

        thrown.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Contain("thread id").And.Contain("512").And.NotContain("TTTT");
        h.StoreTrace.Should().BeEmpty();
        h.MediationTrace.Should().BeEmpty();
        http.Attempts.Should().BeEmpty();
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids, "nothing was leased or acknowledged");
        (await h.LiveFlagAsync(socket)).Should().BeFalse("live delivery was not turned on");

        // 512: answered on that thread.
        var longest = new string('t', 512);
        var message = Build(longest);
        var outcome = await h.SendAsync(message, http);
        switch (request)
        {
            case "status-request":
                outcome.ShouldBeStatus(message, h.Mediator, h.Alice, 2, 30, live: false, times: times);
                break;
            case "delivery-request":
                outcome.ShouldBeNoReply();
                http.Sent.Should().ContainSingle().Which.ShouldBeDelivery(message, h.Mediator, h.Alice, null, new[] { ids[0] });
                http.Sent.Single().Thid.Should().Be(longest);
                break;
            case "messages-received":
                outcome.ShouldBeStatus(message, h.Mediator, h.Alice, 1, 10, live: false, times: times);
                break;
            case "live-delivery-change{true}":
                outcome.ShouldBeLiveModeNotSupported(message, h.Mediator, h.Alice);
                outcome.Reply!.Pthid.Should().Be(longest);
                break;
            default:
                outcome.ShouldBeStatus(message, h.Mediator, h.Alice, 2, 30, live: false, times: times);
                break;
        }
        if (outcome.Reply is { Thid: not null } reply)
            reply.Thid.Should().Be(longest);
    }

    [Fact]
    public async Task Only_the_thread_id_is_held_to_the_echo_bound_so_a_long_id_under_a_short_thid_is_answered()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);

        var request = h.Raw("status-request", h.Alice, new JsonObject(), id: new string('i', 5000), thid: "short-thread");
        var outcome = await h.SendAsync(request);

        outcome.ShouldBeStatus(request, h.Mediator, h.Alice, 0, 0, live: false);
        outcome.Reply!.Thid.Should().Be("short-thread");
    }

    // ---- malformed bodies from an authenticated peer -----------------------------------------

    public static TheoryData<string, string?, string> MalformedBodies => new()
    {
        // message name, body as JSON text (null: no body), the member the refusal must name
        { "status-request", """{"recipient_did":7}""", "recipient_did" },
        { "status-request", """{"recipient_did":""}""", "recipient_did" },
        { "status-request", """{"recipient_did":true}""", "recipient_did" },
        { "status-request", """{"recipient_did":["did:example:SECRET"]}""", "recipient_did" },
        { "status-request", """{"recipient_did":{"did":"did:example:SECRET"}}""", "recipient_did" },
        { "status-request", """{"recipient_did":"<LONG>"}""", "recipient_did" },

        { "delivery-request", null, "limit" },
        { "delivery-request", """{}""", "limit" },
        { "delivery-request", """{"recipient_did":"did:example:SECRET"}""", "limit" },
        { "delivery-request", """{"limit":"10"}""", "limit" },
        { "delivery-request", """{"limit":"SECRET"}""", "limit" },
        { "delivery-request", """{"limit":-1}""", "limit" },
        { "delivery-request", """{"limit":-2147483649}""", "limit" },
        { "delivery-request", """{"limit":-1e400}""", "limit" },
        { "delivery-request", """{"limit":1.5}""", "limit" },
        { "delivery-request", """{"limit":1e-1}""", "limit" },
        { "delivery-request", """{"limit":0.0000000000000000000000000001}""", "limit" },
        { "delivery-request", """{"limit":true}""", "limit" },
        { "delivery-request", """{"limit":null}""", "limit" },
        { "delivery-request", """{"limit":[10]}""", "limit" },
        { "delivery-request", """{"limit":{"value":10}}""", "limit" },
        { "delivery-request", """{"limit":10,"recipient_did":7}""", "recipient_did" },
        { "delivery-request", """{"limit":10,"recipient_did":""}""", "recipient_did" },
        { "delivery-request", """{"limit":10,"recipient_did":"<LONG>"}""", "recipient_did" },
        { "delivery-request", """{"limit":0,"recipient_did":"<LONG>"}""", "recipient_did" },

        { "messages-received", null, "message_id_list" },
        { "messages-received", """{}""", "message_id_list" },
        { "messages-received", """{"message_id_list":7}""", "message_id_list" },
        { "messages-received", """{"message_id_list":"SECRET"}""", "message_id_list" },
        { "messages-received", """{"message_id_list":{"0":"SECRET"}}""", "message_id_list" },
        { "messages-received", """{"message_id_list":null}""", "message_id_list" },
        { "messages-received", """{"message_id_list":["ok",7]}""", "message_id_list" },
        { "messages-received", """{"message_id_list":["ok",""]}""", "message_id_list" },
        { "messages-received", """{"message_id_list":["ok",null]}""", "message_id_list" },
        { "messages-received", """{"message_id_list":["ok",["SECRET"]]}""", "message_id_list" },
        { "messages-received", """{"message_id_list":["<LONG>"]}""", "message_id_list" },
        { "messages-received", """{"message_ids":["ok"]}""", "message_id_list" },

        { "live-delivery-change", null, "live_delivery" },
        { "live-delivery-change", """{}""", "live_delivery" },
        { "live-delivery-change", """{"live_delivery":"true"}""", "live_delivery" },
        { "live-delivery-change", """{"live_delivery":"SECRET"}""", "live_delivery" },
        { "live-delivery-change", """{"live_delivery":1}""", "live_delivery" },
        { "live-delivery-change", """{"live_delivery":0}""", "live_delivery" },
        { "live-delivery-change", """{"live_delivery":null}""", "live_delivery" },
        { "live-delivery-change", """{"live_delivery":[true]}""", "live_delivery" },
        { "live-delivery-change", """{"live-delivery":true}""", "live_delivery" },
    };

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task A_malformed_body_from_an_enrolled_peer_is_a_MalformedMessageException_that_quotes_nothing_and_changes_nothing(string name, string? bodyJson, string member)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2) };
        var socket = h.Live(h.Alice);
        var body = bodyJson is null ? null : JsonNode.Parse(bodyJson.Replace("<LONG>", new string('S', 2049), StringComparison.Ordinal))!.AsObject();
        h.ResetTraces();

        var thrown = await Record.ExceptionAsync(() => h.SendAsync(h.Raw(name, h.Alice, body), socket));

        var malformed = thrown.Should().BeOfType<MalformedMessageException>().Subject;
        malformed.Message.Should().Contain(name).And.Contain("'" + member + "'");
        malformed.Message.Should().NotContain("SECRET").And.NotContain("SSSS").And.NotContain("did:");
        malformed.InnerException.Should().BeNull("a cause could carry the value its text left out");
        malformed.Message.Length.Should().BeLessThan(200);

        h.StoreTrace.Should().BeEmpty("a malformed request reaches no store");
        h.MediationTrace.Should().BeEmpty();
        socket.Attempts.Should().BeEmpty();
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids);
        (await h.LiveFlagAsync(socket)).Should().BeFalse();
    }

    [Fact]
    public async Task A_malformed_request_is_malformed_whoever_sends_it_but_only_with_an_authcrypt_layer()
    {
        // A peer without a grant gets the same refusal (the body is checked before any store is
        // read), so the refusal says nothing about enrolment.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var stranger = await h.NewDidAsync();
        h.ResetTraces();

        var thrown = await Record.ExceptionAsync(() => h.SendAsync(PickupMessages.Raw(MessagePickup.DeliveryRequestType, stranger, h.Mediator, new JsonObject { ["limit"] = -1 })));

        thrown.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Be("delivery-request 'limit' must not be negative.");
        h.StoreTrace.Should().BeEmpty();
        h.MediationTrace.Should().BeEmpty();
    }

    [Fact]
    public async Task A_recipient_did_at_the_length_limit_is_looked_up_and_echoed_and_one_character_more_is_malformed()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var atTheLimit = "did:example:" + new string('a', 2048 - "did:example:".Length);
        h.ResetTraces();

        var request = h.StatusRequest(h.Alice, atTheLimit);
        (await h.SendAsync(request)).ShouldBeStatus(request, h.Mediator, h.Alice, 0, 0, live: false, recipientDid: atTheLimit);
        h.Mediation.Reads.Should().Equal(("FindGrantByRecipientDidAsync", atTheLimit));

        h.ResetTraces();
        var over = h.Raw("status-request", h.Alice, new JsonObject { ["recipient_did"] = atTheLimit + "a" });
        var thrown = await Record.ExceptionAsync(() => h.SendAsync(over));
        thrown.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Contain("2048").And.NotContain("aaaa");
        h.MediationTrace.Should().BeEmpty("an over-long value is never looked up");
    }
}
