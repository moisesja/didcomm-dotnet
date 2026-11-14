using System.Text;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// ADV-2 red-team pass on the delivered-message unpacker (#96): what a sender whose DID resolves
/// slowly costs the recipient. Every timing is driven by the injected <see cref="ManualTimeProvider"/>
/// and rendezvous on scheduled timers (L-044); nothing sleeps.
/// </summary>
public sealed class DeliveredUnpackAdv2Tests
{
    private static readonly TimeSpan UnpackTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);

    /// <summary>
    /// ADV2-1 (fails: defect). Property broken — <see cref="Client.PickupClient.FetchAsync"/> remarks:
    /// "a loop that fetches until drained gets past failures to what is behind them". Attack: a sender
    /// (any outsider who can address the recipient through its mediator) sends ten messages whose
    /// sender DID resolves only when cancelled (a did:webvh host that holds the connection). One
    /// fetch of limit 10 unpacks them four at a time, each until <c>UnpackTimeout</c>, so the batch
    /// needs three waves (90 s by default) — longer than this library's mediator lease
    /// (<see cref="MessagePickupOptions.LeaseTimeout"/>, 60 s). Every lease has ended before the
    /// fetch returns, so the next fetch is handed the same ten failures again, and the messages
    /// queued behind them are never reached. The test asserts the claimed property: the batch is
    /// done by the time the lease ends.
    /// </summary>
    [Fact]
    public async Task ADV2_1_a_batch_of_stalling_senders_outlasts_the_mediators_lease()
    {
        var time = new ManualTimeProvider();
        var unpacker = new DeliveredMessageUnpacker(StallUntilCancelled, UnpackTimeout, time);
        var lease = new MessagePickupOptions().LeaseTimeout;
        var payloads = Enumerable.Range(0, 10).Select(i => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("stall-" + i)).ToArray();

        var batch = unpacker.UnpackAllAsync(payloads, default);
        var elapsed = TimeSpan.Zero;
        try
        {
            // Wave by wave: each wave's deadlines are scheduled, then the clock passes them.
            while (!batch.IsCompleted && elapsed < lease)
            {
                await time.WaitForScheduledTimersAsync(1, RealTime);
                time.Advance(UnpackTimeout);
                elapsed += UnpackTimeout;
                await Task.Yield();
            }

            batch.IsCompleted.Should().BeTrue(
                "a fetch must finish failing its messages within the mediator's lease ({0}), or the next fetch gets the same failures and never reaches what is queued behind them", lease);
        }
        finally
        {
            // Let the rest of the batch end, so nothing outlives the test.
            while (!batch.IsCompleted)
            {
                await time.WaitForScheduledTimersAsync(1, RealTime);
                time.Advance(UnpackTimeout);
                await Task.Yield();
            }
            await batch.WaitAsync(RealTime);
        }
    }

    /// <summary>
    /// ADV2-1 companion (passes: pins the arithmetic the finding rests on). Ten stalling messages
    /// need exactly three deadlines of clock time with four unpacks at a time.
    /// </summary>
    [Fact]
    public async Task ADV2_1_ten_stalling_messages_take_three_unpack_timeouts()
    {
        var time = new ManualTimeProvider();
        var unpacker = new DeliveredMessageUnpacker(StallUntilCancelled, UnpackTimeout, time);
        var payloads = Enumerable.Range(0, 10).Select(i => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("stall-" + i)).ToArray();

        var batch = unpacker.UnpackAllAsync(payloads, default);
        var waves = 0;
        while (!batch.IsCompleted)
        {
            await time.WaitForScheduledTimersAsync(1, RealTime);
            time.Advance(UnpackTimeout);
            waves++;
            await Task.Yield();
            if (waves > 10)
                break;
        }
        var outcomes = await batch.WaitAsync(RealTime);

        waves.Should().Be(3);
        outcomes.Should().HaveCount(10).And.OnlyContain(o => !o.Succeeded && o.Error is TimeoutException);
        unpacker.Abandoned.Should().Be(0, "an unpack that honours cancellation gives its slot back");
    }

    /// <summary>
    /// ADV2-2 (fails: defect, Low). Property broken — L-061 ("isolate per peer before bounding"):
    /// the abandoned-unpack bound is one counter per container, not per sender. Attack: a sender
    /// whose DID resolution ignores cancellation (a caching resolver whose shared lookup does not
    /// take the caller's token) sends 16 messages; each is abandoned at its deadline and keeps its
    /// slot. A message from another sender, which would unpack at once, then fails as "unpack
    /// capacity exhausted" — for every local DID and tenant of the container, until the stalled
    /// lookups end. The test asserts that the other sender's message still unpacks.
    /// </summary>
    [Fact]
    public async Task ADV2_2_one_senders_stalled_lookups_refuse_every_other_senders_message()
    {
        var time = new ManualTimeProvider();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var alice = FakePickupExchange.Authcrypt(FakePickupExchange.A, FakePickupExchange.L, "alice-1");
        var unpacker = new DeliveredMessageUnpacker(
            (text, _) => text.StartsWith("mallory", StringComparison.Ordinal)
                ? never.Task // ignores the token
                : Task.FromResult(new DeliveredUnpack(alice, text, 0)),
            UnpackTimeout,
            time);
        try
        {
            for (var i = 0; i < DeliveredMessageUnpacker.MaxAbandoned; i++)
            {
                var stalled = unpacker.UnpackAsync(Encoding.UTF8.GetBytes("mallory-" + i), default);
                await time.WaitForScheduledTimersAsync(1, RealTime);
                time.Advance(UnpackTimeout);
                (await stalled.WaitAsync(RealTime)).Succeeded.Should().BeFalse();
            }
            unpacker.Abandoned.Should().Be(DeliveredMessageUnpacker.MaxAbandoned);

            var other = await unpacker.UnpackAsync(Encoding.UTF8.GetBytes("alice-1"), default).WaitAsync(RealTime);

            other.Succeeded.Should().BeTrue("another sender's message does not depend on the stalled sender's lookups");
        }
        finally
        {
            never.TrySetCanceled();
        }
    }

    private static async Task<DeliveredUnpack> StallUntilCancelled(string text, CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        throw new InvalidOperationException("unreachable");
    }
}
