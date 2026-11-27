using DidComm.Mediator.Client;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// What one run of <see cref="Qa1Loop"/> measured (#111).
/// </summary>
/// <param name="Found">Whether the honest message was returned opened before the cap.</param>
/// <param name="Elapsed">The clock time from the first fetch to the fetch that returned it (or to the cap).</param>
/// <param name="Opening">The part of it spent inside fetches: deliveries being opened.</param>
/// <param name="WaitingForLeases">The part of it the host waited, after a fetch that returned nothing, for the mediator's earliest lease to end.</param>
/// <param name="LeaseWaits">How many such waits there were: how often a fetch returned nothing.</param>
/// <param name="BudgetFetches">The fetches that lasted a whole budget.</param>
/// <param name="Fetches">The fetches made.</param>
/// <param name="HostCalls">The host unpack calls made.</param>
/// <param name="MostOpenings">The most host calls one message had.</param>
/// <param name="FirstReopeningAt">When, from the start, a message that had already had a host call got another; <c>null</c>: never.</param>
/// <param name="Refused">The items refused for capacity ("unpack capacity exhausted").</param>
/// <param name="BeforeItsTurn">The items that ran out of time before their turn came.</param>
/// <param name="MostReservedBeforeAFetch">The most reservations seen still held by host calls of earlier fetches when the next fetch was due.</param>
/// <param name="Batches">Every fetch's result, in order.</param>
internal sealed record Qa1Reach(
    bool Found, TimeSpan Elapsed, TimeSpan Opening, TimeSpan WaitingForLeases, int LeaseWaits, int BudgetFetches, int Fetches, int HostCalls,
    int MostOpenings, TimeSpan? FirstReopeningAt, int Refused, int BeforeItsTurn, int MostReservedBeforeAFetch, IReadOnlyList<PickupBatch> Batches);

/// <summary>
/// The eagerest host's fetch loop over a <see cref="Qa1Exchange"/> and a <see cref="Qa1Mediator"/>,
/// measured (#111). The loop is the one of <see cref="SlowSenderFetchQa1Tests.ReachAsync"/>, with one
/// thing stated and enforced that that one leaves to the thread pool.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The host's loop, as modelled.</strong> The host calls <c>FetchAsync(limit)</c> again at
/// once for as long as a fetch returns anything, failures included (the documented loop, "until
/// <see cref="PickupBatch.Drained"/>"). When a fetch returns nothing (every queued message is
/// leased), its next loop starts at the instant the mediator's earliest lease ends: the eagerest
/// host that does not spin. Nothing else moves the clock, so the time to the honest message is the
/// time inside fetches plus those waits.
/// </para>
/// <para>
/// <strong>The host's lookups, as modelled.</strong> A lookup that honours its token has ended
/// before the host's next fetch is answered: each fetch is driven through
/// <see cref="Qa1Exchange.DriveAsync{T}"/>, which waits until no such lookup holds a reservation
/// (its remarks state the model). The loop runs, and moves the clock, on a thread-pool thread of its
/// own, so nothing here depends on a test's synchronization context (L-078). Without that wait a
/// fetch can start while calls of the fetch before still hold reservations; with sixteen opened at
/// once against a bound of sixteen per partition, messages of the new delivery are then refused for
/// that bound, and the figures depend on the thread pool. What a delivery does when it does arrive
/// before such lookups have ended is measured on its own (<see cref="DeliveredUnpackNextDeliveryRt3Tests"/>).
/// </para>
/// </remarks>
internal static class Qa1Loop
{
    /// <summary>
    /// Run the loop until <paramref name="honest"/> is returned opened, or <paramref name="cap"/> of
    /// clock time has passed (a horizon, not a bound), or nothing is queued.
    /// </summary>
    /// <param name="exchange">The exchange: its clock, host and unpacker.</param>
    /// <param name="mediator">The mediator that leases what it delivers (or does not).</param>
    /// <param name="limit">The fetch limit.</param>
    /// <param name="cap">The horizon.</param>
    /// <param name="honest">The id of the message looked for.</param>
    /// <param name="waitForHostCallsToEnd">False only for a test of the wait itself: each fetch then starts at once.</param>
    public static Task<Qa1Reach> RunAsync(Qa1Exchange exchange, Qa1Mediator mediator, int limit, TimeSpan cap, string honest = "honest", bool waitForHostCallsToEnd = true)
        // On a thread-pool thread, so the clock is moved, and every budget ended, from a thread
        // without a test's synchronization context (L-078).
        => Task.Run(() => LoopAsync(exchange, mediator, limit, cap, honest, waitForHostCallsToEnd));

    private static async Task<Qa1Reach> LoopAsync(Qa1Exchange exchange, Qa1Mediator mediator, int limit, TimeSpan cap, string honest, bool waitForHostCallsToEnd)
    {
        using var floor = new PoolFloor(parked: 2);
        var client = exchange.Client();
        var start = exchange.Clock.TrueNow;
        var batches = new List<PickupBatch>();
        var opening = TimeSpan.Zero;
        var waiting = TimeSpan.Zero;
        var leaseWaits = 0;
        var budgetFetches = 0;
        var refused = 0;
        var beforeItsTurn = 0;
        var callsSeen = 0;
        var openings = new Dictionary<string, int>(StringComparer.Ordinal);
        TimeSpan? firstReopening = null;
        var found = false;
        while (exchange.Clock.TrueNow - start < cap)
        {
            var fetchStart = exchange.Clock.TrueNow;
            var batch = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit), unwaited: !waitForHostCallsToEnd).ConfigureAwait(false);
            var took = exchange.Clock.TrueNow - fetchStart;
            batches.Add(batch);
            opening += took;
            if (took == exchange.Unpacker.Budget)
                budgetFetches++;
            refused += batch.Items.Count(i => i.Error is not null && i.Error.Message.Contains("unpack capacity exhausted", StringComparison.Ordinal));
            beforeItsTurn += batch.Items.Count(i => i.Error is not null && i.Error.Message.Contains("before its turn came", StringComparison.Ordinal));
            // Each host call of this fetch started while the fetch ran; one for a message that had
            // already had one is a re-opening, counted from the fetch's start.
            var labels = exchange.Host.Calls.Labels;
            for (; callsSeen < labels.Count; callsSeen++)
            {
                var count = openings[labels[callsSeen]] = openings.GetValueOrDefault(labels[callsSeen]) + 1;
                if (count > 1)
                    firstReopening ??= fetchStart - start;
            }
            if (batch.Items.Any(i => i.Id == honest && i.Unpacked != null))
            {
                found = true;
                break;
            }
            if (batch.Drained)
            {
                if (mediator.NextLeaseEnd() is not { } next)
                    break; // nothing is queued
                var wait = next - exchange.Clock.TrueNow;
                exchange.Clock.Inner.Advance(wait);
                waiting += wait;
                leaseWaits++;
            }
        }
        var elapsed = exchange.Clock.TrueNow - start;
        var junk = openings.Where(o => o.Key != honest).Select(o => o.Value).DefaultIfEmpty(0).Max();
        return new Qa1Reach(found, elapsed, opening, waiting, leaseWaits, budgetFetches, batches.Count, callsSeen, junk, firstReopening, refused, beforeItsTurn, exchange.MostReservedBeforeACall, batches);
    }

    /// <summary>The measured figures of one run, as the rest of a printed line.</summary>
    public static string Describe(Qa1Reach reach)
        => FormattableString.Invariant($"reached {(reach.Found ? "yes" : "no")}, {reach.Elapsed.TotalSeconds} s = opening {reach.Opening.TotalSeconds} s + waiting for leases {reach.WaitingForLeases.TotalSeconds} s ({reach.LeaseWaits} waits) + other {(reach.Elapsed - reach.Opening - reach.WaitingForLeases).TotalSeconds} s; {reach.BudgetFetches} fetches took a budget, of {reach.Fetches}; {reach.HostCalls} host calls; most openings of one message {reach.MostOpenings}; first re-opening {(reach.FirstReopeningAt is { } t ? t.TotalSeconds + " s" : "none")}; second learning pass {(reach.MostOpenings > 1 ? "yes" : "no")}; refused {reach.Refused}; before its turn {reach.BeforeItsTurn}");
}
