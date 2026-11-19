using System.Text.Json;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Xunit;

namespace DidComm.InteropTests.Adv98.D4;

/// <summary>
/// #98 D4 attack 3: <c>live-delivery-change{true}</c> sent on a WebSocket session whose
/// <c>return_route</c> is downgraded (mode None: after a <c>none</c>, a <c>thread</c> or a malformed
/// value, FR-RR-05). The status that must answer it (FR-PICK-07) cannot travel on that session, and
/// the session takes no pushes. FR-PICK-07 keeps the live flag off on "a connection incapable of
/// live delivery"; FR-PICK-06 marks the push start at "the status that answers the
/// live-delivery-change". A downgraded session is incapable for as long as it stays downgraded, and
/// the status that would carry the mark is never written. Expected: the change is not applied (the
/// recipient's next status on the socket still reports <c>live_delivery: false</c>).
/// </summary>
public sealed class D4LiveChangeOnDowngradedSessionTests
{
    public static TheoryData<string> Shapes => new()
    {
        "header-less", // after a none, the change carries no return_route (the mode stays None)
        "none",        // the change itself carries return_route: none
        "thread",      // the change carries return_route: thread (+ a thread id): None in this version
        "malformed",   // the change carries a return_route value nobody defines: None
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task D4_A_live_delivery_change_on_a_downgraded_session_is_not_applied_since_its_status_cannot_travel(string shape)
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var (socket, opened) = await mediator.OpenAsync(recipient);
        using var disposeSocket = socket;
        opened.LiveDelivery.Should().BeFalse();
        var session = mediator.Host.Registry.Find(recipient.Did).Should().ContainSingle().Subject;

        // The recipient downgrades the socket (a none on a status-request that is then not answered).
        var none = mediator.Prepare(MessagePickup.CreateStatusRequest(recipient.Did, mediator.Mediator), returnRoute: false);
        ReturnRouteHeaderWriter.Write(none, ReturnRouteMode.None, thread: null);
        await ReturnRouteTestHost.SendAsync(socket, await mediator.Host.AuthcryptAsync(recipient, none));
        (await ReturnRouteTestHost.WaitUntilAsync(() => session.Mode == ReturnRouteMode.None, PickupEndpointHost.Timeout)).Should().BeTrue();
        var counted = await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.Queued);

        // live-delivery-change{true} while the session is in mode None.
        var change = mediator.Prepare(MessagePickup.CreateLiveDeliveryChange(recipient.Did, mediator.Mediator, true), returnRoute: false);
        switch (shape)
        {
            case "none":
                ReturnRouteHeaderWriter.Write(change, ReturnRouteMode.None, thread: null);
                break;
            case "thread":
                ReturnRouteHeaderWriter.Write(change, ReturnRouteMode.Thread, thread: "d4-thread");
                break;
            case "malformed":
                (change.AdditionalHeaders ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal))["return_route"] = JsonSerializer.SerializeToElement("everything");
                break;
        }
        await ReturnRouteTestHost.SendAsync(socket, await mediator.Host.AuthcryptAsync(recipient, change));

        // return_route all again. The receive loop handles messages in order and the writer is
        // first-in first-out, so the first frame is this request's answer and proves the change
        // above was processed and nothing (neither its status nor a push) was written meanwhile.
        var status = await mediator.StatusAsync(socket, recipient);
        var afterOutcome = (await mediator.Queue.DeliverAsync(recipient.Did, (await mediator.PackFromAliceAsync(recipient)).Payload)).Outcome;
        Console.WriteLine($"D4 downgraded/{shape}: status after all: live_delivery={status.LiveDelivery} message_count={status.MessageCount}; next arrival {afterOutcome}; counted {counted.Id} stays for pickup");

        status.MessageCount.Should().Be(1, "the message queued while the session was downgraded waits for pickup");
        status.LiveDelivery.Should().BeFalse("the change arrived on a session that could carry neither its status nor a push (FR-PICK-07: the live flag stays off)");
        afterOutcome.Should().Be(PickupDeliveryOutcome.Queued, "live delivery was never turned on in a way the recipient was told about");
        await mediator.CloseAsync(socket);
    }
}

/// <summary>
/// #98 D4 attack 3, the pickup side: a <c>delivery-request</c> on a downgraded session cannot carry
/// its <c>delivery</c>; whatever it leased must be given back at once, so the next request on the
/// socket (after <c>all</c>) is delivered the same records without waiting for the lease timeout.
/// </summary>
public sealed class D4DeliveryRequestOnDowngradedSessionTests
{
    [Fact]
    public async Task D4_A_delivery_request_on_a_downgraded_session_leaves_nothing_leased_behind()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.Queued);
        var (socket, _) = await mediator.OpenAsync(recipient);
        using var disposeSocket = socket;
        var session = mediator.Host.Registry.Find(recipient.Did).Should().ContainSingle().Subject;

        var none = mediator.Prepare(MessagePickup.CreateStatusRequest(recipient.Did, mediator.Mediator), returnRoute: false);
        ReturnRouteHeaderWriter.Write(none, ReturnRouteMode.None, thread: null);
        await ReturnRouteTestHost.SendAsync(socket, await mediator.Host.AuthcryptAsync(recipient, none));
        (await ReturnRouteTestHost.WaitUntilAsync(() => session.Mode == ReturnRouteMode.None, PickupEndpointHost.Timeout)).Should().BeTrue();

        // A header-less delivery-request while downgraded: its delivery cannot be written.
        await mediator.SendAsync(socket, recipient, MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10), returnRoute: false);

        // all again, on the frozen clock: the first frame is this status, then the fetch.
        var status = await mediator.StatusAsync(socket, recipient);
        var fetch = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        var answer = await mediator.AskAsync(socket, recipient, fetch);
        Console.WriteLine($"D4 downgraded delivery-request: status message_count={status.MessageCount}; next fetch answered {answer.Message.Type}");
        answer.Message.Type.Should().Be(MessagePickup.DeliveryType, "the unwritten delivery must not keep the record leased for LeaseTimeout");
        answer.Delivery(thid: fetch.Id).Should().ContainSingle().Which.Id.Should().Be(queued.Id);
        await mediator.CloseAsync(socket);
    }
}
