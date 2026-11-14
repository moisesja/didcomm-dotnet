using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.Logging;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;
using static DidComm.Mediator.Tests.Client.PickupClientLiveStreamTests;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The cycle of <see cref="PickupClient.ReceiveLiveAsync"/> (#96 design §4.8, D-17): how a stream
/// starts (refused before anything is sent over HTTP or for a second stream of the same pair), the
/// order live-on → drain, the periodic cycle on the injected monotonic clock (which is how delayed
/// messages are picked up, FR-PICK-08), the back-off after a failed step, the end after five
/// failures in a row, a protocol error ending the stream at once, and live-off at the end.
/// </summary>
public sealed class PickupClientLiveCycleTests
{
    // ---- starting -----------------------------------------------------------------------------

    [Fact]
    public void The_arguments_are_checked_when_the_method_is_called_and_nothing_else_happens_until_the_first_MoveNextAsync()
    {
        var (exchange, client) = Live();

        var bad = () => client.ReceiveLiveAsync(M + "#key-1", L);
        bad.Should().Throw<ArgumentException>();
        var badOverride = () => client.ReceiveLiveAsync(M, L, new Uri("wss://user@mediator.example/"));
        badOverride.Should().Throw<ArgumentException>();
        var nullDid = () => client.ReceiveLiveAsync(M, null!);
        nullDid.Should().Throw<ArgumentNullException>();

        var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();
        exchange.RouteCalls.Should().BeEmpty("the route is resolved at the first MoveNextAsync");
        exchange.Registrations.Should().BeEmpty();
        exchange.Sent.Should().BeEmpty();
        _ = stream;
    }

    [Fact]
    public async Task Over_HTTP_the_stream_is_refused_before_anything_is_sent()
    {
        var (exchange, client) = Live();
        exchange.Route = HttpRoute;
        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();

        await FluentActions.Awaiting(() => PickupTest.Within(stream.MoveNextAsync())).Should().ThrowAsync<InvalidOperationException>();

        exchange.RouteCalls.Should().ContainSingle().Which.RequireWebSocket.Should().BeTrue();
        exchange.Sent.Should().BeEmpty("no live-on, and no live-off for a stream that never started");
        exchange.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task The_override_is_handed_to_the_route_resolution()
    {
        var (exchange, client) = Live();
        var endpoint = new Uri("wss://mediator.example/other");
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, endpoint, cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        exchange.RouteCalls.Should().ContainSingle().Which.Should().Be((M, endpoint, true));
        exchange.Sent.Should().OnlyContain(s => ReferenceEquals(s.Route, WebSocketRoute), "every request of the stream uses the route resolved once");
        await StopAsync(stream, first, cts);
    }

    [Fact]
    public async Task A_second_stream_for_the_same_pair_is_refused_sends_nothing_and_leaves_the_first_alone()
    {
        var (exchange, client) = Live();
        using var cts = new CancellationTokenSource();
        var first = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var firstMove = first.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        var sentBefore = exchange.Sent.Count;
        var registration = exchange.Registration;

        await using (var second = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator())
            await FluentActions.Awaiting(() => PickupTest.Within(second.MoveNextAsync())).Should().ThrowAsync<InvalidOperationException>();

        exchange.Sent.Should().HaveCount(sentBefore, "the refused stream turned nothing on, so it turns nothing off");
        exchange.Registration.Should().BeSameAs(registration);
        registration!.IsDisposed.Should().BeFalse();
        registration.TryPush(Push(("q1", "ok:m1"))).Should().BeTrue();
        (await PickupTest.Within(firstMove)).Should().BeTrue();
        first.Current.Id.Should().Be("q1");

        var again = first.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        await StopAsync(first, again, cts);
    }

    [Fact]
    public async Task A_stream_for_another_local_DID_is_its_own()
    {
        var (exchange, client) = Live();
        using var cts = new CancellationTokenSource();
        var bob = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var carol = client.ReceiveLiveAsync(M, "did:example:carol", ct: cts.Token).GetAsyncEnumerator();
        var bobMove = bob.MoveNextAsync().AsTask();
        var carolMove = carol.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(2, PickupTest.Timeout);

        exchange.Registrations.Should().HaveCount(2);
        exchange.Sent.Where(s => s.Is(MessagePickup.LiveDeliveryChangeType)).Select(s => s.LocalDid).Should().BeEquivalentTo(new[] { L, "did:example:carol" });
        await StopAsync(bob, bobMove, cts);
        await FluentActions.Awaiting(() => PickupTest.Within(carolMove)).Should().ThrowAsync<OperationCanceledException>();
        await carol.DisposeAsync();
    }

    // ---- the cycle ----------------------------------------------------------------------------

    [Fact]
    public async Task The_first_cycle_turns_live_delivery_on_and_then_drains_on_a_frozen_clock()
    {
        var (exchange, client) = Live();
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        Types(exchange).Should().Equal(new[] { LiveOn, Drain }, "live-on first, so nothing falls between the drain and the mediator's arrival mark");
        var on = exchange.Sent[0];
        on.Spec.Should().BeSameAs(DeliveryReader.LiveDeliveryChangeSpec);
        on.Request.From.Should().Be(L);
        on.Request.To.Should().Equal(M);
        var drain = exchange.Sent[1];
        drain.Limit.Should().Be(LiveDeliveryStream.DrainLimit);
        drain.RecipientDid.Should().BeNull();
        drain.Spec.Should().BeSameAs(DeliveryReader.DeliveryRequestSpec);
        exchange.Sent.Should().OnlyContain(s => s.Request.CreatedTime == null, "the send path stamps created_time (FR-RR-09)");
        first.IsCompleted.Should().BeFalse();
        await StopAsync(stream, first, cts);
    }

    [Fact]
    public async Task The_drain_stops_at_a_status_or_after_50_rounds()
    {
        // Every round delivers the same message again (as a mediator that keeps redelivering would):
        // the first time it is yielded; later rounds find it remembered and acknowledge it.
        var (exchange, client) = Live();
        exchange.On(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1")));
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        (await Next(stream)).Id.Should().Be("q1");

        var waiting = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        exchange.SentOf(MessagePickup.DeliveryRequestType).Should().HaveCount(LiveDeliveryStream.MaxDrainRounds);
        exchange.AcknowledgedIds.Should().HaveCount(LiveDeliveryStream.MaxDrainRounds).And.OnlyContain(id => id == "q1");
        waiting.IsCompleted.Should().BeFalse("a repeat is acknowledged, never yielded again");
        await StopAsync(stream, waiting, cts);
    }

    [Fact]
    public async Task The_cycle_runs_again_every_LivePollInterval_which_picks_up_a_delayed_message()
    {
        // FR-PICK-08: a delayed message is never pushed when it falls due; the next cycle drains it.
        var (exchange, client) = Live(o => o.LivePollInterval = TimeSpan.FromMinutes(2));
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Status(r), r => Delivery(r, ("late", "ok:delayed")), r => Status(r));
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        Types(exchange).Should().Equal(LiveOn, Drain);

        exchange.Clock.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromTicks(1));
        Types(exchange).Should().Equal(new[] { LiveOn, Drain }, "not yet due");
        exchange.Clock.Advance(TimeSpan.FromTicks(1));

        (await PickupTest.Within(first)).Should().BeTrue();
        stream.Current.Id.Should().Be("late");
        Types(exchange).Should().Equal(LiveOn, Drain, LiveOn, Drain);
        var next = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        await StopAsync(stream, next, cts);
    }

    [Fact]
    public async Task The_cycle_comes_due_on_the_monotonic_clock_even_while_the_consumer_holds_an_item()
    {
        var (exchange, client) = Live(o => o.LivePollInterval = TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        exchange.Registration!.TryPush(Push(("q1", "ok:m1"))).Should().BeTrue();
        (await PickupTest.Within(first)).Should().BeTrue();

        exchange.Clock.Advance(TimeSpan.FromSeconds(30));
        exchange.Registration!.TryPush(Push(("q2", "ok:m2"))).Should().BeTrue();
        (await Next(stream)).Id.Should().Be("q2");
        Types(exchange).Should().Equal(new[] { LiveOn, Drain, LiveOn }, "the cycle came due while the consumer held q1: it runs before the next item, and a waiting push goes before its drain");
        var next = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        Types(exchange).Should().Equal(LiveOn, Drain, LiveOn, Ack, Drain);
        exchange.AcknowledgedIds.Should().Equal(new[] { "q1", "q2" }, "q1 was taken when the consumer asked for q2, so it had not waited ten seconds");
        await StopAsync(stream, next, cts);
    }

    [Fact]
    public async Task A_status_with_live_delivery_off_is_logged_once_and_the_stream_keeps_polling()
    {
        var logs = new PickupTestLogs();
        var exchange = new FakePickupExchange(o => o.LivePollInterval = TimeSpan.FromSeconds(10)) { Route = WebSocketRoute };
        exchange.On(MessagePickup.LiveDeliveryChangeType, r => Status(r, live: false));
        var client = PickupTest.Client(exchange, logs);
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        exchange.Clock.Advance(TimeSpan.FromSeconds(10));
        await exchange.WaitForSentAsync(4);
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        Types(exchange).Should().Equal(LiveOn, Drain, LiveOn, Drain);
        logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("live delivery off")).Should().Be(1);
        await StopAsync(stream, first, cts);
    }

    // ---- failures -----------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_step_re_arms_the_cycle_after_a_back_off_of_one_second_doubling()
    {
        var (exchange, client) = Live();
        var attempts = 0;
        exchange.On(MessagePickup.LiveDeliveryChangeType, r => r.Live && ++attempts <= 2
            ? Task.FromException<InboundMessageSnapshot>(new TransportException("send failed"))
            : Task.FromResult(Status(r, live: r.Live)));
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1")), r => Status(r));
        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();

        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        Types(exchange).Should().Equal(LiveOn);
        exchange.Clock.Advance(TimeSpan.FromMilliseconds(999));
        Types(exchange).Should().Equal(LiveOn);
        exchange.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await exchange.WaitForSentAsync(2);
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        Types(exchange).Should().Equal(LiveOn, LiveOn);

        exchange.Clock.Advance(TimeSpan.FromMilliseconds(1999));
        Types(exchange).Should().Equal(new[] { LiveOn, LiveOn }, "the second pause is two seconds");
        exchange.Clock.Advance(TimeSpan.FromMilliseconds(1));

        (await PickupTest.Within(first)).Should().BeTrue();
        stream.Current.Id.Should().Be("q1");
        Types(exchange).Should().Equal(LiveOn, LiveOn, LiveOn, Drain);
    }

    [Fact]
    public async Task A_failed_acknowledgement_keeps_the_ids_and_re_arms_the_cycle_after_the_back_off()
    {
        var (exchange, client) = Live();
        var acks = 0;
        exchange.On(MessagePickup.MessagesReceivedType, r => ++acks == 1
            ? Task.FromException<InboundMessageSnapshot>(new TimeoutException())
            : Task.FromResult(Status(r)));
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1")), r => Status(r));
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        await Next(stream);

        var waiting = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        Types(exchange).Should().Equal(LiveOn, Drain, Ack);
        exchange.Clock.Advance(TimeSpan.FromSeconds(1));
        await exchange.WaitForSentAsync(6);
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        Types(exchange).Should().Equal(LiveOn, Drain, Ack, Ack, LiveOn, Drain);
        exchange.AcknowledgedIds.Should().Equal(new[] { "q1", "q1" }, "the ids are kept and acknowledged again after the pause");
        await StopAsync(stream, waiting, cts);
    }

    [Fact]
    public async Task Five_failed_steps_in_a_row_end_the_stream_with_the_last_exception()
    {
        var (exchange, client) = Live();
        var failures = new List<TimeoutException>();
        exchange.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            var error = new TimeoutException($"attempt {failures.Count + 1}");
            failures.Add(error);
            return Task.FromException<InboundMessageSnapshot>(error);
        });
        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();

        foreach (var pause in new[] { 1, 2, 4, 8 })
        {
            await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
            first.IsCompleted.Should().BeFalse();
            exchange.Clock.Advance(TimeSpan.FromSeconds(pause));
        }

        var thrown = (await FluentActions.Awaiting(() => PickupTest.Within(first)).Should().ThrowAsync<TimeoutException>()).Which;
        thrown.Should().BeSameAs(failures[4], "the fifth failure in a row ends the stream with its exception");
        Types(exchange).Should().Equal(new[] { LiveOn, LiveOn, LiveOn, LiveOn, LiveOn, LiveOff }, "live-off is still attempted at the end");
        exchange.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cycle_that_completes_starts_the_failure_count_again()
    {
        var (exchange, client) = Live(o => o.LivePollInterval = TimeSpan.FromSeconds(60));
        var attempt = 0;
        // Four failures, a success, then four failures again, then success: never five in a row.
        exchange.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            if (!r.Live)
                return Task.FromResult(Status(r, live: false));
            attempt++;
            return attempt is 5 or 10
                ? Task.FromResult(Status(r, live: true))
                : Task.FromException<InboundMessageSnapshot>(new TransportException("down"));
        });
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Status(r), r => Delivery(r, ("q1", "ok:m1")), r => Status(r));
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();

        foreach (var pause in new[] { 1, 2, 4, 8, 60, 1, 2, 4, 8 })
        {
            await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
            first.IsCompleted.Should().BeFalse();
            exchange.Clock.Advance(TimeSpan.FromSeconds(pause));
        }

        (await PickupTest.Within(first)).Should().BeTrue();
        stream.Current.Id.Should().Be("q1");
        attempt.Should().Be(10);
        var next = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        await StopAsync(stream, next, cts);
    }

    [Fact]
    public async Task Live_mode_not_supported_ends_the_stream_at_once()
    {
        var (exchange, client) = Live();
        exchange.On(MessagePickup.LiveDeliveryChangeType, r => r.Live
            ? Task.FromException<InboundMessageSnapshot>(new MediationProtocolException("The mediator reported a problem.", r.Request.Id, MessagePickup.LiveModeNotSupportedCode))
            : Task.FromResult(Status(r, live: false)));
        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();

        var thrown = (await FluentActions.Awaiting(() => PickupTest.Within(stream.MoveNextAsync())).Should().ThrowAsync<MediationProtocolException>()).Which;

        thrown.ProblemCode.Should().Be(MessagePickup.LiveModeNotSupportedCode);
        Types(exchange).Should().Equal(new[] { LiveOn, LiveOff }, "no retry; the end still turns live mode off");
        exchange.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_malformed_drain_answer_ends_the_stream_at_once()
    {
        var (exchange, client) = Live();
        exchange.On(MessagePickup.DeliveryRequestType, r => Snapshot(new DidComm.Messages.Message
        {
            Id = "a1",
            Type = MessagePickup.StatusType,
            Thid = r.Request.Id,
            Body = new System.Text.Json.Nodes.JsonObject(),
        }));
        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();

        var thrown = (await FluentActions.Awaiting(() => PickupTest.Within(stream.MoveNextAsync())).Should().ThrowAsync<MediationProtocolException>()).Which;
        thrown.RequestId.Should().Be(exchange.SentOf(MessagePickup.DeliveryRequestType).Single().Request.Id);
        Types(exchange).Should().Equal(LiveOn, Drain, LiveOff);
    }

    [Fact]
    public async Task A_mailbox_the_channel_closes_with_an_error_ends_the_stream_with_it()
    {
        var (exchange, client) = Live();
        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        exchange.Registration!.Complete(new ObjectDisposedException("channel"));

        await FluentActions.Awaiting(() => PickupTest.Within(first)).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task A_mailbox_closed_without_an_error_ends_the_enumeration()
    {
        var (exchange, client) = Live();
        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        exchange.Registration!.Complete();

        (await PickupTest.Within(first)).Should().BeFalse();
        Types(exchange).Should().Equal(LiveOn, Drain, LiveOff);
    }

    // ---- what is retried (Q2-2) ----------------------------------------------------------------

    /// <summary>A failure of the stream's first request, which no retry can fix.</summary>
    private static Exception Permanent(string kind) => kind switch
    {
        "no key for from" => new SecretNotFoundException(L + "#key-1"),
        "a malformed message" => new MalformedMessageException("The send path refused the message."),
        "an argument the send path refuses" => new ArgumentException("refused"),
        "a refusal before any answer" => new InvalidOperationException("No key curve in common."),
        "something unexpected" => new InvalidDataException("unexpected"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [InlineData("no key for from")]
    [InlineData("a malformed message")]
    [InlineData("an argument the send path refuses")]
    [InlineData("a refusal before any answer")]
    [InlineData("something unexpected")]
    public async Task A_failure_no_retry_can_fix_ends_the_stream_at_once_and_live_off_is_still_attempted(string kind)
    {
        var (exchange, client) = Live();
        var error = Permanent(kind);
        exchange.On(MessagePickup.LiveDeliveryChangeType, r => r.Live
            ? Task.FromException<InboundMessageSnapshot>(error)
            : Task.FromResult(Status(r, live: false)));
        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();

        var thrown = (await FluentActions.Awaiting(() => PickupTest.Within(stream.MoveNextAsync())).Should().ThrowAsync<Exception>()).Which;

        thrown.Should().BeSameAs(error, "the failure is not retried after a back-off");
        Types(exchange).Should().Equal(LiveOn, LiveOff);
        exchange.Clock.ScheduledTimers.Should().Be(0, "no back-off was scheduled");
    }

    [Theory]
    [InlineData("too many requests waiting")]
    [InlineData("a cancellation the caller did not ask for")]
    public async Task Once_the_mediator_has_answered_a_refusal_for_too_many_waiting_requests_or_a_cancellation_nobody_asked_for_is_retried(string kind)
    {
        var (exchange, client) = Live();
        Exception error = kind == "too many requests waiting"
            ? new InvalidOperationException("64 mediator requests are already waiting for an answer; the request was not sent.")
            : new OperationCanceledException();
        var drains = 0;
        exchange.On(MessagePickup.DeliveryRequestType, r => Interlocked.Increment(ref drains) switch
        {
            1 => Task.FromException<InboundMessageSnapshot>(error),
            2 => Task.FromResult(Delivery(r, ("q1", "ok:m1"))),
            _ => Task.FromResult(Status(r)),
        });
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();

        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        first.IsCompleted.Should().BeFalse("the stream backs off");
        Types(exchange).Should().Equal(LiveOn, Drain);
        exchange.Clock.Advance(LiveDeliveryStream.FirstBackoff);

        (await PickupTest.Within(first)).Should().BeTrue();
        stream.Current.Id.Should().Be("q1");
        Types(exchange).Should().Equal(LiveOn, Drain, LiveOn, Drain);
        var next = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        await StopAsync(stream, next, cts);
    }

    [Fact]
    public async Task The_containers_disposal_ends_the_stream_at_once_though_it_is_an_InvalidOperationException_after_an_answer()
    {
        var (exchange, client) = Live();
        var disposed = new ObjectDisposedException(nameof(MediatorClientChannel));
        exchange.On(MessagePickup.DeliveryRequestType, _ => Task.FromException<InboundMessageSnapshot>(disposed));
        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();

        var thrown = (await FluentActions.Awaiting(() => PickupTest.Within(stream.MoveNextAsync())).Should().ThrowAsync<ObjectDisposedException>()).Which;

        thrown.Should().BeSameAs(disposed);
        Types(exchange).Should().Equal(LiveOn, Drain, LiveOff);
        exchange.Clock.ScheduledTimers.Should().Be(0);
    }

    // ---- deliveries nothing can be acknowledged from ---------------------------------------------

    [Fact]
    public async Task A_drain_answered_with_a_delivery_in_which_no_message_has_a_usable_id_ends_the_stream_with_a_MediationProtocolException()
    {
        // Nothing in it can be acknowledged, so the next round would get it again (QA-2 observation 7).
        var (exchange, client) = Live();
        exchange.On(MessagePickup.DeliveryRequestType, r => RawDelivery(r, new Attachment?[] { Entry(null, "ok:m1"), Entry("has space", "ok:m2"), null }));
        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();

        var thrown = (await FluentActions.Awaiting(() => PickupTest.Within(stream.MoveNextAsync())).Should().ThrowAsync<MediationProtocolException>()).Which;

        thrown.RequestId.Should().Be(exchange.SentOf(MessagePickup.DeliveryRequestType).Single().Request.Id);
        thrown.Message.Should().NotContain("has space").And.NotContain(L);
        Types(exchange).Should().Equal(LiveOn, Drain, LiveOff);
        exchange.Unpacked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_push_in_which_no_message_has_a_usable_id_is_ignored_and_the_stream_goes_on()
    {
        var (exchange, client) = Live();
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        exchange.Registration!.TryPush(RawDelivery(null, new Attachment?[] { Entry(null, "ok:m1") })).Should().BeTrue();
        exchange.Registration!.TryPush(Push(("p1", "ok:p1"))).Should().BeTrue();

        (await PickupTest.Within(first)).Should().BeTrue();
        stream.Current.Id.Should().Be("p1");
        exchange.Unpacked.Should().Equal("ok:p1");
        var next = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        await StopAsync(stream, next, cts);
    }

    // ---- the wait -------------------------------------------------------------------------------

    /// <summary>
    /// The wait for a push races the next cycle. Raced as two tasks, the loser was left running: a
    /// mailbox the channel's disposal ended with an error just as the cycle came due faulted it after
    /// the race was decided, and nobody observed it (QA-1). The wait is one awaited operation now.
    /// </summary>
    [Fact]
    public async Task A_mailbox_closed_with_an_error_as_the_cycle_comes_due_leaves_no_unobserved_task_exception()
    {
        var marker = "wait-race-" + Guid.NewGuid().ToString("N");
        var seen = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var inner in e.Exception.Flatten().InnerExceptions)
                seen.Enqueue(inner);
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            for (var i = 0; i < 20; i++)
                await CloseAsTheCycleComesDueAsync(marker);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            seen.OfType<ObjectDisposedException>().Where(e => e.ObjectName == marker)
                .Should().BeEmpty("the stream threw the error to its consumer, and nothing else was left holding it");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CloseAsTheCycleComesDueAsync(string marker)
    {
        var (exchange, client) = Live();
        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        // The mailbox ends with an error, and the cycle comes due before the wait has seen it.
        exchange.Registration!.Complete(new ObjectDisposedException(marker));
        exchange.Clock.Advance(exchange.Options.LivePollInterval);

        await FluentActions.Awaiting(() => PickupTest.Within(first)).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task A_clock_that_passes_the_cycle_while_the_wait_is_being_set_runs_the_cycle_instead_of_waiting_another_interval()
    {
        // The first timer the stream sets moves the clock a whole interval on before it is created,
        // as a manual clock moved by another thread at that moment would (the interop delayed-message
        // test once timed out this way): the stream must see the cycle is due, with no further move.
        var inner = new FakePickupExchange { Route = WebSocketRoute };
        var exchange = new TimeShiftedExchange(inner, new MovesOnFirstTimer(inner.Clock, inner.Options.LivePollInterval));
        var client = new PickupClient(exchange, logger: null);
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();

        await inner.WaitForSentAsync(4);
        Types(inner).Should().Equal(LiveOn, Drain, LiveOn, Drain);
        await inner.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        await StopAsync(stream, first, cts);
    }

    /// <summary>A clock that moves <paramref name="by"/> on just before the first timer is created on it.</summary>
    private sealed class MovesOnFirstTimer(ManualTimeProvider inner, TimeSpan by) : TimeProvider
    {
        private int _armed = 1;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
                inner.Advance(by);
            return inner.CreateTimer(callback, state, dueTime, period);
        }
    }

    /// <summary>The fake exchange on another clock.</summary>
    private sealed class TimeShiftedExchange(FakePickupExchange inner, TimeProvider time) : IMediatorExchange
    {
        public MediatorClientOptions Options => inner.Options;
        public TimeProvider Time => time;
        public DuplicateFilter Duplicates => inner.Duplicates;
        public NegotiatedVersionCache Versions => inner.Versions;

        public Task<MediatorRoute> ResolveRouteAsync(string mediatorDid, Uri? serviceEndpointOverride, bool requireWebSocket, CancellationToken ct)
            => inner.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket, ct);

        public Task<InboundMessageSnapshot> ExchangeAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, CancellationToken ct)
            => inner.ExchangeAsync(route, request, spec, mediatorDid, localDid, ct);

        public LiveRegistration RegisterLiveStream(string mediatorDid, string localDid) => inner.RegisterLiveStream(mediatorDid, localDid);

        public Task<DeliveredOutcome> UnpackDeliveredAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => inner.UnpackDeliveredAsync(payload, ct);

        public Task<IReadOnlyList<DeliveredOutcome>> UnpackDeliveredBatchAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct)
            => inner.UnpackDeliveredBatchAsync(payloads, ct);
    }

    // ---- the back-off schedule ----------------------------------------------------------------

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(40, 30)]
    public void The_back_off_starts_at_one_second_and_doubles_up_to_thirty(int failures, int seconds)
        => LiveDeliveryStream.Backoff(failures).Should().Be(TimeSpan.FromSeconds(seconds));
}
