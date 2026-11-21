using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using static DidComm.Mediator.Tests.Pickup.MessagePickupWireSupport;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The Message Pickup 4.0 requests of the handler suites (#100), built with the internal 4.0 codec
/// for the harness's mediator, and the way a <c>status</c> or a <c>delivery</c> of 4.0 must look.
/// </summary>
internal static class MessagePickup40Requests
{
    public static Message StatusRequest40(this MessagePickupHandlerHarness h, string from, string? recipientDid = null, string? thid = null)
        => WithThread(MessagePickup40.CreateStatusRequest(from, h.Mediator, recipientDid), thid);

    public static Message DeliveryRequest40(this MessagePickupHandlerHarness h, string from, int limit = 10, string? recipientDid = null, string? thid = null)
        => WithThread(MessagePickup40.CreateDeliveryRequest(from, h.Mediator, limit, recipientDid), thid);

    public static Message MessagesReceived40(this MessagePickupHandlerHarness h, string from, IReadOnlyList<string> messageIds, string? thid = null)
        => WithThread(MessagePickup40.CreateMessagesReceived(from, h.Mediator, messageIds), thid);

    public static Message LiveChange40(this MessagePickupHandlerHarness h, string from, bool liveDelivery, string? thid = null)
        => WithThread(MessagePickup40.CreateLiveDeliveryChange(from, h.Mediator, liveDelivery), thid);

    /// <summary>A 4.0 message of any name with any body, as a sloppy or hostile peer writes it.</summary>
    public static Message Raw40(this MessagePickupHandlerHarness h, string name, string from, JsonObject? body, string? id = null, string? thid = null)
        => PickupMessages.Raw(MessagePickup40.ProtocolUri + "/" + name, from, h.Mediator, body, id, thid);

    /// <summary>Put <c>return_route: all</c> on <paramref name="message"/>, as a recipient that follows 4.0 L69 does.</summary>
    public static Message WithReturnRouteAll(this Message message)
    {
        message.AdditionalHeaders ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        message.AdditionalHeaders["return_route"] = JsonSerializer.SerializeToElement("all");
        return message;
    }

    private static Message WithThread(Message message, string? thid)
    {
        if (thid is not null)
            message.Thid = thid;
        return message;
    }
}

/// <summary>The two time members of a 4.0 <c>status</c>, in UTC epoch milliseconds.</summary>
internal readonly record struct MessagePickup40Times(long NewestReceivedTimeMs, long OldestReceivedTimeMs);

/// <summary>What the 4.0 mediator role must put on the wire, asserted member by member.</summary>
internal static class MessagePickup40Assert
{
    public const string StatusType = "https://didcomm.org/message-pickup/4.0/status";
    public const string DeliveryType = "https://didcomm.org/message-pickup/4.0/delivery";
    public const string ProblemReportType = "https://didcomm.org/message-pickup/4.0/problem-report";

    /// <summary>What <see cref="MessagePickupHandlerAssert.Members"/> gives for a 4.0 <c>status</c> with these values: never <c>longest_waited_seconds</c>.</summary>
    public static string StatusMembers(int count, long bytes, bool live, string? recipientDid = null, MessagePickup40Times? times = null)
    {
        var members = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["message_count"] = count.ToString(CultureInfo.InvariantCulture),
            ["total_bytes"] = bytes.ToString(CultureInfo.InvariantCulture),
            ["live_delivery"] = live ? "true" : "false",
        };
        if (recipientDid is not null)
            members["recipient_did"] = JsonValue.Create(recipientDid).ToJsonString();
        if (times is { } t)
        {
            members["newest_received_time"] = t.NewestReceivedTimeMs.ToString(CultureInfo.InvariantCulture);
            members["oldest_received_time"] = t.OldestReceivedTimeMs.ToString(CultureInfo.InvariantCulture);
        }
        return string.Join("; ", members.Select(p => p.Key + "=" + p.Value));
    }

    /// <summary>
    /// The reply is a 4.0 <c>status</c> on the request's thread, from the DID that decrypted the
    /// request to the peer, with exactly these body members.
    /// </summary>
    public static void ShouldBeStatus40(this DispatchOutcome outcome, Message request, string mediator, string peer, int count, long bytes, bool live, string? recipientDid = null, MessagePickup40Times? times = null)
    {
        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        outcome.Handler.Should().BeOfType<MessagePickup40Handler>();
        var reply = outcome.Reply!;
        reply.Type.Should().Be(StatusType);
        reply.Thid.Should().Be(request.Thid ?? request.Id, "a 4.0 status always carries the request's thread (4.0 L33)");
        reply.Pthid.Should().BeNull();
        reply.From.Should().Be(mediator, "the reply comes from the DID that decrypted the request");
        reply.To.Should().Equal(peer);
        reply.Id.Should().NotBe(request.Id);
        reply.CreatedTime.Should().BeNull("the session stamps created_time when it sends; the handler does not");
        AttachmentsOf(reply).Should().BeNull("a status carries no attachments");
        MessagePickupHandlerAssert.Members(reply).Should().Be(StatusMembers(count, bytes, live, recipientDid, times));
    }

    /// <summary>
    /// The reply is the empty 4.0 <c>delivery</c> of L210/L259: on the request's thread, an
    /// <c>attachments</c> array that is present and empty, and <c>recipient_did</c> exactly when the
    /// request had one.
    /// </summary>
    public static void ShouldBeEmptyDelivery40(this DispatchOutcome outcome, Message request, string mediator, string peer, string? recipientDid)
    {
        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        outcome.Handler.Should().BeOfType<MessagePickup40Handler>();
        var reply = outcome.Reply!;
        reply.Type.Should().Be(DeliveryType, "4.0 answers \"nothing to deliver\" with an empty delivery, never a status (L29, L210)");
        reply.Thid.Should().Be(request.Thid ?? request.Id);
        reply.Pthid.Should().BeNull();
        reply.From.Should().Be(mediator);
        reply.To.Should().Equal(peer);
        reply.Id.Should().NotBe(request.Id);
        reply.CreatedTime.Should().BeNull();
        AttachmentsOf(reply).Should().Be("[]", "the attachments array is written, empty (L259)");
        MessagePickupHandlerAssert.Members(reply).Should().Be(recipientDid is null ? string.Empty : "recipient_did=" + JsonValue.Create(recipientDid).ToJsonString());
        MessagePickup40.TryParseDelivery(RoundTrip(reply), out var messages, out var echoed).Should().BeTrue();
        messages.Should().BeEmpty();
        echoed.Should().Be(recipientDid);
    }

    /// <summary>
    /// <paramref name="pushed"/> is a 4.0 <c>delivery</c> carrying exactly these messages in this
    /// order: on <paramref name="request"/>'s thread, or — for a live push, <paramref name="request"/>
    /// <c>null</c> — on none (L34, L263); and <c>recipient_did</c> only when the request had one.
    /// </summary>
    public static void ShouldBeDelivery40(this Message pushed, Message? request, string mediator, string peer, string? recipientDid, IReadOnlyList<string> ids, IReadOnlyList<byte[]>? payloads = null)
    {
        pushed.Type.Should().Be(DeliveryType);
        pushed.Thid.Should().Be(request is null ? null : request.Thid ?? request.Id);
        pushed.Pthid.Should().BeNull();
        pushed.From.Should().Be(mediator);
        pushed.To.Should().Equal(peer);
        if (request is not null)
            pushed.Id.Should().NotBe(request.Id);
        pushed.CreatedTime.Should().BeNull("the session stamps created_time when it sends; the handler does not");
        MessagePickupHandlerAssert.Members(pushed).Should().Be(recipientDid is null ? string.Empty : "recipient_did=" + JsonValue.Create(recipientDid).ToJsonString());
        MessagePickup40.TryParseDelivery(RoundTrip(pushed), out var messages, out var echoed).Should().BeTrue();
        echoed.Should().Be(recipientDid);
        messages!.Select(m => m.Id).Should().Equal(ids);
        if (payloads is not null)
        {
            for (var i = 0; i < payloads.Count; i++)
                messages![i].Payload.ToArray().Should().Equal(payloads[i]);
        }
    }

    /// <summary>The reply is the 4.0 problem report of L342-L369, on the request's thread as <c>pthid</c>.</summary>
    public static void ShouldBeLiveModeNotSupported40(this DispatchOutcome outcome, Message request, string mediator, string peer)
    {
        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        outcome.Handler.Should().BeOfType<MessagePickup40Handler>();
        var report = outcome.Reply!;
        report.Type.Should().Be(ProblemReportType, "4.0 has its own problem report type (L362)");
        report.Pthid.Should().Be(request.Thid ?? request.Id, "pthid = the request's thid if present, else its id");
        report.Thid.Should().BeNull();
        report.From.Should().Be(mediator);
        report.To.Should().Equal(peer);
        report.Id.Should().NotBe(request.Id);
        MessagePickupHandlerAssert.Members(report).Should().Be("code=\"e.m.live-mode-not-supported\"; comment=\"Connection does not support Live Delivery\"");
    }

    /// <summary>The 4.0 handler took the request and answered nothing.</summary>
    public static void ShouldBeNoReply40(this DispatchOutcome outcome)
    {
        outcome.Result.Should().Be(DispatchResult.NoReply);
        outcome.Reply.Should().BeNull();
        outcome.Handler.Should().BeOfType<MessagePickup40Handler>();
    }

    /// <summary>The parsed 4.0 <c>status</c> of a reply that must be one.</summary>
    public static PickupStatus40 Status40(this DispatchOutcome outcome)
    {
        outcome.Reply.Should().NotBeNull();
        MessagePickup40.TryParseStatus(outcome.Reply!, out var status).Should().BeTrue();
        return status!;
    }
}
