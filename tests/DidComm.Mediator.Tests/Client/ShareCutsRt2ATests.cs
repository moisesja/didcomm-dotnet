using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT2-A for #111 (owner decision A1, "cut only if others wait", with owner decision C): how many
/// messages are cut when their shares end, against how many messages of the delivery wait for a
/// worker. Every timing is driven by the injected <see cref="ManualTimeProvider"/> and rendezvous on
/// host calls; real time is only a hang guard.
/// </summary>
public sealed class ShareCutsRt2ATests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);

    /// <summary>
    /// Two messages whose shares end at the same instant while one message of their delivery waits
    /// for a worker: one cut frees the worker the waiting message needs, so only one is cut. The
    /// other has nothing waiting for it and keeps the rest of the budget (A1): it opens, and it is
    /// not remembered.
    /// </summary>
    /// <remarks>
    /// Six messages, so the first two of the four taken at once have half the budget (10 s) and the
    /// other two all of it. The third fails at once and its worker takes the fifth, so when the first
    /// two shares end only the sixth waits. The first ignores its token; the second needs 12 s and
    /// opens. The second share's timer runs before the first cut's worker has taken the sixth, as
    /// when the timers that come due together fire in one batch.
    /// </remarks>
    [Fact(Skip = "RT2 defect: two shares that end together with one message waiting are both cut")]
    public async Task Two_shares_that_end_together_with_one_message_waiting_cut_only_one()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var register = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            switch (label)
            {
                case "x-0":
                    using (calls.Enter(label, ct))
                    {
                        // Registered after its worker began to wait, so it runs first when the
                        // token is cancelled: the other share due at this instant fires now.
                        await register.Task.ConfigureAwait(false);
                        ct.UnsafeRegister(static state => ((ManualTimeProvider)state!).Advance(TimeSpan.Zero), time);
                        registered.TrySetResult();
                        return await never.Task.ConfigureAwait(false); // ignores its token
                    }
                case "slow-1":
                    var lookup = Task.Delay(TimeSpan.FromSeconds(12), time, ct); // honours its token
                    using (calls.Enter(label, ct))
                        await lookup.ConfigureAwait(false);
                    return UnpackFakes.Opened(label);
                case "fast-2":
                    throw new CryptoException("JWE could not be decrypted.");
                case "open-5":
                    using (calls.Enter(label, ct))
                        return UnpackFakes.Opened(label);
                default:
                    using (calls.Enter(label, ct))
                        return await UnpackFakes.StallUntilCancelled(ct).ConfigureAwait(false);
            }
        }, Budget, time);
        var labels = new[] { "x-0", "slow-1", "fast-2", "stall-3", "stall-4", "open-5" };
        var messages = labels.Select(l => new DeliveredInput(l, TestJwe.Bytes(TestJwe.Anoncrypt(l)))).ToArray();

        Task<IReadOnlyList<DeliveredOutcome>> batch;
        try
        {
            batch = unpacker.UnpackAllAsync(Bob, messages, "request-1", default); // the four workers wait on their calls
            register.TrySetResult();
            await registered.Task.WaitAsync(RealTime);
            await calls.WaitForAsync(4, RealTime); // x-0, slow-1, stall-3, and stall-4 taken after fast-2 failed
            calls.Labels.Should().Contain(new[] { "slow-1", "stall-3", "stall-4" });
            calls.Labels.Should().NotContain("open-5", "only the sixth waits for a worker");

            time.Advance(DeliveredMessageUnpacker.ShareOf(Budget, labels.Length)); // 10 s: the first two shares end
            TokenOf(calls, "x-0").IsCancellationRequested.Should().BeTrue("x-0's share ended while the sixth waited");
            await calls.WaitForAsync(5, RealTime);
            SpinWait.SpinUntil(() => unpacker.Settled >= 3, RealTime).Should().BeTrue("fast-2, x-0's cut and the sixth were recorded");
            TokenOf(calls, "slow-1").IsCancellationRequested.Should().BeFalse("x-0's cut already freed the worker the one waiting message needed");

            time.Advance(TimeSpan.FromSeconds(2)); // 12 s: slow-1's lookup ends
            SpinWait.SpinUntil(() => unpacker.Settled == 4, RealTime).Should().BeTrue("slow-1 opened");
            time.Advance(Budget); // the budget's end, for stall-3 and stall-4
            var outcomes = await batch.WaitAsync(RealTime);

            outcomes[0].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("its share");
            outcomes[5].Succeeded.Should().BeTrue("the sixth got the worker x-0's cut freed");
            outcomes[1].Succeeded.Should().BeTrue("nothing else waited for slow-1's worker, so it kept the rest of the budget");
            unpacker.Memory.HeldBack(Bob, new[] { "slow-1" }).Single().Should().BeFalse("a message that opened is not remembered");
        }
        finally
        {
            never.TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Reservations.Extended == 0, RealTime).Should().BeTrue();
    }

    private static CancellationToken TokenOf(HostCallLog calls, string label)
        => calls.Tokens[calls.Labels.ToList().IndexOf(label)];
}
