namespace DidComm.Secrets;

/// <summary>
/// Bridges the public async <see cref="ISecretsResolver"/> contract to the internal
/// synchronous <see cref="IInternalSecretsLookup"/> the JOSE composition layer consumes
/// (see PRD §7 / Phase 3 plan: the envelope layer stays sync). The synchronous methods run the
/// underlying async resolver on the thread pool via <see cref="Task.Run{TResult}(Func{Task{TResult}})"/>
/// and block on the result, so a consumer resolver's inner <c>await</c> never resumes on a captured
/// <see cref="System.Threading.SynchronizationContext"/> — avoiding the classic sync-over-async
/// deadlock if the facade is ever invoked under a custom/legacy UI context.
/// </summary>
internal sealed class SyncSecretsAdapter : IInternalSecretsLookup
{
    private readonly ISecretsResolver _inner;

    public SyncSecretsAdapter(ISecretsResolver inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public Jwk? TryGet(string kid)
        => Task.Run(() => _inner.FindAsync(kid)).GetAwaiter().GetResult();

    public IReadOnlyList<string> FindPresent(IEnumerable<string> kids)
        => Task.Run(() => _inner.FindPresentAsync(kids)).GetAwaiter().GetResult();
}
