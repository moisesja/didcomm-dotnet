namespace DidComm.Tests;

/// <summary>
/// For a test that parks thread-pool threads on a synchronous wait (a gated subscriber, parser or
/// resolver) while its own next step needs another pool thread. On a 4-vCPU CI runner the pool
/// starts with 4 threads, the test host keeps some of them, and on a busy machine new ones come
/// seconds apart. The parked test then stalls itself and every test running beside it, past their
/// 10 s hang guards (a windows CI run of #98).
/// </summary>
internal static class ThreadPoolFloor
{
    /// <summary>Raise (never lower) the pool's floor so <paramref name="parties"/> parked threads leave threads to spare.</summary>
    /// <param name="parties">How many pool threads the test parks at once.</param>
    public static void Raise(int parties)
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        var needed = Environment.ProcessorCount + 2 * parties;
        if (worker < needed)
            ThreadPool.SetMinThreads(needed, io);
    }
}
