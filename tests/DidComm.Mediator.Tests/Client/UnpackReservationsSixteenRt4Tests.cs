using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT4 (#111): the container's bound of host unpack calls (<see cref="UnpackReservations.MaxReserved"/>,
/// 256) against the bound of sixteen per sender and mailbox, with lookups that ignore their token
/// and so keep their reservations after their deliveries ended. What is checked: how many senders
/// in one mailbox, and how many mailboxes for one sender, hold the whole container; what a message
/// delivered then is told; and that nothing stays counted once the lookups have ended. The lookups
/// here wait without a thread; the manual clock ends each delivery; real time is only a hang guard.
/// </summary>
public sealed class UnpackReservationsSixteenRt4Tests
{
    private const int AtOnce = DeliveredMessageUnpacker.FetchConcurrency;
    private const int PerSender = UnpackReservations.MaxPerSender;
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout;
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Held, as <see cref="UnpackReservations.MaxReserved"/> states it: sixteen senders at their
    /// bound in one mailbox (<paramref name="oneMailbox"/>), or one sender at its bound in each of
    /// sixteen mailboxes, hold all 256; one fewer leaves sixteen free. With all 256 held, a message
    /// that would open, of another sender in another mailbox, is refused for the container and its
    /// outcome is inconclusive; a seventeenth message of a held sender in its mailbox is refused
    /// for its sender's bound, also inconclusive; no host code runs for either. When the lookups
    /// end, no reservation and no partition is left, and the same message opens.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sixteen_partitions_at_the_senders_bound_hold_the_whole_container_and_nothing_stays_counted_when_their_lookups_end(bool oneMailbox)
    {
        (MaxReservedOverPerSender * PerSender).Should().Be(UnpackReservations.MaxReserved);
        PerSender.Should().Be(AtOnce, "one delivery of one sender's messages takes that sender's whole bound");
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            calls.Enter(label, ct);
            return label.StartsWith("target", StringComparison.Ordinal) ? Task.FromResult(UnpackFakes.Opened(label)) : never.Task;
        }, Budget, time);
        DeliveryScope ScopeOf(int p) => new(FakePickupExchange.M, oneMailbox ? FakePickupExchange.L : $"did:example:mailbox-{p}");
        string KidOf(int p) => oneMailbox ? $"did:example:sender-{p}#key-1" : "did:example:sender-0#key-1";
        var other = new DeliveryScope(FakePickupExchange.M, "did:example:another-mailbox");
        try
        {
            // One delivery per partition, each of sixteen messages of that partition's sender.
            for (var p = 0; p < MaxReservedOverPerSender; p++)
            {
                if (p == MaxReservedOverPerSender - 1)
                {
                    unpacker.Reserved.Should().Be(UnpackReservations.MaxReserved - PerSender, "fifteen partitions at their bound leave sixteen free");
                    var free = (await unpacker.UnpackAllAsync(other, new[] { From("did:example:dave#key-1", "target-while-free") }, "request-0", default).WaitAsync(RealTime)).Single();
                    free.Succeeded.Should().BeTrue("the container is not full yet");
                    SpinWait.SpinUntil(() => unpacker.Reserved == UnpackReservations.MaxReserved - PerSender, RealTime).Should().BeTrue();
                }
                var held = calls.Count;
                var messages = Enumerable.Range(0, PerSender).Select(i => From(KidOf(p), $"held-{p}-{i}")).ToArray();
                var delivery = unpacker.UnpackAllAsync(ScopeOf(p), messages, "request-1", default);
                await calls.WaitForAsync(held + PerSender, RealTime);
                time.Advance(Budget);
                (await delivery.WaitAsync(RealTime)).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("while it was being opened") && !o.Inconclusive);
            }
            unpacker.Reserved.Should().Be(UnpackReservations.MaxReserved, "sixteen partitions at the bound of sixteen hold it all");
            unpacker.Reservations.Partitions.Should().Be(MaxReservedOverPerSender);
            var hostCalls = calls.Count;

            var refusedForTheContainer = (await unpacker.UnpackAllAsync(other, new[] { From("did:example:dave#key-1", "target") }, "request-2", default).WaitAsync(RealTime)).Single();
            var refusedForItsSender = (await unpacker.UnpackAllAsync(ScopeOf(0), new[] { From(KidOf(0), "target-of-a-held-sender") }, "request-3", default).WaitAsync(RealTime)).Single();

            refusedForTheContainer.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("capacity exhausted").And.Contain("all mailboxes together");
            refusedForTheContainer.Inconclusive.Should().BeTrue();
            refusedForItsSender.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("capacity exhausted").And.Contain("for its sender");
            refusedForItsSender.Inconclusive.Should().BeTrue();
            calls.Count.Should().Be(hostCalls, "a refused message's host code does not start");
            unpacker.Memory.Count.Should().Be(UnpackReservations.MaxReserved, "only the messages that had their whole budget are remembered; a refusal writes nothing");
        }
        finally
        {
            never.TrySetException(new CryptoException("JWE could not be decrypted."));
        }

        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue("the lookups ended and gave their reservations back");
        unpacker.Reservations.Partitions.Should().Be(0, "no partition is left counted");
        unpacker.Opening.Should().Be(0);
        var after = (await unpacker.UnpackAllAsync(other, new[] { From("did:example:dave#key-1", "target") }, "request-4", default).WaitAsync(RealTime)).Single();
        after.Succeeded.Should().BeTrue("the message that was refused opens once the container is free");
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue();
    }

    private const int MaxReservedOverPerSender = UnpackReservations.MaxReserved / UnpackReservations.MaxPerSender; // sixteen

    private static DeliveredInput From(string kid, string label) => new(label, TestJwe.Bytes(TestJwe.Authcrypt(kid, label)));
}
