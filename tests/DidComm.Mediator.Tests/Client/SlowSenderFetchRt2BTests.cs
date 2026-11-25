using AwesomeAssertions;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// #111 red-team pass 2, RT2-B: the fetch loop's restated bound (fix round 1,
/// <see cref="SlowSenderFetchQa1Tests.RestatedBound"/>) for N ≤ Q messages that never open, when
/// they all name one sender in their outer envelope (its lookups honour their token). Each case is
/// run several times, since the order in which the pool runs a cut call's end and the next take
/// varies from run to run. The clock moves only to the next due timer (L-065).
/// </summary>
public sealed class SlowSenderFetchRt2BTests
{
    private const string MalloryKid = "did:example:mallory#key-x25519-1";
    private const string AliceKid = "did:example:alice#key-x25519-1";
    private const int Runs = 5;

    /// <summary>
    /// The restated bound holds when every message in front names the same sender: a fetch loop
    /// reaches the message behind N of them within the bound, in every run.
    /// </summary>
    [Theory(Skip = "RT2 defect: the restated fetch bound is exceeded when the messages in front name one sender")]
    [InlineData(9, 256, 10)]
    [InlineData(16, 150, 10)]
    [InlineData(15, 64, 10)]
    [InlineData(12, 99, 60)]
    [InlineData(8, 256, 60)]
    public async Task The_message_behind_one_senders_never_opening_messages_is_reached_within_the_restated_bound(int limit, int n, int leaseSeconds)
    {
        var lease = TimeSpan.FromSeconds(leaseSeconds);
        var reaches = new List<TimeSpan>();
        TimeSpan bound = default;
        for (var run = 0; run < Runs; run++)
        {
            var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, lease);
            for (var i = 0; i < n; i++)
                mediator.Enqueue("junk-" + i, Qa1.From(MalloryKid, "junk-" + i));
            mediator.Enqueue("honest", Qa1.From(AliceKid, "honest"));
            bound = SlowSenderFetchQa1Tests.RestatedBound(n, limit, lease, exchange.Unpacker.Memory.FirstPause);

            var reach = await SlowSenderFetchQa1Tests.ReachAsync(exchange, mediator, limit, cap: TimeSpan.FromHours(3));

            reach.Found.Should().BeTrue();
            reaches.Add(reach.Elapsed);
        }

        reaches.Should().OnlyContain(r => r <= bound, "limit {0}, {1} in front from one sender, a lease of {2} s: the bound is {3}, the runs took {4}",
            limit, n, leaseSeconds, bound, string.Join(", ", reaches));
    }
}
