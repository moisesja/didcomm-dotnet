using System.Collections.Concurrent;

namespace DidComm.Threading;

/// <summary>
/// Process-local, thread-safe <see cref="IThreadStateStore"/>. Suitable for single-instance
/// agents; replace with a distributed store (Redis, Cosmos, etc.) for horizontally-scaled
/// mediators where the same thread can be served by different processes.
/// </summary>
public sealed class InMemoryThreadStateStore : IThreadStateStore
{
    private readonly ConcurrentDictionary<string, ThreadState> _states = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ThreadState GetOrCreate(string thid)
    {
        ArgumentException.ThrowIfNullOrEmpty(thid);
        return _states.GetOrAdd(thid, static id => new ThreadState(id));
    }

    /// <inheritdoc />
    public ThreadState? Get(string thid)
    {
        ArgumentException.ThrowIfNullOrEmpty(thid);
        return _states.TryGetValue(thid, out var state) ? state : null;
    }

    /// <inheritdoc />
    public bool Remove(string thid)
    {
        ArgumentException.ThrowIfNullOrEmpty(thid);
        return _states.TryRemove(thid, out _);
    }
}
