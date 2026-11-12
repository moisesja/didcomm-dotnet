using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// L-054 for the pickup role: every collaborator is made to fail on every request path, not only
/// on the path named after it. For each cell: nothing but the documented answer comes out of
/// dispatch, nothing is half-applied, and a lease the request took is released. Each row runs
/// with a working logger and with one that throws on every call. A cell whose collaborator is not
/// read on that path is in the table too, with the ordinary answer — that is what proves it is not
/// read there.
/// </summary>
public sealed class MessagePickupHandlerFaultTests
{
    // ---- the collaborators and how each fails -------------------------------------------------
    private const string GetGrant = "IMediationStore.GetGrantAsync throws";
    private const string FindGrant = "IMediationStore.FindGrantByRecipientDidAsync throws";
    private const string GetStatus = "IMessageStore.GetStatusAsync throws";
    private const string Lease = "IMessageStore.LeaseAsync throws";
    private const string Acknowledge = "IMessageStore.AcknowledgeAsync throws";
    private const string Release = "IMessageStore.ReleaseAsync throws";
    private const string Purge = "IMessageStore.PurgeRecipientAsync throws";
    private const string SendThrows = "session.TrySendAsync throws";
    private const string NotSent = "session.TrySendAsync answers NotSent";
    private const string TooLargeAlways = "session.TrySendAsync answers TooLarge every time";
    private const string TooLargeOnce = "session.TrySendAsync answers TooLarge, then Sent";
    private const string IsLiveThrows = "session.IsLive throws";
    private const string ClosedThrows = "session.Closed throws";
    private const string ClockThrows = "the clock throws";

    // ---- the requests -------------------------------------------------------------------------
    private const string Unfiltered = "unfiltered";
    private const string Filtered = "filtered by a registered DID";
    private const string Removed = "unfiltered, one DID of the batch no longer registered";
    private const string LimitZero = "limit 0";
    private const string NoSession = "no session";

    private const string None = "none";

    /// <summary>
    /// Alice holds a grant with two registered DIDs and three queued messages — #1 (10 bytes, her
    /// own DID), #2 (20 bytes, the second DID), #3 (30 bytes, her own DID) — and a live session
    /// whose failure mode the row chooses.
    /// </summary>
    private sealed class World : IAsyncDisposable
    {
        private int _sends;

        public required MessagePickupHandlerHarness H { get; init; }
        public required string Second { get; init; }
        public required string[] Ids { get; init; }
        public required int LoggedBefore { get; set; }
        public PickupFakeSession? Fake { get; private set; }
        public MessagePickupHandlerFaultySession? Faulty { get; private set; }
        public IReturnRouteSession Session => (IReturnRouteSession?)Fake ?? Faulty!;
        public Message[] Sent => Fake?.Sent.ToArray() ?? Faulty!.Sent.ToArray();

        /// <summary>The fault every armed collaborator throws: its text carries the marker and a DID, neither of which may surface anywhere.</summary>
        public Exception Boom() => new HandlerBoomException(HandlerBoomException.Marker + " " + H.Alice);

        public static async Task<World> CreateAsync(string fault, bool loggerDown, Action<MessagePickupOptions>? pickup = null)
        {
            var h = await MessagePickupHandlerHarness.CreateAsync(pickup, throwingLogger: loggerDown);
            var second = await h.NewDidAsync();
            await h.EnrolAsync(h.Alice, h.Alice, second);
            var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(second, 20, 2), await h.QueueAsync(h.Alice, 30, 3) };
            var world = new World { H = h, Second = second, Ids = ids, LoggedBefore = 0 };
            if (fault is IsLiveThrows or ClosedThrows)
                world.Faulty = new MessagePickupHandlerFaultySession(h.Alice, h.Mediator);
            else
                world.Fake = h.Live(h.Alice);
            return world;
        }

        public Task RemoveSecondFromTheGrantAsync()
            => H.Mediation.Inner.UpdateRecipientsAsync(H.Alice, new[] { new RecipientUpdate(Second, RecipientUpdateAction.Remove) }, CancellationToken.None).AsTask();

        public void Arm(string fault, Func<Exception> boom)
        {
            _sends = 0;
            switch (fault)
            {
                case GetGrant: H.Mediation.OnGetGrant = (_, _) => throw boom(); break;
                case FindGrant: H.Mediation.OnFindGrantByRecipientDid = (_, _) => throw boom(); break;
                case GetStatus: H.Store.OnGetStatus = (_, _, _, _) => throw boom(); break;
                case Lease: H.Store.OnLease = (_, _) => throw boom(); break;
                case Acknowledge: H.Store.OnAcknowledge = (_, _, _) => throw boom(); break;
                case Release: H.Store.OnRelease = (_, _, _, _) => throw boom(); break;
                case Purge: H.Store.OnPurgeRecipient = (_, _, _) => throw boom(); break;
                case SendThrows: Fake!.OnSend = (_, _) => throw boom(); break;
                case NotSent: Fake!.OnSend = (_, _) => ReturnRouteSendResult.NotSent; break;
                case TooLargeAlways: Fake!.OnSend = (_, _) => ReturnRouteSendResult.TooLarge; break;
                case TooLargeOnce: Fake!.OnSend = (_, _) => Interlocked.Increment(ref _sends) == 1 ? ReturnRouteSendResult.TooLarge : null; break;
                case IsLiveThrows: Faulty!.OnIsLive = () => throw boom(); break;
                case ClosedThrows: Faulty!.OnClosed = () => throw boom(); break;
                case ClockThrows: H.ClockFault = boom; break;
                default: throw new ArgumentOutOfRangeException(nameof(fault));
            }
        }

        public void Disarm()
        {
            H.Mediation.OnGetGrant = null;
            H.Mediation.OnFindGrantByRecipientDid = null;
            H.Store.OnGetStatus = null;
            H.Store.OnLease = null;
            H.Store.OnAcknowledge = null;
            H.Store.OnRelease = null;
            H.Store.OnPurgeRecipient = null;
            H.ClockFault = null;
            if (Fake is not null)
                Fake.OnSend = null;
            if (Faulty is not null)
            {
                Faulty.OnIsLive = () => true;
                Faulty.OnClosed = () => CancellationToken.None;
            }
        }

        /// <summary>Unpack with everything healthy, arm the fault, dispatch through the real dispatcher, disarm.</summary>
        public async Task<DispatchOutcome> DispatchAsync(Message message, string fault, Func<Exception>? boom = null, bool withSession = true, CancellationToken ct = default)
        {
            var received = await H.Fixture.ReceiveAsync(message, H.Mediator);
            H.ResetTraces();
            LoggedBefore = H.Fixture.PickupLogs.Count;
            Arm(fault, boom ?? Boom);
            try
            {
                return await H.Fixture.Host.Dispatcher.DispatchAsync(received, H.Fixture.Host.Client, H.Fixture.Host.Options, Task.CompletedTask, withSession ? Session : null, ct);
            }
            finally
            {
                Disarm();
            }
        }

        /// <summary>The pickup lines the last dispatch wrote. Whatever they say, they name no DID, no queue id and no exception text.</summary>
        public HandlerLogEntry[] LinesOfLastDispatch()
        {
            var lines = H.Fixture.PickupLogs.Skip(LoggedBefore).ToArray();
            foreach (var line in lines)
            {
                line.Exception.Should().BeNull();
                line.Everything.Should().NotContain("did:").And.NotContain(HandlerBoomException.Marker);
                foreach (var id in Ids)
                    line.Everything.Should().NotContain(id);
            }
            return lines;
        }

        /// <summary>A fault that ended a request is one Error line: the request id and the exception's type name.</summary>
        public void ShouldHaveLoggedTheFault(Message request, bool loggerDown, string exceptionType = nameof(HandlerBoomException))
        {
            if (loggerDown)
                return;
            var errors = LinesOfLastDispatch().Where(e => e.Level == LogLevel.Error).ToArray();
            var line = errors.Should().ContainSingle().Subject;
            line.State["MessageId"].Should().Be(request.Id);
            line.State["ExceptionType"].Should().Be(exceptionType);
        }

        public ValueTask DisposeAsync() => H.DisposeAsync();
    }

    private static TheoryData<string, bool, string, bool> WithBothLoggers(params (string Fault, bool Flag, string Expected)[] rows)
    {
        var data = new TheoryData<string, bool, string, bool>();
        foreach (var (fault, flag, expected) in rows)
        {
            data.Add(fault, flag, expected, false);
            data.Add(fault, flag, expected, true);
        }
        return data;
    }

    // ---- status-request -----------------------------------------------------------------------

    public static TheoryData<string, bool, string, bool> StatusRequestCells => WithBothLoggers(
        // fault, filtered?, the answer
        (GetGrant, false, None),
        (GetGrant, true, "status"), // a filtered request is authorised by the recipient DID alone
        (FindGrant, false, "status"), // an unfiltered request never looks a recipient DID up
        (FindGrant, true, None),
        (GetStatus, false, None),
        (GetStatus, true, None),
        (ClockThrows, false, None),
        (ClockThrows, true, None),
        (Lease, false, "status"),
        (Lease, true, "status"),
        (Acknowledge, false, "status"),
        (Acknowledge, true, "status"),
        (Release, false, "status"),
        (Release, true, "status"),
        (Purge, false, "status"),
        (Purge, true, "status"),
        (SendThrows, false, "status"), // a status is the reply, never a push
        (SendThrows, true, "status"),
        (NotSent, false, "status"),
        (TooLargeAlways, false, "status"),
        (IsLiveThrows, false, "status"),
        (IsLiveThrows, true, "status"),
        (ClosedThrows, false, "status"),
        (ClosedThrows, true, "status"));

    [Theory]
    [MemberData(nameof(StatusRequestCells))]
    public async Task Status_request_against_every_collaborator_fault(string fault, bool filtered, string expected, bool loggerDown)
    {
        await using var w = await World.CreateAsync(fault, loggerDown);
        var h = w.H;
        var t0 = h.Start.ToUnixTimeSeconds();

        var request = h.StatusRequest(h.Alice, filtered ? w.Second : null);
        var outcome = await w.DispatchAsync(request, fault);

        if (expected == None)
        {
            outcome.ShouldBeNoReply();
            w.ShouldHaveLoggedTheFault(request, loggerDown);
        }
        else if (filtered)
        {
            outcome.ShouldBeStatus(request, h.Mediator, h.Alice, 1, 20, live: false, recipientDid: w.Second, times: new MessagePickupHandlerTimes(0, t0, t0));
        }
        else
        {
            outcome.ShouldBeStatus(request, h.Mediator, h.Alice, 3, 60, live: false, times: new MessagePickupHandlerTimes(0, t0, t0));
        }

        // A status request never writes: nothing pushed, nothing leased, released, removed or purged.
        w.Sent.Should().BeEmpty();
        h.StoreTrace.Should().OnlyContain(call => call == "GetStatusAsync");
        (await h.DeliverableAsync(h.Alice)).Should().Equal(w.Ids);
        if (!loggerDown)
            w.LinesOfLastDispatch().Should().OnlyContain(e => e.Level == LogLevel.Error, "an answered status request logs nothing");
    }

    // ---- delivery-request ---------------------------------------------------------------------

    public static TheoryData<string, string, string, bool> DeliveryRequestCells
    {
        get
        {
            var rows = new (string Fault, string Variant, string Expected)[]
            {
                // "delivery:…" = the queue numbers pushed in one delivery; "status:n" = a status with that count.
                (GetGrant, Unfiltered, None),
                (GetGrant, Filtered, "delivery:2"),
                (GetGrant, Removed, None),
                (GetGrant, LimitZero, None),
                (GetGrant, NoSession, None),

                // The L-054 case: the lease is taken, then the delivery-time registration check fails.
                (FindGrant, Unfiltered, None),
                (FindGrant, Filtered, None),
                (FindGrant, Removed, None),
                (FindGrant, LimitZero, "status:3"),
                (FindGrant, NoSession, "status:3"),

                // The status is read only when nothing is delivered.
                (GetStatus, Unfiltered, "delivery:1,2,3"),
                (GetStatus, Filtered, "delivery:2"),
                (GetStatus, Removed, "delivery:1,3"),
                (GetStatus, LimitZero, None),
                (GetStatus, NoSession, None),

                (Lease, Unfiltered, None),
                (Lease, Filtered, None),
                (Lease, Removed, None),
                (Lease, LimitZero, "status:3"),
                (Lease, NoSession, "status:3"),

                // Read by a delivery only when the delivery-time check found leased records of a DID
                // that is gone: those records — exactly those, by id — are removed with it. When that
                // fails the batch fails as a whole: released, nothing removed, nothing delivered.
                (Acknowledge, Unfiltered, "delivery:1,2,3"),
                (Acknowledge, Filtered, "delivery:2"),
                (Acknowledge, Removed, None),
                (Acknowledge, LimitZero, "status:3"),

                // Not read when the first send is taken (the trim has its own tests below).
                (Release, Unfiltered, "delivery:1,2,3"),
                (Release, Filtered, "delivery:2"),
                (Release, Removed, "delivery:1,3"),
                (Release, LimitZero, "status:3"),

                // Never read by a request: the role removes the stale records it leased, by id, and
                // leaves purging a DID to the host.
                (Purge, Unfiltered, "delivery:1,2,3"),
                (Purge, Filtered, "delivery:2"),
                (Purge, Removed, "delivery:1,3"),
                (Purge, LimitZero, "status:3"),
                (Purge, NoSession, "status:3"),

                (SendThrows, Unfiltered, None),
                (SendThrows, Filtered, None),
                (SendThrows, Removed, None),
                (SendThrows, LimitZero, "status:3"),
                (SendThrows, NoSession, "status:3"),

                (NotSent, Unfiltered, None),
                (NotSent, Filtered, None),
                (NotSent, Removed, None),
                (NotSent, LimitZero, "status:3"),

                // Trimmed from the end down to the first message, and that one, alone, is still over
                // the budget: it is neither removed nor given back — it keeps the request's lease and
                // is passed over (" passing over #…") — and the same request leases again and tries
                // what is behind it. With a session that finds everything too large that ends with
                // every message passed over, one after the other, and the status, which still counts
                // all that is stored.
                (TooLargeAlways, Unfiltered, "status:3 passing over #1,2,3"),
                (TooLargeAlways, Filtered, "status:1 passing over #2"),
                (TooLargeAlways, Removed, "status:2 passing over #1,3"),
                (TooLargeAlways, LimitZero, "status:3"),

                // One too many: the last message goes back and the rest is delivered. A batch of one
                // has no "rest": the first answer already is the verdict on that message alone, and
                // the next lease round finds nothing more in the filter's scope.
                (TooLargeOnce, Unfiltered, "delivery:1,2"),
                (TooLargeOnce, Filtered, "status:1 passing over #2"),
                (TooLargeOnce, Removed, "delivery:1"),

                (IsLiveThrows, Unfiltered, None),
                (IsLiveThrows, Filtered, None),
                (IsLiveThrows, Removed, None),
                (IsLiveThrows, LimitZero, "status:3"),
                (IsLiveThrows, NoSession, "status:3"),

                (ClosedThrows, Unfiltered, None),
                (ClosedThrows, Filtered, None),
                (ClosedThrows, Removed, None),
                (ClosedThrows, LimitZero, "status:3"),
                (ClosedThrows, NoSession, "status:3"),

                (ClockThrows, Unfiltered, None),
                (ClockThrows, Filtered, None),
                (ClockThrows, Removed, None),
                (ClockThrows, LimitZero, None),
                (ClockThrows, NoSession, None),
            };
            var data = new TheoryData<string, string, string, bool>();
            foreach (var (fault, variant, expected) in rows)
            {
                data.Add(fault, variant, expected, false);
                data.Add(fault, variant, expected, true);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(DeliveryRequestCells))]
    public async Task Delivery_request_against_every_collaborator_fault(string fault, string variant, string expected, bool loggerDown)
    {
        await using var w = await World.CreateAsync(fault, loggerDown);
        var h = w.H;
        var t0 = h.Start.ToUnixTimeSeconds();
        if (variant == Removed)
            await w.RemoveSecondFromTheGrantAsync();

        var request = variant switch
        {
            Filtered => h.DeliveryRequest(h.Alice, 10, w.Second),
            LimitZero => h.DeliveryRequest(h.Alice, 0),
            _ => h.DeliveryRequest(h.Alice, 10),
        };
        var outcome = await w.DispatchAsync(request, fault, withSession: variant != NoSession);

        // "… passing over #a,b": the messages that, alone in the delivery this request asked for,
        // were still over the byte budget, in the order the request came to them. None is removed
        // or given back: each keeps the request's lease, so it is passed over until that lease
        // ends, and the request goes on to what is queued behind it (JC-19).
        const string passing = " passing over #";
        var passedOver = Array.Empty<int>();
        var answer = expected;
        if (expected.IndexOf(passing, StringComparison.Ordinal) is var at and >= 0)
        {
            passedOver = expected[(at + passing.Length)..].Split(',').Select(n => int.Parse(n, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            answer = expected[..at];
        }

        // The one thing a delivery-request ever removes: the records it leased itself for a DID
        // that is no longer registered to the peer — here #2, once the delivery-time check has
        // got that far — by id, with AcknowledgeAsync. It never purges a DID.
        var removesTheStaleRecord = variant == Removed && fault is not (GetGrant or FindGrant or Lease or IsLiveThrows or ClosedThrows or ClockThrows);
        var staleRecordIsGone = removesTheStaleRecord && fault != Acknowledge;
        var storedIds = w.Ids.Where((_, i) => !(staleRecordIsGone && i == 1)).ToArray();
        (await h.StoredAsync(h.Alice)).Should().Be(storedIds.Length);
        h.Store.PurgedRecipients.Should().BeEmpty("the role removes the stale records it leased and leaves purging a DID to the host");
        if (removesTheStaleRecord)
        {
            var removal = h.Store.Acknowledged.Should().ContainSingle().Subject;
            removal.GrantPeerDid.Should().Be(h.Alice);
            removal.Ids.Should().Equal(w.Ids[1]);
        }
        else
        {
            h.Store.Acknowledged.Should().BeEmpty("a delivery-request removes nothing else — not even a message it cannot deliver");
        }

        string[] delivered = Array.Empty<string>();
        var kept = passedOver.Select(n => w.Ids[n - 1]).ToArray();
        if (answer == None)
        {
            outcome.ShouldBeNoReply();
            w.Sent.Should().BeEmpty("nothing is delivered when the request fails");
            if (fault is not NotSent)
                w.ShouldHaveLoggedTheFault(request, loggerDown);
        }
        else if (answer.StartsWith("status:", StringComparison.Ordinal))
        {
            // The status counts what is stored in the scope the request named — a message that was
            // passed over included: it is stored, due and unexpired.
            var sizes = new Dictionary<string, int> { [w.Ids[0]] = 10, [w.Ids[1]] = 20, [w.Ids[2]] = 30 };
            var scope = variant == Filtered ? storedIds.Where(id => id == w.Ids[1]).ToArray() : storedIds;
            scope.Should().HaveCount(int.Parse(answer["status:".Length..], System.Globalization.CultureInfo.InvariantCulture));
            w.Sent.Should().BeEmpty();
            outcome.ShouldBeStatus(request, h.Mediator, h.Alice, scope.Length, scope.Sum(id => sizes[id]), live: false,
                recipientDid: variant == Filtered ? w.Second : null,
                times: scope.Length > 0 ? new MessagePickupHandlerTimes(0, t0, t0) : null);
        }
        else
        {
            delivered = answer["delivery:".Length..].Split(',').Select(n => w.Ids[int.Parse(n, System.Globalization.CultureInfo.InvariantCulture) - 1]).ToArray();
            outcome.ShouldBeNoReply();
            w.Sent.Should().ContainSingle().Which.ShouldBeDelivery(request, h.Mediator, h.Alice, variant == Filtered ? w.Second : null, delivered);
        }

        // The trim itself, where the session answers TooLarge: how many attachments each attempt
        // carried, which message was given back after each (always the last one of the batch), and
        // the store calls in order. A message that does not fit alone costs no store call; the
        // request leases again and goes on, and reads the status only when a round finds nothing.
        (int[] Attempts, int[] GivenBack, string[] Trace)? trim = (fault, variant) switch
        {
            (TooLargeAlways, Unfiltered) => (new[] { 3, 2, 1, 2, 1, 1 }, new[] { 3, 2, 3 },
                new[] { "LeaseAsync", "ReleaseAsync", "ReleaseAsync", "PassOverAsync", "LeaseAsync", "ReleaseAsync", "PassOverAsync", "LeaseAsync", "PassOverAsync", "LeaseAsync", "GetStatusAsync" }),
            (TooLargeAlways, Filtered) => (new[] { 1 }, Array.Empty<int>(), new[] { "LeaseAsync", "PassOverAsync", "LeaseAsync", "GetStatusAsync" }),
            (TooLargeAlways, Removed) => (new[] { 2, 1, 1 }, new[] { 3 },
                new[] { "LeaseAsync", "AcknowledgeAsync", "ReleaseAsync", "PassOverAsync", "LeaseAsync", "PassOverAsync", "LeaseAsync", "GetStatusAsync" }),
            (TooLargeOnce, Unfiltered) => (new[] { 3, 2 }, new[] { 3 }, new[] { "LeaseAsync", "ReleaseAsync" }),
            (TooLargeOnce, Filtered) => (new[] { 1 }, Array.Empty<int>(), new[] { "LeaseAsync", "PassOverAsync", "LeaseAsync", "GetStatusAsync" }),
            (TooLargeOnce, Removed) => (new[] { 2, 1 }, new[] { 3 }, new[] { "LeaseAsync", "AcknowledgeAsync", "ReleaseAsync" }),
            _ => null,
        };
        if (trim is { } expectedTrim)
        {
            w.Fake!.Attempts.Select(a => a.Message.Attachments!.Count).Should().Equal(expectedTrim.Attempts);
            h.Store.Released.Select(r => string.Join(",", r.Ids)).Should().Equal(expectedTrim.GivenBack.Select(n => w.Ids[n - 1]));
            h.StoreTrace.Should().Equal(expectedTrim.Trace);
        }

        // The only leases that outlive a request that did not deliver under them are those on the
        // messages it passed over: everything else that is stored and was not pushed is deliverable
        // again at once.
        (await h.DeliverableAsync(h.Alice)).Should().Equal(storedIds.Except(delivered).Except(kept));
        if (kept.Length > 0)
        {
            // … and those are exactly as the request leased them: under its lease for the whole
            // timeout, and back at their old positions when it ends.
            h.Clock.Advance(TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(1));
            (await h.DeliverableAsync(h.Alice)).Should().NotContain(kept, "the lease is the request's, with its whole timeout");
            h.Clock.Advance(TimeSpan.FromTicks(1));
            (await h.DeliverableAsync(h.Alice)).Should().Equal(storedIds.Except(delivered), "when the lease ends the messages are back at their old positions");
        }

        // One request, one lease, however many rounds it took — and whatever the request gave back,
        // it gave back under that lease.
        h.Store.ReleasedLeaseIds.Should().HaveCount(h.Store.Released.Count);
        if (h.Store.LeaseRequests.Count > 0)
        {
            var leaseId = h.Store.LeaseRequests.Select(r => r.Lease.Id).Distinct().Should().ContainSingle().Subject;
            h.Store.ReleasedLeaseIds.Should().OnlyContain(id => id == leaseId);
        }
        else
        {
            h.Store.Released.Should().BeEmpty("a request that took no lease has nothing to give back");
        }

        if (!loggerDown)
        {
            // Passing a message over is said once per message, with the request id, the size and
            // the running count — no DID, no queue id (LinesOfLastDispatch checks every line for
            // those). Any enrolled peer can cause the line, so only the first is a Warning.
            var lines = w.LinesOfLastDispatch();
            lines.Should().NotContain(e => e.Formatted.Contains("removed: alone", StringComparison.Ordinal), "nothing is removed for being too large");
            var passedOverLines = lines.Where(e => e.Formatted.Contains("even alone; it stays queued and is passed over", StringComparison.Ordinal)).ToArray();
            passedOverLines.Select(e => e.State["Bytes"]).Should().Equal(passedOver.Select(n => (object?)(10 * n)));
            passedOverLines.Select(e => e.State["Count"]).Should().Equal(passedOver.Select((_, i) => (object?)(long)(i + 1)));
            passedOverLines.Select(e => e.Level).Should().Equal(passedOver.Select((_, i) => i == 0 ? LogLevel.Warning : LogLevel.Debug));
            foreach (var line in passedOverLines)
            {
                line.State.Keys.Should().BeEquivalentTo("MessageId", "Bytes", "Count", "{OriginalFormat}");
                line.State["MessageId"].Should().Be(request.Id);
            }
        }
    }

    // ---- messages-received --------------------------------------------------------------------

    public static TheoryData<string, bool, string, bool> MessagesReceivedCells => WithBothLoggers(
        // fault, is the acknowledged message removed?, the answer
        (Acknowledge, false, None),
        // JC-16: the acknowledgement is applied before, and without, any grant lookup or status read.
        (GetGrant, true, None),
        (GetStatus, true, None),
        (ClockThrows, true, None),
        (FindGrant, true, "status"),
        (Lease, true, "status"),
        (Release, true, "status"),
        (Purge, true, "status"),
        (SendThrows, true, "status"),
        (NotSent, true, "status"),
        (TooLargeAlways, true, "status"),
        (IsLiveThrows, true, "status"),
        (ClosedThrows, true, "status"));

    [Theory]
    [MemberData(nameof(MessagesReceivedCells))]
    public async Task Messages_received_against_every_collaborator_fault(string fault, bool removed, string expected, bool loggerDown)
    {
        await using var w = await World.CreateAsync(fault, loggerDown);
        var h = w.H;
        var t0 = h.Start.ToUnixTimeSeconds();

        var request = h.MessagesReceived(h.Alice, new[] { w.Ids[0], "no-such-id" });
        var outcome = await w.DispatchAsync(request, fault);

        if (expected == None)
        {
            outcome.ShouldBeNoReply();
            w.ShouldHaveLoggedTheFault(request, loggerDown);
        }
        else
        {
            outcome.ShouldBeStatus(request, h.Mediator, h.Alice, 2, 50, live: false, times: new MessagePickupHandlerTimes(0, t0, t0));
        }

        // All or nothing: the named message is gone, or (the acknowledgement itself failed) every message is still there.
        (await h.DeliverableAsync(h.Alice)).Should().Equal(removed ? w.Ids.Skip(1) : w.Ids);
        w.Sent.Should().BeEmpty();
        h.StoreTrace[0].Should().Be("AcknowledgeAsync", "the acknowledgement comes first");
        if (fault == Acknowledge)
            h.MediationTrace.Should().BeEmpty("a failed acknowledgement is not followed by a status");
        if (!loggerDown)
            w.LinesOfLastDispatch();
    }

    // ---- live-delivery-change -----------------------------------------------------------------

    public static TheoryData<string, bool, string, bool> LiveDeliveryChangeCells => WithBothLoggers(
        // fault, turn it on (true) or off (false), the answer
        (GetGrant, true, None),
        (GetGrant, false, None),
        (GetStatus, true, None),
        (GetStatus, false, None),
        (ClockThrows, true, None),
        (ClockThrows, false, None),
        (IsLiveThrows, true, None),
        (IsLiveThrows, false, None),
        (ClosedThrows, true, None),
        (ClosedThrows, false, None),
        (FindGrant, true, "status"),
        (FindGrant, false, "status"),
        (Lease, true, "status"),
        (Acknowledge, true, "status"),
        (Release, true, "status"),
        (Release, false, "status"),
        (Purge, true, "status"),
        (SendThrows, true, "status"), // the status is the reply; the handler pushes nothing here
        (SendThrows, false, "status"),
        (NotSent, true, "status"),
        (TooLargeAlways, true, "status"));

    [Theory]
    [MemberData(nameof(LiveDeliveryChangeCells))]
    public async Task Live_delivery_change_against_every_collaborator_fault(string fault, bool turnOn, string expected, bool loggerDown)
    {
        await using var w = await World.CreateAsync(fault, loggerDown);
        var h = w.H;
        var t0 = h.Start.ToUnixTimeSeconds();
        // To see "turn it off" fail, it has to be on first.
        if (!turnOn)
            (await h.SendAsync(h.LiveChange(h.Alice, true), w.Session)).Status().LiveDelivery.Should().BeTrue();

        var request = h.LiveChange(h.Alice, turnOn);
        var outcome = await w.DispatchAsync(request, fault);

        if (expected == None)
        {
            // Neither a status nor the problem report: the mediator does not know the queue, and
            // the flag has not moved (the status is read before the flag is touched).
            outcome.ShouldBeNoReply();
            w.ShouldHaveLoggedTheFault(request, loggerDown);
            (await h.LiveFlagAsync(w.Session)).Should().Be(!turnOn, "live delivery is unchanged when the request fails");
        }
        else
        {
            outcome.ShouldBeStatus(request, h.Mediator, h.Alice, 3, 60, live: turnOn, times: new MessagePickupHandlerTimes(0, t0, t0));
            (await h.LiveFlagAsync(w.Session)).Should().Be(turnOn);
        }

        w.Sent.Should().BeEmpty();
        (await h.DeliverableAsync(h.Alice)).Should().Equal(w.Ids);
        if (!loggerDown)
            w.LinesOfLastDispatch();
    }

    // ---- the delivery-time registration check (L-054) -----------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_registration_check_that_fails_part_way_releases_the_whole_batch_delivers_nothing_and_purges_nothing(int failingLookup)
    {
        // Three distinct recipient DIDs in the batch; the first of them is no longer registered.
        // The lookup for the n-th one fails. Whatever was learnt before it is not acted on: the
        // check is the input of a refusal and fails with it.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var gone = await h.NewDidAsync();
        var kept = await h.NewDidAsync();
        await h.EnrolAsync(h.Alice, gone, kept, h.Alice);
        var ids = new[] { await h.QueueAsync(gone, 10, 1), await h.QueueAsync(kept, 20, 2), await h.QueueAsync(h.Alice, 30, 3), await h.QueueAsync(gone, 40, 4) };
        await h.Mediation.Inner.UpdateRecipientsAsync(h.Alice, new[] { new RecipientUpdate(gone, RecipientUpdateAction.Remove) }, default);
        var session = h.Http(h.Alice);
        var lookups = 0;
        h.Mediation.OnFindGrantByRecipientDid = (did, ct) => Interlocked.Increment(ref lookups) == failingLookup
            ? throw new HandlerBoomException(HandlerBoomException.Marker)
            : h.Mediation.Inner.FindGrantByRecipientDidAsync(did, ct);
        h.ResetTraces();

        var outcome = await h.SendAsync(h.DeliveryRequest(h.Alice, 10), session);

        outcome.ShouldBeNoReply();
        session.Attempts.Should().BeEmpty();
        h.StoreTrace.Should().Equal("LeaseAsync", "ReleaseAsync");
        var release = h.Store.Released.Should().ContainSingle().Subject;
        release.GrantPeerDid.Should().Be(h.Alice);
        release.Ids.Should().BeEquivalentTo(ids);
        h.Store.PurgedRecipients.Should().BeEmpty("nothing is purged on the strength of a check that did not finish");
        h.Store.Acknowledged.Should().BeEmpty("and nothing is removed on it either");
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("a DID URL")]
    public async Task A_registry_answer_that_names_no_usable_peer_is_a_fault_of_the_check_not_an_unregistered_did(string peerDidOfTheGrant)
    {
        // "Registered to nobody we can name" must not be read as "not registered to this peer":
        // that would remove the record. The check failed; the batch fails with it (L-054).
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var second = await h.NewDidAsync();
        await h.EnrolAsync(h.Alice, h.Alice, second);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(second, 20, 2) };
        var session = h.Http(h.Alice);
        var unusable = peerDidOfTheGrant switch
        {
            "null" => null!,
            "empty" => string.Empty,
            _ => h.Alice + "#key-1",
        };
        h.Mediation.OnFindGrantByRecipientDid = (did, ct) => did == second
            ? new ValueTask<MediationGrant?>(new MediationGrant(unusable, new[] { h.Mediator }))
            : h.Mediation.Inner.FindGrantByRecipientDidAsync(did, ct);
        h.ResetTraces();

        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), session)).ShouldBeNoReply();

        session.Attempts.Should().BeEmpty();
        h.StoreTrace.Should().Equal("LeaseAsync", "ReleaseAsync");
        h.ReleasedIds.Should().BeEquivalentTo(ids);
        h.Store.Acknowledged.Should().BeEmpty("nothing is removed");
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids);
    }

    [Fact]
    public async Task A_removal_of_stale_records_that_fails_releases_the_whole_batch_delivers_nothing_and_removes_nothing()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var gone1 = await h.NewDidAsync();
        var gone2 = await h.NewDidAsync();
        await h.EnrolAsync(h.Alice, h.Alice, gone1, gone2);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(gone1, 20, 2), await h.QueueAsync(gone2, 30, 3) };
        await h.Mediation.Inner.UpdateRecipientsAsync(h.Alice, new[] { new RecipientUpdate(gone1, RecipientUpdateAction.Remove), new RecipientUpdate(gone2, RecipientUpdateAction.Remove) }, default);
        var session = h.Http(h.Alice);
        h.Store.OnAcknowledge = (_, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker);
        h.ResetTraces();

        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), session)).ShouldBeNoReply();

        session.Attempts.Should().BeEmpty("the still-registered message is not delivered either: the batch failed as a whole");
        // One removal was asked for — of exactly the two leased records whose DID is gone — and failed.
        var attempted = h.Store.Acknowledged.Should().ContainSingle().Subject;
        attempted.GrantPeerDid.Should().Be(h.Alice);
        attempted.Ids.Should().Equal(ids[1], ids[2]);
        h.StoreTrace.Should().Equal("LeaseAsync", "AcknowledgeAsync", "ReleaseAsync");
        h.ReleasedIds.Should().BeEquivalentTo(ids);
        h.Store.PurgedRecipients.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(3, "nothing is removed when the removal fails");
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids, "and nothing stays leased");

        // The next request, with the store working again, finishes the job: the same two records
        // removed by id, the still-registered message delivered.
        h.Store.OnAcknowledge = null;
        h.ResetTraces();
        var retry = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(retry, session)).ShouldBeNoReply();
        session.Sent.Should().ContainSingle().Which.ShouldBeDelivery(retry, h.Mediator, h.Alice, null, new[] { ids[0] });
        h.Store.Acknowledged.Should().ContainSingle().Which.Ids.Should().Equal(ids[1], ids[2]);
        h.Store.PurgedRecipients.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(1);
    }

    // ---- the size-bounded send ----------------------------------------------------------------

    [Fact]
    public async Task A_delivery_over_the_byte_budget_loses_its_last_message_until_it_fits()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
            ids.Add(await h.QueueAsync(h.Alice, 10 + i, (byte)i));
        var session = h.Http(h.Alice);
        // The session's verdict on the packed bytes: anything with more than two attachments is too large.
        session.OnSend = (message, _) => message.Attachments!.Count > 2 ? ReturnRouteSendResult.TooLarge : null;
        h.ResetTraces();

        var request = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(request, session)).ShouldBeNoReply();

        session.Attempts.Select(a => (a.Message.Attachments!.Count, a.Result)).Should().Equal(
            (5, ReturnRouteSendResult.TooLarge), (4, ReturnRouteSendResult.TooLarge), (3, ReturnRouteSendResult.TooLarge), (2, ReturnRouteSendResult.Sent));
        session.Attempts.Should().OnlyContain(a => a.MaxPackedBytes == 1024 * 1024);
        session.Sent.Should().ContainSingle().Which.ShouldBeDelivery(request, h.Mediator, h.Alice, null, ids.Take(2).ToArray());
        // The LAST message is given back each time, one release per trim, in that order.
        h.Store.Released.Select(r => r.GrantPeerDid).Should().OnlyContain(did => did == h.Alice);
        h.Store.Released.Select(r => string.Join(",", r.Ids)).Should().Equal(ids[4], ids[3], ids[2]);
        h.StoreTrace.Should().Equal("LeaseAsync", "ReleaseAsync", "ReleaseAsync", "ReleaseAsync");
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids.Skip(2), "the trimmed messages are deliverable again at once, in their old position");

        // Every attempt is a fresh message on the same thread.
        session.Attempts.Select(a => a.Message.Thid).Should().OnlyContain(thid => thid == request.Id);
    }

    [Fact]
    public async Task When_nothing_fits_even_alone_one_request_passes_every_message_over_in_turn_and_is_answered_with_status()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2), await h.QueueAsync(h.Alice, 30, 3) };
        var socket = h.Live(h.Alice);
        (await h.SendAsync(h.LiveChange(h.Alice, true), socket)).Status().LiveDelivery.Should().BeTrue();
        socket.OnSend = (_, _) => ReturnRouteSendResult.TooLarge;
        var t0 = h.Start.ToUnixTimeSeconds();
        var times = new MessagePickupHandlerTimes(0, t0, t0);
        h.ResetTraces();
        var logged = h.Fixture.PickupLogs.Count;

        var request = h.DeliveryRequest(h.Alice, 10);
        var outcome = await h.SendAsync(request, socket);

        // JC-19: the batch is trimmed from the end down to its first message, and the delivery that
        // carries that one alone is still over the byte budget. Nothing is removed and that message
        // is not given back: it keeps this request's lease. The same request then leases again and
        // tries what is behind it — here with the same result, twice more — and when a lease round
        // finds nothing left the peer is told the state, which still counts all three.
        outcome.ShouldBeStatus(request, h.Mediator, h.Alice, 3, 60, live: true, times: times);
        socket.Attempts.Select(a => string.Join(",", a.Message.Attachments!.Select(x => Array.IndexOf(ids, x.Id)))).Should().Equal("0,1,2", "0,1", "0", "1,2", "1", "2");
        socket.Sent.Should().BeEmpty();
        h.Store.Released.Select(r => string.Join(",", r.Ids)).Should().Equal(ids[2], ids[1], ids[2]);
        h.Store.Released.Should().OnlyContain(r => r.GrantPeerDid == h.Alice);
        h.Store.Acknowledged.Should().BeEmpty("a message that does not fit is not removed");
        h.StoreTrace.Should().Equal("LeaseAsync", "ReleaseAsync", "ReleaseAsync", "PassOverAsync", "LeaseAsync", "ReleaseAsync", "PassOverAsync", "LeaseAsync", "PassOverAsync", "LeaseAsync", "GetStatusAsync");
        h.Store.Inner.MessageCount.Should().Be(3);
        (await h.DeliverableAsync(h.Alice)).Should().BeEmpty("each of the three keeps the request's lease");

        // Four lease rounds, one lease: the same request object's lease, which the socket owns.
        var lease = h.Store.LeaseRequests.Select(r => r.Lease).Distinct().Should().ContainSingle().Subject;
        h.Store.LeaseRequests.Should().HaveCount(4);
        lease.Owner.Should().NotBeNullOrEmpty();
        h.Store.ReleasedLeaseIds.Should().OnlyContain(id => id == lease.Id);

        // One line per message passed over, with the request id, the size and the running count; a
        // line any enrolled peer can cause, so rate-limited.
        var lines = h.Fixture.PickupLogs.Skip(logged).ToArray();
        lines.Select(e => e.Level).Should().Equal(LogLevel.Warning, LogLevel.Debug, LogLevel.Debug);
        lines.Select(e => e.State["Bytes"]).Should().Equal(10, 20, 30);
        lines.Select(e => e.State["Count"]).Should().Equal(1L, 2L, 3L);
        foreach (var line in lines)
        {
            line.Formatted.Should().Contain("even alone; it stays queued and is passed over for a period that doubles each time");
            line.State.Keys.Should().BeEquivalentTo("MessageId", "Bytes", "Count", "{OriginalFormat}");
            line.State["MessageId"].Should().Be(request.Id);
        }
        lines.Should().OnlyContain(e => e.Exception == null && !e.Everything.Contains("did:") && ids.All(id => !e.Everything.Contains(id)));

        // The next request, the clock not moved: there is nothing to try. It says so with the
        // status and logs nothing.
        h.ResetTraces();
        var second = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(second, socket)).ShouldBeStatus(second, h.Mediator, h.Alice, 3, 60, live: true, times: times);
        h.StoreTrace.Should().Equal("LeaseAsync", "GetStatusAsync");
        socket.Attempts.Should().HaveCount(6, "3 + 2 + 1 attempts by the first request, and none by the second");
        h.Fixture.PickupLogs.Skip(logged).Should().HaveCount(3);
        h.Store.Acknowledged.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(3);

        // All three were given up under the first request's lease and are under no lease now:
        // each is passed over for one lease timeout, and nothing but time brings them up again.
        h.Store.PassedOver.Select(p => (string.Join(",", p.Ids), p.LeaseId)).Should().Equal((ids[0], lease.Id), (ids[1], lease.Id), (ids[2], lease.Id));
        (await h.Store.Inner.ReleaseAsync(h.Alice, ids, lease.Id, default)).Should().Be(0);
        (await h.DeliverableAsync(h.Alice)).Should().BeEmpty();
        h.Clock.Advance(TimeSpan.FromSeconds(60));
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids);
    }

    [Fact]
    public async Task A_session_that_does_not_take_the_delivery_gets_no_reply_and_the_batch_is_released_at_once()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2), await h.QueueAsync(h.Alice, 30, 3) };
        var session = h.Http(h.Alice);
        // Too large once, then the session is gone (no free slot, downgraded, closing).
        session.OnSend = (message, _) => message.Attachments!.Count == 3 ? ReturnRouteSendResult.TooLarge : ReturnRouteSendResult.NotSent;
        h.ResetTraces();
        var logged = h.Fixture.PickupLogs.Count;

        var request = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(request, session)).ShouldBeNoReply();

        session.Sent.Should().BeEmpty();
        h.Store.Released.Select(r => string.Join(",", r.Ids)).Should().Equal(ids[2], ids[0] + "," + ids[1]);
        h.StoreTrace.Should().Equal("LeaseAsync", "ReleaseAsync", "ReleaseAsync");
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids);
        var line = h.Fixture.PickupLogs.Skip(logged).Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Information);
        line.State["MessageId"].Should().Be(request.Id);
        line.State["Count"].Should().Be(2);
        line.Everything.Should().NotContain("did:");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_release_that_fails_during_the_trim_ends_the_request_and_the_rest_is_released_quietly(bool storeStaysDown)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2), await h.QueueAsync(h.Alice, 30, 3) };
        var session = h.Http(h.Alice);
        session.OnSend = (_, _) => ReturnRouteSendResult.TooLarge;
        var releases = 0;
        h.Store.OnRelease = (grant, messageIds, lease, ct) => Interlocked.Increment(ref releases) == 1 || storeStaysDown
            ? throw new HandlerBoomException(HandlerBoomException.Marker)
            : h.Store.Inner.ReleaseAsync(grant, messageIds, lease, ct);
        h.ResetTraces();
        var logged = h.Fixture.PickupLogs.Count;

        var request = h.DeliveryRequest(h.Alice, 10);
        var outcome = await h.SendAsync(request, session);

        outcome.ShouldBeNoReply();
        session.Attempts.Should().ContainSingle("the request ends where the store failed; it does not go on trimming");
        session.Sent.Should().BeEmpty();
        // First the release of the trimmed message (failed), then one for everything the request still held — that message included.
        h.Store.Released.Select(r => string.Join(",", r.Ids)).Should().Equal(ids[2], string.Join(",", ids));
        h.StoreTrace.Should().Equal("LeaseAsync", "ReleaseAsync", "ReleaseAsync");
        var lines = h.Fixture.PickupLogs.Skip(logged).ToArray();
        lines.Should().OnlyContain(e => e.Exception == null && !e.Everything.Contains(HandlerBoomException.Marker) && !e.Everything.Contains("did:"));
        lines.Should().Contain(e => e.Level == LogLevel.Error && Equals(e.State["MessageId"], request.Id) && Equals(e.State["ExceptionType"], nameof(HandlerBoomException)));

        if (storeStaysDown)
        {
            // Nothing could be released: said once, at Warning, and the leases end by their timeout.
            lines.Should().Contain(e => e.Level == LogLevel.Warning && Equals(e.State["Count"], 3) && Equals(e.State["ExceptionType"], nameof(HandlerBoomException)));
            (await h.DeliverableAsync(h.Alice)).Should().BeEmpty();
            h.Clock.Advance(TimeSpan.FromSeconds(60));
            (await h.DeliverableAsync(h.Alice)).Should().Equal(ids);
        }
        else
        {
            (await h.DeliverableAsync(h.Alice)).Should().Equal(ids);
        }
    }

    // ---- cancellation -------------------------------------------------------------------------

    public static TheoryData<string, string> CancelledCells => new()
    {
        { GetGrant, "status-request" },
        { GetGrant, "delivery-request" },
        { GetGrant, "messages-received" },
        { GetGrant, "live-delivery-change{true}" },
        { FindGrant, "filtered status-request" },
        { FindGrant, "filtered delivery-request" },
        { FindGrant, "delivery-request" },
        { GetStatus, "status-request" },
        { GetStatus, "delivery-request with limit 0" },
        { GetStatus, "messages-received" },
        { GetStatus, "live-delivery-change{true}" },
        { Lease, "delivery-request" },
        { Acknowledge, "messages-received" },
        { Acknowledge, "delivery-request after a DID was removed" },
        { SendThrows, "delivery-request" },
        { SendThrows, "filtered delivery-request" },
        { IsLiveThrows, "delivery-request" },
        { IsLiveThrows, "live-delivery-change{true}" },
        { ClosedThrows, "delivery-request" },
        { ClosedThrows, "live-delivery-change{true}" },
        { ClockThrows, "status-request" },
        { ClockThrows, "delivery-request" },
        { ClockThrows, "messages-received" },
        { ClockThrows, "live-delivery-change{true}" },
    };

    [Theory]
    [MemberData(nameof(CancelledCells))]
    public async Task When_the_dispatch_token_is_cancelled_while_a_collaborator_throws_only_cancellation_leaves(string fault, string request)
    {
        await using var w = await World.CreateAsync(fault, loggerDown: false);
        var h = w.H;
        if (request == "delivery-request after a DID was removed")
            await w.RemoveSecondFromTheGrantAsync();
        using var cts = new CancellationTokenSource();
        // The collaborator notices the cancellation and fails with its own exception and its own text.
        Exception CancelAndThrow()
        {
            cts.Cancel();
            return w.Boom();
        }
        var releaseTokens = new List<CancellationToken>();
        h.Store.OnRelease = (grant, ids, lease, ct) =>
        {
            releaseTokens.Add(ct);
            return h.Store.Inner.ReleaseAsync(grant, ids, lease, ct);
        };
        var message = request switch
        {
            "status-request" => h.StatusRequest(h.Alice),
            "filtered status-request" => h.StatusRequest(h.Alice, w.Second),
            "filtered delivery-request" => h.DeliveryRequest(h.Alice, 10, w.Second),
            "delivery-request with limit 0" => h.DeliveryRequest(h.Alice, 0),
            "messages-received" => h.MessagesReceived(h.Alice, new[] { w.Ids[0] }),
            "live-delivery-change{true}" => h.LiveChange(h.Alice, true),
            _ => h.DeliveryRequest(h.Alice, 10),
        };
        var received = await h.Fixture.ReceiveAsync(message, h.Mediator);
        h.ResetTraces();
        var logged = h.Fixture.PickupLogs.Count;
        w.Arm(fault, CancelAndThrow);
        // Arm() replaced the release hook for the Release fault only; keep the recorder otherwise.

        var thrown = await Record.ExceptionAsync(() => h.Fixture.Host.Dispatcher.DispatchAsync(received, h.Fixture.Host.Client, h.Fixture.Host.Options, Task.CompletedTask, w.Session, cts.Token));
        w.Disarm();

        thrown.Should().BeOfType<OperationCanceledException>("only cancellation leaves a cancelled dispatch, never the collaborator's own exception");
        thrown!.Message.Should().NotContain(HandlerBoomException.Marker).And.NotContain("did:");
        thrown.InnerException.Should().BeNull();
        ((OperationCanceledException)thrown).CancellationToken.Should().Be(cts.Token);
        thrown.ToString().Should().NotContain(HandlerBoomException.Marker);

        // A lease taken before the cancellation is still released: the clean-up does not run under the cancelled token.
        w.Sent.Should().BeEmpty();
        releaseTokens.Should().OnlyContain(token => !token.IsCancellationRequested && !token.CanBeCanceled);
        if (h.Store.LeaseRequests.Count > 0 && fault != Lease)
            h.ReleasedIds.Should().NotBeEmpty();
        var acknowledged = request == "messages-received" && fault != Acknowledge;
        (await h.DeliverableAsync(h.Alice)).Should().Equal(acknowledged ? w.Ids.Skip(1) : w.Ids);
        (await h.LiveFlagAsync(w.Session)).Should().BeFalse();
        h.Fixture.PickupLogs.Skip(logged).Should().OnlyContain(e => !e.Everything.Contains(HandlerBoomException.Marker) && !e.Everything.Contains("did:"));
    }

    [Fact]
    public async Task A_trim_interrupted_by_cancellation_still_gives_every_lease_back()
    {
        // The session reports TooLarge and the dispatch token is cancelled at that moment: the
        // release of the trimmed message runs under the cancelled token and fails, and the
        // clean-up — which does not — releases everything.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2), await h.QueueAsync(h.Alice, 30, 3) };
        var session = h.Http(h.Alice);
        using var cts = new CancellationTokenSource();
        session.OnSend = (_, _) =>
        {
            cts.Cancel();
            return ReturnRouteSendResult.TooLarge;
        };
        h.ResetTraces();

        var thrown = await Record.ExceptionAsync(() => h.SendAsync(h.DeliveryRequest(h.Alice, 10), session, ct: cts.Token));

        thrown.Should().BeAssignableTo<OperationCanceledException>();
        session.Sent.Should().BeEmpty();
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids);
    }

    public static TheoryData<string, string> OwnCancellationCells => new()
    {
        { GetGrant, "status-request" },
        { GetStatus, "messages-received" },
        { FindGrant, "delivery-request" },
        { Lease, "delivery-request" },
        { SendThrows, "delivery-request" },
        { GetStatus, "live-delivery-change{true}" },
    };

    [Theory]
    [MemberData(nameof(OwnCancellationCells))]
    public async Task A_collaborators_own_cancellation_exception_is_a_fault_like_any_other_while_the_dispatch_token_is_live(string fault, string request)
    {
        // A driver's internal timeout surfaces as OperationCanceledException. The dispatch token is
        // not cancelled, so nothing may leave: no reply, and its text stays out of the log.
        await using var w = await World.CreateAsync(fault, loggerDown: false);
        var h = w.H;
        var message = request switch
        {
            "status-request" => h.StatusRequest(h.Alice),
            "messages-received" => h.MessagesReceived(h.Alice, new[] { w.Ids[0] }),
            "live-delivery-change{true}" => h.LiveChange(h.Alice, true),
            _ => h.DeliveryRequest(h.Alice, 10),
        };

        var outcome = await w.DispatchAsync(message, fault, () => new TaskCanceledException(HandlerBoomException.Marker + " " + h.Alice));

        outcome.ShouldBeNoReply();
        w.Sent.Should().BeEmpty();
        w.ShouldHaveLoggedTheFault(message, loggerDown: false, exceptionType: nameof(TaskCanceledException));
        (await h.DeliverableAsync(h.Alice)).Should().Equal(request == "messages-received" ? w.Ids.Skip(1) : w.Ids);
        (await h.LiveFlagAsync(w.Session)).Should().BeFalse();
    }

    // ---- a session whose identity cannot be read ----------------------------------------------

    [Theory]
    [InlineData("PeerDid", false)]
    [InlineData("LocalDid", false)]
    [InlineData("PeerDid", true)]
    [InlineData("LocalDid", true)]
    public async Task A_session_whose_identity_cannot_be_read_is_handled_as_no_session_and_never_makes_dispatch_throw(string member, bool loggerDown)
    {
        // "A store, registry or session fault never leaves the handler." Which pair a session is
        // bound to is read before anything else is done with it; a session that fails there is not
        // this peer's connection: every request is answered as it is without a session (JC-9, JC-10).
        await using var w = await World.CreateAsync(IsLiveThrows, loggerDown);
        var h = w.H;
        var t0 = h.Start.ToUnixTimeSeconds();
        var times = new MessagePickupHandlerTimes(0, t0, t0);

        async Task<DispatchOutcome> SendOnTheBrokenSessionAsync(Message message)
        {
            var received = await h.Fixture.ReceiveAsync(message, h.Mediator);
            h.ResetTraces();
            w.LoggedBefore = h.Fixture.PickupLogs.Count;
            if (member == "PeerDid")
                w.Faulty!.OnPeerDid = () => throw w.Boom();
            else
                w.Faulty!.OnLocalDid = () => throw w.Boom();
            try
            {
                return await h.Fixture.Host.Dispatcher.DispatchAsync(received, h.Fixture.Host.Client, h.Fixture.Host.Options, Task.CompletedTask, w.Session, CancellationToken.None);
            }
            finally
            {
                w.Faulty.OnPeerDid = null;
                w.Faulty.OnLocalDid = null;
            }
        }

        // Said once per request, at Warning: the request id and the exception's type name.
        void ShouldHaveSaidSoOnce(Message request)
        {
            if (loggerDown)
                return;
            var line = w.LinesOfLastDispatch().Should().ContainSingle().Subject;
            line.Level.Should().Be(LogLevel.Warning);
            line.State["MessageId"].Should().Be(request.Id);
            line.State["ExceptionType"].Should().Be(nameof(HandlerBoomException));
        }

        var status = h.StatusRequest(h.Alice);
        (await SendOnTheBrokenSessionAsync(status)).ShouldBeStatus(status, h.Mediator, h.Alice, 3, 60, live: false, times: times);
        ShouldHaveSaidSoOnce(status);

        // No connection to carry a batch on: nothing is leased, the status instead.
        var fetch = h.DeliveryRequest(h.Alice, 10);
        (await SendOnTheBrokenSessionAsync(fetch)).ShouldBeStatus(fetch, h.Mediator, h.Alice, 3, 60, live: false, times: times);
        h.Store.LeaseRequests.Should().BeEmpty();
        ShouldHaveSaidSoOnce(fetch);

        var ack = h.MessagesReceived(h.Alice, new[] { "no-such-id" });
        (await SendOnTheBrokenSessionAsync(ack)).ShouldBeStatus(ack, h.Mediator, h.Alice, 3, 60, live: false, times: times);
        ShouldHaveSaidSoOnce(ack);

        // No connection that could do live delivery: the problem report, and the flag stays off.
        var on = h.LiveChange(h.Alice, true);
        (await SendOnTheBrokenSessionAsync(on)).ShouldBeLiveModeNotSupported(on, h.Mediator, h.Alice);
        ShouldHaveSaidSoOnce(on);

        var off = h.LiveChange(h.Alice, false);
        (await SendOnTheBrokenSessionAsync(off)).ShouldBeStatus(off, h.Mediator, h.Alice, 3, 60, live: false, times: times);
        ShouldHaveSaidSoOnce(off);

        w.Sent.Should().BeEmpty("a session that cannot say whose it is is never written to");
        (await h.DeliverableAsync(h.Alice)).Should().Equal(w.Ids);
        (await h.LiveFlagAsync(w.Session)).Should().BeFalse();
    }
}
