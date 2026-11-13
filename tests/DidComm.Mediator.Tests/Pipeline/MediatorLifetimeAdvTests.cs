using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.TestSupport;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// ADV-2 (#95): direct push and the mediator's composition under dependencies that never return,
/// races between a push and a pickup on one record, container disposal with a push in flight, and
/// host registrations that close a dependency cycle through the mediator's parts. Deterministic:
/// every interleaving is a gate the test opens, every deadline a manual clock the test moves.
/// </summary>
public sealed class MediatorLifetimeAdvTests
{
    private const string PeerA = "did:example:adv2-peer-a";
    private const string RecipientA = "did:example:adv2-recipient-a";
    private const string PeerB = "did:example:adv2-peer-b";
    private const string RecipientB = "did:example:adv2-recipient-b";
    private const string Routing = "did:example:adv2-mediator";
    private static readonly Uri Inbox = new("https://adv2-recipient.example/inbox");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // ---- a dependency that never returns ----------------------------------------------------

    [Fact(Skip = "ADV-2 DEFECT 5 (P3): the per-push deadline only cancels a token, so a store (or mediation store, or transport) that never returns and ignores the token holds its push worker forever: the job never ends with an outcome, its grant never pushes again, and with DirectPushConcurrency such grants every push stops (DirectPushDispatcher.cs:413/425/434/477/496 await without WaitAsync; RoutingDidValidator abandons a hung check with WaitAsync). main has no direct push.")]
    public async Task A_store_that_never_returns_from_the_take_holds_a_worker_only_until_the_push_deadline()
    {
        var store = new ScriptedMessageStore();
        var never = new TaskCompletionSource<QueuedMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = PushFixture.Create(directory: _ => Inbox, configure: m => m.DirectPushConcurrency = 1, services: s => s.AddSingleton<IMessageStore>(store));
        try
        {
            await fixture.EnrolAsync(PeerA, RecipientA);
            await fixture.EnrolAsync(PeerB, RecipientB);
            var jobA = await fixture.QueuedJobAsync(RecipientA, forwardId: "fwd-a");
            var jobB = await fixture.QueuedJobAsync(RecipientB, forwardId: "fwd-b");
            store.OnLeaseById = (grant, id, lease, now, ct) =>
            {
                if (grant != PeerA)
                    return store.Inner.LeaseByIdAsync(grant, id, lease, now, ct);
                entered.TrySetResult();
                return new ValueTask<QueuedMessage?>(never.Task); // a store call that never returns and never looks at its token
            };

            fixture.Dispatcher.TryEnqueue(jobA).Should().BeTrue();
            await entered.Task.WaitAsync(Wait);
            fixture.Dispatcher.TryEnqueue(jobB).Should().BeTrue();
            fixture.Time.Advance(TimeSpan.FromMinutes(5)); // well past DirectPushTimeout (30 s) and the lease margin

            await fixture.Dispatcher.WaitForIdleAsync(Wait);
            fixture.PushOutcomeLog.Should().BeEquivalentTo(new[] { PushOutcomes.Failed, PushOutcomes.Pushed }, "each job ends with one outcome within its deadline");
            fixture.Router.Requests.Should().ContainSingle("grant B's push is not held behind grant A's hung store call");
        }
        finally
        {
            never.TrySetResult(null);
        }
    }

    // ---- push vs pickup on one record (L-057) ------------------------------------------------

    [Fact]
    public async Task A_push_in_flight_holds_its_record_against_a_pickup_and_gives_it_back_the_moment_it_fails()
    {
        var gate = new PushGate();
        await using var fixture = PushFixture.Create(
            send: async (request, ct) =>
            {
                await gate.PassAsync(ct);
                throw PushFixture.TransportFailure(request, 503);
            },
            directory: _ => Inbox);
        await fixture.EnrolAsync(PeerA, RecipientA);
        var job = await fixture.QueuedJobAsync(RecipientA);

        fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);

        var now = fixture.Options.Now();
        var duringPush = await fixture.Store.LeaseAsync(PickupRequest(now), CancellationToken.None);
        duringPush.Should().BeEmpty("the record is being pushed: a pickup in the same instant must not deliver it a second time");
        (await fixture.Store.GetStatusAsync(PeerA, null, now, CancellationToken.None)).MessageCount.Should().Be(1, "it is still held and counted");

        gate.Open();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        var afterPush = await fixture.Store.LeaseAsync(PickupRequest(now), CancellationToken.None);
        afterPush.Should().ContainSingle("a failed push gives its lease back at once, on the same clock reading")
            .Which.Id.Should().Be(job.MessageId);
    }

    [Fact]
    public async Task A_pickup_lease_taken_first_keeps_the_push_away_and_the_push_ends_with_one_outcome()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(PeerA, RecipientA);
        var job = await fixture.QueuedJobAsync(RecipientA);
        var now = fixture.Options.Now();
        (await fixture.Store.LeaseAsync(PickupRequest(now), CancellationToken.None)).Should().ContainSingle();

        fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedNotHeld);
        fixture.Router.Requests.Should().BeEmpty();
        fixture.Store.MessageCount.Should().Be(1, "the pickup's acknowledgement removes it, not the push");
    }

    // ---- disposal ----------------------------------------------------------------------------

    [Fact]
    public async Task Disposing_the_container_synchronously_with_a_push_in_flight_throws_nothing_and_loses_nothing()
    {
        var gate = new PushGate();
        var fixture = PushFixture.Create(
            send: async (_, ct) =>
            {
                await gate.PassAsync(ct);
                return new TransportResult(true, 202);
            },
            directory: _ => Inbox);
        await fixture.EnrolAsync(PeerA, RecipientA);
        var job = await fixture.QueuedJobAsync(RecipientA);
        var dispatcher = fixture.Dispatcher;
        var store = fixture.Store;
        var options = fixture.Options;
        dispatcher.TryEnqueue(job).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);

        FluentActions.Invoking(() => fixture.Services.Dispose()).Should().NotThrow();
        fixture.Time.Advance(TimeSpan.FromMinutes(1)); // the give-back's own bound, should it be needed
        await dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedShutdown);
        store.MessageCount.Should().Be(1);
        (await store.LeaseAsync(PickupRequest(options.Now()), CancellationToken.None)).Should().ContainSingle("the push gave its lease back on the way out");
        dispatcher.TryEnqueue(job).Should().BeFalse("nothing is taken after disposal");
        await fixture.DisposeAsync();
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

    private static MessageLeaseRequest PickupRequest(DateTimeOffset now)
        => new(PeerA, null, 10, long.MaxValue, now, new MessageLease("adv2-pickup-session", now + TimeSpan.FromSeconds(60)));

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
                o.DirectPush = true;
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
        public ValueTask<QueuedMessage?> LeaseByIdAsync(string grantPeerDid, string messageId, MessageLease lease, DateTimeOffset now, CancellationToken ct) => _inner.LeaseByIdAsync(grantPeerDid, messageId, lease, now, ct);
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
