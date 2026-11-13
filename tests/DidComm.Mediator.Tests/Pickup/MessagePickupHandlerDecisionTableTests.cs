using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Json;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// <see cref="MessagePickupHandler"/> through the real dispatcher with real envelopes: what each of
/// the four requests is answered with in each situation a mediator meets (a grant or none; an
/// HTTP-like session, a live one, none, or one that belongs to another pair), member by member,
/// together with what was pushed on the session and which store calls were made
/// (FR-PICK-02..07; JC-7, -8, -9, -10, -16).
/// </summary>
public sealed class MessagePickupHandlerDecisionTableTests
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
    private const string NoGrantLive = "no grant, live session";
    private const string OtherPeer = "grant, session of another peer";
    private const string OtherLocal = "grant, session for another local DID";
    private const string OtherPair = "grant, session of another pair";

    private static readonly string[] Requests = { StatusRequest, DeliveryRequest, MessagesReceived, LiveOn, LiveOff };
    private static readonly string[] Situations = { GrantHttp, GrantLive, GrantNoSession, NoGrantHttp, NoGrantLive, OtherPeer, OtherLocal, OtherPair };

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
    public async Task Each_request_in_each_situation_gets_exactly_the_documented_answer(string request, string situation, bool requestHasThid)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var alice = h.Alice;
        var otherMediator = await h.NewDidAsync();
        var hasGrant = !situation.StartsWith("no grant", StringComparison.Ordinal);

        // Three messages, ten seconds apart, 10 + 20 + 30 bytes; the request arrives 60 s after the first.
        var ids = new List<string>();
        var payloads = new List<byte[]>();
        if (hasGrant)
        {
            await h.EnrolAsync(alice, alice);
            for (var i = 0; i < 3; i++)
            {
                payloads.Add(PickupFixture.Payload(10 * (i + 1), (byte)(i + 1)));
                ids.Add(await h.Fixture.QueueAsync(alice, payloads[i]));
                h.Clock.Advance(TimeSpan.FromSeconds(10));
            }
            h.Clock.Advance(TimeSpan.FromSeconds(30));
        }
        else
        {
            h.Clock.Advance(TimeSpan.FromSeconds(60));
        }
        var now = h.Clock.Now;
        var t0 = h.Start.ToUnixTimeSeconds();

        // The session the dispatcher is handed. A session of another pair is a live one with live
        // delivery ON for its rightful pair: were it mistaken for Alice's, her answers would show it.
        PickupFakeSession? own = null;
        PickupFakeSession? foreign = null;
        switch (situation)
        {
            case GrantHttp or NoGrantHttp:
                own = h.Http(alice);
                break;
            case GrantLive or NoGrantLive:
                own = h.Live(alice);
                break;
            case GrantNoSession:
                break;
            case OtherPeer:
                foreign = h.Live(h.Bob);
                break;
            case OtherLocal:
                foreign = h.Live(alice, otherMediator);
                break;
            case OtherPair:
                foreign = h.Live(h.Bob, otherMediator);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(situation));
        }
        if (foreign is not null)
        {
            var turnOn = MessagePickup.CreateLiveDeliveryChange(foreign.PeerDid, foreign.LocalDid, true);
            (await h.SendAsync(turnOn, foreign, mediator: foreign.LocalDid)).Status().LiveDelivery.Should().BeTrue();
        }
        var carried = (IReturnRouteSession?)own ?? foreign;
        var liveCapable = own is { IsLive: true };
        h.ResetTraces();

        var thid = requestHasThid ? "thread-" + Guid.NewGuid().ToString("N") : null;
        var count = hasGrant ? 3 : 0;
        var bytes = hasGrant ? 60 : 0;
        MessagePickupHandlerTimes? times = hasGrant ? new MessagePickupHandlerTimes(60, t0 + 20, t0) : null;
        var statusReads = hasGrant ? new[] { "GetStatusAsync" } : Array.Empty<string>();
        var delivered = false;

        switch (request)
        {
            case StatusRequest:
            {
                var message = h.StatusRequest(alice, thid: thid);
                var outcome = await h.SendAsync(message, carried);

                outcome.ShouldBeStatus(message, h.Mediator, alice, count, bytes, live: false, times: times);
                h.MediationTrace.Should().Equal("GetGrantAsync");
                h.StoreTrace.Should().Equal(statusReads);
                break;
            }
            case DeliveryRequest:
            {
                var message = h.DeliveryRequest(alice, 10, thid: thid);
                var outcome = await h.SendAsync(message, carried);

                if (hasGrant && own is not null)
                {
                    // The delivery is pushed through the session, size-bounded; the handler returns
                    // nothing more — over the HTTP-like session too (JC-2).
                    delivered = true;
                    outcome.ShouldBeNoReply();
                    own.Sent.Should().ContainSingle().Which.ShouldBeDelivery(message, h.Mediator, alice, null, ids, payloads);
                    own.Attempts.Should().ContainSingle().Which.MaxPackedBytes.Should().Be(h.Fixture.Host.Options.MaxReceiveBytes, "max_delivery_bytes defaults to MaxReceiveBytes");
                    h.MediationTrace.Should().Equal("GetGrantAsync", "FindGrantByRecipientDidAsync");
                    h.StoreTrace.Should().Equal("LeaseAsync");
                    var lease = h.Store.LeaseRequests.Should().ContainSingle().Subject;
                    lease.GrantPeerDid.Should().Be(alice);
                    lease.RecipientDid.Should().BeNull();
                    lease.MaxMessages.Should().Be(10);
                    lease.Now.Should().Be(now);
                    lease.Lease.ExpiresAt.Should().Be(now + TimeSpan.FromSeconds(60));
                    (lease.Lease.Owner is null).Should().Be(!liveCapable, "an HTTP lease ends by time only; a live session's leases carry its token");
                    lease.Lease.Owner.Should().NotContain("did:");
                }
                else
                {
                    // No grant: an empty queue. No session to carry a batch: nothing is leased (JC-9).
                    outcome.ShouldBeStatus(message, h.Mediator, alice, count, bytes, live: false, times: times);
                    h.MediationTrace.Should().Equal("GetGrantAsync");
                    h.StoreTrace.Should().Equal(statusReads);
                    h.Store.LeaseRequests.Should().BeEmpty();
                }
                break;
            }
            case MessagesReceived:
            {
                // Applied with or without a session, and before any grant is read (JC-16).
                var acknowledged = hasGrant ? ids[0] : "no-such-id";
                var message = h.MessagesReceived(alice, new[] { acknowledged }, thid);
                var outcome = await h.SendAsync(message, carried);

                if (hasGrant)
                    outcome.ShouldBeStatus(message, h.Mediator, alice, 2, 50, live: false, times: new MessagePickupHandlerTimes(50, t0 + 20, t0 + 10));
                else
                    outcome.ShouldBeStatus(message, h.Mediator, alice, 0, 0, live: false);
                h.StoreTrace.Should().Equal(new[] { "AcknowledgeAsync" }.Concat(statusReads));
                h.MediationTrace.Should().Equal("GetGrantAsync");
                var call = h.Store.Acknowledged.Should().ContainSingle().Subject;
                call.GrantPeerDid.Should().Be(alice);
                call.Ids.Should().Equal(acknowledged);
                count = hasGrant ? 2 : 0;
                break;
            }
            case LiveOn:
            {
                var message = h.LiveChange(alice, true, thid);
                var outcome = await h.SendAsync(message, carried);

                if (liveCapable)
                {
                    outcome.ShouldBeStatus(message, h.Mediator, alice, count, bytes, live: true, times: times);
                    h.MediationTrace.Should().Equal("GetGrantAsync");
                    h.StoreTrace.Should().Equal(statusReads);
                    (await h.LiveFlagAsync(own!)).Should().BeTrue();
                }
                else
                {
                    // HTTP, no session, or somebody else's session: the problem report, and no store is read (JC-10).
                    outcome.ShouldBeLiveModeNotSupported(message, h.Mediator, alice);
                    h.MediationTrace.Should().BeEmpty();
                    h.StoreTrace.Should().BeEmpty();
                    if (own is not null)
                        (await h.LiveFlagAsync(own)).Should().BeFalse();
                }
                break;
            }
            case LiveOff:
            {
                var message = h.LiveChange(alice, false, thid);
                var outcome = await h.SendAsync(message, carried);

                outcome.ShouldBeStatus(message, h.Mediator, alice, count, bytes, live: false, times: times);
                h.MediationTrace.Should().Equal("GetGrantAsync");
                h.StoreTrace.Should().Equal(statusReads);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }

        // What every cell has in common: only a delivery is ever pushed, a session of another pair
        // is never written to and keeps its own live flag, and only an acknowledgement removes.
        if (!delivered)
        {
            own?.Sent.Should().BeEmpty();
            own?.Attempts.Should().BeEmpty();
        }
        h.Store.Inner.MessageCount.Should().Be(count);
        if (foreign is not null)
        {
            foreign.Attempts.Should().BeEmpty("a session bound to another (peer, local) pair is treated as absent (JC-9)");
            (await h.LiveFlagAsync(foreign)).Should().BeTrue("a request of another pair never touches this session's live flag");
        }
    }

    // ---- limit ------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Limit_zero_is_answered_with_status_without_a_lease_call(bool filtered)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.QueueAsync(h.Alice, 10);
        await h.QueueAsync(h.Alice, 20);
        var session = h.Http(h.Alice);
        h.ResetTraces();

        var request = h.DeliveryRequest(h.Alice, 0, filtered ? h.Alice : null);
        var outcome = await h.SendAsync(request, session);

        var t0 = h.Start.ToUnixTimeSeconds();
        outcome.ShouldBeStatus(request, h.Mediator, h.Alice, 2, 30, live: false, recipientDid: filtered ? h.Alice : null, times: new MessagePickupHandlerTimes(0, t0, t0));
        h.StoreTrace.Should().Equal("GetStatusAsync");
        h.Store.LeaseRequests.Should().BeEmpty("limit 0 asks for nothing: the status, not a lease");
        session.Attempts.Should().BeEmpty();
        (await h.DeliverableAsync(h.Alice)).Should().HaveCount(2);
    }

    public static TheoryData<string, int> Limits => new()
    {
        // what the body says → what the store must be asked for, with MaxDeliveryBatch = 3
        { "1", 1 },
        { "2", 2 },
        { "3", 3 },
        { "4", 3 },
        { "10", 3 },
        { "1000000", 3 },
        { "2147483647", 3 },
        { "2147483648", 3 },
        { "9223372036854775807", 3 },
        { "9223372036854775808", 3 },
        { "99999999999999999999999999999999999999", 3 },
        { "1e400", 3 },
        { "1E+400", 3 },
        { "4.0", 3 },
        { "2.0", 2 },
        { "2e0", 2 },
        { "0.2e1", 2 },
    };

    [Theory]
    [MemberData(nameof(Limits))]
    public async Task The_store_is_asked_for_the_clamped_batch_and_the_byte_budget_whatever_limit_says(string limit, int expectedMaxMessages)
    {
        // L-055: the bound is applied before the store is touched. The lease request the store
        // sees already carries the clamped count and the byte budget; nothing is trimmed afterwards.
        await using var h = await MessagePickupHandlerHarness.CreateAsync(o => o.MaxDeliveryBatch = 3);
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
            ids.Add(await h.QueueAsync(h.Alice, 8, (byte)i));
        var session = h.Http(h.Alice);
        h.ResetTraces();

        // Written as JSON text, so the number reaches the mediator exactly as spelled.
        var request = h.Raw("delivery-request", h.Alice, JsonNode.Parse("{\"limit\":" + limit + "}")!.AsObject());
        var outcome = await h.SendAsync(request, session);

        outcome.ShouldBeNoReply();
        var lease = h.Store.LeaseRequests.Should().ContainSingle().Subject;
        lease.MaxMessages.Should().Be(expectedMaxMessages);
        lease.MaxPayloadBytes.Should().Be(h.Fixture.Queue.Coordinator.Sizing.BatchPayloadBudget);
        lease.MaxPayloadBytes.Should().BeInRange(1, h.Fixture.Host.Options.MaxReceiveBytes, "the byte budget is below max_delivery_bytes: a delivered payload is base64-encoded twice");
        h.StoreTrace.Should().Equal("LeaseAsync");
        session.Sent.Should().ContainSingle().Which.ShouldBeDelivery(request, h.Mediator, h.Alice, null, ids.Take(expectedMaxMessages).ToArray());
    }

    [Fact]
    public async Task The_default_batch_maximum_is_ten_and_the_default_byte_budget_follows_MaxReceiveBytes()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new List<string>();
        for (var i = 0; i < 12; i++)
            ids.Add(await h.QueueAsync(h.Alice, 8, (byte)i));
        var session = h.Http(h.Alice);
        h.ResetTraces();

        var request = h.DeliveryRequest(h.Alice, int.MaxValue);
        (await h.SendAsync(request, session)).ShouldBeNoReply();

        var lease = h.Store.LeaseRequests.Should().ContainSingle().Subject;
        lease.MaxMessages.Should().Be(10);
        lease.MaxPayloadBytes.Should().Be(PickupSizing.ComputeBatchPayloadBudget(1024 * 1024, 10));
        session.Sent.Should().ContainSingle().Which.ShouldBeDelivery(request, h.Mediator, h.Alice, null, ids.Take(10).ToArray());
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids.Skip(10), "the two beyond the batch were never leased");
    }

    [Fact]
    public async Task Leases_taken_over_one_live_session_share_an_opaque_owner_token_that_never_goes_on_the_wire()
    {
        // FR-PICK-04: an HTTP lease ends by time only; a live session's leases also end when it
        // closes, which the store can only do if they carry that session's token (JC-5).
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        for (var i = 0; i < 4; i++)
            await h.QueueAsync(h.Alice, 8, (byte)i);
        var socket = h.Live(h.Alice);
        var otherSocket = h.Live(h.Alice);
        var http = h.Http(h.Alice);
        h.ResetTraces();

        (await h.SendAsync(h.DeliveryRequest(h.Alice, 1), socket)).ShouldBeNoReply();
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 1), socket)).ShouldBeNoReply();
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 1), otherSocket)).ShouldBeNoReply();
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 1), http)).ShouldBeNoReply();

        var owners = h.Store.LeaseRequests.Select(r => r.Lease.Owner).ToArray();
        owners.Should().HaveCount(4);
        owners[0].Should().NotBeNullOrEmpty();
        owners[1].Should().Be(owners[0], "one session, one token");
        owners[2].Should().NotBeNullOrEmpty().And.NotBe(owners[0], "another session of the same peer has its own");
        owners[3].Should().BeNull("an HTTP lease has no owner");
        foreach (var owner in owners.Where(o => o is not null))
        {
            owner.Should().NotContain("did:").And.NotBe(h.Alice);
            foreach (var pushed in socket.Sent.Concat(otherSocket.Sent).Concat(http.Sent))
                JsonSerializer.Serialize(pushed, DidCommJson.Default).Should().NotContain(owner!);
            h.Fixture.PickupLogs.Should().OnlyContain(e => !e.Everything.Contains(owner!));
        }
    }

    // ---- filters ----------------------------------------------------------------------------

    [Fact]
    public async Task A_recipient_did_narrows_status_and_delivery_to_that_did_and_is_echoed()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var second = await h.NewDidAsync();
        var third = await h.NewDidAsync();
        await h.EnrolAsync(h.Alice, h.Alice, second, third);
        var t0 = h.Start.ToUnixTimeSeconds();
        var own1 = await h.QueueAsync(h.Alice, 10, 1);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        var second1 = await h.QueueAsync(second, 20, 2);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        var own2 = await h.QueueAsync(h.Alice, 30, 3);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        var second2 = await h.QueueAsync(second, 40, 4);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        var session = h.Http(h.Alice);

        // status-request for one DID: only that DID's numbers, and the value comes back (FR-PICK-02).
        h.ResetTraces();
        var status = h.StatusRequest(h.Alice, second);
        (await h.SendAsync(status, session)).ShouldBeStatus(status, h.Mediator, h.Alice, 2, 60, live: false, recipientDid: second, times: new MessagePickupHandlerTimes(15, t0 + 15, t0 + 5));
        h.Mediation.Reads.Should().Equal(("FindGrantByRecipientDidAsync", second));
        h.Store.StatusRequests.Should().Equal((h.Alice, second));
        h.StoreTrace.Should().Equal("GetStatusAsync");

        // A registered DID with nothing queued: the echo, the count and nothing else.
        var empty = h.StatusRequest(h.Alice, third);
        (await h.SendAsync(empty, session)).ShouldBeStatus(empty, h.Mediator, h.Alice, 0, 0, live: false, recipientDid: third);
        var emptyFetch = h.DeliveryRequest(h.Alice, 10, third);
        (await h.SendAsync(emptyFetch, session)).ShouldBeStatus(emptyFetch, h.Mediator, h.Alice, 0, 0, live: false, recipientDid: third);
        session.Sent.Should().BeEmpty();

        // delivery-request for one DID: its messages only, FIFO, the delivery repeating the filter (FR-PICK-03).
        h.ResetTraces();
        var fetch = h.DeliveryRequest(h.Alice, 10, second);
        (await h.SendAsync(fetch, session)).ShouldBeNoReply();
        session.Sent.Should().ContainSingle().Which.ShouldBeDelivery(fetch, h.Mediator, h.Alice, second, new[] { second1, second2 });
        var lease = h.Store.LeaseRequests.Should().ContainSingle().Subject;
        lease.GrantPeerDid.Should().Be(h.Alice);
        lease.RecipientDid.Should().Be(second);
        h.Mediation.Reads.Should().Equal(new[] { ("FindGrantByRecipientDidAsync", second) }, "a filtered request is authorised once, before the store is touched");

        // The other DID's messages were not touched by any of it.
        (await h.DeliverableAsync(h.Alice)).Should().Equal(own1, own2);
    }

    [Fact]
    public async Task An_unfiltered_delivery_checks_each_distinct_recipient_did_of_the_batch_once()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var second = await h.NewDidAsync();
        var third = await h.NewDidAsync();
        await h.EnrolAsync(h.Alice, h.Alice, second, third);
        var ids = new List<string>();
        foreach (var did in new[] { h.Alice, second, h.Alice, third, second, h.Alice, third, third, second })
            ids.Add(await h.QueueAsync(did, 8, (byte)ids.Count));
        var session = h.Http(h.Alice);
        h.ResetTraces();

        var request = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(request, session)).ShouldBeNoReply();

        session.Sent.Should().ContainSingle().Which.ShouldBeDelivery(request, h.Mediator, h.Alice, null, ids);
        h.Mediation.Reads.Should().Equal(
            ("GetGrantAsync", h.Alice),
            ("FindGrantByRecipientDidAsync", h.Alice),
            ("FindGrantByRecipientDidAsync", second),
            ("FindGrantByRecipientDidAsync", third));
        h.StoreTrace.Should().Equal("LeaseAsync");
        h.Store.PurgedRecipients.Should().BeEmpty();
    }

    // ---- messages-received ------------------------------------------------------------------

    [Fact]
    public async Task An_empty_message_id_list_removes_nothing_and_is_answered_with_status()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.QueueAsync(h.Alice, 10);
        h.ResetTraces();

        var request = h.MessagesReceived(h.Alice, Array.Empty<string>());
        var outcome = await h.SendAsync(request);

        var t0 = h.Start.ToUnixTimeSeconds();
        outcome.ShouldBeStatus(request, h.Mediator, h.Alice, 1, 10, live: false, times: new MessagePickupHandlerTimes(0, t0, t0));
        h.StoreTrace.Should().Equal("GetStatusAsync");
        h.Store.Inner.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task Unknown_ids_are_ignored_known_ones_are_removed_whatever_their_state_and_the_status_reflects_it()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new List<string>();
        for (var i = 0; i < 4; i++)
            ids.Add(await h.QueueAsync(h.Alice, 10, (byte)i));

        // Only unknown ids: nothing removed, the status is sent all the same.
        var unknown = h.MessagesReceived(h.Alice, new[] { "no-such-id", Guid.NewGuid().ToString(), new string('z', 2048) });
        (await h.SendAsync(unknown)).Status().MessageCount.Should().Be(4);

        // ids[0] and ids[1] are delivered (leased) over one session; ids[2] is still waiting.
        var session = h.Http(h.Alice);
        (await h.Fixture.FetchAsync(h.Alice, h.Mediator, session, 2))!.Value.Messages.Select(m => m.Id).Should().Equal(ids[0], ids[1]);

        // Acknowledged without any session, mixed with unknown ids and a repeat: a leased one and a waiting one go.
        h.ResetTraces();
        var request = h.MessagesReceived(h.Alice, new[] { "no-such-id", ids[0], ids[2], ids[0] });
        var status = (await h.SendAsync(request)).Status();

        status.MessageCount.Should().Be(2);
        status.TotalBytes.Should().Be(20);
        h.Store.Acknowledged.Should().ContainSingle().Which.Ids.Should().Equal("no-such-id", ids[0], ids[2], ids[0]);
        h.Store.Inner.MessageCount.Should().Be(2);
        (await h.DeliverableAsync(h.Alice)).Should().Equal(new[] { ids[3] }, "ids[1] is still under its lease");
    }

    // ---- live-delivery-change ---------------------------------------------------------------

    [Fact]
    public async Task Live_delivery_is_a_flag_of_one_session_reported_by_every_status_on_it_and_turned_off_again()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var socket = h.Live(h.Alice);
        var otherSocket = h.Live(h.Alice);
        var http = h.Http(h.Alice);

        (await h.SendAsync(h.StatusRequest(h.Alice), socket)).Status().LiveDelivery.Should().BeFalse("live mode is off until the recipient turns it on");

        (await h.SendAsync(h.LiveChange(h.Alice, true), socket)).Status().LiveDelivery.Should().BeTrue();

        // Every status that travels on that session says so …
        (await h.SendAsync(h.StatusRequest(h.Alice), socket)).Status().LiveDelivery.Should().BeTrue();
        (await h.SendAsync(h.MessagesReceived(h.Alice, new[] { "x" }), socket)).Status().LiveDelivery.Should().BeTrue();
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), socket)).Status().LiveDelivery.Should().BeTrue("an empty queue answers a delivery-request with status");
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 0), socket)).Status().LiveDelivery.Should().BeTrue();
        (await h.SendAsync(h.LiveChange(h.Alice, true), socket)).Status().LiveDelivery.Should().BeTrue("turning it on twice changes nothing");

        // … and no other connection of the same peer does.
        (await h.SendAsync(h.StatusRequest(h.Alice), otherSocket)).Status().LiveDelivery.Should().BeFalse();
        (await h.SendAsync(h.StatusRequest(h.Alice), http)).Status().LiveDelivery.Should().BeFalse();
        (await h.SendAsync(h.StatusRequest(h.Alice))).Status().LiveDelivery.Should().BeFalse();
        (await h.SendAsync(h.LiveChange(h.Alice, false), otherSocket)).Status().LiveDelivery.Should().BeFalse();
        (await h.SendAsync(h.StatusRequest(h.Alice), socket)).Status().LiveDelivery.Should().BeTrue("turning it off on another session leaves this one on");

        (await h.SendAsync(h.LiveChange(h.Alice, false), socket)).Status().LiveDelivery.Should().BeFalse();
        (await h.SendAsync(h.StatusRequest(h.Alice), socket)).Status().LiveDelivery.Should().BeFalse();
        (await h.SendAsync(h.LiveChange(h.Alice, false), socket)).Status().LiveDelivery.Should().BeFalse("turning it off twice changes nothing");

        socket.Attempts.Should().BeEmpty("a status is the reply; the handler pushes nothing but a delivery");
    }

    [Fact]
    public async Task Turning_live_delivery_on_leaves_what_is_queued_in_the_queue()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10), await h.QueueAsync(h.Alice, 20) };
        var socket = h.Live(h.Alice);
        h.ResetTraces();

        var status = (await h.SendAsync(h.LiveChange(h.Alice, true), socket)).Status();

        status.MessageCount.Should().Be(2);
        status.LiveDelivery.Should().BeTrue();
        socket.Attempts.Should().BeEmpty("messages already queued must still be asked for with delivery-request (FR-PICK-06)");
        h.StoreTrace.Should().Equal("GetStatusAsync");
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids);
    }

    // ---- reply identity, units and tolerance ------------------------------------------------

    [Fact]
    public async Task The_reply_comes_from_whichever_mediator_did_decrypted_the_request()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var secondMediator = await h.NewDidAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var id = await h.QueueAsync(h.Alice, 10);

        // The same recipient writes to the mediator's other DID: answered from that DID, and the
        // session that counts as hers is the one bound to that (peer, local) pair.
        var session = h.Http(h.Alice, secondMediator);
        var status = MessagePickup.CreateStatusRequest(h.Alice, secondMediator);
        (await h.SendAsync(status, session, mediator: secondMediator)).Reply!.From.Should().Be(secondMediator);

        var live = MessagePickup.CreateLiveDeliveryChange(h.Alice, secondMediator, true);
        (await h.SendAsync(live, session, mediator: secondMediator)).ShouldBeLiveModeNotSupported(live, secondMediator, h.Alice);

        var fetch = MessagePickup.CreateDeliveryRequest(h.Alice, secondMediator, 10);
        (await h.SendAsync(fetch, session, mediator: secondMediator)).ShouldBeNoReply();
        session.Sent.Should().ContainSingle().Which.ShouldBeDelivery(fetch, secondMediator, h.Alice, null, new[] { id });
    }

    [Fact]
    public async Task Status_times_are_whole_epoch_seconds_read_from_the_injected_clock()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var t0 = h.Start.ToUnixTimeSeconds();

        // First message 0.9 s into a second, second one 100.4 s later, read 3 599.95 s after the first.
        h.Clock.Now = h.Start + TimeSpan.FromMilliseconds(900);
        await h.QueueAsync(h.Alice, 10);
        h.Clock.Now = h.Start + TimeSpan.FromMilliseconds(101_300);
        await h.QueueAsync(h.Alice, 10);
        h.Clock.Now = h.Start + TimeSpan.FromMilliseconds(3_600_850);

        var request = h.StatusRequest(h.Alice);
        var outcome = await h.SendAsync(request);

        // 3.0 units: seconds (4.0 uses milliseconds). Every member is a JSON integer.
        outcome.ShouldBeStatus(request, h.Mediator, h.Alice, 2, 20, live: false, times: new MessagePickupHandlerTimes(3599, t0 + 101, t0));
        foreach (var member in new[] { "longest_waited_seconds", "newest_received_time", "oldest_received_time", "total_bytes", "message_count" })
            outcome.Reply!.Body![member]!.ToJsonString().Should().MatchRegex("^[0-9]+$", member);
    }

    [Fact]
    public async Task A_status_request_without_a_body_or_with_null_and_unknown_members_is_an_unfiltered_one()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.QueueAsync(h.Alice, 10);
        var t0 = h.Start.ToUnixTimeSeconds();
        var times = new MessagePickupHandlerTimes(0, t0, t0);

        var noBody = h.Raw("status-request", h.Alice, body: null);
        (await h.SendAsync(noBody)).ShouldBeStatus(noBody, h.Mediator, h.Alice, 1, 10, live: false, times: times);

        var explicitNull = h.Raw("status-request", h.Alice, new JsonObject { ["recipient_did"] = null, ["message_count"] = 99, ["extra"] = new JsonObject { ["recipient_did"] = h.Bob } });
        (await h.SendAsync(explicitNull)).ShouldBeStatus(explicitNull, h.Mediator, h.Alice, 1, 10, live: false, times: times);

        var fetch = h.Raw("delivery-request", h.Alice, new JsonObject { ["limit"] = 0, ["recipient_did"] = null, ["live_delivery"] = true });
        (await h.SendAsync(fetch)).ShouldBeStatus(fetch, h.Mediator, h.Alice, 1, 10, live: false, times: times);
    }

    [Theory]
    [InlineData("https://didcomm.org/messagepickup/3.1/status-request")]
    [InlineData("https://didcomm.org/messagepickup/3.0/STATUS_REQUEST")]
    [InlineData("https://didcomm.org/messagepickup/3.7/Status-Request")]
    public async Task A_compatible_minor_version_or_spelling_of_a_request_is_answered_with_the_3_0_type(string type)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);

        var request = PickupMessages.Raw(type, h.Alice, h.Mediator, new JsonObject());
        var outcome = await h.SendAsync(request);

        outcome.ShouldBeStatus(request, h.Mediator, h.Alice, 0, 0, live: false);
    }

    // ---- 2. messages a mediator does not act on ---------------------------------------------

    [Fact]
    public async Task Responses_and_unknown_names_are_ignored_whatever_they_carry()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var id = await h.QueueAsync(h.Alice, 10);
        var socket = h.Live(h.Alice);
        (await h.SendAsync(h.LiveChange(h.Alice, true), socket)).Status().LiveDelivery.Should().BeTrue();
        h.ResetTraces();
        var longThread = new string('t', 5000);

        var ignored = new[]
        {
            // The mediator's own messages, sent back at it.
            MessagePickup.CreateStatus(h.Alice, h.Mediator, new PickupStatus(7, LiveDelivery: false), "t"),
            MessagePickup.CreateDelivery(h.Alice, h.Mediator, new[] { new DeliveredMessage(id, PickupFixture.Payload(4)) }, "t"),
            // Names 3.0 does not define, carrying what a request would.
            h.Raw("no-such-message", h.Alice, new JsonObject { ["message_id_list"] = new JsonArray(id), ["live_delivery"] = false, ["limit"] = 10 }),
            h.Raw("message-received", h.Alice, new JsonObject { ["message_id_list"] = new JsonArray(id) }),
            h.Raw("live-delivery", h.Alice, new JsonObject { ["live_delivery"] = false }),
            // Bodies their own parsers would refuse: an ignored message is not parsed at all.
            h.Raw("status", h.Alice, new JsonObject { ["message_count"] = "seven" }),
            h.Raw("delivery", h.Alice, new JsonObject { ["recipient_did"] = 7 }),
            h.Raw("status", h.Alice, body: null),
            // A thread id no reply could echo: only the four requests are held to that bound.
            h.Raw("status", h.Alice, new JsonObject { ["message_count"] = 1 }, thid: longThread),
            h.Raw("no-such-message", h.Alice, new JsonObject(), id: longThread),
        };

        foreach (var message in ignored)
        {
            var outcome = await h.SendAsync(message, socket);
            outcome.Result.Should().Be(DispatchResult.NoReply, message.Type);
            outcome.Handler.Should().BeOfType<MessagePickupHandler>();
        }

        h.StoreTrace.Should().BeEmpty();
        h.MediationTrace.Should().BeEmpty();
        socket.Attempts.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(1);
        (await h.LiveFlagAsync(socket)).Should().BeTrue();
        var lines = h.Fixture.PickupLogs.Where(e => e.Formatted.Contains("ignored", StringComparison.Ordinal)).ToArray();
        lines.Should().HaveCount(ignored.Length).And.OnlyContain(e => e.Level == LogLevel.Debug);
    }

    [Theory]
    [InlineData("https://didcomm.org/messagepickup/4.0/status-request")]
    [InlineData("https://didcomm.org/message-pickup/4.0/status-request")]
    [InlineData("https://didcomm.org/message-pickup/4.0/delivery-request")]
    [InlineData("https://didcomm.org/message-pickup/4.0/messages-received")]
    [InlineData("https://didcomm.org/message-pickup/4.0/live-delivery-change")]
    [InlineData("https://didcomm.org/messagepickup/2.0/status-request")]
    public async Task A_type_of_another_major_version_is_not_answered_by_the_3_0_role(string type)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var id = await h.QueueAsync(h.Alice, 10);
        var socket = h.Live(h.Alice);
        h.ResetTraces();

        var body = new JsonObject { ["limit"] = 10, ["message_count_limit"] = 10, ["message_id_list"] = new JsonArray(id), ["live_delivery"] = true };
        var outcome = await h.SendAsync(PickupMessages.Raw(type, h.Alice, h.Mediator, body), socket);

        outcome.Reply.Should().BeNull();
        outcome.Result.Should().Be(DispatchResult.NoHandler, "Message Pickup 4.0 is another protocol (FR-PICK-12); nothing here serves it");
        h.StoreTrace.Should().BeEmpty();
        h.MediationTrace.Should().BeEmpty();
        socket.Attempts.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(1);
        (await h.LiveFlagAsync(socket)).Should().BeFalse();
    }

    [Fact]
    public async Task The_handler_serves_the_3_0_piuri_in_the_mediator_role()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var handler = h.Fixture.Host.Services.GetService(typeof(MessagePickupHandler)).Should().BeOfType<MessagePickupHandler>().Subject;

        handler.ProtocolUri.Should().Be("https://didcomm.org/messagepickup/3.0");
        handler.Roles.Should().Equal("mediator");
        var mutate = () => ((IList<string>)handler.Roles)[0] = "recipient";
        mutate.Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => new MessagePickupHandler(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task HandleAsync_refuses_null_arguments_and_a_token_that_is_already_cancelled_before_anything_is_applied()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var id = await h.QueueAsync(h.Alice, 10);
        var handler = (MessagePickupHandler)h.Fixture.Host.Services.GetService(typeof(MessagePickupHandler))!;
        var message = h.MessagesReceived(h.Alice, new[] { id });
        var received = await h.Fixture.ReceiveAsync(message, h.Mediator);
        var context = new ProtocolContext(received, new DidComm.Threading.ThreadState(message.Id), h.Fixture.Host.Client, h.Fixture.Host.Options, new DidComm.Threading.InMemoryThreadStateStore());
        h.ResetTraces();

        await FluentActions.Awaiting(() => handler.HandleAsync(null!, context, default)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => handler.HandleAsync(message, null!, default)).Should().ThrowAsync<ArgumentNullException>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await FluentActions.Awaiting(() => handler.HandleAsync(received.Message, context, cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => h.SendAsync(message, ct: cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();

        h.StoreTrace.Should().BeEmpty();
        h.MediationTrace.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(1);
    }
}
