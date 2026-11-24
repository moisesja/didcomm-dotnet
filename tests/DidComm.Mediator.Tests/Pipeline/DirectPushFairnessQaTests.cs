using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Pickup;
using Polly.Timeout;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// QA of #110 (direct push): fairness and bounded work of the push workers with direct push on,
/// measured on the manual clock in push deadlines. The real dispatcher and push step run in a
/// container built by <c>AddMediator()</c>; the directory and the single-attempt sender are scripted
/// (<see cref="DirectWorld"/>). The plan's residual 1 is the yardstick: a tarpit holds one worker for
/// <c>PushTimeout</c> per cooldown, and with K tarpits and W workers a healthy push waits up to
/// ceil(K / W) deadlines; nothing is lost.
/// </summary>
public sealed class DirectPushFairnessQaTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PushTimeout = new MediatorOptions().PushTimeout;

    // ---- QA-110-1 (QA2-1 in the plan): a stall the transport gives up on before the deadline -------

    // The host's HTTP transport times a request out after HttpTransportOptions.RequestTimeout (Polly's
    // timeout strategy, TimeoutRejectedException), and the named client after its own
    // HttpClient.Timeout. Before the fix a stall such a timeout ended counted as "not delivered", the
    // operation went on to the grant's next message, and no pause ever started, so each queued
    // message got a stalled POST of its own. Now any direct push that does not deliver ends the run
    // and pauses that recipient's direct pushes, whatever ended it: a timeout shorter than the push
    // deadline ends the stall sooner, and the pause follows all the same (residual 1: a tarpit
    // holds a worker at most once per pause).
    [Fact]
    public async Task QA_110_1_A_stall_the_transport_gives_up_on_before_the_deadline_pauses_its_recipients_direct_pushes_like_the_deadline_does()
    {
        await using var direct = await DirectWorld.StartAsync(o => o.PushConcurrency = 1);
        var fixture = direct.Fixture;
        var tarpit = await direct.GrantAsync("qa1-tarpit");
        var healthy = await direct.GrantAsync("qa1-healthy");
        // The host's transport gives up on a request after a third of the push deadline.
        var requestTimeout = PushTimeout / 3;
        var stalls = 0;
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthySent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        direct.Sends.OnSend = async (endpoint, _, ct) =>
        {
            if (endpoint != direct.Endpoints.Of(tarpit.Peer))
            {
                healthySent.TrySetResult();
                return true;
            }
            Interlocked.Increment(ref stalls);
            stalled.TrySetResult();
            // Never answers; the transport's own timeout gives up on it, as Polly's timeout strategy does.
            await Task.Delay(requestTimeout, fixture.Time, ct);
            throw new TimeoutRejectedException();
        };
        var trapped = new List<string>();
        for (var i = 0; i < 10; i++)
            trapped.Add((await fixture.StoreAsync(tarpit.Recipient, PushFixture.Payload(64, (byte)(20 + i)))).MessageId);
        await fixture.StoreAsync(healthy.Recipient, PushFixture.Payload(64, 90));
        var mark = fixture.Time.Mark();

        fixture.Dispatcher.Wake(tarpit.Peer).Should().BeTrue();
        await stalled.Task.WaitAsync(Wait); // the only worker is held by the stall
        fixture.Dispatcher.Wake(healthy.Peer).Should().BeTrue();
        healthySent.Task.IsCompleted.Should().BeFalse("the only worker is still held by the stall");

        // The transport's timeout, well before the push deadline: the stall ends as not delivered,
        // the tarpit's direct pushes pause, and the worker serves the healthy grant.
        await fixture.Time.WaitForTimerAsync(requestTimeout, mark, Wait);
        fixture.Time.Advance(requestTimeout).Should().Be(1, "only the transport's timeout comes due");
        await healthySent.Task.WaitAsync(Wait);
        await MessagePickupLive.UntilAsync(() => fixture.Pushes(PushOutcomes.Failed) == 1 && fixture.Dispatcher.Unsettled == 0, "the stalled push failed and settled");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        Volatile.Read(ref stalls).Should().Be(1, "a stalling endpoint holds the worker once, then its recipient's direct pushes pause (residual 1)");

        // A wake-up during the pause runs the live step only: no second POST to the tarpit.
        fixture.Dispatcher.Wake(tarpit.Peer).Should().BeTrue("a wake-up is still served: only the direct step waits for the pause");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        Volatile.Read(ref stalls).Should().Be(1);
        fixture.Pushes(PushOutcomes.SkippedCooldown).Should().Be(1);
        (await direct.World.PickupAsync(tarpit.Peer)).Should().BeEquivalentTo(trapped, "nothing is lost: pickup gets every message of the tarpit");
    }

    // ---- K tarpits and W workers -------------------------------------------------------------------

    public static TheoryData<int, int> TarpitsAgainstTwoWorkers => new()
    {
        // K tarpits (each honours its deadline, as the HTTP client does), the deadlines a healthy push waits.
        { 1, 0 },
        { 2, 1 },
        { 3, 1 },
        { 4, 2 },
        { 5, 2 },
    };

    [Theory]
    [MemberData(nameof(TarpitsAgainstTwoWorkers))]
    public async Task With_K_tarpits_and_two_workers_a_healthy_push_waits_at_most_ceil_K_over_W_deadlines_and_nothing_is_lost(int tarpits, int expectedDeadlines)
    {
        const int Workers = 2;
        await using var direct = await DirectWorld.StartAsync(o => o.PushConcurrency = Workers);
        var fixture = direct.Fixture;
        var trapped = new List<DirectGrant>();
        for (var i = 0; i < tarpits; i++)
            trapped.Add(await direct.GrantAsync($"qa-k-tarpit-{i}"));
        var healthy = await direct.GrantAsync("qa-k-healthy");
        var tarpitEndpoints = trapped.Select(g => direct.Endpoints.Of(g.Peer)).ToHashSet();
        var gate = new PushGate();
        var deadlines = 0;
        var healthyAt = -1;
        direct.Sends.OnSend = async (endpoint, _, ct) =>
        {
            if (!tarpitEndpoints.Contains(endpoint))
            {
                Volatile.Write(ref healthyAt, Volatile.Read(ref deadlines));
                return true;
            }
            await gate.PassAsync(ct); // never answers before its deadline
            return false;
        };
        var held = new Dictionary<string, List<string>>();
        byte seed = 30;
        foreach (var grant in trapped)
        {
            held[grant.Peer] = new List<string>
            {
                (await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, seed++))).MessageId,
                (await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, seed++))).MessageId,
            };
        }
        await fixture.StoreAsync(healthy.Recipient, PushFixture.Payload(64, 99));

        foreach (var grant in trapped)
            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await gate.WaitForArrivalsAsync(Math.Min(tarpits, Workers), Wait);
        fixture.Dispatcher.Wake(healthy.Peer).Should().BeTrue();

        // Pushes held at a tarpit now: arrived and not yet ended at a deadline.
        int Held() => gate.Arrived - (int)fixture.Pushes(PushOutcomes.Failed);
        bool HealthySent() => Volatile.Read(ref healthyAt) >= 0;
        bool Done() => HealthySent() && gate.Arrived == tarpits && fixture.Dispatcher.Unsettled == 0;
        while (true)
        {
            // Nothing moves without the clock once every worker is held, or once the healthy push
            // went out and only held tarpits are left; with a worker free, the queue drains by itself.
            await MessagePickupLive.UntilAsync(
                () => Done()
                      || (Held() == Workers && fixture.Dispatcher.Unsettled == Workers)
                      || (HealthySent() && gate.Arrived == tarpits && Held() > 0 && fixture.Dispatcher.Unsettled == Held()),
                "the workers are settled or held");
            if (Done())
                break;
            Volatile.Read(ref deadlines).Should().BeLessThan(tarpits + 2, "hang guard");
            var failed = fixture.Pushes(PushOutcomes.Failed);
            var firing = Held();
            Volatile.Write(ref deadlines, deadlines + 1);
            fixture.Time.Advance(PushTimeout);
            await MessagePickupLive.UntilAsync(() => fixture.Pushes(PushOutcomes.Failed) >= failed + firing, "the held pushes reached their deadline");
        }
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        healthyAt.Should().Be(expectedDeadlines, "{0} tarpits against {1} workers", tarpits, Workers);
        healthyAt.Should().BeLessThanOrEqualTo((tarpits + Workers - 1) / Workers, "residual 1: at most ceil(K / W) deadlines");
        gate.Arrived.Should().Be(tarpits, "each tarpitted grant made one POST, never a second (its second message waits)");
        direct.Sends.MaxInFlightPerEndpoint.Should().Be(1, "at most one POST in flight per grant (D-3)");
        foreach (var grant in trapped)
            (await direct.World.PickupAsync(grant.Peer)).Should().BeEquivalentTo(held[grant.Peer], "nothing is lost: pickup gets both messages");
        fixture.Pushes(PushOutcomes.PushedDirect).Should().Be(1);
        fixture.Pushes(PushOutcomes.Failed).Should().Be(tarpits);
    }

    // ---- MaxDeliveryBatch per run, then the worker is yielded ---------------------------------------

    [Fact]
    public async Task A_busy_grant_yields_its_worker_after_MaxDeliveryBatch_direct_pushes_and_the_next_grant_goes_before_its_eleventh()
    {
        await using var direct = await DirectWorld.StartAsync(o => o.PushConcurrency = 1);
        var fixture = direct.Fixture;
        var busy = await direct.GrantAsync("qa-busy");
        var other = await direct.GrantAsync("qa-other");
        var gate = new PushGate();
        var first = true;
        direct.Sends.OnSend = async (endpoint, _, ct) =>
        {
            // The first POST waits until both grants are woken, so the order is decided by the dispatcher alone.
            if (first)
            {
                first = false;
                await gate.PassAsync(ct);
            }
            return true;
        };
        for (var i = 0; i < 25; i++)
            await fixture.StoreAsync(busy.Recipient, PushFixture.Payload(64, (byte)(100 + i)));
        await fixture.StoreAsync(other.Recipient, PushFixture.Payload(64, 7));

        fixture.Dispatcher.Wake(busy.Peer).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);
        fixture.Dispatcher.Wake(other.Peer).Should().BeTrue();
        gate.Open();
        await fixture.WaitForPushOutcomesAsync(26, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        var perRun = fixture.Dispatcher.PerRun;
        perRun.Should().Be(new MessagePickupOptions().MaxDeliveryBatch);
        var seeds = direct.Sends.Seeds;
        seeds.Should().HaveCount(26);
        seeds.ToList().IndexOf(7).Should().Be(perRun, "the busy grant yields after {0} pushes and the other grant is served in turn", perRun);
        seeds.Where(s => s != 7).Should().Equal(Enumerable.Range(100, 25).Select(i => (byte)i), "the busy grant's messages go oldest first, each once");
        fixture.PushOutcomeLog.Should().OnlyContain(o => o == PushOutcomes.PushedDirect);
        fixture.Store.MessageCount.Should().Be(0);
    }

    // ---- a directory that ignores cancellation ------------------------------------------------------

    public static TheoryData<int, bool> StuckDirectories => new()
    {
        // K grants whose directory lookup never ends (it ignores its token), against 2 workers (4
        // unsettled operations at most): below 2W the healthy push goes out after one deadline; at
        // 2W every operation is held and the healthy wake-up starts nothing (the documented D-4 residual).
        { 1, true },
        { 2, true },
        { 3, true },
        { 4, false },
    };

    [Theory]
    [MemberData(nameof(StuckDirectories))]
    public async Task A_directory_that_ignores_cancellation_holds_its_grant_until_it_returns_and_never_more_than_the_unsettled_bound(int stuck, bool healthyGoesOut)
    {
        const int Workers = 2;
        MessagePickupLive.RaiseThreadPoolFloor(stuck);
        await using var direct = await DirectWorld.StartAsync(o => o.PushConcurrency = Workers);
        var fixture = direct.Fixture;
        var grants = new List<DirectGrant>();
        for (var i = 0; i < stuck; i++)
            grants.Add(await direct.GrantAsync($"qa-stuck-{i}"));
        var healthy = await direct.GrantAsync("qa-stuck-healthy");
        var stuckPeers = grants.Select(g => g.Peer).ToHashSet(StringComparer.Ordinal);
        var lookups = new PushGate();
        direct.Endpoints.OnFind = async (peer, _) =>
        {
            if (stuckPeers.Contains(peer))
                await lookups.PassAsync(CancellationToken.None); // ignores the push deadline
            return direct.Endpoints.Of(peer);
        };
        foreach (var grant in grants)
            await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 40));
        var forHealthy = await fixture.StoreAsync(healthy.Recipient, PushFixture.Payload(64, 41));

        var mark = fixture.Time.Mark();
        foreach (var grant in grants.Take(Workers))
            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await lookups.WaitForArrivalsAsync(Math.Min(stuck, Workers), Wait);
        await MessagePickupLive.UntilAsync(() => fixture.Time.ScheduledTimersSince(mark) == Math.Min(stuck, Workers), "the held lookups' deadlines are scheduled");
        fixture.Time.Advance(PushTimeout); // the workers are freed; the lookups run on
        await MessagePickupLive.UntilAsync(() => fixture.Pushes(PushOutcomes.Failed) == Math.Min(stuck, Workers), "the held pushes reached their deadline");
        if (stuck > Workers)
        {
            mark = fixture.Time.Mark();
            foreach (var grant in grants.Skip(Workers))
                fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await lookups.WaitForArrivalsAsync(stuck, Wait);
            await MessagePickupLive.UntilAsync(() => fixture.Time.ScheduledTimersSince(mark) == stuck - Workers, "the second held lookups' deadlines are scheduled");
            fixture.Time.Advance(PushTimeout);
            await MessagePickupLive.UntilAsync(() => fixture.Pushes(PushOutcomes.Failed) == stuck, "every held push reached its deadline");
        }
        fixture.Dispatcher.Unsettled.Should().Be(stuck, "every abandoned lookup still runs and holds its grant (D-3)");

        fixture.Dispatcher.Wake(healthy.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(stuck + 1, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        if (healthyGoesOut)
        {
            fixture.PushOutcomeLog[^1].Should().Be(PushOutcomes.PushedDirect);
            fixture.Store.MessageCount.Should().Be(stuck);
        }
        else
        {
            fixture.PushOutcomeLog[^1].Should().Be(PushOutcomes.SkippedSaturated, "2 x PushConcurrency operations are held: the documented D-4 residual");
            (await direct.World.PickupAsync(healthy.Peer)).Should().Equal(new[] { forHealthy.MessageId }, "pickup keeps working");
        }

        lookups.Open();
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "every lookup returned and its operation settled");
        direct.Sends.Sent.Where(s => s.Endpoint != direct.Endpoints.Of(healthy.Peer)).Should().BeEmpty("a lookup that ended after its deadline sends nothing");
        fixture.Store.MessageCount.Should().Be(healthyGoesOut ? stuck : stuck + 1, "the stuck grants' messages (and a skipped healthy one) wait for pickup");
    }
}
