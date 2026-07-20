using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Threading;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Protocols.Dispatch;

/// <summary>
/// FR-PROTO-12 delivery internals: the bounded queue's overflow policy (drop + keep flowing) and
/// the binary-compatibility guard on <see cref="ProtocolDispatcher"/>'s constructor.
/// </summary>
public sealed class ObserverDeliveryTests
{
    private static UnpackResult Unpack(Message message) => new(
        Message: message,
        Stack: Array.Empty<DidComm.Jose.EnvelopeKind>(),
        Encrypted: false, Authenticated: false, NonRepudiation: false, AnonymousSender: false,
        ContentEncryption: null, KeyWrap: null, SignatureAlgorithm: null, SignerKid: null,
        SenderKid: null, RecipientKid: null, AllRecipientKids: Array.Empty<string>(), FromPrior: null);

    private static UnpackResult Unpack(string id) =>
        Unpack(new Message { Id = id, Type = "https://didcomm.org/x/1.0/m" });

    private sealed class GatedObserver : IProtocolObserver
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _delivered;
        public string? ProtocolUriFilter => null;
        public int Delivered => Volatile.Read(ref _delivered);
        public System.Collections.Concurrent.ConcurrentQueue<InboundObservation> Seen { get; } = new();
        public void Release() => _gate.TrySetResult();
        public async Task OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct)
        {
            await _gate.Task.ConfigureAwait(false);
            Seen.Enqueue(observation);
            Interlocked.Increment(ref _delivered);
        }
    }

    private sealed class HungObserver : IProtocolObserver
    {
        public string? ProtocolUriFilter => null;
        // Honors the cancellation token, so DisposeAsync's shutdown stops it.
        public Task OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct)
            => Task.Delay(Timeout.Infinite, ct);
    }

    [Fact]
    public async Task Overflow_drops_newest_and_keeps_flowing_rather_than_growing_unbounded()
    {
        // Capacity 2 + a blocked observer: the first item is pulled into the pump (in-flight), the
        // next 2 fill the channel, and everything after is dropped. Nothing throws; the queue never
        // grows past its bound. After release, exactly the retained items are delivered.
        var observer = new GatedObserver();
        await using var delivery = new ObserverDelivery(new IProtocolObserver[] { observer }, logger: null, capacity: 2);

        for (var i = 0; i < 50; i++)
            delivery.Enqueue(Unpack($"m{i}"));

        observer.Release();
        // Deliver at most in-flight(1) + capacity(2) = 3; the other 47 were dropped by the overflow policy.
        await delivery.FlushAsync(TimeSpan.FromSeconds(30));
        observer.Delivered.Should().BeInRange(1, 3, "the bounded queue drops the overflow instead of buffering all 50");
    }

    [Fact]
    public async Task Snapshot_is_taken_at_enqueue_so_mutating_the_live_message_afterward_is_not_observed()
    {
        // Finding 4: the queue must store an immutable snapshot taken AT ENQUEUE, not the live
        // UnpackResult cloned later — otherwise a handler/caller mutating the message after dispatch
        // changes what the observer sees.
        var observer = new GatedObserver();
        await using var delivery = new ObserverDelivery(new IProtocolObserver[] { observer }, logger: null);

        var live = new Message { Id = "m1", Type = "https://didcomm.org/x/1.0/m", From = "did:peer:original" };
        delivery.Enqueue(Unpack(live));
        live.From = "did:peer:MUTATED";      // mutate the live object after enqueue
        live.Type = "https://didcomm.org/evil/1.0/x";

        observer.Release();
        await delivery.FlushAsync(TimeSpan.FromSeconds(30));

        observer.Seen.Should().ContainSingle();
        observer.Seen.Single().Message.From.Should().Be("did:peer:original", "the snapshot was frozen at enqueue");
        observer.Seen.Single().Message.Type.Should().Be("https://didcomm.org/x/1.0/m");
    }

    [Fact]
    public async Task Byte_budget_drops_large_messages_before_the_item_count_cap_is_reached()
    {
        // A tiny byte budget with a blocked observer: large messages exceed the byte budget long
        // before the item-count cap, so most are dropped — memory cannot grow to capacity × MaxReceiveBytes.
        var observer = new GatedObserver();
        await using var delivery = new ObserverDelivery(
            new IProtocolObserver[] { observer }, logger: null, capacity: 1000, byteBudget: 4096);

        var bigBody = new string('x', 8192); // each message well over the 4 KiB budget
        for (var i = 0; i < 100; i++)
        {
            var m = new Message
            {
                Id = $"m{i}",
                Type = "https://didcomm.org/x/1.0/m",
                Body = new System.Text.Json.Nodes.JsonObject { ["pad"] = bigBody },
            };
            delivery.Enqueue(Unpack(m));
        }

        observer.Release();
        await delivery.FlushAsync(TimeSpan.FromSeconds(30));
        // In-flight(1) + at most a couple within budget; nowhere near 100. Byte budget bounds memory.
        observer.Delivered.Should().BeLessThan(5, "the byte budget drops large messages rather than buffering all 100");
    }

    [Fact]
    public async Task DisposeAsync_stops_the_pump_even_while_an_observer_is_hung()
    {
        // Finding 5: shutdown must actually cancel a running observer, not just complete the writer.
        var delivery = new ObserverDelivery(new IProtocolObserver[] { new HungObserver() }, logger: null);
        delivery.Enqueue(Unpack("m1"));

        var dispose = delivery.DisposeAsync().AsTask();
        (await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(dispose,
            "DisposeAsync cancels the pump/observer token and returns promptly");
        await dispose;
    }

    [Fact]
    public async Task Disposal_is_idempotent_across_sync_and_async()
    {
        // A CancellationTokenSource throws if cancelled after Dispose; disposal must be idempotent so
        // calling both Dispose() and DisposeAsync() (or twice) cannot throw ObjectDisposedException.
        var delivery = new ObserverDelivery(new IProtocolObserver[] { new GatedObserver() }, logger: null);
        delivery.Dispose();
        var act = async () => { await delivery.DisposeAsync(); delivery.Dispose(); };
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void ProtocolDispatcher_preserves_the_exact_1_2_0_four_argument_constructor()
    {
        // Binary-compat guard (PR #51 review finding 2): an app compiled against 1.2.0 holds a
        // MemberRef to (registry, threads, logger, traceOptions). Removing it is a runtime break
        // (MissingMethodException) even though adding the 5th optional param was source-compatible.
        var ctor = typeof(ProtocolDispatcher).GetConstructor(new[]
        {
            typeof(ProtocolHandlerRegistry),
            typeof(IThreadStateStore),
            typeof(Microsoft.Extensions.Logging.ILogger<ProtocolDispatcher>),
            typeof(DidComm.Protocols.Trace.TraceOptions),
        });
        ctor.Should().NotBeNull("the exact 1.2.0 4-argument constructor must remain for binary compatibility");
    }
}
