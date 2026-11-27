using AwesomeAssertions;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="PoolFloor"/>, the pool floor of the unpacker's tests that park thread-pool threads
/// (L-065 corollary). Test classes run side by side, so two such tests can park their threads at
/// the same time: the floor must cover what both park, or the second waits for threads the pool
/// adds seconds apart, and every test beside it with it.
/// </summary>
/// <remarks>
/// What is checked is what each holder asks for, and that the pool's floor is at least that. The
/// holders here are counted in a tally of the test's own, so what other tests hold at the same time
/// is not in the figures; their raises can only lift the pool's floor further. Nothing is parked
/// here: whether the pool then hands out the threads in time is the runtime's and the machine's
/// doing, not this class's.
/// </remarks>
public sealed class PoolFloorTests
{
    /// <summary>
    /// Two holders, as two tests running side by side that each park 64 pool threads: the second
    /// asks for its own 64 and the first's, and the pool's floor is at least what it asked for. Once
    /// the first is done (once, however often it is disposed), a third asks for the second's and
    /// its own, no longer for the first's.
    /// </summary>
    [Fact]
    public void A_holder_asks_for_what_every_holder_parks_at_that_moment_and_the_floor_is_at_least_that()
    {
        const int each = 64;
        var spare = Environment.ProcessorCount + 8;
        var tally = new PoolFloor.Tally();
        using var first = new PoolFloor(each, tally);
        using var second = new PoolFloor(each, tally);

        first.Asked.Should().Be(each + spare);
        second.Asked.Should().Be(2 * each + spare, "the second holder asks for its own threads and the first's: both park at the same time");
        tally.Parked.Should().Be(2 * each);
        ThreadPool.GetMinThreads(out var floor, out _);
        floor.Should().BeGreaterThanOrEqualTo(second.Asked, "the pool's floor is at least what the latest holder asked for");

        first.Dispose();
        first.Dispose();
        tally.Parked.Should().Be(each, "a holder that is done no longer counts, and is taken off once");
        using var third = new PoolFloor(each, tally);
        third.Asked.Should().Be(2 * each + spare, "the second's threads and its own");
    }
}
