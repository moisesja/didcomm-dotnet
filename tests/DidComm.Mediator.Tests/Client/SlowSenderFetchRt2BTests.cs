using AwesomeAssertions;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// #111, RT2-B: the fetch loop's restated bound (fix round 1,
/// <see cref="SlowSenderFetchQa1Tests.RestatedBound"/>) for N ≤ Q messages that never open, when
/// they all name one sender in their outer envelope. Its lookups honour their token and have ended
/// before the next fetch (<see cref="Qa1Loop"/>, whose remarks state that model). Each case is run
/// several times. The clock moves only to the next due timer (L-065).
/// </summary>
/// <remarks>
/// The independent pass on fix round 1 committed this skipped, as a defect: with four opened at
/// once, four per sender and a time limit per message, a sender's next message could be refused
/// right after one of its messages was cut, and the bound was exceeded by up to 2.3 times. With no
/// time limit per message and sixteen per sender (owner decisions E and G) a delivery starts all of
/// one sender's messages up to sixteen, as it does messages that name none; what remains of that
/// residual is between deliveries (<see cref="DeliveredUnpackNextDeliveryRt3Tests"/>).
/// </remarks>
public sealed class SlowSenderFetchRt2BTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string MalloryKid = "did:example:mallory#key-x25519-1";
    private const string AliceKid = "did:example:alice#key-x25519-1";
    private const int Runs = 5;

    /// <summary>
    /// The restated bound holds when every message in front names the same sender: a fetch loop
    /// reaches the message behind N of them within the bound, in every run; none of them is refused
    /// for its sender's bound; and each run takes exactly as long as the same number of messages
    /// that name no sender.
    /// </summary>
    [Theory]
    [InlineData(9, 256, 10)]
    [InlineData(16, 150, 10)]
    [InlineData(15, 64, 10)]
    [InlineData(12, 99, 60)]
    [InlineData(8, 256, 60)]
    public async Task The_message_behind_one_senders_never_opening_messages_is_reached_within_the_restated_bound_as_fast_as_behind_messages_that_name_no_sender(int limit, int n, int leaseSeconds)
    {
        var lease = TimeSpan.FromSeconds(leaseSeconds);
        var reaches = new List<TimeSpan>();
        var refused = 0;
        TimeSpan bound = default;
        for (var run = 0; run < Runs; run++)
        {
            var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, lease);
            for (var i = 0; i < n; i++)
                mediator.Enqueue("junk-" + i, Qa1.From(MalloryKid, "junk-" + i));
            mediator.Enqueue("honest", Qa1.From(AliceKid, "honest"));
            bound = SlowSenderFetchQa1Tests.RestatedBound(n, limit, lease, exchange.Unpacker.Memory.FirstPause);

            var reach = await Qa1Loop.RunAsync(exchange, mediator, limit, cap: TimeSpan.FromHours(3));

            reach.Found.Should().BeTrue();
            reaches.Add(reach.Elapsed);
            refused += reach.Refused;
        }
        var (anonymous, anonymousMediator) = SlowSenderFetchQa1Tests.Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, lease);
        SlowSenderFetchQa1Tests.Queue(anonymousMediator, junk: n, honestAt: n);
        var withoutASender = await Qa1Loop.RunAsync(anonymous, anonymousMediator, limit, cap: TimeSpan.FromHours(3));

        output.WriteLine(FormattableString.Invariant($"RT2B | limit {limit} | N {n} from one sender | lease {leaseSeconds} s | {Runs} runs took {string.Join(", ", reaches.Select(r => r.TotalSeconds))} s; refused for the sender's bound {refused}; the same N naming no sender {withoutASender.Elapsed.TotalSeconds} s; restated bound {bound.TotalSeconds} s"));
        reaches.Should().OnlyContain(r => r <= bound, "limit {0}, {1} in front from one sender, a lease of {2} s: the bound is {3}, the runs took {4}",
            limit, n, leaseSeconds, bound, string.Join(", ", reaches));
        refused.Should().Be(0, "a delivery starts every message of one sender up to sixteen, and the calls of the fetch before have ended");
        withoutASender.Found.Should().BeTrue();
        reaches.Should().OnlyContain(r => r == withoutASender.Elapsed, "one sender's messages are learned as fast as messages that name no sender ({0})", withoutASender.Elapsed);
    }
}
