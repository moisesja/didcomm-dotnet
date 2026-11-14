using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// ADV-2 (#95): the push workers and the mediator's composition under dependencies that never return,
/// races between a push and a pickup on one record, container disposal with a push in flight, and
/// host registrations that close a dependency cycle through the mediator's parts. Every push is a
/// live push. Deterministic: every interleaving is a gate the test opens, every deadline a manual
/// clock the test moves.
/// </summary>
public sealed class MediatorLifetimeAdvTests
{
    private const string Routing = "did:example:adv2-mediator";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // ---- a dependency that never returns ----------------------------------------------------

    [Fact]
    public async Task A_store_that_never_returns_from_the_take_holds_a_worker_only_until_the_push_deadline()
    {
        var store = new ScriptedMessageStore();
        var never = new TaskCompletionSource<IReadOnlyList<QueuedMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var world = await LivePushWorld.StartAsync(m => m.PushConcurrency = 1, services: s => s.AddSingleton<IMessageStore>(store));
        try
        {
            var (a, _) = await world.LiveGrantAsync("adv2-a");
            var (b, sessionB) = await world.LiveGrantAsync("adv2-b");
            var fixture = world.Fixture;
            var jobA = await world.StoreAsync(a, 1);
            var jobB = await world.StoreAsync(b, 2);
            store.OnLease = (request, ct) =>
            {
                if (request.GrantPeerDid != a.Peer)
                    return store.Inner.LeaseAsync(request, ct);
                entered.TrySetResult();
                return new ValueTask<IReadOnlyList<QueuedMessage>>(never.Task); // a store call that never returns and never looks at its token
            };

            fixture.Dispatcher.Wake(jobA.GrantPeerDid).Should().BeTrue();
            await entered.Task.WaitAsync(Wait);
            fixture.Dispatcher.Wake(jobB.GrantPeerDid).Should().BeTrue();
            fixture.Time.Advance(TimeSpan.FromMinutes(5)); // well past PushTimeout (30 s)

            await fixture.Dispatcher.WaitForIdleAsync(Wait);
            fixture.PushOutcomeLog.Should().BeEquivalentTo(new[] { PushOutcomes.Failed, PushOutcomes.PushedLive }, "each push ends with one outcome within its deadline");
            LivePushWorld.Deliveries(sessionB).Should().ContainSingle("grant B's push is not held behind grant A's hung store call");
        }
        finally
        {
            never.TrySetResult(Array.Empty<QueuedMessage>());
        }
    }

    // ---- push vs pickup on one record (L-057) ------------------------------------------------

    [Fact]
    public async Task A_push_in_flight_holds_its_record_against_a_pickup_and_gives_it_back_the_moment_it_fails()
    {
        var gate = new PushGate();
        await using var world = await LivePushWorld.StartAsync();
        var (grant, session) = await world.LiveGrantAsync("adv2-a");
        session.OnPush = async (_, ct) =>
        {
            await gate.PassAsync(ct);
            return ReturnRouteSendResult.NotSent;
        };
        var fixture = world.Fixture;
        var job = await world.StoreAsync(grant);

        fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);

        var now = fixture.Options.Now();
        var duringPush = await fixture.Store.LeaseAsync(PickupRequest(grant.Peer, now), CancellationToken.None);
        duringPush.Should().BeEmpty("the record is being pushed: a pickup in the same instant must not deliver it a second time");
        (await fixture.Store.GetStatusAsync(grant.Peer, null, now, CancellationToken.None)).MessageCount.Should().Be(1, "it is still held and counted");

        gate.Open();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        var afterPush = await fixture.Store.LeaseAsync(PickupRequest(grant.Peer, now), CancellationToken.None);
        afterPush.Should().ContainSingle("a failed push gives its lease back at once, on the same clock reading")
            .Which.Id.Should().Be(job.MessageId);
    }

    // ---- disposal ----------------------------------------------------------------------------

    [Fact]
    public async Task Disposing_the_container_synchronously_with_a_push_in_flight_throws_nothing_and_loses_nothing()
    {
        var gate = new PushGate();
        var world = await LivePushWorld.StartAsync();
        var (grant, session) = await world.LiveGrantAsync("adv2-a");
        session.OnPush = async (_, ct) =>
        {
            await gate.PassAsync(ct);
            return ReturnRouteSendResult.Sent;
        };
        var fixture = world.Fixture;
        var job = await world.StoreAsync(grant);
        var dispatcher = fixture.Dispatcher;
        var store = fixture.Store;
        var options = fixture.Options;
        dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);

        FluentActions.Invoking(() => fixture.Services.Dispose()).Should().NotThrow();
        fixture.Time.Advance(TimeSpan.FromMinutes(1)); // the give-back's own bound, should it be needed
        await dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedShutdown);
        store.MessageCount.Should().Be(1);
        await MessagePickupLive.UntilAsync(async () => (await store.LeaseAsync(PickupRequest(grant.Peer, options.Now()), CancellationToken.None)).Count == 1, "the push gave its lease back on the way out");
        dispatcher.Wake(job.GrantPeerDid).Should().BeFalse("nothing is taken after disposal");
        await world.DisposeAsync();
    }

    // ---- DI cycles through the mediator's parts ---------------------------------------------

    public static TheoryData<string> Cycles => new()
    {
        "routing-key provider takes the protocol dispatcher",
        "own forward directory takes the protocol dispatcher",
        "mediation policy takes the protocol dispatcher",
        "mediator identity takes the protocol dispatcher",
    };

    [Theory]
    [MemberData(nameof(Cycles))]
    public async Task A_host_registration_that_closes_a_cycle_through_the_mediator_fails_fast_at_registration_or_first_resolution(string shape)
    {
        (Action<IServiceCollection> register, Action<MediatorOptions>? configure) = shape switch
        {
            "routing-key provider takes the protocol dispatcher" => ((Action<IServiceCollection>)(s => s.AddSingleton<IRecipientRoutingKeyProvider, KeysTakingDispatcher>()), (Action<MediatorOptions>?)null),
            "own forward directory takes the protocol dispatcher" => (s => s.AddSingleton<IForwardRecipientRegistry, DirectoryTakingDispatcher>(), o => o.CoordinateMediation.UseAsForwardRecipientRegistry = false),
            "mediation policy takes the protocol dispatcher" => (s => s.AddSingleton<IMediationPolicy, PolicyTakingDispatcher>(), null),
            "mediator identity takes the protocol dispatcher" => (s => s.AddSingleton<IMediatorIdentity, IdentityTakingDispatcher>(), null),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        var resolving = Task.Factory.StartNew(() =>
        {
            using var sp = Build(register, configure);
            return sp.GetRequiredService<ProtocolDispatcher>();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var finished = await Task.WhenAny(resolving, Task.Delay(Wait));

        finished.Should().BeSameAs(resolving, $"a cycle ({shape}) must fail fast, never hang");
        await FluentActions.Awaiting(() => resolving).Should().ThrowAsync<InvalidOperationException>(shape);
    }

    [Fact]
    public async Task A_host_message_store_that_takes_the_facade_resolves_and_the_mediator_starts_its_pipeline()
    {
        await using var sp = Build(s => s.AddSingleton<IMessageStore, StoreTakingClient>());

        var resolving = Task.Factory.StartNew(() => sp.GetRequiredService<ProtocolDispatcher>(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        (await Task.WhenAny(resolving, Task.Delay(Wait))).Should().BeSameAs(resolving);
        (await resolving).Should().NotBeNull();
        sp.GetRequiredService<IForwardSink>().Should().BeOfType<MediatorForwardSink>();
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static MessageLeaseRequest PickupRequest(string grantPeerDid, DateTimeOffset now)
        => new(grantPeerDid, null, 10, long.MaxValue, now, new MessageLease("adv2-pickup-session", now + TimeSpan.FromSeconds(60)));

    private static ServiceProvider Build(Action<IServiceCollection> before, Action<MediatorOptions>? configure = null)
    {
        var services = new ServiceCollection();
        before(services);
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddMediator(o =>
            {
                o.CoordinateMediation.RoutingDids.Add(Routing);
                configure?.Invoke(o);
            });
        });
        return services.BuildServiceProvider();
    }

    private sealed class KeysTakingDispatcher(ProtocolDispatcher dispatcher) : IRecipientRoutingKeyProvider
    {
        public ProtocolDispatcher Dispatcher { get; } = dispatcher;
        public ValueTask<IReadOnlyList<Jwk>> GetAsync(string recipientDid, CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<Jwk>>(Array.Empty<Jwk>());
    }

    private sealed class DirectoryTakingDispatcher(ProtocolDispatcher dispatcher) : IForwardRecipientRegistry
    {
        public ProtocolDispatcher Dispatcher { get; } = dispatcher;
        public ValueTask<bool> IsRegisteredAsync(string recipientDid, CancellationToken ct) => ValueTask.FromResult(false);
    }

    private sealed class PolicyTakingDispatcher(ProtocolDispatcher dispatcher) : IMediationPolicy
    {
        public ProtocolDispatcher Dispatcher { get; } = dispatcher;
        public ValueTask<MediationDecision> OnMediateRequestAsync(string peerDid, ProtocolContext context, CancellationToken ct) => new(MediationDecision.Grant);
        public ValueTask<bool> MayRegisterAsync(string peerDid, string recipientDid, ProtocolContext context, CancellationToken ct) => new(true);
    }

    private sealed class IdentityTakingDispatcher(ProtocolDispatcher dispatcher) : IMediatorIdentity
    {
        public ProtocolDispatcher Dispatcher { get; } = dispatcher;
        public IReadOnlyList<string> RoutingDids { get; } = new[] { Routing };
    }

    private sealed class StoreTakingClient(DidCommClient client) : IMessageStore
    {
        private readonly InMemoryMessageStore _inner = new();
        public DidCommClient Client { get; } = client;
        public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct) => _inner.EnqueueAsync(message, lease, now, ct);
        public ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct) => _inner.LeaseAsync(request, ct);
        public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct) => _inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
        public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => _inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
        public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct) => _inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
        public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => _inner.ReleaseLeasesAsync(leaseOwner, ct);
        public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => _inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
        public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => _inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
        public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => _inner.PurgeGrantAsync(grantPeerDid, ct);
        public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => _inner.PurgeExpiredAsync(now, ct);
    }
}
