using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Mediator.Tests.Adv98.D5;

/// <summary>
/// #98 red team, D5 hypothesis 2: replay-scope parity for Message Pickup. The endpoint's replay
/// gate (FR-RR-08, <see cref="MediatorServiceProtocols.IsMediatorServiceType"/>) and the
/// registry + <see cref="MessagePickupHandler"/> decide separately whether a type is Message
/// Pickup. A spelling the handler acts on but the gate leaves out of scope would make a captured
/// <c>delivery-request</c>, <c>messages-received</c> or <c>live-delivery-change</c> replayable.
/// Coordinate Mediation has this table (CoordinateMediationAdversarialTests); pickup had none.
/// </summary>
public sealed class D5PickupReplayScopeParityTests
{
    private const string Bob = "did:example:bob";
    private const string Mediator = "did:example:mediator";

    /// <summary>(type, the handler acts on it). Each message name of the four the handler reads.</summary>
    public static TheoryData<string, bool> Spellings => new()
    {
        { "https://didcomm.org/messagepickup/3.0/delivery-request", true },
        { "https://didcomm.org/messagepickup/3.0/messages-received", true },
        { "https://didcomm.org/messagepickup/3.0/live-delivery-change", true },
        { "https://didcomm.org/messagepickup/3.0/status-request", true },
        // FR-PROTO-01/02: case, -/_ punctuation, any minor of major 3.
        { "https://didcomm.org/message-pickup/3.0/delivery-request", true },
        { "https://didcomm.org/message_pickup/3.0/messages_received", true },
        { "https://DIDCOMM.org/MessagePickup/3.0/Live-Delivery-Change", true },
        { "HTTPS://didcomm.org/messagepickup/3.0/delivery-request", true },
        { "https://didcomm.org/messagepickup/3.1/delivery-request", true },
        { "https://didcomm.org/messagepickup/3.999999999/messages-received", true },
        { "https://didcomm.org/messagepickup/3.0/deliveryrequest", true },
        // Not acted on.
        { "https://didcomm.org/messagepickup/2.0/delivery-request", false },
        { "https://didcomm.org/messagepickup/4.0/delivery-request", false },
        { "https://didcomm.org/messagepickup/3.0.0/delivery-request", false },
        { "https://didcomm.org/messagepickup/03.0/delivery-request", false },
        { "http://didcomm.org/messagepickup/3.0/delivery-request", false },
        { "https://didcomm.org/messagepickup/3.0/delivery-request/", false },
        { "https://didcomm.org.evil.example/messagepickup/3.0/delivery-request", false },
        { "https://evil.example/messagepickup/3.0/delivery-request", false },
        { "https://didcomm.org//messagepickup/3.0/delivery-request", false },
        { "https://didcomm.org/x/messagepickup/3.0/delivery-request", false },
        { "https://didcomm.org/messagepickup/3.0/delivery", false },
        { "https://didcomm.org/messagepickup/3.0/status", false },
    };

    [Theory]
    [MemberData(nameof(Spellings))]
    public void A_type_the_pickup_handler_acts_on_is_inside_replay_admission(string type, bool actsOn)
    {
        var registry = new ProtocolHandlerRegistry();
        registry.Register(new StubPickupHandler());

        var dispatched = registry.TryResolve(type, out _);
        var read = dispatched && HandlerReads(type);

        read.Should().Be(actsOn, "the table says whether the registry dispatches '{0}' to pickup and the handler reads it", type);
        if (read)
        {
            MediatorServiceProtocols.IsMediatorServiceType(type).Should().BeTrue(
                "a pickup message the handler acts on must be admitted through the replay guard even without return_route");
            MediatorServiceProtocols.IsReplayExempt(type).Should().BeFalse();
        }
    }

    [Fact]
    public void The_table_holds_acted_on_rows_for_every_name_the_handler_reads()
    {
        var acted = Spellings.Select(row => ((string)row[0], (bool)row[1])).Where(r => r.Item2).Select(r => r.Item1).ToArray();

        acted.Should().Contain(t => t.EndsWith("/delivery-request", StringComparison.Ordinal))
            .And.Contain(t => t.EndsWith("/messages-received", StringComparison.Ordinal))
            .And.Contain(t => t.EndsWith("/live-delivery-change", StringComparison.Ordinal))
            .And.Contain(t => t.EndsWith("/status-request", StringComparison.Ordinal));
    }

    /// <summary>Whether one of the handler's four parsers reads a message of <paramref name="type"/>
    /// (one body that satisfies all four, so only the type decides).</summary>
    private static bool HandlerReads(string type)
    {
        var message = MessagePickup.CreateDeliveryRequest(Bob, Mediator, 10);
        message.Type = type;
        message.Body = new System.Text.Json.Nodes.JsonObject
        {
            ["limit"] = 10,
            ["message_id_list"] = new System.Text.Json.Nodes.JsonArray("m-1"),
            ["live_delivery"] = true,
        };
        return MessagePickup.TryParseStatusRequest(message, out _)
            || MessagePickup.TryParseDeliveryRequest(message, out _, out _)
            || MessagePickup.TryParseMessagesReceived(message, out _)
            || MessagePickup.TryParseLiveDeliveryChange(message, out _);
    }

    private sealed class StubPickupHandler : IProtocolHandler
    {
        // The same PIURI the real handler registers (MessagePickupHandler.ProtocolUri).
        public string ProtocolUri => MessagePickup.ProtocolUri;

        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
            => Task.FromResult<Message?>(null);
    }
}
