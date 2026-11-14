using System.Text;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// QA-2 (#96), unit level over <see cref="FakePickupExchange"/>: cancellation at every await point
/// of <see cref="PickupClient.ReceiveLiveAsync"/> (the route, the live-on, a drain round, an unpack,
/// an acknowledgement, the wait) and of <see cref="PickupClient.FetchAsync"/> (the route, the
/// exchange, the unpack) — each a rendezvous on a gate, never a sleep (L-044); hostile delivery
/// shapes (a repeated id whose first copy is undecodable, ids at and beyond 2048 characters, ids
/// with characters outside the unreserved set, null attachments, a live push of more than ten
/// messages); <see cref="PickupClient.AckAsync"/> chunk boundaries.
/// </summary>
public sealed class PickupClientQa2Tests
{
    // ---- live: cancellation at every await point ------------------------------------------------

    [Fact]
    public async Task Live_cancelled_while_the_route_is_resolved_sends_nothing_and_opens_no_stream()
    {
        var inner = new FakePickupExchange { Route = WebSocketRoute };
        var gate = new Gate();
        var exchange = new GatedRouteExchange(inner, gate);
        var client = new PickupClient(exchange, logger: null);
        using var cts = new CancellationTokenSource();
        await using var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        var first = stream.MoveNextAsync().AsTask();
        await gate.Entered.WaitAsync(PickupTest.Timeout);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => PickupTest.Within(first)).Should().ThrowAsync<OperationCanceledException>();
        inner.Sent.Should().BeEmpty("nothing was sent, not even live-off");
        inner.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task Live_cancelled_during_the_live_on_turns_live_off_at_the_end_and_closes_the_mailbox()
    {
        var (exchange, client) = PickupClientLiveStreamTests.Live();
        var gate = new Gate();
        exchange.On(MessagePickup.LiveDeliveryChangeType, async r =>
        {
            if (r.Live)
                await gate.HoldAsync(r.Ct);
            return Status(r, live: r.Live);
        });
        using var cts = new CancellationTokenSource();
        await using var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        var first = stream.MoveNextAsync().AsTask();
        await gate.Entered.WaitAsync(PickupTest.Timeout);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => PickupTest.Within(first)).Should().ThrowAsync<OperationCanceledException>();
        PickupClientLiveStreamTests.Types(exchange).Should().Equal(PickupClientLiveStreamTests.LiveOn, PickupClientLiveStreamTests.LiveOff);
        exchange.Sent[^1].Ct.IsCancellationRequested.Should().BeFalse("the end runs on its own deadline");
        exchange.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task Live_cancelled_during_a_drain_round_acknowledges_what_was_taken_and_nothing_else()
    {
        var (exchange, client) = PickupClientLiveStreamTests.Live();
        var gate = new Gate();
        var round = 0;
        exchange.On(MessagePickup.DeliveryRequestType, async r =>
        {
            if (Interlocked.Increment(ref round) == 1)
                return Delivery(r, ("q1", "ok:m1"));
            await gate.HoldAsync(r.Ct);
            return Status(r);
        });
        using var cts = new CancellationTokenSource();
        await using var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        (await PickupClientLiveStreamTests.Next(stream)).Id.Should().Be("q1");

        // Asking for the next takes q1 and acknowledges it (nothing ready); the second drain round hangs.
        var second = stream.MoveNextAsync().AsTask();
        await gate.Entered.WaitAsync(PickupTest.Timeout);
        exchange.AcknowledgedIds.Should().Equal("q1");
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => PickupTest.Within(second)).Should().ThrowAsync<OperationCanceledException>();
        exchange.AcknowledgedIds.Should().Equal("q1");
        exchange.Sent[^1].Is(MessagePickup.LiveDeliveryChangeType).Should().BeTrue();
        exchange.Sent[^1].Live.Should().BeFalse();
        exchange.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task Live_cancelled_during_an_unpack_leaves_that_message_and_acknowledges_the_one_taken_before()
    {
        var (exchange, client) = PickupClientLiveStreamTests.Live();
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1"), ("q2", "ok:m2")), r => Status(r));
        var gate = new Gate();
        exchange.Unpacker = async (text, ct) =>
        {
            if (text == "ok:m2")
                await gate.HoldAsync(ct);
            return DefaultOutcome(text);
        };
        using var cts = new CancellationTokenSource();
        await using var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        (await PickupClientLiveStreamTests.Next(stream)).Id.Should().Be("q1");

        var second = stream.MoveNextAsync().AsTask();
        await gate.Entered.WaitAsync(PickupTest.Timeout);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => PickupTest.Within(second)).Should().ThrowAsync<OperationCanceledException>();
        exchange.AcknowledgedIds.Should().Equal(new[] { "q1" }, "q1 was taken when q2 was asked for; q2 never reached the consumer");
        exchange.Duplicates.Contains(DefaultOutcome("ok:m2").DedupeKey).Should().BeFalse();
        exchange.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task Live_cancelled_during_an_acknowledgement_sends_it_again_at_the_end_on_its_own_deadline()
    {
        var (exchange, client) = PickupClientLiveStreamTests.Live();
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1")), r => Status(r));
        var gate = new Gate();
        var acks = 0;
        exchange.On(MessagePickup.MessagesReceivedType, async r =>
        {
            if (Interlocked.Increment(ref acks) == 1)
                await gate.HoldAsync(r.Ct);
            return Status(r);
        });
        using var cts = new CancellationTokenSource();
        await using var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        (await PickupClientLiveStreamTests.Next(stream)).Id.Should().Be("q1");

        var second = stream.MoveNextAsync().AsTask();
        await gate.Entered.WaitAsync(PickupTest.Timeout);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => PickupTest.Within(second)).Should().ThrowAsync<OperationCanceledException>();
        var sentAcks = exchange.SentOf(MessagePickup.MessagesReceivedType);
        sentAcks.Should().HaveCount(2, "the cancelled acknowledgement is sent again by the end");
        sentAcks[1].Ids.Should().Equal("q1");
        sentAcks[1].Ct.IsCancellationRequested.Should().BeFalse();
        exchange.Sent[^1].Live.Should().BeFalse();
    }

    [Fact]
    public async Task Live_cancelled_while_the_consumer_holds_an_item_and_then_disposing_without_asking_again_takes_nothing()
    {
        var (exchange, client) = PickupClientLiveStreamTests.Live();
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1"), ("q2", "ok:m2")), r => Status(r));
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        (await PickupClientLiveStreamTests.Next(stream)).Id.Should().Be("q1");
        (await PickupClientLiveStreamTests.Next(stream)).Id.Should().Be("q2");
        await cts.CancelAsync();

        await PickupTest.Within(stream.DisposeAsync());

        exchange.AcknowledgedIds.Should().Equal(new[] { "q1" }, "q1 was taken when q2 was asked for; q2 was the last yielded");
        exchange.Duplicates.Count.Should().Be(1);
    }

    /// <summary>
    /// <b>Q2-2</b> (#96 design §4.8 "Cycle failures. A cycle failing with <c>TimeoutException</c> /
    /// <c>TransportException</c> is retried with backoff"; <see cref="PickupClient.ReceiveLiveAsync"/>
    /// remarks: "A step that fails (no answer, a transport failure) is retried"). A step that fails for
    /// a reason no retry can fix — here the pack stage cannot find this agent's key for <c>from</c>
    /// (<see cref="SecretNotFoundException"/>) — must end the stream at once, as it ends
    /// <see cref="PickupClient.FetchAsync"/>. The stream retries every exception other than a
    /// <see cref="MediationProtocolException"/>, <see cref="ObjectDisposedException"/> or the caller's
    /// cancellation, so the caller waits through four back-offs (1 + 2 + 4 + 8 s) and five identical
    /// failures before it sees the error.
    /// </summary>
    [Fact]
    public async Task Q2_2_A_permanent_pack_stage_failure_ends_the_live_stream_at_once_instead_of_after_four_back_offs()
    {
        var (exchange, client) = PickupClientLiveStreamTests.Live();
        exchange.On(MessagePickup.LiveDeliveryChangeType, r =>
            r.Live ? throw new SecretNotFoundException(L + "#key-1") : Status(r, live: false));
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        try
        {
            var backingOff = exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
            await Task.WhenAny(first, backingOff);

            first.IsCompleted.Should().BeTrue("a failure no retry can fix is not retried after a back-off");
            await FluentActions.Awaiting(() => first).Should().ThrowAsync<SecretNotFoundException>();
            exchange.SentOf(MessagePickup.LiveDeliveryChangeType).Count(s => s.Live).Should().Be(1);
        }
        finally
        {
            // A MoveNextAsync still pending (the stream backing off) is ended through its token.
            await cts.CancelAsync();
            try
            {
                await first.WaitAsync(PickupTest.Timeout);
            }
            catch (Exception)
            {
                // The outcome was asserted above.
            }
            await stream.DisposeAsync();
        }
    }

    /// <summary>The same failure on a fetch surfaces at once (the contrast for Q2-2).</summary>
    [Fact]
    public async Task A_pack_stage_failure_ends_a_fetch_at_once()
    {
        var exchange = new FakePickupExchange();
        exchange.On(MessagePickup.DeliveryRequestType, (Func<SentRequest, InboundMessageSnapshot>)(_ => throw new SecretNotFoundException(L + "#key-1")));

        await FluentActions.Awaiting(() => PickupTest.Client(exchange).FetchAsync(M, L, 10)).Should().ThrowAsync<SecretNotFoundException>();
    }

    // ---- fetch: cancellation at every await point -----------------------------------------------

    [Fact]
    public async Task Fetch_cancelled_while_the_route_is_resolved_sends_nothing()
    {
        var inner = new FakePickupExchange();
        var gate = new Gate();
        var client = new PickupClient(new GatedRouteExchange(inner, gate), logger: null);
        using var cts = new CancellationTokenSource();

        var fetch = client.FetchAsync(M, L, 10, ct: cts.Token);
        await gate.Entered.WaitAsync(PickupTest.Timeout);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => PickupTest.Within(fetch)).Should().ThrowAsync<OperationCanceledException>();
        inner.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Fetch_cancelled_while_waiting_for_the_delivery_acknowledges_and_remembers_nothing()
    {
        var exchange = new FakePickupExchange();
        var gate = new Gate();
        exchange.On(MessagePickup.DeliveryRequestType, async r =>
        {
            await gate.HoldAsync(r.Ct);
            return Delivery(r, ("q1", "ok:m1"));
        });
        using var cts = new CancellationTokenSource();
        var fetch = PickupTest.Client(exchange).FetchAsync(M, L, 10, ct: cts.Token);
        await gate.Entered.WaitAsync(PickupTest.Timeout);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => PickupTest.Within(fetch)).Should().ThrowAsync<OperationCanceledException>();
        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().BeEmpty();
        exchange.Duplicates.Count.Should().Be(0);
    }

    [Fact]
    public async Task Fetch_cancelled_during_an_unpack_acknowledges_and_remembers_nothing()
    {
        var exchange = new FakePickupExchange();
        exchange.On(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1"), ("q2", "ok:m2")));
        var gate = new Gate();
        exchange.Unpacker = async (text, ct) =>
        {
            if (text == "ok:m2")
                await gate.HoldAsync(ct);
            return DefaultOutcome(text);
        };
        using var cts = new CancellationTokenSource();
        var fetch = PickupTest.Client(exchange).FetchAsync(M, L, 10, ct: cts.Token);
        await gate.Entered.WaitAsync(PickupTest.Timeout);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => PickupTest.Within(fetch)).Should().ThrowAsync<OperationCanceledException>();
        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().BeEmpty("q1 had unpacked, but nothing is acknowledged before the commit");
        exchange.Duplicates.Count.Should().Be(0);
    }

    // ---- hostile delivery shapes ---------------------------------------------------------------

    [Fact]
    public async Task A_repeated_id_whose_first_copy_is_undecodable_is_one_failure_and_nothing_is_acknowledged()
    {
        var exchange = new FakePickupExchange();
        exchange.On(MessagePickup.DeliveryRequestType, r => RawDelivery(r, new[] { PoisonedAttachment("q1"), Entry("q1", "ok:m1") }));

        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, 10);

        batch.Items.Should().ContainSingle().Which.Error.Should().BeOfType<MalformedMessageException>();
        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().BeEmpty("the first copy wins and it failed, so the id is not acknowledged");
        exchange.Unpacked.Should().BeEmpty();
    }

    [Fact]
    public async Task Ids_up_to_2048_unreserved_characters_are_acknowledged_as_given_and_longer_or_other_characters_are_ignored()
    {
        var exchange = new FakePickupExchange();
        var longest = new string('a', 2047) + "~";
        var attachments = new Attachment?[]
        {
            Entry(longest, "ok:m1"),
            Entry(new string('b', 2049), "ok:m2"),
            Entry("q%20x", "ok:m3"),
            Entry("q/slash", "ok:m4"),
            Entry("qé", "ok:m5"),
            null,
            Entry("q.ok_-~", "ok:m6"),
        };
        exchange.On(MessagePickup.DeliveryRequestType, r => RawDelivery(r, attachments));

        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, 10);

        batch.Items.Select(i => i.Id).Should().Equal(longest, "q.ok_-~");
        batch.Items.Should().OnlyContain(i => i.Unpacked != null);
        exchange.AcknowledgedIds.Should().Equal(longest, "q.ok_-~");
        exchange.Unpacked.Should().Equal(new[] { "ok:m1", "ok:m6" }, "an attachment that cannot be acknowledged is not opened either");
    }

    [Fact]
    public async Task A_live_push_of_more_than_ten_messages_hands_out_ten_and_never_acknowledges_the_rest()
    {
        var (exchange, client) = PickupClientLiveStreamTests.Live();
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        var messages = Enumerable.Range(1, 12).Select(i => ($"p{i}", $"ok:m{i}")).ToArray();
        exchange.Registration!.TryPush(Push(messages)).Should().BeTrue();

        (await PickupTest.Within(first)).Should().BeTrue();
        var ids = new List<string> { stream.Current.Id };
        for (var i = 1; i < 10; i++)
            ids.Add((await PickupClientLiveStreamTests.Next(stream)).Id);
        var waiting = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        waiting.IsCompleted.Should().BeFalse("the eleventh and twelfth were not read");
        await PickupClientLiveStreamTests.StopAsync(stream, waiting, cts);

        ids.Should().Equal(Enumerable.Range(1, 10).Select(i => $"p{i}"));
        exchange.AcknowledgedIds.Should().Equal(Enumerable.Range(1, 10).Select(i => $"p{i}"));
        exchange.Unpacked.Should().NotContain(new[] { "ok:m11", "ok:m12" });
    }

    [Fact]
    public async Task A_delivery_with_zero_attachments_is_drained_and_a_delivery_of_only_unusable_ids_is_not()
    {
        var exchange = new FakePickupExchange();
        exchange.OnSequence(MessagePickup.DeliveryRequestType,
            r => RawDelivery(r, Array.Empty<Attachment?>()),
            r => RawDelivery(r, new Attachment?[] { Entry(null, "ok:m1"), Entry("", "ok:m2") }));
        var client = PickupTest.Client(exchange);

        var empty = await client.FetchAsync(M, L, 10);
        empty.Drained.Should().BeTrue();
        empty.Status.Should().BeNull();

        var unusable = await client.FetchAsync(M, L, 10);
        unusable.Items.Should().BeEmpty();
        unusable.Drained.Should().BeFalse("the mediator did deliver something (observation: a loop on Drained against a mediator that keeps doing this never ends)");
        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().BeEmpty();
    }

    // ---- AckAsync chunk boundaries -------------------------------------------------------------

    [Theory]
    [InlineData(100, new[] { 100 })]
    [InlineData(101, new[] { 100, 1 })]
    [InlineData(250, new[] { 100, 100, 50 })]
    public async Task AckAsync_chunks_at_100_in_order(int count, int[] chunks)
    {
        var exchange = new FakePickupExchange();
        var ids = Enumerable.Range(0, count).Select(i => $"id{i}").ToArray();

        await PickupTest.Client(exchange).AckAsync(M, L, ids);

        var sent = exchange.SentOf(MessagePickup.MessagesReceivedType);
        sent.Select(s => s.Ids.Count).Should().Equal(chunks);
        exchange.AcknowledgedIds.Should().Equal(ids);
    }

    [Fact]
    public async Task AckAsync_refuses_an_id_of_2049_characters_and_a_null_id_anywhere_before_sending()
    {
        var exchange = new FakePickupExchange();
        var client = PickupTest.Client(exchange);
        var many = Enumerable.Range(0, 10_000).Select(i => $"id{i}").ToArray();
        many[9_999] = new string('a', 2049);

        await FluentActions.Awaiting(() => client.AckAsync(M, L, many)).Should().ThrowAsync<ArgumentException>().WithMessage("*9999*");
        await FluentActions.Awaiting(() => client.AckAsync(M, L, new[] { "id0", null! })).Should().ThrowAsync<ArgumentException>();
        (await client.AckAsync(M, L, new[] { new string('a', 2048) })).Should().NotBeNull();
        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().ContainSingle();
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>A rendezvous: the step signals that it entered, then waits for its token.</summary>
    private sealed class Gate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public async Task HoldAsync(CancellationToken ct)
        {
            _entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    /// <summary>An exchange whose route resolution waits at a gate; everything else is the inner fake's.</summary>
    private sealed class GatedRouteExchange(FakePickupExchange inner, Gate gate) : IMediatorExchange
    {
        public MediatorClientOptions Options => inner.Options;
        public TimeProvider Time => inner.Time;
        public DuplicateFilter Duplicates => inner.Duplicates;
        public NegotiatedVersionCache Versions => inner.Versions;

        public async Task<MediatorRoute> ResolveRouteAsync(string mediatorDid, Uri? serviceEndpointOverride, bool requireWebSocket, CancellationToken ct)
        {
            await gate.HoldAsync(ct);
            return await inner.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket, ct);
        }

        public Task<InboundMessageSnapshot> ExchangeAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, CancellationToken ct)
            => inner.ExchangeAsync(route, request, spec, mediatorDid, localDid, ct);

        public LiveRegistration RegisterLiveStream(string mediatorDid, string localDid) => inner.RegisterLiveStream(mediatorDid, localDid);

        public Task<DeliveredOutcome> UnpackDeliveredAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => inner.UnpackDeliveredAsync(payload, ct);

        public Task<IReadOnlyList<DeliveredOutcome>> UnpackDeliveredBatchAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct)
            => inner.UnpackDeliveredBatchAsync(payloads, ct);
    }
}
