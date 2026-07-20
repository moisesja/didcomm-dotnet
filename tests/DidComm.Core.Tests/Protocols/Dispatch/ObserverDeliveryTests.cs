using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Tests.Protocols.Dispatch;

/// <summary>
/// FR-PROTO-12 delivery internals: linearizable item/UTF-8-byte admission, off-path private
/// materialization, overflow diagnostics, and cooperative/non-cooperative shutdown semantics.
/// </summary>
public sealed class ObserverDeliveryTests
{
    private static UnpackResult Unpack(Message message) => new(
        Message: message,
        Stack: Array.Empty<DidComm.Jose.EnvelopeKind>(),
        Encrypted: false, Authenticated: false, NonRepudiation: false, AnonymousSender: false,
        ContentEncryption: null, KeyWrap: null, SignatureAlgorithm: null, SignerKid: null,
        SenderKid: null, RecipientKid: null, AllRecipientKids: Array.Empty<string>(), FromPrior: null);

    private static InboundMessageSnapshot Snapshot(string id, string? pad = null) =>
        Snapshot(new Message
        {
            Id = id,
            Type = "https://didcomm.org/x/1.0/m",
            Body = pad is null ? null : new JsonObject { ["pad"] = pad },
        });

    private static InboundMessageSnapshot Snapshot(Message message)
        => InboundMessageSnapshot.CreateFallback(Unpack(message));

    private static InboundMessageSnapshot VerifiedSnapshot(Message message, string plaintextJson)
    {
        InboundMessageSnapshot.RegisterVerified(
            message, plaintextJson,
            encrypted: false, authenticated: false, nonRepudiation: false, anonymousSender: false,
            senderKid: null, signerKid: null, recipientKid: null);
        InboundMessageSnapshot.TryGetFor(message, out var snapshot).Should().BeTrue();
        return snapshot;
    }

    private sealed class GatedObserver : IProtocolObserver
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _delivered;
        public string? ProtocolUriFilter => null;
        public int Delivered => Volatile.Read(ref _delivered);
        public ConcurrentQueue<InboundObservation> Seen { get; } = new();
        public void Release() => _gate.TrySetResult();
        public async Task OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct)
        {
            await _gate.Task.ConfigureAwait(false); // deliberately ignores cancellation until released
            Seen.Enqueue(observation);
            Interlocked.Increment(ref _delivered);
        }
    }

    private sealed class CooperativeHungObserver : IProtocolObserver
    {
        public string? ProtocolUriFilter => null;
        public Task OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct)
            => Task.Delay(Timeout.Infinite, ct);
    }

    private sealed class NonCooperativeObserver : IProtocolObserver
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? ProtocolUriFilter => null;
        public Task Started => _started.Task;
        public void Release() => _release.TrySetResult();
        public async Task OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct)
        {
            _started.TrySetResult();
            await _release.Task.ConfigureAwait(false); // genuinely ignores ct
        }
    }

    private sealed class RecordingObserver : IProtocolObserver
    {
        public string? ProtocolUriFilter => null;
        public ConcurrentQueue<InboundObservation> Seen { get; } = new();
        public Task OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct)
        {
            Seen.Enqueue(observation);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public ConcurrentQueue<IReadOnlyDictionary<string, object?>> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                Entries.Enqueue(values.ToDictionary(pair => pair.Key, pair => pair.Value));
        }
    }

    [Fact]
    public async Task Count_overflow_drops_before_any_extra_materialization()
    {
        var observer = new GatedObserver();
        var materialized = 0;
        await using var delivery = new ObserverDelivery(
            new IProtocolObserver[] { observer }, logger: null, capacity: 1,
            materialize: snapshot =>
            {
                Interlocked.Increment(ref materialized);
                return InboundObservation.FromSnapshot(snapshot);
            });

        delivery.Enqueue(Snapshot("accepted"));
        for (var i = 0; i < 100; i++)
            delivery.Enqueue(Snapshot($"dropped-{i}"));

        observer.Release();
        await delivery.FlushAsync(TimeSpan.FromSeconds(30));

        observer.Delivered.Should().Be(1);
        materialized.Should().Be(1, "count-full drops must do zero message clone/deserialization work");
    }

    [Fact]
    public async Task Snapshot_is_frozen_before_enqueue_and_observer_gets_a_private_clone()
    {
        var observer = new GatedObserver();
        await using var delivery = new ObserverDelivery(new IProtocolObserver[] { observer }, logger: null);

        var live = new Message
        {
            Id = "m1",
            Type = "https://didcomm.org/x/1.0/m",
            From = "did:peer:original",
            Body = new JsonObject { ["nested"] = new JsonObject { ["value"] = "original" } },
        };
        var snapshot = Snapshot(live);
        delivery.Enqueue(snapshot);
        live.From = "did:peer:MUTATED";
        live.Type = "https://didcomm.org/evil/1.0/x";
        live.Body!["nested"]!["value"] = "MUTATED";

        observer.Release();
        await delivery.FlushAsync(TimeSpan.FromSeconds(30));

        observer.Seen.Should().ContainSingle();
        observer.Seen.Single().Message.From.Should().Be("did:peer:original");
        observer.Seen.Single().Message.Type.Should().Be("https://didcomm.org/x/1.0/m");
        observer.Seen.Single().Message.Body!["nested"]!["value"]!.GetValue<string>().Should().Be("original");
    }

    [Fact]
    public async Task A_single_oversized_snapshot_is_rejected_before_materialization()
    {
        var observer = new RecordingObserver();
        var materialized = 0;
        var oversized = Snapshot("large", new string('x', 8192));
        await using var delivery = new ObserverDelivery(
            new IProtocolObserver[] { observer }, logger: null, capacity: 1000,
            byteBudget: oversized.Utf8ByteCount - 1,
            materialize: snapshot =>
            {
                Interlocked.Increment(ref materialized);
                return InboundObservation.FromSnapshot(snapshot);
            });

        delivery.Enqueue(oversized);
        await delivery.FlushAsync(TimeSpan.FromSeconds(30));

        observer.Seen.Should().BeEmpty();
        materialized.Should().Be(0);
        delivery.GetOutstanding(0).Should().Be((0, 0));
    }

    [Fact]
    public async Task Byte_budget_is_cumulative_and_counts_exact_utf8_bytes()
    {
        var observer = new GatedObserver();
        var first = Snapshot("m1", "ascii");
        var secondMessage = new Message
        {
            Id = "m2",
            Type = "https://didcomm.org/x/1.0/m",
            Body = new JsonObject { ["pad"] = "emoji-💥-🔐" },
        };
        var second = VerifiedSnapshot(
            secondMessage,
            """{"id":"m2","type":"https://didcomm.org/x/1.0/m","body":{"pad":"emoji-💥-🔐"}}""");
        second.Utf8ByteCount.Should().BeGreaterThan(second.PlaintextJson.Length,
            "non-ASCII JSON occupies more UTF-8 bytes than UTF-16 characters");

        await using var delivery = new ObserverDelivery(
            new IProtocolObserver[] { observer }, logger: null, capacity: 10,
            byteBudget: first.Utf8ByteCount + second.Utf8ByteCount - 1);

        delivery.Enqueue(first);
        delivery.Enqueue(second);
        delivery.GetOutstanding(0).Should().Be((1, first.Utf8ByteCount));

        observer.Release();
        await delivery.FlushAsync(TimeSpan.FromSeconds(30));
        observer.Delivered.Should().Be(1, "the second exact UTF-8 charge would exceed the cumulative budget");
    }

    [Fact]
    public async Task Concurrent_producers_never_exceed_item_or_byte_bounds()
    {
        var observer = new GatedObserver();
        var snapshot = Snapshot("same", new string('x', 100));
        const int capacity = 8;
        var budget = capacity * (long)snapshot.Utf8ByteCount;
        await using var delivery = new ObserverDelivery(
            new IProtocolObserver[] { observer }, logger: null, capacity: capacity, byteBudget: budget);

        Parallel.For(0, 1000, _ => delivery.Enqueue(snapshot));

        var outstanding = delivery.GetOutstanding(0);
        outstanding.Items.Should().Be(capacity);
        outstanding.Bytes.Should().Be(budget);

        observer.Release();
        await delivery.FlushAsync(TimeSpan.FromSeconds(30));
        observer.Delivered.Should().Be(capacity);
        delivery.GetOutstanding(0).Should().Be((0, 0));
    }

    [Fact]
    public async Task Materialization_failure_is_isolated_and_releases_the_entire_reservation()
    {
        var observer = new RecordingObserver();
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var delivery = new ObserverDelivery(
            new IProtocolObserver[] { observer }, logger: null, capacity: 1,
            materialize: snapshot =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    firstAttempt.TrySetResult();
                    throw new InvalidOperationException("synthetic materializer failure");
                }
                return InboundObservation.FromSnapshot(snapshot);
            });

        delivery.Enqueue(Snapshot("bad"));
        await firstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await EventuallyAsync(() => delivery.GetOutstanding(0).Items == 0);

        delivery.Enqueue(Snapshot("good"));
        await delivery.FlushAsync(TimeSpan.FromSeconds(30));

        attempts.Should().Be(2);
        observer.Seen.Should().ContainSingle(observation => observation.Message.Id == "good");
        delivery.GetOutstanding(0).Should().Be((0, 0));
    }

    [Fact]
    public async Task Two_observers_receive_independent_mutable_messages()
    {
        var first = new RecordingObserver();
        var second = new RecordingObserver();
        await using var delivery = new ObserverDelivery(new IProtocolObserver[] { first, second }, logger: null);

        delivery.Enqueue(Snapshot("m1", "original"));
        await delivery.FlushAsync(TimeSpan.FromSeconds(30));

        first.Seen.Single().Message.Body!["pad"] = "mutated";
        second.Seen.Single().Message.Body!["pad"]!.GetValue<string>().Should().Be("original");
        first.Seen.Single().Message.Should().NotBeSameAs(second.Seen.Single().Message);
    }

    [Fact]
    public async Task Overflow_log_reports_the_actual_message_capacity_budget_and_drop_count()
    {
        var observer = new GatedObserver();
        var logger = new CapturingLogger();
        await using var delivery = new ObserverDelivery(
            new IProtocolObserver[] { observer }, logger, capacity: 1, byteBudget: 12_345);

        delivery.Enqueue(Snapshot("accepted"));
        delivery.Enqueue(Snapshot("rejected-id"));

        var entry = logger.Entries.Should().ContainSingle().Which;
        entry["MessageId"].Should().Be("rejected-id");
        entry["Capacity"].Should().Be(1);
        entry["ByteBudget"].Should().Be(12_345L);
        entry["Dropped"].Should().Be(1L);

        observer.Release();
        await delivery.FlushAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task DisposeAsync_cancels_a_cooperative_inflight_observer()
    {
        var delivery = new ObserverDelivery(
            new IProtocolObserver[] { new CooperativeHungObserver() }, logger: null,
            shutdownGrace: TimeSpan.FromSeconds(2));
        delivery.Enqueue(Snapshot("m1"));

        var dispose = delivery.DisposeAsync().AsTask();
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        await delivery.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        delivery.GetOutstanding(0).Should().Be((0, 0));
    }

    [Fact]
    public async Task Noncooperative_callback_cannot_retain_the_queued_plaintext_after_disposal()
    {
        var observer = new NonCooperativeObserver();
        var delivery = new ObserverDelivery(
            new IProtocolObserver[] { observer }, logger: null, capacity: 4,
            shutdownGrace: TimeSpan.FromMilliseconds(50));
        delivery.Enqueue(Snapshot("inflight", new string('a', 512)));
        await observer.Started.WaitAsync(TimeSpan.FromSeconds(30));
        delivery.Enqueue(Snapshot("queued-1", new string('b', 512)));
        delivery.Enqueue(Snapshot("queued-2", new string('c', 512)));

        var stopwatch = Stopwatch.StartNew();
        await delivery.DisposeAsync();
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        delivery.ShutdownCompletion.IsCompleted.Should().BeFalse(
            "the trusted callback genuinely ignores cancellation and is still in flight");
        delivery.GetOutstanding(0).Items.Should().Be(1,
            "shutdown must drain every queued snapshot while retaining only the unavoidable in-flight item");

        observer.Release();
        await delivery.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        delivery.GetOutstanding(0).Should().Be((0, 0));
    }

    [Fact]
    public async Task Enqueue_and_flush_after_disposal_fail_before_materialization()
    {
        var materialized = 0;
        var delivery = new ObserverDelivery(
            new IProtocolObserver[] { new RecordingObserver() }, logger: null,
            materialize: snapshot =>
            {
                Interlocked.Increment(ref materialized);
                return InboundObservation.FromSnapshot(snapshot);
            });
        delivery.Dispose();

        delivery.Enqueue(Snapshot("after-dispose"));
        var flush = async () => await delivery.FlushAsync(TimeSpan.FromSeconds(1));

        await flush.Should().ThrowAsync<ObjectDisposedException>();
        await delivery.DisposeAsync();
        materialized.Should().Be(0);
    }

    [Fact]
    public async Task Disposal_is_idempotent_across_sync_and_async()
    {
        var delivery = new ObserverDelivery(new IProtocolObserver[] { new GatedObserver() }, logger: null);
        delivery.Dispose();
        var act = async () => { await delivery.DisposeAsync(); delivery.Dispose(); };
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Enqueue_flush_and_shutdown_races_finish_without_leaking_reservations()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var delivery = new ObserverDelivery(
                new IProtocolObserver[] { new RecordingObserver() }, logger: null, capacity: 8);
            var snapshot = Snapshot($"race-{iteration}", new string('x', 64));
            using var start = new ManualResetEventSlim();

            var producer = Task.Run(() =>
            {
                start.Wait();
                Parallel.For(0, 100, _ => delivery.Enqueue(snapshot));
            });
            var flush = Task.Run(async () =>
            {
                start.Wait();
                try
                {
                    await delivery.FlushAsync(TimeSpan.FromSeconds(5));
                }
                catch (ObjectDisposedException)
                {
                    // Shutdown won before the serialized barrier was admitted.
                }
            });
            var shutdown = Task.Run(() =>
            {
                start.Wait();
                delivery.Dispose();
            });

            start.Set();
            await Task.WhenAll(producer, flush, shutdown).WaitAsync(TimeSpan.FromSeconds(10));
            await delivery.DisposeAsync();
            await delivery.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            delivery.GetOutstanding(0).Should().Be((0, 0));
        }
    }

    [Fact]
    public void ProtocolDispatcher_preserves_the_exact_1_2_0_four_argument_constructor()
    {
        var ctor = typeof(ProtocolDispatcher).GetConstructor(new[]
        {
            typeof(ProtocolHandlerRegistry),
            typeof(IThreadStateStore),
            typeof(ILogger<ProtocolDispatcher>),
            typeof(DidComm.Protocols.Trace.TraceOptions),
        });
        ctor.Should().NotBeNull("the exact 1.2.0 constructor must remain for runtime binary binding");
    }

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!predicate())
            await Task.Delay(1, cts.Token);
    }
}
