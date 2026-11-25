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
    private readonly TaskCompletionSource _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, TimeSpan> _lookups = new(StringComparer.Ordinal);
    private readonly HashSet<string> _neverEnds = new(StringComparer.Ordinal);
    private long _next;

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

    /// <summary>The outer-envelope sender of every host call made, in order (<c>null</c>: anonymous).</summary>
    public IReadOnlyList<string?> Calls => _calls.ToArray();

    /// <summary>The unpack step a channel is built with.</summary>
    public Func<string, CancellationToken, Task<DeliveredUnpack>> For(DidCommClient client)
        => (text, ct) => UnpackAsync(client, text, ct);

    /// <summary>Let every lookup that never ends go on (the end of the test).</summary>
    public void End() => _end.TrySetResult();

    private async Task<DeliveredUnpack> UnpackAsync(DidCommClient client, string text, CancellationToken ct)
    {
        var sender = SenderPartition.Of(text);
        _calls.Enqueue(sender);
        if (sender is not null && _neverEnds.Contains(sender))
            await WaitAsync(_end.Task, ct); // ignores the token
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
/// four (the opening concurrency) wait, so no message starts at another time than it would at full
/// speed. After each fetch the mediator's clock moves by the client's elapsed time, and nothing else.
/// </summary>
internal sealed class SlowFetchLoop(MediatorTestServer mediator, MediatorClientHarness harness, QaManualTimeProvider time, LeaseRecordingStore store, SlowSenderSeam seam, string from)
{
    private static readonly TimeSpan HangGuard = MediatorTestServer.Timeout;

    /// <summary>The fetches made.</summary>
    public int Fetches { get; private set; }

    /// <summary>The fetches during which the client's clock moved (the others opened nothing or only what opens at once).</summary>
    public int LearningFetches { get; private set; }

    /// <summary>The client's time spent fetching so far; the mediator's clock moved by exactly this.</summary>
    public TimeSpan Elapsed { get; private set; }

    /// <summary>Every item the loop's fetches returned, in order.</summary>
    public List<PickupItem> Items { get; } = new();

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

    /// <summary>Fetch until an item opens whose message id is <paramref name="messageId"/>, or the loop has spent more than <paramref name="learningBound"/> learning fetches, or <paramref name="maxFetches"/> fetches.</summary>
    /// <returns>Whether the message was reached.</returns>
    public async Task<bool> FetchUntilAsync(string messageId, int limit, int learningBound, int maxFetches = 1000)
    {
        while (LearningFetches <= learningBound && Fetches < maxFetches)
        {
            var batch = await FetchOnceAsync(limit);
            if (batch.Items.Any(i => i.Unpacked?.Message.Id == messageId))
                return true;
        }
        return false;
    }

    /// <summary>One fetch, driven to its end.</summary>
    public async Task<PickupBatch> FetchOnceAsync(int limit)
    {
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
        Elapsed += elapsed;
        mediator.Clock.Advance(elapsed);
        Items.AddRange(batch.Items);
        return batch;
    }
}
