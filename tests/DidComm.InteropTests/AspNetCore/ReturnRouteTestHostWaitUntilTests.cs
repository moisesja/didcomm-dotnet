using System.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// The shared wait helper (#119): <see cref="ReturnRouteTestHost.WaitUntilAsync(Func{bool}, TimeSpan)"/>,
/// which every interop test's hang guards use (directly and through <c>MediatorTestServer.WaitUntilAsync</c>).
/// Its deadline was read from the wall clock, which jumps ahead across a machine sleep while the
/// monotonic clock and the product's timers do not, so every wait in progress returned false on wake
/// though its condition was about to hold (seen as fast failures: a 297 s sleep, 1.9 s of test time).
/// </summary>
public sealed class ReturnRouteTestHostWaitUntilTests
{
    [Fact]
    public async Task A_wall_clock_jump_during_the_wait_does_not_end_it()
    {
        var time = new SleepingClock(TimeSpan.FromSeconds(300));
        var checks = 0;

        // The condition holds on its fifth check; the wall clock jumps 300 s at the helper's second
        // reading of it, while the wait is in progress (as on wake from a sleep).
        var held = await ReturnRouteTestHost.WaitUntilAsync(() => Interlocked.Increment(ref checks) >= 5, TimeSpan.FromSeconds(15), time);

        held.Should().BeTrue("the wait is measured on the monotonic clock, which a sleep does not move");
        checks.Should().Be(5);
    }

    [Fact]
    public async Task A_condition_that_never_holds_still_ends_the_wait_once_the_timeout_passed_on_the_monotonic_clock()
    {
        var watch = Stopwatch.StartNew();

        var held = await ReturnRouteTestHost.WaitUntilAsync(() => false, TimeSpan.FromMilliseconds(200));

        held.Should().BeFalse();
        watch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(200), "a hang guard, never shorter than asked");
    }

    // The system's monotonic timestamps; a wall clock that jumps ahead by `jump` once, from its second reading on.
    private sealed class SleepingClock(TimeSpan jump) : TimeProvider
    {
        private int _readings;

        public override long TimestampFrequency => System.TimestampFrequency;

        public override long GetTimestamp() => System.GetTimestamp();

        public override DateTimeOffset GetUtcNow()
            => Interlocked.Increment(ref _readings) >= 2 ? System.GetUtcNow() + jump : System.GetUtcNow();
    }
}
