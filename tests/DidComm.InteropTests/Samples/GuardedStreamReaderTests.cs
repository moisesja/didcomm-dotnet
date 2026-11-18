using System.Runtime.CompilerServices;
using AwesomeAssertions;
using DidComm.InteropTests.Mediator;
using DidComm.Samples.Shared;
using Xunit;

namespace DidComm.InteropTests.Samples;

/// <summary>
/// PR #123 review (P2): the samples read the live stream one item at a time behind a hang guard.
/// When the guard fires, or the reader is disposed, while a read is still running, the read must
/// be cancelled and must have ENDED before the timeout is reported or the stream is disposed:
/// disposing an async stream mid-read fails and leaves its own clean-up running. The stream here
/// has slow asynchronous clean-up, like <c>LiveDeliveryStream</c>'s, held by a gate the test opens.
/// The hang guard runs on a manual clock; real time is only the test's own hang guard (L-065).
/// </summary>
public sealed class GuardedStreamReaderTests
{
    private static readonly TimeSpan ReaderGuard = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TestGuard = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_timeout_is_reported_only_after_the_cancelled_read_has_finished_its_clean_up()
    {
        var time = new QaManualTimeProvider();
        var stream = new SlowCleanupStream();
        var reader = new GuardedStreamReader<int>(ct => stream.ReadAsync(itemsFirst: 0, ct), ReaderGuard, time);

        var next = reader.NextAsync("an item");
        await stream.Waiting.Task.WaitAsync(TestGuard);
        await time.WaitForScheduledTimersAsync(1, TestGuard); // the reader's hang guard for this read
        time.Advance(ReaderGuard);                            // ... fires

        // The reader cancels the read and waits for it, on a guard timer of its own: wait for that.
        await stream.CancelSeen.Task.WaitAsync(TestGuard);
        await time.WaitForScheduledTimersAsync(1, TestGuard);
        next.IsCompleted.Should().BeFalse("the timeout is reported only once the read has ended");
        stream.CleanupDone.Should().BeFalse();

        stream.CleanupGate.SetResult();
        await FluentActions.Awaiting(() => next).Should().ThrowExactlyAsync<TimeoutException>()
            .WithMessage("Timed out waiting for an item.");
        stream.CleanupDone.Should().BeTrue("the read's clean-up finished before the timeout surfaced");

        await FluentActions.Awaiting(() => reader.DisposeAsync().AsTask()).Should().NotThrowAsync(
            "disposing after the timeout must not mask it");
    }

    [Fact]
    public async Task Disposing_while_a_read_runs_cancels_it_and_waits_for_it_before_disposing_the_stream()
    {
        var time = new QaManualTimeProvider();
        var stream = new SlowCleanupStream();
        var reader = new GuardedStreamReader<int>(ct => stream.ReadAsync(itemsFirst: 0, ct), ReaderGuard, time);

        var next = reader.NextAsync("an item");
        await stream.Waiting.Task.WaitAsync(TestGuard);
        await time.WaitForScheduledTimersAsync(1, TestGuard); // the read's hang guard

        // What sample 11 does when its own wait gives up: dispose the connection mid-read.
        var dispose = reader.DisposeAsync().AsTask();
        await stream.CancelSeen.Task.WaitAsync(TestGuard);
        await time.WaitForScheduledTimersAsync(2, TestGuard); // the reader now waits for the read to end
        dispose.IsCompleted.Should().BeFalse("the stream is not disposed while its read is still running");
        stream.CleanupDone.Should().BeFalse();

        stream.CleanupGate.SetResult();
        await FluentActions.Awaiting(() => dispose).Should().NotThrowAsync();
        stream.CleanupDone.Should().BeTrue();
        await FluentActions.Awaiting(() => next).Should().ThrowAsync<OperationCanceledException>(
            "the running read was cancelled, not abandoned");
    }

    [Fact]
    public async Task With_no_read_running_disposing_ends_the_stream_normally_without_cancelling_it()
    {
        var time = new QaManualTimeProvider();
        var stream = new SlowCleanupStream();
        stream.CleanupGate.SetResult();
        var reader = new GuardedStreamReader<int>(ct => stream.ReadAsync(itemsFirst: 2, ct), ReaderGuard, time);

        (await reader.NextAsync("the first item").WaitAsync(TestGuard)).Should().Be(0);
        (await reader.NextAsync("the second item").WaitAsync(TestGuard)).Should().Be(1);
        await reader.DisposeAsync().AsTask().WaitAsync(TestGuard);

        // Like a break out of await foreach: the live stream acknowledges and turns live delivery
        // off on a normal end, which a cancellation would cut short.
        stream.CleanupDone.Should().BeTrue();
        stream.CancelledAtCleanup.Should().BeFalse();
    }

    /// <summary>
    /// Yields <c>itemsFirst</c> items, then waits until cancelled; its clean-up is asynchronous and
    /// waits for <see cref="CleanupGate"/>.
    /// </summary>
    private sealed class SlowCleanupStream
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancelSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool CleanupDone;
        public volatile bool CancelledAtCleanup;

        public async IAsyncEnumerable<int> ReadAsync(int itemsFirst, [EnumeratorCancellation] CancellationToken ct = default)
        {
            using var registration = ct.Register(() => CancelSeen.TrySetResult());
            try
            {
                for (var i = 0; i < itemsFirst; i++)
                    yield return i;
                Waiting.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                yield return -1;
            }
            finally
            {
                CancelledAtCleanup = ct.IsCancellationRequested;
                await CleanupGate.Task;
                CleanupDone = true;
            }
        }
    }
}
