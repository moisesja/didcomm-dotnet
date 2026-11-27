using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT3 (#111, fix round 2): what a delivery leaves behind, whatever its messages do and whatever
/// ends it. Deliveries of one to forty-eight messages (up to three times as many as are opened at
/// once, so in the larger ones messages wait for a worker) of every kind the unpacker tells apart (opening or
/// failing at once or later, never ending, ending from inside a cancellation callback as a failure
/// or as a success, not valid UTF-8; from two named senders or none) are ended in each of four ways
/// (on their own, by the budget, by the caller, by the owner's shutdown) at different moments. What
/// is checked after each: the result is set, once, with one outcome per message or cancelled with
/// the token that ended it; every reservation is given back; no partition, timer or open delivery
/// is left; and a message that is not valid UTF-8 is never remembered.
/// </summary>
/// <remarks>
/// The kinds and endings are drawn from a fixed seed; the interleavings of the pool are not fixed,
/// and the properties checked hold in every one of them. Real time is only a hang guard.
/// </remarks>
public sealed class DeliveredUnpackEndingsRt3Tests(ITestOutputHelper output)
{
    private const string Kinds = "ofOFscCm";
    private const int Deliveries = 250;
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(20);
    private static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);
    private static readonly string?[] Senders = { null, "did:example:alice#key-x25519-1", "did:example:mallory#key-x25519-1" };

    private enum Ending
    {
        OnItsOwn,
        Budget,
        Caller,
        Shutdown,
    }

    [Theory]
    [InlineData(111)]
    [InlineData(20261125)]
    public async Task Whatever_its_messages_do_and_whatever_ends_it_a_delivery_sets_its_result_and_leaves_nothing_behind(int seed)
    {
        var random = new Random(seed);
        var endings = new int[4];
        var cancelled = 0;
        for (var iteration = 0; iteration < Deliveries; iteration++)
        {
            var time = new ManualTimeProvider();
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var caller = new CancellationTokenSource();
            using var shutdown = new CancellationTokenSource();
            var calls = new HostCallLog();
            var unpacker = new DeliveredMessageUnpacker((packed, ct) => HostAsync(packed, ct, calls, release.Task), Budget, time, shutdown.Token);
            var count = random.Next(1, 3 * DeliveredMessageUnpacker.FetchConcurrency + 1);
            var messages = new DeliveredInput[count];
            for (var i = 0; i < count; i++)
            {
                var kind = Kinds[random.Next(Kinds.Length)];
                var label = $"{kind}-{iteration}-{i}";
                var sender = Senders[random.Next(Senders.Length)];
                messages[i] = kind == 'm'
                    ? new DeliveredInput(label, new byte[] { 0xC3, 0x28, 0xFF })
                    : new DeliveredInput(label, TestJwe.Bytes(sender is null ? TestJwe.Anoncrypt(label) : TestJwe.Authcrypt(sender, label)));
            }
            var ending = (Ending)random.Next(4);
            var afterCalls = random.Next(0, 3); // end it at once, or once one or two host calls were made
            endings[(int)ending]++;

            var delivery = unpacker.UnpackAllAsync(Bob, messages, "request-1", caller.Token);
            if (afterCalls > 0)
                SpinWait.SpinUntil(() => delivery.IsCompleted || calls.Count >= afterCalls, TimeSpan.FromMilliseconds(20));
            switch (ending)
            {
                case Ending.OnItsOwn:
                    release.TrySetResult(true);
                    SpinWait.SpinUntil(() => delivery.IsCompleted, TimeSpan.FromMilliseconds(20));
                    await Task.Run(() => time.Advance(Budget)).WaitAsync(RealTime); // what never ends is ended by the budget
                    break;
                case Ending.Budget:
                    await Task.Run(() => time.Advance(Budget)).WaitAsync(RealTime);
                    break;
                case Ending.Caller:
                    await Task.Run(caller.Cancel).WaitAsync(RealTime);
                    break;
                default:
                    await Task.Run(shutdown.Cancel).WaitAsync(RealTime);
                    break;
            }
            await delivery.ContinueWith(static _ => { }, TaskScheduler.Default).WaitAsync(RealTime);
            release.TrySetResult(true); // the lookups that ignore their token end now

            var what = $"seed {seed}, delivery {iteration}: {string.Join(" ", messages.Select(m => m.Id))}, ended {ending} after {afterCalls} calls";
            if (delivery.IsCanceled)
            {
                cancelled++;
                ending.Should().BeOneOf(new[] { Ending.Caller, Ending.Shutdown }, "only the caller's cancellation and the shutdown cancel a result ({0})", what);
                var token = (await FluentActions.Awaiting(() => delivery).Should().ThrowAsync<OperationCanceledException>()).Which.CancellationToken;
                token.Should().Be(ending == Ending.Caller ? caller.Token : shutdown.Token, what);
            }
            else
            {
                delivery.IsCompletedSuccessfully.Should().BeTrue("a delivery's result is a list of outcomes or a cancellation, never a fault ({0})", what);
                var outcomes = await delivery;
                outcomes.Should().HaveCount(count, what);
                outcomes.Should().NotContainNulls("every message has an outcome ({0})", what);
                for (var i = 0; i < count; i++)
                {
                    if (messages[i].Id![0] == 'm')
                        outcomes[i].Error.Should().Match(e => e is MalformedMessageException || e is TimeoutException, what);
                }
            }
            var held = unpacker.Memory.HeldBack(Bob, messages.Select(m => m.Id).ToArray());
            for (var i = 0; i < count; i++)
            {
                if (messages[i].Id![0] == 'm')
                    held[i].Should().BeFalse("a message that is not valid UTF-8 is never remembered ({0})", what);
            }
            SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Reservations.Partitions == 0 && unpacker.Opening == 0 && time.ScheduledTimers == 0, RealTime)
                .Should().BeTrue("every reservation came back and no partition, open delivery or timer is left: reserved {0}, partitions {1}, opening {2}, timers {3} ({4})",
                    unpacker.Reserved, unpacker.Reservations.Partitions, unpacker.Opening, time.ScheduledTimers, what);
        }
        output.WriteLine($"seed {seed}: {Deliveries} deliveries; ended on their own {endings[0]}, by the budget {endings[1]}, by the caller {endings[2]}, by the shutdown {endings[3]}; {cancelled} results cancelled");
    }

    // What each kind of message does: 'o' opens at once; 'f' fails at once; 'O' opens and 'F' fails
    // once the test releases them, whatever their token; 's' never ends until its token is
    // cancelled; 'c' fails and 'C' opens from inside a callback on its token.
    private static async Task<DeliveredUnpack> HostAsync(string packed, CancellationToken ct, HostCallLog calls, Task<bool> released)
    {
        var label = TestJwe.LabelOf(packed);
        using (calls.Enter(label, ct))
        {
            switch (label[0])
            {
                case 'o':
                    return UnpackFakes.Opened(label);
                case 'f':
                    throw new CryptoException("JWE could not be decrypted.");
                case 'O':
                    await released.ConfigureAwait(false);
                    return UnpackFakes.Opened(label);
                case 'F':
                    await released.ConfigureAwait(false);
                    throw new CryptoException("JWE could not be decrypted.");
                case 's':
                    return await UnpackFakes.StallUntilCancelled(ct).ConfigureAwait(false);
                case 'c':
                {
                    var ends = new TaskCompletionSource<DeliveredUnpack>();
                    using var registration = ct.UnsafeRegister(_ => ends.TrySetException(new DidResolutionException("did:example:alice", "notFound")), null);
                    return await ends.Task.ConfigureAwait(false);
                }
                default:
                {
                    var ends = new TaskCompletionSource<DeliveredUnpack>();
                    using var registration = ct.UnsafeRegister(_ => ends.TrySetResult(UnpackFakes.Opened(label)), null);
                    return await ends.Task.ConfigureAwait(false);
                }
            }
        }
    }
}
