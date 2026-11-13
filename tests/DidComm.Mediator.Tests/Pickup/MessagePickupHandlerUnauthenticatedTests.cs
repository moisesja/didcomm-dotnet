using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Protocols;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// FR-PICK-01 through the real dispatcher: every envelope shape against every request. A request
/// without an authcrypt layer is dropped before its type or body is read — nothing applied, no
/// reply, no store call, one rate-limited log line with the id — and every shape that has an
/// authcrypt layer is answered.
/// </summary>
public sealed class MessagePickupHandlerUnauthenticatedTests
{
    private const string StatusRequest = "status-request";
    private const string DeliveryRequest = "delivery-request";
    private const string MessagesReceived = "messages-received";
    private const string LiveOn = "live-delivery-change{true}";
    private const string LiveOff = "live-delivery-change{false}";

    private static readonly string[] Requests = { StatusRequest, DeliveryRequest, MessagesReceived, LiveOn, LiveOff };
    private static readonly HandlerEnvelope[] Unauthenticated = { HandlerEnvelope.Plaintext, HandlerEnvelope.Anoncrypt, HandlerEnvelope.SignedOnly, HandlerEnvelope.AnoncryptOfSigned };
    private static readonly HandlerEnvelope[] Authenticated = { HandlerEnvelope.Authcrypt, HandlerEnvelope.AuthcryptOfSigned, HandlerEnvelope.AnoncryptOfAuthcrypt };

    public static TheoryData<HandlerEnvelope, string> UnauthenticatedCells => Cross(Unauthenticated);

    public static TheoryData<HandlerEnvelope, string> AuthenticatedCells => Cross(Authenticated);

    private static TheoryData<HandlerEnvelope, string> Cross(HandlerEnvelope[] shapes)
    {
        var data = new TheoryData<HandlerEnvelope, string>();
        foreach (var shape in shapes)
        {
            foreach (var request in Requests)
                data.Add(shape, request);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(UnauthenticatedCells))]
    public async Task A_request_without_an_authcrypt_layer_is_neither_applied_nor_answered_and_no_store_is_called(HandlerEnvelope shape, string request)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2), await h.QueueAsync(h.Alice, 30, 3) };
        // The best case for the sender: a live session of the right pair is offered with the request.
        var socket = h.Live(h.Alice);
        // For "turn it off" the flag must be on beforehand, so a flip would show.
        var liveBefore = request == LiveOff;
        if (liveBefore)
            (await h.SendAsync(h.LiveChange(h.Alice, true), socket)).Status().LiveDelivery.Should().BeTrue();
        h.ResetTraces();
        var logged = h.Fixture.PickupLogs.Count;

        var message = request switch
        {
            StatusRequest => h.StatusRequest(h.Alice),
            DeliveryRequest => h.DeliveryRequest(h.Alice, 10),
            MessagesReceived => h.MessagesReceived(h.Alice, ids),
            LiveOn => h.LiveChange(h.Alice, true),
            _ => h.LiveChange(h.Alice, false),
        };
        var outcome = await h.SendAsync(message, socket, shape);

        outcome.ShouldBeNoReply();
        h.StoreTrace.Should().BeEmpty("the message store is not even read for an unauthenticated message");
        h.MediationTrace.Should().BeEmpty("the mediation store is not even read for an unauthenticated message");
        socket.Attempts.Should().BeEmpty("nothing is delivered");

        // Nothing was applied: every message is still stored and not under a lease, and the live flag is where it was.
        h.Store.Inner.MessageCount.Should().Be(3, "such a messages-received removes nothing");
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids, "such a delivery-request leases nothing");
        (await h.LiveFlagAsync(socket)).Should().Be(liveBefore, "such a live-delivery-change flips nothing");

        // One line, with the id and nothing else of the message.
        var line = h.Fixture.PickupLogs.Skip(logged).Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Warning, "the first such drop is logged at Warning");
        line.Formatted.Should().Contain(message.Id).And.Contain("FR-PICK-01");
        line.State["MessageId"].Should().Be(message.Id);
        line.State["Count"].Should().Be(1L);
        line.Exception.Should().BeNull();
        line.Everything.Should().NotContain("did:").And.NotContain(ids[0]).And.NotContain(ids[1]).And.NotContain(ids[2]);
    }

    [Theory]
    [InlineData(HandlerEnvelope.Plaintext)]
    [InlineData(HandlerEnvelope.Anoncrypt)]
    [InlineData(HandlerEnvelope.SignedOnly)]
    [InlineData(HandlerEnvelope.AnoncryptOfSigned)]
    public async Task A_malformed_unauthenticated_request_is_dropped_like_a_well_formed_one(HandlerEnvelope shape)
    {
        // Neither the type nor the body is read for an unauthenticated message, so no shape of it can throw.
        await using var h = await MessagePickupHandlerHarness.CreateAsync(o => o.MaxDeliveryBatch = 1);
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.QueueAsync(h.Alice, 10);
        var socket = h.Live(h.Alice);
        h.ResetTraces();
        var longValue = new string('x', 5000);
        var tooMany = new JsonArray();
        for (var i = 0; i < 500; i++)
            tooMany.Add(i % 2 == 0 ? JsonValue.Create(i) : JsonValue.Create("id-" + i));

        var malformed = new[]
        {
            h.Raw("status-request", h.Alice, new JsonObject { ["recipient_did"] = 7 }),
            h.Raw("status-request", h.Alice, new JsonObject { ["recipient_did"] = longValue }),
            h.Raw("delivery-request", h.Alice, body: null),
            h.Raw("delivery-request", h.Alice, new JsonObject { ["limit"] = "ten" }),
            h.Raw("delivery-request", h.Alice, new JsonObject { ["limit"] = -1 }),
            h.Raw("delivery-request", h.Alice, JsonNode.Parse("""{"limit":1.5,"recipient_did":""}""")!.AsObject()),
            h.Raw("messages-received", h.Alice, new JsonObject { ["message_id_list"] = 7 }),
            h.Raw("messages-received", h.Alice, new JsonObject { ["message_id_list"] = tooMany }),
            h.Raw("messages-received", h.Alice, new JsonObject { ["message_id_list"] = new JsonArray(longValue) }),
            h.Raw("live-delivery-change", h.Alice, new JsonObject { ["live_delivery"] = "yes" }),
            h.Raw("live-delivery-change", h.Alice, body: null),
            // A thread id over the echo bound, on a request and on a name nobody defines.
            h.Raw("status-request", h.Alice, new JsonObject(), thid: longValue),
            h.Raw("delivery-request", h.Alice, new JsonObject { ["limit"] = 1 }, id: longValue),
            h.Raw("no-such-message", h.Alice, new JsonObject { ["limit"] = "ten" }),
        };

        foreach (var message in malformed)
            (await h.SendAsync(message, socket, shape)).ShouldBeNoReply();

        h.StoreTrace.Should().BeEmpty();
        h.MediationTrace.Should().BeEmpty();
        socket.Attempts.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(1);
        var lines = h.Fixture.PickupLogs.Where(e => e.Formatted.Contains("FR-PICK-01", StringComparison.Ordinal)).ToArray();
        lines.Should().HaveCount(malformed.Length, "each is dropped at the same place, whatever it carries");
        lines.Should().OnlyContain(e => !e.Everything.Contains("xxxxxxxx") && !e.Everything.Contains("did:"));
        lines.Should().Contain(e => e.Formatted.Contains("(id of 5000 characters)"), "an over-long message id is replaced, not written");
    }

    [Theory]
    [MemberData(nameof(AuthenticatedCells))]
    public async Task Every_envelope_with_an_authcrypt_layer_is_answered(HandlerEnvelope shape, string request)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2) };
        var socket = h.Live(h.Alice);
        var t0 = h.Start.ToUnixTimeSeconds();
        var times = new MessagePickupHandlerTimes(0, t0, t0);

        switch (request)
        {
            case StatusRequest:
            {
                var message = h.StatusRequest(h.Alice);
                (await h.SendAsync(message, socket, shape)).ShouldBeStatus(message, h.Mediator, h.Alice, 2, 30, live: false, times: times);
                break;
            }
            case DeliveryRequest:
            {
                var message = h.DeliveryRequest(h.Alice, 10);
                (await h.SendAsync(message, socket, shape)).ShouldBeNoReply();
                socket.Sent.Should().ContainSingle().Which.ShouldBeDelivery(message, h.Mediator, h.Alice, null, ids);
                break;
            }
            case MessagesReceived:
            {
                var message = h.MessagesReceived(h.Alice, new[] { ids[0] });
                (await h.SendAsync(message, socket, shape)).ShouldBeStatus(message, h.Mediator, h.Alice, 1, 20, live: false, times: times);
                h.Store.Inner.MessageCount.Should().Be(1);
                break;
            }
            case LiveOn:
            {
                var message = h.LiveChange(h.Alice, true);
                (await h.SendAsync(message, socket, shape)).ShouldBeStatus(message, h.Mediator, h.Alice, 2, 30, live: true, times: times);
                break;
            }
            default:
            {
                (await h.SendAsync(h.LiveChange(h.Alice, true), socket)).Status().LiveDelivery.Should().BeTrue();
                var message = h.LiveChange(h.Alice, false);
                (await h.SendAsync(message, socket, shape)).ShouldBeStatus(message, h.Mediator, h.Alice, 2, 30, live: false, times: times);
                (await h.LiveFlagAsync(socket)).Should().BeFalse();
                break;
            }
        }

        h.Fixture.PickupLogs.Should().NotContain(e => e.Formatted.Contains("FR-PICK-01", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_unauthenticated_drop_line_is_rate_limited_across_every_shape_and_request()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.QueueAsync(h.Alice, 10);

        // Sixteen different unauthenticated messages, dispatched in turn: one counter for all of them.
        var received = new List<UnpackResult>();
        foreach (var shape in Unauthenticated)
        {
            received.Add(await h.Fixture.ReceiveAsync(h.StatusRequest(h.Alice), h.Mediator, shape));
            received.Add(await h.Fixture.ReceiveAsync(h.DeliveryRequest(h.Alice, 10), h.Mediator, shape));
            received.Add(await h.Fixture.ReceiveAsync(h.MessagesReceived(h.Alice, new[] { "x" }), h.Mediator, shape));
            received.Add(await h.Fixture.ReceiveAsync(h.LiveChange(h.Alice, true), h.Mediator, shape));
        }
        h.ResetTraces();

        for (var i = 0; i < 2001; i++)
        {
            var outcome = await h.Fixture.Host.Dispatcher.DispatchAsync(received[i % received.Count], h.Fixture.Host.Client, h.Fixture.Host.Options);
            outcome.Result.Should().Be(DispatchResult.NoReply);
        }

        var lines = h.Fixture.PickupLogs.Where(e => e.Formatted.Contains("FR-PICK-01", StringComparison.Ordinal)).ToArray();
        lines.Should().HaveCount(2001);
        lines.Count(e => e.Level == LogLevel.Warning).Should().Be(3, "the 1st, the 1000th and the 2000th");
        lines.Count(e => e.Level == LogLevel.Debug).Should().Be(1998);
        lines[0].Level.Should().Be(LogLevel.Warning);
        lines[1].Level.Should().Be(LogLevel.Debug);
        lines[998].Level.Should().Be(LogLevel.Debug);
        lines[999].Level.Should().Be(LogLevel.Warning);
        lines[1000].Level.Should().Be(LogLevel.Debug);
        lines[1999].Level.Should().Be(LogLevel.Warning);
        lines.Select(e => e.State["Count"]).Should().Equal(Enumerable.Range(1, 2001).Select(n => (object?)(long)n));
        lines.Should().OnlyContain(e => !e.Everything.Contains("did:"));
        h.StoreTrace.Should().BeEmpty();
        h.MediationTrace.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task An_unauthenticated_message_of_an_ignored_name_gets_the_drop_line_not_the_ignored_line()
    {
        // The peer gate comes before the type is looked at, so the same line covers every name.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        _ = h.Fixture.Queue; // built now, so its start-up line is not counted below
        var logged = h.Fixture.PickupLogs.Count;

        (await h.SendAsync(MessagePickup.CreateStatus(h.Alice, h.Mediator, new PickupStatus(1)), shape: HandlerEnvelope.Anoncrypt)).ShouldBeNoReply();
        (await h.SendAsync(h.Raw("no-such-message", h.Alice, null), shape: HandlerEnvelope.SignedOnly)).ShouldBeNoReply();

        var lines = h.Fixture.PickupLogs.Skip(logged).ToArray();
        lines.Should().HaveCount(2).And.OnlyContain(e => e.Formatted.Contains("FR-PICK-01"));
        lines.Select(e => e.Level).Should().Equal(LogLevel.Warning, LogLevel.Debug);
    }

    [Fact]
    public async Task A_stranger_without_a_grant_cannot_tell_the_mediator_role_is_there_without_authcrypt()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var mallory = await h.NewDidAsync();
        h.ResetTraces();

        foreach (var shape in Unauthenticated)
        {
            (await h.SendAsync(MessagePickup.CreateStatusRequest(mallory, h.Mediator), shape: shape)).ShouldBeNoReply();
            (await h.SendAsync(MessagePickup.CreateLiveDeliveryChange(mallory, h.Mediator, true), shape: shape)).ShouldBeNoReply();
        }

        h.StoreTrace.Should().BeEmpty();
        h.MediationTrace.Should().BeEmpty();
    }
}
