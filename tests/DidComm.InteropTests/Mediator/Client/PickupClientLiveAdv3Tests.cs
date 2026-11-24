using System.Collections.Concurrent;
using System.Reflection;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// ADV-3 red-team pass (#96), end to end: the recipient's live stream over this library's mediator
/// on a TestServer, through the real WebSocket transport, inbound dispatcher and correlation
/// channel. The client's clock is a manual one; the channel is built over a seam that runs the real
/// unpack and then holds a message whose content is "slow" until its budget cancels it — a sender
/// whose DID never resolves. Rendezvous only (L-044).
/// </summary>
public sealed class PickupClientLiveAdv3Tests
{
    private static readonly FieldInfo LiveRegistry = typeof(MediatorClientChannel).GetField("_live", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>
    /// ADV3-1, end to end (defect, Medium; fixed in fix round 3). The property and the root cause are on the unit
    /// test of the same id (<c>DidComm.Mediator.Tests.Client.PickupClientLiveAdv3Tests</c>). Here, with
    /// this library's mediator: Alice's message delayed by 30 s (FR-PICK-08: never pushed when it
    /// falls due; queued before the stream turned live mode on, so before its arrival mark) falls
    /// due. Mallory, a stranger whose DID never resolves, sends one message per budget (20 s):
    /// the mediator pushes each at once; while the stream opens one, the next waits in its mailbox.
    /// For three poll intervals the stream re-arms live mode every minute and yields Mallory's
    /// failures, but never sends a <c>delivery-request</c>, so Alice's message is never delivered.
    /// <c>PickupClientDelayedQa2Tests.A_delayed_message_queued_before_live_mode_is_reached_only_by_a_cycles_drain</c>
    /// is the control: without Mallory the first cycle after it is due drains it.
    /// </summary>
    [Fact]
    public async Task ADV3_1_end_to_end_a_strangers_slow_messages_keep_a_due_delayed_message_from_ever_being_drained()
    {
        var time = new QaManualTimeProvider();
        var observer = new PickupRequestObserver();
        await using var mediator = await PickupE2E.StartMediatorAsync(observer);
        var slow = new SlowSeam();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.Time = time;
            o.ConfigureServices = services => services.AddSingleton(sp =>
            {
                var client = sp.GetRequiredService<DidCommClient>();
                return new MediatorClientChannel(client, new MediatorClientOptions { MessagePickupVersion = MessagePickupVersion.V3 }, receivesInbound: true, time, sp.GetService<ILoggerFactory>(),
                    send: null, resolveEndpoint: null, unpack: (text, ct) => slow.UnpackAsync(client, text, ct));
            });
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var mallory = await harness.NewAliceAsync();
        var delayed = alice.Note(recipient.Did, "delayed, queued first");
        await Qa2.PostForwardAsync(mediator, alice, recipient.Did, await alice.AuthcryptAsync(delayed, recipient.Did), Qa.Delay(30_000));

        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var move = PickupE2E.MoveNext(stream);
        await observer.WaitForAsync(bob.Did, 2); // live-on, then a drain that finds nothing due yet
        await Qa2.StreamIdleAsync(harness, time);
        mediator.Clock.Advance(TimeSpan.FromSeconds(31)); // Alice's message is due: only a drain reaches it
        var drainsBefore = Drains(observer, bob.Did);

        // Mallory's first message is pushed and the stream starts opening it.
        await PickupE2E.SendAsync(mediator, mallory, recipient, "slow");
        await slow.EnteredAsync(1);
        var options = harness.Channel.Options;
        var perInterval = (int)(options.LivePollInterval.Ticks / options.UnpackTimeout.Ticks);
        var rounds = 3 * perInterval; // three poll intervals
        var liveOnBefore = observer.KindsFrom(bob.Did).Count(k => k == "live-on");
        try
        {
            for (var i = 1; i <= rounds; i++)
            {
                // While it is being opened, Mallory's next one is pushed and waits in the mailbox.
                await PickupE2E.SendAsync(mediator, mallory, recipient, "slow");
                await mediator.PushesIdleAsync();
                await MediatorTestServer.WaitUntilAsync(() => Buffered(harness.Channel) >= 1, "Mallory's next push waits in the stream's mailbox");
                time.Advance(options.UnpackTimeout);
                (await PickupE2E.Within(move)).Should().BeTrue();
                stream.Current.Error.Should().BeOfType<TimeoutException>("Mallory's message fails at its budget");
                move = PickupE2E.MoveNext(stream);
                if (i == perInterval)
                {
                    // Fix round 3: the cycle came due (one poll interval on the client's clock); its
                    // drain round runs before the stream takes Mallory's waiting push, so Alice's due
                    // message is the next item.
                    (await PickupE2E.Within(move)).Should().BeTrue();
                    PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, delayed);
                    move = PickupE2E.MoveNext(stream);
                }
                await slow.EnteredAsync(i + 1);
            }

            observer.KindsFrom(bob.Did).Count(k => k == "live-on").Should().BeGreaterThan(liveOnBefore, "the stream's cycle came due and re-armed live mode");
            Drains(observer, bob.Did).Should().BeGreaterThan(drainsBefore,
                "the stream must drain at least once per poll interval; after three intervals Alice's due message is still never asked for");
        }
        finally
        {
            await PickupE2E.StopAsync(stream, move, cts);
        }
    }

    /// <summary>
    /// ADV3-6 (defect, Medium; fixed by #111: the client remembers messages that were slow to open,
    /// per mailbox, and holds them back while their pause lasts). Mallory, whose DID never resolves,
    /// queues sixteen messages ahead of Alice's (well inside <c>MaxQueueMessages</c>, 100): more than
    /// the three fetches of <c>limit: 4</c> that fit in one of the mediator's leases (60 s) can pass
    /// over. A loop of <c>FetchAsync(limit: 4)</c> until drained reaches Alice's message within
    /// <c>ceil(16 / 4)</c> = 4 fetches that each take one budget (80 s, 1.3 leases); the other fetches
    /// meet only messages the client remembers, take no time and open nothing. Each fetch waits until
    /// it completes or its four new messages are being opened (a remembered message is never opened,
    /// so it never reaches the seam); only then does the client's clock move, by one budget, and the
    /// mediator's clock moves by the client's elapsed time, nothing else.
    /// </summary>
    [Fact]
    public async Task ADV3_6_a_fetch_until_drained_loop_reaches_the_message_behind_more_slow_messages_than_one_lease_of_fetches_passes_within_the_bound()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var time = new QaManualTimeProvider();
        var slow = new SlowSeam();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.Time = time;
            o.ConfigureServices = services => services.AddSingleton(sp =>
            {
                var client = sp.GetRequiredService<DidCommClient>();
                return new MediatorClientChannel(client, new MediatorClientOptions { MessagePickupVersion = MessagePickupVersion.V3 }, receivesInbound: false, time, sp.GetService<ILoggerFactory>(),
                    send: null, resolveEndpoint: null, unpack: (text, ct) => slow.UnpackAsync(client, text, ct));
            });
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var mallory = await harness.NewAliceAsync();
        const int limit = 4;
        const int slowMessages = 4 * limit;
        for (var i = 0; i < slowMessages; i++)
            await PickupE2E.SendAsync(mediator, mallory, recipient, "slow");
        var behind = await PickupE2E.SendAsync(mediator, alice, recipient, "behind");
        var budget = harness.Channel.Options.UnpackTimeout;
        var lease = new DidComm.Mediator.Pickup.MessagePickupOptions().LeaseTimeout;
        const int bound = slowMessages / limit; // ceil(16 / min(limit, 16))
        (bound * budget).Should().BeGreaterThan(lease, "the case under test: more slow messages than the loop passes over within one lease");

        var got = new List<PickupItem>();
        var learning = 0;
        var fetches = 0;
        var elapsed = TimeSpan.Zero;
        while (!got.Any(i => i.Unpacked?.Message.Id == behind.Id) && learning <= bound && fetches < 10 * bound)
        {
            var started = time.GetUtcNow();
            var entered = slow.Entered;
            var fetch = harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit);
            await MediatorTestServer.WaitUntilAsync(() => fetch.IsCompleted || slow.Entered >= entered + limit, "the fetch completes, or its four new messages are being opened");
            if (!fetch.IsCompleted)
            {
                learning++;
                time.Advance(budget);
            }
            var batch = await PickupE2E.Within(fetch);
            got.AddRange(batch.Items);
            fetches++;
            var spent = time.GetUtcNow() - started;
            elapsed += spent;
            mediator.Clock.Advance(spent); // the mediator's clock moves only by the client's elapsed time
        }

        got.Should().Contain(i => i.Unpacked != null && i.Unpacked.Message.Id == behind.Id,
            "a loop that fetches until drained reaches Alice's message within {0} fetches that take time; after {1} ({2} fetches, {3}) it had seen only failures", bound, learning, fetches, elapsed);
        learning.Should().BeLessThanOrEqualTo(bound);
        elapsed.Should().BeLessThanOrEqualTo(bound * budget, "{0} budgets, {1:0.##} leases", bound, bound * budget / lease);
        slow.Entered.Should().Be(slowMessages, "each slow message is opened once, then held back while its pause lasts");
    }

    /// <summary>
    /// ADV3-6 control (passes): with fewer slow messages than three fetches hold (two fetches' worth),
    /// the same loop gets past them to Alice's message inside the first lease, the fetches passing
    /// over messages the mediator still has leased to the client.
    /// </summary>
    [Fact]
    public async Task With_fewer_slow_messages_than_three_fetches_hold_the_loop_reaches_the_message_behind_them()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var time = new QaManualTimeProvider();
        var slow = new SlowSeam();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.Time = time;
            o.ConfigureServices = services => services.AddSingleton(sp =>
            {
                var client = sp.GetRequiredService<DidCommClient>();
                return new MediatorClientChannel(client, new MediatorClientOptions { MessagePickupVersion = MessagePickupVersion.V3 }, receivesInbound: false, time, sp.GetService<ILoggerFactory>(),
                    send: null, resolveEndpoint: null, unpack: (text, ct) => slow.UnpackAsync(client, text, ct));
            });
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var mallory = await harness.NewAliceAsync();
        const int limit = 4;
        for (var i = 0; i < 2 * limit; i++)
            await PickupE2E.SendAsync(mediator, mallory, recipient, "slow");
        var behind = await PickupE2E.SendAsync(mediator, alice, recipient, "behind");
        var budget = harness.Channel.Options.UnpackTimeout;

        for (var f = 0; f < 2; f++)
        {
            var fetch = harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit);
            await slow.EnteredAsync(limit * (f + 1));
            time.Advance(budget);
            (await PickupE2E.Within(fetch)).Items.Should().OnlyContain(i => i.Error is TimeoutException);
            mediator.Clock.Advance(budget);
        }
        var third = await PickupE2E.Within(harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit));

        PickupE2E.ShouldBeAlicesNote(third.Items.Should().ContainSingle().Subject, alice, recipient, behind);
    }

    /// <summary>
    /// Held (the transport capability check): a <c>ws://</c> route when the WebSocket transport allows
    /// only <c>wss</c> (its default). No transport handles the scheme, so the facade "cannot tell" and
    /// the route is allowed — but the send itself refuses at once with a
    /// <see cref="DidComm.Exceptions.TransportException"/>: never a silent wait for an answer that
    /// cannot come. Nothing is connected and nothing stays pending.
    /// </summary>
    [Fact]
    public async Task A_ws_route_with_a_wss_only_transport_fails_at_once_and_leaves_nothing_pending()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.UseHttp = false;
            o.WebSocket = w => w.AllowedSchemes = new[] { "wss" };
            o.Client = c => c.RequestTimeout = TimeSpan.FromMinutes(10);
        });
        var bob = await harness.NewPeerAsync();

        var thrown = await Record.ExceptionAsync(() => harness.Pickup.GetStatusAsync(mediator.Did, bob.Did, serviceEndpointOverride: harness.WebSocketEndpoint).WaitAsync(MediatorTestServer.Timeout));

        thrown.Should().BeOfType<DidComm.Exceptions.TransportException>();
        harness.Connects.Should().Be(0);
        harness.Channel.PendingCount.Should().Be(0);
    }

    private static int Drains(PickupRequestObserver observer, string bob) => observer.KindsFrom(bob).Count(k => k == "delivery-request");

    private static int Buffered(MediatorClientChannel channel)
        => ((ConcurrentDictionary<string, LiveRegistration>)LiveRegistry.GetValue(channel)!).Values.Sum(r => r.Pushes.Count);

    /// <summary>The real unpack; a message whose content is "slow" then waits until its budget cancels it.</summary>
    private sealed class SlowSeam
    {
        private int _entered;

        public async Task<DeliveredUnpack> UnpackAsync(DidCommClient client, string text, CancellationToken ct)
        {
            var unpacked = await client.UnpackDeliveredAsync(text, ct);
            if (unpacked.Result.Message.Body?["content"]?.GetValue<string>() == "slow")
            {
                Interlocked.Increment(ref _entered);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return unpacked;
        }

        public int Entered => Volatile.Read(ref _entered);

        public Task EnteredAsync(int count)
            => MediatorTestServer.WaitUntilAsync(() => Volatile.Read(ref _entered) >= count, $"{count} slow message(s) being opened");
    }
}
