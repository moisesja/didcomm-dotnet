using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.TestSupport;
using DidComm.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// The push workers with direct push on (#110, D6 step 3): the real dispatcher and the real push
/// step in a container built by <c>AddMediator()</c>, with the host's endpoint directory and the
/// single-attempt sender scripted (<see cref="DirectEndpoints"/>, <see cref="DirectSends"/>), a manual
/// clock for the deadlines and a rendezvous for every interleaving (L-044, L-065). Covers the
/// outcome table of the plan, the delivery order (a live session wins; one that closes hands the
/// rest to direct push), Q-4 (a tarpitting endpoint), R1-1 (nothing of a forward's wire id is
/// retained) and ADV-6 D1 (more grants than <c>MaxGrants</c> on a host store).
/// </summary>
public sealed class DirectPushDispatcherTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PushTimeout = new MediatorOptions().PushTimeout;
    private static readonly TimeSpan Pause = PushDispatcher.CooldownPushTimeouts * PushTimeout;

    // ---- the outcome table ------------------------------------------------------------------------

    [Fact]
    public async Task A_due_message_of_a_grant_without_a_live_session_is_posted_unchanged_to_its_registered_endpoint_and_removed()
    {
        await using var direct = await DirectWorld.StartAsync();
        var grant = await direct.GrantAsync("delivered");
        var payload = PushFixture.Payload(96, seed: 11);
        var stored = await direct.Fixture.StoreAsync(grant.Recipient, payload);

        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        var outcomes = await direct.Fixture.WaitForPushOutcomesAsync(1, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        outcomes.Should().Equal(PushOutcomes.PushedDirect);
        var sent = direct.Sends.Sent.Should().ContainSingle().Subject;
        sent.Endpoint.Should().Be(direct.Endpoints.Of(grant.Peer));
        sent.Payload.Should().Equal(payload, "the stored message is sent as it was stored");
        direct.Endpoints.Asked.Should().OnlyContain(did => did == grant.Peer, "the directory is asked for the grant, by its peer DID");
        direct.Fixture.Store.MessageCount.Should().Be(0, "a 2xx removes the record");
        PipelineLogs.Leaking(direct.Fixture.Logs, grant.Peer, grant.Recipient, stored.MessageId, sent.Endpoint.Host).Should().BeEmpty();
    }

    [Fact]
    public async Task A_refused_message_is_posted_once_ends_the_run_and_pauses_direct_pushes_and_the_messages_behind_it_go_at_the_first_wake_up_after_the_pause()
    {
        await using var direct = await DirectWorld.StartAsync();
        var grant = await direct.GrantAsync("refused");
        direct.Sends.Fail(21);
        var refused = await direct.Fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 21));
        for (byte seed = 22; seed <= 24; seed++)
            await direct.Fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, seed));

        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Fixture.WaitForPushOutcomesAsync(1, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        direct.Sends.Seeds.Should().Equal(new byte[] { 21 }, "the refusal ends the run, with no yield");

        // Wake-ups during the pause post nothing.
        for (var wake = 0; wake < 3; wake++)
        {
            direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue("a wake-up is still served: only the direct step waits for the pause");
            await direct.Fixture.WaitForPushOutcomesAsync(2 + wake, Wait);
            await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        }
        direct.Sends.Seeds.Should().Equal(new byte[] { 21 });
        direct.Fixture.PushOutcomeLog.Skip(1).Should().Equal(Enumerable.Repeat(PushOutcomes.SkippedCooldown, 3));

        // The first wake-up after the pause: the burst behind the refused one, each posted once.
        direct.Fixture.Time.Advance(2 * PushTimeout);
        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Fixture.WaitForPushOutcomesAsync(7, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        direct.Fixture.PushOutcomeLog.Skip(4).Should().Equal(PushOutcomes.PushedDirect, PushOutcomes.PushedDirect, PushOutcomes.PushedDirect);
        direct.Sends.Seeds.Should().Equal(new byte[] { 21, 22, 23, 24 }, "the refused one is never sent again, and holds the others up no longer than the pause");
        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        direct.Sends.Seeds.Should().HaveCount(4, "each message is posted at most once");
        (await direct.World.PickupAsync(grant.Peer)).Should().Equal(new[] { refused.MessageId }, "pickup is the later delivery of a refused push (OD-2)");

        // The push step's line says why (it knows); the dispatcher's says what it paused.
        var line = direct.Fixture.Logs.Entries.Should().ContainSingle(e => e.Formatted.StartsWith("Direct push not delivered", StringComparison.Ordinal)).Subject;
        line.Category.Should().NotBe(typeof(PushDispatcher).FullName);
        line.Formatted.Should().Contain("did not accept the message");
        line.Level.Should().Be(LogLevel.Warning, "the first of its kind");
        PipelineLogs.Of<PushDispatcher>(direct.Fixture.Logs).Should().ContainSingle(e => e.Formatted.StartsWith("Direct pushes to a recipient paused for", StringComparison.Ordinal));
        PipelineLogs.Leaking(direct.Fixture.Logs, grant.Peer, grant.Recipient, refused.MessageId, direct.Endpoints.Of(grant.Peer).Host).Should().BeEmpty();
    }

    [Fact]
    public async Task A_grant_the_directory_has_no_endpoint_for_is_skipped_once_per_operation_and_nothing_is_taken()
    {
        await using var direct = await DirectWorld.StartAsync();
        var grant = await direct.GrantAsync("no-endpoint", endpoint: false);
        var stored = new[] { await direct.Fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 31)), await direct.Fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 32)) };

        for (var wake = 0; wake < 2; wake++)
        {
            direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await direct.Fixture.WaitForPushOutcomesAsync(wake + 1, Wait);
            await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        }

        direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedNoEndpoint, PushOutcomes.SkippedNoEndpoint);
        direct.Fixture.Pushes(PushOutcomes.SkippedNoLiveSession).Should().Be(0, "with direct push on, a grant without a live session is not a live-session skip");
        direct.Sends.Sent.Should().BeEmpty();
        (await direct.World.PickupAsync(grant.Peer)).Should().Equal(stored.Select(s => s.MessageId), "nothing was taken, so nothing is held under a lease");
    }

    [Fact]
    public async Task A_record_whose_recipient_was_unregistered_is_removed_unposted_and_the_next_one_is_delivered()
    {
        await using var direct = await DirectWorld.StartAsync();
        const string Peer = "did:example:direct-unregistered";
        const string Kept = "did:example:direct-unregistered-kept";
        const string Gone = "did:example:direct-unregistered-gone";
        await direct.Fixture.EnrolAsync(Peer, Kept, Gone);
        direct.Endpoints.Set(Peer, DirectWorld.EndpointFor("unregistered"));
        await direct.Fixture.StoreAsync(Gone, PushFixture.Payload(64, 41));
        await direct.Fixture.StoreAsync(Kept, PushFixture.Payload(64, 42));
        (await direct.Fixture.Mediation.UpdateRecipientsAsync(Peer, new[] { new RecipientUpdate(Gone, RecipientUpdateAction.Remove) }, CancellationToken.None))
            .Should().Equal(RecipientStoreOutcome.Removed);

        direct.Fixture.Dispatcher.Wake(Peer).Should().BeTrue();
        await direct.Fixture.WaitForPushOutcomesAsync(2, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedUnregistered, PushOutcomes.PushedDirect);
        direct.Sends.Seeds.Should().Equal(new byte[] { 42 }, "the unregistered record is never posted");
        direct.Fixture.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_record_that_expires_between_its_take_and_its_send_counts_nothing_ends_the_run_and_the_next_one_goes_at_the_next_wake_up_with_no_pause()
    {
        var store = new ScriptedMessageStore();
        await using var direct = await DirectWorld.StartAsync(services: s => s.AddSingleton<IMessageStore>(store));
        var grant = await direct.GrantAsync("expiring");
        var now = direct.Fixture.Time.GetUtcNow();
        await direct.Fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 51), new PickupDeliveryOptions(ExpiresTime: now.ToUnixTimeSeconds() + 1, AllowLive: false));
        await direct.Fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 52));
        var armed = 1;
        store.OnLease = async (request, ct) =>
        {
            var leased = await store.Inner.LeaseAsync(request, ct);
            // The first direct push's take: the clock passes the record's expiry right after it.
            if (request.Lease is { ForPush: true, Owner: null } && Interlocked.Exchange(ref armed, 0) == 1)
                direct.Fixture.Time.Advance(TimeSpan.FromSeconds(2));
            return leased;
        };

        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        direct.Fixture.PushOutcomeLog.Should().BeEmpty("an expired record is no outcome");
        direct.Sends.Seeds.Should().BeEmpty("the expired record is never sent, and it ends the run");

        // No pause (nothing was sent): the next wake-up delivers the next record.
        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Fixture.WaitForPushOutcomesAsync(1, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedDirect);
        direct.Sends.Seeds.Should().Equal(new byte[] { 52 });
    }

    [Fact]
    public async Task A_sender_that_cannot_be_built_fails_each_direct_push_and_never_stops_a_worker()
    {
        var factoryCalls = 0;
        var endpoints = new DirectEndpoints();
        var broken = new Lazy<IDirectPushSender>(() =>
        {
            Interlocked.Increment(ref factoryCalls);
            throw new InvalidOperationException("no HTTP transport " + HandlerBoomException.Marker);
        });
        await using var world = await LivePushWorld.StartAsync(
            o => o.PushConcurrency = 1,
            s => DirectPushRouting.Register(s, new DirectPushRoute(endpoints, broken)));
        var fixture = world.Fixture;
        const string Peer = "did:example:direct-broken-sender";
        const string Recipient = "did:example:direct-broken-sender-recipient";
        await fixture.EnrolAsync(Peer, Recipient);
        endpoints.Set(Peer, DirectWorld.EndpointFor("broken"));
        var stored = await fixture.StoreAsync(Recipient);

        for (var wake = 0; wake < 2; wake++)
        {
            fixture.Dispatcher.Wake(Peer).Should().BeTrue();
            await fixture.WaitForPushOutcomesAsync(wake + 1, Wait);
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
            fixture.Time.Advance(2 * PushTimeout); // past the direct pause the failure started
        }
        // The one worker still serves: a live grant is pushed live.
        var (live, session) = await world.LiveGrantAsync("broken-sender-live");
        var pushed = await world.StoreAsync(live, 1);
        fixture.Dispatcher.Wake(live.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(3, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.Failed, PushOutcomes.PushedLive);
        LivePushWorld.Deliveries(session).Select(d => d.Id).Should().Equal(pushed.MessageId);
        Volatile.Read(ref factoryCalls).Should().Be(1, "a sender that could not be built is not built again (the refusal is kept)");
        (await world.PickupAsync(Peer)).Should().Equal(new[] { stored.MessageId }, "nothing was taken");
        fixture.Logs.Entries.Should().NotContain(e => e.Everything.Contains(HandlerBoomException.Marker, StringComparison.Ordinal), "exception text never reaches a log");
        PipelineLogs.Of<PushDispatcher>(fixture.Logs).Should().Contain(e => e.Formatted.StartsWith("Direct push not delivered (InvalidOperationException)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_endpoint_check_that_throws_fails_the_push_with_nothing_taken_and_never_stops_a_worker()
    {
        await using var direct = await DirectWorld.StartAsync(o => o.PushConcurrency = 1);
        var grant = await direct.GrantAsync("check-throws");
        var stored = await direct.Fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 91));
        direct.Sends.OnCheckTarget = _ => throw new HandlerBoomException("endpoint check " + HandlerBoomException.Marker);

        for (var wake = 0; wake < 2; wake++)
        {
            direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await direct.Fixture.WaitForPushOutcomesAsync(wake + 1, Wait);
            await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
            direct.Fixture.Time.Advance(2 * PushTimeout); // past the direct pause the fault started
        }
        // The one worker still serves.
        direct.Sends.OnCheckTarget = null;
        var other = await direct.GrantAsync("check-ok");
        await direct.Fixture.StoreAsync(other.Recipient, PushFixture.Payload(64, 92));
        direct.Fixture.Dispatcher.Wake(other.Peer).Should().BeTrue();
        await direct.Fixture.WaitForPushOutcomesAsync(3, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.Failed, PushOutcomes.PushedDirect);
        direct.Sends.Seeds.Should().Equal(new byte[] { 92 });
        (await direct.World.PickupAsync(grant.Peer)).Should().Equal(new[] { stored.MessageId }, "the check comes before the take: nothing was taken");
        PipelineLogs.Of<PushDispatcher>(direct.Fixture.Logs).Where(e => e.Formatted.StartsWith("Direct push not delivered (HandlerBoomException)", StringComparison.Ordinal))
            .Should().HaveCount(2, "one line per failed push, by exception type");
        direct.Fixture.Logs.Entries.Should().NotContain(e => e.Everything.Contains(HandlerBoomException.Marker, StringComparison.Ordinal), "exception text never reaches a log");
    }

    // ---- the direct pause's bookkeeping (ADV-110-1b-1) ------------------------------------------------

    // A revoked grant is never woken again, so the entry its last, failed direct push left is dropped
    // by the next pause that starts once that pause has run out; a pause still running is never cut
    // short by that clean-up, which reads each entry's own pause, never the order of the queue.
    [Fact]
    public async Task Spent_pauses_of_revoked_grants_go_at_the_next_pause_start_and_a_running_pause_is_never_cut_short()
    {
        await using var direct = await DirectWorld.StartAsync();
        var fixture = direct.Fixture;
        direct.Sends.Fail(11);
        var revoked = new List<DirectGrant>();
        for (var i = 0; i < 3; i++)
        {
            var grant = await direct.GrantAsync($"pause-revoked-{i}");
            await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 11));
            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await fixture.WaitForPushOutcomesAsync(i + 1, Wait);
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
            (await fixture.Queue.RevokeAndPurgeAsync(grant.Peer)).Revoked.Should().BeTrue();
            revoked.Add(grant);
        }
        fixture.Dispatcher.GrantEntries.Should().Be(3, "precondition: each pause keeps its entry while it runs");
        fixture.Time.Advance(Pause);

        // K's refusal starts a pause: the spent ones go, K's is kept.
        var k = await direct.GrantAsync("pause-k");
        await fixture.StoreAsync(k.Recipient, PushFixture.Payload(64, 11));
        await fixture.StoreAsync(k.Recipient, PushFixture.Payload(64, 21));
        fixture.Dispatcher.Wake(k.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(4, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Dispatcher.GrantEntries.Should().Be(1, "the revoked grants' spent entries went at K's pause start");
        fixture.Dispatcher.RetainedCharacters.Should().Be(k.Peer.Length);
        fixture.Dispatcher.PauseStarts.Should().Be(1);

        // L's refusal starts another pause, and the clean-up runs again: K is still paused.
        var l = await direct.GrantAsync("pause-l");
        await fixture.StoreAsync(l.Recipient, PushFixture.Payload(64, 11));
        fixture.Dispatcher.Wake(l.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(5, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Dispatcher.Wake(k.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(6, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(
            PushOutcomes.Failed, PushOutcomes.Failed, PushOutcomes.Failed, PushOutcomes.Failed, PushOutcomes.Failed,
            PushOutcomes.SkippedCooldown);
        direct.Sends.Seeds.Should().NotContain(21, "K's second message waits for K's own pause to run out");
        fixture.Dispatcher.GrantEntries.Should().Be(2, "K and L are paused");
    }

    // A recipient whose push fails again right after its first pause ran out: the first pause's queue item,
    // looked at when the second pause starts, belongs to a pause that is no longer the entry's, and
    // must leave the entry (and its running pause) alone.
    [Fact]
    public async Task A_newer_pause_of_the_same_recipient_is_never_dropped_by_the_older_pauses_clean_up()
    {
        await using var direct = await DirectWorld.StartAsync();
        var fixture = direct.Fixture;
        var grant = await direct.GrantAsync("pause-again");
        direct.Sends.Fail(31);
        direct.Sends.Fail(32);
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 31));
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(1, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        // The first pause runs out; the next push fails too (a 5xx-like answer), which starts a second pause and
        // looks again at the first (spent) one.
        fixture.Time.Advance(Pause);
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 32));
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 33));
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(2, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Dispatcher.PauseStarts.Should().Be(1, "the first pause's item was looked at and dropped");
        fixture.Dispatcher.GrantEntries.Should().Be(1, "the entry, and its second pause, are kept");

        // The second pause holds.
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(3, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        direct.Sends.Seeds.Should().Equal(new byte[] { 31, 32 }, "nothing is posted during the second pause");

        // Once it runs out, the message behind it goes.
        fixture.Time.Advance(Pause);
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(4, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.Failed, PushOutcomes.SkippedCooldown, PushOutcomes.PushedDirect);
        direct.Sends.Seeds.Should().Equal(new byte[] { 31, 32, 33 });
    }

    // ---- wake-ups with direct push on -------------------------------------------------------------

    [Fact]
    public async Task With_direct_push_on_a_due_forward_wakes_its_grant_without_a_live_session_and_is_posted()
    {
        await using var direct = await DirectWorld.StartAsync();
        var grant = await direct.GrantAsync("woken");

        await direct.World.ForwardAsync(grant.Recipient, 3, "fwd-direct-woken");
        await direct.Fixture.WaitForPushOutcomesAsync(1, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        direct.Fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued);
        direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedDirect);
        direct.Sends.Seeds.Should().Equal(LivePushWorld.Seed(3));
    }

    [Fact]
    public async Task D2_a_delayed_message_is_not_posted_before_it_is_due_and_once_due_goes_with_the_grants_next_push()
    {
        await using var direct = await DirectWorld.StartAsync();
        var grant = await direct.GrantAsync("delayed");

        await direct.ForwardAsync(grant.Recipient, PushFixture.Payload(64, 61), "fwd-direct-delayed", delay: TimeSpan.FromSeconds(5));
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        direct.Fixture.Time.Advance(TimeSpan.FromSeconds(6));
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        direct.Fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued);
        direct.Fixture.PushOutcomeLog.Should().BeEmpty("a message that is not due wakes nothing, and nothing schedules its due time (D2)");
        direct.Sends.Sent.Should().BeEmpty();

        await direct.World.ForwardAsync(grant.Recipient, 4, "fwd-direct-next");
        await direct.Fixture.WaitForPushOutcomesAsync(2, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedDirect, PushOutcomes.PushedDirect);
        direct.Sends.Seeds.Should().Equal(new byte[] { 61, LivePushWorld.Seed(4) }, "the delayed one, due by now, goes first with the grant's next push");
    }

    // ---- the delivery order (D6) ------------------------------------------------------------------

    [Fact]
    public async Task D6_a_live_session_wins_nothing_is_posted_and_the_directory_is_not_asked_while_the_grant_is_live()
    {
        await using var direct = await DirectWorld.StartAsync();
        var grant = await direct.World.GrantAsync("d6-live");
        direct.Endpoints.Set(grant.Peer, DirectWorld.EndpointFor("d6-live"));
        var counted = await direct.World.StoreAsync(grant, 1); // counted by the live-on status: the recipient's to pick up
        var session = await direct.World.LiveAsync(grant);
        var pushed = new[] { await direct.World.StoreAsync(grant, 2), await direct.World.StoreAsync(grant, 3) };

        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Fixture.WaitForPushOutcomesAsync(2, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        LivePushWorld.Deliveries(session).Select(d => d.Id).Should().Equal(pushed.Select(p => p.MessageId));
        direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedLive, PushOutcomes.PushedLive);
        direct.Sends.Sent.Should().BeEmpty("live wins: a grant with a live session is never pushed direct");
        direct.Endpoints.Asked.Should().BeEmpty("the directory is asked only for a grant without a live session");
        (await direct.World.PickupAsync(grant.Peer)).Should().Equal(new[] { counted.MessageId }, "what the live-on status counted is picked up, not posted");
    }

    [Fact]
    public async Task D6_a_session_that_closes_mid_operation_hands_the_rest_to_direct_push_and_nothing_is_delivered_twice()
    {
        var hook = new DeadlineTimerHook(PushTimeout);
        await using var direct = await DirectWorld.StartAsync(clock: inner => new TimerHookTimeProvider(inner, hook.OnCreate));
        var grant = await direct.World.GrantAsync("d6-close");
        direct.Endpoints.Set(grant.Peer, DirectWorld.EndpointFor("d6-close"));
        var session = await direct.World.LiveAsync(grant);
        var stored = new List<StoredMessage>();
        for (byte i = 1; i <= 4; i++)
            stored.Add(await direct.World.StoreAsync(grant, i));
        // The third push's deadline timer is made after the second live push ended and before the
        // third push looks for a session: the session closes there, mid-operation.
        hook.Arm(3, session.Close);

        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Fixture.WaitForPushOutcomesAsync(4, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedLive, PushOutcomes.PushedLive, PushOutcomes.PushedDirect, PushOutcomes.PushedDirect);
        var live = LivePushWorld.Deliveries(session).Select(d => d.Id).ToList();
        live.Should().Equal(stored[0].MessageId, stored[1].MessageId);
        direct.Sends.Seeds.Should().Equal(new[] { LivePushWorld.Seed(3), LivePushWorld.Seed(4) }, "the rest went direct, and what was pushed live is never posted");
        for (var i = 0; i < stored.Count; i++)
        {
            var viaLive = live.Contains(stored[i].MessageId);
            var viaDirect = direct.Sends.Seeds.Contains(LivePushWorld.Seed(i + 1));
            (viaLive ^ viaDirect).Should().BeTrue("message {0} was pushed exactly one way", i + 1);
        }

        // Once the close has given the session's leases back, later wake-ups still post nothing a
        // push already took: the live ones keep their push mark.
        await MessagePickupLive.UntilAsync(async () => (await UnleasedAsync(direct.Fixture, grant.Peer)).Count == 2, "the close gave the session's leases back");
        for (var wake = 0; wake < 2; wake++)
        {
            direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        }
        direct.Sends.Sent.Should().HaveCount(2, "later wake-ups post nothing a push already took");

        // The live ones, returned unacknowledged by the close, are the recipient's to pick up; the
        // posted ones are gone.
        (await direct.World.PickupAsync(grant.Peer)).Should().BeEquivalentTo(live);
    }

    /// <summary>The records of <paramref name="grantPeerDid"/> no lease holds now, read under a lease that holds nothing.</summary>
    private static async Task<IReadOnlyList<string>> UnleasedAsync(PushFixture fixture, string grantPeerDid)
    {
        var now = fixture.Time.GetUtcNow();
        var probe = await fixture.Store.LeaseAsync(new MessageLeaseRequest(grantPeerDid, null, 32, long.MaxValue, now, new MessageLease("unleased-probe", now)), CancellationToken.None);
        return probe.Select(r => r.Id).ToList();
    }

    [Fact]
    public async Task D6_a_session_that_closes_mid_operation_for_a_grant_without_an_endpoint_counts_no_skip_for_that_operation()
    {
        var hook = new DeadlineTimerHook(PushTimeout);
        await using var direct = await DirectWorld.StartAsync(clock: inner => new TimerHookTimeProvider(inner, hook.OnCreate));
        var grant = await direct.World.GrantAsync("d6-close-no-endpoint");
        var session = await direct.World.LiveAsync(grant);
        await direct.World.StoreAsync(grant, 1);
        await direct.World.StoreAsync(grant, 2);
        hook.Arm(2, session.Close);

        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Fixture.WaitForPushOutcomesAsync(1, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        direct.Fixture.PushOutcomeLog.Should().Equal(new[] { PushOutcomes.PushedLive }, "no endpoint is counted only on an operation's first push");
        direct.Endpoints.Asked.Should().NotBeEmpty("the push went on to the directory once the session had closed");
        direct.Sends.Sent.Should().BeEmpty();
    }

    // ---- Q-4: one tarpitting endpoint -------------------------------------------------------------

    // A POST honours its deadline's token; a host collaborator may not. With either, the worker is
    // freed at the deadline, and the grant gets no second POST until the first one has ended (D-3).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Q4_One_tarpitting_endpoint_holds_one_worker_and_every_other_grant_is_pushed_in_turn(bool honoursItsDeadline)
    {
        await using var direct = await DirectWorld.StartAsync(o => o.PushConcurrency = 2);
        var fixture = direct.Fixture;
        var tarpit = await direct.GrantAsync("q4-tarpit");
        var healthy = new List<DirectGrant>();
        for (var i = 0; i < 4; i++)
            healthy.Add(await direct.GrantAsync($"q4-healthy-{i}"));
        var gate = new PushGate();
        direct.Sends.OnSend = async (endpoint, _, ct) =>
        {
            if (endpoint != direct.Endpoints.Of(tarpit.Peer))
                return true;
            // Never answers until the test lets it: then it refuses.
            await gate.PassAsync(honoursItsDeadline ? ct : CancellationToken.None);
            return false;
        };
        var trapped = await fixture.StoreAsync(tarpit.Recipient, PushFixture.Payload(64, 70));
        var waiting = await fixture.StoreAsync(tarpit.Recipient, PushFixture.Payload(64, 71));
        byte seed = 80;
        foreach (var grant in healthy)
        {
            await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, seed++));
            await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, seed++));
        }

        var mark = fixture.Time.Mark();
        fixture.Dispatcher.Wake(tarpit.Peer).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait); // one worker is held by the tarpit
        await fixture.Time.WaitForTimerAsync(PushTimeout, mark, Wait);
        foreach (var grant in healthy)
            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(8, Wait);

        fixture.PushOutcomeLog.Should().HaveCount(8).And.OnlyContain(o => o == PushOutcomes.PushedDirect, "every other grant is served by the other worker while the tarpit holds one");
        gate.Arrived.Should().Be(1, "the tarpitted grant has one POST in flight and no second one");

        // The deadline: the worker stops waiting, the push counts as failed and its recipient's direct pushes pause.
        // Every other operation has ended first (each one's last, empty push makes a deadline timer
        // of its own), so the only deadline scheduled is the held push's (L-065 rule 3).
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 1 && fixture.Time.ScheduledTimersSince(mark) == 1, "only the held push is still running");
        fixture.Time.Advance(PushTimeout).Should().Be(1, "only the held push's deadline is due");
        await fixture.WaitForPushOutcomesAsync(9, Wait);
        fixture.PushOutcomeLog[8].Should().Be(PushOutcomes.Failed);
        fixture.Dispatcher.Wake(tarpit.Peer).Should().BeTrue("a wake-up is still served: only its direct step waits for the pause");
        if (!honoursItsDeadline)
        {
            fixture.Dispatcher.Unsettled.Should().Be(1, "the abandoned POST still runs, and holds its grant (D-3)");
            gate.Open();
        }
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the abandoned POST ended");
        await fixture.WaitForPushOutcomesAsync(10, Wait); // the held wake-up, served once the grant settled
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog[9].Should().Be(PushOutcomes.SkippedCooldown, "during the pause the direct step is skipped");

        direct.Sends.MaxInFlightPerEndpoint.Should().Be(1, "at most one POST in flight per grant (D-3)");
        direct.Sends.Seeds.Where(s => s is 70 or 71).Should().Equal(new byte[] { 70 }, "the tarpitted grant's second message was never posted");
        (await direct.World.PickupAsync(tarpit.Peer)).Should().BeEquivalentTo(new[] { trapped.MessageId, waiting.MessageId }, "both wait for pickup; the timed-out one's attempt is spent");
        fixture.Store.MessageCount.Should().Be(2, "every healthy message was delivered and removed");
    }

    // ---- R1-1: nothing of a forward's wire id is retained -------------------------------------------

    [Fact]
    public async Task R1_1_Forwards_waiting_for_a_held_direct_push_leave_nothing_of_their_wire_ids_behind()
    {
        await using var direct = await DirectWorld.StartAsync(o => o.PushConcurrency = 1);
        var fixture = direct.Fixture;
        var grants = new List<DirectGrant>();
        for (var i = 0; i < 8; i++)
            grants.Add(await direct.GrantAsync($"r1-{i}"));
        var gate = new PushGate();
        direct.Sends.OnSend = async (endpoint, _, ct) =>
        {
            if (endpoint == direct.Endpoints.Of(grants[0].Peer))
                await gate.PassAsync(ct);
            return true;
        };
        await fixture.StoreAsync(grants[0].Recipient, PushFixture.Payload(64, 1));
        fixture.Dispatcher.Wake(grants[0].Peer).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait); // the only worker is held

        var ids = new LetterIds(new string('r', 500));
        var forwardIds = new List<string>();
        var index = 0;
        foreach (var grant in grants)
        {
            for (var n = 0; n < 8; n++)
            {
                var id = ids.NewId();
                id.Length.Should().Be(512);
                forwardIds.Add(id);
                await direct.World.ForwardAsync(grant.Recipient, (byte)(10 + index++), id);
            }
        }
        fixture.ForwardOutcomeLog.Should().HaveCount(64).And.OnlyContain(o => o == ForwardOutcomes.Queued);

        fixture.Dispatcher.RetainedCharacters.Should().Be(grants.Sum(g => (long)g.Peer.Length), "a wake-up holds its grant's DID and nothing of the message");
        fixture.Dispatcher.GrantEntries.Should().Be(grants.Count);

        gate.Open();
        await fixture.WaitForPushOutcomesAsync(65, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().OnlyContain(o => o == PushOutcomes.PushedDirect);
        fixture.Dispatcher.RetainedCharacters.Should().Be(0, "settled grants leave no entry");
        PipelineLogs.Leaking(fixture.Logs, forwardIds.ToArray()).Should().BeEmpty("a long forward id is logged only shortened");
    }

    // ---- ADV-6 D1: more grants than MaxGrants on a host store -----------------------------------

    [Fact]
    public async Task ADV6_D1_More_direct_push_grants_than_MaxGrants_on_a_host_store_post_each_record_at_most_once()
    {
        // The mediator's own grant bound is 2; the host's store holds more grants than that. Progress
        // lives on each record (D-6), so nothing the dispatcher holds is bounded by MaxGrants.
        await using var direct = await DirectWorld.StartAsync(
            o => o.CoordinateMediation.MaxGrants = 2,
            s => s.AddSingleton<IMediationStore>(new ScriptedMediationStore()));
        var fixture = direct.Fixture;
        var grants = new List<DirectGrant>();
        var refused = new List<string>();
        for (var g = 0; g < 6; g++)
        {
            var grant = await direct.GrantAsync($"adv6-d1-{g}");
            grants.Add(grant);
            direct.Sends.Fail((byte)(200 + g));
            refused.Add((await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, (byte)(200 + g)))).MessageId); // the oldest: refused
            await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, (byte)(20 + 2 * g)));
            await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, (byte)(21 + 2 * g)));
        }

        // Round 0: each grant's refused record ends its run and pauses its direct pushes; round 1,
        // after the pause: the good ones; round 2: nothing is left to push.
        for (var round = 0; round < 3; round++)
        {
            foreach (var grant in grants)
                fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            if (round < 2)
                await fixture.WaitForPushOutcomesAsync(round == 0 ? 6 : 18, Wait);
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
            if (round == 0)
                fixture.Pushes(PushOutcomes.Failed).Should().Be(6, "each grant's refused record, posted once");
            if (round == 1)
                fixture.Pushes(PushOutcomes.PushedDirect).Should().Be(12, "every good message is delivered at the first wake-up after its grant's pause");
            fixture.Time.Advance(2 * PushTimeout);
        }

        direct.Sends.Seeds.GroupBy(s => s).Should().OnlyContain(g => g.Count() == 1, "no record is posted twice, whatever the number of grants");
        direct.Sends.Seeds.Should().HaveCount(18);
        fixture.Pushes(PushOutcomes.Failed).Should().Be(6);
        fixture.Store.MessageCount.Should().Be(6, "only the refused records remain");
        var picked = new List<string>();
        foreach (var grant in grants)
            picked.AddRange(await direct.World.PickupAsync(grant.Peer));
        picked.Should().BeEquivalentTo(refused, "pickup gets exactly the refused records");
    }
}

// ---- support ---------------------------------------------------------------------------------------

/// <summary>A grant of a <see cref="DirectWorld"/>: its peer DID (what a push wakes), the recipient DID registered under it, and its endpoint.</summary>
internal sealed record DirectGrant(string Peer, string Recipient);

/// <summary>
/// A <see cref="LivePushWorld"/> whose push workers have a direct-push route: a scripted directory and
/// sender (the dispatcher is registered before <c>AddMediator()</c>, which then adds none of its own).
/// </summary>
internal sealed class DirectWorld : IAsyncDisposable
{
    private DirectWorld(LivePushWorld world, DirectEndpoints endpoints, DirectSends sends)
    {
        World = world;
        Endpoints = endpoints;
        Sends = sends;
    }

    public LivePushWorld World { get; }
    public PushFixture Fixture => World.Fixture;
    public DirectEndpoints Endpoints { get; }
    public DirectSends Sends { get; }

    /// <param name="configure">More mediator settings.</param>
    /// <param name="services">More registrations, made before <c>AddDidComm</c>.</param>
    /// <param name="clock">Wraps the clock the dispatcher alone runs on (the deadline timers).</param>
    public static async Task<DirectWorld> StartAsync(Action<MediatorOptions>? configure = null, Action<IServiceCollection>? services = null, Func<TimeProvider, TimeProvider>? clock = null)
    {
        var endpoints = new DirectEndpoints();
        var sends = new DirectSends();
        var world = await LivePushWorld.StartAsync(configure, s =>
        {
            services?.Invoke(s);
            DirectPushRouting.Register(s, new DirectPushRoute(endpoints, new Lazy<IDirectPushSender>(() => sends)), clock);
        });
        return new DirectWorld(world, endpoints, sends);
    }

    public static Uri EndpointFor(string label) => new($"https://{label}.recipient.example/inbox");

    /// <summary>What the forward pump does with a relayed forward, with an optional <c>delay_milli</c> hold resolved on the facade clock.</summary>
    public Task ForwardAsync(string recipient, byte[] payload, string forwardId, TimeSpan? delay = null)
        => Fixture.Sink.AcceptAsync(
            new ForwardProcessingResult(recipient, payload, null, delay) { NotBefore = delay is { } hold ? Fixture.Time.GetUtcNow() + hold : null },
            ForwardContext(World.Mediator.Did, Fixture.Options, forwardId),
            CancellationToken.None);

    /// <summary>The context the forward handler hands its sink: an anoncrypted forward to <paramref name="mediatorDid"/> with id <paramref name="forwardId"/>.</summary>
    public static ProtocolContext ForwardContext(string mediatorDid, DidCommOptions options, string forwardId)
        => new(
            new UnpackResult(
                new Message { Id = forwardId, Type = ForwardConstants.ForwardTypeUri },
                new[] { EnvelopeKind.Encrypted, EnvelopeKind.Plaintext },
                Encrypted: true,
                Authenticated: false,
                NonRepudiation: false,
                AnonymousSender: true,
                ContentEncryption: null,
                KeyWrap: null,
                SignatureAlgorithm: null,
                SignerKid: null,
                SenderKid: null,
                RecipientKid: mediatorDid + "#key-1",
                AllRecipientKids: new[] { mediatorDid + "#key-1" },
                FromPrior: null),
            new DidComm.Threading.ThreadState(forwardId),
            Client: null,
            options,
            new InMemoryThreadStateStore());

    /// <summary>A grant with a did:example peer (no live session can be made for it) and, unless told otherwise, an endpoint.</summary>
    public async Task<DirectGrant> GrantAsync(string label, bool endpoint = true)
    {
        var grant = new DirectGrant($"did:example:direct-{label}", $"did:example:direct-{label}-recipient");
        await Fixture.EnrolAsync(grant.Peer, grant.Recipient);
        if (endpoint)
            Endpoints.Set(grant.Peer, EndpointFor(label));
        return grant;
    }

    public ValueTask DisposeAsync() => World.DisposeAsync();
}

/// <summary>Registers a push dispatcher with a direct-push route ahead of <c>AddMediator()</c>'s own.</summary>
internal static class DirectPushRouting
{
    public static void Register(IServiceCollection services, DirectPushRoute route, Func<TimeProvider, TimeProvider>? clock = null)
        => services.AddSingleton(sp => new PushDispatcher(
            sp.GetRequiredService<IOptions<MediatorOptions>>(),
            sp.GetRequiredService<MessagePickupQueue>(),
            sp.GetRequiredService<MediatorMetrics>(),
            sp.GetService<ILogger<PushDispatcher>>(),
            clock is null ? sp.GetRequiredService<TimeProvider>() : clock(sp.GetRequiredService<TimeProvider>()),
            shutdownGrace: null,
            route));
}

/// <summary>The host's endpoint directory, scripted: one endpoint per grant; records every grant it is asked for.</summary>
internal sealed class DirectEndpoints : IRecipientEndpointDirectory
{
    private readonly ConcurrentDictionary<string, Uri> _endpoints = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> Asked { get; } = new();

    /// <summary>Replaces the lookup (to park or throw); the default reads the map.</summary>
    public Func<string, CancellationToken, ValueTask<Uri?>>? OnFind { get; set; }

    public void Set(string grantPeerDid, Uri endpoint) => _endpoints[grantPeerDid] = endpoint;

    public Uri Of(string grantPeerDid) => _endpoints[grantPeerDid];

    public ValueTask<Uri?> FindAsync(string grantPeerDid, CancellationToken ct)
    {
        Asked.Enqueue(grantPeerDid);
        return OnFind is { } hook ? hook(grantPeerDid, ct) : ValueTask.FromResult(_endpoints.TryGetValue(grantPeerDid, out var endpoint) ? endpoint : null);
    }
}

/// <summary>
/// The single-attempt sender, scripted: every absolute http(s) endpoint may be pushed to; each send
/// is recorded (endpoint, payload) when it starts, with the sends in flight per endpoint; it answers
/// "delivered" unless the payload's seed was refused or <see cref="OnSend"/> says otherwise.
/// </summary>
internal sealed class DirectSends : IDirectPushSender
{
    // seed → the answer for it: Refused (a 4xx) or Failed (a 5xx and the like).
    private readonly ConcurrentDictionary<byte, DirectSendResult> _answers = new();
    private readonly ConcurrentDictionary<Uri, int> _inFlight = new();
    private int _maxInFlight;

    public ConcurrentQueue<(Uri Endpoint, byte[] Payload)> Sent { get; } = new();

    /// <summary>The seeds of the payloads sent, in order (<see cref="StandInJwe.SeedOf"/>).</summary>
    public IReadOnlyList<byte> Seeds => Sent.Select(s => StandInJwe.SeedOf(s.Payload)).ToList();

    /// <summary>The most sends ever in flight to one endpoint at once.</summary>
    public int MaxInFlightPerEndpoint => Volatile.Read(ref _maxInFlight);

    /// <summary>Replaces the answer (to park, fault or fail): <c>true</c> delivered, <c>false</c> failed (as a 5xx: the recipient's direct pushes pause). The default delivers unless the seed was refused or failed.</summary>
    public Func<Uri, ReadOnlyMemory<byte>, CancellationToken, Task<bool>>? OnSend { get; set; }

    /// <summary>Replaces the answer with any <see cref="DirectSendResult"/> (a 4xx refusal included); takes precedence over <see cref="OnSend"/>.</summary>
    public Func<Uri, ReadOnlyMemory<byte>, CancellationToken, Task<DirectSendResult>>? OnAnswer { get; set; }

    /// <summary>Replaces the endpoint check (to throw); the default accepts every absolute http(s) endpoint.</summary>
    public Func<Uri?, bool>? OnCheckTarget { get; set; }

    /// <summary>The endpoint refuses the message with this seed with a 4xx (other than 408 and 429): its definite "not this message", no pause.</summary>
    public void Refuse(byte seed) => _answers[seed] = DirectSendResult.Refused;

    /// <summary>The endpoint fails the message with this seed (a 5xx, 408, 429, a redirect not followed): the recipient's direct pushes pause.</summary>
    public void Fail(byte seed) => _answers[seed] = DirectSendResult.Failed;

    public ValueTask<PushTargetCheck> CheckTargetAsync(Uri? endpoint, CancellationToken ct)
    {
        if (OnCheckTarget is { } hook && !hook(endpoint))
            return ValueTask.FromResult(new PushTargetCheck(PushTarget.NotUsable, "Refused"));
        var ok = endpoint is { IsAbsoluteUri: true } && (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps);
        return ValueTask.FromResult(ok ? new PushTargetCheck(PushTarget.Accepted, string.Empty) : new PushTargetCheck(PushTarget.NotUsable, "NotHttp"));
    }

    public async Task<DirectSendResult> SendAsync(Uri endpoint, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        Sent.Enqueue((endpoint, payload.ToArray()));
        var now = _inFlight.AddOrUpdate(endpoint, 1, (_, n) => n + 1);
        int seen;
        while ((seen = Volatile.Read(ref _maxInFlight)) < now && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
        {
        }
        try
        {
            if (OnAnswer is { } answer)
                return await answer(endpoint, payload, ct);
            if (OnSend is { } hook)
                return await hook(endpoint, payload, ct) ? DirectSendResult.Delivered : DirectSendResult.Failed;
            return _answers.TryGetValue(StandInJwe.SeedOf(payload.Span), out var scripted) ? scripted : DirectSendResult.Delivered;
        }
        finally
        {
            _inFlight.AddOrUpdate(endpoint, 0, (_, n) => n - 1);
        }
    }
}

/// <summary>Runs an action when the n-th push deadline timer (of the given length) from the moment it is armed is made.</summary>
internal sealed class DeadlineTimerHook(TimeSpan deadline)
{
    private int _count;
    private int _at = -1;
    private Action? _action;

    public void Arm(int at, Action action)
    {
        _action = action;
        Volatile.Write(ref _count, 0);
        Volatile.Write(ref _at, at);
    }

    public void OnCreate(TimeSpan dueTime)
    {
        if (dueTime != deadline || Volatile.Read(ref _at) < 0)
            return;
        if (Interlocked.Increment(ref _count) == Volatile.Read(ref _at))
            _action?.Invoke();
    }
}

/// <summary>A clock that runs a hook whenever a timer is made, then makes it on the inner clock.</summary>
internal sealed class TimerHookTimeProvider(TimeProvider inner, Action<TimeSpan> onCreate) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
    public override long GetTimestamp() => inner.GetTimestamp();
    public override long TimestampFrequency => inner.TimestampFrequency;
    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        onCreate(dueTime);
        return inner.CreateTimer(callback, state, dueTime, period);
    }
}
