using DidComm.Transports;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Transports;

/// <summary>
/// The per-message time limit shared by the WebSocket receive loops. Its <c>Dispose</c> runs first in
/// the loops' cleanup, before the session is unregistered and the socket closed, so it must never
/// throw, even when the host's clock hands out a timer that fails to dispose.
/// </summary>
public sealed class ReceiveDeadlineTests
{
    [Fact]
    public void Disposing_a_deadline_whose_host_timer_throws_on_dispose_does_not_throw()
    {
        var deadline = ReceiveDeadline.Start(new ThrowingDisposeClock(), TimeSpan.FromSeconds(37));
        deadline.Should().NotBeNull();

        var thrown = Record.Exception(() => deadline!.Dispose());

        thrown.Should().BeNull();
    }

    [Fact]
    public void An_infinite_limit_creates_no_timer()
    {
        var clock = new ThrowingDisposeClock();

        var deadline = ReceiveDeadline.Start(clock, Timeout.InfiniteTimeSpan);

        deadline.Should().BeNull();
        clock.TimersCreated.Should().Be(0);
    }

    /// <summary>A clock whose timers never fire and throw when disposed.</summary>
    private sealed class ThrowingDisposeClock : TimeProvider
    {
        public int TimersCreated { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimersCreated++;
            return new ThrowingTimer();
        }

        private sealed class ThrowingTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => throw new InvalidOperationException("host timer fault");

            public ValueTask DisposeAsync() => throw new InvalidOperationException("host timer fault");
        }
    }
}
