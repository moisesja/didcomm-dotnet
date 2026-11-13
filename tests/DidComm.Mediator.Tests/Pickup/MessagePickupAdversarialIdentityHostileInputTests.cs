using System.Text;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using Xunit;
using Wire = DidComm.Mediator.Tests.Pickup.MessagePickupAdversarialIdentityWire;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// Red-team pass ADV-1 over the mediator role of Message Pickup 3.0 (#94), part 2: JSON a hostile
/// enrolled peer writes by hand, sent through a real authcrypt envelope, the real
/// <c>UnpackAsync</c> and the real dispatcher. Every row is an attack that is
/// <strong>defeated</strong>: the only things that leave are a <c>DidCommException</c> from the
/// receive path, a value-free <c>MalformedMessageException</c> from dispatch, or the documented
/// answer. The rows pin which of the three, so a change in any layer shows.
/// </summary>
public sealed class MessagePickupAdversarialIdentityHostileInputTests
{
    private const string Sentinel = "ADV1SENTINEL";
    private const string StatusRequest = MessagePickup.StatusRequestType;
    private const string DeliveryRequest = MessagePickup.DeliveryRequestType;
    private const string MessagesReceived = MessagePickup.MessagesReceivedType;
    private const string LiveDeliveryChange = MessagePickup.LiveDeliveryChangeType;

    // Where a hand-written request must end:
    //   unpack    — refused by the receive path (a DidCommException), the handler never runs
    //   malformed — a MalformedMessageException out of dispatch that quotes nothing
    //   status    — answered with a status; nothing pushed
    //   delivery  — the queued message is pushed as a delivery; no reply
    public static TheoryData<string, string, string?, string?, string> HandWritten => new()
    {
        // ---- bodies that are not objects, or not there ----
        { "status-request: body is an array", StatusRequest, "[]", null, "unpack" },
        { "status-request: body is a string", StatusRequest, "\"" + Sentinel + "\"", null, "unpack" },
        { "status-request: body is a number", StatusRequest, "5", null, "unpack" },
        { "status-request: body is null", StatusRequest, "null", null, "status" },
        { "status-request: no body", StatusRequest, null, null, "status" },
        { "delivery-request: body is an array", DeliveryRequest, "[1]", null, "unpack" },
        { "delivery-request: no body", DeliveryRequest, null, null, "malformed" },
        { "messages-received: no body", MessagesReceived, null, null, "malformed" },
        { "live-delivery-change: no body", LiveDeliveryChange, null, null, "malformed" },
        { "status-request: nested 60 deep", StatusRequest, Wire.Nested(60), null, "status" },
        { "status-request: nested 200 deep", StatusRequest, Wire.Nested(200), null, "unpack" },

        // ---- a member named twice: the receive path refuses it, so "which one wins" never arises ----
        { "status-request: recipient_did twice", StatusRequest, "{\"recipient_did\":\"did:example:" + Sentinel + "\",\"recipient_did\":\"did:example:b\"}", null, "unpack" },
        { "delivery-request: limit twice, the second negative", DeliveryRequest, "{\"limit\":1,\"limit\":-1}", null, "unpack" },
        { "delivery-request: limit twice, the first negative", DeliveryRequest, "{\"limit\":-1,\"limit\":1}", null, "unpack" },
        { "messages-received: message_id_list twice", MessagesReceived, "{\"message_id_list\":[\"a\",\"b\",\"c\",\"d\"],\"message_id_list\":[]}", null, "unpack" },
        { "live-delivery-change: live_delivery twice", LiveDeliveryChange, "{\"live_delivery\":true,\"live_delivery\":false}", null, "unpack" },
        { "status-request: thid twice", StatusRequest, "{}", "\"thid\":\"t1\",\"thid\":\"t2\"", "unpack" },
        { "status-request: id twice", StatusRequest, "{}", "\"id\":\"second-id\"", "unpack" },
        { "status-request: a member that only differs by an escape", StatusRequest, "{\"recipient_did\":\"did:example:a\",\"recipient" + Wire.Escape("005f") + "did\":\"did:example:b\"}", null, "unpack" },

        // ---- wrong JSON types where a scalar is expected ----
        { "status-request: recipient_did is an array", StatusRequest, "{\"recipient_did\":[\"did:example:" + Sentinel + "\"]}", null, "malformed" },
        { "status-request: recipient_did is an object", StatusRequest, "{\"recipient_did\":{\"did\":\"" + Sentinel + "\"}}", null, "malformed" },
        { "status-request: recipient_did is a number", StatusRequest, "{\"recipient_did\":1}", null, "malformed" },
        { "status-request: recipient_did is empty", StatusRequest, "{\"recipient_did\":\"\"}", null, "malformed" },
        { "delivery-request: limit is a string", DeliveryRequest, "{\"limit\":\"" + Sentinel + "\"}", null, "malformed" },
        { "delivery-request: limit is null", DeliveryRequest, "{\"limit\":null}", null, "malformed" },
        { "delivery-request: limit is true", DeliveryRequest, "{\"limit\":true}", null, "malformed" },
        { "delivery-request: limit is an array", DeliveryRequest, "{\"limit\":[1]}", null, "malformed" },
        { "messages-received: an entry is a number", MessagesReceived, "{\"message_id_list\":[1]}", null, "malformed" },
        { "messages-received: an entry is a list", MessagesReceived, "{\"message_id_list\":[[\"" + Sentinel + "\"]]}", null, "malformed" },
        { "messages-received: an entry is null", MessagesReceived, "{\"message_id_list\":[null]}", null, "malformed" },
        { "messages-received: the list is an object", MessagesReceived, "{\"message_id_list\":{\"0\":\"" + Sentinel + "\"}}", null, "malformed" },
        { "messages-received: the list is a string", MessagesReceived, "{\"message_id_list\":\"" + Sentinel + "\"}", null, "malformed" },
        { "live-delivery-change: a string", LiveDeliveryChange, "{\"live_delivery\":\"true\"}", null, "malformed" },
        { "live-delivery-change: a number", LiveDeliveryChange, "{\"live_delivery\":1}", null, "malformed" },
        { "live-delivery-change: null", LiveDeliveryChange, "{\"live_delivery\":null}", null, "malformed" },

        // ---- number literals ----
        { "limit -0", DeliveryRequest, "{\"limit\":-0}", null, "status" },
        { "limit 0e0", DeliveryRequest, "{\"limit\":0e0}", null, "status" },
        { "limit -0.0e-0", DeliveryRequest, "{\"limit\":-0.0e-0}", null, "status" },
        { "limit 1E+2", DeliveryRequest, "{\"limit\":1E+2}", null, "delivery" },
        { "limit 1e-0", DeliveryRequest, "{\"limit\":1e-0}", null, "delivery" },
        { "limit 1.0", DeliveryRequest, "{\"limit\":1.0}", null, "delivery" },
        { "limit 2^63", DeliveryRequest, "{\"limit\":9223372036854775808}", null, "delivery" },
        { "limit 1e999999", DeliveryRequest, "{\"limit\":1e999999}", null, "delivery" },
        { "limit of 400 digits", DeliveryRequest, "{\"limit\":" + new string('9', 400) + "}", null, "delivery" },
        { "limit 1e-999999", DeliveryRequest, "{\"limit\":1e-999999}", null, "malformed" },
        { "limit 0.5", DeliveryRequest, "{\"limit\":0.5}", null, "malformed" },
        { "limit -1", DeliveryRequest, "{\"limit\":-1}", null, "malformed" },
        { "limit of minus 400 digits", DeliveryRequest, "{\"limit\":-" + new string('9', 400) + "}", null, "malformed" },
        { "limit with a leading zero", DeliveryRequest, "{\"limit\":01}", null, "unpack" },
        { "limit NaN", DeliveryRequest, "{\"limit\":NaN}", null, "unpack" },
        { "limit Infinity", DeliveryRequest, "{\"limit\":Infinity}", null, "unpack" },
        { "limit in hexadecimal", DeliveryRequest, "{\"limit\":0x10}", null, "unpack" },

        // ---- strings: escapes that only fail when they are decoded, and characters that survive ----
        { "recipient_did with an escaped lone surrogate", StatusRequest, "{\"recipient_did\":\"did:example:" + Wire.Escape("d800") + "\"}", null, "unpack" },
        { "an unknown member with an escaped lone surrogate", StatusRequest, "{\"x\":\"" + Wire.Escape("dc00") + "\"}", null, "unpack" },
        { "a member name with an escaped lone surrogate", StatusRequest, "{\"recipient_did" + Wire.Escape("d800") + "\":\"x\"}", null, "unpack" },
        { "a message id with an escaped lone surrogate", MessagesReceived, "{\"message_id_list\":[\"" + Wire.Escape("dfff") + "\"]}", null, "unpack" },
        { "an escaped surrogate pair in the wrong order", MessagesReceived, "{\"message_id_list\":[\"" + Wire.Escape("dc00") + Wire.Escape("d800") + "\"]}", null, "unpack" },
        { "recipient_did with an escaped NUL", StatusRequest, "{\"recipient_did\":\"did:example:a" + Wire.Escape("0000") + "\"}", null, "status" },
        { "recipient_did with a right-to-left override", StatusRequest, "{\"recipient_did\":\"did:example:a" + Wire.Escape("202e") + "\"}", null, "status" },
        { "recipient_did under an escaped member name", StatusRequest, "{\"recipient" + Wire.Escape("005f") + "did\":\"did:example:nobody\"}", null, "status" },
        { "a message id with an escaped NUL", MessagesReceived, "{\"message_id_list\":[\"a" + Wire.Escape("0000") + "b\"]}", null, "status" },

        // ---- headers a pickup request has no use for ----
        { "status-request with please_ack", StatusRequest, "{}", "\"please_ack\":[\"x\"]", "status" },
        { "status-request with an empty please_ack", StatusRequest, "{}", "\"please_ack\":[]", "status" },
        { "status-request with ack", StatusRequest, "{}", "\"ack\":[\"x\"]", "status" },
        { "delivery-request with please_ack", DeliveryRequest, "{\"limit\":10}", "\"please_ack\":[\"x\"]", "delivery" },
        { "live-delivery-change with please_ack", LiveDeliveryChange, "{\"live_delivery\":true}", "\"please_ack\":[\"x\"]", "status" },
        { "status-request with an empty thid", StatusRequest, "{}", "\"thid\":\"\"", "unpack" },
        { "status-request with a numeric thid", StatusRequest, "{}", "\"thid\":5", "unpack" },
        { "status-request with pthid", StatusRequest, "{}", "\"pthid\":\"parent-1\"", "status" },
        { "delivery-request with pthid", DeliveryRequest, "{\"limit\":10}", "\"pthid\":\"parent-1\"", "delivery" },
        { "status-request with return_route thread", StatusRequest, "{}", "\"return_route\":\"thread\"", "status" },
        { "status-request with a made-up return_route", StatusRequest, "{}", "\"return_route\":\"" + Sentinel + "\"", "status" },
        { "status-request with return_route as an object", StatusRequest, "{}", "\"return_route\":{}", "status" },
        { "status-request with attachments", StatusRequest, "{}", "\"attachments\":[{\"id\":\"a\",\"data\":{\"base64\":\"AA\"}}]", "status" },
        { "status-request with a broken from_prior", StatusRequest, "{}", "\"from_prior\":\"x\"", "unpack" },
        { "status-request that expired long ago", StatusRequest, "{}", "\"expires_time\":1", "unpack" },
    };

    [Theory]
    [MemberData(nameof(HandWritten))]
    public async Task A_hand_written_request_from_an_enrolled_peer_ends_in_exactly_one_of_three_ways(string what, string type, string? body, string? extraMembers, string expected)
    {
        await using var fx = PickupFixture.Create(fakeStore: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did, bob.Did);
        var queued = await fx.QueueAsync(bob.Did, PickupFixture.Payload(100));
        var session = fx.Session(bob.Did, mediator.Did, live: true, register: false);
        fx.FakeStore!.Calls.Clear();

        var delivery = await Wire.DeliverAsync(fx, Wire.Plaintext(type, bob.Did, mediator.Did, body, extraMembers: extraMembers), bob, mediator, session);

        switch (expected)
        {
            case "unpack":
                delivery.Verdict.Should().Be("unpack", what);
                fx.FakeStore.Calls.Should().BeEmpty();
                session.Attempts.Should().BeEmpty();
                break;
            case "malformed":
                delivery.Verdict.Should().Be("malformed", what);
                delivery.DispatchError!.InnerException.Should().BeNull("a cause could carry what the text withholds");
                delivery.ThrownText.Should().NotContain(Sentinel).And.NotContain(bob.Did).And.NotContain(queued);
                fx.FakeStore.Calls.Should().BeEmpty("a malformed request touches no store");
                session.Attempts.Should().BeEmpty();
                break;
            case "status":
                delivery.Verdict.Should().Be("reply", what);
                delivery.Outcome!.Reply!.Type.Should().Be(MessagePickup.StatusType);
                delivery.Outcome.Reply.To.Should().Equal(bob.Did);
                delivery.Outcome.Reply.PleaseAck.Should().BeNull("the mediator asks for no acknowledgement of its own");
                delivery.Outcome.Reply.Ack.Should().BeNull();
                session.Attempts.Should().BeEmpty("a status is the handler's reply, never a push");
                break;
            case "delivery":
                delivery.Verdict.Should().Be("no-reply", what);
                var pushed = session.Sent.Should().ContainSingle().Subject;
                pushed.Type.Should().Be(MessagePickup.DeliveryType);
                pushed.PleaseAck.Should().BeNull();
                pushed.Ack.Should().BeNull();
                MessagePickup.TryParseDelivery(pushed, out var messages, out _).Should().BeTrue();
                messages!.Select(m => m.Id).Should().Equal(queued);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(expected));
        }
        fx.Memory.MessageCount.Should().Be(1, "nothing a request carries removes a message it did not name");
        MessagePickupLive.ShouldNotLeak(fx.PickupLogs, bob.Did, mediator.Did, queued, Sentinel);
    }

    // The message types a hostile peer might hope the 3.0 role answers, or chokes on. Control and
    // non-ASCII characters are built with casts so that they survive every tool on the way here.
    private static readonly Dictionary<string, (string Type, string Expected)> Types = new(StringComparer.Ordinal)
    {
        ["the type itself"] = ("https://didcomm.org/messagepickup/3.0/status-request", "reply"),
        ["a minor version of 3"] = ("https://didcomm.org/messagepickup/3.7/status-request", "reply"),
        ["another case and punctuation"] = ("https://DIDCOMM.org/Message_Pickup/3.0/STATUS-request", "reply"),
        ["punctuation only"] = ("https://didcomm.org/-m-e-s-s-a-g-e-p-i-c-k-u-p-/3.0/status--__request", "reply"),
        ["version 3.0x"] = ("https://didcomm.org/messagepickup/3.0x/status-request", "unpack"),
        ["version 30"] = ("https://didcomm.org/messagepickup/30/status-request", "unpack"),
        ["version 03.0"] = ("https://didcomm.org/messagepickup/03.0/status-request", "unpack"),
        ["version 3.00"] = ("https://didcomm.org/messagepickup/3.00/status-request", "unpack"),
        ["a minor version beyond 32 bits"] = ("https://didcomm.org/messagepickup/3.99999999999999999999/status-request", "unpack"),
        ["an Arabic-Indic three"] = ("https://didcomm.org/messagepickup/" + (char)0x0663 + ".0/status-request", "unpack"),
        ["major version 4"] = ("https://didcomm.org/messagepickup/4.0/status-request", "no-handler"),
        ["the 4.0 name"] = ("https://didcomm.org/message-pickup/4.0/status-request", "no-handler"),
        ["plain http"] = ("http://didcomm.org/messagepickup/3.0/status-request", "no-handler"),
        ["a trailing dot on the host"] = ("https://didcomm.org./messagepickup/3.0/status-request", "no-handler"),
        ["a doubled slash"] = ("https://didcomm.org//messagepickup/3.0/status-request", "no-handler"),
        ["a dot segment"] = ("https://didcomm.org/x/../messagepickup/3.0/status-request", "no-handler"),
        ["another organisation"] = ("https://didcomm.org.evil.example/messagepickup/3.0/status-request", "no-handler"),
        ["a user-info trick"] = ("https://didcomm.org@evil.example/messagepickup/3.0/status-request", "no-handler"),
        ["a trailing newline"] = ("https://didcomm.org/messagepickup/3.0/status-request" + (char)0x0A, "no-reply"),
        ["a trailing space"] = ("https://didcomm.org/messagepickup/3.0/status-request ", "no-reply"),
        ["a query"] = ("https://didcomm.org/messagepickup/3.0/status-request?x=1", "no-reply"),
        ["a fragment"] = ("https://didcomm.org/messagepickup/3.0/status-request#x", "no-reply"),
        ["a Kelvin sign for k"] = ("https://didcomm.org/messagepic" + (char)0x212A + "up/3.0/status-request", "no-handler"),
        ["a long s"] = ("https://didcomm.org/messagepickup/3.0/statu" + (char)0x017F + "-request", "no-reply"),
        ["a dotless i"] = ("https://didcomm.org/messagep" + (char)0x0131 + "ckup/3.0/status-request", "no-handler"),
        ["a name the role does not define"] = ("https://didcomm.org/messagepickup/3.0/" + Sentinel, "no-reply"),
        ["a response type"] = ("https://didcomm.org/messagepickup/3.0/delivery", "no-reply"),
    };

    public static TheoryData<string> TypeNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Types.Keys)
                data.Add(name);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(TypeNames))]
    public async Task Only_the_didcomm_org_3_x_types_reach_the_role_and_a_look_alike_is_never_answered(string what)
    {
        var (type, expected) = Types[what];
        await using var fx = PickupFixture.Create(fakeStore: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did, bob.Did);
        await fx.QueueAsync(bob.Did, PickupFixture.Payload(100));
        var session = fx.Session(bob.Did, mediator.Did, live: true, register: false);
        fx.FakeStore!.Calls.Clear();

        var delivery = await Wire.DeliverAsync(fx, Wire.Plaintext(type, bob.Did, mediator.Did, "{}"), bob, mediator, session);

        delivery.Verdict.Should().Be(expected, what);
        if (expected == "reply")
        {
            delivery.Outcome!.Reply!.Type.Should().Be(MessagePickup.StatusType, "the answer carries the 3.0 type, whatever spelling asked");
            delivery.Outcome.Status().MessageCount.Should().Be(1);
        }
        else
        {
            fx.FakeStore.Calls.Should().BeEmpty("a type the role does not answer touches no store ({0})", what);
            session.Attempts.Should().BeEmpty();
        }
        MessagePickupLive.ShouldNotLeak(fx.PickupLogs, bob.Did, mediator.Did, Sentinel);
    }

    [Fact]
    public async Task At_the_real_receive_boundary_a_message_id_list_of_300_000_entries_is_refused_from_its_length_without_allocating_for_it()
    {
        // Defeated (L-048, L-055: the claim tested at our own boundary). QA measured this on a
        // message deserialized in the test; this one comes through a real envelope and
        // UnpackAsync, the only way a peer can present it. The refusal is the count — the entries
        // are numbers, so reading even one would be a different error — and its cost does not
        // depend on the list.
        await using var fx = PickupFixture.Create(fakeStore: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did, bob.Did);

        async Task<DidComm.Messages.Message> ReceiveAsync(int entries)
        {
            var body = new StringBuilder("{\"message_id_list\":[");
            for (var i = 0; i < entries; i++)
                body.Append(i == 0 ? "0" : ",0");
            body.Append("]}");
            var packed = await Wire.AuthcryptAsync(fx.Host, Wire.Plaintext(MessagesReceived, bob.Did, mediator.Did, body.ToString()), bob, mediator);
            return (await fx.Host.Client.UnpackAsync(packed)).Message;
        }

        static (long Allocated, Exception? Thrown) Parse(DidComm.Messages.Message message)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            Exception? thrown = null;
            try
            {
                MessagePickup.TryParseMessagesReceived(message, 100, out _);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
            return (GC.GetAllocatedBytesForCurrentThread() - before, thrown);
        }

        var justOver = await ReceiveAsync(101);
        var huge = await ReceiveAsync(300_000);
        Parse(await ReceiveAsync(101)); // warm-up

        var (smallCost, smallThrown) = Parse(justOver);
        var (hugeCost, hugeThrown) = Parse(huge);

        smallThrown.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Be("messages-received 'message_id_list' has more than 100 entries.");
        hugeThrown.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Be(smallThrown!.Message);
        hugeCost.Should().BeLessThan(smallCost + 4096, "300 000 entries cost what 101 cost");

        // And through dispatch: the same refusal, no store call.
        fx.FakeStore!.Calls.Clear();
        var packedAgain = await Wire.AuthcryptAsync(fx.Host, Wire.Plaintext(MessagesReceived, bob.Did, mediator.Did, "{\"message_id_list\":[" + string.Join(",", Enumerable.Repeat("0", 101)) + "]}"), bob, mediator);
        var received = await fx.Host.Client.UnpackAsync(packedAgain);
        var dispatch = await Record.ExceptionAsync(() => fx.Host.Dispatcher.DispatchAsync(received, fx.Host.Client, fx.Host.Options, CancellationToken.None));
        dispatch.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Be(smallThrown.Message);
        fx.FakeStore.Calls.Should().BeEmpty();
    }
}
