using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// The host unpack call of the #111 acceptance tests, around the real unpack. Before it, a message
/// whose outer envelope names a sender (authcrypt <c>skid</c>) may wait for that sender's lookup: a
/// number of seconds on the client's clock, honouring the token (<see cref="LookupTakes"/>), or until
/// the test ends, ignoring the token (<see cref="LookupNeverEnds"/>). After it, a message whose
/// content is "slow" waits until its token is cancelled: a sender whose DID never resolves. Each wait
/// is registered with its token, so a test can tell when every call of a delivery is waiting on a
/// timer (<see cref="Live"/>). Nothing here sleeps.
/// </summary>
internal sealed class SlowSenderSeam
{
    private readonly ConcurrentDictionary<long, CancellationToken> _waiting = new();
    private readonly ConcurrentQueue<string?> _calls = new();
    private readonly ConcurrentQueue<(string? Sender, string Packed, DateTimeOffset At)> _openings = new();
    private readonly TaskCompletionSource _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, TimeSpan> _lookups = new(StringComparer.Ordinal);
    private readonly HashSet<string> _neverEnds = new(StringComparer.Ordinal);
    private long _next;
    private int _held;

    public SlowSenderSeam(TimeProvider time) => Time = time;

    /// <summary>The client's clock, on which a lookup's time passes.</summary>
    public TimeProvider Time { get; }

    /// <summary>The lookup of <paramref name="senderDid"/> takes <paramref name="time"/> on the client's clock and honours its token.</summary>
    public void LookupTakes(string senderDid, TimeSpan time) => _lookups[senderDid] = time;

    /// <summary>The lookup of <paramref name="senderDid"/> never ends while the test runs, and ignores its token.</summary>
    public void LookupNeverEnds(string senderDid) => _neverEnds.Add(senderDid);

    /// <summary>
    /// How many calls wait now with their token not cancelled: for a lookup that takes its time, or
    /// for a message that never opens. A call that is running (not waiting) is not counted, nor is one
    /// whose budget has already ended.
    /// </summary>
    public int Live => _waiting.Values.Count(t => !t.IsCancellationRequested);

    /// <summary>
    /// How many host calls are inside a lookup that never ends (<see cref="LookupNeverEnds"/>): each
    /// keeps its reservation until <see cref="End"/>, whatever its token does.
    /// </summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>The outer-envelope sender of every host call made, in order (<c>null</c>: anonymous).</summary>
    public IReadOnlyList<string?> Calls => _calls.ToArray();

    /// <summary>
    /// Every host call made, in order: the outer-envelope sender (<c>null</c>: anonymous), the packed
    /// text it was handed, and the client's clock when its code started. A packed text that is there
    /// twice was opened twice.
    /// </summary>
    public IReadOnlyList<(string? Sender, string Packed, DateTimeOffset At)> Openings => _openings.ToArray();

    /// <summary>The unpack step a channel is built with.</summary>
    public Func<string, CancellationToken, Task<DeliveredUnpack>> For(DidCommClient client)
        => (text, ct) => UnpackAsync(client, text, ct);

    /// <summary>Let every lookup that never ends go on (the end of the test).</summary>
    public void End() => _end.TrySetResult();

    private async Task<DeliveredUnpack> UnpackAsync(DidCommClient client, string text, CancellationToken ct)
    {
        var sender = SenderPartition.Of(text);
        _calls.Enqueue(sender);
        _openings.Enqueue((sender, text, Time.GetUtcNow()));
        if (sender is not null && _neverEnds.Contains(sender))
        {
            Interlocked.Increment(ref _held);
            try
            {
                await WaitAsync(_end.Task, ct); // ignores the token
            }
            finally
            {
                Interlocked.Decrement(ref _held);
            }
        }
        else if (sender is not null && _lookups.TryGetValue(sender, out var lookup))
            await WaitAsync(Task.Delay(lookup, Time, ct), ct); // the timer exists before the call counts as waiting
        var unpacked = await client.UnpackDeliveredAsync(text, ct);
        if (unpacked.Result.Message.Body?["content"]?.GetValue<string>() == "slow")
            await WaitAsync(Task.Delay(Timeout.InfiniteTimeSpan, ct), ct);
        return unpacked;
    }

    private async Task WaitAsync(Task wait, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _next);
        _waiting[id] = ct;
        try
        {
            await wait;
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }
    }
}

/// <summary>
/// A fetch-until-drained loop against this library's mediator, with the client's clock moved by the
/// test (#111 acceptance). Each fetch is driven one timer at a time: the clock moves to the next timer
/// due only once every message of the delivery not held back has an outcome or waits on a timer, or
/// sixteen (the opening concurrency) wait, so no message starts at another time than it would at
/// full speed. After each fetch the mediator's clock moves by the client's elapsed time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The host's loop, as modelled</strong> (<see cref="RunAsync"/>), which is the whole of it:
/// </para>
/// <list type="number">
/// <item><description>The host calls <c>FetchAsync(limit)</c> again at once for as long as a fetch returns anything, failures included: the documented loop, "until <see cref="PickupBatch.Drained"/>".</description></item>
/// <item><description>When a fetch returns nothing (every queued message is leased), the host's next loop starts at the instant the mediator's earliest lease ends: the eagerest host that does not spin. Both clocks move by that wait. A real host adds its own polling interval at each such wait; <see cref="LeaseWaits"/> counts them.</description></item>
/// <item><description>Nothing else moves a clock: the time the loop took is <see cref="Opening"/> (inside fetches) plus <see cref="WaitingForLeases"/> (step 2).</description></item>
/// </list>
/// <para>
/// <strong>The host's lookups, as modelled:</strong> a lookup that honours its token has ended
/// before the host's next fetch is answered. A host call keeps its reservation until it ends, and
/// one whose token was cancelled ends a moment after its delivery's result is set (where its
/// continuation runs is the scheduler's choice). So before each fetch the loop waits until the only
/// reservations held are those of lookups that never end (<see cref="SlowSenderSeam.Held"/>): the
/// event "every other host call has ended", not a neighbour of it. Without that wait a fetch could
/// start while calls of the fetch before still hold reservations, and with sixteen opened at once
/// against a bound of sixteen per partition, messages of the new delivery would then be refused for
/// that bound: the figures would depend on the thread pool. What a delivery does when it does
/// arrive before such lookups have ended is measured on its own
/// (<c>DeliveredUnpackNextDeliveryRt3Tests</c> in <c>DidComm.Mediator.Tests</c>).
/// </para>
/// <para>
/// Without step 2 a loop whose fetch returns nothing would fetch again at the same instant for ever
/// (no clock moves when a fetch opens nothing), and "not reached" would be a property of the
/// harness, not of the client. The lease ends are kept here from what the store handed out
/// (<see cref="LeaseRecordingStore.Leases"/>) and the mediator's clock; the fetch after a wait must
/// return something, or the loop throws.
/// </para>
/// </remarks>
internal sealed class SlowFetchLoop(MediatorTestServer mediator, MediatorClientHarness harness, QaManualTimeProvider time, LeaseRecordingStore store, SlowSenderSeam seam, string from)
{
    private static readonly TimeSpan HangGuard = MediatorTestServer.Timeout;
    private readonly Dictionary<string, DateTimeOffset> _leasedUntil = new(StringComparer.Ordinal);
    private readonly TimeSpan _lease = mediator.App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MessagePickupOptions>>().Value.LeaseTimeout;

    /// <summary>The fetches made.</summary>
    public int Fetches { get; private set; }

    /// <summary>The fetches during which the client's clock moved (the others opened nothing or only what opens at once).</summary>
    public int LearningFetches { get; private set; }

    /// <summary>The fetches that lasted a whole budget (<see cref="MediatorClientOptions.UnpackTimeout"/>).</summary>
    public int BudgetFetches { get; private set; }

    /// <summary>The client's time spent inside fetches so far: deliveries being opened.</summary>
    public TimeSpan Opening { get; private set; }

    /// <summary>The time the host waited so far, after a fetch that returned nothing, for the mediator's earliest lease to end.</summary>
    public TimeSpan WaitingForLeases { get; private set; }

    /// <summary>How many times the host waited for a lease to end.</summary>
    public int LeaseWaits { get; private set; }

    /// <summary>The loop's time so far, on both clocks: <see cref="Opening"/> plus <see cref="WaitingForLeases"/>.</summary>
    public TimeSpan Elapsed => Opening + WaitingForLeases;

    /// <summary>Every item the loop's fetches returned, in order.</summary>
    public List<PickupItem> Items { get; } = new();

    /// <summary>Each fetch made, in order: how long it lasted on the client's clock, and what it returned.</summary>
    public List<(TimeSpan Elapsed, IReadOnlyList<PickupItem> Items)> Log { get; } = new();

    private DeliveredMessageUnpacker Unpacker => harness.Channel.Unpacker;

    /// <summary>Build a channel whose unpack step is <paramref name="seam"/>'s, on <paramref name="time"/>.</summary>
    public static Task<MediatorClientHarness> HarnessAsync(MediatorTestServer mediator, QaManualTimeProvider time, SlowSenderSeam seam)
        => MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.Time = time;
            o.ConfigureServices = services => services.AddSingleton(sp =>
            {
                var client = sp.GetRequiredService<DidCommClient>();
                return new MediatorClientChannel(client, new MediatorClientOptions { MessagePickupVersion = MessagePickupVersion.V3 }, receivesInbound: false, time, sp.GetService<ILoggerFactory>(),
                    send: null, resolveEndpoint: null, unpack: seam.For(client));
            });
        });

    /// <summary>
    /// Run the host's loop (see the remarks) until an item opens whose message id is
    /// <paramref name="messageId"/>, or <paramref name="horizon"/> of clock time has passed (a
    /// horizon, not a bound: "not reached" means not reached within it), or nothing is queued.
    /// </summary>
    /// <returns>Whether the message was reached.</returns>
    public Task<bool> RunAsync(string messageId, int limit, TimeSpan horizon)
        => RunAsync(messageId, limit, horizon, learningBound: int.MaxValue);

    /// <summary>
    /// The same loop, which also stops once it has spent more than <paramref name="learningBound"/>
    /// learning fetches: for a test whose property is that bound.
    /// </summary>
    /// <returns>Whether the message was reached.</returns>
    public Task<bool> FetchUntilAsync(string messageId, int limit, int learningBound)
        => RunAsync(messageId, limit, horizon: TimeSpan.FromHours(6), learningBound);

    private async Task<bool> RunAsync(string messageId, int limit, TimeSpan horizon, int learningBound)
    {
        var waited = false;
        while (LearningFetches <= learningBound && Elapsed < horizon)
        {
            var batch = await FetchOnceAsync(limit);
            if (batch.Items.Any(i => i.Unpacked?.Message.Id == messageId))
                return true;
            if (!batch.Drained)
            {
                waited = false;
                continue;
            }
            if (waited)
                throw new InvalidOperationException("The fetch after a lease's end returned nothing: the loop's record of the mediator's leases is wrong.");
            var now = mediator.Clock.Now;
            var leased = _leasedUntil.Values.Where(until => until > now).ToArray();
            if (leased.Length == 0)
                return false; // nothing is queued
            var wait = leased.Min() - now;
            time.Advance(wait);
            mediator.Clock.Advance(wait);
            WaitingForLeases += wait;
            LeaseWaits++;
            waited = true;
        }
        return false;
    }

    /// <summary>One fetch, driven to its end.</summary>
    public async Task<PickupBatch> FetchOnceAsync(int limit)
    {
        // The model's lookups (see the remarks): every host call of the fetches before has ended,
        // except the lookups that never end.
        SpinWait.SpinUntil(() => Unpacker.Reserved == seam.Held, HangGuard).Should().BeTrue("every host call of the fetches before ends, but for the {0} that never do ({1} reserved)", seam.Held, Unpacker.Reserved);
        var start = time.GetUtcNow();
        var leases = store.Leases.Count;
        var settled = Unpacker.Settled;
        var scope = new DeliveryScope(mediator.Did, from);
        int? opened = null;
        var fetch = harness.Pickup.FetchAsync(mediator.Did, from, limit);

        bool Parked()
        {
            if (opened is null)
            {
                var recorded = store.Leases;
                if (recorded.Count == leases)
                    return false; // not delivered yet
                var ids = recorded[leases].Ids;
                opened = ids.Count - Unpacker.Memory.HeldBack(scope, ids).Count(held => held);
            }
            var live = seam.Live;
            return live == DeliveredMessageUnpacker.FetchConcurrency || Unpacker.Settled - settled + live == opened;
        }

        while (true)
        {
            SpinWait.SpinUntil(() => fetch.IsCompleted || Parked(), HangGuard).Should().BeTrue("the fetch ends, or the delivery waits on its timers");
            if (fetch.IsCompleted)
                break;
            // Only a timer of the delivery's opening is moved to: they are all due within one budget
            // of the fetch's start, and a request deadline (RequestTimeout) is due later.
            if (time.NextDueAt is not { } due || due > start + Unpacker.Budget)
                break;
            time.Advance(due - time.GetUtcNow());
        }
        var batch = await fetch.WaitAsync(HangGuard);
        var elapsed = time.GetUtcNow() - start;
        Fetches++;
        if (elapsed > TimeSpan.Zero)
            LearningFetches++;
        if (elapsed == Unpacker.Budget)
            BudgetFetches++;
        Opening += elapsed;
        // What this fetch was handed is leased from the instant the mediator answered it (its clock
        // has not moved since); what opened was acknowledged, and is gone.
        var leasedAt = mediator.Clock.Now;
        foreach (var record in store.Leases.Skip(leases))
        {
            foreach (var id in record.Ids)
                _leasedUntil[id] = leasedAt + _lease;
        }
        foreach (var item in batch.Items)
        {
            if (item.Unpacked is not null)
                _leasedUntil.Remove(item.Id);
        }
        mediator.Clock.Advance(elapsed);
        Items.AddRange(batch.Items);
        Log.Add((elapsed, batch.Items));
        return batch;
    }
}
