using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="PickupClient.ReceiveLiveAsync"/>: what is yielded and what is acknowledged (#96 design
/// §4.8 as amended by R-2). A yielded message is taken — remembered and queued for an
/// acknowledgement — only when the consumer asks for the next item; whatever ends the enumeration
/// (break, an exception in the loop body, cancellation), the last yielded message is not taken and
/// comes back; buffered or drained messages never yielded are never acknowledged; failures are
/// yielded, never acknowledged, and reported again; repeats are acknowledged and not yielded.
/// The fake exchange answers synchronously and the clock is frozen, so every step is deterministic.
/// </summary>
public sealed class PickupClientLiveStreamTests
{
    // ---- the flow -----------------------------------------------------------------------------

    [Fact]
    public async Task Queued_messages_arrive_by_the_drain_after_live_on_and_later_ones_by_push()
    {
        var (exchange, client) = Live();
        exchange.OnSequence(MessagePickup.DeliveryRequestType,
            r => Delivery(r, ("q1", "ok:m1"), ("q2", "ok:m2")),
            r => Status(r));

        var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();
        try
        {
            (await Next(stream)).Id.Should().Be("q1");
            Types(exchange).Should().Equal(LiveOn, Drain);

            (await Next(stream)).Id.Should().Be("q2");
            exchange.AcknowledgedIds.Should().BeEmpty("an item is acknowledged only after the consumer asks for the next one, in a batch");

            var third = stream.MoveNextAsync().AsTask();
            await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
            third.IsCompleted.Should().BeFalse("nothing more is queued: the stream waits for a push or the next cycle");
            exchange.AcknowledgedIds.Should().Equal(new[] { "q1", "q2" }, "nothing else was ready, so the taken messages were acknowledged");
            Types(exchange).Should().Equal(LiveOn, Drain, Ack, Drain);

            exchange.Registration!.TryPush(Push(("q3", "ok:m3"))).Should().BeTrue();
            (await PickupTest.Within(third)).Should().BeTrue();
            stream.Current.Id.Should().Be("q3");
            stream.Current.Unpacked!.Message.Id.Should().Be("m3");
        }
        finally
        {
            await PickupTest.Within(stream.DisposeAsync());
        }

        exchange.AcknowledgedIds.Should().Equal(new[] { "q1", "q2" }, "q3 was the last item yielded");
        exchange.Sent[^1].Is(MessagePickup.LiveDeliveryChangeType).Should().BeTrue();
        exchange.Sent[^1].Live.Should().BeFalse("live mode is turned off at the end");
        exchange.Registrations.Should().BeEmpty("the mailbox is closed at the end");
    }

    // Owner decision 2-A: a delivery the stream takes is opened whole, under one budget, on the
    // consumer's own MoveNextAsync, before its first item is yielded; then its items are yielded in
    // order without opening anything more.
    [Fact]
    public async Task A_delivery_is_opened_whole_on_the_consumers_own_MoveNextAsync_before_its_first_item_is_yielded()
    {
        var (exchange, client) = Live();
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1"), ("q2", "ok:m2"), ("q3", "ok:m3")), r => Status(r));
        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();

        (await Next(stream)).Id.Should().Be("q1");
        exchange.Unpacked.Should().Equal("ok:m1", "ok:m2", "ok:m3");
        exchange.Batches.Should().ContainSingle().Which.Should().Be((exchange.SentOf(MessagePickup.DeliveryRequestType)[0].Request.Id, 3),
            "one batch for the drain round's delivery, naming its request");
        (await Next(stream)).Id.Should().Be("q2");
        (await Next(stream)).Id.Should().Be("q3");
        exchange.Unpacked.Should().Equal(new[] { "ok:m1", "ok:m2", "ok:m3" }, "nothing is opened again while the delivery's items are yielded");
    }

    [Fact]
    public async Task A_push_is_opened_as_a_delivery_of_its_own_that_names_no_request()
    {
        var (exchange, client) = Live();
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        exchange.Registration!.TryPush(Push(("p1", "ok:p1"), ("p2", "ok:p2"))).Should().BeTrue();

        (await PickupTest.Within(first)).Should().BeTrue();
        stream.Current.Id.Should().Be("p1");
        exchange.Batches.Should().Equal(new[] { ((string?)null, 2) });
        var second = stream.MoveNextAsync().AsTask();
        (await PickupTest.Within(second)).Should().BeTrue();
        stream.Current.Id.Should().Be("p2");
        var third = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        await StopAsync(stream, third, cts);
    }

    // ---- the delivery budget (owner decision 2-A) ----------------------------------------------

    [Fact]
    public async Task A_message_the_budget_runs_out_on_is_a_failed_item_naming_the_drain_and_the_rest_of_its_delivery_is_yielded_and_acknowledged()
    {
        var budget = TimeSpan.FromSeconds(20);
        var (exchange, client) = Live(o => o.UnpackTimeout = budget);
        exchange.UseBudget(budget);
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1"), ("q2", "stall:m2"), ("q3", "ok:m3")), r => Status(r));
        exchange.Unpacker = async (text, ct) =>
        {
            if (text.StartsWith("stall:", StringComparison.Ordinal))
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return DefaultOutcome(text);
        };
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout); // the delivery's budget
        first.IsCompleted.Should().BeFalse("nothing of the delivery is yielded while it is being opened");
        exchange.Clock.Advance(budget);
        (await PickupTest.Within(first)).Should().BeTrue();

        var drainId = exchange.SentOf(MessagePickup.DeliveryRequestType)[0].Request.Id;
        stream.Current.Id.Should().Be("q1");
        (await Next(stream)).Should().Match<PickupItem>(i => i.Id == "q2" && i.Error is TimeoutException);
        stream.Current.Error!.Message.Should().Contain(drainId).And.Contain("UnpackTimeout").And.NotContain("m2");
        (await Next(stream)).Id.Should().Be("q3");
        var waiting = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        exchange.AcknowledgedIds.Should().Equal(new[] { "q1", "q3" }, "the rest of the delivery is acknowledged as usual; q2 failed and is never acknowledged");
        await StopAsync(stream, waiting, cts);
    }

    [Fact]
    public async Task Cancelled_while_a_later_delivery_is_being_opened_leaves_it_unacknowledged_and_unremembered_and_acknowledges_what_was_taken()
    {
        // The QA-2 property (a cancellation during an unpack), under up-front opening: q1 comes in one
        // delivery, q2 in the next; the consumer cancels while q2's delivery is being opened.
        var (exchange, client) = Live();
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1")), r => Delivery(r, ("q2", "ok:m2")), r => Status(r));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        exchange.Unpacker = async (text, ct) =>
        {
            if (text == "ok:m2")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return DefaultOutcome(text);
        };
        using var cts = new CancellationTokenSource();
        await using var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        (await Next(stream)).Id.Should().Be("q1");

        var second = stream.MoveNextAsync().AsTask();
        await entered.Task.WaitAsync(PickupTest.Timeout);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => PickupTest.Within(second)).Should().ThrowAsync<OperationCanceledException>();
        exchange.AcknowledgedIds.Should().Equal(new[] { "q1" }, "q1 was taken when q2 was asked for; q2 never reached the consumer");
        exchange.Duplicates.Contains(L, DefaultOutcome("ok:m2").DedupeKey).Should().BeFalse();
        exchange.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task The_consumers_time_with_an_item_never_counts_against_the_budget()
    {
        var budget = TimeSpan.FromSeconds(20);
        var (exchange, client) = Live(o => o.UnpackTimeout = budget);
        exchange.UseBudget(budget);
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1"), ("q2", "ok:m2")), r => Status(r));
        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();

        (await Next(stream)).Id.Should().Be("q1");
        // The consumer works on q1 for longer than the budget (but less than the poll interval).
        exchange.Clock.Advance(budget + budget);

        var second = await Next(stream);
        second.Id.Should().Be("q2");
        second.Error.Should().BeNull("q2 was opened with the delivery, before q1 was handed over");
    }

    // ---- acknowledgement (R-2) ------------------------------------------------------------------

    [Fact]
    public async Task An_item_is_acknowledged_only_once_the_consumer_asks_for_the_next_one()
    {
        var (exchange, client) = Live();
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        exchange.Registration!.TryPush(Push(("q1", "ok:m1"))).Should().BeTrue();
        (await PickupTest.Within(first)).Should().BeTrue();

        exchange.AcknowledgedIds.Should().BeEmpty("handed over, not yet taken");
        exchange.Duplicates.Count.Should().Be(0, "nothing is remembered before it is taken");

        var second = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        second.IsCompleted.Should().BeFalse();
        exchange.AcknowledgedIds.Should().Equal("q1");
        exchange.Duplicates.Contains(L, DefaultOutcome("ok:m1").DedupeKey).Should().BeTrue();
        await StopAsync(stream, second, cts);
    }

    [Fact]
    public async Task On_break_the_last_item_is_not_acknowledged_and_comes_back_on_the_next_stream()
    {
        var (exchange, client) = Live();
        exchange.OnSequence(MessagePickup.DeliveryRequestType,
            r => Delivery(r, ("q1", "ok:m1"), ("q2", "ok:m2")),
            r => Delivery(r, ("q2", "ok:m2")),
            r => Status(r));

        var seen = new List<string>();
        await PickupTest.Within(Task.Run(async () =>
        {
            await foreach (var item in client.ReceiveLiveAsync(M, L).WithCancellation(CancellationToken.None))
            {
                seen.Add(item.Id);
                if (seen.Count == 2)
                    break;
            }
        }));

        seen.Should().Equal("q1", "q2");
        exchange.AcknowledgedIds.Should().Equal("q1");
        exchange.Duplicates.Contains(L, DefaultOutcome("ok:m2").DedupeKey).Should().BeFalse("the last item is not remembered either");
        exchange.Sent[^1].Should().Match<SentRequest>(s => s.Is(MessagePickup.LiveDeliveryChangeType) && !s.Live);
        exchange.Registrations.Should().BeEmpty();

        // The mediator delivers q2 again to the next stream: it is yielded, not filtered.
        await using var next = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();
        (await Next(next)).Id.Should().Be("q2");
    }

    [Fact]
    public async Task When_the_loop_body_throws_the_last_item_is_not_acknowledged()
    {
        var (exchange, client) = Live();
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1"), ("q2", "ok:m2")), r => Status(r));

        var act = async () =>
        {
            await foreach (var item in client.ReceiveLiveAsync(M, L))
            {
                if (item.Id == "q2")
                    throw new InvalidDataException("the application failed on q2");
            }
        };

        await FluentActions.Awaiting(() => PickupTest.Within(act())).Should().ThrowAsync<InvalidDataException>();
        exchange.AcknowledgedIds.Should().Equal("q1");
        exchange.Sent[^1].Should().Match<SentRequest>(s => s.Is(MessagePickup.LiveDeliveryChangeType) && !s.Live);
    }

    [Fact]
    public async Task A_token_cancelled_while_the_loop_body_runs_leaves_that_item_unacknowledged_and_the_end_still_runs()
    {
        var (exchange, client) = Live();
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1"), ("q2", "ok:m2")), r => Status(r));
        using var cts = new CancellationTokenSource();

        var act = async () =>
        {
            await foreach (var item in client.ReceiveLiveAsync(M, L, ct: cts.Token))
            {
                if (item.Id == "q2")
                    await cts.CancelAsync();
            }
        };

        await FluentActions.Awaiting(() => PickupTest.Within(act())).Should().ThrowAsync<OperationCanceledException>();
        exchange.AcknowledgedIds.Should().Equal("q1");
        var end = exchange.Sent.Where(s => s.Is(MessagePickup.MessagesReceivedType) || (s.Is(MessagePickup.LiveDeliveryChangeType) && !s.Live)).ToArray();
        end.Should().OnlyContain(s => !s.Ct.IsCancellationRequested, "the end runs on its own deadline, not the caller's token");
        exchange.Sent[^1].Live.Should().BeFalse();
    }

    [Fact]
    public async Task Cancelling_while_waiting_ends_the_stream_after_acknowledging_what_was_taken()
    {
        var (exchange, client) = Live();
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1")), r => Status(r));
        using var cts = new CancellationTokenSource();
        await using var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        await Next(stream);

        var waiting = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => PickupTest.Within(waiting)).Should().ThrowAsync<OperationCanceledException>();
        exchange.AcknowledgedIds.Should().Equal("q1");
    }

    [Fact]
    public async Task Drained_and_buffered_messages_that_were_never_yielded_are_never_acknowledged()
    {
        var (exchange, client) = Live();
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1"), ("q2", "ok:m2"), ("q3", "ok:m3")), r => Status(r));
        var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();
        await Next(stream);
        exchange.Registration!.TryPush(Push(("p1", "ok:p1"))).Should().BeTrue();
        exchange.Registration!.TryPush(Push(("p2", "ok:p2"))).Should().BeTrue();

        await PickupTest.Within(stream.DisposeAsync());

        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().BeEmpty("q1 was the last item yielded; q2, q3, p1 and p2 were never yielded");
        exchange.Unpacked.Should().Equal(new[] { "ok:m1", "ok:m2", "ok:m3" }, "the drained delivery was opened whole; the pushes the stream never took were never opened");
        exchange.Duplicates.Count.Should().Be(0);
    }

    [Fact]
    public async Task Ten_taken_messages_are_acknowledged_in_one_batch_even_while_more_are_ready()
    {
        var (exchange, client) = Live();
        var ten = Enumerable.Range(0, 10).Select(i => ($"q{i}", $"ok:m{i}")).ToArray();
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ten), r => Status(r));
        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();

        for (var i = 0; i < 10; i++)
            (await Next(stream)).Id.Should().Be($"q{i}");
        // A push is waiting, so something is ready when the tenth is taken.
        exchange.Registration!.TryPush(Push(("p1", "ok:p1"))).Should().BeTrue();
        exchange.AcknowledgedIds.Should().BeEmpty();

        (await Next(stream)).Id.Should().Be("p1");
        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().ContainSingle()
            .Which.Ids.Should().Equal(ten.Select(t => t.Item1), "ten taken: acknowledged at once, in one message");
    }

    [Fact]
    public async Task The_oldest_taken_message_is_acknowledged_once_it_has_waited_ten_seconds()
    {
        var (exchange, client) = Live();
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q0", "ok:m0"), ("q1", "ok:m1"), ("q2", "ok:m2")), r => Status(r));
        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();

        await Next(stream);
        await Next(stream);                         // q0 taken now
        exchange.Clock.Advance(TimeSpan.FromSeconds(9));
        (await Next(stream)).Id.Should().Be("q2");  // q1 taken; q0 has waited 9 s
        exchange.AcknowledgedIds.Should().BeEmpty();

        exchange.Registration!.TryPush(Push(("p1", "ok:p1"))).Should().BeTrue();
        exchange.Clock.Advance(TimeSpan.FromSeconds(1));
        (await Next(stream)).Id.Should().Be("p1");  // q2 taken; q0 has waited 10 s
        exchange.AcknowledgedIds.Should().Equal("q0", "q1", "q2");
    }

    // ---- what is yielded ------------------------------------------------------------------------

    [Fact]
    public async Task A_failure_is_yielded_never_acknowledged_and_reported_again_when_it_comes_back()
    {
        var (exchange, client) = Live();
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        exchange.Registration!.TryPush(Push(("f1", "bad:x"))).Should().BeTrue();
        (await PickupTest.Within(first)).Should().BeTrue();
        stream.Current.Error.Should().NotBeNull();
        stream.Current.Unpacked.Should().BeNull();

        // It comes back (after the lease): reported again.
        exchange.Registration!.TryPush(Push(("f1", "bad:x"))).Should().BeTrue();
        exchange.Registration!.TryPush(Push(("q1", "ok:m1"))).Should().BeTrue();
        (await Next(stream)).Should().Match<PickupItem>(i => i.Id == "f1" && i.Error != null);
        (await Next(stream)).Id.Should().Be("q1");
        var fourth = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        exchange.AcknowledgedIds.Should().Equal("q1");
        exchange.Unpacked.Should().Equal("bad:x", "bad:x", "ok:m1");
        fourth.IsCompleted.Should().BeFalse();
        await StopAsync(stream, fourth, cts);
    }

    [Fact]
    public async Task An_undecodable_push_is_a_failed_item()
    {
        var (exchange, client) = Live();
        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        exchange.Registration!.TryPush(RawDelivery(null, new[] { PoisonedAttachment("f1"), Entry(null, "ok:no-id") })).Should().BeTrue();

        (await PickupTest.Within(first)).Should().BeTrue();
        stream.Current.Id.Should().Be("f1");
        stream.Current.Error.Should().BeOfType<DidComm.Exceptions.MalformedMessageException>();
        exchange.Unpacked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_repeat_of_a_message_already_returned_is_acknowledged_and_not_yielded()
    {
        var (exchange, client) = Live();
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        exchange.Registration!.TryPush(Push(("q1", "ok:m1"))).Should().BeTrue();
        (await PickupTest.Within(first)).Should().BeTrue();

        // The same message under another queue id (a replayed forward), then a new one.
        exchange.Registration!.TryPush(Push(("q2", "ok:m1"))).Should().BeTrue();
        exchange.Registration!.TryPush(Push(("q3", "ok:m3"))).Should().BeTrue();
        (await Next(stream)).Id.Should().Be("q3");
        var waiting = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        exchange.AcknowledgedIds.Should().BeEquivalentTo(new[] { "q1", "q2", "q3" });
        waiting.IsCompleted.Should().BeFalse();
        await StopAsync(stream, waiting, cts);
    }

    [Fact]
    public async Task An_id_the_stream_already_holds_is_skipped_and_not_unpacked_again()
    {
        var (exchange, client) = Live();
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1"), ("q2", "ok:m2")), r => Status(r));
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        (await Next(stream)).Id.Should().Be("q1");

        // q2 is buffered (drained, not yet yielded); the mediator pushes it again.
        exchange.Registration!.TryPush(Push(("q2", "ok:m2-other-bytes"))).Should().BeTrue();
        (await Next(stream)).Id.Should().Be("q2");
        var waiting = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        exchange.Unpacked.Should().Equal("ok:m1", "ok:m2");
        exchange.AcknowledgedIds.Should().Equal("q1", "q2");
        waiting.IsCompleted.Should().BeFalse();
        await StopAsync(stream, waiting, cts);
    }

    [Fact]
    public async Task A_push_beyond_the_full_mailbox_is_dropped_and_never_acknowledged()
    {
        var (exchange, client) = Live();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        exchange.Unpacker = async (text, ct) =>
        {
            if (text == "ok:p0")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return DefaultOutcome(text);
        };
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        exchange.Registration!.TryPush(Push(("p0", "ok:p0"))).Should().BeTrue();
        await entered.Task.WaitAsync(PickupTest.Timeout);

        // The stream is busy with p0: the mailbox takes 8 more and drops the ninth.
        for (var i = 1; i <= LiveRegistration.PushCapacity; i++)
            exchange.Registration!.TryPush(Push(($"p{i}", $"ok:p{i}"))).Should().BeTrue();
        exchange.Registration!.TryPush(Push(("dropped", "ok:dropped"))).Should().BeFalse("the mailbox is full");
        release.SetResult();

        (await PickupTest.Within(first)).Should().BeTrue();
        for (var i = 1; i <= LiveRegistration.PushCapacity; i++)
            (await Next(stream)).Id.Should().Be($"p{i}");
        var waiting = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        exchange.AcknowledgedIds.Should().NotContain("dropped");
        exchange.Unpacked.Should().NotContain("ok:dropped");
        waiting.IsCompleted.Should().BeFalse();
        await StopAsync(stream, waiting, cts);
    }

    [Fact]
    public async Task The_end_acknowledges_what_was_taken_and_turns_live_mode_off_within_RequestTimeout_whatever_the_token()
    {
        var (exchange, client) = Live(o => o.RequestTimeout = TimeSpan.FromSeconds(20));
        var ackFails = true;
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1"), ("q2", "ok:m2")), r => Status(r));
        // The first acknowledgement fails, so q1 and q2 are still waiting when the stream ends.
        exchange.On(MessagePickup.MessagesReceivedType, r => ackFails
            ? Task.FromException<InboundMessageSnapshot>(new TimeoutException())
            : Task.FromResult(Status(r)));
        exchange.On(MessagePickup.LiveDeliveryChangeType, async r =>
        {
            if (!r.Live)
                await Task.Delay(Timeout.InfiniteTimeSpan, r.Ct); // live-off never answers
            return Status(r, live: r.Live);
        });
        using var cts = new CancellationTokenSource();

        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        await Next(stream);
        await Next(stream);                              // q1 taken (q2 ready, so no flush yet)
        var waiting = stream.MoveNextAsync().AsTask();   // q2 taken; the flush fails; back-off
        await exchange.WaitForSentAsync(3);
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        Types(exchange).Should().Equal(LiveOn, Drain, Ack);
        ackFails = false;

        await cts.CancelAsync();
        await exchange.WaitForSentAsync(5);
        Types(exchange).Should().Equal(LiveOn, Drain, Ack, Ack, LiveOff);
        exchange.Sent.Skip(3).Should().OnlyContain(s => !s.Ct.IsCancellationRequested, "the end does not run on the caller's token");
        waiting.IsCompleted.Should().BeFalse("live-off has not answered");
        exchange.Clock.Advance(TimeSpan.FromSeconds(19));
        waiting.IsCompleted.Should().BeFalse();
        exchange.Clock.Advance(TimeSpan.FromSeconds(1));
        await FluentActions.Awaiting(() => PickupTest.Within(waiting)).Should().ThrowAsync<OperationCanceledException>();

        exchange.AcknowledgedIds.Should().Equal(new[] { "q1", "q2", "q1", "q2" }, "the taken messages were acknowledged again at the end");
        exchange.Registrations.Should().BeEmpty();
        await stream.DisposeAsync();
    }

    [Fact]
    public async Task While_acknowledgements_fail_at_most_100_taken_messages_are_kept_and_the_oldest_are_let_go_unacknowledged()
    {
        // L-062: what the stream keeps for a later acknowledgement is bounded, whatever the consumer takes.
        var logs = new PickupTestLogs();
        var exchange = new FakePickupExchange { Route = WebSocketRoute };
        var client = PickupTest.Client(exchange, logs);
        var acks = 0;
        exchange.On(MessagePickup.MessagesReceivedType, r => ++acks == 1
            ? Task.FromException<InboundMessageSnapshot>(new TimeoutException())
            : Task.FromResult(Status(r)));
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        exchange.Registration!.TryPush(Push(("p0", "ok:p0"))).Should().BeTrue();
        (await PickupTest.Within(first)).Should().BeTrue();
        exchange.Registration!.TryPush(Push(("p1", "ok:p1"))).Should().BeTrue();
        (await Next(stream)).Id.Should().Be("p1");

        // A push is always waiting when the next item is asked for, so the first flush comes at ten
        // taken; it fails and the stream backs off (the clock is frozen) while the consumer goes on.
        for (var i = 2; i < 122; i++)
        {
            exchange.Registration!.TryPush(Push(($"p{i}", $"ok:p{i}"))).Should().BeTrue();
            (await Next(stream)).Id.Should().Be($"p{i}");
        }
        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().ContainSingle("no acknowledgement is sent during the back-off");

        var waiting = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        exchange.Clock.Advance(LiveDeliveryStream.FirstBackoff);
        // live-on, drain, the failed acknowledgement; then ten acknowledgements of ten, the cycle's
        // live-on and its drain; then the stream waits.
        await exchange.WaitForSentAsync(15);
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        var acknowledged = exchange.SentOf(MessagePickup.MessagesReceivedType);
        acknowledged.Should().HaveCount(11);
        acknowledged[0].Ids.Should().Equal(Enumerable.Range(0, 10).Select(i => $"p{i}"));
        acknowledged.Skip(1).Should().OnlyContain(a => a.Ids.Count == LiveDeliveryStream.MaxAckIds, "a mediator may accept no more than ten ids per message");
        acknowledged.Skip(1).SelectMany(a => a.Ids).Should().Equal(Enumerable.Range(22, 100).Select(i => $"p{i}"), "the 100 most recent are kept; p0..p21 were let go");
        logs.Entries.Should().Contain(e => e.Message.Contains("forgotten unacknowledged"));
        await StopAsync(stream, waiting, cts);
    }

    [Fact]
    public async Task At_the_end_more_than_ten_taken_messages_are_acknowledged_ten_ids_per_message()
    {
        // A mediator may accept no more than its delivery batch size (ten) per messages-received.
        var exchange = new FakePickupExchange { Route = WebSocketRoute };
        var client = PickupTest.Client(exchange);
        var acks = 0;
        exchange.On(MessagePickup.MessagesReceivedType, r => ++acks == 1
            ? Task.FromException<InboundMessageSnapshot>(new TimeoutException())
            : Task.FromResult(Status(r)));
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        exchange.Registration!.TryPush(Push(("p0", "ok:p0"))).Should().BeTrue();
        (await PickupTest.Within(first)).Should().BeTrue();
        for (var i = 1; i <= 25; i++)
        {
            exchange.Registration!.TryPush(Push(($"p{i}", $"ok:p{i}"))).Should().BeTrue();
            (await Next(stream)).Id.Should().Be($"p{i}");
        }
        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().ContainSingle("the flush at ten taken failed, and the stream backs off");

        var waiting = stream.MoveNextAsync().AsTask(); // takes p25
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        await StopAsync(stream, waiting, cts);

        var sent = exchange.SentOf(MessagePickup.MessagesReceivedType);
        sent[0].Ids.Should().Equal(Enumerable.Range(0, 10).Select(i => $"p{i}"));
        sent.Skip(1).Select(a => a.Ids.Count).Should().Equal(10, 10, 6);
        sent.Skip(1).SelectMany(a => a.Ids).Should().Equal(Enumerable.Range(0, 26).Select(i => $"p{i}"));
        exchange.Sent[^1].Is(MessagePickup.LiveDeliveryChangeType).Should().BeTrue();
    }

    // ---- logs ---------------------------------------------------------------------------------

    [Fact]
    public async Task Log_lines_carry_no_DID_or_id()
    {
        var logs = new PickupTestLogs();
        var exchange = new FakePickupExchange { Route = WebSocketRoute };
        var client = PickupTest.Client(exchange, logs);
        exchange.OnSequence(MessagePickup.DeliveryRequestType,
            r => RawDelivery(r, Enumerable.Range(0, 12).Select(i => Entry($"attachment-{i}", i % 2 == 0 ? $"ok:message-{i}" : "bad:x")).Append(Entry(null, "ok:x"))),
            r => Status(r, live: false));
        exchange.On(MessagePickup.LiveDeliveryChangeType, r => Status(r, live: false));

        await PickupTest.Within(Task.Run(async () =>
        {
            await foreach (var item in client.ReceiveLiveAsync(M, L))
            {
                if (item.Id == "attachment-9")
                    break;
            }
        }));

        logs.Entries.Should().Contain(e => e.Message.Contains("live delivery off"));
        foreach (var value in new[] { M, L, A, "attachment-", "message-" })
            logs.Entries.Should().NotContain(e => e.Message.Contains(value), "no log line carries {0}", value);
    }

    // ---- helpers ------------------------------------------------------------------------------

    internal const string LiveOn = "live-on";
    internal const string LiveOff = "live-off";
    internal const string Drain = "drain";
    internal const string Ack = "ack";

    internal static (FakePickupExchange Exchange, PickupClient Client) Live(Action<MediatorClientOptions>? configure = null)
    {
        var exchange = new FakePickupExchange(configure) { Route = WebSocketRoute };
        return (exchange, PickupTest.Client(exchange));
    }

    internal static async Task<PickupItem> Next(IAsyncEnumerator<PickupItem> stream)
    {
        (await PickupTest.Within(stream.MoveNextAsync())).Should().BeTrue("an item was expected");
        return stream.Current;
    }

    /// <summary>End a stream whose MoveNextAsync is pending (an enumerator cannot be disposed mid-move): cancel, observe, dispose.</summary>
    internal static async Task StopAsync(IAsyncEnumerator<PickupItem> stream, Task<bool> pending, CancellationTokenSource cts)
    {
        await cts.CancelAsync();
        await FluentActions.Awaiting(() => PickupTest.Within(pending)).Should().ThrowAsync<OperationCanceledException>();
        await stream.DisposeAsync();
    }

    /// <summary>The requests sent, by kind.</summary>
    internal static IReadOnlyList<string> Types(FakePickupExchange exchange) => exchange.Sent.Select(s => s.Type switch
    {
        MessagePickup.LiveDeliveryChangeType => s.Live ? LiveOn : LiveOff,
        MessagePickup.DeliveryRequestType => Drain,
        MessagePickup.MessagesReceivedType => Ack,
        _ => s.Type,
    }).ToArray();
}
