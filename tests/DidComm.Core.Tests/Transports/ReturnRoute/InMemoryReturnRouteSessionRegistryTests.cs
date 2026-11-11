using System.Collections.Concurrent;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Transports.ReturnRoute;

/// <summary>
/// FR-RR-03 registry contract: keyed by DID subject, bounded per peer (evict oldest) and in total
/// (refuse), events raised once each — on the thread pool, never inline, so a subscriber can delay
/// neither the call nor another call's notifications — and isolated from throwing subscribers,
/// concurrency owned by the store (L-028, L-044).
/// </summary>
public sealed class InMemoryReturnRouteSessionRegistryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

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

    private static InMemoryReturnRouteSessionRegistry Registry(int perPeer = 8, int total = 4096)
        => new(new ReturnRouteSessionOptions { MaxSessionsPerPeer = perPeer, MaxSessions = total });

    private static bool SameInstances(IEnumerable<IReturnRouteSession> actual, IEnumerable<IReturnRouteSession> expected)
        => new HashSet<IReturnRouteSession>(actual, ReferenceEqualityComparer.Instance).SetEquals(expected);

    /// <summary>One notification as a subscriber saw it, with its arrival order.</summary>
    private sealed record Seen(long Order, bool Opened, IReturnRouteSession Session, SessionCloseReason Reason);

    /// <summary>
    /// Records every notification of a registry. Notifications arrive on the thread pool after the
    /// call returned, so assertions wait for them (bounded) instead of reading them at once.
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
        /// notifications (which <see cref="All"/> leaves out). Their work items were queued after
        /// those of every call made before, so a notification still missing here would have to be
        /// overtaken by two later ones. Events of different calls are unordered, so this bounds — it
        /// does not prove — the absence of a late notification; the calls whose absence is asserted
        /// queue none at all.
        /// </summary>
        public void Drain(IReturnRouteSessionRegistry registry)
        {
            var sentinel = new FakeSession("did:example:sentinel" + Guid.NewGuid().ToString("N"));
            _sentinels[sentinel] = true;
            registry.Register(sentinel).Registered.Should().BeTrue("the drain needs room for its sentinel");
            registry.Unregister(sentinel).Should().BeTrue();
            SpinWait.SpinUntil(() => _seen.Count(s => ReferenceEquals(s.Session, sentinel)) == 2, Timeout)
                .Should().BeTrue("the sentinel's notifications arrive");
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
    public async Task A_subscriber_that_never_returns_delays_neither_the_call_nor_any_other_calls_notifications()
    {
        // PR #103 review (P2): subscribers ran inline, before Register returned, so a SessionClosed
        // subscriber that blocked kept the registrant from closing the evicted session at all (and
        // one that waited for that session's Closed deadlocked). Notifications are now raised on the
        // thread pool, one work item per call.
        var registry = Registry(perPeer: 1);
        var oldAlice = new FakeSession(Alice);
        registry.Register(oldAlice);
        using var release = new ManualResetEventSlim(false);
        using var blocking = new ManualResetEventSlim(false);
        var returned = 0;
        // Subscribed first: it blocks on oldAlice's close only, until the test releases it.
        registry.SessionClosed += (_, e) =>
        {
            if (!ReferenceEquals(e.Session, oldAlice))
                return;
            blocking.Set();
            release.Wait();
            Interlocked.Exchange(ref returned, 1);
        };
        var log = new EventLog(registry);
        var newAlice = new FakeSession(Alice);
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
            blocking.Wait(Timeout).Should().BeTrue("the blocking subscriber is running (the overlap happened)");

            // Later calls complete, and their own notifications arrive, while it is still blocked.
            var carol = new FakeSession(Carol);
            registry.Register(carol).Registered.Should().BeTrue();
            registry.Unregister(carol).Should().BeTrue();
            var dave = new FakeSession("did:example:dave");
            registry.Register(dave).Registered.Should().BeTrue();
            registry.Unregister(newAlice).Should().BeTrue();
            log.WaitUntil(l => l.All.Count == 4, "the later calls' notifications arrive");
            log.All.Should().Contain(e => e.Opened && e.Session == carol)
                .And.Contain(e => !e.Opened && e.Session == carol && e.Reason == SessionCloseReason.Unregistered)
                .And.Contain(e => e.Opened && e.Session == dave)
                .And.Contain(e => !e.Opened && e.Session == newAlice && e.Reason == SessionCloseReason.Unregistered);
            log.All.Should().NotContain(e => e.Session == oldAlice || (e.Opened && e.Session == newAlice),
                "only the blocked call's own later notifications wait for its subscriber");
            Volatile.Read(ref returned).Should().Be(0, "the subscriber was still blocked throughout");
        }
        finally
        {
            release.Set();
        }

        // Released: the blocked call's remaining notifications arrive, in the call's order.
        log.WaitUntil(l => l.All.Count == 6, "the blocked call's remaining notifications arrive once it is released");
        log.All.Where(e => e.Session == oldAlice || (e.Opened && e.Session == newAlice))
            .Select(e => (e.Opened, e.Session, e.Reason))
            .Should().Equal((false, (IReturnRouteSession)oldAlice, SessionCloseReason.Evicted), (true, (IReturnRouteSession)newAlice, SessionCloseReason.None));
    }

    [Fact]
    public void Each_registrations_eviction_is_notified_before_its_SessionOpened()
    {
        // Per call, SessionClosed(Evicted) for the evicted session comes before SessionOpened for the
        // new one; across calls nothing is ordered, so this checks each call's own pair only.
        const int Registrations = 200;
        var registry = Registry(perPeer: 1);
        var log = new EventLog(registry);
        var sessions = Enumerable.Range(0, Registrations).Select(_ => new FakeSession(Alice)).ToArray();

        foreach (var session in sessions)
            registry.Register(session).Registered.Should().BeTrue();
        log.WaitUntil(l => l.All.Count == 2 * Registrations - 1, "every registration and eviction is notified");

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
