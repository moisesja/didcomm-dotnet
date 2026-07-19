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
    private static UnpackResult Unpack(string id) => new(
        Message: new Message { Id = id, Type = "https://didcomm.org/x/1.0/m" },
        Stack: Array.Empty<DidComm.Jose.EnvelopeKind>(),
        Encrypted: false, Authenticated: false, NonRepudiation: false, AnonymousSender: false,
        ContentEncryption: null, KeyWrap: null, SignatureAlgorithm: null, SignerKid: null,
        SenderKid: null, RecipientKid: null, AllRecipientKids: Array.Empty<string>(), FromPrior: null);

    private sealed class GatedObserver : IProtocolObserver
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _delivered;
        public string? ProtocolUriFilter => null;
        public int Delivered => Volatile.Read(ref _delivered);
        public void Release() => _gate.TrySetResult();
        public async Task OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct)
        {
            await _gate.Task.ConfigureAwait(false);
            Interlocked.Increment(ref _delivered);
        }
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
        await delivery.FlushAsync(TimeSpan.FromSeconds(5));
        observer.Delivered.Should().BeInRange(1, 3, "the bounded queue drops the overflow instead of buffering all 50");
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
