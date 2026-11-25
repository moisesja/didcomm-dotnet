using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.InteropTests.AspNetCore;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// #111 acceptance, end to end against this library's mediator on a TestServer: a loop that fetches
/// until drained reaches an honest message queued behind messages that never open, within the
/// bound the client's memory of slow messages gives (one budget per learning fetch, each learning up
/// to <c>min(limit, 16)</c> of them, and fetches that take no time over the ones it remembers). How
/// long it takes is also measured here, cell by cell, printed, and pinned. The client's clock is a
/// manual one, moved one timer at a time by <see cref="SlowFetchLoop"/>, whose remarks state the
/// host's loop and the host's lookups as they are modelled; the mediator's clock moves with it. The
/// unpack step is the real one, with the waits of <see cref="SlowSenderSeam"/> around it.
/// </summary>
/// <remarks>
/// The client opens up to sixteen messages of a delivery at once (#111, owner decision G), and this
/// mediator delivers at most ten per fetch at its defaults, so every message of a delivery here starts when the
/// delivery does: a delivery of messages that never open lasts one budget and all of them are
/// remembered at its end.
/// </remarks>
public sealed class PickupClientSlowSenderAcceptanceTests(ITestOutputHelper output)
{
    private const int None = -1;
    private static readonly TimeSpan DefaultLease = new MessagePickupOptions().LeaseTimeout;

    // A horizon, not a bound: a run ends when the honest message opens or this much clock time has
    // passed, so "not reached" would mean "not reached within six hours" (more than four times the
    // longest pause, 256 budgets).
    private static readonly TimeSpan Horizon = TimeSpan.FromHours(6);

    /// <summary>
    /// The A2 matrix, limit × messages that never open in front × the mediator's lease, each cell
    /// with its measured figures: the seconds until the honest message is returned opened, how many
    /// of them the loop waited for a lease to end (and in how many waits), the fetches that lasted a
    /// budget, the fetches made, the most times one message that never opens was opened, and the
    /// second at which one was first opened again (<see cref="None"/>: never).
    /// </summary>
    public static TheoryData<int, int, int, int, int, int, int, int, int, int> Matrix()
        => new()
        {
            // limit, N, lease | seconds, waiting, waits | budget fetches, fetches | most openings, first re-opening
            { 4, 1, 60, 20, 0, 0, 1, 1, 1, None },
            { 4, 1, 10, 20, 0, 0, 1, 1, 1, None },
            { 4, 16, 60, 80, 0, 0, 4, 7, 1, None },
            { 4, 16, 10, 80, 0, 0, 4, 15, 1, None },
            { 4, 31, 60, 160, 0, 0, 8, 15, 1, None },
            { 4, 31, 10, 160, 0, 0, 8, 36, 1, None },
            { 4, 99, 60, 500, 0, 0, 25, 117, 1, None },
            { 4, 99, 10, 500, 0, 0, 25, 325, 1, None },
            { 10, 1, 60, 20, 0, 0, 1, 1, 1, None },
            { 10, 1, 10, 20, 0, 0, 1, 1, 1, None },
            { 10, 16, 60, 40, 0, 0, 2, 2, 1, None },
            { 10, 16, 10, 40, 0, 0, 2, 3, 1, None },
            { 10, 31, 60, 80, 0, 0, 4, 5, 1, None },
            { 10, 31, 10, 80, 0, 0, 4, 10, 1, None },
            { 10, 99, 60, 200, 0, 0, 10, 22, 1, None },
            { 10, 99, 10, 200, 0, 0, 10, 55, 1, None },
        };

    /// <summary>
    /// A2: <paramref name="slow"/> messages whose sender's DID never resolves (each anoncrypted around
    /// its authcrypt, so they are counted in the mailbox's anonymous partition of 16, which no
    /// delivery here fills), then Alice's message. A loop of <c>FetchAsync(limit)</c> reaches Alice's
    /// message within <c>ceil(slow / min(limit, 16))</c> fetches that take time, each at most one
    /// budget; and every message that never opens is opened exactly once. That is what this test
    /// asserted before the client opened sixteen at once, and it asserts it unchanged. It also pins
    /// what is measured in each cell: the time to Alice's message, split into opening (whole
    /// budgets) and waiting for leases; the fetches; and that no message is opened a second time.
    /// </summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task A_fetch_until_drained_loop_reaches_the_message_behind_ones_that_never_open_within_the_bound(
        int limit, int slow, int leaseSeconds, int seconds, int waitingSeconds, int waits, int budgetFetches, int fetches, int mostOpenings, int firstReopeningSeconds)
    {
        var lease = TimeSpan.FromSeconds(leaseSeconds);
        var (mediator, store) = await StartMediatorAsync(lease);
        await using var _ = mediator;
        var time = new QaManualTimeProvider();
        var seam = new SlowSenderSeam(time);
        await using var harness = await SlowFetchLoop.HarnessAsync(mediator, time, seam);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var mallory = await harness.NewAliceAsync();
        var alice = await harness.NewAliceAsync();
        for (var i = 0; i < slow; i++)
            await SendAnoncryptedAsync(mediator, mallory, recipient, "slow");
        var honest = await PickupE2E.SendAsync(mediator, alice, recipient, "honest");
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(slow + 1, "everything fits the mediator's quota (MaxQueueMessages)");
        var budget = harness.Channel.Options.UnpackTimeout;
        var bound = Bound(slow, limit);
        var loop = new SlowFetchLoop(mediator, harness, time, store, seam, bob.Did);
        var started = time.GetUtcNow();

        var reached = await loop.RunAsync(honest.Id, limit, Horizon);

        // Observed, not computed: how often the host call of each message that never opens ran, and when.
        var total = time.GetUtcNow() - started;
        var openings = seam.Openings.Where(o => o.Sender is null).ToArray();
        var perMessage = new Dictionary<string, int>(StringComparer.Ordinal);
        TimeSpan? firstReopening = null;
        foreach (var (_, packed, at) in openings)
        {
            perMessage[packed] = perMessage.GetValueOrDefault(packed) + 1;
            if (perMessage[packed] > 1)
                firstReopening ??= at - started;
        }
        var most = perMessage.Count == 0 ? 0 : perMessage.Values.Max();
        output.WriteLine(FormattableString.Invariant($"A2 | limit {limit} | N {slow} | lease {leaseSeconds} s | reached {(reached ? "yes" : "no")}, {total.TotalSeconds} s = opening {loop.Opening.TotalSeconds} s + waiting for leases {loop.WaitingForLeases.TotalSeconds} s ({loop.LeaseWaits} waits) + other {(total - loop.Opening - loop.WaitingForLeases).TotalSeconds} s; {loop.BudgetFetches} fetches took a budget, of {loop.Fetches}; {openings.Length} host calls for the {slow}; most openings of one message {most}; first re-opening {(firstReopening is { } t ? t.TotalSeconds + " s" : "none")}; second learning pass {(most > 1 ? "yes" : "no")}; bound {bound} learning fetches, {(budget * bound).TotalSeconds} s"));

        // What this test asserted before owner decision E, unchanged: the bound as it stood then.
        reached.Should().BeTrue("Alice's message is reached within {0} learning fetches; after {1} ({2} fetches, {3}) it was not", bound, loop.LearningFetches, loop.Fetches, loop.Elapsed);
        loop.LearningFetches.Should().BeLessThanOrEqualTo(bound);
        loop.Elapsed.Should().BeLessThanOrEqualTo(budget * bound, "each learning fetch takes at most one budget, and the others take none");
        Math.Ceiling(loop.Elapsed / lease).Should().BeLessThanOrEqualTo(Math.Ceiling(budget * bound / lease), "in the mediator's lease periods");
        seam.Calls.Count(sender => sender is null).Should().Be(slow, "each message that never opens is opened once, then held back while its pause lasts");
        loop.Items.Where(i => i.Error is not null).Should().OnlyContain(i => i.Error is TimeoutException, "a message that never opens fails when its delivery's budget ends, or at once while it is held back");

        // What is measured in this cell, pinned.
        total.Should().Be(TimeSpan.FromSeconds(seconds), "the measured time to Alice's message");
        loop.Elapsed.Should().Be(total, "nothing but the loop's fetches and its waits for leases moves the clock");
        loop.WaitingForLeases.Should().Be(TimeSpan.FromSeconds(waitingSeconds), "the part of it the loop waited, after a fetch that returned nothing, for a lease to end");
        loop.LeaseWaits.Should().Be(waits, "how often a fetch returned nothing");
        loop.BudgetFetches.Should().Be(budgetFetches, "the fetches that lasted a whole budget");
        loop.LearningFetches.Should().Be(loop.BudgetFetches, "a fetch takes a whole budget or no time: a message that never opens runs until its delivery's budget ends");
        loop.Opening.Should().Be(budget * budgetFetches);
        loop.Fetches.Should().Be(fetches);
        most.Should().Be(mostOpenings, "the most times one message that never opens was opened");
        firstReopening.Should().Be(firstReopeningSeconds == None ? null : TimeSpan.FromSeconds(firstReopeningSeconds), "when a message that never opens was first opened again");
        loop.Items.Should().NotContain(i => i.Error != null && i.Error.Message.Contains("unpack capacity exhausted", StringComparison.Ordinal), "no message is refused: a delivery here has at most ten messages, the anonymous partition holds sixteen, and the calls of the fetch before have ended");
        loop.Items.Should().NotContain(i => i.Error != null && i.Error.Message.Contains("before its turn came", StringComparison.Ordinal), "every message of a delivery of at most ten starts when the delivery does");
    }

    /// <summary>
    /// The loop's model of the host's lookups, forced (<see cref="SlowFetchLoop"/> starts a fetch
    /// only once the lookups of the fetch before have ended): the cell limit 10, 31 in front, a 60 s
    /// lease (80 s). The ten host calls of the first fetch stay held at a gate after their
    /// cancellation, each holding its reservation, until the loop has been seen keeping the second
    /// fetch from starting for them. The cell's figure holds: nothing refused, 80 s. Started at
    /// once instead, the second fetch finds ten of the anonymous partition's sixteen reservations
    /// held and refuses four of its ten.
    /// </summary>
    [Fact]
    public async Task The_loop_starts_a_fetch_only_once_the_host_calls_of_the_fetch_before_have_ended()
    {
        var (mediator, store) = await StartMediatorAsync(DefaultLease);
        await using var _ = mediator;
        var time = new QaManualTimeProvider();
        var seam = new SlowSenderSeam(time);
        await using var harness = await SlowFetchLoop.HarnessAsync(mediator, time, seam);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var mallory = await harness.NewAliceAsync();
        var alice = await harness.NewAliceAsync();
        for (var i = 0; i < 31; i++)
            await SendAnoncryptedAsync(mediator, mallory, recipient, "slow");
        var honest = await PickupE2E.SendAsync(mediator, alice, recipient, "honest");
        var loop = new SlowFetchLoop(mediator, harness, time, store, seam, bob.Did);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        seam.EndGate = gate.Task;
        // Opened only once the loop has been seen waiting with reservations still held (and, as a
        // hang guard only, after the timeout).
        var opener = Task.Run(() =>
        {
            SpinWait.SpinUntil(() => loop.SawLookupsStillEnding, MediatorTestServer.Timeout);
            gate.TrySetResult();
        });
        try
        {
            var reached = await loop.RunAsync(honest.Id, limit: 10, Horizon);

            var refused = loop.Items.Count(i => i.Error != null && i.Error.Message.Contains("unpack capacity exhausted", StringComparison.Ordinal));
            output.WriteLine(FormattableString.Invariant($"limit 10, 31 in front, lease 60 s, the first fetch's lookups ending late: reached {(reached ? "yes" : "no")} after {loop.Elapsed.TotalSeconds} s; {refused} refused; most reserved before a fetch {loop.MostReservedBeforeAFetch}"));
            loop.MostReservedBeforeAFetch.Should().Be(10, "the ten host calls of the first fetch still held their reservations when the second fetch was due");
            refused.Should().Be(0, "the second fetch was started once they had ended");
            reached.Should().BeTrue();
            loop.Elapsed.Should().Be(TimeSpan.FromSeconds(80), "the cell's figure");
        }
        finally
        {
            gate.TrySetResult();
        }
        await opener.WaitAsync(MediatorTestServer.Timeout + MediatorTestServer.Timeout);
    }

    /// <summary>
    /// A3: Mallory sends thirty messages whose sender lookups never end and ignore their token, then
    /// Alice (another sender) sends hers. Mallory's bound is sixteen host calls
    /// (<see cref="UnpackReservations.MaxPerSender"/>): they are the only ones of hers that ever run.
    /// This mediator delivers at most ten per fetch at its defaults, so the bound is reached over several fetches:
    /// each fetch that starts some of her lookups lasts one budget (a message has no time limit of
    /// its own: #111, owner decision E), and from the fetch that starts her sixteenth on, each of her
    /// further messages is refused at once, without host code, as "unpack capacity exhausted" for
    /// her sender; a fetch made only of such messages takes no time. Her sixteen are remembered, so
    /// they are held back when their leases end. Alice's message is reached within
    /// <c>ceil(16 / limit)</c> budgets, the time sixteen messages that never open take: 40 s at limit
    /// 10, inside one of the mediator's leases; 80 s at limit 4, which is more than one lease of
    /// 60 s. Once the lookups end, every reservation is given back.
    /// </summary>
    /// <remarks>
    /// Reworked for owner decision G (sixteen at once, sixteen per sender). With a bound of four per
    /// sender this test queued sixteen of Mallory's, one fetch filled her bound, and Alice's message
    /// was reached in one learning fetch at both limits (measured before owner decision E by this
    /// test as it was then: 20 s at limit 4, 10 s at limit 10). With sixteen per sender, filling the bound
    /// takes <c>ceil(16 / limit)</c> fetches of one budget each, so the row at limit 4 no longer
    /// holds "within one lease"; it pins the 80 s that is measured, and the name says what holds at
    /// both limits.
    /// </remarks>
    [Theory]
    [InlineData(4, 80, 4, 10)]
    [InlineData(10, 40, 2, 4)]
    public async Task One_sender_whose_lookups_ignore_their_token_is_held_to_its_bound_of_sixteen_and_another_senders_message_is_reached_in_the_time_sixteen_take(int limit, int seconds, int learningFetches, int fetches)
    {
        var (mediator, store) = await StartMediatorAsync(DefaultLease);
        await using var _ = mediator;
        var time = new QaManualTimeProvider();
        var seam = new SlowSenderSeam(time);
        await using var harness = await SlowFetchLoop.HarnessAsync(mediator, time, seam);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var mallory = await harness.NewAliceAsync();
        var alice = await harness.NewAliceAsync();
        seam.LookupNeverEnds(mallory.Did);
        const int fromMallory = 30;
        const int perSender = UnpackReservations.MaxPerSender;
        perSender.Should().Be(16);
        for (var i = 0; i < fromMallory; i++)
            await PickupE2E.SendAsync(mediator, mallory, recipient, "mallory");
        var honest = await PickupE2E.SendAsync(mediator, alice, recipient, "honest");
        var budget = harness.Channel.Options.UnpackTimeout;
        var bound = Bound(perSender, limit);
        var loop = new SlowFetchLoop(mediator, harness, time, store, seam, bob.Did);
        try
        {
            var reached = await loop.RunAsync(honest.Id, limit, Horizon);

            var refused = loop.Items.Where(i => i.Error?.Message.Contains("unpack capacity exhausted", StringComparison.Ordinal) == true).ToArray();
            var notStarted = loop.Items.Where(i => i.Error?.Message.Contains("before its turn came", StringComparison.Ordinal) == true).ToArray();
            var heldBack = loop.Items.Count(i => i.Error?.Message.Contains("did not open when it was delivered before", StringComparison.Ordinal) == true);
            var onlyRefused = loop.Log.Where(f => f.Items.Count > 0 && f.Items.All(i => i.Error?.Message.Contains("unpack capacity exhausted", StringComparison.Ordinal) == true)).ToArray();
            output.WriteLine(FormattableString.Invariant($"A3 | limit {limit} | {fromMallory} from one sender whose lookups ignore their token | lease {DefaultLease.TotalSeconds} s | reached {(reached ? "yes" : "no")}, {loop.Elapsed.TotalSeconds} s = opening {loop.Opening.TotalSeconds} s + waiting for leases {loop.WaitingForLeases.TotalSeconds} s; {loop.LearningFetches} fetches took a budget, of {loop.Fetches}; {seam.Calls.Count(sender => sender == mallory.Did)} host calls for the sender; {refused.Length} refused for its bound, {onlyRefused.Length} fetches made only of them; {notStarted.Length} out of time before their turn; {heldBack} held back; {harness.Channel.Unpacker.Reserved} reserved; bound for sixteen {bound} learning fetches, {(budget * bound).TotalSeconds} s"));
            reached.Should().BeTrue();
            // Held to its bound.
            seam.Calls.Count(sender => sender == mallory.Did).Should().Be(perSender, "only the sixteen host calls of the sender's bound ever run");
            harness.Channel.Unpacker.Reserved.Should().Be(perSender, "the sixteen lookups still hold their reservations");
            // The others are refused at once.
            refused.Should().HaveCount(fromMallory - perSender, "each of her messages beyond the sixteenth is refused once, and Alice's message is reached before any comes back");
            refused.Should().OnlyContain(i => i.Error is TimeoutException && i.Error.Message.Contains("its sender in this mailbox", StringComparison.Ordinal), "for her sender's bound, not for the container");
            onlyRefused.Should().NotBeEmpty("at least one fetch brings only messages of hers beyond the bound").And.OnlyContain(f => f.Elapsed == TimeSpan.Zero, "a refusal takes no time");
            notStarted.Should().BeEmpty("a delivery of at most ten starts all its messages, or refuses them");
            // Another sender's message is reached in the time sixteen messages that never open take.
            loop.LearningFetches.Should().Be(learningFetches).And.BeLessThanOrEqualTo(bound, "her bound of sixteen fills in ceil(16 / limit) fetches, each one budget");
            loop.Elapsed.Should().Be(TimeSpan.FromSeconds(seconds), "the measured time to Alice's message").And.BeLessThanOrEqualTo(budget * bound);
            loop.Fetches.Should().Be(fetches);
            if (limit >= 10)
                loop.Elapsed.Should().BeLessThan(DefaultLease, "at limit 10 Alice's message is reached within one lease");
            else
                loop.Elapsed.Should().BeGreaterThan(DefaultLease, "at limit 4, filling a bound of sixteen takes four budgets: more than one lease, stated plainly");
        }
        finally
        {
            seam.End();
        }
        SpinWait.SpinUntil(() => harness.Channel.Unpacker.Reserved == 0, MediatorTestServer.Timeout).Should().BeTrue("each reservation is given back when its lookup ends");
    }

    /// <summary>
    /// A3, the other side of the comparison, on the same harness and queue: thirty messages from one
    /// sender, then another sender's message, when that sender's lookups honour their token (its
    /// messages never open: each is opened once, runs until its delivery's budget ends, and is
    /// remembered). None of its messages is refused: its bound of sixteen is never reached, since a
    /// delivery here has at most ten and the calls of the fetch before have ended. The other
    /// sender's message is reached after <c>ceil(30 / limit)</c> budgets: 160 s at limit 4, 60 s at
    /// limit 10. With lookups that ignore their token
    /// (<see cref="One_sender_whose_lookups_ignore_their_token_is_held_to_its_bound_of_sixteen_and_another_senders_message_is_reached_in_the_time_sixteen_take"/>)
    /// the sender is held to sixteen host calls, the rest of its messages are refused at once, and
    /// the same message is reached after 80 s and 40 s.
    /// </summary>
    [Theory]
    [InlineData(4, 160, 8, 15)]
    [InlineData(10, 60, 3, 5)]
    public async Task One_sender_whose_lookups_honour_their_token_has_every_message_opened_once_and_another_senders_message_is_reached_in_the_time_thirty_take(int limit, int seconds, int learningFetches, int fetches)
    {
        var (mediator, store) = await StartMediatorAsync(DefaultLease);
        await using var _ = mediator;
        var time = new QaManualTimeProvider();
        var seam = new SlowSenderSeam(time);
        await using var harness = await SlowFetchLoop.HarnessAsync(mediator, time, seam);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var mallory = await harness.NewAliceAsync();
        var alice = await harness.NewAliceAsync();
        const int fromMallory = 30;
        for (var i = 0; i < fromMallory; i++)
            await PickupE2E.SendAsync(mediator, mallory, recipient, "slow");
        var honest = await PickupE2E.SendAsync(mediator, alice, recipient, "honest");
        var budget = harness.Channel.Options.UnpackTimeout;
        var bound = Bound(fromMallory, limit);
        var loop = new SlowFetchLoop(mediator, harness, time, store, seam, bob.Did);

        var reached = await loop.RunAsync(honest.Id, limit, Horizon);

        var refused = loop.Items.Count(i => i.Error?.Message.Contains("unpack capacity exhausted", StringComparison.Ordinal) == true);
        var notStarted = loop.Items.Count(i => i.Error?.Message.Contains("before its turn came", StringComparison.Ordinal) == true);
        var heldBack = loop.Items.Count(i => i.Error?.Message.Contains("did not open when it was delivered before", StringComparison.Ordinal) == true);
        output.WriteLine(FormattableString.Invariant($"A3 | limit {limit} | {fromMallory} from one sender whose lookups honour their token | lease {DefaultLease.TotalSeconds} s | reached {(reached ? "yes" : "no")}, {loop.Elapsed.TotalSeconds} s = opening {loop.Opening.TotalSeconds} s + waiting for leases {loop.WaitingForLeases.TotalSeconds} s; {loop.LearningFetches} fetches took a budget, of {loop.Fetches}; {seam.Calls.Count(sender => sender == mallory.Did)} host calls for the sender; {refused} refused for its bound; {notStarted} out of time before their turn; {heldBack} held back; {harness.Channel.Unpacker.Reserved} reserved; bound for thirty {bound} learning fetches, {(budget * bound).TotalSeconds} s"));
        reached.Should().BeTrue();
        seam.Calls.Count(sender => sender == mallory.Did).Should().Be(fromMallory, "each of the sender's messages is opened once, then held back while its pause lasts");
        refused.Should().Be(0, "the sender's bound of sixteen is never reached");
        notStarted.Should().Be(0);
        loop.LearningFetches.Should().Be(learningFetches).And.BeLessThanOrEqualTo(bound);
        loop.Elapsed.Should().Be(TimeSpan.FromSeconds(seconds), "the measured time to the other sender's message").And.BeLessThanOrEqualTo(budget * bound);
        if (fetches > 0)
            loop.Fetches.Should().Be(fetches);
    }

    /// <summary>
    /// D-13: Sam is an honest sender whose DID takes <paramref name="lookupSeconds"/> to resolve.
    /// Sixteen messages of junk name Sam's key in their <c>skid</c> (Sam's own envelopes with their
    /// ciphertext changed: Sam's DID is resolved, then they fail to decrypt), then Sam's own message
    /// follows. Sam's message reaches the application within the bound of sixteen messages that
    /// never open, no fetch refuses any message for Sam's partition, and each piece of junk is opened
    /// once: at the short lease the junk comes back before the loop is past it, and is held back.
    /// </summary>
    [Theory]
    [InlineData(4, 2, 60)]
    [InlineData(4, 5, 60)]
    [InlineData(10, 2, 60)]
    [InlineData(10, 5, 60)]
    [InlineData(4, 2, 10)]
    [InlineData(4, 5, 10)]
    [InlineData(10, 2, 10)]
    [InlineData(10, 5, 10)]
    public async Task An_honest_slow_senders_message_behind_junk_naming_its_key_reaches_the_application_within_the_bound(int limit, int lookupSeconds, int leaseSeconds)
    {
        var (mediator, store) = await StartMediatorAsync(TimeSpan.FromSeconds(leaseSeconds));
        await using var _ = mediator;
        var time = new QaManualTimeProvider();
        var seam = new SlowSenderSeam(time);
        await using var harness = await SlowFetchLoop.HarnessAsync(mediator, time, seam);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var sam = await harness.NewAliceAsync();
        var mallory = await harness.NewAliceAsync();
        seam.LookupTakes(sam.Did, TimeSpan.FromSeconds(lookupSeconds));
        const int junk = 16;
        for (var i = 0; i < junk; i++)
            await PostJunkNamingAsync(mediator, mallory, sam, recipient);
        var real = await PickupE2E.SendAsync(mediator, sam, recipient, "from sam");
        var budget = harness.Channel.Options.UnpackTimeout;
        var bound = Bound(junk, limit);
        var loop = new SlowFetchLoop(mediator, harness, time, store, seam, bob.Did);

        // One more fetch than the junk needs may take time: the one that opens Sam's message, which
        // costs Sam's lookup.
        var reached = await loop.FetchUntilAsync(real.Id, limit, bound + 1);

        output.WriteLine($"limit {limit}, lookup {lookupSeconds} s, lease {leaseSeconds} s: {loop.LearningFetches} fetches that took time, of {loop.Fetches}, {loop.Elapsed.TotalSeconds} s; bound {bound} learning fetches, {(budget * bound).TotalSeconds} s");
        reached.Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(loop.Items.Single(i => i.Unpacked?.Message.Id == real.Id), sam, recipient, real);
        loop.LearningFetches.Should().BeLessThanOrEqualTo(bound + 1, "the junk is learned within {0} fetches, and the fetch that opens Sam's message costs its lookup", bound);
        loop.Elapsed.Should().BeLessThanOrEqualTo(budget * bound, "the junk is learned at least as fast as messages that never open");
        loop.Items.Should().NotContain(i => i.Error != null && i.Error.Message.Contains("unpack capacity exhausted", StringComparison.Ordinal), "no message, Sam's own included, is refused for Sam's partition");
        seam.Calls.Count(sender => sender == sam.Did).Should().Be(junk + 1, "each piece of junk is opened once, then held back while its pause lasts");
    }

    /// <summary>
    /// What "sixteen in one delivery takes another mediator" rests on: at its defaults this library's
    /// mediator answers a fetch with at most ten messages (<see cref="MessagePickupOptions.MaxDeliveryBatch"/>),
    /// whatever limit the client asks for. Thirty are queued; a fetch with a limit of 100 returns
    /// the first ten, and the next fetch the next ten.
    /// </summary>
    [Fact]
    public async Task At_its_defaults_this_librarys_mediator_delivers_at_most_ten_messages_per_fetch()
    {
        var (mediator, store) = await StartMediatorAsync(DefaultLease);
        await using var _ = mediator;
        var time = new QaManualTimeProvider();
        var seam = new SlowSenderSeam(time);
        await using var harness = await SlowFetchLoop.HarnessAsync(mediator, time, seam);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        for (var i = 0; i < 30; i++)
            await PickupE2E.SendAsync(mediator, alice, recipient, "note");
        var leasesBefore = store.Leases.Count;

        var first = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 100).WaitAsync(MediatorTestServer.Timeout);
        var second = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 100).WaitAsync(MediatorTestServer.Timeout);

        output.WriteLine($"30 queued, limit 100: the first fetch returned {first.Items.Count}, the second {second.Items.Count}; MaxDeliveryBatch {new MessagePickupOptions().MaxDeliveryBatch}");
        new MessagePickupOptions().MaxDeliveryBatch.Should().Be(10);
        first.Items.Should().HaveCount(10).And.OnlyContain(i => i.Unpacked != null);
        second.Items.Should().HaveCount(10).And.OnlyContain(i => i.Unpacked != null);
        store.Leases.Skip(leasesBefore).Should().OnlyContain(l => l.Ids.Count <= 10, "no delivery carries more than ten");
    }

    // The learning fetches the bound allows: ceil(slow / min(limit, 16)).
    private static int Bound(int slow, int limit)
    {
        var perFetch = Math.Min(limit, 16);
        return (slow + perFetch - 1) / perFetch;
    }

    private static async Task<(MediatorTestServer Mediator, LeaseRecordingStore Store)> StartMediatorAsync(TimeSpan lease)
    {
        LeaseRecordingStore? store = null;
        var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Mediator = m => m.Pickup.LeaseTimeout = lease;
            o.ConfigureServices = services => LeaseRecordingStore.Register(services, s => store = s);
        });
        // The store is built when the mediator first resolves it; ask for it now.
        _ = mediator.App.Services.GetRequiredService<IMessageStore>();
        return (mediator, store!);
    }

    // A note anoncrypted around its authcrypt (SendOptions.ProtectSender): the outer envelope names
    // no sender, so the client counts it in the mailbox's anonymous partition.
    private static async Task SendAnoncryptedAsync(MediatorTestServer mediator, MediatorSender sender, TestPeer recipient, string content)
    {
        var note = sender.Note(recipient.Did, content);
        (await sender.Client.SendAsync(note, new SendOptions(new[] { recipient.Did }, sender.Did, ProtectSender: true))).Transport.Accepted.Should().BeTrue();
        await mediator.FlushAsync();
    }

    // Sam's own authcrypted note to the recipient with its ciphertext changed, forwarded by Mallory:
    // the outer envelope names Sam's key in skid, Sam's DID is resolved to open it, and it fails to
    // decrypt.
    private static async Task PostJunkNamingAsync(MediatorTestServer mediator, MediatorSender mallory, MediatorSender sam, TestPeer recipient)
    {
        var envelope = JsonNode.Parse(await sam.AuthcryptAsync(sam.Note(recipient.Did, "junk"), recipient.Did))!.AsObject();
        var ciphertext = envelope["ciphertext"]!.GetValue<string>();
        envelope["ciphertext"] = (ciphertext[0] == 'A' ? "B" : "A") + ciphertext[1..];
        var (packed, _) = await mallory.ForwardAsync(mediator.Did, recipient.Did, envelope.ToJsonString());
        (await mediator.PostAsync(packed)).Status.Should().Be(System.Net.HttpStatusCode.Accepted);
        await mediator.FlushAsync();
    }
}
