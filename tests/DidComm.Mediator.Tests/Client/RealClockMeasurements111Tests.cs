using System.Diagnostics;
using System.Text;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The switch for #111's measurements on the real clock: they run only when the environment
/// variable <c>DIDCOMM_111_REALCLOCK</c> is <c>1</c>, and are skipped otherwise (they wait on real
/// time, change the process's thread-pool minimum while they run, and their figures mean something
/// only in a process of their own). <c>DIDCOMM_111_REALCLOCK_REPS</c> sets how many repetitions a
/// row counts (10 when not set; the recorded tables used 200).
/// </summary>
internal static class RealClock111
{
    /// <summary>The environment variable that turns the measurements on.</summary>
    public const string Variable = "DIDCOMM_111_REALCLOCK";

    /// <summary>The environment variable that sets the repetitions of a row.</summary>
    public const string RepetitionsVariable = "DIDCOMM_111_REALCLOCK_REPS";

    /// <summary>Why a measurement is skipped, or <c>null</c> when the switch is on.</summary>
    public static string? SkipReason => Environment.GetEnvironmentVariable(Variable) == "1"
        ? null
        : "A measurement on the real clock and real threads, in a process of its own: set " + Variable + "=1 to run it.";

    /// <summary>A label for the printed lines (which head, which repetition): <c>DIDCOMM_111_REALCLOCK_LABEL</c>, or "-".</summary>
    public static string Label => Environment.GetEnvironmentVariable("DIDCOMM_111_REALCLOCK_LABEL") ?? "-";

    /// <summary>How many repetitions a row counts.</summary>
    public static int Repetitions => int.TryParse(Environment.GetEnvironmentVariable(RepetitionsVariable), out var reps) && reps > 0 ? reps : 10;
}

/// <summary>A <see cref="FactAttribute"/> that runs only when <c>DIDCOMM_111_REALCLOCK=1</c>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RealClock111FactAttribute : FactAttribute
{
    public RealClock111FactAttribute() => Skip = RealClock111.SkipReason;
}

/// <summary>A <see cref="TheoryAttribute"/> that runs only when <c>DIDCOMM_111_REALCLOCK=1</c>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RealClock111TheoryAttribute : TheoryAttribute
{
    public RealClock111TheoryAttribute() => Skip = RealClock111.SkipReason;
}

/// <summary>The real-clock measurements never run beside another test class of this assembly.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealClock111Collection
{
    public const string Name = "#111: measurements on the real clock";
}

/// <summary>
/// An exchange for the real-clock measurements: its wire is a <see cref="FakePickupExchange"/>, and
/// its deliveries are opened by a real unpacker on <see cref="TimeProvider.System"/>. Nothing here
/// waits between deliveries.
/// </summary>
internal sealed class RealClockExchange : IMediatorExchange
{
    public RealClockExchange(TimeSpan budget, Func<string, CancellationToken, Task<DeliveredUnpack>> host)
    {
        Wire = new FakePickupExchange { Route = FakePickupExchange.WebSocketRoute };
        Unpacker = new DeliveredMessageUnpacker(host, budget, TimeProvider.System);
        Duplicates = new DuplicateFilter(Wire.Options.DedupeCapacity);
    }

    public FakePickupExchange Wire { get; }
    public DeliveredMessageUnpacker Unpacker { get; }
    public MediatorClientOptions Options => Wire.Options;
    public TimeProvider Time => TimeProvider.System;
    public DuplicateFilter Duplicates { get; }
    public NegotiatedVersionCache<CoordinateMediationVersion> Versions => Wire.Versions;
    public NegotiatedVersionCache<MessagePickupVersion> PickupVersions => Wire.PickupVersions;
    public Task<MediatorRoute> ResolveRouteAsync(string mediatorDid, Uri? serviceEndpointOverride, bool requireWebSocket, CancellationToken ct) => Wire.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket, ct);
    public Task<InboundMessageSnapshot> ExchangeAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, CancellationToken ct) => Wire.ExchangeAsync(route, request, spec, mediatorDid, localDid, ct);
    public Task SendOneWayAsync(MediatorRoute route, Message request, string name, string mediatorDid, string localDid, CancellationToken ct) => Wire.SendOneWayAsync(route, request, name, mediatorDid, localDid, ct);
    public LiveRegistration RegisterLiveStream(string mediatorDid, string localDid) => Wire.RegisterLiveStream(mediatorDid, localDid);
    public Task<IReadOnlyList<DeliveredOutcome>> UnpackDeliveredBatchAsync(DeliveryScope scope, IReadOnlyList<DeliveredInput> messages, string? requestId, CancellationToken ct) => Unpacker.UnpackAllAsync(scope, messages, requestId, ct);
}

/// <summary>
/// #111, measurement A: blocking lookups under a small thread pool, on the real clock. Opt-in
/// (<see cref="RealClock111"/>); it asserts nothing, it prints one line per repetition (prefix
/// <c>RCA |</c>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it measures.</strong> Sixteen messages that never open are delivered in one
/// delivery, under the default budget (20 s; the start allowance is then 2.5 s). Each lookup blocks
/// its pool thread until its token is cancelled (a host resolver that blocks its thread and waits on the token). The
/// pool's minimum worker threads is <c>min</c>. Per delivery of the sixteen: how many pool threads
/// existed when it started, for how many messages host code started, how many of those within the
/// start allowance, when the last one started, and how many are remembered afterwards; and at which
/// delivery all sixteen are held back. After each delivery an honest message is delivered alone.
/// The sixteen are delivered again at once, or after <c>idleSeconds</c> of idling.
/// </para>
/// <para>
/// <strong>How to run it.</strong> One row per process, since the pool is the process's own:
/// <c>DIDCOMM_111_REALCLOCK=1 dotnet test … --filter "FullyQualifiedName~RealClockBlockingLookups111Tests&amp;DisplayName~min: 2, idleSeconds: 0"</c>.
/// The minimum is set with <see cref="ThreadPool.SetMinThreads"/> and put back when the row ends;
/// to have it in force from the pool's first thread, also start the process with
/// <c>DOTNET_ThreadPool_ForceMinWorkerThreads</c> (the line says which was the case).
/// </para>
/// <para>
/// <strong>Where the recorded tables were measured.</strong> An Apple M3 Max (14 cores), macOS
/// 26.6, .NET 10.0.15, load averages between 1.8 and 9.2 with other test hosts running, 4 to 10
/// repetitions per row, each in a process of its own.
/// </para>
/// </remarks>
[Collection(RealClock111Collection.Name)]
public sealed class RealClockBlockingLookups111Tests(ITestOutputHelper output)
{
    private static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);

    /// <summary>One repetition of one row (see the class remarks). Prints; asserts nothing.</summary>
    [RealClock111Theory]
    [InlineData(2, 0)]
    [InlineData(4, 0)]
    [InlineData(8, 0)]
    [InlineData(2, 40)]
    [InlineData(4, 40)]
    [InlineData(8, 40)]
    public async Task Measurement_A_blocking_lookups_under_a_small_pool_how_many_start_within_the_allowance_and_when_all_sixteen_are_held_back(int min, int idleSeconds)
    {
        var label = RealClock111.Label;
        var budget = new MediatorClientOptions().UnpackTimeout;
        var allowance = TimeSpan.FromTicks(Math.Max(1, budget.Ticks / 4) / 2);
        ThreadPool.GetMinThreads(out var workers0, out var io0);
        var applied = ThreadPool.SetMinThreads(min, io0);
        try
        {
            // Let the pool come down to its floor (idle workers retire); bounded, on this thread (not a pool thread).
            var settle = Stopwatch.StartNew();
            var settleFor = TimeSpan.FromSeconds(Environment.GetEnvironmentVariable("DOTNET_ThreadPool_ForceMinWorkerThreads") is null ? 30 : 5);
            while (ThreadPool.ThreadCount > min && settle.Elapsed < settleFor)
                Thread.Sleep(250);
            var starts = new List<TimeSpan>();
            var sw = new Stopwatch();
            var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
            {
                var label = TestJwe.LabelOf(packed);
                if (label == "h")
                    return Task.FromResult(UnpackFakes.Opened("h"));
                lock (starts)
                    starts.Add(sw.Elapsed);
                ct.WaitHandle.WaitOne(); // the host's lookup blocks its thread until its token is cancelled
                throw new OperationCanceledException(ct);
            }, budget, TimeProvider.System);
            var sixteen = Enumerable.Range(0, 16).Select(i => new DeliveredInput($"n-{i}", TestJwe.Bytes(TestJwe.Anoncrypt($"n-{i}")))).ToArray();
            var ids = sixteen.Select(m => m.Id).ToArray();
            var honest = new[] { new DeliveredInput("h", TestJwe.Bytes(TestJwe.Authcrypt("did:example:alice#key-x25519-1", "h"))) };
            int? allHeldBackAt = null;
            var perDelivery = new List<string>();
            for (var k = 1; k <= 8; k++)
            {
                var heldBefore = unpacker.Memory.HeldBack(Bob, ids).Count(h => h);
                if (heldBefore == 16)
                {
                    allHeldBackAt = k;
                    break;
                }
                lock (starts)
                    starts.Clear();
                var threads = ThreadPool.ThreadCount;
                sw.Restart();
                var outcomes = await unpacker.UnpackAllAsync(Bob, sixteen, "r" + k, default);
                var took = sw.Elapsed;
                TimeSpan[] s;
                lock (starts)
                    s = starts.ToArray();
                var remembered = unpacker.Memory.HeldBack(Bob, ids).Count(h => h);
                var refused = outcomes.Count(o => o.Error is not null && o.Error.Message.Contains("unpack capacity exhausted"));
                var beforeTurn = outcomes.Count(o => o.Error is not null && o.Error.Message.Contains("before its turn came"));
                // The host calls of this delivery have ended before the next (this measurement is not about reservations).
                SpinWait.SpinUntil(() => unpacker.Reserved == 0, TimeSpan.FromSeconds(30));
                var h = Stopwatch.StartNew();
                var opened = (await unpacker.UnpackAllAsync(Bob, honest, "h" + k, default))[0].Succeeded;
                perDelivery.Add(FormattableString.Invariant($"delivery {k}: pool threads at start {threads}, held back at start {heldBefore}, host code started for {s.Length} (within the allowance of {allowance.TotalSeconds} s: {s.Count(x => x <= allowance)}; latest start {(s.Length == 0 ? 0 : s.Max().TotalSeconds):0.00} s), refused {refused}, before its turn {beforeTurn}, remembered after it {remembered}, took {took.TotalSeconds:0.0} s, then the honest one {(opened ? "opened" : "NOT opened")} in {h.Elapsed.TotalMilliseconds:0} ms"));
                if (idleSeconds > 0)
                    Thread.Sleep(TimeSpan.FromSeconds(idleSeconds));
            }
            output.WriteLine(FormattableString.Invariant($"RCA | {label} | min {min} (applied {applied}, was {workers0}; pool started with {(Environment.GetEnvironmentVariable("DOTNET_ThreadPool_ForceMinWorkerThreads") is { } forced ? "its minimum forced to 0x" + forced : "the default minimum")}) | idle between deliveries {idleSeconds} s | budget {budget.TotalSeconds} s | all sixteen held back at delivery {(allHeldBackAt?.ToString() ?? "none of 8")} | {string.Join(" | ", perDelivery)}"));
        }
        finally
        {
            ThreadPool.SetMinThreads(workers0, io0);
        }
    }
}

/// <summary>
/// #111, measurement B: a delivery that is available the moment the one before it ends, on the real
/// clock, with nothing waiting in between. Opt-in (<see cref="RealClock111"/>); it asserts nothing,
/// it prints one line per row (prefix <c>RCB |</c>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it measures.</strong> Delivery 1 holds messages of one partition that never open;
/// delivery 2 holds more of the same partition and, last, one honest message of that partition.
/// Fetch loop: sixteen, then sixteen and one, two fetches in a row over an in-memory wire. Live
/// stream: a drain round of ten, then a push of nine and one that is in the mailbox before the
/// round is handed over (a stream's delivery has at most ten). The lookup is
/// <c>await Task.Delay(Timeout.Infinite, token)</c> and nothing else, or ends 1, 10 or 100 ms after
/// its cancellation. Over the repetitions of a row: how many messages of delivery 2 are refused
/// for capacity (minimum, median, maximum, repetitions with any), how many start, and what the
/// honest message got. The budget is 100 ms, so a repetition takes about a fifth of a second.
/// </para>
/// <para>
/// <strong>How to run it.</strong> In a process of its own:
/// <c>DIDCOMM_111_REALCLOCK=1 DIDCOMM_111_REALCLOCK_REPS=200 dotnet test … --filter "FullyQualifiedName~RealClockNextDelivery111Tests"</c>.
/// </para>
/// <para>
/// <strong>Where the recorded table was measured.</strong> An Apple M3 Max (14 cores), macOS 26.6,
/// .NET 10.0.15, load averages between 1.8 and 4.3, 200 repetitions per row after five that are
/// not counted, two test hosts at a time.
/// </para>
/// </remarks>
[Collection(RealClock111Collection.Name)]
public sealed class RealClockNextDelivery111Tests(ITestOutputHelper output)
{
    private const string SamKid = "did:example:sam#key-x25519-1";
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);
    private static readonly int Reps = RealClock111.Repetitions;

    public enum Way { FetchLoop, LiveStream }

    /// <summary>
    /// Where <c>await Task.Delay(Timeout.Infinite, token)</c> resumes when its token is cancelled
    /// from a pool thread with no synchronization context: inside <c>Cancel()</c> on the cancelling
    /// thread, or elsewhere. Sixteen waits per repetition. Prints; asserts nothing.
    /// </summary>
    [RealClock111Fact]
    public async Task Measurement_where_a_wait_cancelled_through_its_token_resumes()
    {
        var inside = 0; var elsewhere = 0; var reps = RealClock111.Repetitions;
        for (var rep = 0; rep < reps; rep++)
        {
            using var cts = new CancellationTokenSource();
            var canceller = 0;
            var resumed = new System.Collections.Concurrent.ConcurrentBag<(int Thread, bool WhileCancelling)>();
            var cancelling = 0;
            async Task Lookup()
            {
                try { await Task.Delay(Timeout.Infinite, cts.Token); }
                catch (OperationCanceledException) { resumed.Add((Environment.CurrentManagedThreadId, Volatile.Read(ref cancelling) == 1)); }
            }
            var lookups = new List<Task>();
            for (var i = 0; i < 16; i++)
                lookups.Add(Task.Run(Lookup));
            await Task.Delay(20);
            await Task.Run(() =>
            {
                canceller = Environment.CurrentManagedThreadId;
                Volatile.Write(ref cancelling, 1);
                cts.Cancel();
                Volatile.Write(ref cancelling, 0);
            });
            await Task.WhenAll(lookups);
            inside += resumed.Count(r => r.Thread == canceller && r.WhileCancelling);
            elsewhere += resumed.Count(r => !(r.Thread == canceller && r.WhileCancelling));
        }
        output.WriteLine($"RCP | cancelled Task.Delay(Infinite, token), 16 per repetition, {reps} repetitions, cancelled from a pool thread with no synchronization context: resumed on the cancelling thread inside Cancel() {inside}, elsewhere {elsewhere}; runtime {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
    }

    public static TheoryData<Way, bool, int, bool> Rows()
    {
        var data = new TheoryData<Way, bool, int, bool>();
        foreach (var way in new[] { Way.FetchLoop, Way.LiveStream })
            foreach (var named in new[] { false, true })
                foreach (var endMs in new[] { -1, 1, 10, 100 })
                    foreach (var pool in new[] { false, true })
                        data.Add(way, named, endMs, pool);
        return data;
    }

    /// <summary>
    /// One row (see the class remarks). <paramref name="endMs"/> −1: the lookup is the wait on its
    /// token and nothing else; otherwise it ends that many milliseconds after its cancellation.
    /// <paramref name="fromPoolThread"/>: each repetition runs inside <c>Task.Run</c> (no
    /// synchronization context), or is awaited on the test's thread. Prints; asserts nothing.
    /// </summary>
    [RealClock111Theory]
    [MemberData(nameof(Rows))]
    public async Task Measurement_B_a_delivery_available_the_moment_the_one_before_ends_how_many_of_its_messages_are_refused(Way way, bool named, int endMs, bool fromPoolThread)
    {
        var label = RealClock111.Label;
        var refused = new List<int>();
        var started = new List<int>();
        var withAny = new List<int>();
        var resumedOnSeveral = 0;
        var refusedAndSeveral = 0;
        var resumedUnderAContext = 0;
        var refusedInFirst = 0;
        var notStartedInFirst = 0;
        var honestRefused = 0;
        var honestOpened = 0;
        var honestBeforeItsTurn = 0;
        var honestOther = 0;
        var context = SynchronizationContext.Current?.GetType().Name ?? "none";
        // Five repetitions that are not counted come first (the first calls compile the code they run).
        for (var rep = -5; rep < Reps; rep++)
        {
            var one = fromPoolThread ? await Task.Run(() => OnceAsync(way, named, endMs)) : await OnceAsync(way, named, endMs);
            if (rep < 0)
                continue;
            refused.Add(one.Refused2);
            started.Add(one.Started2);
            if (one.Refused2 > 0)
                withAny.Add(rep);
            if (one.ResumedOnThreads > 1)
            {
                resumedOnSeveral++;
                if (one.Refused2 > 0)
                    refusedAndSeveral++;
            }
            resumedUnderAContext += one.ResumedUnderAContext;
            refusedInFirst += one.Refused1;
            notStartedInFirst += one.NotStarted1;
            if (one.Honest == "refused") honestRefused++;
            else if (one.Honest == "opened") honestOpened++;
            else if (one.Honest == "before its turn") honestBeforeItsTurn++;
            else honestOther++;
        }
        refused.Sort();
        started.Sort();
        output.WriteLine(FormattableString.Invariant($"RCB | {label} | {way} | {(named ? "one named sender" : "no named sender")} | lookup ends {(endMs < 0 ? "at once (Task.Delay(Infinite, token))" : endMs + " ms after its cancellation")} | driver {(fromPoolThread ? "on a pool thread (no synchronization context)" : "on the test's thread (" + context + ")")} | budget {Budget.TotalMilliseconds} ms | {Reps} repetitions | delivery 2: refused for capacity min {refused[0]}, median {refused[Reps / 2]}, max {refused[^1]}, repetitions with any {withAny.Count}{(withAny.Count is > 0 and <= 12 ? " (repetitions " + string.Join(",", withAny) + ")" : "")}; started min {started[0]}, median {started[Reps / 2]}, max {started[^1]} | the honest message: refused in {honestRefused}, opened in {honestOpened}, out of time before its turn in {honestBeforeItsTurn}, other {honestOther} | delivery 1 over all repetitions: refused {refusedInFirst}, host calls whose code had not started when its budget ended {notStartedInFirst}; repetitions in which its lookups resumed after their cancellation on more than one thread {resumedOnSeveral} (with a refusal in delivery 2: {refusedAndSeveral}); lookups resumed with a synchronization context present {resumedUnderAContext}"));
    }

    // A delivery (or, without a request, a push) whose attachments are the packed texts themselves.
    private static InboundMessageSnapshot Raw(SentRequest? request, (string Id, string Packed)[] messages)
        => FakePickupExchange.Snapshot(MessagePickup.CreateDelivery(FakePickupExchange.M, FakePickupExchange.L,
            messages.Select(m => new DeliveredMessage(m.Id, Encoding.UTF8.GetBytes(m.Packed))).ToArray(), thid: request?.Request.Id));

    private sealed record Once(int Refused1, int NotStarted1, int Refused2, int Started2, string Honest, int ResumedOnThreads, int ResumedUnderAContext);

    private static async Task<Once> OnceAsync(Way way, bool named, int endMs)
    {
        var enteredOfFirst = 0;
        var resumedOn = new HashSet<int>();
        var resumedUnderAContext = 0;
        async Task<DeliveredUnpack> Host(string packed, CancellationToken token)
        {
            var label = TestJwe.LabelOf(packed);
            if (label == "h")
                return UnpackFakes.Opened("h");
            if (label.StartsWith("a-", StringComparison.Ordinal))
                Interlocked.Increment(ref enteredOfFirst);
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                // Where the lookup resumes after its cancellation: on the cancelling thread (inside the
                // cancellation), or on other threads (queued).
                if (label.StartsWith("a-", StringComparison.Ordinal))
                {
                    lock (resumedOn)
                        resumedOn.Add(Environment.CurrentManagedThreadId);
                    if (SynchronizationContext.Current is not null)
                        Interlocked.Increment(ref resumedUnderAContext);
                }
                if (endMs >= 0)
                    await Task.Delay(endMs); // real time: the lookup's end comes this long after its cancellation
                throw;
            }
            throw new InvalidOperationException("unreachable");
        }
        var exchange = new RealClockExchange(Budget, Host);
        string Packed(string label) => named ? TestJwe.Authcrypt(SamKid, label) : TestJwe.Anoncrypt(label);
        var perDelivery = way == Way.FetchLoop ? 16 : 10; // a stream's delivery has at most ten messages
        var first = Enumerable.Range(0, perDelivery).Select(i => ($"a-{i}", Packed($"a-{i}"))).ToArray();
        var second = Enumerable.Range(0, way == Way.FetchLoop ? 16 : 9).Select(i => ($"b-{i}", Packed($"b-{i}"))).Append(("h", Packed("h"))).ToArray();
        var requests = 0;
        exchange.Wire.On(MessagePickup.MessagesReceivedType, r => FakePickupExchange.Status(r, 0));
        var items = new List<PickupItem>();
        var firstItems = new List<PickupItem>();
        if (way == Way.FetchLoop)
        {
            exchange.Wire.On(MessagePickup.DeliveryRequestType, r => Interlocked.Increment(ref requests) switch
            {
                1 => Raw(r, first),
                2 => Raw(r, second),
                _ => FakePickupExchange.Status(r, 0),
            });
            var client = new PickupClient(exchange, logger: null);
            // The host fetches again at once.
            firstItems.AddRange((await client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 17)).Items);
            items.AddRange((await client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 17)).Items);
        }
        else
        {
            // The push with delivery 2 is in the stream's mailbox before delivery 1 (the first drain round's answer) is handed over.
            exchange.Wire.On(MessagePickup.DeliveryRequestType, r =>
            {
                if (Interlocked.Increment(ref requests) != 1)
                    return FakePickupExchange.Status(r, 0);
                exchange.Wire.Registration!.TryPush(Raw(null, second));
                return Raw(r, first);
            });
            using var cts = new CancellationTokenSource();
            var stream = LiveDeliveryStream.ReceiveAsync(exchange, new PickupLog(logger: null), FakePickupExchange.M, FakePickupExchange.L, serviceEndpointOverride: null,
                static (_, _, _, _) => Task.FromResult(MessagePickupVersion.V3), cts.Token).GetAsyncEnumerator(cts.Token);
            try
            {
                while (firstItems.Count + items.Count < first.Length + second.Length)
                {
                    if (!await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)))
                        break;
                    (stream.Current.Id.StartsWith("a-", StringComparison.Ordinal) ? firstItems : items).Add(stream.Current);
                }
            }
            finally
            {
                await cts.CancelAsync();
                try { await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)); } catch (OperationCanceledException) { }
            }
        }
        static bool Refused(PickupItem i) => i.Error is not null && i.Error.Message.Contains("unpack capacity exhausted", StringComparison.Ordinal);
        static bool BeforeItsTurn(PickupItem i) => i.Error is not null && i.Error.Message.Contains("before its turn came", StringComparison.Ordinal);
        var honest = items.FirstOrDefault(i => i.Id == "h");
        var startedOfFirst = firstItems.Count(i => i.Error is not null && i.Error.Message.Contains("ran out while it was being opened", StringComparison.Ordinal));
        var result = new Once(firstItems.Count(Refused), startedOfFirst - Volatile.Read(ref enteredOfFirst), items.Count(Refused), items.Count(i => i.Error is not null && i.Error.Message.Contains("ran out while it was being opened", StringComparison.Ordinal)),
            honest is null ? "missing" : honest.Unpacked is not null ? "opened" : Refused(honest) ? "refused" : BeforeItsTurn(honest) ? "before its turn" : "other",
            resumedOn.Count, Volatile.Read(ref resumedUnderAContext));
        // Every lookup has ended before the next repetition starts (a clean-up, in real time).
        SpinWait.SpinUntil(() => exchange.Unpacker.Reserved == 0, TimeSpan.FromSeconds(20));
        return result;
    }
}
