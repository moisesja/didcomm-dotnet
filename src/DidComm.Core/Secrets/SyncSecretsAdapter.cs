using DidComm.Jose;

namespace DidComm.Secrets;

/// <summary>
/// Bridges the public async <see cref="ISecretsResolver"/> contract to the internal
/// synchronous <see cref="IInternalSecretsLookup"/> the JOSE composition layer consumes
/// (see PRD §7 / Phase 3 plan: the envelope layer stays sync). The synchronous methods block
/// on the underlying async resolver via <c>.GetAwaiter().GetResult()</c>; this is safe under
/// .NET 10's default no-synchronization-context runtime, where the facade itself is async.
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
        => _inner.FindAsync(kid).ConfigureAwait(false).GetAwaiter().GetResult();

    public IReadOnlyList<string> FindPresent(IEnumerable<string> kids)
        => _inner.FindPresentAsync(kids).ConfigureAwait(false).GetAwaiter().GetResult();
}
