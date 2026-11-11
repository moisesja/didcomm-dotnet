using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Tests.Transports.ReturnRoute;

/// <summary>
/// FR-RR-03 / FR-RR-05 registry contract: keyed by DID subject, bounded per peer (evict oldest) and
/// in total (refuse); notifications delivered once each by one notifier per registry — never inline,
/// one at a time, in state-change order, isolated from throwing subscribers — and bounded under
/// subscriber backpressure (the oldest dropped and counted), so a blocked subscriber delays no call
/// and parks one pool thread at most; concurrency owned by the store (L-028, L-044).
/// </summary>
public sealed class InMemoryReturnRouteSessionRegistryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The longest a gated subscriber ever blocks, so a failing run cannot hold a pool thread for good.</summary>
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);

    private static readonly AsyncLocal<string?> Ambient = new();

    private sealed class FakeSession : IReturnRouteSession
    {
        public FakeSession(string peerDid, string localDid = "did:example:bob")
        {
            PeerDid = peerDid;
            LocalDid = localDid;
        }

        public string PeerDid { get; }
        public string LocalDid { get; }
        public ReturnRouteMode Mode => ReturnRouteMode.All;
        public string? Thread => null;
        public ReturnRouteTransport Transport => ReturnRouteTransport.WebSocket;
        public bool IsLive => true;
        public CancellationToken Closed => CancellationToken.None;
        public ValueTask<bool> TrySendAsync(Message message, CancellationToken ct) => ValueTask.FromResult(false);
    }

    private const string Alice = "did:example:alice";
    private const string Carol = "did:example:carol";

    private static InMemoryReturnRouteSessionRegistry Registry(int perPeer = 8, int total = 4096, int pending = 1024)
        => new(new ReturnRouteSessionOptions { MaxSessionsPerPeer = perPeer, MaxSessions = total, MaxPendingNotifications = pending });

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private static void ForceFullGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static bool SameInstances(IEnumerable<IReturnRouteSession> actual, IEnumerable<IReturnRouteSession> expected)
        => new HashSet<IReturnRouteSession>(actual, ReferenceEqualityComparer.Instance).SetEquals(expected);

    /// <summary>One notification as a subscriber saw it, with its arrival order.</summary>
    private sealed record Seen(long Order, bool Opened, IReturnRouteSession Session, SessionCloseReason Reason);

    /// <summary>
    /// Records every notification of a registry. Notifications arrive from the registry's notifier,
    /// possibly after the call returned, so assertions wait for them (bounded) instead of reading
    /// them at once.
    /// </summary>
    private sealed class EventLog
    {
        private readonly ConcurrentQueue<Seen> _seen = new();
        private readonly ConcurrentDictionary<IReturnRouteSession, bool> _sentinels = new(ReferenceEqualityComparer.Instance);
        private long _order;

        public EventLog(IReturnRouteSessionRegistry registry)
        {
            registry.SessionOpened += (_, e) => _seen.Enqueue(new Seen(Interlocked.Increment(ref _order), true, e.Session, e.Reason));
            registry.SessionClosed += (_, e) => _seen.Enqueue(new Seen(Interlocked.Increment(ref _order), false, e.Session, e.Reason));
        }

        /// <summary>Every notification seen so far, in arrival order, without the drain's sentinels.</summary>
        public IReadOnlyList<Seen> All => _seen.Where(s => !_sentinels.ContainsKey(s.Session)).OrderBy(s => s.Order).ToList();

        public IReadOnlyList<Seen> Opened => All.Where(s => s.Opened).ToList();

        public IReadOnlyList<Seen> Closed => All.Where(s => !s.Opened).ToList();

        /// <summary>Wait (bounded) until <paramref name="condition"/> holds; fails the test otherwise.</summary>
        public void WaitUntil(Func<EventLog, bool> condition, string because)
            => SpinWait.SpinUntil(() => condition(this), Timeout).Should().BeTrue(because);

        /// <summary>
        /// Register and unregister a sentinel session of its own DID and wait for both of its
        /// notifications (which <see cref="All"/> leaves out). Notifications are delivered in
        /// state-change order, so once the sentinel's have arrived every notification of an earlier
        /// call has been delivered or dropped (the drops are asserted) — none can still be late.
        /// </summary>
        public void Drain(InMemoryReturnRouteSessionRegistry registry, long expectedDrops = 0)
        {
            var sentinel = new FakeSession("did:example:sentinel" + Guid.NewGuid().ToString("N"));
            _sentinels[sentinel] = true;
            registry.Register(sentinel).Registered.Should().BeTrue("the drain needs room for its sentinel");
            registry.Unregister(sentinel).Should().BeTrue();
            SpinWait.SpinUntil(() => _seen.Count(s => ReferenceEquals(s.Session, sentinel)) == 2, Timeout)
                .Should().BeTrue("the sentinel's notifications arrive");
            registry.DroppedNotifications.Should().Be(expectedDrops, "a drain proves nothing is late only when no other notification was dropped");
        }
    }

    [Fact]
    public void Register_unregister_and_count()
    {
        var registry = Registry();
        var s = new FakeSession(Alice);

        var result = registry.Register(s);

        result.Registered.Should().BeTrue();
        result.Outcome.Should().Be(SessionRegistrationOutcome.Registered);
        result.Evicted.Should().BeNull();
        registry.Count.Should().Be(1);

        registry.Unregister(s).Should().BeTrue();
        registry.Count.Should().Be(0);
        registry.Unregister(s).Should().BeFalse();
    }

    [Fact]
    public void Find_by_DID_and_by_DID_URL_of_the_same_subject_returns_a_snapshot()
    {
        var registry = Registry();
        var s = new FakeSession(Alice);
        registry.Register(s);

        var byDid = registry.Find(Alice);
        var byUrl = registry.Find(Alice + "/inbox?v=2#frag");

        byDid.Should().ContainSingle().Which.Should().BeSameAs(s);
        byUrl.Should().ContainSingle().Which.Should().BeSameAs(s);
        registry.Find("not-a-did").Should().BeEmpty();
        registry.Find(Carol).Should().BeEmpty();

        registry.Register(new FakeSession(Alice));
        byDid.Should().HaveCount(1, "a snapshot is not live");
        registry.Find(Alice).Should().HaveCount(2);
    }

    [Fact]
    public void Events_fire_once_each_with_the_session()
    {
        var registry = Registry();
        var log = new EventLog(registry);
        var s = new FakeSession(Alice);

        registry.Register(s);
        registry.Unregister(s);
        log.WaitUntil(l => l.Opened.Count >= 1 && l.Closed.Count >= 1, "both notifications arrive");
        log.Drain(registry);

        log.Opened.Should().ContainSingle().Which.Should().Match<Seen>(e => e.Session == s && e.Reason == SessionCloseReason.None);
        log.Closed.Should().ContainSingle().Which.Should().Match<Seen>(e => e.Session == s && e.Reason == SessionCloseReason.Unregistered);
    }

    [Fact]
    public void Per_peer_bound_evicts_that_peers_oldest_and_leaves_others_alone()
    {
        var registry = Registry(perPeer: 8);
        var log = new EventLog(registry);
        var carol = new FakeSession(Carol);
        registry.Register(carol);
        var alices = Enumerable.Range(0, 8).Select(_ => new FakeSession(Alice)).ToList();
        foreach (var a in alices) registry.Register(a);
        registry.Count.Should().Be(9);

        var ninth = new FakeSession(Alice);
        var result = registry.Register(ninth);

        result.Registered.Should().BeTrue();
        result.Evicted.Should().BeSameAs(alices[0]);
        registry.Count.Should().Be(9);
        log.WaitUntil(l => l.Closed.Count >= 1, "the eviction is notified");
        log.Closed.Should().ContainSingle().Which.Should().Match<Seen>(e => e.Session == alices[0] && e.Reason == SessionCloseReason.Evicted);
        registry.Find(Alice).Should().NotContain(alices[0]).And.Contain(ninth).And.HaveCount(8);
        registry.Find(Carol).Should().ContainSingle().Which.Should().BeSameAs(carol);
        registry.Unregister(alices[0]).Should().BeFalse("the registry already forgot it; no second event");
        log.Drain(registry);
        log.Closed.Should().HaveCount(1);
    }

    [Fact]
    public void Total_bound_refuses_a_new_DID_without_evicting_or_raising()
    {
        var registry = Registry(perPeer: 1, total: 2);
        var log = new EventLog(registry);
        registry.Register(new FakeSession(Alice));
        var carol = new FakeSession(Carol);
        registry.Register(carol);
        var dave = new FakeSession("did:example:dave");

        var result = registry.Register(dave);

        result.Registered.Should().BeFalse();
        result.Outcome.Should().Be(SessionRegistrationOutcome.RefusedTotalBound);
        result.Evicted.Should().BeNull();
        registry.Count.Should().Be(2);
        // Room for the drain's sentinel; Carol's own close is then the only close.
        registry.Unregister(carol).Should().BeTrue();
        log.Drain(registry);
        log.Closed.Should().ContainSingle().Which.Should().Match<Seen>(e => e.Session == carol && e.Reason == SessionCloseReason.Unregistered);
        log.All.Should().NotContain(e => e.Session == dave, "a refused registration raises nothing");
    }

    [Fact]
    public void A_peer_at_its_own_bound_replaces_its_oldest_even_when_the_registry_is_full()
    {
        // FR-RR-03: the per-DID bound evicts that DID's oldest; the total bound refuses only a
        // registration that would grow the total, so a reconnecting peer is not locked out by a full
        // registry and no other DID is touched.
        var registry = Registry(perPeer: 1, total: 2);
        var log = new EventLog(registry);
        var oldAlice = new FakeSession(Alice);
        var carol = new FakeSession(Carol);
        registry.Register(oldAlice);
        registry.Register(carol);

        var newAlice = new FakeSession(Alice);
        var result = registry.Register(newAlice);

        result.Outcome.Should().Be(SessionRegistrationOutcome.Registered);
        result.Evicted.Should().BeSameAs(oldAlice);
        registry.Count.Should().Be(2);
        registry.Find(Alice).Should().ContainSingle().Which.Should().BeSameAs(newAlice);
        registry.Find(Carol).Should().ContainSingle().Which.Should().BeSameAs(carol);
        registry.Register(new FakeSession("did:example:dave")).Outcome.Should().Be(SessionRegistrationOutcome.RefusedTotalBound);
        registry.Unregister(carol).Should().BeTrue("room for the drain's sentinel");
        log.Drain(registry);
        log.Closed.Where(e => e.Session != carol).Should().ContainSingle()
            .Which.Should().Match<Seen>(e => e.Session == oldAlice && e.Reason == SessionCloseReason.Evicted);
    }

    [Fact]
    public void Registering_the_same_instance_twice_is_AlreadyRegistered()
    {
        var registry = Registry();
        var s = new FakeSession(Alice);
        registry.Register(s);
        // Subscribed after the first registration returned: that call captured its subscribers when
        // it queued its notification, so only a notification of the second call could arrive here.
        var log = new EventLog(registry);

        registry.Register(s).Outcome.Should().Be(SessionRegistrationOutcome.AlreadyRegistered);
        registry.Count.Should().Be(1);
        log.Drain(registry);
        log.All.Should().BeEmpty();
    }

    [Fact]
    public void Fresh_DID_flood_never_evicts_an_existing_peer()
    {
        var registry = Registry(perPeer: 1, total: 16);
        var first = new FakeSession(Alice);
        registry.Register(first);

        for (var i = 0; i < 32; i++)
            registry.Register(new FakeSession($"did:example:flood{i}"));

        registry.Count.Should().Be(16);
        registry.Find(Alice).Should().ContainSingle().Which.Should().BeSameAs(first);
    }

    [Fact]
    public void A_throwing_subscriber_is_isolated()
    {
        var registry = Registry();
        var secondSaw = 0;
        registry.SessionOpened += (_, _) => throw new InvalidOperationException("boom");
        registry.SessionOpened += (_, _) => Interlocked.Increment(ref secondSaw);
        registry.SessionClosed += (_, _) => throw new InvalidOperationException("boom");
        var log = new EventLog(registry);

        var result = registry.Register(new FakeSession(Alice));

        result.Registered.Should().BeTrue();
        SpinWait.SpinUntil(() => Volatile.Read(ref secondSaw) == 1, Timeout).Should().BeTrue("the next subscriber still runs");
        var act = () => registry.Unregister(registry.Find(Alice)[0]);
        act.Should().NotThrow();
        log.WaitUntil(l => l.Closed.Count == 1, "a subscriber after the throwing one still sees the close");
    }

    [Fact]
    public void A_non_subject_PeerDid_is_refused_without_echoing_the_value()
    {
        var registry = Registry();
        var url = new FakeSession("did:example:alice/path?x=1");
        var notDid = new FakeSession("nope");

        foreach (var s in new[] { url, notDid })
        {
            var act = () => registry.Register(s);
            var ex = act.Should().Throw<ArgumentException>().Which;
            ex.Message.Should().NotContain("alice").And.NotContain("nope");
        }
        registry.Count.Should().Be(0);
    }

    [Fact]
    public void Concurrent_registrations_respect_the_per_peer_bound_exactly()
    {
        // L-044: rendezvous on a gate, assert the invariant per result, and prove the overlap.
        // Dedicated threads, not Task.Run: 64 pool tasks parked on a gate starve the thread pool
        // (it injects threads slowly), which turns the rendezvous into a timeout.
        const int Tasks = 64;
        const int Dids = 4;
        const int PerPeer = 4;
        var registry = Registry(perPeer: PerPeer, total: 4096);
        var log = new EventLog(registry);
        var gate = new ManualResetEventSlim(false);
        var arrived = 0;
        var sessions = Enumerable.Range(0, Tasks).Select(i => new FakeSession($"did:example:p{i % Dids}")).ToArray();
        var results = new SessionRegistration[Tasks];

        var threads = Enumerable.Range(0, Tasks).Select(i => new Thread(() =>
        {
            Interlocked.Increment(ref arrived);
            gate.Wait();
            results[i] = registry.Register(sessions[i]);
        })).ToArray();
        foreach (var t in threads) t.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref arrived) == Tasks, TimeSpan.FromSeconds(10)).Should().BeTrue();
        gate.Set();
        foreach (var t in threads) t.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();

        arrived.Should().Be(Tasks);
        results.Should().OnlyContain(r => r.Registered);
        registry.Count.Should().Be(Dids * PerPeer);
        for (var d = 0; d < Dids; d++)
            registry.Find($"did:example:p{d}").Should().HaveCount(PerPeer);
        log.WaitUntil(l => l.Closed.Count >= Tasks - Dids * PerPeer && l.Opened.Count >= Tasks, "every registration and eviction is notified");
        log.Drain(registry);
        var evicted = log.Closed.Where(e => e.Reason == SessionCloseReason.Evicted).Select(e => e.Session).ToList();
        evicted.Should().HaveCount(Tasks - Dids * PerPeer).And.OnlyHaveUniqueItems();
        log.Closed.Should().OnlyContain(e => e.Reason == SessionCloseReason.Evicted);
        log.Opened.Should().HaveCount(Tasks);
        SameInstances(log.Opened.Select(e => e.Session), sessions).Should().BeTrue("one SessionOpened per registration");
        results.Count(r => r.Evicted is not null).Should().Be(Tasks - Dids * PerPeer);
        SameInstances(results.Where(r => r.Evicted is not null).Select(r => r.Evicted!), evicted)
            .Should().BeTrue("each evicted session is reported by its own call and notified once");
        foreach (var e in evicted)
            registry.Find(e.PeerDid).Should().NotContain(e);
    }

    [Fact]
    public async Task Two_concurrent_registrations_at_the_bound_evict_exactly_one_each()
    {
        var registry = Registry(perPeer: 2, total: 4096);
        registry.Register(new FakeSession(Alice));
        registry.Register(new FakeSession(Alice));
        var gate = new CountdownEvent(2);
        var a = new FakeSession(Alice);
        var b = new FakeSession(Alice);

        var ta = Task.Run(() => { gate.Signal(); gate.Wait(); return registry.Register(a); });
        var tb = Task.Run(() => { gate.Signal(); gate.Wait(); return registry.Register(b); });
        var results = await Task.WhenAll(ta, tb);

        results.Should().OnlyContain(r => r.Registered && r.Evicted != null);
        results[0].Evicted.Should().NotBeSameAs(results[1].Evicted);
        registry.Count.Should().Be(2);
        registry.Find(Alice).Should().BeEquivalentTo(new IReturnRouteSession[] { a, b }, o => o.WithoutStrictOrdering());
    }

    [Fact]
    public async Task A_subscriber_that_never_returns_delays_no_call_and_later_notifications_wait_behind_it_up_to_the_bound()
    {
        // PR #103 review (P2), rounds 2 and 3. Round 2: subscribers ran inline, before Register
        // returned, so a blocked SessionClosed subscriber kept the registrant from closing the
        // evicted session. Round 3: one pool work item per call then let blocked subscribers pin a
        // pool thread per call and retain every queued call's sessions without bound. Now one
        // notifier per registry delivers in state-change order: a blocked subscriber delays no call,
        // and the later notifications wait behind it — at most MaxPendingNotifications of them.
        const int Capacity = 4;
        var registry = Registry(perPeer: 1, pending: Capacity);
        var oldAlice = new FakeSession(Alice);
        registry.Register(oldAlice); // nobody subscribes yet: nothing is queued
        registry.PendingNotifications.Should().Be(0);
        using var release = new ManualResetEventSlim(false);
        var blocking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = 0;
        // Subscribed first: it blocks on oldAlice's close only, until the test releases it.
        registry.SessionClosed += (_, e) =>
        {
            if (!ReferenceEquals(e.Session, oldAlice))
                return;
            blocking.TrySetResult();
            release.Wait(Cap);
            Interlocked.Exchange(ref returned, 1);
        };
        var log = new EventLog(registry);
        var newAlice = new FakeSession(Alice);
        var carol = new FakeSession(Carol);
        var dave = new FakeSession("did:example:dave");
        var erin = new FakeSession("did:example:erin");
        try
        {
            // On a dedicated thread with a bounded wait, so an inline subscriber fails the test
            // instead of hanging it.
            var register = Task.Factory.StartNew(() => registry.Register(newAlice), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            (await Task.WhenAny(register, Task.Delay(Timeout))).Should().BeSameAs(register, "Register returns without waiting for any subscriber");
            var registration = await register;
            registration.Outcome.Should().Be(SessionRegistrationOutcome.Registered);
            registration.Evicted.Should().BeSameAs(oldAlice, "the eviction is reported to the registrant at once");
            registry.Find(Alice).Should().ContainSingle().Which.Should().BeSameAs(newAlice);
            await blocking.Task.WaitAsync(Timeout); // the subscriber is running and blocked (the overlap happened)

            // Later calls complete at once; their notifications wait behind the blocked one.
            registry.Register(carol).Registered.Should().BeTrue();
            registry.Unregister(carol).Should().BeTrue();
            registry.Register(dave).Registered.Should().BeTrue();
            registry.Unregister(newAlice).Should().BeTrue();
            registry.PendingNotifications.Should().Be(Capacity + 1, "four calls wait behind the one being delivered");
            registry.DroppedNotifications.Should().Be(0);
            log.All.Should().BeEmpty("nothing is delivered past a blocked subscriber, not even the rest of its own call");

            // One more call at the bound still completes; the oldest waiting notification is dropped.
            registry.Register(erin).Registered.Should().BeTrue();
            registry.PendingNotifications.Should().Be(Capacity + 1, "the bound holds");
            registry.DroppedNotifications.Should().Be(1);
            registry.Count.Should().Be(2);
            Volatile.Read(ref returned).Should().Be(0, "the subscriber was still blocked throughout");
        }
        finally
        {
            release.Set();
        }

        // Released: the rest of the blocked call, then the waiting calls, in state-change order.
        // Carol's SessionOpened — the oldest waiting notification — was dropped, so her close comes
        // without an open: per-session state belongs on IReturnRouteSession.Closed.
        log.WaitUntil(l => l.All.Count == 6, "the remaining notifications arrive once the subscriber is released");
        log.Drain(registry, expectedDrops: 1);
        log.All.Select(e => (e.Opened, e.Session, e.Reason)).Should().Equal(
            (false, (IReturnRouteSession)oldAlice, SessionCloseReason.Evicted),
            (true, (IReturnRouteSession)newAlice, SessionCloseReason.None),
            (false, (IReturnRouteSession)carol, SessionCloseReason.Unregistered),
            (true, (IReturnRouteSession)dave, SessionCloseReason.None),
            (false, (IReturnRouteSession)newAlice, SessionCloseReason.Unregistered),
            (true, (IReturnRouteSession)erin, SessionCloseReason.None));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(1000)]
    public async Task A_blocked_subscriber_under_churn_keeps_its_backlog_bounded_and_retains_no_dropped_session(int registrations)
    {
        // PR #103 review (P2, round 3), the reviewer's repro: MaxSessions = MaxSessionsPerPeer = 1, a
        // gated SessionOpened subscriber, repeated replacements for one DID. With one pool work item
        // per call, Count stayed 1 but 40 calls left blocked callbacks on every pool worker plus the
        // rest queued — each retaining its sessions — and unrelated pool work waited for the gate.
        const int Capacity = 8;
        var registry = Registry(perPeer: 1, total: 1, pending: Capacity);
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inside = 0;
        var maxInside = 0;
        // Every SessionOpened blocks until the gate opens (capped).
        registry.SessionOpened += (_, _) =>
        {
            InterlockedMax(ref maxInside, Interlocked.Increment(ref inside));
            entered.TrySetResult();
            release.Wait(Cap);
            Interlocked.Decrement(ref inside);
        };
        var log = new EventLog(registry);
        var first = new FakeSession(Alice);
        var expected = new List<(bool Opened, IReturnRouteSession Session, SessionCloseReason Reason)> { (true, first, SessionCloseReason.None) };
        try
        {
            registry.Register(first).Registered.Should().BeTrue();
            await entered.Task.WaitAsync(Timeout); // the notifier is inside the gate: the one parked thread

            var churn = Task.Factory.StartNew(() => Churn(registry, first, registrations, Capacity), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            (await Task.WhenAny(churn, Task.Delay(Timeout))).Should().BeSameAs(churn, "no Register waits for the blocked subscriber");
            var sessions = await churn; // per call: registered, evicted its predecessor, Count == 1, backlog within the bound

            registry.Count.Should().Be(1);
            registry.PendingNotifications.Should().Be(Capacity + 1, "the one being delivered plus a full queue, however many calls were made");
            registry.DroppedNotifications.Should().Be(registrations - 1 - Capacity, "each call past the bound dropped the oldest waiting notification");

            // Unrelated pool work still runs while the gate is held.
            var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ThreadPool.QueueUserWorkItem(_ => queued.TrySetResult());
            await queued.Task.WaitAsync(Timeout);
            (await Task.Run(() => 42).WaitAsync(Timeout)).Should().Be(42);
            Volatile.Read(ref inside).Should().Be(1, "the gate was still held");

            // Session i is carried by call i's SessionOpened and call i+1's SessionClosed(Evicted).
            // Calls 1 .. n-1-Capacity were dropped, so sessions 1 .. n-2-Capacity are referenced by
            // nothing any more; the rest are held by the waiting calls, the registry or the gate.
            ForceFullGc();
            for (var i = 1; i <= registrations - 2 - Capacity; i++)
                sessions[i].IsAlive.Should().BeFalse("session {0} was carried only by dropped notifications", i);
            for (var i = registrations - 1 - Capacity; i < registrations; i++)
                sessions[i].IsAlive.Should().BeTrue("session {0} is carried by a waiting notification or registered", i);

            for (var i = registrations - Capacity; i < registrations; i++)
            {
                expected.Add((false, (IReturnRouteSession)sessions[i - 1].Target!, SessionCloseReason.Evicted));
                expected.Add((true, (IReturnRouteSession)sessions[i].Target!, SessionCloseReason.None));
            }
        }
        finally
        {
            release.Set();
        }

        // Released: the blocked SessionOpened, then the newest calls' notifications in state-change order.
        log.WaitUntil(l => l.All.Count == expected.Count, "the waiting notifications arrive once the gate opens");
        SpinWait.SpinUntil(() => registry.PendingNotifications == 0, Timeout).Should().BeTrue("nothing else is waiting");
        log.All.Select(e => (e.Opened, e.Session, e.Reason)).Should().Equal(expected);
        maxInside.Should().Be(1, "subscribers are never called concurrently");
    }

    /// <summary>
    /// Replace the peer's session <paramref name="count"/> − 1 times, checking each call, and return
    /// weak references to every session (index 0 is <paramref name="first"/>). Not inlined, and run
    /// on its own thread, so no session outlives it on a stack.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] Churn(InMemoryReturnRouteSessionRegistry registry, IReturnRouteSession first, int count, int capacity)
    {
        var sessions = new WeakReference[count];
        sessions[0] = new WeakReference(first);
        var previous = first;
        for (var i = 1; i < count; i++)
        {
            var session = new FakeSession(Alice);
            sessions[i] = new WeakReference(session);
            var result = registry.Register(session);
            Assert.Equal(SessionRegistrationOutcome.Registered, result.Outcome);
            Assert.Same(previous, result.Evicted);
            Assert.Equal(1, registry.Count);
            Assert.InRange(registry.PendingNotifications, 1, capacity + 1);
            previous = session;
        }
        return sessions;
    }

    [Fact]
    public void A_subscriber_that_keeps_up_gets_every_notification_once_in_state_change_order()
    {
        // L-044: workers released together on dedicated threads, the overlap proved, and an
        // invariant that holds for every interleaving: replaying the delivered sequence against a
        // model of the registry must reproduce each call's own result (who it evicted) and the
        // registry's final state. Any reordering across calls of one DID breaks the replay.
        const int Workers = 8;
        const int PerWorker = 60;
        const int Dids = 3;
        const int PerPeer = 2;
        const int Calls = Workers * (PerWorker + PerWorker / 2);
        // The bound covers every call, so no interleaving can drop a notification.
        var registry = Registry(perPeer: PerPeer, total: 4096, pending: Calls);
        var seen = new List<(bool Opened, IReturnRouteSession Session, SessionCloseReason Reason)>();
        var inside = 0;
        var maxInside = 0;
        void Record(bool opened, ReturnRouteSessionEventArgs e)
        {
            InterlockedMax(ref maxInside, Interlocked.Increment(ref inside));
            lock (seen)
                seen.Add((opened, e.Session, e.Reason));
            Interlocked.Decrement(ref inside);
        }
        registry.SessionOpened += (_, e) => Record(true, e);
        registry.SessionClosed += (_, e) => Record(false, e);

        var registrations = new ConcurrentQueue<(IReturnRouteSession Session, SessionRegistration Result)>();
        var unregistered = new ConcurrentQueue<IReturnRouteSession>();
        var inFlight = 0;
        var maxInFlight = 0;
        var arrived = 0;
        using var go = new ManualResetEventSlim(false);
        // Each worker's first call parks inside Call (already counted in flight) until every worker
        // is there, so the callers are in flight together on every run — on a 2-core runner a
        // released-together start alone did not guarantee it (L-044).
        using var firstCalls = new Barrier(Workers);
        T Call<T>(Func<T> call)
        {
            InterlockedMax(ref maxInFlight, Interlocked.Increment(ref inFlight));
            try
            {
                return call();
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        }
        var threads = Enumerable.Range(0, Workers).Select(w => new Thread(() =>
        {
            Interlocked.Increment(ref arrived);
            go.Wait(Timeout);
            for (var k = 0; k < PerWorker; k++)
            {
                var session = new FakeSession($"did:example:p{(w + k) % Dids}");
                var first = k == 0;
                registrations.Enqueue((session, Call(() =>
                {
                    if (first)
                        firstCalls.SignalAndWait(Timeout).Should().BeTrue("every worker reached its first call");
                    return registry.Register(session);
                })));
                if (k % 2 == 1 && Call(() => registry.Unregister(session)))
                    unregistered.Enqueue(session);
            }
        })).ToArray();
        foreach (var t in threads) t.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref arrived) == Workers, Timeout).Should().BeTrue();
        go.Set();
        foreach (var t in threads) t.Join(Timeout).Should().BeTrue();

        maxInFlight.Should().Be(Workers, "every worker's first call was in flight at once (L-044)");
        registrations.Should().HaveCount(Workers * PerWorker).And.OnlyContain(r => r.Result.Outcome == SessionRegistrationOutcome.Registered);
        var evictedBy = new Dictionary<IReturnRouteSession, IReturnRouteSession>(ReferenceEqualityComparer.Instance);
        foreach (var (session, result) in registrations)
            if (result.Evicted is not null)
                evictedBy.Add(session, result.Evicted);
        var expectedCount = registrations.Count + evictedBy.Count + unregistered.Count;
        SpinWait.SpinUntil(() => { lock (seen) return seen.Count == expectedCount; }, Timeout).Should().BeTrue("every notification arrives");
        SpinWait.SpinUntil(() => registry.PendingNotifications == 0, Timeout).Should().BeTrue();
        registry.DroppedNotifications.Should().Be(0);
        maxInside.Should().Be(1, "one notifier: subscribers are never called concurrently");

        List<(bool Opened, IReturnRouteSession Session, SessionCloseReason Reason)> delivered;
        lock (seen)
            delivered = seen.ToList();
        delivered.Should().HaveCount(expectedCount, "nothing arrives twice or late");
        SameInstances(delivered.Where(e => e.Opened).Select(e => e.Session), registrations.Select(r => r.Session)).Should().BeTrue();
        delivered.Where(e => e.Opened).Select(e => e.Session).Should().OnlyHaveUniqueItems("one SessionOpened per registration");
        delivered.Where(e => !e.Opened && e.Reason == SessionCloseReason.Evicted).Select(e => e.Session)
            .Should().OnlyHaveUniqueItems().And.HaveCount(evictedBy.Count);
        SameInstances(delivered.Where(e => !e.Opened && e.Reason == SessionCloseReason.Evicted).Select(e => e.Session), evictedBy.Values).Should().BeTrue();
        delivered.Where(e => !e.Opened && e.Reason == SessionCloseReason.Unregistered).Select(e => e.Session)
            .Should().OnlyHaveUniqueItems().And.HaveCount(unregistered.Count);
        SameInstances(delivered.Where(e => !e.Opened && e.Reason == SessionCloseReason.Unregistered).Select(e => e.Session), unregistered).Should().BeTrue();

        // Replay the delivered sequence against a model of the registry.
        var model = new Dictionary<string, List<IReturnRouteSession>>(StringComparer.Ordinal);
        List<IReturnRouteSession> ListOf(IReturnRouteSession s)
            => model.TryGetValue(s.PeerDid, out var l) ? l : model[s.PeerDid] = new List<IReturnRouteSession>();
        for (var i = 0; i < delivered.Count; i++)
        {
            var (opened, session, reason) = delivered[i];
            var list = ListOf(session);
            if (opened)
            {
                list.Should().NotContain(session);
                if (evictedBy.TryGetValue(session, out var victim))
                    (i > 0 && delivered[i - 1] == (false, victim, SessionCloseReason.Evicted)).Should().BeTrue(
                        "at {0}: a registration's own eviction is delivered right before its SessionOpened", i);
                else
                    list.Count.Should().BeLessThan(PerPeer, "at {0}: a registration that evicted nothing found its peer below the bound", i);
                list.Add(session);
            }
            else if (reason == SessionCloseReason.Evicted)
            {
                list.Should().HaveCount(PerPeer, "at {0}: an eviction happens only at the bound", i);
                list[0].Should().BeSameAs(session, "at {0}: the peer's oldest is evicted", i);
                (i + 1 < delivered.Count && delivered[i + 1].Opened && evictedBy.TryGetValue(delivered[i + 1].Session, out var v) && ReferenceEquals(v, session))
                    .Should().BeTrue("at {0}: the SessionOpened of the registration that evicted it follows at once", i);
                list.RemoveAt(0);
            }
            else
            {
                list.Remove(session).Should().BeTrue("at {0}: only a registered session is unregistered", i);
            }
        }
        for (var d = 0; d < Dids; d++)
        {
            var did = $"did:example:p{d}";
            registry.Find(did).Should().Equal(model.TryGetValue(did, out var l) ? l : new List<IReturnRouteSession>(),
                (a, b) => ReferenceEquals(a, b), "the replay ends in the registry's state, oldest first");
        }
    }

    [Fact]
    public async Task Subscribers_see_neither_the_creators_nor_the_callers_ambient_state()
    {
        // The notifier lives as long as the registry: it must not keep the ambient state of the
        // request that happened to create it, and none may flow from a registering request either.
        Ambient.Value = "creator";
        var registry = Registry();
        Ambient.Value = "caller";
        var observed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.SessionOpened += (_, _) => observed.TrySetResult(Ambient.Value);

        registry.Register(new FakeSession(Alice)).Registered.Should().BeTrue();

        (await observed.Task.WaitAsync(Timeout)).Should().BeNull();
        Ambient.Value.Should().Be("caller");
    }

    [Fact]
    public async Task Dispose_returns_at_once_with_a_subscriber_blocked_and_nothing_is_delivered_after_it()
    {
        var registry = Registry(perPeer: 1, pending: 4);
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        registry.SessionOpened += (_, _) =>
        {
            if (Interlocked.Increment(ref calls) != 1)
                return;
            entered.TrySetResult();
            release.Wait(Cap);
        };
        var log = new EventLog(registry);
        try
        {
            registry.Register(new FakeSession(Alice)).Registered.Should().BeTrue();
            await entered.Task.WaitAsync(Timeout);
            registry.Register(new FakeSession(Alice)).Evicted.Should().NotBeNull();
            registry.Register(new FakeSession(Carol)).Registered.Should().BeTrue();
            registry.PendingNotifications.Should().Be(3);

            var dispose = Task.Factory.StartNew(registry.Dispose, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            (await Task.WhenAny(dispose, Task.Delay(Timeout))).Should().BeSameAs(dispose, "Dispose never waits for a subscriber");
            await dispose;
            registry.Dispose(); // idempotent
            registry.Notifier.IsCompleted.Should().BeFalse("the notifier is still inside the blocked subscriber");
        }
        finally
        {
            release.Set();
        }

        await registry.Notifier.WaitAsync(Timeout); // it stops as soon as the subscriber returns
        registry.DroppedNotifications.Should().Be(2, "the two waiting notifications were dropped");
        registry.PendingNotifications.Should().Be(0);
        log.All.Should().BeEmpty("no subscriber call starts after Dispose — not even the rest of the blocked call's");
        Volatile.Read(ref calls).Should().Be(1);
        registry.Count.Should().Be(2, "Dispose leaves the registry's state alone");
    }

    [Fact]
    public async Task After_Dispose_the_registry_keeps_working_and_counts_the_notifications_it_drops()
    {
        // The endpoints may still unregister their sessions while the host disposes the container.
        var registry = Registry(perPeer: 1);
        var log = new EventLog(registry);
        registry.Dispose();
        await registry.Notifier.WaitAsync(Timeout); // an idle notifier stops at once

        var a = new FakeSession(Alice);
        var b = new FakeSession(Alice);
        registry.Register(a).Registered.Should().BeTrue();
        registry.Register(b).Evicted.Should().BeSameAs(a);
        registry.Find(Alice).Should().ContainSingle().Which.Should().BeSameAs(b);
        registry.Unregister(b).Should().BeTrue();
        registry.Unregister(b).Should().BeFalse();
        registry.Count.Should().Be(0);

        registry.DroppedNotifications.Should().Be(3, "one per call that changed something");
        registry.PendingNotifications.Should().Be(0);
        log.All.Should().BeEmpty();
        registry.Dispose();
    }

    [Fact]
    public void A_registry_that_is_never_disposed_is_collected_with_its_parked_notifier()
    {
        // The endpoints' per-application fallback registry is never disposed: its idle notifier is
        // an awaiting loop that nothing outside the registry references, so the two go together.
        var registry = CreateUsedRegistry();
        SpinWait.SpinUntil(() =>
        {
            ForceFullGc();
            return !registry.IsAlive;
        }, Timeout).Should().BeTrue("once its notifier has parked, nothing roots the registry");
    }

    /// <summary>A registry whose notifier has delivered one notification (so it ran and parked again); only a weak reference escapes.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateUsedRegistry()
    {
        var registry = Registry();
        var delivered = new ManualResetEventSlim(false); // not disposed: the notifier may still be inside Set
        registry.SessionOpened += (_, _) => delivered.Set();
        registry.Register(new FakeSession(Alice)).Registered.Should().BeTrue();
        delivered.Wait(Timeout).Should().BeTrue();
        return new WeakReference(registry);
    }

    [Fact]
    public async Task Dropped_notifications_log_a_Warning_for_the_first_and_every_thousandth_with_counts_only()
    {
        var logger = new CapturingLogger();
        var registry = new InMemoryReturnRouteSessionRegistry(
            new ReturnRouteSessionOptions { MaxSessionsPerPeer = 1, MaxSessions = 1, MaxPendingNotifications = 1 }, logger);
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.SessionOpened += (_, _) =>
        {
            entered.TrySetResult();
            release.Wait(Cap);
        };
        try
        {
            registry.Register(new FakeSession(Alice));
            await entered.Task.WaitAsync(Timeout);
            for (var i = 0; i < 2001; i++) // the first waits; the other 2000 each drop the one before
                registry.Register(new FakeSession(Alice));
        }
        finally
        {
            release.Set();
        }

        SpinWait.SpinUntil(() => registry.PendingNotifications == 0, Timeout).Should().BeTrue("the gate's subscriber calls have returned");
        registry.DroppedNotifications.Should().Be(2000);
        var warnings = logger.Lines.Where(l => l.Level == LogLevel.Warning).Select(l => l.Text).ToList();
        warnings.Should().HaveCount(3, "the first drop and the 1000th and 2000th");
        warnings.Should().AllSatisfy(w => w.Should().Contain("a subscriber is not keeping up").And.NotContain("did:"));
        warnings.Should().SatisfyRespectively(
            w => w.Should().EndWith("1 dropped in total."),
            w => w.Should().EndWith("1000 dropped in total."),
            w => w.Should().EndWith("2000 dropped in total."));
    }

    private sealed class CapturingLogger : ILogger<InMemoryReturnRouteSessionRegistry>
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Enqueue((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void Each_registrations_eviction_is_notified_before_its_SessionOpened()
    {
        // Per call, SessionClosed(Evicted) for the evicted session comes before SessionOpened for the
        // new one. (The order across calls is pinned by
        // A_subscriber_that_keeps_up_gets_every_notification_once_in_state_change_order.)
        const int Registrations = 200;
        var registry = Registry(perPeer: 1);
        var log = new EventLog(registry);
        var sessions = Enumerable.Range(0, Registrations).Select(_ => new FakeSession(Alice)).ToArray();

        foreach (var session in sessions)
            registry.Register(session).Registered.Should().BeTrue();
        log.WaitUntil(l => l.All.Count == 2 * Registrations - 1, "every registration and eviction is notified");
        registry.DroppedNotifications.Should().Be(0);

        var all = log.All;
        for (var i = 1; i < Registrations; i++)
        {
            var closed = all.Single(e => !e.Opened && e.Session == sessions[i - 1]);
            var opened = all.Single(e => e.Opened && e.Session == sessions[i]);
            closed.Reason.Should().Be(SessionCloseReason.Evicted);
            closed.Order.Should().BeLessThan(opened.Order, "registration {0}: its eviction is notified before its SessionOpened", i);
        }
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var registry = Registry();
        var r = () => registry.Register(null!);
        r.Should().Throw<ArgumentNullException>();
        var u = () => registry.Unregister(null!);
        u.Should().Throw<ArgumentNullException>();
        var c = () => new InMemoryReturnRouteSessionRegistry(null!);
        c.Should().Throw<ArgumentNullException>();
    }
}
