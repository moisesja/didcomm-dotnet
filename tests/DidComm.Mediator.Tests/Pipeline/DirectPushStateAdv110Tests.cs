using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// Red team pass 1 (#110, direct push), state: the push mark, leases, the direct pause and the
/// delivery order under adversarial interleavings — several coordinators over one store, pickups
/// that take, release, give back, pass over and acknowledge, leases that run out during a POST,
/// cancellations before and during the send, a live session that takes, refuses or cannot fit its
/// pushes — and the dispatcher's retained state under grant churn. Seam-driven: scripted directory,
/// a scripted sender, store hooks, the manual clock; no sleeps.
/// </summary>
public sealed class DirectPushStateAdv110Tests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PushTimeout = new MediatorOptions().PushTimeout;
    private static readonly TimeSpan Pause = PushDispatcher.CooldownPushTimeouts * PushTimeout;

    // ---- exactly one attempt, nothing lost: a seeded interleaving of everything that touches a record ----

    // Three coordinators over one store push the same grant's records directly; the first also runs
    // live pushes over a live session that takes, refuses (NotSent) or cannot fit (TooLarge) each one
    // at random. Concurrently a pickup takes up to three records under its own (non-push) lease and
    // acknowledges, releases, gives back as unsent, passes over or abandons them. The endpoint
    // accepts, refuses, faults; some pushes are cancelled before the call, between the take and the
    // send, or during the send; the clock jumps past the lease timeout during some POSTs (so a pickup
    // may take a record a POST still holds). Invariants: (1) a record is handed to the session or the
    // endpoint at most once, whatever happens (D-6; a live "not sent" and a pass-over do not count);
    // (2) none is lost: once every lease and pass-over has run out, each record is still queued for
    // pickup, or the endpoint answered 2xx for it, or the pickup acknowledged it.
    [Theory]
    [InlineData(StoreKind.InMemory, 1)]
    [InlineData(StoreKind.InMemory, 2)]
    [InlineData(StoreKind.InMemory, 3)]
    [InlineData(StoreKind.Reference, 4)]
    [InlineData(StoreKind.Reference, 5)]
    [InlineData(StoreKind.Reference, 6)]
    public async Task Under_any_interleaving_of_pushes_pickups_cancellations_and_lease_expiry_a_record_is_attempted_at_most_once_and_none_is_lost(StoreKind kind, int seed)
    {
        using var world = DirectPushWorld.Create(kind, o => o.MaxMessageAge = null);
        var chaos = new Chaos(seed);
        var peer = await world.GrantAsync("adv1b-chaos-" + seed);
        var seedOf = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        for (byte s = 1; s <= 40; s++)
            seedOf[await world.QueueAsync(peer, s)] = s; // counted by the live-on status: the session's to pick up
        var liveBuilt = new ConcurrentQueue<string>();
        var session = await world.GoLiveAsync(peer, liveBuilt);
        session.OnSend = (_, _) => chaos.Next(10) switch
        {
            < 5 => ReturnRouteSendResult.Sent,
            < 8 => ReturnRouteSendResult.NotSent,
            _ => ReturnRouteSendResult.TooLarge,
        };
        for (byte s = 41; s <= 80; s++)
            seedOf[await world.QueueAsync(peer, s)] = s; // after live-on: live pushes may take them

        var sender = new ChaosSender(chaos, world.Clock, world.LeaseFor);
        world.Store.AfterLease = (request, taken) =>
        {
            // Between the take and the send: the push's own deadline passes.
            if (request.Lease.ForPush && taken.Count > 0 && chaos.Next(8) == 0)
                ChaosSender.Current.Value?.Cancel();
            return Task.CompletedTask;
        };
        var coordinators = new[] { world.Coordinator, world.NewCoordinator(), world.NewCoordinator() };
        var pickupAcknowledged = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

        async Task PushLoopAsync(int index)
        {
            var coordinator = coordinators[index];
            for (var i = 0; i < 60; i++)
            {
                using var cts = new CancellationTokenSource();
                ChaosSender.Current.Value = cts;
                if (chaos.Next(20) == 0)
                    cts.Cancel(); // already past its deadline
                try
                {
                    if (index == 0)
                        await coordinator.PushNextLiveAsync(peer, cts.Token);
                    await coordinator.PushNextDirectAsync(peer, world.Directory, sender, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // The deadline: whatever was taken was given back (as unsent, or released when sent).
                }
                ChaosSender.Current.Value = null;
                await Task.Yield();
            }
        }

        async Task PickupLoopAsync()
        {
            for (var i = 0; i < 80; i++)
            {
                var now = world.Clock.GetUtcNow();
                var lease = new MessageLease(null, now + world.LeaseFor);
                var leased = await world.Store.LeaseAsync(new MessageLeaseRequest(peer, null, 3, long.MaxValue, now, lease), CancellationToken.None);
                foreach (var record in leased)
                {
                    switch (chaos.Next(5))
                    {
                        case 0:
                            if (await world.Store.AcknowledgeAsync(peer, new[] { record.Id }, CancellationToken.None) > 0)
                                pickupAcknowledged[record.Id] = true;
                            break;
                        case 1:
                            await world.Store.ReleaseAsync(peer, new[] { record.Id }, lease.Id, CancellationToken.None);
                            break;
                        case 2:
                            await world.Store.ReturnUnsentAsync(peer, new[] { record.Id }, lease.Id, CancellationToken.None);
                            break;
                        case 3:
                            await world.Store.PassOverAsync(peer, new[] { record.Id }, lease.Id, now + TimeSpan.FromSeconds(1), CancellationToken.None);
                            break;
                        default:
                            break; // the lease runs out
                    }
                }
                await Task.Yield();
            }
        }

        await Task.WhenAll(Task.Run(() => PushLoopAsync(0)), Task.Run(() => PushLoopAsync(1)), Task.Run(() => PushLoopAsync(2)), Task.Run(PickupLoopAsync)).WaitAsync(TimeSpan.FromSeconds(60));

        // Every lease and pass-over runs out; what a delivery-request would be handed now is what is left.
        world.Clock.Advance(TimeSpan.FromDays(20));
        var farNow = world.Clock.GetUtcNow();
        var left = (await world.Store.Inner.LeaseAsync(new MessageLeaseRequest(peer, null, 1000, long.MaxValue, farNow, new MessageLease(null, farNow)), CancellationToken.None))
            .Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

        // The world's live builder names each delivery "live-" + the record id.
        var handedLive = session.Attempts
            .Where(a => a.Result == ReturnRouteSendResult.Sent)
            .Select(a => a.Message.Id["live-".Length..])
            .ToList();
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in seedOf.Keys)
            attempts[id] = handedLive.Count(x => x == id) + sender.Posted.Count(s => s == seedOf[id]);

        attempts.Where(a => a.Value > 1).Should().BeEmpty("a record is handed to the session or the endpoint at most once (seed {0}; posts {1}, live {2})", seed, sender.Posted.Count, handedLive.Count);
        var lost = seedOf.Keys.Where(id => !left.Contains(id) && !sender.Accepted.Contains(seedOf[id]) && !pickupAcknowledged.ContainsKey(id)).ToList();
        lost.Should().BeEmpty("a record leaves the store only after a 2xx, an acknowledgement, or a removal the test made (seed {0})", seed);
    }

    // ---- live first (D6) across two mediator processes over one store ----------------------------------

    // Two mediator processes over one host store (the deployment the plan's own two-process test
    // covers). The recipient's live session, with live delivery on, is held by process A. Process B —
    // where the next forward happened to land, so its sink woke its own push workers — has no live
    // session for the grant in its own tracker. "Live first" (D6) is decided by the sessions of the
    // process that runs the push, so B posts both messages to the registered endpoint (ADV-110-1b-3,
    // documented as a residual, the coordinator's call). What does hold across processes: the push mark
    // keeps each message to one attempt, and nothing is lost.
    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task ADV_110_1b_3_A_second_process_over_the_same_store_decides_live_first_by_its_own_sessions_and_each_message_still_gets_one_attempt(StoreKind kind)
    {
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("adv1b-two-processes");
        var counted = await world.QueueAsync(peer, 1); // counted by the live-on status
        var pushedLive = new ConcurrentQueue<string>();
        await world.GoLiveAsync(peer, pushedLive); // on process A (world.Coordinator)
        var afterLiveOn = await world.QueueAsync(peer, 2); // the live session's on process A
        var processB = world.NewCoordinator();

        await world.PushAsync(processB, peer);
        await world.PushAsync(processB, peer);
        await world.PushAsync(processB, peer);

        world.Sender.Posts.Should().HaveCount(2, "process B has no live session of its own for the grant, so it pushes directly (the documented residual)");
        world.Sender.Bodies.Select(DirectPushWorld.SeedOf).Should().OnlyHaveUniqueItems("the push mark keeps each message to one attempt across processes");
        _ = counted;
        _ = afterLiveOn;
    }

    // ---- the dispatcher's retained state under grant churn ----------------------------------------------

    // A grant whose last direct push failed keeps its dispatcher entry (its DID) until the grant is
    // woken again or a live push's deadline starts a cooldown (QA-110-4-1 documents this). A revoked
    // grant is never woken again (a forward for it is dropped before any wake-up), and a mediator
    // whose recipients have no live sessions never has a live cooldown: so each revoked grant whose
    // last direct push failed stays in the dispatcher for the life of the process. MaxGrants does not
    // bound it (revoking frees the slot). Measured: 200 grants churned through 10 MaxGrants slots, each
    // refusing one push → 200 entries, ~390,000 retained characters (~780 KB), after their pauses
    // ended and after healthy traffic on another grant.
    [Fact]
    public async Task ADV_110_1b_1_Revoked_grants_whose_last_direct_push_failed_leave_no_entry_behind_once_their_pause_has_run_out()
    {
        const int churn = 200;
        const int maxGrants = 10;
        await using var direct = await DirectWorld.StartAsync(o => o.CoordinateMediation.MaxGrants = maxGrants);
        var fixture = direct.Fixture;
        direct.Sends.Fail(98);
        var padding = new string('x', 1900);
        for (var i = 0; i < churn; i++)
        {
            var grant = await direct.GrantAsync($"adv1b-churn-{i}-{padding}", endpoint: false);
            direct.Endpoints.Set(grant.Peer, DirectWorld.EndpointFor($"adv1b-churn-{i}"));
            await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 98));
            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await fixture.WaitForPushOutcomesAsync(i + 1, Wait);
            await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the push settled");
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
            (await fixture.Queue.RevokeAndPurgeAsync(grant.Peer)).Revoked.Should().BeTrue();
        }
        direct.Sends.Sent.Should().HaveCount(churn, "precondition: each grant refused one push");

        // Every pause has run out; healthy traffic flows on another grant.
        fixture.Time.Advance(Pause + TimeSpan.FromSeconds(1));
        var healthy = await direct.GrantAsync("adv1b-churn-healthy");
        for (byte i = 0; i < 5; i++)
        {
            await fixture.StoreAsync(healthy.Recipient, PushFixture.Payload(64, (byte)(120 + i)));
            fixture.Dispatcher.Wake(healthy.Peer).Should().BeTrue();
            await fixture.WaitForPushOutcomesAsync(churn + 1 + i, Wait);
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }

        fixture.Dispatcher.GrantEntries.Should().Be(
            0,
            "the entries of revoked grants whose pauses have run out go once a worker next finishes (held {0} entries, {1} characters)",
            fixture.Dispatcher.GrantEntries, fixture.Dispatcher.RetainedCharacters);
        fixture.Dispatcher.RetainedCharacters.Should().Be(0);
        fixture.Dispatcher.PauseStarts.Should().Be(0, "nothing is left of a pause once it has run out and been looked at again");
    }

    // ---- the directory may be asked about a DID with no grant; nothing is taken or sent ----------------

    // Turning live delivery on needs no grant, and it wakes the push workers for the peer. A peer with
    // no grant that turns live delivery on and then downgrades its session (return_route none) before
    // the wake-up is served gets a direct step: the host's directory may be asked about its DID. The
    // 1b-2 fix stopped that wake-up, and lost a message whose grant was made while the live-on ran
    // (ADV-110-2b-2); the coordinator's call under the churn rule keeps the wake-up and widens the
    // directory's contract instead. Whatever the directory answers — here an endpoint for every DID —
    // nothing is taken or sent for a DID with no grant.
    [Fact]
    public async Task ADV_110_1b_2_A_peer_without_a_grant_that_turns_live_delivery_on_gets_nothing_taken_or_sent_whatever_the_directory_answers()
    {
        await using var direct = await DirectWorld.StartAsync(o => o.PushConcurrency = 1);
        var fixture = direct.Fixture;

        // Park the only worker on a healthy grant's POST.
        var parker = await direct.GrantAsync("adv1b-parker");
        await fixture.StoreAsync(parker.Recipient, PushFixture.Payload(64, 40));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        direct.Sends.OnSend = async (_, _, ct) =>
        {
            entered.TrySetResult();
            await gate.Task.WaitAsync(ct);
            return true;
        };
        fixture.Dispatcher.Wake(parker.Peer).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);

        // A peer that never enrolled: live delivery on (no grant needed), then a downgrade. The
        // directory answers an endpoint for any DID.
        var stranger = await direct.World.Peers.NewAsync();
        direct.Endpoints.OnFind = (_, _) => ValueTask.FromResult<Uri?>(DirectWorld.EndpointFor("anyone"));
        var session = new PickupFakeSession(stranger.Did, direct.World.Mediator.Did, live: true);
        await TurnLiveOnAsync(fixture, session);
        session.Downgrade();

        gate.TrySetResult();
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "both operations settled");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        (await fixture.Mediation.GetGrantAsync(stranger.Did, CancellationToken.None)).Should().BeNull("precondition: the peer never enrolled");
        direct.Sends.Sent.Should().ContainSingle("only the parker's message is sent; nothing for a DID with no grant");
        fixture.PushOutcomeLog.Should().Equal(new[] { PushOutcomes.PushedDirect }, "the stranger's push took nothing: no outcome");
    }

    private static async Task TurnLiveOnAsync(PushFixture fixture, IReturnRouteSession session)
    {
        fixture.Services.GetRequiredService<IReturnRouteSessionRegistry>().Register(session);
        var client = fixture.Services.GetRequiredService<DidCommClient>();
        var change = MessagePickup.CreateLiveDeliveryChange(session.PeerDid, session.LocalDid, liveDelivery: true);
        var packed = await client.PackEncryptedAsync(change, new PackEncryptedOptions(Recipients: new[] { session.LocalDid }, From: session.PeerDid));
        await fixture.Services.GetRequiredService<ProtocolDispatcher>().DispatchAsync(await client.UnpackAsync(packed.Message), client, fixture.Options, Task.CompletedTask, session, CancellationToken.None);
    }

    /// <summary>A seeded source of choices, safe to share between the actors.</summary>
    private sealed class Chaos(int seed)
    {
        private readonly Random _random = new(seed);

        public int Next(int max)
        {
            lock (_random)
                return _random.Next(max);
        }
    }

    /// <summary>
    /// The endpoint, chaotic: each send is recorded (its seed) when it begins; it may move the clock
    /// past the lease timeout (the POST outlives its lease), cancel the push's own deadline and
    /// throw, fault, refuse, or accept (recorded).
    /// </summary>
    private sealed class ChaosSender(Chaos chaos, ManualTimeProvider clock, TimeSpan leaseFor) : IDirectPushSender
    {
        /// <summary>The deadline of the push the current flow runs.</summary>
        public static readonly AsyncLocal<CancellationTokenSource?> Current = new();

        public ConcurrentQueue<byte> Posted { get; } = new();

        public ConcurrentBag<byte> Accepted { get; } = new();

        public ValueTask<PushTargetCheck> CheckTargetAsync(Uri? endpoint, CancellationToken ct)
        {
            var ok = endpoint is { IsAbsoluteUri: true } && (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps);
            return ValueTask.FromResult(ok ? new PushTargetCheck(PushTarget.Accepted, string.Empty) : new PushTargetCheck(PushTarget.NotUsable, "NotHttp"));
        }

        public async Task<DirectSendResult> SendAsync(Uri endpoint, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            var seed = StandInJwe.SeedOf(payload.Span);
            Posted.Enqueue(seed);
            await Task.Yield();
            if (chaos.Next(4) == 0)
                clock.Advance(leaseFor + TimeSpan.FromSeconds(1));
            switch (chaos.Next(10))
            {
                case 0:
                    Current.Value?.Cancel();
                    ct.ThrowIfCancellationRequested();
                    return DirectSendResult.Failed;
                case 1:
                    throw new HandlerBoomException(HandlerBoomException.Marker);
                case < 3:
                    return DirectSendResult.Refused; // a 4xx: no pause, the next record goes on
                case < 5:
                    return DirectSendResult.Failed; // a 5xx: the recipient's direct pushes pause
                default:
                    Accepted.Add(seed);
                    return DirectSendResult.Delivered;
            }
        }
    }
}
