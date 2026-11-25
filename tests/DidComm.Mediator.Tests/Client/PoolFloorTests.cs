using AwesomeAssertions;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="PoolFloor"/>, the pool floor of the unpacker's tests that park thread-pool threads
/// (L-065 corollary). Test classes run side by side, so two such tests can park their threads at
/// the same time: each must find its threads there, or it stalls itself and every test beside it
/// until the pool has added them, seconds apart.
/// </summary>
public sealed class PoolFloorTests
{
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Two holders, as two tests running side by side, each parking 64 pool threads on a gate: all
    /// 128 are parked within the hang guard, and the floor covers what both park. (A floor that
    /// covers only the larger of the two requests leaves about half of the second holder's threads
    /// to be added by the pool one at a time.)
    /// </summary>
    [Fact]
    public void Two_tests_that_park_pool_threads_at_the_same_time_each_find_their_threads()
    {
        const int each = 64;
        using var gate = new ManualResetEventSlim(false);
        using var parked = new CountdownEvent(2 * each);
        using var first = new PoolFloor(parked: each);
        using var second = new PoolFloor(parked: each);
        try
        {
            for (var i = 0; i < 2 * each; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static state =>
                {
                    var (arrived, release) = ((CountdownEvent, ManualResetEventSlim))state!;
                    arrived.Signal();
                    release.Wait(TimeSpan.FromSeconds(30)); // parks its thread until the test lets it go (bounded so a red run cannot hang)
                }, (parked, gate));
            }

            parked.Wait(RealTime).Should().BeTrue("all {0} threads are parked at once: {1} still missing", 2 * each, parked.CurrentCount);
            ThreadPool.GetMinThreads(out var floor, out _);
            floor.Should().BeGreaterThanOrEqualTo(Environment.ProcessorCount + 2 * each, "the floor covers what both park");
            PoolFloor.ParkedNow.Should().BeGreaterThanOrEqualTo(2 * each, "both holders are counted");
        }
        finally
        {
            gate.Set();
        }
    }
}
