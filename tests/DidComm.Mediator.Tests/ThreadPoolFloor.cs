namespace DidComm.Mediator.Tests;

/// <summary>
/// The thread-pool floor of this test process, raised for a test that parks pool threads and put
/// back once no such test holds it any more (the L-065 corollary: raise first, restore afterwards).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it counts:</strong> the tests holding a raise (<see cref="Raise"/> until its result
/// is disposed). While any holds one, the floor is never lowered here, so one test's end never takes
/// the threads another still parks.
/// </para>
/// <para>
/// <strong>What it puts back:</strong> when the last holder is done, the floor that was in place
/// before this helper's own raise, and only if the floor is still the value this helper set. A raise
/// made by something else meanwhile (a helper that raises and never lowers) is never undone: it
/// becomes the floor to put back.
/// </para>
/// <para>One lock; thread-safe.</para>
/// </remarks>
internal static class ThreadPoolFloor
{
    private static readonly object Gate = new();
    private static int _holders;
    private static int _restoreTo;
    private static int _set;

    /// <summary>Raise the worker floor to at least <paramref name="needed"/> threads until the result is disposed.</summary>
    /// <param name="needed">The worker floor the test needs.</param>
    public static IDisposable Raise(int needed)
    {
        lock (Gate)
        {
            ThreadPool.GetMinThreads(out var worker, out var io);
            // The first holder, or a floor something else changed since this helper set it: the
            // floor now is what to put back.
            if (_holders == 0 || worker != _set)
            {
                _restoreTo = worker;
                _set = 0;
            }
            _holders++;
            if (worker < needed && ThreadPool.SetMinThreads(needed, io))
                _set = needed;
        }
        return new Holder();
    }

    private static void Release()
    {
        lock (Gate)
        {
            if (--_holders > 0)
                return;
            ThreadPool.GetMinThreads(out var worker, out var io);
            if (_set != 0 && worker == _set)
                ThreadPool.SetMinThreads(_restoreTo, io);
            _set = 0;
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
