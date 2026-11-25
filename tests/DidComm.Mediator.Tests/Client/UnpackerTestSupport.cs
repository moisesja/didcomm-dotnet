using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.TestSupport;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// Stand-in General JSON JWEs for the unpacker's sender partition (#111): the protected header and the
/// structure pass <c>JweParser.PeekRecipients</c>, nothing is encrypted, and each carries a label the
/// faked unpack step reads back (<see cref="LabelOf"/>) to decide what the message does.
/// </summary>
internal static class TestJwe
{
    /// <summary>The authcrypt key agreement.</summary>
    public const string Authcrypt1Pu = "ECDH-1PU+A256KW";

    /// <summary>The anoncrypt key agreement.</summary>
    public const string AnoncryptEs = "ECDH-ES+A256KW";

    /// <summary>The recipient kid every stand-in names.</summary>
    public const string RecipientKid = "did:example:bob#key-x25519-1";

    /// <summary>Authcrypt naming <paramref name="skid"/> in <c>skid</c> (and in <c>apu</c>, as the spec asks).</summary>
    public static string Authcrypt(string skid, string label)
        => Build(new JsonObject { ["alg"] = Authcrypt1Pu, ["enc"] = "A256CBC-HS512", ["skid"] = skid, ["apu"] = B64(skid) }, label);

    /// <summary>Authcrypt naming its sender only in <c>apu</c>, without <c>skid</c>.</summary>
    public static string AuthcryptApuOnly(string senderKid, string label)
        => Build(new JsonObject { ["alg"] = Authcrypt1Pu, ["enc"] = "A256CBC-HS512", ["apu"] = B64(senderKid) }, label);

    /// <summary>An <c>ECDH-ES</c> envelope that carries a <c>skid</c> anyway.</summary>
    public static string EcdhEsWithSkid(string skid, string label)
        => Build(new JsonObject { ["alg"] = AnoncryptEs, ["enc"] = "A256GCM", ["skid"] = skid }, label);

    /// <summary>Anoncrypt.</summary>
    public static string Anoncrypt(string label)
        => Build(new JsonObject { ["alg"] = AnoncryptEs, ["enc"] = "A256GCM" }, label);

    /// <summary>The stand-in's bytes.</summary>
    public static ReadOnlyMemory<byte> Bytes(string packed) => Encoding.UTF8.GetBytes(packed);

    /// <summary>The label of a stand-in, or the text itself when it is not one.</summary>
    public static string LabelOf(string packed)
    {
        try
        {
            return JsonNode.Parse(packed)?["x-label"]?.GetValue<string>() ?? packed;
        }
        catch (System.Text.Json.JsonException)
        {
            return packed;
        }
    }

    private static string Build(JsonObject header, string label)
        => new JsonObject
        {
            ["protected"] = B64(header.ToJsonString()),
            ["recipients"] = new JsonArray(new JsonObject { ["header"] = new JsonObject { ["kid"] = RecipientKid }, ["encrypted_key"] = "AAAA" }),
            ["iv"] = "AAAA",
            ["ciphertext"] = "AAAA",
            ["tag"] = "AAAA",
            ["x-label"] = label,
        }.ToJsonString();

    private static string B64(string text) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(text));
}

/// <summary>Unpack results and a recorder of host unpack calls for the unpacker's tests (#111).</summary>
internal static class UnpackFakes
{
    /// <summary>A message that opened, with <paramref name="id"/> as its message id.</summary>
    public static DeliveredUnpack Opened(string id)
    {
        var message = new MessageBuilder().WithId(id).WithType("https://example.org/notes/1.0/note").WithBody(new JsonObject()).Build();
        var result = new UnpackResult(message, new[] { DidComm.Jose.EnvelopeKind.Plaintext }, false, false, false, false, null, null, null, null, null, null, Array.Empty<string>(), null);
        return new DeliveredUnpack(result, id, 0);
    }

    /// <summary>A host lookup that honours its token and never ends otherwise.</summary>
    public static async Task<DeliveredUnpack> StallUntilCancelled(CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        throw new InvalidOperationException("unreachable");
    }
}

/// <summary>Records each host unpack call (its label and token) and lets a test wait for a number of them.</summary>
internal sealed class HostCallLog
{
    private readonly object _gate = new();
    private readonly List<(string Label, CancellationToken Token)> _calls = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _running;
    private int _maxRunning;

    /// <summary>How many calls were made.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _calls.Count;
        }
    }

    /// <summary>The most calls that were running at once (entered and not left).</summary>
    public int MaxRunning => Volatile.Read(ref _maxRunning);

    /// <summary>The labels of the calls, in the order they were made.</summary>
    public IReadOnlyList<string> Labels
    {
        get
        {
            lock (_gate)
                return _calls.Select(c => c.Label).ToArray();
        }
    }

    /// <summary>The tokens of the calls, in the order they were made.</summary>
    public IReadOnlyList<CancellationToken> Tokens
    {
        get
        {
            lock (_gate)
                return _calls.Select(c => c.Token).ToArray();
        }
    }

    /// <summary>Record a call entering; dispose the result when it leaves.</summary>
    public IDisposable Enter(string label, CancellationToken ct)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            _calls.Add((label, ct));
            var running = ++_running;
            if (running > _maxRunning)
                Volatile.Write(ref _maxRunning, running);
            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
        return new Leave(this);
    }

    /// <summary>Wait (bounded, in real time) until at least <paramref name="count"/> calls were made.</summary>
    public async Task WaitForAsync(int count, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_calls.Count >= count)
                    return;
                changed = _changed.Task;
            }
            await changed.WaitAsync(cts.Token);
        }
    }

    private sealed class Leave(HostCallLog owner) : IDisposable
    {
        private int _left;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0)
            {
                lock (owner._gate)
                    owner._running--;
            }
        }
    }
}

/// <summary>
/// Raises the thread pool's floor for a test that parks pool threads (L-065 corollary), through the
/// counted <see cref="DidComm.TestSupport.ThreadPoolFloor"/>: put back when the last holder is done,
/// and never undoing a raise made by another test meanwhile.
/// </summary>
internal sealed class PoolFloor : IDisposable
{
    private readonly IDisposable _raise;

    public PoolFloor(int parked) => _raise = DidComm.TestSupport.ThreadPoolFloor.Raise(Environment.ProcessorCount + parked + 8);

    public void Dispose() => _raise.Dispose();
}

/// <summary>
/// A scheduler that never runs a task inline and holds every queued task until <see cref="RunAll"/>:
/// it makes the reservations' release continuation run late (D-13's seam).
/// </summary>
internal sealed class HeldScheduler : TaskScheduler
{
    private readonly ConcurrentQueue<Task> _held = new();

    /// <summary>How many tasks are held.</summary>
    public int Held => _held.Count;

    /// <summary>Run every held task, on this thread.</summary>
    public void RunAll()
    {
        while (_held.TryDequeue(out var task))
            TryExecuteTask(task);
    }

    protected override IEnumerable<Task> GetScheduledTasks() => _held.ToArray();

    protected override void QueueTask(Task task) => _held.Enqueue(task);

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
}

/// <summary>
/// A clock for the unpacker's tests over a <see cref="ManualTimeProvider"/>: it can run a hook right
/// after one of its timers is created, and its readings can be shifted. A delivery's only timer is
/// its budget, created after the budget's start was read and before any message is taken, so a hook
/// on a delivery's timer moves the clock between the budget's start and the start of its first
/// messages, as when their host calls wait for a thread.
/// </summary>
internal sealed class UnpackClock(ManualTimeProvider inner) : TimeProvider
{
    private int _created;
    private long _shift;

    /// <summary>The manual clock behind it: timers fire only when it is advanced.</summary>
    public ManualTimeProvider Inner => inner;

    /// <summary>Runs right after a timer is created, with its 1-based number, on the creating thread.</summary>
    public Action<int>? AfterTimerCreated { get; set; }

    /// <summary>Shift every later reading by <paramref name="by"/> (negative: the clock steps back). Timers are not moved.</summary>
    public void Shift(TimeSpan by) => Interlocked.Add(ref _shift, by.Ticks);

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override long GetTimestamp() => inner.GetTimestamp() + Interlocked.Read(ref _shift);

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = inner.CreateTimer(callback, state, dueTime, period);
        AfterTimerCreated?.Invoke(Interlocked.Increment(ref _created));
        return timer;
    }

    /// <summary>A clock that moves on by <paramref name="step"/> right after its first timer (the first delivery's budget) is created.</summary>
    public static UnpackClock SteppingAfterTheFirstBudget(ManualTimeProvider inner, TimeSpan step)
    {
        var clock = new UnpackClock(inner);
        clock.AfterTimerCreated = n =>
        {
            if (n == 1)
                inner.Advance(step);
        };
        return clock;
    }
}

/// <summary>
/// Awaits a task without capturing a context, always suspending, and runs an action once the
/// awaiting method's continuation is hooked on the task. A signal given from that action tells a
/// test that the method is parked there, and that completing the task runs the method's
/// continuation on the completing thread (a signal given before the <c>await</c> would not).
/// </summary>
internal readonly struct ParkedAwaitable<T>(Task<T> task, Action parked)
{
    public Awaiter GetAwaiter() => new(task, parked);

    internal readonly struct Awaiter(Task<T> task, Action parked) : System.Runtime.CompilerServices.ICriticalNotifyCompletion
    {
        public bool IsCompleted => false;

        public T GetResult() => task.GetAwaiter().GetResult();

        public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation)
        {
            task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(continuation);
            parked();
        }
    }
}

/// <summary>
/// The gate of <see cref="CallbackLookups"/>: a lookup parks on it before it registers its
/// cancellation callback. A test opens it once it has seen <c>UnpackAllAsync</c> return (every worker
/// waits on its call by then) and the lookups are parked (each has returned its task to the
/// unpacker by then), so a lookup's callback is registered after its worker's and runs before it.
/// </summary>
internal sealed class RegistrationGate
{
    private readonly TaskCompletionSource<bool> _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HostCallLog _parked = new();

    /// <summary>Park here until the gate is open; always suspends.</summary>
    public ParkedAwaitable<bool> Parked() => new(_open.Task, () => _parked.Enter("parked", default));

    /// <summary>Wait (bounded, in real time) until <paramref name="lookups"/> lookups are parked on the gate, then open it.</summary>
    public async Task OpenAsync(int lookups, TimeSpan timeout)
    {
        await _parked.WaitForAsync(lookups, timeout);
        _open.TrySetResult(true);
    }
}

/// <summary>
/// Host lookups for the unpacker's tests that end from inside a cancellation callback of their own.
/// Each parks on the <see cref="RegistrationGate"/> first, registers its callback once the gate is
/// open, and records itself in the log once it is parked on its own end: completing it from the
/// callback then runs the lookup's continuation, and the unpacker's worker behind it, on the thread
/// that cancels.
/// </summary>
/// <remarks>
/// What a test can rely on: the order of the callbacks (the lookup's runs before its worker's and
/// before the delivery's own), and that the lookup's continuation is hooked when the log shows it.
/// That the unpacker's own wait on the lookup's task is hooked by then follows from the lookup
/// having parked on the gate first, a step earlier; it is not observed.
/// </remarks>
internal static class CallbackLookups
{
    /// <summary>
    /// A lookup that never ends on its own and reports its cancellation as a resolution failure
    /// (a resolver that maps every error, its cancellation included, to "notFound"), from inside the
    /// callback it registered on its token, or on the token <paramref name="on"/> picks instead.
    /// </summary>
    public static Func<string, CancellationToken, Task<DeliveredUnpack>> ReportsCancellationAsFailure(HostCallLog calls, RegistrationGate gate, Func<CancellationToken, CancellationToken>? on = null)
        => async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            var ends = new TaskCompletionSource<DeliveredUnpack>(); // its continuations run inline, inside the callback
            await gate.Parked();
            (on is null ? ct : on(ct)).UnsafeRegister(_ => ends.TrySetException(new DidComm.Exceptions.DidResolutionException("did:example:alice", "notFound")), null);
            return await new ParkedAwaitable<DeliveredUnpack>(ends.Task, () => calls.Enter(label, ct));
        };

    /// <summary>
    /// A lookup that never ends on its own and opens its message from inside the callback it
    /// registered on its token, or on the token <paramref name="on"/> picks instead. The callback
    /// then stays inside the cancellation until <paramref name="recorded"/> says the unpacker has
    /// recorded that success (bounded, as a hang guard): the delivery's own callback runs after it,
    /// so the delivery is still open when the success is recorded, wherever its worker runs.
    /// </summary>
    public static Func<string, CancellationToken, Task<DeliveredUnpack>> OpensWhenCancelled(HostCallLog calls, RegistrationGate gate, Func<bool> recorded, Func<CancellationToken, CancellationToken>? on = null)
        => async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            var ends = new TaskCompletionSource<DeliveredUnpack>(); // its continuations run inline, inside the callback
            await gate.Parked();
            (on is null ? ct : on(ct)).UnsafeRegister(_ =>
            {
                ends.TrySetResult(UnpackFakes.Opened(label));
                SpinWait.SpinUntil(recorded, TimeSpan.FromSeconds(10));
            }, null);
            return await new ParkedAwaitable<DeliveredUnpack>(ends.Task, () => calls.Enter(label, ct));
        };
}

/// <summary>What a host lookup that never opens its message does when its token is cancelled.</summary>
public enum WhenCancelled
{
    /// <summary>It ends, cancelled.</summary>
    Ends,

    /// <summary>It ignores its token and runs on until the test lets it go.</summary>
    RunsOn,

    /// <summary>It ends as a resolution failure, from inside its cancellation callback (<see cref="CallbackLookups.ReportsCancellationAsFailure"/>).</summary>
    ReportsAFailure,
}

/// <summary>Host lookups that never open their message, for each <see cref="WhenCancelled"/>.</summary>
internal static class NeverOpens
{
    /// <summary>The lookup for <paramref name="whenCancelled"/>; each records itself in <paramref name="calls"/> once it is in place.</summary>
    /// <param name="whenCancelled">What it does when its token is cancelled.</param>
    /// <param name="calls">The log of host calls.</param>
    /// <param name="register">The gate a lookup that reports a failure parks on before it registers its callback.</param>
    /// <param name="never">What a lookup that runs on waits for.</param>
    public static Func<string, CancellationToken, Task<DeliveredUnpack>> Lookup(WhenCancelled whenCancelled, HostCallLog calls, RegistrationGate register, Task<DeliveredUnpack> never)
        => whenCancelled switch
        {
            WhenCancelled.Ends => async (packed, ct) =>
            {
                using (calls.Enter(TestJwe.LabelOf(packed), ct))
                    return await UnpackFakes.StallUntilCancelled(ct).ConfigureAwait(false);
            },
            WhenCancelled.RunsOn => (packed, ct) =>
            {
                calls.Enter(TestJwe.LabelOf(packed), ct);
                return never;
            },
            _ => CallbackLookups.ReportsCancellationAsFailure(calls, register),
        };
}
