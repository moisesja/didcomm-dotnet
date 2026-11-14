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
    /// ADV3-1, end to end (fails: defect, Medium). The property and the root cause are on the unit
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
                return new MediatorClientChannel(client, new MediatorClientOptions(), receivesInbound: true, time, sp.GetService<ILoggerFactory>(),
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
        var rounds = 3 * (int)(options.LivePollInterval.Ticks / options.UnpackTimeout.Ticks); // three poll intervals
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

        public Task EnteredAsync(int count)
            => MediatorTestServer.WaitUntilAsync(() => Volatile.Read(ref _entered) >= count, $"{count} slow message(s) being opened");
    }
}
