using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Pickup;
using DidComm.TestSupport;
using DidComm.Transports;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// Red-team pass 1 (#110, direct push): starvation and fairness. A recipient that controls its
/// registered endpoint controls that endpoint's host name, and so its name lookup. The mediator's
/// endpoint check before the take (<see cref="DirectPushSender.CheckTargetAsync"/>) runs under the
/// push deadline (ADV-110-1a-2, 1a-3). These tests run the real sender's check over the guard's
/// lookup seam: a name under <c>stall.attacker.example</c> does not answer until the test lets it
/// fail the way a resolver that timed out does, or until the push deadline cancels it.
/// </summary>
public sealed class DirectPushStarvationAdv110Tests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PushTimeout = new MediatorOptions().PushTimeout;
    private static readonly TimeSpan Pause = PushDispatcher.CooldownPushTimeouts * PushTimeout;

    // Residual 1 says a tarpit holds one worker at most once per pause, whatever ends the stall. A
    // stalled name lookup used to end as a guard refusal before the take, reported as "no endpoint",
    // which never pauses: the same recipient then held a worker for the whole stall again at every
    // wake-up. Now the deadline cuts the lookup off, or it fails and the endpoint is refused, and
    // either way the recipient's direct pushes pause.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_recipient_whose_endpoint_name_lookup_stalls_holds_a_worker_at_most_once_per_pause(bool cutByTheDeadline)
    {
        await using var direct = await StallWorld.StartAsync();
        var fixture = direct.World.Fixture;
        var grant = await direct.World.GrantAsync("adv1a-stall");
        direct.Endpoints.Set(grant.Peer, new Uri("https://inbox.stall.attacker.example/didcomm"));

        // Wake-up 1: the lookup before the take stalls, holding the operation (and its worker), until
        // the deadline cuts it off or it fails.
        await direct.World.StoreAsync(grant, 1);
        var mark = fixture.Time.Mark();
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Lookups.ArrivedAsync(1, Wait);
        fixture.Dispatcher.Unsettled.Should().Be(1, "the stalled lookup holds the operation");
        if (cutByTheDeadline)
        {
            await fixture.Time.WaitForTimerAsync(PushTimeout, mark, Wait);
            fixture.Time.Advance(PushTimeout).Should().Be(1, "only the stalled push's deadline is due");
        }
        else
        {
            direct.Lookups.Release(1);
        }
        await fixture.WaitForPushOutcomesAsync(1, Wait);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the stalled push settled");

        // Wake-ups 2 and 3, the clock not moved past the pause: each lookup would stall as long again.
        direct.Lookups.Release(2);
        for (var i = 2; i <= 3; i++)
        {
            await direct.World.StoreAsync(grant, (byte)i);
            fixture.Dispatcher.Wake(grant.Peer);
            await fixture.WaitForPushOutcomesAsync(i, Wait);
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }

        direct.Lookups.Count.Should().Be(1, "a recipient that stalled a worker is paused for {0} (residual 1), however the stall ended", Pause);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.SkippedCooldown, PushOutcomes.SkippedCooldown);
    }

    // The D-4 bound counts an abandoned push until its call ends. The endpoint check runs under the
    // push deadline, so each push whose lookup does not answer ends at its deadline and nothing else
    // waits.
    [Fact]
    public async Task Recipients_whose_endpoint_name_lookups_stall_past_the_deadline_never_stop_another_recipients_live_push()
    {
        MessagePickupLive.RaiseThreadPoolFloor(2);
        await using var direct = await StallWorld.StartAsync(o => o.PushConcurrency = 1);
        var fixture = direct.World.Fixture;
        var (healthy, session) = await direct.World.LiveGrantAsync("adv1a-healthy");
        try
        {
            for (var i = 1; i <= 2; i++)
            {
                var stalling = await direct.World.GrantAsync($"adv1a-stall-{i}");
                direct.Endpoints.Set(stalling.Peer, new Uri($"https://inbox-{i}.stall.attacker.example/didcomm"));
                await direct.World.StoreAsync(stalling, (byte)i);
                var mark = fixture.Time.Mark();
                fixture.Dispatcher.Wake(stalling.Peer).Should().BeTrue();
                await direct.Lookups.ArrivedAsync(i, Wait);
                await fixture.Time.WaitForTimerAsync(PushTimeout, mark, Wait);
                fixture.Time.Advance(PushTimeout);
                await fixture.WaitForPushOutcomesAsync(i, Wait);
                await fixture.Dispatcher.WaitForIdleAsync(Wait);
            }
            await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the deadline ended both stalled lookups");

            // Long after both pauses ran out, the healthy recipient's live push.
            fixture.Time.Advance(10 * PushTimeout);
            var message = await direct.World.StoreAsync(healthy, 9);
            fixture.Dispatcher.Wake(healthy.Peer);
            await fixture.WaitForPushOutcomesAsync(3, Wait);

            fixture.PushOutcomeLog.Should().Equal(
                new[] { PushOutcomes.Failed, PushOutcomes.Failed, PushOutcomes.PushedLive },
                "a recipient's stall holds one push at a time, never every push of others (PushConcurrency docs)");
            LivePushWorld.Deliveries(session).Select(d => d.Id).Should().Equal(message.MessageId);
            direct.Lookups.Count.Should().Be(2, "each name was looked up once");
        }
        finally
        {
            direct.Lookups.Release(16);
            await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the stalled lookups ended");
        }
    }

    // A third party, not the recipient: any sender can put a message in a recipient's mailbox that
    // the recipient's endpoint refuses (this library's own receiving endpoint answers 400 to an
    // envelope it cannot open). Before the fix each such message, taken first (oldest first), paused
    // the recipient's direct pushes; N of them held the recipient's real mail back for N pauses
    // (N × 2 × PushTimeout), each costing the sender one small forward. Now (the coordinator's call on
    // ADV-110-1a-4) a 4xx other than 408 and 429 — the endpoint's definite "not this message" — moves
    // on to the next record with no pause: the real message goes in the same wake-up.
    [Fact]
    public async Task Messages_a_third_party_stored_ahead_that_the_endpoint_refuses_do_not_hold_a_recipients_real_message_back()
    {
        await using var direct = await DirectWorld.StartAsync();
        var fixture = direct.Fixture;
        var grant = await direct.GrantAsync("adv1a-junk");
        for (var i = 1; i <= 3; i++)
        {
            direct.Sends.Refuse((byte)i);
            await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, (byte)i));
        }
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 4));

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(4, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        direct.Sends.Seeds.Should().Equal(new byte[] { 1, 2, 3, 4 }, "the real message goes in the same wake-up as the junk ahead of it, each posted once");
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.Failed, PushOutcomes.Failed, PushOutcomes.PushedDirect);
        fixture.PushOutcomeLog.Should().NotContain(PushOutcomes.SkippedCooldown, "a refusal pauses nothing");
    }
}

/// <summary>
/// A push world whose direct pushes go through the real <see cref="DirectPushSender"/> check (the
/// default policy, whose name lookup is <see cref="StalledLookups"/>); the send itself is accepted
/// without a network.
/// </summary>
internal sealed class StallWorld : IAsyncDisposable
{
    private StallWorld(LivePushWorld world, DirectEndpoints endpoints, StalledLookups lookups)
    {
        World = world;
        Endpoints = endpoints;
        Lookups = lookups;
    }

    public LivePushWorld World { get; }
    public DirectEndpoints Endpoints { get; }
    public StalledLookups Lookups { get; }

    public static async Task<StallWorld> StartAsync(Action<MediatorOptions>? configure = null)
    {
        var endpoints = new DirectEndpoints();
        var lookups = new StalledLookups();
        var sender = new DirectPushSender(new AcceptingTransport(), new OutboundEndpointPolicy { ResolveDnsAsync = lookups.ResolveAsync });
        var world = await LivePushWorld.StartAsync(configure, s =>
            DirectPushRouting.Register(s, new DirectPushRoute(endpoints, new Lazy<IDirectPushSender>(() => sender))));
        return new StallWorld(world, endpoints, lookups);
    }

    public ValueTask DisposeAsync()
    {
        Lookups.Release(64);
        return World.DisposeAsync();
    }
}

/// <summary>
/// The outbound check's name lookup: a name under <c>stall.attacker.example</c> stalls until released,
/// then fails as a resolver that timed out does, or until the token (the push deadline) cancels it;
/// any other name resolves at once to a public address.
/// </summary>
internal sealed class StalledLookups
{
    private readonly SemaphoreSlim _release = new(0);
    private readonly SemaphoreSlim _arrived = new(0);
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Release(int n) => _release.Release(n);

    public async Task ArrivedAsync(int total, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Count < total)
        {
            if (!await _arrived.WaitAsync(deadline - DateTime.UtcNow))
                throw new TimeoutException("the lookup never arrived");
        }
    }

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        if (!host.EndsWith(".stall.attacker.example", StringComparison.OrdinalIgnoreCase))
            return new[] { IPAddress.Parse("203.0.113.50") };
        Interlocked.Increment(ref _count);
        _arrived.Release();
        await _release.WaitAsync(ct);
        throw new SocketException((int)SocketError.TryAgain);
    }
}

/// <summary>A transport that accepts every send (no network).</summary>
internal sealed class AcceptingTransport : IDidCommTransport
{
    public string Scheme => "https";

    public bool CanHandle(Uri endpoint) => endpoint.Scheme is "https" or "http";

    public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
        => Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: 202));
}
