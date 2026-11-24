using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Mediator.Tests.Pickup;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// QA round 4 (#110, direct push), on the churn simplification: a direct push that does not deliver
/// ends its run and pauses that recipient's direct pushes for 2 × <c>PushTimeout</c> on the
/// monotonic clock. These tests put that pause next to the existing shared cooldown, an abandoned
/// push (D-3), live sessions that come and go, other grants' entries, many grants at once, a store
/// outside its contract, a stepped wall clock and the edge settings. Seam-driven: the scripted
/// directory and sender of <see cref="DirectWorld"/>, the manual clock, rendezvous gates.
/// </summary>
public sealed class DirectPushPauseQaTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PushTimeout = new MediatorOptions().PushTimeout;
    private static readonly TimeSpan Pause = PushDispatcher.CooldownPushTimeouts * PushTimeout;

    // ---- the pause and the shared cooldown ---------------------------------------------------------

    // A direct pause runs; the recipient goes live and its live push reaches the deadline: that is the
    // shared cooldown (wake-ups skipped), and it runs past the end of the direct pause, so nothing is
    // posted until the cooldown ends too ("vice versa": no direct push can start inside a cooldown).
    // Then direct pushes go on, and neither the refused record nor the live-taken one is ever posted.
    [Fact]
    public async Task A_live_deadline_during_a_direct_pause_pauses_the_whole_grant_past_the_direct_pause_and_direct_pushes_resume_after_both()
    {
        await using var direct = await DirectWorld.StartAsync();
        var fixture = direct.Fixture;
        var grant = await direct.World.GrantAsync("qa4-live-deadline");
        direct.Endpoints.Set(grant.Peer, DirectWorld.EndpointFor("qa4-live-deadline"));
        direct.Sends.Fail(LivePushWorld.Seed(1));
        await direct.World.StoreAsync(grant, 1);

        // t0: the refusal pauses the direct pushes until t0 + Pause.
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(1, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        // t0: live; the live push of a new record stalls until its deadline (t0 + PushTimeout).
        var session = await direct.World.LiveAsync(grant);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnPush = async (_, ct) =>
        {
            arrived.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return ReturnRouteSendResult.Sent;
        };
        await direct.World.StoreAsync(grant, 2);
        var mark = fixture.Time.Mark();
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue("a direct pause does not skip a wake-up");
        await arrived.Task.WaitAsync(Wait);
        await fixture.Time.WaitForTimerAsync(PushTimeout, mark, Wait);
        fixture.Time.Advance(PushTimeout);
        await fixture.WaitForPushOutcomesAsync(2, Wait);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the stalled live push settled");
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.Failed);
        session.Close();
        await direct.World.StoreAsync(grant, 3);

        // t0 + Pause + 1 s: the direct pause is over, the cooldown (from t0 + PushTimeout) is not.
        fixture.Time.Advance(Pause - PushTimeout + TimeSpan.FromSeconds(1));
        fixture.Dispatcher.Wake(grant.Peer).Should().BeFalse("the live deadline paused the whole grant, past the direct pause");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        direct.Sends.Seeds.Should().Equal(new[] { LivePushWorld.Seed(1) }, "nothing is posted inside the cooldown");

        // The cooldown ends: the next wake-up goes direct (the session is gone) and posts the new record only.
        fixture.Time.Advance(PushTimeout - TimeSpan.FromSeconds(1));
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(4, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.Failed, PushOutcomes.SkippedCooldown, PushOutcomes.PushedDirect);
        direct.Sends.Seeds.Should().Equal(new[] { LivePushWorld.Seed(1), LivePushWorld.Seed(3) }, "the refused one and the live-taken one are never posted again");
    }

    // D-3 at the pause's edge: the direct push abandoned at its deadline ignores its token and runs on
    // past the end of the pause it started. A wake-up then starts nothing (the grant's operation has
    // not settled): no second POST in flight. When the abandoned POST ends, the held wake-up is served
    // at once (the pause is over) and posts the next record.
    [Fact]
    public async Task A_direct_pause_that_runs_out_while_its_abandoned_push_still_runs_starts_no_second_push_until_that_one_ends()
    {
        await using var direct = await DirectWorld.StartAsync();
        var fixture = direct.Fixture;
        var grant = await direct.GrantAsync("qa4-abandoned");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        direct.Sends.OnSend = (_, payload, _) =>
        {
            if (StandInJwe.SeedOf(payload.Span) != 41)
                return Task.FromResult(true);
            held.TrySetResult();
            return release.Task; // ignores its token
        };
        var first = await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 41));
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 42));
        var mark = fixture.Time.Mark();

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await held.Task.WaitAsync(Wait);
        await fixture.Time.WaitForTimerAsync(PushTimeout, mark, Wait);
        fixture.Time.Advance(PushTimeout);
        await fixture.WaitForPushOutcomesAsync(1, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Dispatcher.Unsettled.Should().Be(1, "the abandoned POST still runs");

        // The pause runs out; the POST has not ended.
        fixture.Time.Advance(Pause);
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue("held for after the operation");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        direct.Sends.Seeds.Should().Equal(new byte[] { 41 }, "one operation per grant until it settles (D-3), pause or no pause");
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);

        // The abandoned POST ends (refused): the held wake-up posts the next record.
        release.TrySetResult(false);
        await fixture.WaitForPushOutcomesAsync(2, Wait);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "settled");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.PushedDirect);
        direct.Sends.Seeds.Should().Equal(new byte[] { 41, 42 });
        direct.Sends.MaxInFlightPerEndpoint.Should().Be(1);
        (await direct.World.PickupAsync(grant.Peer)).Should().Equal(new[] { first.MessageId }, "the abandoned one kept its mark: pickup only");
    }

    // ---- live sessions that come and go during a pause --------------------------------------------

    // Three times during one pause the recipient goes live, gets a push, and its session closes in the
    // middle of the operation (before the second push looks for it). Each time the rest of that
    // operation meets the pause: nothing is posted. After the pause, the records no push took are
    // posted once each; the live-pushed ones and the refused one never.
    [Fact]
    public async Task A_session_that_goes_live_and_closes_mid_operation_again_and_again_during_a_pause_never_lets_a_direct_push_through_before_it_ends()
    {
        var hook = new DeadlineTimerHook(PushTimeout);
        await using var direct = await DirectWorld.StartAsync(clock: inner => new TimerHookTimeProvider(inner, hook.OnCreate));
        var fixture = direct.Fixture;
        var grant = await direct.World.GrantAsync("qa4-churn");
        direct.Endpoints.Set(grant.Peer, DirectWorld.EndpointFor("qa4-churn"));
        direct.Sends.Fail(LivePushWorld.Seed(0));
        var refused = await direct.World.StoreAsync(grant, 0);
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(1, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        var livePushed = new List<string>();
        var leftOver = new List<byte>();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var session = await direct.World.LiveAsync(grant);
            var a = (byte)(10 + 2 * cycle);
            var b = (byte)(11 + 2 * cycle);
            var stored = await direct.World.StoreAsync(grant, a);
            await direct.World.StoreAsync(grant, b);
            hook.Arm(2, session.Close); // the second push of the operation finds the session closed
            var before = fixture.PushOutcomeLog.Count;

            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await fixture.WaitForPushOutcomesAsync(before + 2, Wait);
            await fixture.Dispatcher.WaitForIdleAsync(Wait);

            fixture.PushOutcomeLog.Skip(before).Should().Equal(new[] { PushOutcomes.PushedLive, PushOutcomes.SkippedCooldown }, "cycle {0}: live, then the pause for the rest", cycle);
            LivePushWorld.Deliveries(session).Select(d => d.Id).Should().Equal(stored.MessageId);
            livePushed.Add(stored.MessageId);
            leftOver.Add(LivePushWorld.Seed(b));
        }
        direct.Sends.Seeds.Should().Equal(new[] { LivePushWorld.Seed(0) }, "nothing is posted during the pause, however often the session comes and goes");

        fixture.Time.Advance(Pause);
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(1 + 6 + 3, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Skip(7).Should().Equal(Enumerable.Repeat(PushOutcomes.PushedDirect, 3));
        direct.Sends.Seeds.Should().Equal(new[] { LivePushWorld.Seed(0) }.Concat(leftOver), "only what no push took, each once");
        await MessagePickupLive.UntilAsync(async () => (await UnleasedAsync(fixture, grant.Peer)).Count == 4, "the closes gave the live leases back");
        (await direct.World.PickupAsync(grant.Peer)).Should().BeEquivalentTo(livePushed.Append(refused.MessageId));
    }

    // ---- other grants' entries ---------------------------------------------------------------------

    // A pause that starts walks no entries (QA4-3: amortized O(1) under the lock): it looks again only
    // at the pauses that have run out, oldest first (ADV-110-1b-1). E's pause ran out: A's pause start
    // drops E's entry. A's runs (kept: still paused); B is busy in a held POST (kept: no second
    // operation); C waits for a worker (kept: served).
    [Fact]
    public async Task A_pause_that_starts_walks_no_entries_a_spent_one_goes_at_the_next_pause_start_and_paused_busy_and_waiting_ones_are_kept()
    {
        await using var direct = await DirectWorld.StartAsync(o => o.PushConcurrency = 2);
        var fixture = direct.Fixture;
        var e = await direct.GrantAsync("qa4-prune-e");
        var a = await direct.GrantAsync("qa4-prune-a");
        var b = await direct.GrantAsync("qa4-prune-b");
        var c = await direct.GrantAsync("qa4-prune-c");
        var d = await direct.GrantAsync("qa4-prune-d");
        var gateB = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateD = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldD = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        direct.Sends.OnSend = (_, payload, _) => StandInJwe.SeedOf(payload.Span) switch
        {
            51 or 52 => Task.FromResult(false), // E's and A's heads: refused
            53 => Hold(heldB, gateB),
            54 => Hold(heldD, gateD),
            _ => Task.FromResult(true),
        };
        await fixture.StoreAsync(e.Recipient, PushFixture.Payload(64, 51));
        await fixture.StoreAsync(a.Recipient, PushFixture.Payload(64, 52));
        await fixture.StoreAsync(a.Recipient, PushFixture.Payload(64, 62));
        await fixture.StoreAsync(b.Recipient, PushFixture.Payload(64, 53));
        await fixture.StoreAsync(b.Recipient, PushFixture.Payload(64, 63));
        await fixture.StoreAsync(c.Recipient, PushFixture.Payload(64, 55));
        await fixture.StoreAsync(d.Recipient, PushFixture.Payload(64, 54));

        // E is paused, and its pause runs out; its entry stays until something looks at it again.
        fixture.Dispatcher.Wake(e.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(1, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Time.Advance(Pause);
        // A is paused now.
        fixture.Dispatcher.Wake(a.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(2, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Dispatcher.GrantEntries.Should().Be(1, "A's pause start dropped E's spent entry; A's own is kept");

        // B and D hold both workers; C waits in line.
        fixture.Dispatcher.Wake(b.Peer).Should().BeTrue();
        await heldB.Task.WaitAsync(Wait);
        fixture.Dispatcher.Wake(d.Peer).Should().BeTrue();
        await heldD.Task.WaitAsync(Wait);
        fixture.Dispatcher.Wake(c.Peer).Should().BeTrue();
        fixture.Dispatcher.Wake(b.Peer).Should().BeTrue("held for after B's operation");
        fixture.Dispatcher.GrantEntries.Should().Be(4, "A paused, B and D busy, C in line");

        // D is refused: its pause starts and touches none of the others.
        gateD.TrySetResult(false);
        await fixture.WaitForPushOutcomesAsync(4, Wait); // D failed, C delivered
        fixture.PushOutcomeLog.Skip(2).Should().BeEquivalentTo(new[] { PushOutcomes.Failed, PushOutcomes.PushedDirect });
        direct.Sends.Seeds.Count(s => s == 53).Should().Be(1, "B's held wake-up started no second operation");

        // A is still paused: its wake-up posts nothing.
        fixture.Dispatcher.Wake(a.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(5, Wait);
        fixture.PushOutcomeLog[4].Should().Be(PushOutcomes.SkippedCooldown);

        // B's POST ends: its held wake-up posts its next record, once.
        gateB.TrySetResult(true);
        await fixture.WaitForPushOutcomesAsync(7, Wait);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "settled");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        direct.Sends.Seeds.Should().BeEquivalentTo(new byte[] { 51, 52, 53, 54, 55, 63 });
        direct.Sends.Seeds.Should().NotContain(62, "A's second record waits for A's pause");
        direct.Sends.MaxInFlightPerEndpoint.Should().Be(1);
        fixture.Dispatcher.GrantEntries.Should().Be(2, "A and D are paused; B and C were dropped when their operations ended");

        // E's next wake-up finds nothing to push and leaves no entry.
        fixture.Dispatcher.Wake(e.Peer).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Dispatcher.GrantEntries.Should().Be(2, "A and D are paused");
    }

    // ---- bounded state with many grants ------------------------------------------------------------

    private const int ManyGrants = 2_000;

    // Two thousand recipients each refuse one push: each holds one entry (its DID, nothing of a
    // message) while its pause runs, each was posted once; once their pauses have run out, the next
    // pause that starts drops every spent entry (ADV-110-1b-1), each looked at once.
    [Fact]
    public async Task Many_recipients_that_each_refuse_once_hold_one_DID_each_while_paused_and_the_next_pause_start_drops_every_spent_entry()
    {
        await using var direct = await DirectWorld.StartAsync(o => o.CoordinateMediation.MaxGrants = 2 * ManyGrants);
        var fixture = direct.Fixture;
        var grants = await ManyRefusingGrantsAsync(direct);

        fixture.Dispatcher.GrantEntries.Should().Be(ManyGrants, "one entry per paused recipient");
        fixture.Dispatcher.RetainedCharacters.Should().Be(grants.Sum(g => (long)g.Peer.Length), "its DID only");
        direct.Sends.Sent.Should().HaveCount(ManyGrants, "one POST each");

        // The pauses run out. Another recipient's refusal pauses it, and drops every spent entry.
        fixture.Time.Advance(Pause);
        var late = await direct.GrantAsync("qa4-many-late");
        await fixture.StoreAsync(late.Recipient, PushFixture.Payload(64, 98)); // refused too
        fixture.Dispatcher.Wake(late.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(ManyGrants + 1, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Dispatcher.GrantEntries.Should().Be(1, "the spent entries went at that pause start; only its own is kept");
        fixture.Dispatcher.PauseStarts.Should().Be(1);

        // And a wake-up of each spent one (nothing left to push) leaves nothing behind but that one.
        foreach (var grant in grants)
            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Dispatcher.GrantEntries.Should().Be(1);
        fixture.Dispatcher.RetainedCharacters.Should().Be(late.Peer.Length);
        direct.Sends.Sent.Should().HaveCount(ManyGrants + 1, "no record was posted twice");
    }

    // QA-110-4-1 (P3). The docs said an entry is "dropped when it is none of these"; an entry whose
    // pause ran out is dropped only when its grant is next looked at. The docs now say so: such an
    // entry stays until its grant is woken again or the next live cooldown starts, so the entries are
    // bounded by the recipients whose direct push failed since then, each holding only its DID. This
    // pins that statement: after quiet, at most one entry per failed recipient, DIDs only; after a
    // wake-up of each, none.
    [Fact]
    public async Task QA_110_4_1_After_quiet_the_entries_are_at_most_one_DID_per_failed_recipient_and_a_wake_up_of_each_leaves_none()
    {
        await using var direct = await DirectWorld.StartAsync(o => o.CoordinateMediation.MaxGrants = 2 * ManyGrants);
        var fixture = direct.Fixture;
        var grants = await ManyRefusingGrantsAsync(direct);
        fixture.Dispatcher.GrantEntries.Should().Be(ManyGrants);

        fixture.Time.Advance(Pause);

        fixture.Dispatcher.GrantEntries.Should().BeLessThanOrEqualTo(ManyGrants, "at most one entry per recipient whose direct push failed");
        fixture.Dispatcher.RetainedCharacters.Should().BeLessThanOrEqualTo(grants.Sum(g => (long)g.Peer.Length), "each holds only its DID");

        foreach (var grant in grants)
            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Dispatcher.GrantEntries.Should().Be(0, "a wake-up of each drops its spent entry");
        fixture.Dispatcher.RetainedCharacters.Should().Be(0);
        direct.Sends.Sent.Should().HaveCount(ManyGrants, "no record was posted twice");
    }

    // QA-110-4-3 (P3). Every pause that starts walks every entry the dispatcher holds, under the lock
    // every wake-up takes (StartDirectPause → PruneLocked on each). With N recipients paused at once —
    // an outage of an endpoint host many recipients share — a burst of N refusals costs about N²/2
    // entry checks: each paused entry reads the clock once per scan. Measured (real clock, 4
    // workers): 1,000 → 27–69 µs per failure; 4,000 → 120 µs; 16,000 → 178 µs (2.8 s for the burst);
    // 32,000 → 350 µs (11.2 s), with a forward's wake-up waiting up to 44–53 ms for the lock. Before
    // the churn change this walk ran only at a deadline (at most one per worker per PushTimeout).
    // Fixed: a direct pause that starts looks at no other entry (O(1)); the live cooldown keeps its walk.
    [Fact]
    public async Task QA_110_4_3_A_burst_of_refusals_costs_the_dispatcher_work_linear_in_the_number_of_recipients()
    {
        CountingClock? clock = null;
        await using var direct = await DirectWorld.StartAsync(o => o.CoordinateMediation.MaxGrants = 2 * ManyGrants, clock: inner => clock = new CountingClock(inner));
        _ = direct.Fixture.Dispatcher; // built on first use, with the counting clock
        var before = clock!.Reads;

        await ManyRefusingGrantsAsync(direct);

        (clock.Reads - before).Should().BeLessThan(10L * ManyGrants, "a pause that starts should not walk every other recipient's entry ({0} recipients)", ManyGrants);
    }

    private static async Task<List<DirectGrant>> ManyRefusingGrantsAsync(DirectWorld direct)
    {
        var fixture = direct.Fixture;
        var grants = new List<DirectGrant>(ManyGrants);
        for (var i = 0; i < ManyGrants; i++)
        {
            var grant = await direct.GrantAsync("qa4-many-" + i);
            await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 98));
            grants.Add(grant);
        }
        direct.Sends.Fail(98);
        foreach (var grant in grants)
            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(ManyGrants, Wait);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "every push settled");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Pushes(PushOutcomes.Failed).Should().Be(ManyGrants);
        return grants;
    }

    // ---- counters and log lines --------------------------------------------------------------------

    // During a pause the directory is not asked and nothing else is counted: no NoEndpoint (even when
    // the directory has dropped the endpoint meanwhile), no NoLiveSession (direct push is on). After
    // the pause the directory's answer counts again, once per operation.
    [Fact]
    public async Task During_a_pause_a_wake_up_counts_one_skip_and_nothing_else_and_the_directory_is_not_asked()
    {
        await using var direct = await DirectWorld.StartAsync();
        var fixture = direct.Fixture;
        var grant = await direct.GrantAsync("qa4-counts");
        direct.Sends.Fail(71);
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 71));
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 72));
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(1, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        var asked = direct.Endpoints.Asked.Count;

        // The host drops the endpoint during the pause.
        direct.Endpoints.OnFind = (_, _) => ValueTask.FromResult<Uri?>(null);
        for (var wake = 0; wake < 3; wake++)
        {
            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await fixture.WaitForPushOutcomesAsync(2 + wake, Wait);
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }
        direct.Endpoints.Asked.Count.Should().Be(asked, "a paused direct step asks the directory nothing");

        fixture.Time.Advance(Pause);
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(5, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().Equal(
            PushOutcomes.Failed,
            PushOutcomes.SkippedCooldown, PushOutcomes.SkippedCooldown, PushOutcomes.SkippedCooldown,
            PushOutcomes.SkippedNoEndpoint);
        fixture.PushOutcomeLog.Should().NotContain(PushOutcomes.SkippedNoLiveSession);
        direct.Sends.Seeds.Should().Equal(new byte[] { 71 });
    }

    public enum Ending
    {
        Refused,
        SendFault,
        Deadline,
    }

    // Each way a direct push can fail writes its lines without a wire value: no DID, no record id,
    // no endpoint, no exception text — during the push, the pause it starts and the skips after it.
    [Theory]
    [InlineData(Ending.Refused)]
    [InlineData(Ending.SendFault)]
    [InlineData(Ending.Deadline)]
    public async Task A_failed_direct_push_its_pause_and_the_skips_after_it_log_no_wire_value(Ending ending)
    {
        await using var direct = await DirectWorld.StartAsync();
        var fixture = direct.Fixture;
        var grant = await direct.GrantAsync("qa4-logs-" + ending);
        var stored = await StartFailedPushAsync(direct, grant, ending);
        for (var wake = 0; wake < 2; wake++)
        {
            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await fixture.WaitForPushOutcomesAsync(2 + wake, Wait);
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }

        PipelineLogs.Of<PushDispatcher>(fixture.Logs).Should().ContainSingle(l => l.Formatted.Contains("paused for", StringComparison.Ordinal), "one pause, one line");
        PipelineLogs.Leaking(fixture.Logs, grant.Peer, grant.Recipient, stored.MessageId, direct.Endpoints.Of(grant.Peer).Host, direct.Endpoints.Of(grant.Peer).ToString(), HandlerBoomException.Marker)
            .Should().BeEmpty();
    }

    // QA-110-4-2 (P3). The docs said "one line per failed push". A refused direct push, and one whose
    // send faulted, write two lines on purpose: the push step's, which says why it did not deliver
    // ("Direct push not delivered: …"), and the dispatcher's, which says what it paused ("Direct
    // pushes to a recipient paused …"); the deadline writes one, the dispatcher's, with its reason and
    // the pause. The docs now say so; this pins it.
    [Theory]
    [InlineData(Ending.Refused)]
    [InlineData(Ending.SendFault)]
    [InlineData(Ending.Deadline)]
    public async Task QA_110_4_2_A_refused_or_faulted_direct_push_writes_the_steps_line_and_the_pauses_and_a_deadline_writes_one(Ending ending)
    {
        await using var direct = await DirectWorld.StartAsync();
        var grant = await direct.GrantAsync("qa4-lines-" + ending);
        await StartFailedPushAsync(direct, grant, ending);

        var lines = direct.Fixture.Logs.Entries.Where(l => l.Formatted.StartsWith("Direct push", StringComparison.Ordinal)).ToList();
        var dispatcher = typeof(PushDispatcher).FullName;
        if (ending == Ending.Deadline)
        {
            var only = lines.Should().ContainSingle("the deadline: the dispatcher's line alone").Subject;
            only.Category.Should().Be(dispatcher);
            only.Formatted.Should().StartWith("Direct push not delivered (Timeout)").And.Contain("paused for");
        }
        else
        {
            lines.Should().HaveCount(2, "why it did not deliver, and what was paused ({0})", ending);
            var step = lines.Should().ContainSingle(l => l.Category != dispatcher, "the push step's line").Subject;
            step.Formatted.Should().StartWith("Direct push not delivered:");
            step.Formatted.Should().Contain(ending == Ending.Refused ? "did not accept the message" : nameof(HandlerBoomException));
            lines.Should().ContainSingle(l => l.Category == dispatcher && l.Formatted.StartsWith("Direct pushes to a recipient paused for", StringComparison.Ordinal), "the dispatcher's line");
        }
    }

    private static async Task<StoredMessage> StartFailedPushAsync(DirectWorld direct, DirectGrant grant, Ending ending)
    {
        var fixture = direct.Fixture;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        direct.Sends.OnSend = async (_, _, ct) =>
        {
            switch (ending)
            {
                case Ending.Refused:
                    return false;
                case Ending.SendFault:
                    throw new HandlerBoomException(HandlerBoomException.Marker + " " + direct.Endpoints.Of(grant.Peer));
                default:
                    held.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                    return true;
            }
        };
        var stored = await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 81));
        var mark = fixture.Time.Mark();
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        if (ending == Ending.Deadline)
        {
            await held.Task.WaitAsync(Wait);
            await fixture.Time.WaitForTimerAsync(PushTimeout, mark, Wait);
            fixture.Time.Advance(PushTimeout);
        }
        await fixture.WaitForPushOutcomesAsync(1, Wait);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "settled");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        return stored;
    }

    // ---- the narrowed guarantee under a stream of forwards ----------------------------------------

    // A recipient with a continuous stream of forwards and an endpoint that refuses every second POST.
    // Every message gets at most one POST; none is lost (pickup gets exactly what was not delivered);
    // the messages go in arrival order; and each pause cycle posts exactly two (one delivered, one
    // refused): a refused message holds the later ones up for one pause, never longer, and is never
    // sent again.
    [Fact]
    public async Task An_endpoint_that_refuses_every_second_POST_under_a_stream_of_forwards_posts_each_message_at_most_once_loses_none_and_posts_two_per_pause()
    {
        const int Cycles = 6;
        const int PerCycle = 3;
        await using var direct = await DirectWorld.StartAsync();
        var fixture = direct.Fixture;
        var grant = await direct.GrantAsync("qa4-stream");
        var posts = 0;
        direct.Sends.OnSend = (_, _, _) => Task.FromResult(Interlocked.Increment(ref posts) % 2 == 1);

        var postsPerCycle = new List<int>();
        byte seed = 100;
        for (var cycle = 0; cycle < Cycles; cycle++)
        {
            var before = direct.Sends.Sent.Count;
            for (var i = 0; i < PerCycle; i++, seed++)
            {
                await direct.ForwardAsync(grant.Recipient, PushFixture.Payload(64, seed), $"qa4-stream-{seed}");
                await fixture.Dispatcher.WaitForIdleAsync(Wait);
            }
            postsPerCycle.Add(direct.Sends.Sent.Count - before);
            fixture.Time.Advance(Pause);
        }

        var sent = direct.Sends.Seeds;
        sent.Should().OnlyHaveUniqueItems("every message is posted at most once");
        sent.Should().BeInAscendingOrder("oldest first");
        postsPerCycle.Should().AllSatisfy(n => n.Should().Be(2), "one run per pause: one delivered, then the refusal that ends it");
        fixture.Pushes(PushOutcomes.PushedDirect).Should().Be(Cycles);
        fixture.Pushes(PushOutcomes.Failed).Should().Be(Cycles);
        fixture.Pushes(PushOutcomes.SkippedCooldown).Should().Be((PerCycle - 1) * Cycles - 1, "one skip per forward that arrived during a pause (all but the first two of the first cycle, and of each later one all but the first)");

        var total = Cycles * PerCycle;
        var delivered = sent.Where((_, i) => i % 2 == 0).ToHashSet();
        fixture.Store.MessageCount.Should().Be(total - delivered.Count, "none is lost: what was not delivered is still queued");
        var picked = await direct.World.PickupAsync(grant.Peer);
        picked.Should().HaveCount(total - delivered.Count, "pickup gets the refused ones and the ones the pauses held back");
    }

    // ---- stores outside their contract over many pause cycles -------------------------------------

    // A store that drops the push mark, at batch 1, 2 and 10, over five pauses with three wake-ups
    // each: one POST per pause (the refused head), whatever the wake-ups; the records behind it are
    // never posted (the IMessageStore docs say so), and every record is still there for pickup.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    public async Task A_store_that_drops_the_mark_costs_one_POST_per_pause_at_any_batch_over_many_pauses(int batch)
    {
        const int Pauses = 5;
        var store = new MarkDroppingStore(new InMemoryMessageStore());
        await using var direct = await DirectWorld.StartAsync(o => o.Pickup.MaxDeliveryBatch = batch, s => s.AddSingleton<IMessageStore>(store));
        var fixture = direct.Fixture;
        var grant = await direct.GrantAsync("qa4-dropping-" + batch);
        direct.Sends.Fail(91);
        var ids = new List<string>
        {
            (await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 91))).MessageId,
            (await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 92))).MessageId,
            (await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 93))).MessageId,
        };

        for (var pause = 1; pause <= Pauses; pause++)
        {
            for (var wake = 0; wake < 3; wake++)
            {
                fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
                await fixture.WaitForPushOutcomesAsync(3 * (pause - 1) + 1 + wake, Wait);
                await fixture.Dispatcher.WaitForIdleAsync(Wait);
            }
            direct.Sends.Sent.Should().HaveCount(pause, "pause {0}: one POST", pause);
            store.PushTakes.Should().HaveCount(pause, "pause {0}: one take", pause);
            fixture.Time.Advance(Pause);
        }
        direct.Sends.Seeds.Should().AllBeEquivalentTo((byte)91);
        fixture.PushOutcomeLog.Should().Equal(Enumerable.Range(0, Pauses).SelectMany(_ => new[] { PushOutcomes.Failed, PushOutcomes.SkippedCooldown, PushOutcomes.SkippedCooldown }));
        (await direct.World.PickupFromAsync(store, grant.Peer)).Should().Equal(ids);
    }

    // A store whose clock lags, at batch 1, 2 and 10, over five wake-ups: one take per wake-up,
    // nothing posted, nothing counted, and no pause (the next wake-up takes again at once).
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    public async Task A_store_whose_clock_lags_costs_one_take_per_wake_up_at_any_batch_and_never_pauses(int batch)
    {
        const int Wakes = 5;
        var store = new LaggingClockStore(new InMemoryMessageStore(), TimeSpan.FromSeconds(5));
        await using var direct = await DirectWorld.StartAsync(o => o.Pickup.MaxDeliveryBatch = batch, s => s.AddSingleton<IMessageStore>(store));
        var fixture = direct.Fixture;
        var grant = await direct.GrantAsync("qa4-lagging-" + batch);
        var expires = fixture.Time.GetUtcNow().ToUnixTimeSeconds() + 2;
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 94), new PickupDeliveryOptions(null, expires, AllowLive: false));
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 95));
        fixture.Time.Advance(TimeSpan.FromSeconds(3)); // expired on the mediator's clock, not on the store's

        for (var wake = 1; wake <= Wakes; wake++)
        {
            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await MessagePickupLive.UntilAsync(() => store.PushTakes == wake, $"wake-up {wake} took once");
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
            store.PushTakes.Should().Be(wake, "one take per wake-up");
        }
        direct.Sends.Sent.Should().BeEmpty("an expired record is never sent, and it blocks the one behind it (the store's fault)");
        fixture.PushOutcomeLog.Should().BeEmpty("given back unsent: no outcome, no pause");
    }

    // ---- the monotonic clock -----------------------------------------------------------------------

    // A step of the wall clock — forward or back, an NTP correction — neither shortens nor lengthens a
    // direct pause: it is measured on the monotonic clock.
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task A_step_of_the_wall_clock_neither_shortens_nor_lengthens_a_direct_pause(int hours)
    {
        WallStepClock? wall = null;
        await using var direct = await DirectWorld.StartAsync(clock: inner => wall = new WallStepClock(inner));
        var fixture = direct.Fixture;
        var grant = await direct.GrantAsync("qa4-wall-" + hours);
        direct.Sends.Fail(111);
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 111));
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 112));
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(1, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        wall!.Step(TimeSpan.FromHours(hours));
        fixture.Time.Advance(Pause - TimeSpan.FromTicks(1));
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(2, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog[1].Should().Be(PushOutcomes.SkippedCooldown, "still paused on the monotonic clock, whatever the wall clock says");

        fixture.Time.Advance(TimeSpan.FromTicks(1));
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(3, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog[2].Should().Be(PushOutcomes.PushedDirect, "the pause ended on the monotonic clock, whatever the wall clock says");
        direct.Sends.Seeds.Should().Equal(new byte[] { 111, 112 });
    }

    // ---- edge settings -----------------------------------------------------------------------------

    // PushTimeout at its 5-minute maximum (a 10-minute pause) and at its smallest (one tick), with one
    // worker and with 64: every recipient's refusal pauses it for exactly 2 × PushTimeout; the next
    // record of each goes at the first wake-up after that, once.
    [Theory]
    [InlineData(5 * 60 * TimeSpan.TicksPerSecond, 1)]
    [InlineData(5 * 60 * TimeSpan.TicksPerSecond, 64)]
    [InlineData(1L, 1)]
    [InlineData(1L, 64)]
    public async Task At_the_edge_settings_each_refusal_pauses_its_recipient_for_exactly_twice_PushTimeout(long pushTimeoutTicks, int concurrency)
    {
        var pushTimeout = TimeSpan.FromTicks(pushTimeoutTicks);
        var pause = PushDispatcher.CooldownPushTimeouts * pushTimeout;
        await using var direct = await DirectWorld.StartAsync(o =>
        {
            o.PushTimeout = pushTimeout;
            o.PushConcurrency = concurrency;
        });
        var fixture = direct.Fixture;
        var grants = new List<DirectGrant>();
        for (var i = 0; i < concurrency; i++)
        {
            var grant = await direct.GrantAsync($"qa4-edge-{pushTimeoutTicks}-{concurrency}-{i}");
            await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 121));
            await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 122));
            grants.Add(grant);
        }
        direct.Sends.Fail(121);

        foreach (var grant in grants)
            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(concurrency, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Time.Advance(pause - TimeSpan.FromTicks(1));
        foreach (var grant in grants)
            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(2 * concurrency, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Pushes(PushOutcomes.SkippedCooldown).Should().Be(concurrency, "one tick before 2 × PushTimeout every recipient is still paused");
        direct.Sends.Sent.Should().HaveCount(concurrency);

        fixture.Time.Advance(TimeSpan.FromTicks(1));
        foreach (var grant in grants)
            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(3 * concurrency, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Pushes(PushOutcomes.PushedDirect).Should().Be(concurrency, "at 2 × PushTimeout every recipient's next record goes");
        direct.Sends.Sent.Should().HaveCount(2 * concurrency, "each record once");
        direct.Sends.MaxInFlightPerEndpoint.Should().Be(1);
    }

    // ---- support -----------------------------------------------------------------------------------

    /// <summary>The records of <paramref name="grantPeerDid"/> no lease holds now, read under a lease that holds nothing.</summary>
    private static async Task<IReadOnlyList<string>> UnleasedAsync(PushFixture fixture, string grantPeerDid)
    {
        var now = fixture.Time.GetUtcNow();
        var probe = await fixture.Store.LeaseAsync(new MessageLeaseRequest(grantPeerDid, null, 32, long.MaxValue, now, new MessageLease("qa4-unleased-probe", now)), CancellationToken.None);
        return probe.Select(r => r.Id).ToList();
    }

    private static async Task<bool> Hold(TaskCompletionSource held, TaskCompletionSource<bool> gate)
    {
        held.TrySetResult();
        return await gate.Task; // ignores its token
    }

    /// <summary>The dispatcher's clock: the manual one, counting its monotonic readings.</summary>
    private sealed class CountingClock(TimeProvider inner) : TimeProvider
    {
        private long _reads;

        public long Reads => Interlocked.Read(ref _reads);

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp()
        {
            Interlocked.Increment(ref _reads);
            return inner.GetTimestamp();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => inner.CreateTimer(callback, state, dueTime, period);
    }

    /// <summary>The dispatcher's clock: the manual one, whose wall-clock reading a test can step (timers and timestamps stay monotonic).</summary>
    private sealed class WallStepClock(TimeProvider inner) : TimeProvider
    {
        private long _offset;

        public void Step(TimeSpan by) => Interlocked.Add(ref _offset, by.Ticks);

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow().AddTicks(Interlocked.Read(ref _offset));

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => inner.CreateTimer(callback, state, dueTime, period);
    }
}
