using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Protocols;
using DidComm.Transports;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// No task of the #96 client's own faults unobserved (L-039, QA Q1-3 / Q2-3): a pending request
/// failed by the HTTP no-answer rule or by disposal that no one awaits; a disposed live stream's push
/// queue; an unpack whose wait passed its deadline, still running or already ended. Each case runs in
/// its own frame and is then collected, and a unique marker keeps tests running in parallel from
/// being counted here.
/// </summary>
public sealed class MediatorClientUnobservedTests : IAsyncLifetime
{
    private ClientWorld _w = null!;

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    // ---- the pending request (the root cause) ------------------------------------------------

    [Fact]
    public async Task A_failed_pending_request_nobody_awaits_is_not_reported_and_one_that_is_awaited_still_throws_its_failure()
    {
        var marker = Marker();
        var seen = await CollectUnobservedAsync(marker, () =>
        {
            FailUnawaited(marker);
            return Task.CompletedTask;
        });
        seen.Should().BeEmpty("a failure the exchange threw itself leaves nothing for the finalizer");

        var awaited = Pending();
        var failure = new TimeoutException(marker);
        awaited.TryFail(failure).Should().BeTrue();
        (await FluentActions.Awaiting(() => awaited.Completion.Task).Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(failure);
        awaited.TryFail(new TimeoutException()).Should().BeFalse("decided once");
    }

    [Fact]
    public async Task An_HTTP_response_without_an_answer_fails_the_request_and_leaves_nothing_unobserved()
    {
        var send = new FakeSend(); // accepted, nothing returned: an HTTP 202
        await using var channel = Create(_w, send);
        var ids = new ConcurrentBag<string>();

        var seen = await CollectUnobservedAsync(null, async () =>
        {
            for (var i = 0; i < 5; i++)
                ids.Add(await HttpNoAnswerAsync(channel));
        });

        seen.Where(e => ids.Any(id => e.Message.Contains(id, StringComparison.Ordinal))).Should().BeEmpty();
        channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Disposal_during_a_send_and_during_the_wait_for_an_answer_leaves_nothing_unobserved()
    {
        var seen = await CollectUnobservedAsync(null, () => DisposeDuringSendAndWaitAsync(_w));

        seen.OfType<ObjectDisposedException>().Where(e => e.ObjectName == nameof(MediatorClientChannel))
            .Should().BeEmpty("the callers saw the disposal; nothing is left for the finalizer");
    }

    // ---- the live stream's push queue --------------------------------------------------------

    [Fact]
    public async Task A_push_queue_ended_with_an_error_leaves_nothing_unobserved_whether_it_was_empty_or_drained_later()
    {
        var marker = Marker();

        var seen = await CollectUnobservedAsync(marker, async () =>
        {
            EndEmpty(marker);
            await EndBufferedThenDrainAsync(marker);
        });

        seen.Should().BeEmpty("the queue's completion is the channel library's, which observes an error it completes with");
    }

    [Fact]
    public async Task A_reader_waiting_on_the_push_queue_sees_the_error_it_ended_with()
    {
        using var registration = new LiveRegistration("did:example:mediator", "did:example:local", onDispose: null);
        var wait = registration.Pushes.WaitToReadAsync().AsTask();
        var error = new ObjectDisposedException(nameof(MediatorClientChannel));

        registration.Complete(error);

        (await FluentActions.Awaiting(() => wait).Should().ThrowAsync<ObjectDisposedException>()).Which.Should().BeSameAs(error);
        registration.IsEnded.Should().BeTrue();
        registration.IsDisposed.Should().BeFalse("Complete ends the queue without removing the entry");
    }

    // ---- the unpack limiter ------------------------------------------------------------------

    [Fact]
    public async Task An_unpack_whose_wait_passed_its_deadline_is_observed_whether_it_ends_later_or_had_ended_with_it()
    {
        var marker = Marker();
        var time = new ManualTimeProvider();

        var seen = await CollectUnobservedAsync(marker, async () =>
        {
            await FaultAfterTheDeadlineAsync(time, marker);
            await FaultAsTheDeadlineFiresAsync(time, marker);
        });

        seen.Should().BeEmpty("an abandoned unpack's fault is observed, whenever it comes");
    }

    [Fact]
    public async Task An_unpack_that_ended_as_its_deadline_fired_gives_its_abandoned_slot_back_at_once()
    {
        var time = new ManualTimeProvider();
        var unpacker = new DeliveredMessageUnpacker(FaultsWhenCancelled("ended with the deadline"), TimeSpan.FromSeconds(1), time);

        var outcome = unpacker.UnpackAsync(new byte[] { (byte)'x' }, default);
        await time.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        time.Advance(TimeSpan.FromSeconds(1));

        (await outcome.WaitAsync(PickupTest.Timeout)).Error.Should().BeOfType<TimeoutException>();
        unpacker.Abandoned.Should().Be(0, "nothing still runs");
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static string Marker() => "unobserved-marker-" + Guid.NewGuid().ToString("N");

    private static PendingRequest Pending()
        => new(Guid.NewGuid().ToString(), ClientSpecs.StatusRequest, "did:example:mediator", "did:example:local", 0);

    // Collect what reaches UnobservedTaskException while running act and collecting afterwards:
    // only exceptions whose text carries the marker (or every one, for a null marker).
    private static async Task<IReadOnlyList<Exception>> CollectUnobservedAsync(string? marker, Func<Task> act)
    {
        var seen = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var inner in e.Exception.Flatten().InnerExceptions)
            {
                if (marker is null || inner.ToString().Contains(marker, StringComparison.Ordinal))
                    seen.Enqueue(inner);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await act();
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return seen.ToArray();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FailUnawaited(string marker)
    {
        for (var i = 0; i < 5; i++)
            Pending().TryFail(new TimeoutException(marker)).Should().BeTrue();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<string> HttpNoAnswerAsync(MediatorClientChannel channel)
    {
        var request = _w.Request();
        await FluentActions.Awaiting(() => channel.ExchangeAsync(HttpRoute, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default))
            .Should().ThrowAsync<TimeoutException>();
        return request.Id;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task DisposeDuringSendAndWaitAsync(ClientWorld w)
    {
        // The first request is still in its send (a send that honours its token); the second waits
        // for its answer. Disposal fails both pending requests and ends the first one's send.
        var inSend = new FakeSend { Respond = (_, _, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith<SendResult>(_ => throw new OperationCanceledException(ct), TaskScheduler.Default) };
        var channel = Create(w, inSend);
        var sending = channel.ExchangeAsync(WebSocketRoute, w.Request(), ClientSpecs.StatusRequest, w.Mediator.Did, w.Local.Did, default);
        await inSend.FirstCall.WaitAsync(PickupTest.Timeout);
        inSend.Respond = (_, options, _) => Task.FromResult(FakeSend.Accepted(options.ServiceEndpointOverride!));
        var waiting = channel.ExchangeAsync(WebSocketRoute, w.Request(), ClientSpecs.StatusRequest, w.Mediator.Did, w.Local.Did, default);

        channel.Dispose();

        await FluentActions.Awaiting(() => sending.WaitAsync(PickupTest.Timeout)).Should().ThrowAsync<ObjectDisposedException>();
        await FluentActions.Awaiting(() => waiting.WaitAsync(PickupTest.Timeout)).Should().ThrowAsync<ObjectDisposedException>();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EndEmpty(string marker)
    {
        var registration = new LiveRegistration("did:example:mediator", "did:example:local", onDispose: null);
        registration.Complete(new ObjectDisposedException(marker));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task EndBufferedThenDrainAsync(string marker)
    {
        var registration = new LiveRegistration("did:example:mediator", "did:example:local", onDispose: null);
        registration.TryPush(await _w.SnapshotAsync(_w.Delivery(null))).Should().BeTrue();
        registration.Complete(new ObjectDisposedException(marker));
        registration.Pushes.TryRead(out _).Should().BeTrue("a buffered push is still read after the end");
        registration.Pushes.TryRead(out _).Should().BeFalse();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task FaultAfterTheDeadlineAsync(ManualTimeProvider time, string marker)
    {
        var work = new TaskCompletionSource<DeliveredUnpack>(); // ignores its token
        var unpacker = new DeliveredMessageUnpacker((_, _) => work.Task, TimeSpan.FromSeconds(1), time);
        var outcome = unpacker.UnpackAsync(new byte[] { (byte)'x' }, default);
        await time.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        time.Advance(TimeSpan.FromSeconds(1));
        (await outcome.WaitAsync(PickupTest.Timeout)).Error.Should().BeOfType<TimeoutException>();
        unpacker.Abandoned.Should().Be(1, "it still runs");

        work.SetException(new InvalidOperationException(marker));
        unpacker.Abandoned.Should().Be(0, "it ended");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task FaultAsTheDeadlineFiresAsync(ManualTimeProvider time, string marker)
    {
        var unpacker = new DeliveredMessageUnpacker(FaultsWhenCancelled(marker), TimeSpan.FromSeconds(1), time);
        var outcome = unpacker.UnpackAsync(new byte[] { (byte)'x' }, default);
        await time.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        AdvanceWithoutInlining(time, TimeSpan.FromSeconds(1));
        (await outcome.WaitAsync(PickupTest.Timeout)).Error.Should().BeOfType<TimeoutException>();
    }

    // Fire the deadline under a context the wait's continuation may not run inline on, so it is
    // queued and the work, faulted inside the same cancellation, has usually ended by the time the
    // wait looks at it. The test holds whatever the order; this makes the ended-first order likely.
    private static void AdvanceWithoutInlining(ManualTimeProvider time, TimeSpan by)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new NoInlining());
        try
        {
            time.Advance(by);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class NoInlining : SynchronizationContext
    {
    }

    // An unpack that faults from inside the deadline's own cancellation (it never completes as
    // cancelled), so it may already have ended by the time the wait sees the deadline.
    private static Func<string, CancellationToken, Task<DeliveredUnpack>> FaultsWhenCancelled(string text)
        => (_, ct) =>
        {
            var work = new TaskCompletionSource<DeliveredUnpack>();
            ct.UnsafeRegister(_ => work.TrySetException(new InvalidOperationException(text)), null);
            return work.Task;
        };
}
