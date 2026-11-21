using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using Microsoft.Extensions.Logging;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;
using static DidComm.Mediator.Tests.Client.PickupClientLiveStreamTests;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="PickupClient.ReceiveLiveAsync"/> speaking Message Pickup 4.0 (#100 DC-6, DC-8): the
/// version is settled once, when the stream starts (a query that fails ends the stream and frees its
/// place); every request and answer is the stream's version's (a drain ends at an empty 4.0
/// <c>delivery</c>); a push of the other version is ignored and never acknowledged; and
/// acknowledgements are sent one way, so a mediator that never answers them neither stalls the
/// stream nor makes it back off. The stream's scheduling is 3.0's, unchanged (owner decision on #96,
/// 2026-11-14): these tests read it, they do not re-test it.
/// </summary>
public sealed class PickupClientLiveV4Tests
{
    private const string LiveOn4 = "live-on (4.0)";
    private const string LiveOff4 = "live-off (4.0)";
    private const string Drain4 = "drain (4.0)";
    private const string Ack4 = "ack (4.0, one way)";
    private const string Query = "discover-features";

    private static (FakePickupExchange Exchange, PickupClient Client) Live4(Action<MediatorClientOptions>? configure = null)
        => Live(o =>
        {
            o.MessagePickupVersion = MessagePickupVersion.V4;
            configure?.Invoke(o);
        });

    // ---- the flow -------------------------------------------------------------------------------

    [Fact]
    public async Task Under_4_0_the_cycle_drains_until_an_empty_delivery_and_acknowledges_one_way()
    {
        var (exchange, client) = Live4();
        exchange.OnSequence(MessagePickup40.DeliveryRequestType,
            r => Delivery40(r, ("q1", "ok:m1"), ("q2", "ok:m2")),
            r => Delivery40(r));

        var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();
        try
        {
            (await Next(stream)).Id.Should().Be("q1");
            Types4(exchange).Should().Equal(LiveOn4, Drain4);
            exchange.SentOf(MessagePickup40.DeliveryRequestType)[0].MessageCountLimit.Should().Be(LiveDeliveryStream.DrainLimit);
            (await Next(stream)).Id.Should().Be("q2");

            var third = stream.MoveNextAsync().AsTask();
            await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
            third.IsCompleted.Should().BeFalse("the empty delivery ended the drain: the stream waits for a push or the next cycle");
            Types4(exchange).Should().Equal(LiveOn4, Drain4, Ack4, Drain4);
            exchange.AcknowledgedIds.Should().Equal("q1", "q2");

            exchange.Registration!.TryPush(Push40(("q3", "ok:m3"))).Should().BeTrue();
            (await PickupTest.Within(third)).Should().BeTrue();
            stream.Current.Id.Should().Be("q3");
        }
        finally
        {
            await PickupTest.Within(stream.DisposeAsync());
        }

        Types4(exchange)[^1].Should().Be(LiveOff4, "live mode is turned off with a 4.0 live-delivery-change");
        exchange.Sent[^1].OneWay.Should().BeFalse("live-delivery-change is answered with a status (4.0 L340)");
        exchange.AcknowledgedIds.Should().Equal(new[] { "q1", "q2" }, "q3 was the last item yielded");
        exchange.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task Under_4_0_a_flush_waits_for_no_answer_so_a_mediator_that_never_answers_acknowledgements_neither_stalls_nor_backs_off_the_stream()
    {
        var (exchange, client) = Live4();
        exchange.OnSequence(MessagePickup40.DeliveryRequestType,
            r => Delivery40(r, ("q1", "ok:m1"), ("q2", "ok:m2"), ("q3", "ok:m3")),
            r => Delivery40(r));
        // A 4.0 mediator answers no messages-received (L62, L294): asked for an answer, it would
        // never come. The one-way send itself completes, as a sent frame does.
        exchange.On(MessagePickup40.MessagesReceivedType, NeverAnswered);
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        (await Next(stream)).Id.Should().Be("q1");
        (await Next(stream)).Id.Should().Be("q2");
        (await Next(stream)).Id.Should().Be("q3");
        var mark = exchange.Clock.Mark();
        var parked = stream.MoveNextAsync().AsTask();

        // The next timer the stream sets is the next cycle, one LivePollInterval after the drain
        // ended: the flush neither waited out a RequestTimeout nor set a back-off.
        await exchange.Clock.WaitForTimerAsync(exchange.Options.LivePollInterval, mark, PickupTest.Timeout);
        exchange.Clock.ScheduledTimersSince(mark).Should().Be(1);
        exchange.AcknowledgedIds.Should().Equal("q1", "q2", "q3");
        exchange.SentOf(MessagePickup40.MessagesReceivedType).Should().OnlyContain(s => s.OneWay);
        parked.IsCompleted.Should().BeFalse();
        await StopAsync(stream, parked, cts);
    }

    [Fact]
    public async Task Under_4_0_the_end_acknowledges_what_was_taken_one_way_then_turns_live_delivery_off()
    {
        var (exchange, client) = Live4();
        exchange.OnSequence(MessagePickup40.DeliveryRequestType, r => Delivery40(r, ("q1", "ok:m1"), ("q2", "ok:m2")), r => Delivery40(r));

        await using (var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator())
        {
            (await Next(stream)).Id.Should().Be("q1");
            (await Next(stream)).Id.Should().Be("q2"); // q1 is taken; q2 was ready, so nothing was flushed yet
            exchange.AcknowledgedIds.Should().BeEmpty();
        }

        Types4(exchange).Should().Equal(LiveOn4, Drain4, Ack4, LiveOff4);
        exchange.AcknowledgedIds.Should().Equal("q1");
    }

    [Fact]
    public async Task Under_4_0_a_drain_answered_with_more_messages_than_its_limit_ends_the_stream_before_anything_is_opened()
    {
        var (exchange, client) = Live4();
        var tooMany = Enumerable.Range(0, LiveDeliveryStream.DrainLimit + 1).Select(i => ($"q{i}", $"ok:m{i}")).ToArray();
        exchange.On(MessagePickup40.DeliveryRequestType, r => Delivery40(r, tooMany));

        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();
        var act = async () => await stream.MoveNextAsync();

        (await act.Should().ThrowAsync<MediationProtocolException>()).Which.Message.Should().Contain("message_count_limit");
        exchange.Batches.Should().BeEmpty();
        exchange.AcknowledgedIds.Should().BeEmpty();
    }

    // ---- the other version's pushes -------------------------------------------------------------

    [Fact]
    public async Task A_3_0_push_to_a_4_0_stream_is_ignored_and_never_acknowledged()
    {
        var logs = new PickupTestLogs();
        var exchange = new FakePickupExchange(o => o.MessagePickupVersion = MessagePickupVersion.V4) { Route = WebSocketRoute };
        var client = PickupTest.Client(exchange, logs);
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        exchange.Registration!.TryPush(Push(("p1", "ok:p1"))).Should().BeTrue("the channel hands every thread-less delivery to the stream of its pair");
        exchange.Registration!.TryPush(Push40(("p2", "ok:p2"))).Should().BeTrue();
        (await PickupTest.Within(first)).Should().BeTrue();
        stream.Current.Id.Should().Be("p2");
        var parked = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        exchange.Unpacked.Should().Equal(new[] { "ok:p2" }, "the 3.0 push was never opened");
        exchange.AcknowledgedIds.Should().Equal(new[] { "p2" }, "the 3.0 push was never acknowledged");
        logs.Entries.Should().Contain(e => e.Level == LogLevel.Debug && e.Message.Contains("another Message Pickup version", StringComparison.Ordinal));
        await StopAsync(stream, parked, cts);
    }

    [Fact]
    public async Task A_4_0_push_to_a_3_0_stream_is_ignored_and_never_acknowledged()
    {
        var (exchange, client) = Live(); // 3.0, the fake's pin
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        exchange.Registration!.TryPush(Push40(("p1", "ok:p1"))).Should().BeTrue();
        exchange.Registration!.TryPush(Push(("p2", "ok:p2"))).Should().BeTrue();
        (await PickupTest.Within(first)).Should().BeTrue();
        stream.Current.Id.Should().Be("p2");
        var parked = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        exchange.Unpacked.Should().Equal("ok:p2");
        exchange.AcknowledgedIds.Should().Equal("p2");
        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().OnlyContain(s => !s.OneWay, "3.0 acknowledgements are still exchanges");
        await StopAsync(stream, parked, cts);
    }

    // ---- the version, once per stream -----------------------------------------------------------

    [Fact]
    public async Task A_version_query_that_fails_at_the_start_ends_the_stream_at_once_and_frees_its_place()
    {
        var (exchange, client) = Live(o => o.MessagePickupVersion = MessagePickupVersion.Negotiate);
        var answer = false;
        exchange.On(DiscoverFeatures.QueriesType, r => Volatile.Read(ref answer)
            ? Task.FromResult(Snapshot(DiscoverFeatures.CreateDisclose(M, L, r.Request.Id, PickupClientNegotiationTests.Protocol("https://didcomm.org/message-pickup/4.0", "mediator"))))
            : Task.FromException<InboundMessageSnapshot>(new TimeoutException($"Mediator request '{r.Request.Id}' (discover-features query) was not answered.")));

        await using (var failed = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator())
        {
            var act = async () => await failed.MoveNextAsync();
            await act.Should().ThrowAsync<TimeoutException>("a failed query is not retried");
        }

        Types4(exchange).Should().Equal(new[] { Query }, "nothing was turned on, so nothing is turned off");
        exchange.Registrations.Should().BeEmpty("the mailbox is closed when the version cannot be settled");

        Volatile.Write(ref answer, true);
        using var cts = new CancellationTokenSource();
        var second = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var waiting = second.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        Types4(exchange).Should().Equal(Query, Query, LiveOn4, Drain4);
        await StopAsync(second, waiting, cts);
    }

    [Fact]
    public async Task A_mediator_that_discloses_neither_version_ends_the_stream_at_once_and_frees_its_place()
    {
        var exchange = PickupClientNegotiationTests.Negotiating(PickupClientNegotiationTests.Protocol("https://didcomm.org/coordinate-mediation/3.0", "mediator"));
        exchange.Route = WebSocketRoute;
        var client = PickupTest.Client(exchange);

        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();
        var act = async () => await stream.MoveNextAsync();

        await act.Should().ThrowAsync<MediationNotSupportedException>();
        exchange.Registrations.Should().BeEmpty();
        Types4(exchange).Should().Equal(Query);
    }

    [Fact]
    public async Task The_version_is_settled_once_and_kept_for_the_streams_life()
    {
        // Nothing is remembered between calls (capacity 0), so only the stream keeps the version.
        var exchange = PickupClientNegotiationTests.Negotiating(PickupClientNegotiationTests.Protocol("https://didcomm.org/message-pickup/4.0", "mediator"));
        exchange.PickupVersions = new NegotiatedVersionCache<MessagePickupVersion>(exchange.Clock, capacity: 0);
        exchange.Route = WebSocketRoute;
        var client = PickupTest.Client(exchange);
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var mark = exchange.Clock.Mark();
        var waiting = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForTimerAsync(exchange.Options.LivePollInterval, mark, PickupTest.Timeout);

        mark = exchange.Clock.Mark();
        exchange.Clock.Advance(exchange.Options.LivePollInterval).Should().Be(1, "the next cycle is the only timer");
        await exchange.Clock.WaitForTimerAsync(exchange.Options.LivePollInterval, mark, PickupTest.Timeout);

        Types4(exchange).Should().Equal(Query, LiveOn4, Drain4, LiveOn4, Drain4);
        await StopAsync(stream, waiting, cts);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static async Task<InboundMessageSnapshot> NeverAnswered(SentRequest request)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, request.Ct);
        throw new InvalidOperationException("A delay without end ended.");
    }

    /// <summary>The requests sent, by kind and version.</summary>
    private static IReadOnlyList<string> Types4(FakePickupExchange exchange) => exchange.Sent.Select(s => s.Type switch
    {
        MessagePickup40.LiveDeliveryChangeType => s.Live ? LiveOn4 : LiveOff4,
        MessagePickup40.DeliveryRequestType => Drain4,
        MessagePickup40.MessagesReceivedType when s.OneWay => Ack4,
        DiscoverFeatures.QueriesType => Query,
        MessagePickup.LiveDeliveryChangeType => s.Live ? LiveOn : LiveOff,
        MessagePickup.DeliveryRequestType => Drain,
        MessagePickup.MessagesReceivedType => Ack,
        _ => s.Type,
    }).ToArray();
}
