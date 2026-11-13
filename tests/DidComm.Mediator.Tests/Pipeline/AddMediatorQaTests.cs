using System.Globalization;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Jose;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// QA-1 (#95): <c>AddMediator()</c> settings at and around every documented bound, a delegate that
/// throws, composition with the host's <c>Configure&lt;T&gt;</c> before and after, repeated calls
/// whose settings only conflict together, the rewrap / routing-key-provider rule reached by other
/// routes than the delegate, and DI cycles that must fail fast and never hang.
/// </summary>
public sealed class AddMediatorQaTests
{
    private const string Routing = "did:example:qa-routing";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // ── every pipeline bound, one tick either side ────────────────────────────────────────

    public static TheoryData<string, Action<MediatorOptions>, bool> Bounds => new()
    {
        { "MaxForwardDelay", o => o.MaxForwardDelay = TimeSpan.FromTicks(-1), false },
        { "MaxForwardDelay", o => o.MaxForwardDelay = TimeSpan.Zero, true },
        { "MaxForwardDelay", o => o.MaxForwardDelay = TimeSpan.FromMilliseconds(int.MaxValue), true },
        { "MaxForwardDelay", o => o.MaxForwardDelay = TimeSpan.FromMilliseconds(int.MaxValue) + TimeSpan.FromTicks(1), false },
        { "PurgeInterval", o => o.PurgeInterval = TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1), false },
        { "PurgeInterval", o => o.PurgeInterval = TimeSpan.FromSeconds(1), true },
        { "PurgeInterval", o => o.PurgeInterval = TimeSpan.FromHours(24), true },
        { "PurgeInterval", o => o.PurgeInterval = TimeSpan.FromHours(24) + TimeSpan.FromTicks(1), false },
        { "DirectPushConcurrency", o => o.DirectPushConcurrency = 0, false },
        { "DirectPushConcurrency", o => o.DirectPushConcurrency = 1, true },
        { "DirectPushConcurrency", o => o.DirectPushConcurrency = 64, true },
        { "DirectPushConcurrency", o => o.DirectPushConcurrency = 65, false },
        { "DirectPushTimeout", o => o.DirectPushTimeout = TimeSpan.Zero, false },
        { "DirectPushTimeout", o => o.DirectPushTimeout = TimeSpan.FromTicks(1), true },
        { "DirectPushTimeout", o => o.DirectPushTimeout = TimeSpan.FromMinutes(5), true },
        { "DirectPushTimeout", o => o.DirectPushTimeout = TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1), false },
        { "RewrapMode", o => o.RewrapMode = (RewrapMode)99, false },
        { "RewrapMode", o => o.RewrapMode = (RewrapMode)(-1), false },
        { "Pickup.MaxMessageAge", o => o.Pickup.MaxMessageAge = TimeSpan.FromHours(1), false },
        { "Pickup.MaxMessageAge", o => o.Pickup.MaxMessageAge = TimeSpan.FromHours(1) + TimeSpan.FromTicks(1), true },
        { "Pickup.MaxMessageAge", o => o.Pickup.MaxMessageAge = null, true },
        { "MaxQueueMessages", o => o.Pickup.MaxQueueMessages = 0, false },
        { "MaxStoreMessages", o => { o.Pickup.MaxQueueMessages = 10; o.Pickup.MaxStoreMessages = 9; }, false },
        { "LeaseTimeout", o => o.Pickup.LeaseTimeout = TimeSpan.FromHours(24) + TimeSpan.FromTicks(1), false },
        { "MaxGrants", o => o.CoordinateMediation.MaxGrants = 0, false },
    };

    [Theory]
    [MemberData(nameof(Bounds))]
    public void Each_bound_is_refused_one_tick_outside_and_accepted_on_it_and_a_refusal_adds_nothing(string setting, Action<MediatorOptions> assign, bool accepted)
    {
        var services = new ServiceCollection();
        var builder = Builder(services);
        var before = services.Count;

        var act = () => builder.AddMediator(o =>
        {
            o.CoordinateMediation.RoutingDids.Add(Routing);
            assign(o);
        });

        if (accepted)
        {
            act.Should().NotThrow(setting);
            return;
        }
        var ex = act.Should().Throw<ArgumentException>(setting).Which;
        ex.Message.Should().NotContain("2147483647").And.NotContain("00:05:00.0000001").And.NotContain("99");
        services.Count.Should().Be(before, "a refused call leaves the collection as it was");
    }

    [Fact]
    public void A_delegate_that_throws_propagates_its_own_exception_and_adds_nothing()
    {
        var services = new ServiceCollection();
        var builder = Builder(services);
        var before = services.Count;

        var act = () => builder.AddMediator(_ => throw new NotSupportedException("qa delegate"));

        act.Should().Throw<NotSupportedException>();
        services.Count.Should().Be(before);
    }

    // ── composition with the host's Configure<T> ─────────────────────────────────────────

    [Fact]
    public async Task A_host_Configure_before_keeps_what_the_delegate_does_not_assign_and_one_after_wins()
    {
        var services = new ServiceCollection();
        services.Configure<MessagePickupOptions>(o =>
        {
            o.MaxQueueMessages = 7;
            o.MaxQueueBytes = 7_000;
        });
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddMediator(o =>
            {
                o.CoordinateMediation.RoutingDids.Add(Routing);
                o.Pickup.MaxQueueBytes = 5_000;
                o.Pickup.MaxDeliveryBatch = 3;
            });
        });
        services.Configure<MessagePickupOptions>(o => o.MaxDeliveryBatch = 4);
        await using var sp = services.BuildServiceProvider();

        var pickup = sp.GetRequiredService<IOptions<MessagePickupOptions>>().Value;
        pickup.MaxQueueMessages.Should().Be(7, "the delegate never assigned it");
        pickup.MaxQueueBytes.Should().Be(5_000, "the delegate ran after the host's earlier Configure");
        pickup.MaxDeliveryBatch.Should().Be(4, "a later Configure wins");
    }

    [Fact]
    public async Task The_routing_dids_of_a_host_Configure_and_of_the_delegate_are_both_kept_host_first()
    {
        var services = new ServiceCollection();
        services.Configure<CoordinateMediationOptions>(o => o.RoutingDids.Add("did:example:qa-host-first"));
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddMediator(o => o.CoordinateMediation.RoutingDids.Add(Routing));
        });
        await using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IMediatorIdentity>().RoutingDids.Should().Equal("did:example:qa-host-first", Routing);
    }

    [Fact]
    public async Task Repeated_calls_whose_settings_conflict_only_together_are_refused_when_the_mediator_starts()
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddMediator(o =>
            {
                o.CoordinateMediation.RoutingDids.Add(Routing);
                o.MaxForwardDelay = TimeSpan.FromHours(2);
            });
            // Alone this is fine (its own probe has the 1-hour default cap); with the first call it is not.
            b.AddMediator(o => o.Pickup.MaxMessageAge = TimeSpan.FromMinutes(90));
        });
        await using var sp = services.BuildServiceProvider();
        var hosted = sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<MediatorHostedService>().Single();

        var act = () => hosted.StartingAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain(nameof(MessagePickupOptions.MaxMessageAge));
        sp.GetRequiredService<ForwardProcessorOptions>().MaxDelay.Should().Be(TimeSpan.FromHours(2), "the composed cap is the one the processor gets");
    }

    // ── the forward settings reached by other routes (QA-1 DEFECT 6, fixed by Q-8) ────────

    // QA-1 DEFECT 6 (P3): the rule "RewrapMode.ReanoncryptToNext excludes a host
    // IRecipientRoutingKeyProvider" was checked only against AddMediator()'s own delegate and the
    // registrations made before it, and nothing checked the forward settings or the forward
    // directory in effect. The mediator's start-up now refuses each of these routes (Q-8). L-020:
    // the "today" pins that asserted a start and a silently ignored provider or dropped forwards
    // are turned into refusal tests.
    [Fact]
    public async Task Rewrap_set_through_a_host_Configure_with_a_host_routing_key_provider_fails_start_up()
    {
        await using var fixture = await HostedFixture.CreateAsync(
            before: s => s.AddSingleton<IRecipientRoutingKeyProvider>(new QaRoutingKeyProvider()),
            after: s => s.Configure<MediatorOptions>(o => o.RewrapMode = RewrapMode.ReanoncryptToNext));

        await RefusedAsync(fixture, "IRecipientRoutingKeyProvider");
    }

    [Fact]
    public async Task Rewrap_in_the_delegate_with_a_provider_registered_after_AddMediator_fails_start_up()
    {
        await using var fixture = await HostedFixture.CreateAsync(
            configure: o => o.RewrapMode = RewrapMode.ReanoncryptToNext,
            after: s => s.AddSingleton<IRecipientRoutingKeyProvider>(new QaRoutingKeyProvider()));

        await RefusedAsync(fixture, "IRecipientRoutingKeyProvider");
    }

    [Fact]
    public async Task Pass_through_with_a_provider_registered_after_AddMediator_starts_and_uses_it()
    {
        var provider = new QaRoutingKeyProvider();
        await using var fixture = await HostedFixture.CreateAsync(after: s => s.AddSingleton<IRecipientRoutingKeyProvider>(provider));

        await fixture.StartingAsync();

        fixture.Services.GetRequiredService<IRecipientRoutingKeyProvider>().Should().BeSameAs(provider);
    }

    [Fact]
    public async Task A_mediator_whose_grant_store_is_not_the_forward_directory_and_no_other_directory_fails_start_up()
    {
        // With UseAsForwardRecipientRegistry = false and no directory of the host's, AddRoutingMediator's
        // empty in-memory directory would drop every forward to an enrolled recipient as unknown.
        await using var fixture = await HostedFixture.CreateAsync(configure: o => o.CoordinateMediation.UseAsForwardRecipientRegistry = false);

        await RefusedAsync(fixture, "forward recipient directory");
        fixture.Services.GetRequiredService<IForwardRecipientRegistry>().Should().BeOfType<InMemoryForwardRecipientRegistry>();
    }

    [Fact]
    public async Task A_host_forward_directory_with_the_grant_store_not_used_starts()
    {
        var directory = new QaForwardDirectory();
        await using var fixture = await HostedFixture.CreateAsync(
            configure: o => o.CoordinateMediation.UseAsForwardRecipientRegistry = false,
            before: s => s.AddSingleton<IForwardRecipientRegistry>(directory));

        await fixture.StartingAsync();

        fixture.Services.GetRequiredService<IForwardRecipientRegistry>().Should().BeSameAs(directory);
    }

    public static TheoryData<string, ForwardProcessorOptions> LaterForwardOptions => new()
    {
        { "no cap", new ForwardProcessorOptions(RewrapMode.PassThrough) },
        { "a longer cap", new ForwardProcessorOptions(RewrapMode.PassThrough) { MaxDelay = TimeSpan.FromHours(1) + TimeSpan.FromTicks(1) } },
        { "another mode", new ForwardProcessorOptions(RewrapMode.ReanoncryptToNext) { MaxDelay = TimeSpan.FromHours(1) } },
    };

    [Theory]
    [MemberData(nameof(LaterForwardOptions))]
    public async Task Forward_settings_registered_after_AddMediator_that_differ_from_the_mediators_fail_start_up(string because, ForwardProcessorOptions later)
    {
        await using var fixture = await HostedFixture.CreateAsync(after: s => s.AddSingleton(later));

        var refusal = await RefusedAsync(fixture, "ForwardProcessorOptions");
        refusal.Message.Should().NotBeNullOrEmpty(because);
    }

    [Fact]
    public async Task Forward_settings_registered_after_AddMediator_with_a_shorter_cap_and_the_same_mode_start()
    {
        // A stricter cap still meets the FR-PICK-10 bound.
        await using var fixture = await HostedFixture.CreateAsync(after: s => s.AddSingleton(new ForwardProcessorOptions(RewrapMode.PassThrough) { MaxDelay = TimeSpan.FromMinutes(1) }));

        await fixture.StartingAsync();
    }

    private static async Task<InvalidOperationException> RefusedAsync(HostedFixture fixture, string names)
    {
        var refusal = (await FluentActions.Awaiting(fixture.StartingAsync).Should().ThrowAsync<InvalidOperationException>()).Which;
        refusal.Message.Should().Contain(names);
        foreach (var did in fixture.Dids)
            refusal.Message.Should().NotContain(did);
        return refusal;
    }

    private sealed class QaForwardDirectory : IForwardRecipientRegistry
    {
        public ValueTask<bool> IsRegisteredAsync(string recipientDid, CancellationToken ct) => ValueTask.FromResult(false);
    }

    // ── DI cycles fail fast and never hang ────────────────────────────────────────────────

    [Fact]
    public async Task A_host_message_store_that_takes_the_pickup_queue_fails_the_first_resolution_fast()
    {
        await using var sp = Build(before: s => s.AddSingleton<IMessageStore, StoreTakingQueue>());

        await ResolveFailsFast(sp, typeof(ProtocolDispatcher));
        await ResolveFailsFast(sp, typeof(MessagePickupQueue));
    }

    [Fact]
    public async Task A_host_mediation_store_that_takes_the_push_dispatcher_fails_the_first_resolution_fast()
    {
        await using var sp = Build(before: s => s.AddSingleton<IMediationStore, MediationStoreTakingDispatcher>());

        await ResolveFailsFast(sp, typeof(MediatorForwardSink));
    }

    [Fact]
    public async Task A_host_message_store_that_takes_the_protocol_dispatcher_fails_the_first_resolution_fast()
    {
        await using var sp = Build(before: s => s.AddSingleton<IMessageStore, StoreTakingDispatcher>());

        await ResolveFailsFast(sp, typeof(ProtocolDispatcher));
    }

    [Fact]
    public async Task A_host_message_store_that_takes_the_forward_sink_fails_the_first_resolution_fast()
    {
        await using var sp = Build(before: s => s.AddSingleton<IMessageStore, StoreTakingSink>());

        await ResolveFailsFast(sp, typeof(IForwardSink));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static DidCommBuilder Builder(IServiceCollection services)
    {
        DidCommBuilder? captured = null;
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            captured = b;
        });
        return captured!;
    }

    private static ServiceProvider Build(Action<IServiceCollection>? before = null)
    {
        var services = new ServiceCollection();
        before?.Invoke(services);
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddMediator(o =>
            {
                o.CoordinateMediation.RoutingDids.Add(Routing);
                o.DirectPush = true;
            });
        });
        return services.BuildServiceProvider();
    }

    private static async Task ResolveFailsFast(IServiceProvider sp, Type service)
    {
        var resolving = Task.Factory.StartNew(() => sp.GetRequiredService(service), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var finished = await Task.WhenAny(resolving, Task.Delay(Timeout));
        finished.Should().BeSameAs(resolving, $"resolving {service.Name} must not hang");
        await FluentActions.Awaiting(() => resolving).Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class QaRoutingKeyProvider : IRecipientRoutingKeyProvider
    {
        public ValueTask<IReadOnlyList<Jwk>> GetAsync(string recipientDid, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<Jwk>>(Array.Empty<Jwk>());
    }

    private abstract class DelegatingStore : IMessageStore
    {
        private readonly InMemoryMessageStore _inner = new();
        public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct) => _inner.EnqueueAsync(message, lease, now, ct);
        public ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct) => _inner.LeaseAsync(request, ct);
        public ValueTask<QueuedMessage?> LeaseByIdAsync(string grantPeerDid, string messageId, MessageLease lease, DateTimeOffset now, CancellationToken ct)
            => _inner.LeaseByIdAsync(grantPeerDid, messageId, lease, now, ct);

        public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct) => _inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
        public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => _inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
        public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct) => _inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
        public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => _inner.ReleaseLeasesAsync(leaseOwner, ct);
        public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => _inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
        public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => _inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
        public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => _inner.PurgeGrantAsync(grantPeerDid, ct);
        public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => _inner.PurgeExpiredAsync(now, ct);
    }

    private sealed class StoreTakingQueue(MessagePickupQueue queue) : DelegatingStore
    {
        public MessagePickupQueue Queue { get; } = queue;
    }

    private sealed class StoreTakingDispatcher(ProtocolDispatcher dispatcher) : DelegatingStore
    {
        public ProtocolDispatcher Dispatcher { get; } = dispatcher;
    }

    private sealed class StoreTakingSink(IForwardSink sink) : DelegatingStore
    {
        public IForwardSink Sink { get; } = sink;
    }

    private sealed class MediationStoreTakingDispatcher(DirectPushDispatcher pushes) : IMediationStore
    {
        private readonly InMemoryMediationStore _inner = new(new CoordinateMediationOptions());
        public DirectPushDispatcher Pushes { get; } = pushes;
        public ValueTask<MediationGrant?> GetGrantAsync(string peerDid, CancellationToken ct) => _inner.GetGrantAsync(peerDid, ct);
        public ValueTask<MediationGrant?> GrantAsync(MediationGrant grant, CancellationToken ct) => _inner.GrantAsync(grant, ct);
        public ValueTask<bool> RevokeAsync(string peerDid, CancellationToken ct) => _inner.RevokeAsync(peerDid, ct);
        public ValueTask<IReadOnlyList<RecipientStoreOutcome>> UpdateRecipientsAsync(string peerDid, IReadOnlyList<RecipientUpdate> updates, CancellationToken ct) => _inner.UpdateRecipientsAsync(peerDid, updates, ct);
        public ValueTask<RecipientPage> ListRecipientsAsync(string peerDid, int offset, int limit, CancellationToken ct) => _inner.ListRecipientsAsync(peerDid, offset, limit, ct);
        public ValueTask<MediationGrant?> FindGrantByRecipientDidAsync(string recipientDid, CancellationToken ct) => _inner.FindGrantByRecipientDidAsync(recipientDid, ct);
    }
}
