using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// Red-team pass ADV-3 over Message Pickup 4.0 (#100): replay scope by spelling. A 4.0
/// <c>messages-received</c> travels without <c>return_route</c> (4.0 L35), so only its protocol
/// puts it inside FR-RR-08 replay admission (and makes the sender stamp <c>created_time</c>,
/// FR-RR-09). If any spelling of the type reached the 4.0 handler and was acted on while falling
/// outside that scope, a captured acknowledgement, delivery-request or live-delivery-change could be
/// played again. Held: the registry, the handler's own type test and the scope use one comparison.
/// </summary>
public sealed class MessagePickup40AdversarialReplayScopeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Every_type_the_4_0_handler_acts_on_is_inside_replay_admission_and_the_senders_stamping_scope()
    {
        await using var fixture = PickupFixture.Create();
        var registry = new ProtocolHandlerRegistry();
        registry.Register(new MessagePickupHandler(fixture.Queue));
        registry.Register(new MessagePickup40Handler(fixture.Queue));

        var acting = new[] { MessagePickup40.StatusRequestUri, MessagePickup40.DeliveryRequestUri, MessagePickup40.MessagesReceivedUri, MessagePickup40.LiveDeliveryChangeUri };
        var docs = new[] { "https://didcomm.org", "HTTPS://DIDCOMM.ORG", "https://DIDComm.org", "https://didcomm.org/", "http://didcomm.org", "https://dıdcomm.org" };
        var protocols = new[]
        {
            "message-pickup", "messagepickup", "Message_Pickup", "MESSAGE-PICKUP", "message__pickup", "-message-pickup-",
            "messagepıckup", "meſſage-pickup", "message-picKup", "message-pic" + (char)0x212A + "up", "message-pickup ", "message.pickup",
        };
        var names = new[]
        {
            "messages-received", "Messages_Received", "MESSAGESRECEIVED", "messages-receıved", "meſſages-received", "messages-received-",
            "delivery-request", "Delivery_Request", "status-request", "statusrequest", "live-delivery-change", "LIVE_DELIVERY_CHANGE",
        };

        var routed = 0;
        var acted = 0;
        var nonAscii = 0;
        foreach (var doc in docs)
            foreach (var protocol in protocols)
                for (var minor = 0; minor <= 12; minor++)
                    foreach (var name in names)
                    {
                        var type = $"{doc}/{protocol}/4.{minor}/{name}";
                        if (!registry.TryResolve(type, out var handler) || handler is not MessagePickup40Handler)
                            continue;
                        routed++;
                        MediatorServiceProtocols.IsMediatorServiceType(type).Should().BeTrue(type);
                        MediatorServiceProtocols.IsReplayExempt(type).Should().BeFalse(type);
                        OutgoingCreatedTime.IsInScope(new MessageBuilder().WithType(type).Build()).Should().BeTrue("a sender stamps every type the receiver admits ({0})", type);
                        if (!MessageTypeUri.TryParse(type, out var parsed) || !acting.Any(a => a.Matches(parsed!)))
                            continue;
                        acted++;
                        if (type.Any(c => c > 127))
                            nonAscii++;
                    }
        output.WriteLine($"routed to 4.0: {routed}; acted on: {acted}; of which with non-ASCII look-alikes: {nonAscii}");
        routed.Should().BeGreaterThan(1000);
        acted.Should().BeGreaterThan(500);
        nonAscii.Should().Be(0, "ordinal case-insensitive matching folds no dotless i, long s or Kelvin sign into ASCII, so no look-alike reaches the handler");

        // One envelope end to end: a sloppy spelling of the acknowledgement, authcrypted, without
        // return_route, is in the replay scope, and routed to the 4.0 handler; a look-alike is neither.
        var mediator = await fixture.NewPeerAsync();
        var bob = await fixture.NewPeerAsync();
        foreach (var (type, routes) in new[] { ("https://DIDComm.org/Message_Pickup/4.0/Messages_Received", true), ("https://didcomm.org/meſſage-pickup/4.0/messages-received", false) })
        {
            var message = PickupMessages.Raw(type, bob.Did, mediator.Did, new JsonObject { ["message_id_list"] = new JsonArray("q1") });
            var received = await fixture.ReceiveAsync(message, mediator.Did);
            (registry.TryResolve(type, out var routedTo) && routedTo is MessagePickup40Handler).Should().Be(routes, type);
            InboundReplayGate.IsInScope(received, ReturnRouteHeader.None).Should().Be(routes, "inside replay admission exactly when routed to the 4.0 handler ({0})", type);
        }
    }
}
