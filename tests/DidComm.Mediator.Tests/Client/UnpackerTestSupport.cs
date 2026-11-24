using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;

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
/// Raises the thread pool's floor for a test that parks pool threads (L-065 corollary), and puts it
/// back when disposed (<see cref="ThreadPoolFloor"/>: not while another test still holds a raise, and
/// not over a raise something else made meanwhile).
/// </summary>
internal sealed class PoolFloor : IDisposable
{
    private readonly IDisposable _raise;

    public PoolFloor(int parked) => _raise = ThreadPoolFloor.Raise(Environment.ProcessorCount + parked + 8);

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
