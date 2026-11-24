namespace DidComm.TestSupport;

/// <summary>
/// The thread-pool floor of a test process, raised for a test that parks pool threads and put back
/// once no such test holds it any more (the L-065 corollary: raise first, restore afterwards). On a
/// 4-vCPU CI runner the pool starts with few threads and adds them seconds apart, so a test that
/// parks threads without a higher floor stalls itself and every test running beside it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it counts:</strong> the tests holding a raise (<see cref="Raise"/> or
/// <see cref="ForParked"/> until the result is disposed). While any holds one, the floor is never
/// lowered here, so one test's end never takes the threads another still parks. Hold the result for
/// the whole test: <c>using var floor = ThreadPoolFloor.ForParked(n);</c>, or, for a raise made in a
/// test class's constructor, a field disposed by the class's <c>Dispose</c>.
/// </para>
/// <para>
/// <strong>What it puts back:</strong> when the last holder is done, the floors that were in place
/// before this helper's own raise, and only if they are still the values this helper set. A raise
/// made by something else meanwhile is never undone: it becomes the floor to put back.
/// </para>
/// <para>One lock; thread-safe.</para>
/// </remarks>
public static class ThreadPoolFloor
{
    private static readonly object Gate = new();
    private static int _holders;
    private static (int Worker, int Io) _restoreTo;
    private static (int Worker, int Io)? _applied;

    /// <summary>Raise the worker floor to at least <paramref name="workers"/> threads, and the I/O floor to at least <paramref name="io"/>, until the result is disposed.</summary>
    /// <param name="workers">The worker floor the test needs.</param>
    /// <param name="io">The I/O completion floor the test needs (0: leave it).</param>
    /// <returns>The raise; dispose it when the test ends.</returns>
    public static IDisposable Raise(int workers, int io = 0)
    {
        lock (Gate)
        {
            ThreadPool.GetMinThreads(out var worker, out var completion);
            // The first holder, or floors something else changed since this helper set them: the
            // floors now are what to put back.
            if (_holders == 0 || _applied is not { } applied || applied != (worker, completion))
            {
                _restoreTo = (worker, completion);
                _applied = null;
            }
            _holders++;
            var target = (Worker: Math.Max(worker, workers), Io: Math.Max(completion, io));
            if (target != (worker, completion) && ThreadPool.SetMinThreads(target.Worker, target.Io))
                _applied = target;
        }
        return new Holder();
    }

    /// <summary>Raise the worker floor so <paramref name="parties"/> parked pool threads leave threads to spare (the processor count plus twice the parties), until the result is disposed.</summary>
    /// <param name="parties">How many pool threads the test parks at once.</param>
    /// <returns>The raise; dispose it when the test ends.</returns>
    public static IDisposable ForParked(int parties) => Raise(Environment.ProcessorCount + 2 * parties);

    private static void Release()
    {
        lock (Gate)
        {
            if (--_holders > 0)
                return;
            ThreadPool.GetMinThreads(out var worker, out var completion);
            if (_applied is { } applied && applied == (worker, completion))
                ThreadPool.SetMinThreads(_restoreTo.Worker, _restoreTo.Io);
            _applied = null;
        }
    }

    private sealed class Holder : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Release();
        }
    }
}
