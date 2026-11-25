using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="DeliveredMessageUnpacker"/> (#96 design D-19, §4.9): every delivered message becomes
/// exactly one outcome — a bad one is a failure, never an exception, and only the caller's
/// cancellation throws; one budget on the injected clock covers every layer of every message of a
/// delivery (owner decision 2-A), and decides the delivery when it runs out; a batch runs at most
/// sixteen at a time (<see cref="DeliveredMessageUnpacker.FetchConcurrency"/>; #111, owner decision
/// G) and keeps the input order; and host unpack calls are reserved before they start (#111, I-2),
/// the seventeenth for a mailbox's messages without a named sender failing fast without starting.
/// The success path is the real
/// <see cref="DidCommClient.UnpackDeliveredAsync"/> over real envelopes, with the duplicate key the
/// filter uses.
/// </summary>
public sealed class DeliveredMessageUnpackerTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // As many messages of one delivery as are opened at the same time.
    private const int AtOnce = DeliveredMessageUnpacker.FetchConcurrency;
    private readonly ManualTimeProvider _time = new();
    private ClientWorld _w = null!;

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    // ---- the real unpack ----

    [Fact]
    public async Task A_message_that_unpacks_is_a_success_with_its_layers_and_the_filters_key()
    {
        var unpacker = Real();
        var note = Note(_w.Alice.Did);
        var inner = await _w.PackAsync(note, from: _w.Alice.Did);
        var rewrapped = await RewrapAsync(inner, layers: 2);

        var outcome = await unpacker.UnpackAsync(Utf8(rewrapped), default);

        outcome.Succeeded.Should().BeTrue();
        outcome.Error.Should().BeNull();
        outcome.Unpacked!.Message.Id.Should().Be(note.Id);
        outcome.LayersOpened.Should().Be(2);
        var direct = await _w.Client.UnpackDeliveredAsync(rewrapped, default);
        outcome.DedupeKey.Should().Be(DuplicateFilter.KeyOf(direct.Result, direct.InnermostPacked));
    }

    [Fact]
    public async Task The_same_authenticated_message_has_one_key_however_it_was_wrapped()
    {
        var unpacker = Real();
        var inner = await _w.PackAsync(Note(_w.Alice.Did), from: _w.Alice.Did);

        var direct = await unpacker.UnpackAsync(Utf8(inner), default);
        var rewrapped = await unpacker.UnpackAsync(Utf8(await RewrapAsync(inner, layers: 1)), default);

        rewrapped.DedupeKey.Should().Be(direct.DedupeKey);
    }

    [Fact]
    public async Task An_anonymous_message_is_keyed_by_its_innermost_bytes_not_by_its_wrapping()
    {
        var unpacker = Real();
        var inner = await _w.PackAsync(Note(from: null), AnswerEnvelope.Anoncrypt);
        var other = await _w.PackAsync(Note(from: null), AnswerEnvelope.Anoncrypt);

        var once = await unpacker.UnpackAsync(Utf8(await RewrapAsync(inner, 1)), default);
        var twice = await unpacker.UnpackAsync(Utf8(await RewrapAsync(inner, 3)), default);
        var different = await unpacker.UnpackAsync(Utf8(other), default);

        twice.DedupeKey.Should().Be(once.DedupeKey, "the same innermost packed bytes");
        different.DedupeKey.Should().NotBe(once.DedupeKey);
    }

    public static TheoryData<string, byte[]> Bad => new()
    {
        { "not UTF-8", new byte[] { 0xC3, 0x28, 0xFF } },
        { "empty", Array.Empty<byte>() },
        { "not a packed message", Encoding.UTF8.GetBytes("{\"hello\":1}") },
        { "not JSON", Encoding.UTF8.GetBytes("garbage") },
    };

    [Theory]
    [MemberData(nameof(Bad))]
    public async Task A_message_that_does_not_unpack_is_a_failure_never_an_exception(string name, byte[] payload)
    {
        var outcome = await Real().UnpackAsync(payload, default);

        outcome.Succeeded.Should().BeFalse(name);
        outcome.Unpacked.Should().BeNull();
        outcome.Error.Should().NotBeNull();
        outcome.LayersOpened.Should().Be(0);
    }

    [Fact]
    public async Task A_forward_that_is_not_for_this_agent_is_a_failure()
    {
        var inner = await _w.PackAsync(Note(_w.Alice.Did, to: _w.Mallory.Did), from: _w.Alice.Did, to: _w.Mallory.Did);
        var forMallory = await _w.PackAsync(ForwardMessage.Create(_w.Local.Did, _w.Mallory.Did, new[] { inner }), AnswerEnvelope.Anoncrypt);

        var outcome = await Real().UnpackAsync(Utf8(forMallory), default);

        outcome.Error.Should().BeOfType<ConsistencyException>();
    }

    [Fact]
    public async Task The_callers_cancellation_throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Real().UnpackAsync(Utf8("{}"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Cancellation_during_the_unpack_throws_and_is_not_a_failure()
    {
        using var cts = new CancellationTokenSource();
        var fake = new GatedUnpack(honoursCancellation: true);
        var unpacker = new DeliveredMessageUnpacker(fake.UnpackAsync, Timeout, _time);

        var running = unpacker.UnpackAsync(Utf8("x"), cts.Token);
        await fake.Started(1);
        await cts.CancelAsync();

        (await FluentActions.Awaiting(() => running).Should().ThrowAsync<OperationCanceledException>()).Which.CancellationToken.Should().Be(cts.Token);
    }

    // ---- the deadline ----

    [Fact]
    public async Task One_deadline_on_the_injected_clock_covers_the_whole_unpack()
    {
        var fake = new GatedUnpack(honoursCancellation: true);
        var unpacker = new DeliveredMessageUnpacker(fake.UnpackAsync, Timeout, _time);

        var running = unpacker.UnpackAsync(Utf8("x"), default);
        await fake.Started(1);
        _time.Advance(Timeout - TimeSpan.FromTicks(1));
        fake.Tokens.Single().IsCancellationRequested.Should().BeFalse("one tick early");
        _time.Advance(TimeSpan.FromTicks(1));

        var outcome = await running;
        fake.Tokens.Single().IsCancellationRequested.Should().BeTrue();
        outcome.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("UnpackTimeout");
        // The budget lets the unpack go at once; the unpack gives its reservation back when it has
        // noticed its cancellation, which host code may do a little later.
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, TimeSpan.FromSeconds(10)).Should().BeTrue("the unpack honoured cancellation and ended");
    }

    // ---- one budget per delivery (owner decision 2-A) ----

    [Fact]
    public async Task One_budget_covers_a_delivery_and_messages_that_never_open_keep_their_workers_until_it_ends()
    {
        // Twice as many messages as are opened at once (thirty-two) under one budget of 20 s, and
        // no time limit per message (#111, owner decision E). The first sixteen never open: they
        // keep the sixteen workers until the budget ends, one tick before it they are all still
        // being opened, and the second sixteen are out of time before their turn. (Before #111's
        // fix round 2 each message had a share of the budget, and the messages behind started when
        // the shares of those in front ended.)
        var fake = new GatedUnpack(honoursCancellation: true);
        var unpacker = new DeliveredMessageUnpacker(fake.UnpackAsync, TimeSpan.FromSeconds(20), _time);
        var payloads = Enumerable.Range(0, 2 * AtOnce).Select(i => (ReadOnlyMemory<byte>)Utf8("message-" + i)).ToArray();

        var batch = unpacker.UnpackAllAsync(payloads, "request-1", default);
        await fake.Started(AtOnce);
        _time.Advance(TimeSpan.FromSeconds(20) - TimeSpan.FromTicks(1));
        fake.Tokens.Should().HaveCount(AtOnce).And.OnlyContain(t => !t.IsCancellationRequested, "one tick before the budget ends the first {0} are still being opened", AtOnce);
        batch.IsCompleted.Should().BeFalse();
        _time.Advance(TimeSpan.FromTicks(1));

        var outcomes = await batch.WaitAsync(TimeSpan.FromSeconds(10));
        outcomes.Should().HaveCount(2 * AtOnce);
        outcomes.Take(AtOnce).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("ran out while it was being opened") && o.Error.Message.Contains("'request-1'"));
        outcomes.Skip(AtOnce).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("before its turn came") && o.Error.Message.Contains("'request-1'"));
        fake.Calls.Should().Be(AtOnce, "the second {0} never started", AtOnce);
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, TimeSpan.FromSeconds(10)).Should().BeTrue();
    }

    [Fact]
    public async Task A_message_that_starts_late_still_ends_by_the_budget()
    {
        // Twice as many messages as are opened at once. The first sixteen open at 9 s; the second
        // sixteen start then (none of them before) and have the time left, 11 s, which ends with
        // the budget at 20 s. The delivery never outlives it.
        var fake = new GatedUnpack(honoursCancellation: true);
        var unpacker = new DeliveredMessageUnpacker(fake.UnpackAsync, TimeSpan.FromSeconds(20), _time);
        var payloads = Enumerable.Range(0, 2 * AtOnce).Select(i => (ReadOnlyMemory<byte>)Utf8("message-" + i)).ToArray();

        var batch = unpacker.UnpackAllAsync(payloads, default);
        await fake.Started(AtOnce);
        _time.Advance(TimeSpan.FromSeconds(9));
        fake.Calls.Should().Be(AtOnce, "the second {0} wait for a worker", AtOnce);
        for (var i = 0; i < AtOnce; i++)
            fake.ReleaseOldest();
        await fake.Started(2 * AtOnce);
        _time.Advance(TimeSpan.FromSeconds(11));

        var outcomes = await batch.WaitAsync(TimeSpan.FromSeconds(10));
        outcomes.Should().HaveCount(2 * AtOnce);
        outcomes.Take(AtOnce).Should().OnlyContain(o => o.Succeeded);
        outcomes.Skip(AtOnce).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("while it was being opened"));
    }

    [Fact]
    public async Task Unpacks_that_ignore_their_cancellation_are_let_go_at_the_budget_and_keep_their_reservations()
    {
        // Two more messages than are opened at once (eighteen), whose host code ignores
        // cancellation. It cannot hold the delivery past its budget: when the budget ends the
        // sixteen being opened are let go, each keeping its reservation until it ends, and the two
        // behind them are out of time before their turn.
        var fake = new GatedUnpack(honoursCancellation: false);
        var unpacker = new DeliveredMessageUnpacker(fake.UnpackAsync, Timeout, _time);
        var payloads = Enumerable.Range(0, AtOnce + 2).Select(i => (ReadOnlyMemory<byte>)Utf8("message-" + i)).ToArray();

        var batch = unpacker.UnpackAllAsync(payloads, default);
        await fake.Started(AtOnce);
        _time.Advance(Timeout);

        var outcomes = await batch.WaitAsync(TimeSpan.FromSeconds(10));
        outcomes.Should().HaveCount(AtOnce + 2);
        outcomes.Take(AtOnce).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("while it was being opened"));
        outcomes.Skip(AtOnce).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("before its turn came"));
        outcomes.Should().OnlyContain(o => !o.Error!.Message.Contains("request"), "a delivery without a request id names none");
        fake.Calls.Should().Be(AtOnce);
        unpacker.Reserved.Should().Be(AtOnce, "the {0} still running keep their reservations", AtOnce);

        // What the unpacks let go do afterwards changes nothing that was returned.
        fake.Release();
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, TimeSpan.FromSeconds(10)).Should().BeTrue();
        outcomes.Should().OnlyContain(o => !o.Succeeded);
    }

    [Fact]
    public async Task A_delivery_that_ends_before_its_budget_stops_its_timer()
    {
        var unpacker = Real();
        var good = await _w.PackAsync(Note(_w.Alice.Did), from: _w.Alice.Did);

        var outcomes = await unpacker.UnpackAllAsync(new ReadOnlyMemory<byte>[] { Utf8(good), Utf8(good) }, default);

        outcomes.Should().OnlyContain(o => o.Succeeded);
        _time.ScheduledTimers.Should().Be(0, "the budget's timer is disposed with the delivery");
    }

    [Fact]
    public async Task A_throwing_callback_on_the_unpacks_token_cannot_escape_the_timer()
    {
        var fake = new GatedUnpack(honoursCancellation: true, onToken: ct => ct.Register(() => throw new InvalidOperationException("host callback")));
        var unpacker = new DeliveredMessageUnpacker(fake.UnpackAsync, Timeout, _time);

        var running = unpacker.UnpackAsync(Utf8("x"), default);
        await fake.Started(1);
        Action advance = () => _time.Advance(Timeout);

        advance.Should().NotThrow();
        (await running).Error.Should().BeOfType<TimeoutException>();
    }

    // ---- the reservations (#111, I-1, I-2) ----

    [Fact]
    public async Task Unpacks_that_ignore_their_deadline_are_bounded_and_the_next_fails_fast_without_starting()
    {
        // Messages without a named sender, from one mailbox: their partition holds 16.
        var fake = new GatedUnpack(honoursCancellation: false);
        var unpacker = new DeliveredMessageUnpacker(fake.UnpackAsync, Timeout, _time);

        var letGo = Enumerable.Range(0, UnpackReservations.MaxPerAnonymous).Select(_ => unpacker.UnpackAsync(Utf8("x"), default)).ToArray();
        await fake.Started(UnpackReservations.MaxPerAnonymous);
        _time.Advance(Timeout);
        (await Task.WhenAll(letGo)).Should().OnlyContain(o => o.Error is TimeoutException);
        unpacker.Reserved.Should().Be(UnpackReservations.MaxPerAnonymous);

        // Bounded, so a missing bound fails the test instead of parking it.
        var refused = await unpacker.UnpackAsync(Utf8("x"), default).WaitAsync(TimeSpan.FromSeconds(10));

        refused.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("capacity exhausted");
        fake.Calls.Should().Be(UnpackReservations.MaxPerAnonymous, "the refused unpack never started");

        fake.Release();
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, TimeSpan.FromSeconds(10)).Should().BeTrue("each reservation is given back when its work ends");
        var after = unpacker.UnpackAsync(Utf8("x"), default);
        await fake.Started(UnpackReservations.MaxPerAnonymous + 1);
        (await after).Succeeded.Should().BeTrue("released work answers at once");
    }

    // ---- batches ----

    [Fact]
    public async Task A_batch_runs_at_most_sixteen_at_a_time_and_keeps_the_input_order()
    {
        // Two and a half times as many messages as are opened at once (forty): each message
        // released lets exactly one more start, and never more than sixteen run.
        const int count = 2 * AtOnce + AtOnce / 2;
        var fake = new GatedUnpack(honoursCancellation: true);
        var unpacker = new DeliveredMessageUnpacker(fake.UnpackAsync, Timeout, _time);
        var payloads = Enumerable.Range(0, count).Select(i => (ReadOnlyMemory<byte>)Utf8("message-" + i)).ToArray();

        var batch = unpacker.UnpackAllAsync(payloads, default);
        await fake.Started(AtOnce);
        fake.Calls.Should().Be(AtOnce, "the seventeenth waits for a slot");

        // Release in reverse order of arrival, one at a time: the results still come back in input order.
        for (var released = 0; released < count; released++)
        {
            fake.ReleaseNewest();
            if (released + AtOnce < count)
            {
                await fake.Started(released + 1 + AtOnce);
                fake.Calls.Should().Be(released + 1 + AtOnce, "one message released lets one more start, no more");
            }
        }

        var outcomes = await batch;
        outcomes.Select(o => o.Unpacked!.Message.Id).Should().Equal(Enumerable.Range(0, count).Select(i => "message-" + i));
        fake.MaxConcurrent.Should().Be(AtOnce);
    }

    [Fact]
    public async Task A_batch_with_failures_returns_one_outcome_per_message_in_order()
    {
        var unpacker = Real();
        var good = await _w.PackAsync(Note(_w.Alice.Did), from: _w.Alice.Did);
        var payloads = new ReadOnlyMemory<byte>[] { Utf8(good), new byte[] { 0xFF }, Utf8("garbage"), Utf8(good) };

        var outcomes = await unpacker.UnpackAllAsync(payloads, default);

        outcomes.Select(o => o.Succeeded).Should().Equal(true, false, false, true);
    }

    [Fact]
    public async Task An_empty_batch_is_empty_and_a_cancelled_batch_throws()
    {
        (await Real().UnpackAllAsync(Array.Empty<ReadOnlyMemory<byte>>(), default)).Should().BeEmpty();
        using var cts = new CancellationTokenSource();
        var fake = new GatedUnpack(honoursCancellation: true);
        var unpacker = new DeliveredMessageUnpacker(fake.UnpackAsync, Timeout, _time);

        // Two more messages than are opened at once: cancelled with sixteen running and two waiting.
        var batch = unpacker.UnpackAllAsync(Enumerable.Range(0, AtOnce + 2).Select(_ => (ReadOnlyMemory<byte>)Utf8("x")).ToArray(), cts.Token);
        await fake.Started(AtOnce);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => batch).Should().ThrowAsync<OperationCanceledException>();
        fake.Calls.Should().Be(AtOnce, "the two that waited for a worker never started");
    }

    // ---- helpers ----

    private DeliveredMessageUnpacker Real() => new(_w.Client.UnpackDeliveredAsync, Timeout, _time);

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private Message Note(string? from, string? to = null)
    {
        var builder = new MessageBuilder().WithType("https://example.org/notes/1.0/note").WithTo(to ?? _w.Local.Did).WithBody(new JsonObject { ["text"] = "hi" });
        if (from is not null)
            builder.WithFrom(from);
        return builder.Build();
    }

    private async Task<string> RewrapAsync(string inner, int layers)
    {
        var packed = inner;
        for (var i = 0; i < layers; i++)
            packed = await _w.PackAsync(ForwardMessage.Create(_w.Local.Did, _w.Local.Did, new[] { packed }), AnswerEnvelope.Anoncrypt);
        return packed;
    }

    /// <summary>An unpack step that parks every call until released; it answers with a fake result carrying the payload as the message id.</summary>
    private sealed class GatedUnpack(bool honoursCancellation, Action<CancellationToken>? onToken = null)
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _parked = new();
        private readonly ConcurrentQueue<CancellationToken> _tokens = new();
        private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _released;
        private int _calls;
        private int _running;
        private int _maxRunning;

        public int Calls => Volatile.Read(ref _calls);
        public int MaxConcurrent => Volatile.Read(ref _maxRunning);
        public IReadOnlyCollection<CancellationToken> Tokens => _tokens;

        public async Task<DeliveredUnpack> UnpackAsync(string packed, CancellationToken ct)
        {
            onToken?.Invoke(ct);
            _tokens.Enqueue(ct);
            var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource arrived;
            lock (_gate)
            {
                Interlocked.Increment(ref _calls);
                var running = Interlocked.Increment(ref _running);
                if (running > _maxRunning)
                    _maxRunning = running;
                if (_released)
                    parked.TrySetResult();
                else
                    _parked.Add(parked);
                arrived = _arrived;
                _arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            arrived.TrySetResult();
            try
            {
                if (honoursCancellation)
                    await parked.Task.WaitAsync(ct);
                else
                    await parked.Task;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
            var message = new MessageBuilder().WithId(packed).WithType("https://example.org/notes/1.0/note").WithBody(new JsonObject()).Build();
            var result = new UnpackResult(message, new[] { DidComm.Jose.EnvelopeKind.Plaintext }, false, false, false, false, null, null, null, null, null, null, Array.Empty<string>(), null);
            return new DeliveredUnpack(result, packed, 0);
        }

        /// <summary>Wait (bounded) until <paramref name="count"/> calls have started.</summary>
        public async Task Started(int count)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                Task next;
                lock (_gate)
                {
                    if (_calls >= count)
                        return;
                    next = _arrived.Task;
                }
                await next.WaitAsync(cts.Token);
            }
        }

        public void Release()
        {
            lock (_gate)
            {
                _released = true;
                foreach (var parked in _parked)
                    parked.TrySetResult();
                _parked.Clear();
            }
        }

        public void ReleaseOldest()
        {
            lock (_gate)
            {
                if (_parked.Count == 0)
                    return;
                var oldest = _parked[0];
                _parked.RemoveAt(0);
                oldest.TrySetResult();
            }
        }

        public void ReleaseNewest()
        {
            lock (_gate)
            {
                if (_parked.Count == 0)
                    return;
                var newest = _parked[^1];
                _parked.RemoveAt(_parked.Count - 1);
                newest.TrySetResult();
            }
        }
    }
}
