using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// #100 red team ADV-1, mediator role: cross-version confusion between Message Pickup 3.0
/// (<c>https://didcomm.org/messagepickup/3.0</c>) and 4.0 (<c>https://didcomm.org/message-pickup/4.0</c>)
/// over the one mailbox. Every test here is an attack that held; each says what it tried.
/// </summary>
/// <remarks>
/// The registry folds case and <c>-</c>/<c>_</c> out of the protocol name (FR-PROTO-01), so the two
/// names are the same name to it and only the major version tells 3 from 4. These tests pin that a
/// spelling is answered by exactly the handler of its major, in that handler's canonical form; that
/// every spelling either handler acts on is inside replay admission (FR-RR-08); that neither version
/// reads the other's members; and that leases, live flags, live marks and framing stay consistent
/// when one recipient mixes versions on one session or across sessions.
/// </remarks>
public sealed class MessagePickupCrossVersionAdv1Tests
{
    // ---- type spellings ---------------------------------------------------------------------

    /// <summary>(status-request type as sent, which handler answers: "3", "4" or "none").</summary>
    public static TheoryData<string, string> StatusRequestSpellings => new()
    {
        { "https://didcomm.org/messagepickup/3.0/status-request", "3" },
        { "https://didcomm.org/message-pickup/4.0/status-request", "4" },
        // The 3.0 name with major 4, and the 4.0 name with major 3: the major decides.
        { "https://didcomm.org/messagepickup/4.0/status-request", "4" },
        { "https://didcomm.org/message-pickup/3.0/status-request", "3" },
        { "https://didcomm.org/message_pickup/4.0/status_request", "4" },
        { "https://DIDCOMM.ORG/MESSAGE-PICKUP/4.7/STATUS-REQUEST", "4" },
        { "https://didcomm.org/MessagePickup/3.9/StatusRequest", "3" },
        { "https://didcomm.org/message-pickup/4.999999999/status-request", "4" },
        // Not a pickup version this mediator speaks.
        { "https://didcomm.org/message-pickup/5.0/status-request", "none" },
        { "https://didcomm.org/messagepickup/2.0/status-request", "none" },
        // Not a Message Type URI at all: refused when the message is read, before any dispatch.
        { "https://didcomm.org/message-pickup/04.0/status-request", "invalid" },
        { "https://didcomm.org/message-pickup/4.0.0/status-request", "invalid" },
        { "https://didcomm.org/message-pickup/4/status-request", "invalid" },
        // Look-alike letters and punctuation the fold does not remove.
        { "https://didcomm.org/messаge-pickup/4.0/status-request", "none" },   // Cyrillic a
        { "https://didcomm.org/message‐pickup/4.0/status-request", "none" },   // Unicode hyphen
        { "https://didcomm.org/message.pickup/4.0/status-request", "none" },
        // The protocol is 4.0's, the message name is not one it defines: the 4.0 handler ignores it.
        { "https://didcomm.org/message-pickup/4.0/status-request ", "none" },
        { "https://didcomm.org/message-pickup/4.0/status-requests", "none" },
    };

    [Theory]
    [MemberData(nameof(StatusRequestSpellings))]
    public async Task A_status_request_is_answered_only_by_the_handler_of_its_major_in_that_version_s_canonical_form(string type, string answeredBy)
    {
        if (answeredBy == "invalid")
        {
            MessageTypeUri.IsValid(type).Should().BeFalse("such a type never reaches the registry");
            MediatorServiceProtocols.IsMediatorServiceType(type).Should().BeFalse();
            return;
        }

        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.QueueAsync(h.Alice, 100);
        var request = PickupMessages.Raw(type, h.Alice, h.Mediator, new JsonObject());
        var session = h.Http(h.Alice);

        var outcome = await h.SendAsync(request, session);

        switch (answeredBy)
        {
            case "3":
                outcome.Handler.Should().BeOfType<MessagePickupHandler>();
                outcome.Reply!.Type.Should().Be(MessagePickup.StatusType, "the 3.0 handler writes its canonical type whatever spelling it was asked in");
                MessagePickup.TryParseStatus(outcome.Reply, out var s3).Should().BeTrue();
                s3!.NewestReceivedTime.Should().Be(h.Start.ToUnixTimeSeconds(), "3.0 times are seconds");
                s3.LongestWaitedSeconds.Should().NotBeNull();
                break;
            case "4":
                outcome.Handler.Should().BeOfType<MessagePickup40Handler>();
                outcome.Reply!.Type.Should().Be(MessagePickup40.StatusType, "the 4.0 handler writes its canonical type whatever spelling it was asked in");
                outcome.Reply.Thid.Should().Be(request.Id);
                outcome.Reply.Body!.ContainsKey("longest_waited_seconds").Should().BeFalse("4.0 removed it (L37)");
                MessagePickup40.TryParseStatus(outcome.Reply, out var s4).Should().BeTrue();
                s4!.NewestReceivedTimeMs.Should().Be(h.Start.ToUnixTimeMilliseconds(), "4.0 times are milliseconds (L39)");
                break;
            default:
                outcome.Result.Should().NotBe(DispatchResult.ReplyProduced, "no pickup answer in any version");
                outcome.Reply.Should().BeNull();
                session.Sent.Should().BeEmpty();
                break;
        }

        // FR-RR-08 parity: a spelling either pickup handler acts on is inside replay admission, or a
        // captured request in that spelling could be replayed.
        if (answeredBy != "none")
            MediatorServiceProtocols.IsMediatorServiceType(type).Should().BeTrue("a type a pickup handler acts on must be in the replay-protection scope");
    }

    /// <summary>Each state-changing request name, in the 3.0 name with major 4 and the 4.0 name with major 3: inside replay admission.</summary>
    [Theory]
    [InlineData("https://didcomm.org/messagepickup/4.0/delivery-request")]
    [InlineData("https://didcomm.org/messagepickup/4.0/messages-received")]
    [InlineData("https://didcomm.org/messagepickup/4.0/live-delivery-change")]
    [InlineData("https://didcomm.org/message-pickup/3.0/delivery-request")]
    [InlineData("https://didcomm.org/message_pickup/3.0/messages_received")]
    [InlineData("https://didcomm.org/Message-Pickup/4.2/Live_Delivery_Change")]
    public void Every_spelling_the_registry_sends_to_a_pickup_handler_is_inside_replay_admission(string type)
    {
        var registry = new ProtocolHandlerRegistry();
        registry.Register(new Stub(MessagePickup.ProtocolUri));
        registry.Register(new Stub(MessagePickup40.ProtocolUri));

        registry.TryResolve(type, out var handler).Should().BeTrue();
        handler.Should().NotBeNull();
        MediatorServiceProtocols.IsMediatorServiceType(type).Should().BeTrue();
        MediatorServiceProtocols.IsReplayExempt(type).Should().BeFalse();
    }

    // ---- members of the other version -------------------------------------------------------

    [Fact]
    public async Task A_4_0_delivery_request_that_also_carries_3_0_s_limit_is_bounded_by_message_count_limit_alone()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        for (byte i = 0; i < 3; i++)
            await h.QueueAsync(h.Alice, 100, i);
        var session = h.Http(h.Alice);

        var request = h.Raw40("delivery-request", h.Alice, new JsonObject { ["limit"] = 10, ["message_count_limit"] = 1 });
        (await h.SendAsync(request, session)).ShouldBeNoReply40();

        MessagePickup40.TryParseDelivery(session.Sent.Single(), out var delivered, out _).Should().BeTrue();
        delivered.Should().HaveCount(1, "a 4.0 delivery MUST NOT exceed message_count_limit (L255); 3.0's limit is not read");
    }

    [Fact]
    public async Task A_3_0_delivery_request_that_also_carries_4_0_s_message_count_limit_is_bounded_by_limit_alone()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        for (byte i = 0; i < 3; i++)
            await h.QueueAsync(h.Alice, 100, i);
        var session = h.Http(h.Alice);

        var request = h.Raw("delivery-request", h.Alice, new JsonObject { ["limit"] = 1, ["message_count_limit"] = 10 });
        (await h.SendAsync(request, session)).ShouldBeNoReply();

        MessagePickup.TryParseDelivery(session.Sent.Single(), out var delivered, out _).Should().BeTrue();
        delivered.Should().HaveCount(1);
    }

    // ---- no session: 3.0 as on main, 4.0 per DC-4 ---------------------------------------------

    [Fact]
    public async Task Without_a_session_3_0_still_answers_a_delivery_request_with_its_status_and_4_0_answers_nothing_and_leases_nothing()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var id = await h.QueueAsync(h.Alice, 100);

        (await h.SendAsync(h.DeliveryRequest(h.Alice), session: null)).Status().MessageCount.Should().Be(1, "3.0 behaves as on main: a status, nothing leased");
        h.ResetTraces();
        (await h.SendAsync(h.DeliveryRequest40(h.Alice), session: null)).ShouldBeNoReply40();

        h.Store.LeaseRequests.Should().BeEmpty("an empty 4.0 delivery sent out of band would claim nothing is waiting");
        (await h.DeliverableAsync(h.Alice)).Should().Equal(id);
    }

    // ---- live leases, flags, marks and framing across versions ------------------------------

    [Theory]
    [InlineData("3.0", "4.0")]
    [InlineData("4.0", "3.0")]
    public async Task A_live_push_leased_under_one_version_s_framing_is_acknowledged_through_the_other(string onWith, string ackWith)
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);

        (await fixture.SendAsync(LiveChange(onWith, peer, mediator, true), mediator, socket)).Reply.Should().NotBeNull();
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(90))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        var id = PushedIds(socket.Sent.Single()).Single();
        (await DeliverableAsync(fixture, peer)).Should().BeEmpty("the push is leased to the session");

        var ack = ackWith == "3.0" ? MessagePickup.CreateMessagesReceived(peer, mediator, new[] { id }) : MessagePickup40.CreateMessagesReceived(peer, mediator, new[] { id });
        await fixture.SendAsync(ack, mediator, socket);

        (await StoredAsync(fixture, peer)).Should().Be(0, "acknowledgement is by grant and id, whichever version framed the push");
    }

    [Theory]
    [InlineData("3.0", "4.0")]
    [InlineData("4.0", "3.0")]
    public async Task Live_mode_turned_on_in_one_version_does_not_survive_a_reconnect_seen_through_the_other(string onWith, string askWith)
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var first = fixture.Session(peer, mediator, live: true);
        (await fixture.SendAsync(LiveChange(onWith, peer, mediator, true), mediator, first)).Reply.Should().NotBeNull();
        first.Close();

        var second = fixture.Session(peer, mediator, live: true);
        var status = askWith == "3.0"
            ? (await fixture.SendAsync(MessagePickup.CreateStatusRequest(peer, mediator), mediator, second)).Status().LiveDelivery
            : (await fixture.SendAsync(MessagePickup40.CreateStatusRequest(peer, mediator), mediator, second)).Status40().LiveDelivery;

        status.Should().BeFalse("a new connection starts with live mode disabled (4.0 L301), whichever version turned it on before");
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(90))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        first.Sent.Should().BeEmpty();
        second.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Leases_of_pushes_in_both_framings_on_one_session_go_back_to_the_queue_when_it_closes_and_the_other_version_picks_them_up()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);

        await fixture.SendAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, true), mediator, socket);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(90, 1))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        await fixture.SendAsync(MessagePickup40.CreateLiveDeliveryChange(peer, mediator, true), mediator, socket);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(90, 2))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        var sent = socket.Sent.ToArray();
        sent.Select(m => m.Type).Should().Equal(MessagePickup.DeliveryType, MessagePickup40.DeliveryType);
        var ids = sent.SelectMany(PushedIds).ToArray();
        (await DeliverableAsync(fixture, peer)).Should().BeEmpty();

        socket.Close();
        await MessagePickupLive.UntilAsync(async () => (await DeliverableAsync(fixture, peer)).Length == 2, "both leases end with the session");

        var http = fixture.Session(peer, mediator, live: false, register: false);
        var request = MessagePickup40.CreateDeliveryRequest(peer, mediator, 10);
        await fixture.SendAsync(request, mediator, http);
        http.Sent.Single().ShouldBeDelivery40(request, mediator, peer, recipientDid: null, ids);
    }

    [Fact]
    public async Task Turning_live_on_again_in_the_other_version_keeps_the_first_mark_so_what_was_queued_before_is_never_pushed()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var before = await fixture.QueueAsync(peer, PickupFixture.Payload(90, 7));
        var socket = fixture.Session(peer, mediator, live: true);

        (await fixture.SendAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, true), mediator, socket)).Status().MessageCount.Should().Be(1);
        (await fixture.SendAsync(MessagePickup40.CreateLiveDeliveryChange(peer, mediator, true), mediator, socket)).Status40().MessageCount.Should().Be(1);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(90, 8))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);

        var push = socket.Sent.Should().ContainSingle().Subject;
        push.Type.Should().Be(MessagePickup40.DeliveryType);
        PushedIds(push).Should().NotContain(before, "messages already in the queue are not affected by live mode (4.0 L303)");
        (await DeliverableAsync(fixture, peer)).Should().Equal(before);
    }

    [Fact]
    public async Task Off_in_one_version_then_on_in_the_other_takes_a_fresh_mark_so_what_arrived_while_off_stays_queued()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);

        await fixture.SendAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, true), mediator, socket);
        await fixture.SendAsync(MessagePickup40.CreateLiveDeliveryChange(peer, mediator, false), mediator, socket);
        var whileOff = (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(90, 3))).Outcome;
        await fixture.SendAsync(MessagePickup40.CreateLiveDeliveryChange(peer, mediator, true), mediator, socket);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(90, 4))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);

        whileOff.Should().Be(PickupDeliveryOutcome.Queued);
        socket.Sent.Should().ContainSingle().Which.Type.Should().Be(MessagePickup40.DeliveryType);
        (await DeliverableAsync(fixture, peer)).Should().HaveCount(1, "the message that arrived while live was off is the recipient's to pick up");
    }

    [Fact]
    public async Task A_3_0_delivery_request_on_a_session_live_in_4_0_is_answered_in_3_0_form_and_pushes_keep_the_session_s_framing()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var queued = await fixture.QueueAsync(peer, PickupFixture.Payload(90, 5));
        var socket = fixture.Session(peer, mediator, live: true);
        await fixture.SendAsync(MessagePickup40.CreateLiveDeliveryChange(peer, mediator, true), mediator, socket);

        var request = MessagePickup.CreateDeliveryRequest(peer, mediator, 10);
        await fixture.SendAsync(request, mediator, socket);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(90, 6))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);

        var sent = socket.Sent.ToArray();
        sent.Should().HaveCount(2);
        sent[0].ShouldBeDelivery(request, mediator, peer, recipientDid: null, new[] { queued });
        sent[1].Type.Should().Be(MessagePickup40.DeliveryType);
        sent[1].Thid.Should().BeNull();
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static Message LiveChange(string version, string peer, string mediator, bool on)
        => version == "3.0" ? MessagePickup.CreateLiveDeliveryChange(peer, mediator, on) : MessagePickup40.CreateLiveDeliveryChange(peer, mediator, on);

    private static IReadOnlyList<string> PushedIds(Message delivery)
    {
        IReadOnlyList<DeliveredMessage>? messages;
        var parsed = MessagePickup40.TryParseDelivery(delivery, out messages, out _) || MessagePickup.TryParseDelivery(delivery, out messages, out _);
        parsed.Should().BeTrue();
        return messages!.Select(m => m.Id).ToArray();
    }

    private static async Task<string[]> DeliverableAsync(PickupFixture fixture, string peer)
    {
        var now = fixture.Clock.Now;
        var probe = new MessageLeaseRequest(peer, null, int.MaxValue, long.MaxValue, now, new MessageLease(null, now.AddTicks(1)));
        var taken = await fixture.Store.LeaseAsync(probe, CancellationToken.None);
        var ids = taken.Select(m => m.Id).ToArray();
        await fixture.Store.ReleaseAsync(peer, ids, probe.Lease.Id, CancellationToken.None);
        return ids;
    }

    private static async Task<int> StoredAsync(PickupFixture fixture, string peer)
        => (await fixture.Store.GetStatusAsync(peer, null, fixture.Clock.Now, CancellationToken.None)).MessageCount;

    private sealed class Stub(string protocolUri) : IProtocolHandler
    {
        public string ProtocolUri { get; } = protocolUri;

        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct) => Task.FromResult<Message?>(null);
    }
}
