using System.Runtime.CompilerServices;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// ADV-2, goal 5: composition and lifetime. What a host can get wrong when it wires the pickup role
/// — and whether the role then fails at once with a message that names the mistake; what the role
/// keeps alive; and what happens to work that is still in flight when the container goes away.
/// </summary>
public sealed class MessagePickupAdversarialResourceDiTests
{
    private static ServiceProvider Build(Action<DidCommBuilder> configure, Action<IServiceCollection>? before = null)
    {
        var services = new ServiceCollection();
        before?.Invoke(services);
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
            configure(b);
        });
        return services.BuildServiceProvider();
    }

    // ---- a collaborator of the queue that takes the queue -------------------------------------------

    public static TheoryData<string> CollaboratorsThatTakeTheQueue => new() { "the message store", "the mediation store" };

    /// <summary>
    /// <see cref="MessagePickupQueue"/> is the type host code is told to take in a constructor ("so
    /// a forward sink may take it"). A host store that takes it — a mediation store that wants to
    /// purge what a revoked grant left behind is the obvious one — closes a cycle: the queue needs
    /// the store, the store needs the queue. Everywhere else in this library such a cycle fails at
    /// once with "A circular dependency was detected …" (the dispatcher, the handler registry, the
    /// handlers, <c>DidCommClient</c>, the WebSocket transport are guarded; a message store that
    /// takes the dispatcher is tested to fail fast). The queue's own factory is not guarded: the
    /// first resolution re-enters it a few hundred times (502 nested entries were counted) and then
    /// stops for good — no exception, no stack overflow, no return. The host hangs, with no
    /// message, at start-up or on its first inbound.
    /// </summary>
    /// <remarks>
    /// Slow on purpose while the defect exists: a hang can only be seen by waiting for it (two
    /// seconds here; a guarded factory answers in milliseconds). The resolution that hangs is left
    /// behind with its thread until the test process ends.
    /// </remarks>
    [Theory]
    [MemberData(nameof(CollaboratorsThatTakeTheQueue))]
    public async Task A_host_store_that_takes_the_queue_in_its_constructor_fails_fast_with_a_circular_dependency_message_instead_of_hanging(string which)
    {
        MessagePickupAdversarialResource.RaiseThreadPoolFloor();
        var sp = Build(
            b =>
            {
                b.AddCoordinateMediation();
                b.AddMessagePickup();
            },
            before: s =>
            {
                if (which == "the message store")
                    s.AddSingleton<IMessageStore, QueueTakingMessageStore>();
                else
                    s.AddSingleton<IMediationStore, QueueTakingMediationStore>();
            });

        var resolution = Task.Run(() => sp.GetRequiredService<ProtocolDispatcher>());
        var finished = await Task.WhenAny(resolution, Task.Delay(TimeSpan.FromSeconds(2))) == resolution;

        finished.Should().BeTrue("a dependency cycle through the pickup queue must fail at once, like every other cycle in the library, not hang the host");
        var act = () => resolution;
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("circular dependency");
        await sp.DisposeAsync();
    }

    private sealed class QueueTakingMessageStore(MessagePickupQueue queue) : IMessageStore
    {
        private readonly InMemoryMessageStore _inner = new();

        public MessagePickupQueue Queue { get; } = queue;

        public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct) => _inner.EnqueueAsync(message, lease, now, ct);
        public ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct) => _inner.LeaseAsync(request, ct);
        public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct) => _inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
        public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => _inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
        public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => _inner.ReleaseLeasesAsync(leaseOwner, ct);
        public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => _inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
        public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => _inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
        public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => _inner.PurgeGrantAsync(grantPeerDid, ct);
        public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => _inner.PurgeExpiredAsync(now, ct);
    }

    /// <summary>A mediation store that purges the queue when a grant is revoked — and takes the queue to do it.</summary>
    private sealed class QueueTakingMediationStore(MessagePickupQueue queue) : IMediationStore
    {
        private readonly InMemoryMediationStore _inner = new(new CoordinateMediationOptions());

        public MessagePickupQueue Queue { get; } = queue;

        public ValueTask<MediationGrant?> GetGrantAsync(string peerDid, CancellationToken ct) => _inner.GetGrantAsync(peerDid, ct);
        public ValueTask<MediationGrant?> GrantAsync(MediationGrant grant, CancellationToken ct) => _inner.GrantAsync(grant, ct);
        public ValueTask<bool> RevokeAsync(string peerDid, CancellationToken ct) => _inner.RevokeAsync(peerDid, ct);
        public ValueTask<IReadOnlyList<RecipientStoreOutcome>> UpdateRecipientsAsync(string peerDid, IReadOnlyList<RecipientUpdate> updates, CancellationToken ct) => _inner.UpdateRecipientsAsync(peerDid, updates, ct);
        public ValueTask<RecipientPage> ListRecipientsAsync(string peerDid, int offset, int limit, CancellationToken ct) => _inner.ListRecipientsAsync(peerDid, offset, limit, ct);
        public ValueTask<MediationGrant?> FindGrantByRecipientDidAsync(string recipientDid, CancellationToken ct) => _inner.FindGrantByRecipientDidAsync(recipientDid, ct);
    }

    // ---- settings changed after the role was built --------------------------------------------------

    /// <summary>
    /// The store "reads its bounds once"; the delivery budget is computed once. The role's other
    /// three settings (<c>LeaseTimeout</c>, <c>MaxDeliveryBatch</c>, <c>MaxAckIds</c>) are read from
    /// the live options object on every request and never validated again: a host that changes
    /// <c>IOptions&lt;MessagePickupOptions&gt;.Value</c> after start-up gets a role that runs with
    /// values <c>Validate()</c> refuses — here a lease that is over before it starts, so every
    /// request hands out the same messages again.
    /// </summary>
    [Fact]
    public async Task A_setting_changed_after_the_role_was_built_is_not_applied_without_validation()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var id = await fixture.QueueAsync(peer, PickupFixture.Payload(30, 1));
        var options = fixture.Host.Services.GetRequiredService<IOptions<MessagePickupOptions>>().Value;

        // What Validate() refuses at registration and at first use.
        options.LeaseTimeout = TimeSpan.FromSeconds(-1);
        var invalid = () => options.Validate();
        invalid.Should().Throw<ArgumentException>();

        (await MessagePickupAdversarialResource.FetchIdsAsync(fixture, peer, mediator)).Should().Equal(id);
        (await MessagePickupAdversarialResource.FetchIdsAsync(fixture, peer, mediator)).Should().BeEmpty(
            "a delivered message is leased for the LeaseTimeout the role was validated with; a value Validate() refuses must not silently take effect");
    }

    // ---- what the role keeps alive --------------------------------------------------------------------

    /// <summary>
    /// Many connections that turn live delivery on, are pushed to, take leases and then go away —
    /// closed, or simply dropped without their <c>Closed</c> signal ever firing. Nothing in the role
    /// may keep them: not the per-session state, not the store's owner index, not a callback.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sessions_that_went_away_are_not_kept_alive_by_the_role(bool closed)
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.MaxQueueMessages = 500);
        var (mediator, peer) = await fixture.EnrolledAsync();

        var sessions = await UseAndDropSessionsAsync(fixture, peer, mediator, 100, closed);

        await MessagePickupAdversarialResource.UntilAsync(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return Task.FromResult(sessions.All(s => !s.IsAlive));
        }, "every session that went away is collectable: " + sessions.Count(s => s.IsAlive) + " still alive");

        // And what they were pushed is not lost with them.
        fixture.Memory.MessageCount.Should().Be(100);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<List<WeakReference>> UseAndDropSessionsAsync(PickupFixture fixture, string peer, string mediator, int count, bool close)
    {
        var references = new List<WeakReference>();
        for (var i = 0; i < count; i++)
        {
            var session = fixture.Session(peer, mediator, live: true);
            (await fixture.StatusOfAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, true), mediator, session)).LiveDelivery.Should().BeTrue();
            (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, (byte)i))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
            await fixture.SendAsync(MessagePickupAdversarialResource.DeliveryRequest(peer, mediator, 1), mediator, session);
            if (close)
                session.Close();
            fixture.Sessions.Unregister(session);
            session.Sent.Clear();
            session.Attempts.Clear();
            references.Add(new WeakReference(session));
        }
        return references;
    }

    // ---- the container goes away with work in flight ---------------------------------------------------

    /// <summary>
    /// Sessions close after the container was disposed, and the host's store now throws
    /// <see cref="ObjectDisposedException"/> — synchronously or from a faulted task — for the
    /// release the role queued on the thread pool. No exception may go unobserved (an unobserved
    /// task exception, or one on a thread-pool thread, is a process-level event).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Leases_released_in_the_background_after_the_container_was_disposed_leave_no_unobserved_exception(bool faultedTask)
    {
        MessagePickupAdversarialResource.RaiseThreadPoolFloor();
        var unobserved = 0;
        var releases = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.InnerExceptions.Any(x => x is ObjectDisposedException { ObjectName: nameof(MessagePickupAdversarialResourceDiTests) }))
                Interlocked.Increment(ref unobserved);
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var fixture = PickupFixture.Create(fakeStore: true);
            var (mediator, peer) = await fixture.EnrolledAsync();
            var sessions = new List<PickupFakeSession>();
            for (var i = 0; i < 20; i++)
            {
                var session = fixture.Session(peer, mediator, live: true);
                (await fixture.StatusOfAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, true), mediator, session)).LiveDelivery.Should().BeTrue();
                (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, (byte)i))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
                sessions.Add(session);
            }

            var store = fixture.FakeStore!;
            await fixture.DisposeAsync();
            store.OnReleaseLeases = (_, _) =>
            {
                Interlocked.Increment(ref releases);
                var gone = new ObjectDisposedException(nameof(MessagePickupAdversarialResourceDiTests));
                return faultedTask ? new ValueTask<int>(Task.FromException<int>(gone)) : throw gone;
            };

            foreach (var session in sessions)
                session.Close();

            await MessagePickupAdversarialResource.UntilAsync(() => Task.FromResult(Volatile.Read(ref releases) == sessions.Count), "every closed session's release ran");
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            unobserved.Should().Be(0, "the background release observes its own faults");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }
}
