using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.InteropTests.Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #98 FX-1 (PR #130 review P1): the decisions of <see cref="HttpResponseAcknowledgement"/> under a
/// deadline, on a bare <see cref="DefaultHttpContext"/> whose abort is recorded, with the deadline on
/// a hand-moved clock. The endpoint-level behaviour is in <c>MediatorAcknowledgementDeadlineTests</c>
/// (TestServer) and <c>MediatorAcknowledgementDeadlineKestrelTests</c> (Kestrel); these pin the orders
/// those cannot produce on demand.
/// </summary>
public sealed class HttpResponseAcknowledgementTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public void A_write_that_finished_wins_over_the_deadline_even_before_its_continuation_marked_it_handed_over()
    {
        var (context, lifetime) = NewContext();
        var time = new QaManualTimeProvider();
        var acknowledgement = HttpResponseAcknowledgement.Start(context);
        acknowledgement.WithDeadline(Results.StatusCode(StatusCodes.Status202Accepted), time, Deadline, new ResponseDeadlineLog(), logger: null);

        // The server took the whole response, but the result's continuation has not run yet.
        acknowledgement.Writing(Task.CompletedTask);
        time.Advance(Deadline);
        var abortsAtTheDeadline = lifetime.Aborts;
        var statusAtTheDeadline = acknowledgement.Acknowledged.Status;
        acknowledgement.HandedOver();

        abortsAtTheDeadline.Should().Be(0, "a response the server has taken is never aborted");
        statusAtTheDeadline.Should().NotBe(TaskStatus.Canceled);
        acknowledgement.Acknowledged.Status.Should().Be(TaskStatus.RanToCompletion);
        time.ScheduledTimers.Should().Be(0);
    }

    [Fact]
    public void A_write_still_pending_at_the_deadline_is_aborted_and_a_late_hand_over_cannot_acknowledge_it()
    {
        var (context, lifetime) = NewContext();
        var time = new QaManualTimeProvider();
        var acknowledgement = HttpResponseAcknowledgement.Start(context);
        acknowledgement.WithDeadline(Results.StatusCode(StatusCodes.Status202Accepted), time, Deadline, new ResponseDeadlineLog(), logger: null);
        var pending = new TaskCompletionSource();
        acknowledgement.Writing(pending.Task);

        time.Advance(Deadline - TimeSpan.FromTicks(1));
        var abortsBefore = lifetime.Aborts;
        time.Advance(TimeSpan.FromTicks(1));
        pending.SetResult(); // the server lets go of the write after the abort
        acknowledgement.HandedOver();

        abortsBefore.Should().Be(0);
        lifetime.Aborts.Should().Be(1);
        acknowledgement.Acknowledged.Status.Should().Be(TaskStatus.Canceled, "the deadline decided first");
        time.ScheduledTimers.Should().Be(0);
    }

    [Fact]
    public void Once_handed_over_neither_the_deadline_nor_a_later_abort_takes_the_acknowledgement_back()
    {
        var (context, lifetime) = NewContext();
        var time = new QaManualTimeProvider();
        var acknowledgement = HttpResponseAcknowledgement.Start(context);
        acknowledgement.WithDeadline(Results.StatusCode(StatusCodes.Status202Accepted), time, Deadline, new ResponseDeadlineLog(), logger: null);
        acknowledgement.Writing(Task.CompletedTask);

        acknowledgement.HandedOver();
        var timersAfterHandingOver = time.ScheduledTimers;
        lifetime.Abort(); // the client closes its connection while later completion callbacks run
        time.Advance(Deadline * 2);

        timersAfterHandingOver.Should().Be(0, "handing over disposes the deadline");
        acknowledgement.Acknowledged.Status.Should().Be(TaskStatus.RanToCompletion);
        lifetime.Aborts.Should().Be(1, "only the client's own abort");
    }

    [Fact]
    public void An_abort_before_the_hand_over_cancels_and_the_hand_over_then_changes_nothing()
    {
        var (context, lifetime) = NewContext();
        var time = new QaManualTimeProvider();
        var acknowledgement = HttpResponseAcknowledgement.Start(context);
        acknowledgement.WithDeadline(Results.StatusCode(StatusCodes.Status202Accepted), time, Deadline, new ResponseDeadlineLog(), logger: null);

        lifetime.Abort();
        acknowledgement.Writing(Task.CompletedTask);
        acknowledgement.HandedOver();
        time.Advance(Deadline);

        acknowledgement.Acknowledged.Status.Should().Be(TaskStatus.Canceled);
        lifetime.Aborts.Should().Be(1, "the deadline was disposed by the abort and never fired");
        time.ScheduledTimers.Should().Be(0);
    }

    private static (DefaultHttpContext Context, RecordingLifetime Lifetime) NewContext()
    {
        var lifetime = new RecordingLifetime();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        return (context, lifetime);
    }

    /// <summary>A request lifetime whose abort cancels <c>RequestAborted</c> at once (as the test server does) and is counted.</summary>
    private sealed class RecordingLifetime : IHttpRequestLifetimeFeature
    {
        private readonly CancellationTokenSource _aborted = new();
        private int _aborts;

        public CancellationToken RequestAborted
        {
            get => _aborted.Token;
            set => throw new NotSupportedException();
        }

        public int Aborts => Volatile.Read(ref _aborts);

        public void Abort()
        {
            Interlocked.Increment(ref _aborts);
            _aborted.Cancel();
        }
    }
}
