using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// What an <see cref="IMessageStore"/> hands back is host code's answer, not a fact: it is checked
/// against the contract before any of it is delivered or reported. One record outside the lease
/// that was asked for and nothing of the batch goes out, no reply is sent, and every lease the
/// store did hand out is released (the plan's threat model, "Faults"; JC-15).
/// </summary>
public sealed class MessagePickupHandlerStoreContractTests
{
    private const string AnotherGrant = "a record of another grant among the caller's";
    private const string OnlyAnotherGrant = "nothing but a record of another grant";
    private const string TooMany = "more records than were asked for";
    private const string WrongRecipient = "a record for another recipient DID than the filter";
    private const string NullRecord = "a null record among real ones";
    private const string NullRecordFirst = "a null record before the real ones";
    private const string NoRecipientDid = "a record without a recipient DID";
    private const string NoGrant = "a record without a grant";
    private const string EmptyId = "a record with an empty id";
    private const string NullId = "a record with a null id";
    private const string SpaceInId = "a record whose id has a space";
    private const string SlashInId = "a record whose id has a slash";
    private const string PercentInId = "a record whose id has a percent sign";
    private const string NonAsciiId = "a record whose id has a non-ASCII letter";
    private const string LongId = "a record whose id is over 2048 characters";
    private const string DiesPartWay = "a list that dies after two records";
    private const string DiesAtOnce = "a list that dies before its first record";

    public static TheoryData<string> LeaseGarbage => new()
    {
        AnotherGrant, OnlyAnotherGrant, TooMany, WrongRecipient, NullRecord, NullRecordFirst, NoRecipientDid, NoGrant,
        EmptyId, NullId, SpaceInId, SlashInId, PercentInId, NonAsciiId, LongId, DiesPartWay, DiesAtOnce,
    };

    [Theory]
    [MemberData(nameof(LeaseGarbage))]
    public async Task A_lease_outside_what_was_asked_for_delivers_nothing_answers_nothing_and_is_released(string garbage)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var second = await h.NewDidAsync();
        await h.EnrolAsync(h.Alice, h.Alice, second);
        await h.EnrolAsync(h.Bob, h.Bob);
        var real = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(second, 20, 2), await h.QueueAsync(h.Alice, 30, 3) };
        var bobs = await h.QueueAsync(h.Bob, 5, 4);
        var session = h.Http(h.Alice);
        var now = h.Clock.Now;
        var payload = PickupFixture.Payload(7, 9);

        // The store really leases the caller's records, then hands back something else.
        string?[] handedOut = Array.Empty<string?>();
        h.Store.OnLease = async (request, ct) =>
        {
            IReadOnlyList<QueuedMessage> Extra(string? id) => new[] { new QueuedMessage(id!, h.Alice, h.Alice, payload, now) };
            var unfiltered = request with { RecipientDid = null, MaxMessages = 10 };
            IReadOnlyList<QueuedMessage> result;
            switch (garbage)
            {
                case AnotherGrant:
                    result = (await h.Store.Inner.LeaseAsync(request, ct)).Append(new QueuedMessage("foreign-id", h.Bob, h.Bob, payload, now)).ToArray();
                    break;
                case OnlyAnotherGrant:
                    result = new[] { new QueuedMessage("foreign-id", h.Bob, h.Bob, payload, now) };
                    break;
                case TooMany:
                case WrongRecipient:
                    result = await h.Store.Inner.LeaseAsync(unfiltered, ct);
                    break;
                case NullRecord:
                {
                    var taken = (await h.Store.Inner.LeaseAsync(request, ct)).ToList();
                    taken.Insert(1, null!);
                    result = taken;
                    break;
                }
                case NullRecordFirst:
                    result = new QueuedMessage[] { null! }.Concat(await h.Store.Inner.LeaseAsync(request, ct)).ToArray();
                    break;
                case NoRecipientDid:
                {
                    var taken = (await h.Store.Inner.LeaseAsync(request, ct)).ToArray();
                    taken[1] = taken[1] with { RecipientDid = null! };
                    result = taken;
                    break;
                }
                case NoGrant:
                {
                    var taken = (await h.Store.Inner.LeaseAsync(request, ct)).ToArray();
                    taken[2] = taken[2] with { GrantPeerDid = null! };
                    result = taken;
                    break;
                }
                case EmptyId:
                    result = (await h.Store.Inner.LeaseAsync(request, ct)).Concat(Extra(string.Empty)).ToArray();
                    break;
                case NullId:
                    result = (await h.Store.Inner.LeaseAsync(request, ct)).Concat(Extra(null)).ToArray();
                    break;
                case SpaceInId:
                    result = (await h.Store.Inner.LeaseAsync(request, ct)).Concat(Extra("id with-space")).ToArray();
                    break;
                case SlashInId:
                    result = Extra("id/with-slash").Concat(await h.Store.Inner.LeaseAsync(request, ct)).ToArray();
                    break;
                case PercentInId:
                    result = (await h.Store.Inner.LeaseAsync(request, ct)).Concat(Extra("id%2Fescaped")).ToArray();
                    break;
                case NonAsciiId:
                    result = (await h.Store.Inner.LeaseAsync(request, ct)).Concat(Extra("idé")).ToArray();
                    break;
                case LongId:
                    result = (await h.Store.Inner.LeaseAsync(request, ct)).Concat(Extra(new string('a', 2049))).ToArray();
                    break;
                case DiesPartWay:
                    result = new MessagePickupHandlerFaultyList<QueuedMessage>(await h.Store.Inner.LeaseAsync(request, ct), 2);
                    handedOut = real.Take(2).ToArray();
                    return result;
                case DiesAtOnce:
                    return new MessagePickupHandlerFaultyList<QueuedMessage>(await h.Store.Inner.LeaseAsync(request, ct), 0);
                default:
                    throw new ArgumentOutOfRangeException(nameof(garbage));
            }
            handedOut = result.Where(r => r is not null).Select(r => r.Id).ToArray();
            return result;
        };
        h.ResetTraces();
        var logged = h.Fixture.PickupLogs.Count;

        var message = garbage switch
        {
            TooMany => h.DeliveryRequest(h.Alice, 2),
            WrongRecipient => h.DeliveryRequest(h.Alice, 10, second),
            _ => h.DeliveryRequest(h.Alice, 10),
        };
        var outcome = await h.SendAsync(message, session);

        outcome.ShouldBeNoReply();
        session.Attempts.Should().BeEmpty("one wrong record and nothing of the batch is delivered");
        h.StoreTrace.Should().NotContain("GetStatusAsync", "a store that broke its contract is not asked again for a status to send");

        // Every id the store handed out goes back in a release under the caller's own key.
        var known = handedOut.Where(id => id is not null).Select(id => id!).ToArray();
        h.ReleasedIds.Should().BeEquivalentTo(known);
        h.Store.Released.Should().OnlyContain(call => call.GrantPeerDid == h.Alice);
        h.Store.ReleasedLeaseIds.Should().OnlyContain(id => id == h.Store.LeaseRequests.Single().Lease.Id, "what is given back is given back under the lease this request took");
        if (garbage is not (DiesPartWay or DiesAtOnce))
            (await h.DeliverableAsync(h.Alice)).Should().Equal(real, "what was leased for the failed request is deliverable again at once");
        (await h.DeliverableAsync(h.Bob)).Should().Equal(bobs);
        h.Store.Inner.MessageCount.Should().Be(4, "nothing is removed or purged because of it");

        // Logged by exception type; no DID, no queue id, no exception text.
        var line = h.Fixture.PickupLogs.Skip(logged).Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Error);
        line.Formatted.Should().Contain(message.Id);
        line.State["ExceptionType"].Should().Be(garbage is DiesPartWay or DiesAtOnce ? nameof(HandlerBoomException) : nameof(InvalidOperationException));
        line.Exception.Should().BeNull();
        line.Everything.Should().NotContain("did:").And.NotContain(HandlerBoomException.Marker).And.NotContain("foreign-id").And.NotContain("id with-space");
        foreach (var id in real)
            line.Everything.Should().NotContain(id);

        // The request after it, with a store that behaves again, delivers as if nothing happened.
        h.Store.OnLease = null;
        if (garbage is DiesPartWay or DiesAtOnce)
            h.Clock.Advance(TimeSpan.FromSeconds(61)); // the leases nobody could name end by their timeout
        var retry = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(retry, session)).ShouldBeNoReply();
        session.Sent.Should().ContainSingle().Which.ShouldBeDelivery(retry, h.Mediator, h.Alice, null, real);
    }

    [Fact]
    public async Task A_record_with_an_unusable_id_really_stored_under_the_grant_is_never_put_on_the_wire()
    {
        // No tampering this time: the store genuinely holds a record whose id cannot be an
        // attachment id (a host that filled the store its own way).
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var good = await h.QueueAsync(h.Alice, 10, 1);
        var now = h.Clock.Now;
        (await h.Store.Inner.EnqueueAsync(new QueuedMessage("bad id#1", h.Alice, h.Alice, PickupFixture.Payload(5), now), null, now, default)).Should().Be(EnqueueOutcome.Enqueued);
        var session = h.Http(h.Alice);
        h.ResetTraces();

        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), session)).ShouldBeNoReply();

        session.Attempts.Should().BeEmpty();
        h.ReleasedIds.Should().BeEquivalentTo(new[] { good, "bad id#1" });
        (await h.DeliverableAsync(h.Alice)).Should().Equal(good, "bad id#1");
        h.Fixture.PickupLogs.Should().OnlyContain(e => !e.Everything.Contains("bad id#1"));

        // A batch that stops before it is unaffected.
        var first = h.DeliveryRequest(h.Alice, 1);
        (await h.SendAsync(first, session)).ShouldBeNoReply();
        session.Sent.Should().ContainSingle().Which.ShouldBeDelivery(first, h.Mediator, h.Alice, null, new[] { good });
    }

    [Fact]
    public async Task A_null_lease_list_is_a_store_fault_not_an_empty_queue()
    {
        // LeaseAsync returns a list, "empty when nothing is deliverable". A null is outside the
        // contract like any other garbage: what the store did before returning it is unknown, so
        // the request is not answered with a status that claims to know the queue.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.QueueAsync(h.Alice, 10);
        var session = h.Http(h.Alice);
        h.Store.OnLease = (_, _) => new ValueTask<IReadOnlyList<QueuedMessage>>((IReadOnlyList<QueuedMessage>)null!);
        h.ResetTraces();
        var logged = h.Fixture.PickupLogs.Count;

        var request = h.DeliveryRequest(h.Alice, 10);
        var outcome = await h.SendAsync(request, session);

        session.Attempts.Should().BeEmpty();
        outcome.ShouldBeNoReply();
        h.StoreTrace.Should().Equal(new[] { "LeaseAsync" }, "no status is read after it, and there is no id to give back");
        var line = h.Fixture.PickupLogs.Skip(logged).Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Error);
        line.State["MessageId"].Should().Be(request.Id);
        line.State["ExceptionType"].Should().Be(nameof(InvalidOperationException));
        line.Everything.Should().NotContain("did:");
    }

    // ---- GetStatusAsync ---------------------------------------------------------------------

    public static TheoryData<string, string> StatusGarbage
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var garbage in new[] { "null", "a negative count", "negative bytes", "both negative" })
            {
                foreach (var request in new[] { "status-request", "filtered status-request", "delivery-request with limit 0", "delivery-request without a session", "delivery-request on an empty lease", "messages-received", "live-delivery-change{true}", "live-delivery-change{false}" })
                    data.Add(garbage, request);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(StatusGarbage))]
    public async Task A_status_outside_the_contract_is_not_reported(string garbage, string request)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2) };
        var socket = h.Live(h.Alice);
        if (request == "live-delivery-change{false}")
            (await h.SendAsync(h.LiveChange(h.Alice, true), socket)).Status().LiveDelivery.Should().BeTrue();
        if (request == "delivery-request on an empty lease")
            (await h.Fixture.FetchAsync(h.Alice, h.Mediator, h.Http(h.Alice), 10))!.Value.Messages.Should().HaveCount(2); // everything is out under a lease
        h.Store.OnGetStatus = (_, _, _, _) => new ValueTask<QueueStatus>(garbage switch
        {
            "null" => null!,
            "a negative count" => new QueueStatus(-1, 10, h.Start, h.Start),
            "negative bytes" => new QueueStatus(1, -1, h.Start, h.Start),
            _ => new QueueStatus(int.MinValue, long.MinValue, null, null),
        });
        h.ResetTraces();
        var logged = h.Fixture.PickupLogs.Count;

        var message = request switch
        {
            "status-request" => h.StatusRequest(h.Alice),
            "filtered status-request" => h.StatusRequest(h.Alice, h.Alice),
            "delivery-request with limit 0" => h.DeliveryRequest(h.Alice, 0),
            "delivery-request without a session" => h.DeliveryRequest(h.Alice, 10),
            "delivery-request on an empty lease" => h.DeliveryRequest(h.Alice, 10),
            "messages-received" => h.MessagesReceived(h.Alice, new[] { ids[0] }),
            "live-delivery-change{true}" => h.LiveChange(h.Alice, true),
            _ => h.LiveChange(h.Alice, false),
        };
        var outcome = await h.SendAsync(message, request == "delivery-request without a session" ? null : socket);

        outcome.ShouldBeNoReply();
        socket.Attempts.Should().BeEmpty();
        h.StoreTrace.Should().Contain("GetStatusAsync");
        var line = h.Fixture.PickupLogs.Skip(logged).Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Error);
        line.State["ExceptionType"].Should().Be(nameof(InvalidOperationException));
        line.State["MessageId"].Should().Be(message.Id);

        // Nothing is half-applied: the flag did not move, and nothing was leased or lost. Only an
        // acknowledgement — applied before any status is read — has taken effect.
        h.Store.OnGetStatus = null;
        (await h.LiveFlagAsync(socket)).Should().Be(request == "live-delivery-change{false}", "the status is read first; without it the flag does not move");
        h.Store.Inner.MessageCount.Should().Be(request == "messages-received" ? 1 : 2);
        if (request != "delivery-request on an empty lease")
            (await h.DeliverableAsync(h.Alice)).Should().Equal(request == "messages-received" ? ids.Skip(1) : ids);
    }

    [Fact]
    public async Task Receive_times_ahead_of_the_clock_or_before_the_epoch_never_put_a_negative_member_on_the_wire()
    {
        // Not garbage: a record stamped by another node whose clock runs ahead is within the
        // contract. The reply must still be a valid status.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var now = h.Clock.Now;

        h.Store.OnGetStatus = (_, _, _, _) => new ValueTask<QueueStatus>(new QueueStatus(2, 30, now + TimeSpan.FromHours(1), now + TimeSpan.FromHours(2)));
        var ahead = h.StatusRequest(h.Alice);
        (await h.SendAsync(ahead)).ShouldBeStatus(ahead, h.Mediator, h.Alice, 2, 30, live: false,
            times: new MessagePickupHandlerTimes(0, now.ToUnixTimeSeconds() + 7200, now.ToUnixTimeSeconds() + 3600));

        h.Store.OnGetStatus = (_, _, _, _) => new ValueTask<QueueStatus>(new QueueStatus(1, 5, DateTimeOffset.MinValue, DateTimeOffset.UnixEpoch - TimeSpan.FromSeconds(1)));
        var ancient = h.StatusRequest(h.Alice);
        var status = (await h.SendAsync(ancient)).Status();
        status.OldestReceivedTime.Should().Be(0);
        status.NewestReceivedTime.Should().Be(0);
        status.LongestWaitedSeconds.Should().Be((long)Math.Floor((now - DateTimeOffset.MinValue).TotalSeconds));
    }
}
