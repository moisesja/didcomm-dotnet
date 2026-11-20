using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// <see cref="MessagePickup40Handler"/> (FR-PICK-12, #100) through the real dispatcher with real
/// envelopes, over the same fixture as the 3.0 suites: what each 4.0 request is answered with in each
/// situation a mediator meets, member by member, with what was pushed and which store calls were
/// made; then the rules 4.0 adds — an empty <c>delivery</c> instead of a <c>status</c> (L29, L210,
/// L259), never more attachments than <c>message_count_limit</c> (L255), no answer to
/// <c>messages-received</c> (L35, L294), no answer and no work without a session (plan DC-4), the 4.0
/// problem report (L342-L369), live pushes framed as 4.0 deliveries without a thread (L34, L308),
/// times in milliseconds (L39, L164) — and the rules it keeps: the authcrypt gate, the bounded thread
/// and id list, value-free errors and logs.
/// </summary>
public sealed class MessagePickup40HandlerTests
{
    private const string StatusRequest = "status-request";
    private const string DeliveryRequest = "delivery-request";
    private const string MessagesReceived = "messages-received";
    private const string LiveOn = "live-delivery-change{true}";
    private const string LiveOff = "live-delivery-change{false}";

    private const string GrantHttp = "grant, HTTP-like session";
    private const string GrantLive = "grant, live session";
    private const string GrantNoSession = "grant, no session";
    private const string NoGrantHttp = "no grant, HTTP-like session";
    private const string OtherPair = "grant, session of another pair";

    /// <summary>The spacing of the queued messages: not a whole second, so a time in seconds cannot pass for one in milliseconds.</summary>
    private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(10_123);

    private static readonly string[] Requests = { StatusRequest, DeliveryRequest, MessagesReceived, LiveOn, LiveOff };
    private static readonly string[] Situations = { GrantHttp, GrantLive, GrantNoSession, NoGrantHttp, OtherPair };

    public static TheoryData<string, string, bool> Cells
    {
        get
        {
            var data = new TheoryData<string, string, bool>();
            foreach (var request in Requests)
            {
                foreach (var situation in Situations)
                {
                    data.Add(request, situation, false);
                    data.Add(request, situation, true);
                }
            }
            return data;
        }
    }

    // ---- 1. the decision table --------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task Each_4_0_request_in_each_situation_gets_exactly_the_documented_answer(string request, string situation, bool requestHasThid)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var alice = h.Alice;
        var otherMediator = await h.NewDidAsync();
        var hasGrant = situation != NoGrantHttp;

        // Three messages, 10.123 s apart, 10 + 20 + 30 bytes.
        var ids = new List<string>();
        var payloads = new List<byte[]>();
        if (hasGrant)
        {
            await h.EnrolAsync(alice, alice);
            for (var i = 0; i < 3; i++)
            {
                payloads.Add(PickupFixture.Payload(10 * (i + 1), (byte)(i + 1)));
                ids.Add(await h.Fixture.QueueAsync(alice, payloads[i]));
                h.Clock.Advance(Spacing);
            }
        }
        var t0 = h.Start.ToUnixTimeMilliseconds();

        PickupFakeSession? own = null;
        PickupFakeSession? foreign = null;
        switch (situation)
        {
            case GrantHttp or NoGrantHttp:
                own = h.Http(alice);
                break;
            case GrantLive:
                own = h.Live(alice);
                break;
            case GrantNoSession:
                break;
            case OtherPair:
                foreign = h.Live(h.Bob, otherMediator);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(situation));
        }
        if (foreign is not null)
        {
            // A live session with live delivery ON (through 4.0) for its rightful pair: were it
            // mistaken for Alice's, her answers would show it.
            var turnOn = MessagePickup40.CreateLiveDeliveryChange(foreign.PeerDid, foreign.LocalDid, true);
            (await h.SendAsync(turnOn, foreign, mediator: foreign.LocalDid)).Status40().LiveDelivery.Should().BeTrue();
        }
        var carried = (IReturnRouteSession?)own ?? foreign;
        var liveCapable = own is { IsLive: true };
        h.ResetTraces();

        var thid = requestHasThid ? new LetterIds("thread").First : null;
        var count = hasGrant ? 3 : 0;
        var bytes = hasGrant ? 60 : 0;
        MessagePickup40Times? times = hasGrant ? new MessagePickup40Times(t0 + 2 * (long)Spacing.TotalMilliseconds, t0) : null;
        var statusReads = hasGrant ? new[] { "GetStatusAsync" } : Array.Empty<string>();
        var delivered = false;

        switch (request)
        {
            case StatusRequest:
            {
                var message = h.StatusRequest40(alice, thid: thid);
                var outcome = await h.SendAsync(message, carried);

                outcome.ShouldBeStatus40(message, h.Mediator, alice, count, bytes, live: false, times: times);
                h.MediationTrace.Should().Equal("GetGrantAsync");
                h.StoreTrace.Should().Equal(statusReads);
                break;
            }
            case DeliveryRequest:
            {
                var message = h.DeliveryRequest40(alice, 10, thid: thid);
                var outcome = await h.SendAsync(message, carried);

                if (hasGrant && own is not null)
                {
                    // Pushed through the session, size-bounded; the handler returns nothing more.
                    delivered = true;
                    outcome.ShouldBeNoReply40();
                    own.Sent.Should().ContainSingle().Which.ShouldBeDelivery40(message, h.Mediator, alice, null, ids, payloads);
                    h.MediationTrace.Should().Equal("GetGrantAsync", "FindGrantByRecipientDidAsync");
                    h.StoreTrace.Should().Equal("LeaseAsync");
                    h.Store.LeaseRequests.Should().ContainSingle().Which.MaxMessages.Should().Be(10);
                }
                else if (own is null)
                {
                    // No session of this pair (none, or another pair's): no answer, nothing read or leased (DC-4).
                    outcome.ShouldBeNoReply40();
                    h.MediationTrace.Should().BeEmpty();
                    h.StoreTrace.Should().BeEmpty();
                }
                else
                {
                    // No grant: nothing to deliver, so the empty delivery, and the queue is not read.
                    outcome.ShouldBeEmptyDelivery40(message, h.Mediator, alice, recipientDid: null);
                    h.MediationTrace.Should().Equal("GetGrantAsync");
                    h.StoreTrace.Should().BeEmpty();
                }
                break;
            }
            case MessagesReceived:
            {
                // Applied whatever the session, with return_route: all as a sloppy recipient might
                // still send it, and never answered (L35, L294).
                var acknowledged = hasGrant ? ids[0] : "no-such-id";
                var message = h.MessagesReceived40(alice, new[] { acknowledged }, thid).WithReturnRouteAll();
                var outcome = await h.SendAsync(message, carried);

                outcome.ShouldBeNoReply40();
                h.StoreTrace.Should().Equal("AcknowledgeAsync");
                h.MediationTrace.Should().BeEmpty("no status is read for an answer that is not sent");
                var call = h.Store.Acknowledged.Should().ContainSingle().Subject;
                call.GrantPeerDid.Should().Be(alice);
                call.Ids.Should().Equal(acknowledged);
                count = hasGrant ? 2 : 0;
                break;
            }
            case LiveOn:
            {
                var message = h.LiveChange40(alice, true, thid);
                var outcome = await h.SendAsync(message, carried);

                if (liveCapable)
                {
                    outcome.ShouldBeStatus40(message, h.Mediator, alice, count, bytes, live: true, times: times);
                    h.MediationTrace.Should().Equal("GetGrantAsync");
                    h.StoreTrace.Should().Equal(statusReads);
                    (await h.LiveFlagAsync(own!)).Should().BeTrue();
                }
                else
                {
                    outcome.ShouldBeLiveModeNotSupported40(message, h.Mediator, alice);
                    h.MediationTrace.Should().BeEmpty();
                    h.StoreTrace.Should().BeEmpty();
                    if (own is not null)
                        (await h.LiveFlagAsync(own)).Should().BeFalse();
                }
                break;
            }
            case LiveOff:
            {
                var message = h.LiveChange40(alice, false, thid);
                var outcome = await h.SendAsync(message, carried);

                outcome.ShouldBeStatus40(message, h.Mediator, alice, count, bytes, live: false, times: times);
                h.MediationTrace.Should().Equal("GetGrantAsync");
                h.StoreTrace.Should().Equal(statusReads);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (!delivered)
        {
            own?.Sent.Should().BeEmpty("only a delivery with messages is pushed; every other answer is the handler's reply");
            own?.Attempts.Should().BeEmpty();
        }
        h.Store.Inner.MessageCount.Should().Be(count, "only an acknowledgement removes");
        if (foreign is not null)
        {
            foreign.Attempts.Should().BeEmpty("a session bound to another (peer, local) pair is treated as absent");
            (await h.LiveFlagAsync(foreign)).Should().BeTrue("a request of another pair never touches this session's live flag");
        }
    }

    // ---- 2. the empty delivery (L29, L210, L259) ---------------------------------------------

    public static TheoryData<string> Filters => new() { "none", "a registered DID", "a DID of another grant", "a DID nobody registered", "not a DID" };

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task An_empty_queue_is_answered_with_an_empty_delivery_on_the_request_s_thread_repeating_its_filter(string filter)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var aliceKey = await h.NewDidAsync();
        await h.EnrolAsync(h.Alice, h.Alice, aliceKey);
        await h.EnrolAsync(h.Bob, h.Bob);
        await h.QueueAsync(h.Bob, 10);
        var recipientDid = filter switch
        {
            "none" => null,
            "a registered DID" => aliceKey,
            "a DID of another grant" => h.Bob,
            "a DID nobody registered" => "did:example:nobody",
            _ => "not-a-did",
        };
        var session = h.Http(h.Alice);
        h.ResetTraces();

        var request = h.DeliveryRequest40(h.Alice, 10, recipientDid, thid: new LetterIds("empty").First);
        var outcome = await h.SendAsync(request, session);

        outcome.ShouldBeEmptyDelivery40(request, h.Mediator, h.Alice, recipientDid);
        session.Attempts.Should().BeEmpty("the empty delivery is the reply, not a push");
        h.Store.LeaseRequests.Should().HaveCount(filter is "none" or "a registered DID" ? 1 : 0, "only a scope the peer owns is leased from; for anything else the store is not asked");
        (await h.DeliverableAsync(h.Bob)).Should().HaveCount(1, "another grant's message is untouched");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_limit_of_zero_with_messages_queued_gets_an_empty_delivery_and_leases_nothing(bool filtered)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10), await h.QueueAsync(h.Alice, 20) };
        var session = h.Http(h.Alice);
        h.ResetTraces();

        var request = h.DeliveryRequest40(h.Alice, 0, filtered ? h.Alice : null);
        var outcome = await h.SendAsync(request, session);

        outcome.ShouldBeEmptyDelivery40(request, h.Mediator, h.Alice, filtered ? h.Alice : null);
        h.Store.LeaseRequests.Should().BeEmpty("a limit of zero asks for nothing");
        h.StoreTrace.Should().NotContain("LeaseAsync");
        session.Attempts.Should().BeEmpty();
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids);
    }

    [Fact]
    public async Task When_everything_queued_is_already_leased_the_answer_is_an_empty_delivery()
    {
        // "No messages are available to be sent" (L210): leased to an earlier request is not available.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var id = await h.QueueAsync(h.Alice, 10);
        var first = h.Http(h.Alice);
        var firstRequest = h.DeliveryRequest40(h.Alice);
        (await h.SendAsync(firstRequest, first)).ShouldBeNoReply40();
        first.Sent.Should().ContainSingle().Which.ShouldBeDelivery40(firstRequest, h.Mediator, h.Alice, null, new[] { id });
        var second = h.Http(h.Alice);
        h.ResetTraces();

        var request = h.DeliveryRequest40(h.Alice);
        var outcome = await h.SendAsync(request, second);

        outcome.ShouldBeEmptyDelivery40(request, h.Mediator, h.Alice, recipientDid: null);
        second.Attempts.Should().BeEmpty();
        h.StoreTrace.Should().Equal("LeaseAsync", "GetStatusAsync");
        h.Store.Inner.MessageCount.Should().Be(1, "the leased message is still held");
    }

    // ---- 3. how many (L206, L255) -----------------------------------------------------------

    [Fact]
    public async Task A_delivery_never_carries_more_attachments_than_message_count_limit()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
            ids.Add(await h.QueueAsync(h.Alice, 10, (byte)i));
        var session = h.Http(h.Alice);
        h.ResetTraces();

        var request = h.DeliveryRequest40(h.Alice, 2);
        var outcome = await h.SendAsync(request, session);

        outcome.ShouldBeNoReply40();
        session.Sent.Should().ContainSingle().Which.ShouldBeDelivery40(request, h.Mediator, h.Alice, null, ids.Take(2).ToArray());
        h.Store.LeaseRequests.Should().ContainSingle().Which.MaxMessages.Should().Be(2, "the bound is applied before the store is asked");
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids.Skip(2), "the rest stays queued and unleased");
    }

    [Theory]
    [InlineData("4", 3)]
    [InlineData("1000", 3)]
    [InlineData("2147483648", 3)]
    [InlineData("1e30", 3)]
    [InlineData("2", 2)]
    public async Task A_message_count_limit_above_MaxDeliveryBatch_is_clamped_to_it(string limit, int expected)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync(o => o.MaxDeliveryBatch = 3);
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
            ids.Add(await h.QueueAsync(h.Alice, 8, (byte)i));
        var session = h.Http(h.Alice);
        h.ResetTraces();

        var request = h.Raw40("delivery-request", h.Alice, JsonNode.Parse("{\"message_count_limit\":" + limit + "}")!.AsObject());
        var outcome = await h.SendAsync(request, session);

        outcome.ShouldBeNoReply40();
        h.Store.LeaseRequests.Should().ContainSingle().Which.MaxMessages.Should().Be(expected);
        session.Sent.Should().ContainSingle().Which.ShouldBeDelivery40(request, h.Mediator, h.Alice, null, ids.Take(expected).ToArray());
    }

    [Theory]
    [InlineData("""{"limit":10}""")]
    [InlineData("""{"limit":10,"recipient_did":"did:example:alice"}""")]
    [InlineData("""{"message_count_limit":-1}""")]
    [InlineData("""{"message_count_limit":"10"}""")]
    public async Task A_delivery_request_without_a_usable_message_count_limit_is_malformed_and_touches_nothing(string body)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.QueueAsync(h.Alice, 10);
        var session = h.Http(h.Alice);
        h.ResetTraces();

        var thrown = await Record.ExceptionAsync(() => h.SendAsync(h.Raw40("delivery-request", h.Alice, JsonNode.Parse(body)!.AsObject()), session));

        thrown.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Contain("message_count_limit").And.NotContain("did:");
        h.StoreTrace.Should().BeEmpty();
        h.MediationTrace.Should().BeEmpty();
        session.Attempts.Should().BeEmpty();
    }

    // ---- 4. no session, no answer, no work (DC-4) ------------------------------------------

    [Theory]
    [InlineData("none")]
    [InlineData("another peer's")]
    [InlineData("another local DID's")]
    public async Task A_delivery_request_without_this_pair_s_session_is_not_answered_and_nothing_is_read_or_leased(string session)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var id = await h.QueueAsync(h.Alice, 10);
        var otherMediator = await h.NewDidAsync();
        PickupFakeSession? carried = session switch
        {
            "none" => null,
            "another peer's" => h.Http(h.Bob),
            _ => h.Http(h.Alice, otherMediator),
        };
        h.ResetTraces();
        var logged = h.Fixture.PickupLogs.Count;

        var request = h.DeliveryRequest40(h.Alice, 10, thid: new LetterIds("nosession").First);
        var outcome = await h.SendAsync(request, carried);

        outcome.ShouldBeNoReply40();
        h.StoreTrace.Should().BeEmpty("nothing is read or leased for a delivery that cannot travel");
        h.MediationTrace.Should().BeEmpty();
        carried?.Attempts.Should().BeEmpty();
        (await h.DeliverableAsync(h.Alice)).Should().Equal(id);
        var line = h.Fixture.PickupLogs.Skip(logged).Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Debug);
        line.Category.Should().Be(typeof(MessagePickup40Handler).FullName, "the 4.0 handler writes its own lines");
        line.Formatted.Should().Contain(request.Id).And.Contain("no return-route session");
        line.Everything.Should().NotContain("did:").And.NotContain(request.Thid!).And.NotContain(id);
    }

    // ---- 5. acknowledgements (L35, L294) -----------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Messages_received_removes_what_it_names_and_is_never_answered(bool returnRouteAll)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2), await h.QueueAsync(h.Alice, 30, 3) };
        // Leased first, as a recipient that fetched them would hold them.
        var fetch = h.Http(h.Alice);
        (await h.SendAsync(h.DeliveryRequest40(h.Alice), fetch)).ShouldBeNoReply40();
        // With return_route: all the HTTP endpoint opens a session; without it there is none.
        var session = returnRouteAll ? h.Http(h.Alice) : null;
        h.ResetTraces();

        var request = h.MessagesReceived40(h.Alice, new[] { ids[0], ids[2] });
        if (returnRouteAll)
            request.WithReturnRouteAll();
        var outcome = await h.SendAsync(request, session);

        outcome.ShouldBeNoReply40();
        session?.Attempts.Should().BeEmpty();
        h.StoreTrace.Should().Equal("AcknowledgeAsync");
        h.Store.Acknowledged.Should().ContainSingle().Which.Ids.Should().Equal(ids[0], ids[2]);
        (await h.StoredAsync(h.Alice)).Should().Be(1);
        h.Clock.Advance(TimeSpan.FromSeconds(61)); // the fetch's lease runs out
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids[1]);
    }

    [Fact]
    public async Task A_message_id_list_over_MaxAckIds_is_refused_from_its_length_even_when_the_entry_beyond_is_invalid()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync(o =>
        {
            o.MaxDeliveryBatch = 2;
            o.MaxAckIds = 3;
        });
        await h.EnrolAsync(h.Alice, h.Alice);
        var id = await h.QueueAsync(h.Alice, 10);
        h.ResetTraces();

        // L-055: three valid ids, then a number. The refusal must be the count, not the entry.
        var list = new JsonArray(id, "filler-1", "filler-2", 7);
        var thrown = await Record.ExceptionAsync(() => h.SendAsync(h.Raw40("messages-received", h.Alice, new JsonObject { ["message_id_list"] = list })));

        thrown.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Be("messages-received 'message_id_list' has more than 3 entries.");
        h.StoreTrace.Should().BeEmpty();
        (await h.DeliverableAsync(h.Alice)).Should().Equal(id);
    }

    // ---- 6. live delivery (L302-L369) ---------------------------------------------------------

    [Theory]
    [InlineData("HTTP-like")]
    [InlineData("none")]
    [InlineData("downgraded")]
    public async Task Live_delivery_on_a_connection_that_cannot_do_it_gets_the_4_0_problem_report(string connection)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        PickupFakeSession? session = connection switch
        {
            "HTTP-like" => h.Http(h.Alice),
            "none" => null,
            _ => h.Live(h.Alice),
        };
        if (connection == "downgraded")
            session!.Downgrade();
        h.ResetTraces();

        var request = h.LiveChange40(h.Alice, true, new LetterIds("live").First);
        var outcome = await h.SendAsync(request, session);

        outcome.ShouldBeLiveModeNotSupported40(request, h.Mediator, h.Alice);
        outcome.Reply!.Pthid.Should().Be(request.Thid, "pthid is the thread the problem occurred in");
        h.StoreTrace.Should().BeEmpty();
        session?.Attempts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_live_push_after_4_0_turned_live_delivery_on_is_a_4_0_delivery_with_one_attachment_and_no_thread()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        var turnOn = MessagePickup40.CreateLiveDeliveryChange(peer, mediator, true);
        var status = await fixture.SendAsync(turnOn, mediator, socket);
        status.ShouldBeStatus40(turnOn, mediator, peer, 0, 0, live: true);

        var payload = PickupFixture.Payload(64, 9);
        var result = await fixture.Queue.DeliverAsync(peer, payload);

        result.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        var pushed = socket.Sent.Should().ContainSingle().Subject;
        pushed.ShouldBeDelivery40(null, mediator, peer, null, new[] { result.MessageId! }, new[] { payload });
        pushed.Attachments.Should().ContainSingle();

        // Held until acknowledged — through 4.0, without an answer.
        (await fixture.SendAsync(MessagePickup40.CreateMessagesReceived(peer, mediator, new[] { result.MessageId! }), mediator, socket)).ShouldBeNoReply40();
        ((InMemoryMessageStore)fixture.Store).MessageCount.Should().Be(0);
        socket.Sent.Should().ContainSingle("the acknowledgement is not answered");
    }

    // ---- 7. the thread (DD-11, v2.1 threading L15/L17) ---------------------------------------

    public static TheoryData<string, bool, bool> ThreadedRequests
    {
        get
        {
            var data = new TheoryData<string, bool, bool>();
            foreach (var asThid in new[] { true, false })
            {
                data.Add(StatusRequest, true, asThid);
                data.Add(DeliveryRequest, true, asThid);
                data.Add(DeliveryRequest, false, asThid);
                data.Add(MessagesReceived, true, asThid);
                data.Add(LiveOn, true, asThid);
                data.Add(LiveOff, false, asThid);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ThreadedRequests))]
    public async Task A_request_whose_thread_is_over_512_characters_is_malformed_before_any_store_is_touched(string name, bool withSession, bool asThid)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var id = await h.QueueAsync(h.Alice, 10);
        var session = withSession ? h.Live(h.Alice) : null;
        var request = name switch
        {
            StatusRequest => h.StatusRequest40(h.Alice),
            DeliveryRequest => h.DeliveryRequest40(h.Alice),
            MessagesReceived => h.MessagesReceived40(h.Alice, new[] { id }),
            LiveOn => h.LiveChange40(h.Alice, true),
            _ => h.LiveChange40(h.Alice, false),
        };
        h.ResetTraces();

        // Only a direct caller can: unpack refuses an id or thid over 512 characters (FR-MSG-02/11).
        // A thid that long is refused by the thread rule itself; an id that long (the thread of a
        // request without thid) by the bound on what a reply echoes.
        var tooLong = new string('t', 513);
        var thrown = await Record.ExceptionAsync(() => h.SendHandBuiltAsync(request, m => { if (asThid) m.Thid = tooLong; else m.Id = tooLong; }, session));

        thrown.Should().BeOfType<MalformedMessageException>().Which.Message.Should().NotContain("ttt", "the refusal quotes no value");
        if (!asThid)
            thrown!.Message.Should().Be("A message-pickup request's thread id exceeds the maximum length of 512 characters.");
        h.StoreTrace.Should().BeEmpty();
        h.MediationTrace.Should().BeEmpty();
        session?.Attempts.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task A_thread_id_of_exactly_512_characters_is_answered_on_it()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var thid = new string('t', 512);

        var outcome = await h.SendHandBuiltAsync(h.StatusRequest40(h.Alice), m => m.Thid = thid, h.Http(h.Alice));

        outcome.Reply!.Thid.Should().Be(thid);
        outcome.Handler.Should().BeOfType<MessagePickup40Handler>();
    }

    // ---- 8. times in milliseconds (L39, L164) --------------------------------------------------

    [Fact]
    public async Task Status_times_are_UTC_epoch_milliseconds_of_the_oldest_and_newest_message()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        h.Clock.Advance(TimeSpan.FromMilliseconds(789));
        var oldest = h.Clock.Now;
        await h.QueueAsync(h.Alice, 10);
        h.Clock.Advance(TimeSpan.FromMilliseconds(1_234));
        var newest = h.Clock.Now;
        await h.QueueAsync(h.Alice, 5);
        h.Clock.Advance(TimeSpan.FromMinutes(2));

        var request = h.StatusRequest40(h.Alice);
        var outcome = await h.SendAsync(request, h.Http(h.Alice));

        outcome.ShouldBeStatus40(request, h.Mediator, h.Alice, 2, 15, live: false, times: new MessagePickup40Times(newest.ToUnixTimeMilliseconds(), oldest.ToUnixTimeMilliseconds()));
        (newest.ToUnixTimeMilliseconds() % 1000).Should().NotBe(0, "the instant is not a whole second, so seconds cannot pass for milliseconds");
    }

    [Fact]
    public async Task Status_times_are_left_out_when_nothing_is_waiting_even_if_the_store_reports_instants()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var at = h.Clock.Now.AddMilliseconds(5);
        h.Store.OnGetStatus = (_, _, _, _) => new ValueTask<QueueStatus>(new QueueStatus(0, 0, at, at));

        var request = h.StatusRequest40(h.Alice);
        var outcome = await h.SendAsync(request, h.Http(h.Alice));

        outcome.ShouldBeStatus40(request, h.Mediator, h.Alice, 0, 0, live: false, times: null);
    }

    // ---- 9. who is a peer (FR-PICK-01) ---------------------------------------------------------

    public static TheoryData<HandlerEnvelope, string> UnauthenticatedCells
    {
        get
        {
            var data = new TheoryData<HandlerEnvelope, string>();
            foreach (var shape in new[] { HandlerEnvelope.Plaintext, HandlerEnvelope.Anoncrypt, HandlerEnvelope.SignedOnly, HandlerEnvelope.AnoncryptOfSigned })
            {
                foreach (var request in Requests.Append("malformed delivery-request"))
                    data.Add(shape, request);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(UnauthenticatedCells))]
    public async Task A_4_0_request_without_an_authcrypt_layer_is_neither_applied_nor_answered_and_no_store_is_called(HandlerEnvelope shape, string request)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2) };
        var socket = h.Live(h.Alice);
        var liveBefore = request == LiveOff;
        if (liveBefore)
            (await h.SendAsync(h.LiveChange40(h.Alice, true), socket)).Status40().LiveDelivery.Should().BeTrue();
        h.ResetTraces();

        var message = request switch
        {
            StatusRequest => h.StatusRequest40(h.Alice),
            DeliveryRequest => h.DeliveryRequest40(h.Alice),
            MessagesReceived => h.MessagesReceived40(h.Alice, ids),
            LiveOn => h.LiveChange40(h.Alice, true),
            LiveOff => h.LiveChange40(h.Alice, false),
            // Neither the type nor the body is read for an unauthenticated message, so 3.0's limit cannot throw.
            _ => h.Raw40("delivery-request", h.Alice, new JsonObject { ["limit"] = 10 }),
        };
        var outcome = await h.SendAsync(message, socket, shape);

        outcome.ShouldBeNoReply40();
        h.StoreTrace.Should().BeEmpty();
        h.MediationTrace.Should().BeEmpty();
        socket.Attempts.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(2);
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids);
        (await h.LiveFlagAsync(socket)).Should().Be(liveBefore);
    }

    // ---- 10. what the mediator does not answer --------------------------------------------------

    [Fact]
    public async Task Status_delivery_problem_report_and_unknown_names_of_4_0_are_ignored_without_touching_anything()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var id = await h.QueueAsync(h.Alice, 10);
        var socket = h.Live(h.Alice);
        (await h.SendAsync(h.LiveChange40(h.Alice, true), socket)).Status40().LiveDelivery.Should().BeTrue();
        h.ResetTraces();
        var logged = h.Fixture.PickupLogs.Count;

        var ignored = new[]
        {
            MessagePickup40.CreateStatus(h.Alice, h.Mediator, new PickupStatus40(1), "t-1"),
            MessagePickup40.CreateDelivery(h.Alice, h.Mediator, new[] { new DeliveredMessage(id, new byte[] { 1 }) }, "t-1"),
            MessagePickup40.CreateDelivery(h.Alice, h.Mediator, Array.Empty<DeliveredMessage>(), "t-1"),
            MessagePickup40.CreateLiveModeNotSupported(h.Alice, h.Mediator, "t-1"),
            h.Raw40("no-such-message", h.Alice, new JsonObject { ["message_count_limit"] = 10, ["message_id_list"] = new JsonArray(id) }),
        };
        foreach (var message in ignored)
            (await h.SendAsync(message, socket)).ShouldBeNoReply40();

        h.StoreTrace.Should().BeEmpty();
        h.MediationTrace.Should().BeEmpty();
        socket.Attempts.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(1);
        var lines = h.Fixture.PickupLogs.Skip(logged).ToArray();
        lines.Should().HaveCount(ignored.Length).And.OnlyContain(e => e.Level == LogLevel.Debug && e.Formatted.Contains("ignored", StringComparison.Ordinal));
        lines.Should().OnlyContain(e => e.Category == typeof(MessagePickup40Handler).FullName);
    }

    [Fact]
    public async Task The_handler_serves_the_4_0_piuri_in_the_mediator_role_and_refuses_a_null_queue()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var handler = h.Fixture.Host.Services.GetRequiredService<MessagePickup40Handler>();

        handler.ProtocolUri.Should().Be("https://didcomm.org/message-pickup/4.0");
        handler.Roles.Should().Equal("mediator");
        var mutate = () => ((IList<string>)handler.Roles)[0] = "recipient";
        mutate.Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => new MessagePickup40Handler(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task HandleAsync_refuses_null_arguments_and_a_token_that_is_already_cancelled_before_anything_is_applied()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var id = await h.QueueAsync(h.Alice, 10);
        var handler = h.Fixture.Host.Services.GetRequiredService<MessagePickup40Handler>();
        var message = h.MessagesReceived40(h.Alice, new[] { id });
        var received = await h.Fixture.ReceiveAsync(message, h.Mediator);
        var context = new ProtocolContext(received, new DidComm.Threading.ThreadState(message.Id), h.Fixture.Host.Client, h.Fixture.Host.Options, new DidComm.Threading.InMemoryThreadStateStore());
        h.ResetTraces();

        await FluentActions.Awaiting(() => handler.HandleAsync(null!, context, default)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => handler.HandleAsync(message, null!, default)).Should().ThrowAsync<ArgumentNullException>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await FluentActions.Awaiting(() => handler.HandleAsync(received.Message, context, cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();

        h.StoreTrace.Should().BeEmpty();
        h.MediationTrace.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(1);
    }

    // ---- 11. logs ---------------------------------------------------------------------------------

    [Fact]
    public async Task No_4_0_log_line_carries_a_did_a_queue_id_or_a_thread_id()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var key = await h.NewDidAsync();
        await h.EnrolAsync(h.Alice, h.Alice, key);
        var ids = new[] { await h.QueueAsync(h.Alice, 10), await h.QueueAsync(key, 10) };
        var threads = new LetterIds("logthread");
        var secrets = new List<string>();
        string Thread()
        {
            var t = threads.NewId();
            secrets.Add(t);
            return t;
        }

        var http = h.Http(h.Alice);
        var live = h.Live(h.Alice);
        await h.SendAsync(h.StatusRequest40(h.Alice, key, Thread()), http);
        await h.SendAsync(h.DeliveryRequest40(h.Alice, 1, key, Thread()), http);
        await h.SendAsync(h.DeliveryRequest40(h.Alice, 1, null, Thread()));                         // no session: the Debug line
        await h.SendAsync(h.DeliveryRequest40(h.Alice, 0, "did:example:nobody", Thread()), http);  // empty delivery
        await h.SendAsync(h.MessagesReceived40(h.Alice, ids, Thread()), http);
        await h.SendAsync(h.LiveChange40(h.Alice, true, Thread()), http);                          // problem report
        await h.SendAsync(h.LiveChange40(h.Alice, true, Thread()), live);
        await h.SendAsync(h.Raw40("status", h.Alice, new JsonObject { ["message_count"] = 1 }, thid: Thread()), live); // ignored
        await h.SendAsync(h.StatusRequest40(h.Alice, thid: Thread()), live, HandlerEnvelope.Anoncrypt);          // dropped
        h.Store.OnLease = (_, _) => throw new HandlerBoomException(HandlerBoomException.Marker);
        await h.SendAsync(h.DeliveryRequest40(h.Alice, 1, null, Thread()), http);                  // a store fault

        var lines = h.Fixture.PickupLogs;
        lines.Should().Contain(e => e.Category == typeof(MessagePickup40Handler).FullName && e.Level == LogLevel.Debug);
        lines.Should().Contain(e => e.Level == LogLevel.Error, "the store fault was logged");
        MessagePickupLive.ShouldNotLeak(lines, new[] { h.Alice, h.Mediator, key, "did:example:nobody", ids[0], ids[1] }.Concat(secrets).ToArray());
        foreach (var line in lines)
        {
            line.Exception.Should().BeNull();
            line.Everything.Should().NotContain("did:");
        }
    }
}
