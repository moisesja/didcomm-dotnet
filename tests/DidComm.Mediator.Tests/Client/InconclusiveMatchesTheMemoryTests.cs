using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// Invariant (a) of <see cref="PickupItem.Inconclusive"/> (#111, the coordinator's call), as the
/// equivalence itself, through <see cref="PickupClient.FetchAsync"/> and the real unpacker on the
/// manual clock: for every failed item of a fetch, the flag is true exactly when the memory took no
/// strike for the message in that delivery and the message was not held back, is a message this
/// client opens, and was not turned away by the fetch's filter. The strikes are read from the
/// memory (is the message held back at a lookup after the delivery), never from the flag. The
/// deliveries are random mixes of every kind of message, from a fixed seed; the clock moves only to
/// the next due timer, once every worker is parked. Real time is only a hang guard.
/// </summary>
public sealed class InconclusiveMatchesTheMemoryTests(ITestOutputHelper output)
{
    private const string AnotherOfTheHostsDids = "did:example:bob-two";
    private const string SenderAtItsBound = "did:example:full";
    private const int Deliveries = 200;
    private static readonly TimeSpan Lease = new MessagePickupOptions().LeaseTimeout; // 60 s

    // No sum of these reaches a boundary of the 20 s budget exactly (its end, the last instant a
    // message may start at 15 s, the start allowance at 2.5 s), so no outcome hangs on which of two
    // timers due at the same instant fires first.
    private static readonly TimeSpan[] Durations = { TimeSpan.Zero, TimeSpan.FromSeconds(1.3), TimeSpan.FromSeconds(4.1), TimeSpan.FromSeconds(9.7) };

    // What a message of a delivery is drawn from: the kinds that take a worker three times as often
    // as the others, so that deliveries also run out of workers and of budget.
    private static readonly What[] Mix =
    {
        What.Opens, What.Opens, What.Opens,
        What.FailsOnItsOwn, What.FailsOnItsOwn, What.FailsOnItsOwn,
        What.NeverOpens, What.NeverOpens, What.NeverOpens,
        What.OpensForAnotherOfTheHostsDids,
        What.NeverOpensAndIgnoresItsToken,
        What.HeldBack,
        What.FromASenderAtItsBound,
        What.NotEncrypted,
    };

    private enum What
    {
        Opens,
        OpensForAnotherOfTheHostsDids,
        FailsOnItsOwn,
        NeverOpens,
        NeverOpensAndIgnoresItsToken,
        HeldBack,
        FromASenderAtItsBound,
        NotEncrypted,
    }

    /// <summary>
    /// Two hundred fetches, each of one delivery of 1 to 60 messages: messages that open or fail on
    /// their own at once or after 1.3, 4.1 or 9.7 s, messages that never open (honouring their token
    /// or not), messages held back from before, messages of a sender whose bound is held, messages
    /// that open for another of the host's DIDs under the fetch's filter, and attachments that are
    /// not in encrypted form. For every failed item, the flag equals "not struck in this delivery,
    /// not held back, a message this client opens, and not filtered". A message out of time says
    /// "while it was being opened" exactly when host code was called for it.
    /// </summary>
    [Fact]
    public async Task For_every_failed_item_of_two_hundred_mixed_deliveries_the_flag_says_what_the_memory_says()
    {
        using var floor = new PoolFloor(parked: 2);
        var random = new Random(20261125);
        var seen = new SortedDictionary<string, int>();
        var endedByTheirBudget = 0;
        var items = 0;

        for (var delivery = 0; delivery < Deliveries; delivery++)
        {
            var count = random.Next(1, 61);
            var plan = new Dictionary<string, (What What, TimeSpan After)>();
            for (var i = 0; i < count; i++)
                plan[$"m-{i}"] = (Mix[random.Next(Mix.Length)], Durations[random.Next(Durations.Length)]);

            var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(
                label => plan[label] switch
                {
                    (What.Opens or What.OpensForAnotherOfTheHostsDids, var after) => Qa1Step.OpenAfter(after),
                    (What.FailsOnItsOwn, var after) => Qa1Step.FailAfter(after),
                    (What.NeverOpensAndIgnoresItsToken, _) => new Qa1Step(Qa1Kind.IgnoresToken),
                    _ => Qa1Step.Stall,
                },
                Lease,
                recipientOf: label => plan[label].What == What.OpensForAnotherOfTheHostsDids ? AnotherOfTheHostsDids : FakePickupExchange.L);
            var memory = exchange.Unpacker.Memory;
            var held = new List<UnpackReservations.Reservation>();
            for (var i = 0; i < UnpackReservations.MaxPerSender; i++)
            {
                exchange.Unpacker.Reservations.TryTake(Qa1.Bob, SenderAtItsBound, out var reservation, out _).Should().BeTrue();
                held.Add(reservation!);
            }
            foreach (var (id, (what, _)) in plan)
            {
                mediator.Enqueue(id, what switch
                {
                    What.NotEncrypted => $"{{\"id\":\"{id}\",\"type\":\"https://example.org/plain\"}}",
                    What.FromASenderAtItsBound => Qa1.From(SenderAtItsBound + "#key-x25519-1", id),
                    _ => Qa1.From($"did:example:sender-{id}#key-x25519-1", id),
                });
                if (what == What.HeldBack)
                    memory.Strike(Qa1.Bob, id);
            }
            var client = exchange.Client();
            var start = exchange.Clock.TrueNow;

            PickupBatch batch;
            try
            {
                batch = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 100, recipientDid: FakePickupExchange.L));
            }
            finally
            {
                exchange.Host.ReleaseIgnored();
                foreach (var reservation in held)
                    reservation.Release();
            }
            Qa1.LookupsEnded(exchange.Unpacker);
            if (exchange.Clock.TrueNow - start == Qa1.Budget)
                endedByTheirBudget++;

            batch.Items.Select(i => i.Id).Should().Equal(plan.Keys, "every message of the delivery is an item (delivery {0})", delivery);
            var called = exchange.Host.Calls.Labels.ToHashSet(StringComparer.Ordinal);
            foreach (var item in batch.Items)
            {
                items++;
                var what = plan[item.Id].What;
                if (item.Unpacked is not null)
                {
                    item.Inconclusive.Should().BeFalse();
                    Count("opened");
                    continue;
                }
                var filtered = item.Error is MediationProtocolException;
                var notAMessage = what == What.NotEncrypted;
                var heldBack = what == What.HeldBack;
                var struck = memory.HeldBack(Qa1.Bob, new[] { item.Id }).Single();

                item.Inconclusive.Should().Be(!struck && !heldBack && !notAMessage && !filtered,
                    "delivery {0}, {1} ({2} after {3}): {4}; held back at a lookup after the delivery: {5}", delivery, item.Id, what, plan[item.Id].After, item.Error!.Message, struck);
                var text = item.Error!.Message;
                if (item.Error is TimeoutException && text.Contains("ran out"))
                {
                    text.Contains("while it was being opened").Should().Be(called.Contains(item.Id), "delivery {0}, {1}: the text says whether host code was called for it", delivery, item.Id);
                    text.Contains("before its turn came").Should().Be(!called.Contains(item.Id));
                }
                Count(filtered ? "conclusive: turned away by the filter"
                    : notAMessage ? "conclusive: not in encrypted form"
                    : heldBack ? "conclusive: held back"
                    : text.Contains("capacity exhausted") ? "inconclusive: refused for capacity"
                    : text.Contains("before its turn came") ? "inconclusive: out of time before its turn"
                    : text.Contains("while it was being opened") ? (item.Inconclusive ? "inconclusive: started late and cut" : "conclusive: cut having had the whole budget")
                    : "conclusive: failed on its own");
            }
        }

        foreach (var (kind, count) in seen)
            output.WriteLine($"{count} items {kind}");
        output.WriteLine($"{items} items in {Deliveries} deliveries, {endedByTheirBudget} of them ended by their budget");
        seen.Keys.Should().BeEquivalentTo(new[]
        {
            "opened",
            "conclusive: turned away by the filter",
            "conclusive: not in encrypted form",
            "conclusive: held back",
            "conclusive: failed on its own",
            "conclusive: cut having had the whole budget",
            "inconclusive: refused for capacity",
            "inconclusive: out of time before its turn",
            "inconclusive: started late and cut",
        }, "the mixes produced every kind of item");
        endedByTheirBudget.Should().BeGreaterThan(Deliveries / 2);

        void Count(string kind) => seen[kind] = seen.GetValueOrDefault(kind) + 1;
    }
}
