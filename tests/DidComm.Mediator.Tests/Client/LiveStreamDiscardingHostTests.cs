using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// Hosts that discard the failed items a live stream yields (<see cref="PickupClient.AckAsync"/>),
/// through the stream itself (#111): the careful host, which discards a failed item unless its
/// <see cref="PickupItem.NotAttempted"/> is true, and a host that discards every failed item. The
/// stream runs over a real unpacker and its memory on a simulated clock (<see cref="SimExchange"/>),
/// against a mediator that leases what it delivers; the host handles each item before it asks for
/// the next. Nothing sleeps, and real time is only a hang guard.
/// </summary>
public sealed class LiveStreamDiscardingHostTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Lease = new MessagePickupOptions().LeaseTimeout; // 60 s

    /// <summary>
    /// The careful host on a live stream with N messages that never open queued in front of one
    /// that does: the last message is yielded to it opened after the simulated time given. A drain
    /// round brings at most ten messages, fewer than are opened at once, so every message of a
    /// round is started and no item is yielded as not attempted.
    /// </summary>
    [Theory]
    [InlineData(4, 20)]
    [InlineData(16, 40)]
    [InlineData(17, 40)]
    [InlineData(99, 200)]
    [InlineData(300, 600)]
    public async Task On_a_live_stream_a_host_that_discards_only_what_was_tried_receives_the_message_behind_N_that_never_open(int inFront, int seconds)
    {
        LiveDeliveryStream.MaxPerDelivery.Should().BeLessThanOrEqualTo(DeliveredMessageUnpacker.FetchConcurrency);
        var exchange = new SimExchange();
        exchange.BeforeBatch = () => Qa1.LookupsEnded(exchange.Unpacker);
        var mediator = new LeasingMediator(exchange, Lease);
        for (var i = 0; i < inFront; i++)
            mediator.Enqueue($"never-{i}", $"stall:never-{i}");
        mediator.Enqueue("honest", "ok:honest");
        await using var host = new HostedStream(exchange, Qa1Discard.WhatWasTried);

        await host.RunAsync(() => host.Opened("honest") is not null, until: TimeSpan.FromHours(1));

        output.WriteLine($"{inFront} in front: yielded opened at {(host.Opened("honest") is { } at ? at.ToString() : "never")} of {exchange.Clock.Now} simulated; {exchange.Loads.Count} deliveries opened, {exchange.HostCalls} host calls, {host.Items.Count(i => i.Item.NotAttempted)} items yielded as not attempted, {host.Discarded.Count} discarded; ended with {host.EndedWith?.GetType().Name ?? "nothing"}");
        host.EndedWith.Should().BeNull();
        host.Opened("honest").Should().Be(TimeSpan.FromSeconds(seconds));
        host.Items.Should().NotContain(i => i.Item.NotAttempted, "every message of a drain round is started");
        host.Discarded.Should().HaveCount(inFront, "each message in front was tried once, yielded as a failure and discarded");
        exchange.HostCalls.Should().Be(inFront + 1, "each message was handed to host code once");
    }

    /// <summary>
    /// Messages refused for capacity, on a live stream: two messages that would open are queued
    /// while the mailbox's bound for messages naming no sender is held (as sixteen lookups of earlier
    /// deliveries that have not ended hold it; the test takes those reservations itself). In each
    /// cycle while it is held the stream yields both as failed items that were not attempted, once
    /// per cycle, and calls no host code for them. The careful host leaves them queued, and the
    /// first cycle after the bound is free yields both opened. A host that discards every failed
    /// item deleted them in the first cycle, and never receives them.
    /// </summary>
    [Theory]
    [InlineData(Qa1Discard.WhatWasTried, true)]
    [InlineData(Qa1Discard.EveryFailure, false)]
    public async Task On_a_live_stream_messages_refused_for_capacity_are_not_attempted_until_it_is_free_and_only_a_host_that_left_them_queued_receives_them(Qa1Discard discard, bool received)
    {
        var exchange = new SimExchange();
        var mediator = new LeasingMediator(exchange, Lease);
        mediator.Enqueue("m-1", "ok:m-1");
        mediator.Enqueue("m-2", "ok:m-2");
        var held = new List<UnpackReservations.Reservation>();
        for (var i = 0; i < UnpackReservations.MaxPerAnonymous; i++)
        {
            exchange.Unpacker.Reservations.TryTake(SimExchange.Scope, sender: null, out var reservation, out _).Should().BeTrue();
            held.Add(reservation!);
        }
        await using var host = new HostedStream(exchange, discard);
        var cyclesWhileHeld = received ? 2 : 1;

        // While the bound is held: each cycle yields the two as not attempted (the host that
        // discards every failure gets them once: its discard removes them).
        await host.RunAsync(() => host.Items.Count == 2 * cyclesWhileHeld, until: TimeSpan.FromMinutes(10));

        host.Items.Should().HaveCount(2 * cyclesWhileHeld);
        host.Items.Select(i => i.Item.Id).Should().Equal(Enumerable.Repeat(new[] { "m-1", "m-2" }, cyclesWhileHeld).SelectMany(ids => ids));
        host.Items.Should().OnlyContain(i => i.Item.NotAttempted && i.Item.Error is TimeoutException && i.Item.Error.Message.Contains("capacity exhausted"));
        host.Items.Select(i => i.At).Should().Equal(Enumerable.Range(0, cyclesWhileHeld).SelectMany(cycle => new[] { Lease * cycle, Lease * cycle }), "they are yielded once per cycle, a poll interval apart");
        exchange.HostCalls.Should().Be(0, "a refused message's host code does not start");
        mediator.Queued.Should().Equal(received ? new[] { "m-1", "m-2" } : Array.Empty<string>(), "only the host that left them queued still has them");

        foreach (var reservation in held)
            reservation.Release();
        await host.RunAsync(() => host.Opened("m-2") is not null, until: TimeSpan.FromMinutes(10));

        output.WriteLine($"the host discards {discard}: m-1 opened at {(host.Opened("m-1") is { } a ? a.ToString() : "never")}, m-2 at {(host.Opened("m-2") is { } b ? b.ToString() : "never")} of {exchange.Clock.Now} simulated; {host.Items.Count} items yielded, {host.Discarded.Count} discarded, {exchange.HostCalls} host calls");
        host.EndedWith.Should().BeNull();
        if (received)
        {
            host.Opened("m-1").Should().Be(Lease * cyclesWhileHeld, "the first cycle after the bound was free");
            host.Opened("m-2").Should().Be(Lease * cyclesWhileHeld);
            host.Discarded.Should().BeEmpty();
        }
        else
        {
            host.Opened("m-1").Should().BeNull();
            host.Opened("m-2").Should().BeNull();
            host.Discarded.Should().Equal("m-1", "m-2");
            exchange.HostCalls.Should().Be(0, "they were never tried");
        }
    }

    /// <summary>
    /// One live stream and its host, in simulated time. The host handles each item when it is
    /// yielded, before it asks for the next one: it discards a failed item with
    /// <see cref="PickupClient.AckAsync"/> according to its <see cref="Qa1Discard"/>. Each time the
    /// stream waits, the clock moves to its next timer.
    /// </summary>
    private sealed class HostedStream : IAsyncDisposable
    {
        private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(20);
        private readonly SimExchange _exchange;
        private readonly Qa1Discard _discard;
        private readonly PickupClient _client;
        private readonly CancellationTokenSource _cts = new();
        private readonly IAsyncEnumerator<PickupItem> _stream;
        private readonly object _gate = new();
        private int _waits;
        private TaskCompletionSource _waited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task<bool>? _move;
        private int _mark;
        private bool _parked;
        private bool _ended;

        public HostedStream(SimExchange exchange, Qa1Discard discard)
        {
            _exchange = exchange;
            _discard = discard;
            _client = new PickupClient(exchange, logger: null);
            _stream = LiveDeliveryStream.ReceiveAsync(exchange, new PickupLog(logger: null), FakePickupExchange.M, FakePickupExchange.L, serviceEndpointOverride: null,
                static (_, _, _, _) => Task.FromResult(MessagePickupVersion.V3), _cts.Token, OnWaitPublished).GetAsyncEnumerator(_cts.Token);
        }

        /// <summary>The items yielded so far, with the simulated time of each.</summary>
        public List<(PickupItem Item, TimeSpan At)> Items { get; } = new();

        /// <summary>The ids the host discarded, in order.</summary>
        public List<string> Discarded { get; } = new();

        /// <summary>The exception the stream ended with, if it ended with one.</summary>
        public Exception? EndedWith { get; private set; }

        /// <summary>When the message with <paramref name="id"/> was yielded opened (simulated time), or <c>null</c>.</summary>
        public TimeSpan? Opened(string id)
        {
            foreach (var (item, at) in Items)
            {
                if (item.Id == id && item.Unpacked is not null)
                    return at;
            }
            return null;
        }

        /// <summary>Run until <paramref name="stop"/> holds, the stream ends, the clock reaches <paramref name="until"/>, or the stream waits with no timer scheduled.</summary>
        public async Task RunAsync(Func<bool> stop, TimeSpan until)
        {
            while (!stop() && !_ended)
            {
                if (_move is null)
                {
                    _mark = Waits;
                    _move = _stream.MoveNextAsync().AsTask();
                }
                if (!_parked)
                {
                    var first = await Task.WhenAny(_move, WaitBeyond(_mark)).WaitAsync(RealTime).ConfigureAwait(false);
                    if (first != _move)
                    {
                        _parked = true; // the stream waits: look at stop() before the clock moves
                        continue;
                    }
                    bool moved;
                    try
                    {
                        moved = await _move.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        EndedWith = ex;
                        _ended = true;
                        return;
                    }
                    if (!moved)
                    {
                        _ended = true;
                        return;
                    }
                    var item = _stream.Current;
                    Items.Add((item, _exchange.Clock.Now));
                    // The host handles the item before it asks for the next one (at the top of the
                    // loop, unless the run stops here).
                    if (Qa1.ToDiscard(new[] { item }, _discard) is { Length: > 0 } ids)
                    {
                        await _client.AckAsync(FakePickupExchange.M, FakePickupExchange.L, ids).WaitAsync(RealTime).ConfigureAwait(false);
                        Discarded.AddRange(ids);
                    }
                    _move = null;
                    continue;
                }
                _mark = Waits;
                if (_exchange.Clock.Now >= until || !_exchange.Clock.AdvanceToNextDue())
                    return; // left waiting
                _parked = false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_move is not null && !_ended)
            {
                try
                {
                    await _move.WaitAsync(RealTime).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            await _stream.DisposeAsync().AsTask().WaitAsync(RealTime).ConfigureAwait(false);
        }

        private int Waits
        {
            get
            {
                lock (_gate)
                    return _waits;
            }
        }

        private void OnWaitPublished()
        {
            TaskCompletionSource signal;
            lock (_gate)
            {
                _waits++;
                signal = _waited;
                _waited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            signal.TrySetResult();
        }

        private async Task WaitBeyond(int count)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (_waits > count)
                        return;
                    changed = _waited.Task;
                }
                await changed.ConfigureAwait(false);
            }
        }
    }
}
