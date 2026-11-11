using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Transports.ReturnRoute;

/// <summary>
/// FR-RR-03 registry contract: keyed by DID subject, bounded per peer (evict oldest) and in total
/// (refuse), events raised once each and isolated from throwing subscribers, concurrency owned by the
/// store (L-028, L-044).
/// </summary>
public sealed class InMemoryReturnRouteSessionRegistryTests
{
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
        var opened = new List<ReturnRouteSessionEventArgs>();
        var closed = new List<ReturnRouteSessionEventArgs>();
        registry.SessionOpened += (_, e) => opened.Add(e);
        registry.SessionClosed += (_, e) => closed.Add(e);
        var s = new FakeSession(Alice);

        registry.Register(s);
        registry.Unregister(s);

        opened.Should().ContainSingle().Which.Should().Match<ReturnRouteSessionEventArgs>(e => e.Session == s && e.Reason == SessionCloseReason.None);
        closed.Should().ContainSingle().Which.Should().Match<ReturnRouteSessionEventArgs>(e => e.Session == s && e.Reason == SessionCloseReason.Unregistered);
    }

    [Fact]
    public void Per_peer_bound_evicts_that_peers_oldest_and_leaves_others_alone()
    {
        var registry = Registry(perPeer: 8);
        var closed = new List<ReturnRouteSessionEventArgs>();
        registry.SessionClosed += (_, e) => closed.Add(e);
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
        closed.Should().ContainSingle().Which.Should().Match<ReturnRouteSessionEventArgs>(e => e.Session == alices[0] && e.Reason == SessionCloseReason.Evicted);
        registry.Find(Alice).Should().NotContain(alices[0]).And.Contain(ninth).And.HaveCount(8);
        registry.Find(Carol).Should().ContainSingle().Which.Should().BeSameAs(carol);
        registry.Unregister(alices[0]).Should().BeFalse("the registry already forgot it; no second event");
        closed.Should().HaveCount(1);
    }

    [Fact]
    public void Total_bound_refuses_a_new_DID_without_evicting_or_raising()
    {
        var registry = Registry(perPeer: 1, total: 2);
        var events = 0;
        registry.SessionClosed += (_, _) => events++;
        registry.Register(new FakeSession(Alice));
        registry.Register(new FakeSession(Carol));

        var result = registry.Register(new FakeSession("did:example:dave"));

        result.Registered.Should().BeFalse();
        result.Outcome.Should().Be(SessionRegistrationOutcome.RefusedTotalBound);
        result.Evicted.Should().BeNull();
        registry.Count.Should().Be(2);
        events.Should().Be(0);
    }

    [Fact]
    public void Registering_the_same_instance_twice_is_AlreadyRegistered()
    {
        var registry = Registry();
        var s = new FakeSession(Alice);
        registry.Register(s);
        var opened = 0;
        registry.SessionOpened += (_, _) => opened++;

        registry.Register(s).Outcome.Should().Be(SessionRegistrationOutcome.AlreadyRegistered);
        registry.Count.Should().Be(1);
        opened.Should().Be(0);
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
        registry.SessionOpened += (_, _) => secondSaw++;
        registry.SessionClosed += (_, _) => throw new InvalidOperationException("boom");

        var result = registry.Register(new FakeSession(Alice));

        result.Registered.Should().BeTrue();
        secondSaw.Should().Be(1);
        var act = () => registry.Unregister(registry.Find(Alice)[0]);
        act.Should().NotThrow();
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
        var evicted = new System.Collections.Concurrent.ConcurrentBag<IReturnRouteSession>();
        registry.SessionClosed += (_, e) => { if (e.Reason == SessionCloseReason.Evicted) evicted.Add(e.Session); };
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
        evicted.Should().HaveCount(Tasks - Dids * PerPeer);
        results.Count(r => r.Evicted is not null).Should().Be(Tasks - Dids * PerPeer);
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
