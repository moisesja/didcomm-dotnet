using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// #111 red-team pass 2, RT2-B: what the memory remembers after fix round 1 (owner decision C,
/// "every failure, fair to late starts"; the pause cap of Q budgets; RT1B-2's single strike per
/// pause). Every timing is driven by a manual clock; real time is only a hang guard.
/// </summary>
public sealed class SlowMemoryRt2BTests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);
    private const string AliceKid = "did:example:alice#key-x25519-1";

    /// <summary>
    /// I-9 (as amended by decision C): nothing is written for a message still running when the
    /// caller cancels. The host's lookup ends at once when its token is cancelled and reports that
    /// as a resolution failure (a resolver that maps every error, its cancellation included, to
    /// "notFound"). The message had been opening for 1 s of its 20 s share when the caller
    /// cancelled. It is not remembered.
    /// </summary>
    [Fact]
    public async Task A_message_running_when_the_caller_cancels_is_not_remembered_when_its_lookup_reports_the_cancellation_as_a_failure()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker((packed, ct) => LookupReportingCancellationAsFailure(calls, packed, ct), Budget, time);
        using var caller = new CancellationTokenSource();

        var delivery = unpacker.UnpackAllAsync(Bob, new[] { Alice("alice-1") }, "request-1", caller.Token);
        await calls.WaitForAsync(1, RealTime);
        time.Advance(TimeSpan.FromSeconds(1));
        caller.Cancel();
        var ended = await Task.WhenAny(delivery, Task.Delay(RealTime));

        ended.Should().BeSameAs(delivery, "the delivery ends when the caller cancels");
        unpacker.Memory.HeldBack(Bob, new[] { "alice-1" }).Should().Equal(new[] { false },
            "it was running when the caller cancelled (the delivery ended {0}); remembered, it would not be opened again for {1} min",
            delivery.Status, unpacker.Memory.FirstPause.TotalMinutes);
    }

    /// <summary>
    /// I-9 (as amended by decision C): nothing is written for a message still running when the
    /// caller cancels, in a delivery of four. Each host lookup ends at once when its token is
    /// cancelled and reports that as a resolution failure. None of the four is remembered.
    /// </summary>
    [Fact]
    public async Task Messages_running_when_the_caller_cancels_are_not_remembered_when_their_lookups_report_the_cancellation_as_a_failure()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker((packed, ct) => LookupReportingCancellationAsFailure(calls, packed, ct), Budget, time);
        using var caller = new CancellationTokenSource();
        var ids = new[] { "alice-1", "alice-2", "alice-3", "alice-4" };

        var delivery = unpacker.UnpackAllAsync(Bob, ids.Select(Alice).ToArray(), "request-1", caller.Token);
        await calls.WaitForAsync(ids.Length, RealTime);
        time.Advance(TimeSpan.FromSeconds(1));
        caller.Cancel();
        var ended = await Task.WhenAny(delivery, Task.Delay(RealTime));

        ended.Should().BeSameAs(delivery, "the delivery ends when the caller cancels");
        unpacker.Memory.Count.Should().Be(0, "all four were running when the caller cancelled (the delivery ended {0})", delivery.Status);
    }

    private static DeliveredInput Alice(string id) => new(id, TestJwe.Bytes(TestJwe.Authcrypt(AliceKid, id)));

    // A host lookup that ends when its token is cancelled and reports it as a resolution failure.
    private static async Task<DeliveredUnpack> LookupReportingCancellationAsFailure(HostCallLog calls, string packed, CancellationToken ct)
    {
        using (calls.Enter(TestJwe.LabelOf(packed), ct))
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                throw new DidResolutionException("did:example:alice", "notFound");
            }
        }
        throw new InvalidOperationException("unreachable");
    }
}
