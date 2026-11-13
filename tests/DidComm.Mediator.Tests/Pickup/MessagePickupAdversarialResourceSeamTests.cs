using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// ADV-2, goal 4: the host seams, where the fault is not a throw. A store, a mediation registry, a
/// session registry, an id generator, a session or a logger that answers — late, wrongly, twice,
/// with garbage, or by calling back in. Each must end fail-closed: no exception out of dispatch
/// (but the caller's own cancellation), nothing delivered that should not be, nothing deleted on a
/// garbage answer, and no lease left behind.
/// </summary>
public sealed class MessagePickupAdversarialResourceSeamTests
{
    public MessagePickupAdversarialResourceSeamTests() => MessagePickupAdversarialResource.RaiseThreadPoolFloor();

    // ---- the mediation registry answers with something that is not a grant -------------------------

    public static TheoryData<string> GrantsThatAreNotGrants => new() { "a null peer DID", "an empty peer DID", "a DID URL as the peer DID", "a peer DID over 2048 characters" };

    private static MediationGrant NotAGrant(string what, string peer) => what switch
    {
        "a null peer DID" => new MediationGrant(null!, new[] { "did:example:mediator" }),
        "an empty peer DID" => new MediationGrant(string.Empty, new[] { "did:example:mediator" }),
        "a DID URL as the peer DID" => new MediationGrant(peer + "#key-1", new[] { "did:example:mediator" }),
        _ => new MediationGrant("did:example:" + new string('a', 2048), new[] { "did:example:mediator" }),
    };

    /// <summary>
    /// The delivery-time check deletes what is queued for a DID that "no longer authorises the
    /// peer". A registry row that names no usable peer at all (a corrupt read, a half-written
    /// record) says nothing about who the DID belongs to: it is an answer outside the contract, the
    /// check could not be made, and "if the check cannot be made, the leases are released and
    /// nothing is delivered" (L-054) — not "everything queued for the DID is deleted".
    /// </summary>
    [Theory]
    [MemberData(nameof(GrantsThatAreNotGrants))]
    public async Task A_registry_answer_that_names_no_usable_peer_is_a_check_that_could_not_be_made_and_deletes_nothing(string what)
    {
        var mediation = new MessagePickupAdversarialResourceMediation();
        await using var fixture = PickupFixture.Create(services: c => c.AddSingleton<IMediationStore>(mediation));
        var (mediator, peer) = await fixture.EnrolledAsync();
        var ids = new[] { await fixture.QueueAsync(peer, PickupFixture.Payload(30, 1)), await fixture.QueueAsync(peer, PickupFixture.Payload(30, 2)) };

        mediation.OnFind = (_, _) => new ValueTask<MediationGrant?>(NotAGrant(what, peer));
        var session = fixture.Session(peer, mediator);
        var outcome = await fixture.SendAsync(MessagePickupAdversarialResource.DeliveryRequest(peer, mediator), mediator, session);

        session.Sent.Should().BeEmpty("nothing is delivered on an answer that authorises nobody");
        fixture.Memory.MessageCount.Should().Be(2, "a registry answer outside its contract is a fault of the check, not proof that the DID was removed: nothing is deleted on it");

        // With the registry answering properly again both messages are there, and not under a lease.
        mediation.OnFind = null;
        (await MessagePickupAdversarialResource.FetchIdsAsync(fixture, peer, mediator)).Should().Equal(ids);
        outcome.Reply.Should().BeNull("a check that could not be made is answered with nothing (JC-15)");
    }

    /// <summary>The same answers on the way in: the message is refused, nothing is stored under a key that is no peer's, nothing is thrown.</summary>
    [Theory]
    [MemberData(nameof(GrantsThatAreNotGrants))]
    public async Task A_message_for_a_did_whose_registry_row_names_no_usable_peer_is_not_stored_under_anything(string what)
    {
        var mediation = new MessagePickupAdversarialResourceMediation();
        await using var fixture = PickupFixture.Create(services: c => c.AddSingleton<IMediationStore>(mediation));
        var (_, peer) = await fixture.EnrolledAsync();
        mediation.OnFind = (_, _) => new ValueTask<MediationGrant?>(NotAGrant(what, peer));

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30, 1));

        result.Outcome.Should().BeOneOf(PickupDeliveryOutcome.NotRegistered, PickupDeliveryOutcome.Faulted);
        result.MessageId.Should().BeNull();
        fixture.Memory.MessageCount.Should().Be(0);
    }

    // ---- the store answers after the caller has gone ---------------------------------------------

    /// <summary>
    /// A store that ignores the token and completes — on another thread — after the request was
    /// cancelled. What it leased in that last moment must not stay leased: only cancellation leaves,
    /// and the messages are deliverable at once.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_lease_that_completes_after_the_request_was_cancelled_is_given_back(bool filtered)
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var ids = new[] { await fixture.QueueAsync(peer, PickupFixture.Payload(30, 1)), await fixture.QueueAsync(peer, PickupFixture.Payload(30, 2)) };
        var store = fixture.FakeStore!;
        using var cts = new CancellationTokenSource();
        store.OnLease = async (request, _) =>
        {
            await Task.Yield();
            var taken = await store.Inner.LeaseAsync(request, CancellationToken.None);
            cts.Cancel();
            return taken;
        };

        var session = fixture.Session(peer, mediator);
        var act = () => fixture.SendAsync(MessagePickupAdversarialResource.DeliveryRequest(peer, mediator, 10, filtered ? peer : null), mediator, session, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        session.Sent.Should().BeEmpty();
        store.OnLease = null;
        (await MessagePickupAdversarialResource.FetchIdsAsync(fixture, peer, mediator)).Should().Equal(ids, "a cancelled request leaves no lease behind");
    }

    /// <summary>The same on the way in: a live push whose enqueue completes after the caller cancelled. The message is stored (documented) and must not stay under the dead push's lease.</summary>
    [Fact]
    public async Task A_live_push_whose_enqueue_completes_after_the_caller_cancelled_leaves_the_message_stored_and_not_leased()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var live = fixture.Session(peer, mediator, live: true);
        (await fixture.StatusOfAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, true), mediator, live)).LiveDelivery.Should().BeTrue();
        var store = fixture.FakeStore!;
        using var cts = new CancellationTokenSource();
        store.OnEnqueue = async (message, lease, now, _) =>
        {
            await Task.Yield();
            var outcome = await store.Inner.EnqueueAsync(message, lease, now, CancellationToken.None);
            cts.Cancel();
            return outcome;
        };

        var act = async () => await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30, 1), ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        live.Sent.Should().BeEmpty();
        fixture.Memory.MessageCount.Should().Be(1, "cancelling after the message was stored leaves it stored");
        (await MessagePickupAdversarialResource.FetchIdsAsync(fixture, peer, mediator)).Should().HaveCount(1, "the lease of a push that never happened is given back");
    }

    /// <summary>
    /// A store that never answers one peer's lease. The role holds no lock and no session slot
    /// while it waits: another peer is served, the stalled peer's own arrivals are accepted, and a
    /// push to its live session goes out.
    /// </summary>
    [Fact]
    public async Task A_store_that_never_answers_one_request_stalls_that_request_only()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var mediator = (await fixture.NewPeerAsync()).Did;
        var stalled = (await fixture.NewPeerAsync()).Did;
        var other = (await fixture.NewPeerAsync()).Did;
        await fixture.EnrolAsync(stalled, mediator, stalled);
        await fixture.EnrolAsync(other, mediator, other);
        await fixture.QueueAsync(stalled, PickupFixture.Payload(30, 1));
        var othersMessage = await fixture.QueueAsync(other, PickupFixture.Payload(30, 2));

        var store = fixture.FakeStore!;
        var gate = new MessagePickupAdversarialResourceGate();
        store.OnLease = async (request, ct) =>
        {
            if (request.GrantPeerDid == stalled)
                await gate.PassAsync();
            return await store.Inner.LeaseAsync(request, ct);
        };

        var live = fixture.Session(stalled, mediator, live: true);
        (await fixture.StatusOfAsync(MessagePickup.CreateLiveDeliveryChange(stalled, mediator, true), mediator, live)).LiveDelivery.Should().BeTrue();
        var stuck = fixture.SendAsync(MessagePickupAdversarialResource.DeliveryRequest(stalled, mediator), mediator, live);
        await gate.Arrived;

        (await MessagePickupAdversarialResource.FetchIdsAsync(fixture, other, mediator)).Should().Equal(othersMessage);
        (await MessagePickupAdversarialResource.StatusAsync(fixture, stalled, mediator)).MessageCount.Should().Be(1);
        var pushed = await fixture.Queue.DeliverAsync(stalled, PickupFixture.Payload(30, 3));
        pushed.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive, "the session is not held by the request that waits for the store");
        stuck.IsCompleted.Should().BeFalse();

        gate.Resume();
        await stuck.WaitAsync(MessagePickupAdversarialResource.Deadline);
        live.Sent.Should().HaveCount(2);
    }

    // ---- a status that is not a status -------------------------------------------------------------

    public static TheoryData<int, long, long?, long?> OddStatuses => new()
    {
        // count, bytes, oldest (ticks from the clock; null = absent), newest
        { 0, 5_000, 0, 0 },
        { 3, 0, null, null },
        { int.MaxValue, long.MaxValue, long.MinValue, long.MaxValue },
        { 1, 1, long.MaxValue, long.MinValue },
        { 2, 10, TimeSpan.TicksPerDay * 365, -TimeSpan.TicksPerDay * 365 },
    };

    /// <summary>Whatever a store reports — nothing waiting but bytes, dates at the ends of time, newest before oldest — the reply carries no negative member and nothing is thrown.</summary>
    [Theory]
    [MemberData(nameof(OddStatuses))]
    public async Task A_status_with_dates_at_the_ends_of_time_or_numbers_that_disagree_puts_no_negative_member_on_the_wire(int count, long bytes, long? oldest, long? newest)
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var now = fixture.Clock.Now;
        DateTimeOffset? At(long? ticks) => ticks switch
        {
            null => null,
            long.MinValue => DateTimeOffset.MinValue,
            long.MaxValue => DateTimeOffset.MaxValue,
            _ => now.AddTicks(ticks.Value),
        };
        fixture.FakeStore!.OnGetStatus = (_, _, _, _) => new ValueTask<QueueStatus>(new QueueStatus(count, bytes, At(oldest), At(newest)));

        var outcome = await fixture.SendAsync(MessagePickup.CreateStatusRequest(peer, mediator), mediator, fixture.Session(peer, mediator));

        if (outcome.Reply is null)
            return; // refused as outside the contract: also fail-closed
        MessagePickup.TryParseStatus(outcome.Reply, out var status).Should().BeTrue("what the mediator writes, a recipient can read");
        status!.MessageCount.Should().Be(count);
        (status.LongestWaitedSeconds ?? 0).Should().BeGreaterThanOrEqualTo(0);
        (status.OldestReceivedTime ?? 0).Should().BeGreaterThanOrEqualTo(0);
        (status.NewestReceivedTime ?? 0).Should().BeGreaterThanOrEqualTo(0);
        (status.TotalBytes ?? 0).Should().BeGreaterThanOrEqualTo(0);
    }

    // ---- a payload the store should never have held ------------------------------------------------

    /// <summary>An empty payload and one far over the budget, handed out by a store: nothing is thrown, and nothing stays leased unless it is the documented "does not fit alone" lease.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2 * 1024 * 1024)]
    public async Task A_record_with_an_empty_payload_or_one_far_over_the_budget_breaks_nothing(int size)
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var behind = await fixture.QueueAsync(peer, PickupFixture.Payload(30, 1));
        var store = fixture.FakeStore!;
        var odd = new QueuedMessage("odd-record", peer, peer, new byte[size], fixture.Clock.Now);
        store.OnLease = async (request, ct) => new[] { odd }.Concat(await store.Inner.LeaseAsync(request with { MaxMessages = request.MaxMessages - 1 }, ct)).ToArray();

        // The session packs for real, so the bound is the real one.
        var session = fixture.Session(peer, mediator, pack: true);
        var outcome = await fixture.SendAsync(MessagePickupAdversarialResource.DeliveryRequest(peer, mediator, 2), mediator, session);

        foreach (var attempt in session.Attempts)
        {
            if (attempt.Result == ReturnRouteSendResult.Sent)
                attempt.PackedBytes!.Value.Should().BeLessThanOrEqualTo(attempt.MaxPackedBytes, "nothing over max_delivery_bytes is ever sent, whatever the store handed out");
        }
        session.Attempts.Count.Should().BeLessThanOrEqualTo(2, "one pack per leased record at most");

        // The real record behind the odd one is not lost and not stuck.
        store.OnLease = null;
        var delivered = session.Sent.SelectMany(m => MessagePickup.TryParseDelivery(m, out var d, out string? _) ? d! : Array.Empty<DeliveredMessage>()).Select(m => m.Id).ToList();
        if (!delivered.Contains(behind))
            delivered.AddRange(await MessagePickupAdversarialResource.FetchIdsAsync(fixture, peer, mediator));
        delivered.Should().Contain(behind);
        (outcome.Reply is null || MessagePickup.TryParseStatus(outcome.Reply, out _)).Should().BeTrue();
    }

    // ---- the id generator that has one id ---------------------------------------------------------

    [Fact]
    public async Task An_id_generator_that_returns_one_id_for_ever_costs_two_attempts_per_message_and_stores_nothing_more()
    {
        var generator = new OneIdGenerator();
        await using var fixture = PickupFixture.Create(fakeStore: true, services: c => c.AddSingleton(new PickupIdSource(generator.NewId)));
        var (mediator, peer) = await fixture.EnrolledAsync();
        var other = (await fixture.NewPeerAsync()).Did;
        await fixture.EnrolAsync(other, mediator, other);

        var first = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30, 1));
        first.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        var callsBefore = fixture.FakeStore!.CountOf(nameof(IMessageStore.EnqueueAsync));

        var second = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30, 2));
        second.Outcome.Should().Be(PickupDeliveryOutcome.Faulted);
        second.MessageId.Should().BeNull();
        (fixture.FakeStore.CountOf(nameof(IMessageStore.EnqueueAsync)) - callsBefore).Should().Be(2, "one retry, not a loop");
        fixture.Memory.MessageCount.Should().Be(1);

        // The id is unique within a grant only: another grant takes the same id, and neither sees the other's.
        (await fixture.Queue.DeliverAsync(other, PickupFixture.Payload(30, 3))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        (await MessagePickupAdversarialResource.FetchIdsAsync(fixture, peer, mediator)).Should().Equal(first.MessageId!);
        fixture.Memory.MessageCount.Should().Be(2);
        (await fixture.SendAsync(MessagePickup.CreateMessagesReceived(peer, mediator, new[] { first.MessageId! }), mediator, fixture.Session(peer, mediator))).Reply.Should().NotBeNull();
        fixture.Memory.MessageCount.Should().Be(1, "acknowledging an id removes it under the caller's grant only");
    }

    private sealed class OneIdGenerator
    {
        public string NewId() => "the-one-id";
    }

    // ---- the session registry and the session ------------------------------------------------------

    /// <summary>A registry whose answer is enormous and full of sessions that throw from every member: the message is queued, nothing leaves, and the one good session at the end is still found.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_registry_answer_full_of_nulls_and_sessions_that_throw_ends_in_the_queue_or_at_the_good_session(bool throwingSessions)
    {
        var registry = new ScriptedRegistry();
        await using var fixture = PickupFixture.Create(services: c => c.AddSingleton<IReturnRouteSessionRegistry>(registry));
        var (mediator, peer) = await fixture.EnrolledAsync();
        var good = fixture.Session(peer, mediator, live: true, register: false);
        (await fixture.StatusOfAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, true), mediator, good)).LiveDelivery.Should().BeTrue();

        var list = new List<IReturnRouteSession>(100_001) { good };
        for (var i = 0; i < 100_000; i++)
            list.Add(throwingSessions && i == 99_999 ? new ThrowingSession() : null!);
        registry.Answer = list;

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30, 1));

        // Newest first: with the throwing session last in the list the registry "could not be read".
        result.Outcome.Should().Be(throwingSessions ? PickupDeliveryOutcome.Queued : PickupDeliveryOutcome.PushedLive);
        fixture.Memory.MessageCount.Should().Be(1);
        if (throwingSessions)
            (await MessagePickupAdversarialResource.FetchIdsAsync(fixture, peer, mediator)).Should().Equal(result.MessageId!);
    }

    private sealed class ScriptedRegistry : IReturnRouteSessionRegistry
    {
        public IReadOnlyList<IReturnRouteSession> Answer { get; set; } = Array.Empty<IReturnRouteSession>();

#pragma warning disable CS0067 // the events are part of the interface; this registry raises none
        public event EventHandler<ReturnRouteSessionEventArgs>? SessionOpened;
        public event EventHandler<ReturnRouteSessionEventArgs>? SessionClosed;
#pragma warning restore CS0067

        public SessionRegistration Register(IReturnRouteSession session) => new(SessionRegistrationOutcome.Registered, null);
        public bool Unregister(IReturnRouteSession session) => true;
        public int Count => Answer.Count;
        public IReadOnlyList<IReturnRouteSession> Find(string did) => Answer;
    }

    private sealed class ThrowingSession : IReturnRouteSession
    {
        public string PeerDid => throw new InvalidOperationException("session fault with did:example:secret in its text");
        public string LocalDid => throw new InvalidOperationException("session fault");
        public ReturnRouteMode Mode => throw new InvalidOperationException("session fault");
        public string? Thread => throw new InvalidOperationException("session fault");
        public ReturnRouteTransport Transport => throw new InvalidOperationException("session fault");
        public bool IsLive => throw new InvalidOperationException("session fault");
        public CancellationToken Closed => throw new InvalidOperationException("session fault");
        public ValueTask<bool> TrySendAsync(Message message, CancellationToken ct) => throw new InvalidOperationException("session fault");
        public ValueTask<ReturnRouteSendResult> TrySendAsync(Message message, int maxPackedBytes, CancellationToken ct) => throw new InvalidOperationException("session fault");
    }

    public static TheoryData<string> OddSendAnswers => new() { "an undefined enum value", "a negative enum value", "cancellation of somebody else's token", "a cancelled task of somebody else's token" };

    /// <summary>
    /// A session that answers with a value the enum does not define, or "cancelled" for a token
    /// that is not the caller's: not sent, not the caller's cancellation. Nothing leaves dispatch,
    /// and the leases are given back at once.
    /// </summary>
    [Theory]
    [MemberData(nameof(OddSendAnswers))]
    public async Task A_send_that_answers_outside_the_enum_or_with_a_foreign_cancellation_is_not_sent_and_not_the_callers_cancellation(string answer)
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var queued = await fixture.QueueAsync(peer, PickupFixture.Payload(30, 1));
        using var foreign = new CancellationTokenSource();
        foreign.Cancel();
        ReturnRouteSendResult? Odd(Message message, int bound) => answer switch
        {
            "an undefined enum value" => (ReturnRouteSendResult)99,
            "a negative enum value" => (ReturnRouteSendResult)(-1),
            "cancellation of somebody else's token" => throw new OperationCanceledException(foreign.Token),
            _ => throw new TaskCanceledException("cancelled elsewhere", null, foreign.Token),
        };

        // On a request…
        var session = fixture.Session(peer, mediator);
        session.OnSend = Odd;
        var outcome = await fixture.SendAsync(MessagePickupAdversarialResource.DeliveryRequest(peer, mediator), mediator, session);
        outcome.Reply.Should().BeNull();
        session.Sent.Should().BeEmpty();

        // …and on a live push.
        var live = fixture.Session(peer, mediator, live: true);
        (await fixture.StatusOfAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, true), mediator, live)).LiveDelivery.Should().BeTrue();
        live.OnSend = Odd;
        var pushed = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30, 2));
        pushed.Outcome.Should().Be(PickupDeliveryOutcome.Queued, "a push the session did not take leaves the message queued");

        (await MessagePickupAdversarialResource.FetchIdsAsync(fixture, peer, mediator)).Should().Equal(queued, pushed.MessageId!);
    }

    /// <summary>And the other way round: when the caller's token IS cancelled, a session that answers with its own exception does not hide it.</summary>
    [Fact]
    public async Task When_the_callers_token_is_cancelled_a_session_fault_does_not_swallow_the_cancellation_and_the_lease_is_given_back()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var queued = await fixture.QueueAsync(peer, PickupFixture.Payload(30, 1));
        using var cts = new CancellationTokenSource();
        var session = fixture.Session(peer, mediator);
        session.OnSend = (_, _) =>
        {
            cts.Cancel();
            throw new InvalidOperationException("the driver says: cancelled by user " + peer);
        };

        var act = () => fixture.SendAsync(MessagePickupAdversarialResource.DeliveryRequest(peer, mediator), mediator, session, ct: cts.Token);

        var thrown = (await act.Should().ThrowAsync<OperationCanceledException>()).Which;
        thrown.Message.Should().NotContain(peer);
        (await MessagePickupAdversarialResource.FetchIdsAsync(fixture, peer, mediator)).Should().Equal(queued);
    }

    // ---- a logger that calls back in ---------------------------------------------------------------

    /// <summary>
    /// A logging provider that, on the "queue full" line, hands the queue another message on the
    /// same thread (a sink that mirrors log events into DIDComm). The role holds no lock while it
    /// logs: the nested call completes, and the outer one is unaffected.
    /// </summary>
    [Fact]
    public async Task A_logger_that_calls_back_into_the_queue_from_a_log_line_neither_deadlocks_nor_corrupts_the_count()
    {
        var logger = new ReentrantLogger();
        await using var fixture = PickupFixture.Create(pickup: o => o.MaxQueueMessages = 2, services: c => c.AddSingleton<ILogger<MessagePickupQueue>>(logger));
        var (mediator, peer) = await fixture.EnrolledAsync();
        var other = (await fixture.NewPeerAsync()).Did;
        await fixture.EnrolAsync(other, mediator, other);
        await fixture.QueueAsync(peer, PickupFixture.Payload(30, 1));
        await fixture.QueueAsync(peer, PickupFixture.Payload(30, 2));
        logger.Reenter = () => fixture.Queue.DeliverAsync(other, PickupFixture.Payload(30, 3)).AsTask().GetAwaiter().GetResult().Outcome;

        var refused = await Task.Run(async () => await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30, 4))).WaitAsync(MessagePickupAdversarialResource.Deadline);

        refused.Outcome.Should().Be(PickupDeliveryOutcome.QueueFull);
        logger.Nested.Should().Be(PickupDeliveryOutcome.Queued, "the nested delivery, made from inside the log call, completed");
        fixture.Memory.MessageCount.Should().Be(3);
        (await MessagePickupAdversarialResource.FetchIdsAsync(fixture, other, mediator)).Should().HaveCount(1);
    }

    private sealed class ReentrantLogger : ILogger<MessagePickupQueue>
    {
        private int _entered;

        public Func<PickupDeliveryOutcome>? Reenter { get; set; }
        public PickupDeliveryOutcome? Nested { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (Reenter is { } reenter && text.Contains("was not queued", StringComparison.Ordinal) && Interlocked.Exchange(ref _entered, 1) == 0)
                Nested = reenter();
        }
    }
}
