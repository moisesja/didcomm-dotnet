using AwesomeAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// #111, the fetch loop's 96 cells, measured and pinned: what stands in front (messages that name no
/// sender; messages that all name one other sender; messages that all name the honest sender's own
/// key) × the mediator's lease (60 s, 10 s) × the fetch limit (4, 10, 16, 100) × how many stand in
/// front (16, 31, 99, 255). The messages in front never open (their host call waits until its token
/// is cancelled), the honest message is last, and what is measured is the clock time until a fetch
/// returns it opened. The real unpacker and its memory run on the manual clock, driven by
/// <see cref="Qa1Loop"/>, whose remarks state the host's loop and the host's lookups as they are
/// modelled. Each cell prints one line (prefix <c>C |</c>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The bound.</strong> Each cell is also held against the restated bound
/// (<see cref="SlowSenderFetchQa1Tests.RestatedBound"/>): with L the time to learn every message in
/// front once, at limits up to 16 L = ceil(N / limit) budgets, above 16
/// L = ceil(limit / 16) · (ceil(N / limit) budgets + one lease); the bound is L when L fits in the
/// first pause (30 budgets), and 2 · ceil(log2(L / first pause)) · L otherwise. The line says
/// whether the bound is the measured time exactly, or only above it.
/// </para>
/// <para>
/// <strong>The three kinds measure the same.</strong> A named sender's bound is sixteen host calls,
/// as the anonymous partition's is, and sixteen are opened at once: so messages that name one sender
/// are learned as fast as messages that name none, and the honest sender's own message is not
/// refused behind messages naming its key once their calls have ended.
/// </para>
/// </remarks>
public sealed class SlowSenderFetchMatrixTests(ITestOutputHelper output)
{
    private const string AliceKid = "did:example:alice#key-x25519-1";
    private const string MalloryKid = "did:example:mallory#key-x25519-1";

    /// <summary>What the messages in front name in their outer envelope.</summary>
    public enum InFront
    {
        /// <summary>No sender (anoncrypt): the mailbox's anonymous partition.</summary>
        NoSender,

        /// <summary>One sender, not the honest one.</summary>
        OneOtherSender,

        /// <summary>The honest sender's own key.</summary>
        TheHonestSendersKey,
    }

    // lease, limit → the seconds measured with 16, 31, 99 and 255 in front (the same for the three kinds).
    private static readonly Dictionary<(int Lease, int Limit), int[]> Measured = new()
    {
        [(60, 4)] = new[] { 80, 160, 500, 3200 },
        [(60, 10)] = new[] { 40, 80, 200, 520 },
        [(60, 16)] = new[] { 20, 40, 140, 320 },
        [(60, 100)] = new[] { 60, 80, 380, 240 },
        [(10, 4)] = new[] { 80, 160, 500, 3120 },
        [(10, 10)] = new[] { 40, 80, 200, 520 },
        [(10, 16)] = new[] { 20, 40, 140, 320 },
        [(10, 100)] = new[] { 20, 40, 140, 360 },
    };

    private static readonly int[] Ns = { 16, 31, 99, 255 };

    /// <summary>The 96 cells, each with its measured seconds.</summary>
    public static TheoryData<InFront, int, int, int, int> Cells()
    {
        var data = new TheoryData<InFront, int, int, int, int>();
        foreach (var kind in new[] { InFront.NoSender, InFront.OneOtherSender, InFront.TheHonestSendersKey })
        {
            foreach (var lease in new[] { 60, 10 })
            {
                foreach (var limit in new[] { 4, 10, 16, 100 })
                {
                    for (var i = 0; i < Ns.Length; i++)
                        data.Add(kind, lease, limit, Ns[i], Measured[(lease, limit)][i]);
                }
            }
        }
        return data;
    }

    /// <summary>
    /// One cell: <paramref name="n"/> messages of <paramref name="kind"/> that never open, then the
    /// honest sender's message; a fetch loop at <paramref name="limit"/> against a mediator leasing
    /// for <paramref name="leaseSeconds"/>. The honest message is returned opened after exactly the
    /// cell's measured time, which is within the restated bound; no message is refused for
    /// capacity.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cells))]
    public async Task The_message_behind_N_that_never_open_is_returned_in_the_cells_measured_time_within_the_restated_bound(InFront kind, int leaseSeconds, int limit, int n, int seconds)
    {
        var lease = TimeSpan.FromSeconds(leaseSeconds);
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, lease);
        for (var j = 0; j < n; j++)
        {
            var label = "junk-" + j;
            mediator.Enqueue(label, kind == InFront.NoSender ? Qa1.Anon(label) : Qa1.From(kind == InFront.OneOtherSender ? MalloryKid : AliceKid, label));
        }
        mediator.Enqueue("honest", Qa1.From(AliceKid, "honest"));
        var bound = SlowSenderFetchQa1Tests.RestatedBound(n, limit, lease, exchange.Unpacker.Memory.FirstPause);

        var reach = await Qa1Loop.RunAsync(exchange, mediator, limit, cap: TimeSpan.FromHours(30));

        var against = reach.Elapsed == bound ? "exact" : reach.Elapsed < bound ? "bounds" : "MEASURED ABOVE IT";
        output.WriteLine(FormattableString.Invariant($"C | kind {kind} | lease {leaseSeconds} | limit {limit} | N {n} | {Qa1Loop.Describe(reach)}; restated bound {bound.TotalSeconds} s: {against}"));
        reach.Found.Should().BeTrue();
        reach.Elapsed.Should().Be(TimeSpan.FromSeconds(seconds), "the measured time of the cell");
        reach.Elapsed.Should().BeLessThanOrEqualTo(bound, "the restated bound");
        reach.Refused.Should().Be(0, "no message is refused for capacity: sixteen are opened at once, each partition holds sixteen, and the calls of the fetch before have ended");
    }

    // ---- the loop's model of the host's lookups, forced ------------------------------------------

    /// <summary>
    /// <see cref="Qa1Loop"/> starts a fetch only once the host calls of the fetch before have ended,
    /// forced: sixteen messages that name no sender and never open, then an honest message that
    /// names none either, at limit 10. The ten host calls of the first fetch see their token
    /// cancelled at the budget's end and then stay as they are, each holding its reservation, until
    /// the loop has been seen waiting for them; only then are they ended. So the second fetch (six
    /// that never open and the honest message) starts with no reservation held: nothing is refused,
    /// and the honest message is returned at 40 s, the cell's figure.
    /// </summary>
    [Fact]
    public async Task A_fetch_is_started_only_once_the_host_calls_of_the_fetch_before_have_ended()
    {
        var (exchange, mediator) = LookupsThatEndWhenReleased();
        try
        {
            var reach = await Qa1Loop.RunAsync(exchange, mediator, limit: 10, cap: TimeSpan.FromMinutes(30), whileHostCallsEnd: exchange.Host.ReleaseIgnored);

            output.WriteLine(FormattableString.Invariant($"lookups ended only once the loop waited for them: {Qa1Loop.Describe(reach)}; most reserved before a fetch {reach.MostReservedBeforeAFetch}"));
            reach.MostReservedBeforeAFetch.Should().Be(10, "the ten host calls of the first fetch still held their reservations when the second fetch was due");
            reach.Refused.Should().Be(0, "the second fetch was started once they had ended");
            reach.Found.Should().BeTrue();
            reach.Elapsed.Should().Be(TimeSpan.FromSeconds(40));
        }
        finally
        {
            exchange.Host.ReleaseIgnored();
        }
    }

    /// <summary>
    /// What the product does when the next fetch does arrive before those lookups have ended (the
    /// residual R-13 through a fetch loop; <see cref="DeliveredUnpackNextDeliveryRt3Tests"/> pins it
    /// on one delivery): the same queue, with the second fetch started at once. Ten of the anonymous
    /// partition's sixteen reservations are still held, so the second fetch starts its six messages
    /// that never open and refuses the seventh, the honest one ("unpack capacity exhausted"): it is
    /// not opened, not remembered and not acknowledged, and stays leased. With the lookups ended
    /// after that fetch, the loop returns it opened when its lease ends: at 80 s, where the loop
    /// that waits returns it at 40 s.
    /// </summary>
    [Fact]
    public async Task A_fetch_that_arrives_while_the_lookups_of_the_fetch_before_are_still_ending_refuses_what_the_bound_does_not_leave_free_and_the_message_is_returned_a_lease_later()
    {
        var (exchange, mediator) = LookupsThatEndWhenReleased();
        try
        {
            var client = exchange.Client();
            var start = exchange.Clock.TrueNow;
            var first = await Task.Run(() => exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 10)));
            var reservedWhenTheSecondStarts = exchange.Unpacker.Reserved;
            var second = await Task.Run(() => exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 10)));
            var afterTwo = exchange.Clock.TrueNow - start;
            exchange.Host.ReleaseIgnored();
            var rest = await Qa1Loop.RunAsync(exchange, mediator, limit: 10, cap: TimeSpan.FromMinutes(30), whileHostCallsEnd: exchange.Host.ReleaseIgnored);
            var total = exchange.Clock.TrueNow - start;

            var honest = second.Items.Single(i => i.Id == "honest");
            output.WriteLine(FormattableString.Invariant($"the second fetch started with {reservedWhenTheSecondStarts} reservations still held: {second.Items.Count(i => i.Error is not null && i.Error.Message.Contains("ran out while it was being opened"))} started, {second.Items.Count(i => i.Error is not null && i.Error.Message.Contains("unpack capacity exhausted"))} refused (the honest message: {honest.Error?.Message.Split(':')[0] ?? "opened"}); two fetches took {afterTwo.TotalSeconds} s; the honest message was returned opened at {total.TotalSeconds} s ({(rest.Found ? "found" : "not found")})"));
            first.Items.Should().HaveCount(10).And.OnlyContain(i => i.Error is TimeoutException);
            reservedWhenTheSecondStarts.Should().Be(10, "the ten lookups of the first fetch are still ending");
            second.Items.Should().HaveCount(7);
            second.Items.Take(6).Should().OnlyContain(i => i.Error is TimeoutException && i.Error.Message.Contains("ran out while it was being opened"), "six fit under the bound of sixteen and never open");
            honest.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("unpack capacity exhausted", "the seventeenth reservation of the partition is refused");
            exchange.Unpacker.Memory.HeldBack(Qa1.Bob, new[] { "honest" }).Single().Should().BeFalse("a refused message is not remembered");
            mediator.Holds("honest").Should().BeFalse("it was acknowledged once it opened");
            rest.Found.Should().BeTrue();
            total.Should().Be(TimeSpan.FromSeconds(80), "its lease, taken at 20 s for 60 s, ends at 80 s");
        }
        finally
        {
            exchange.Host.ReleaseIgnored();
        }
    }

    // Sixteen messages that name no sender, then an honest one that names none either, at a
    // mediator leasing for 60 s. A host call of the sixteen never opens; when its token is
    // cancelled it stays as it is, holding its reservation, until the test releases it: a lookup
    // whose end comes when the test says, after its delivery's result is set.
    private static (Qa1Exchange Exchange, Qa1Mediator Mediator) LookupsThatEndWhenReleased()
    {
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "honest" ? Qa1Step.Open : new Qa1Step(Qa1Kind.IgnoresToken), Qa1.Lease);
        for (var j = 0; j < 16; j++)
            mediator.Enqueue("junk-" + j, Qa1.Anon("junk-" + j));
        mediator.Enqueue("honest", Qa1.Anon("honest"));
        return (exchange, mediator);
    }
}
