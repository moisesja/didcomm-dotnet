using DidComm.Jose;
using DidComm.Secrets;

namespace DidComm.TestSupport;

/// <summary>
/// Dictionary-backed <see cref="ISecretsResolver"/> for tests and samples (FR-SEC-05). Lives
/// outside <c>DidComm.Core</c> so DD-02 ("ship no production key store in Core") stays
/// honest.
/// </summary>
/// <remarks>
/// Thread-safe for the typical add-then-read pattern: callers populate the resolver during
/// arrange, then concurrent test threads only read. For mutation under concurrent reads, wrap
/// the dictionary externally.
/// </remarks>
public sealed class InMemorySecretsResolver : ISecretsResolver
{
    private readonly Dictionary<string, Jwk> _byKid;

    /// <summary>Initialise empty; populate via <see cref="Add(Jwk)"/>.</summary>
    public InMemorySecretsResolver()
    {
        _byKid = new Dictionary<string, Jwk>(StringComparer.Ordinal);
    }

    /// <summary>Initialise from a sequence of JWKs whose <c>Kid</c> is set.</summary>
    /// <param name="seed">Private JWKs to register.</param>
    public InMemorySecretsResolver(IEnumerable<Jwk> seed) : this()
    {
        ArgumentNullException.ThrowIfNull(seed);
        foreach (var jwk in seed)
            Add(jwk);
    }

    /// <summary>Add a private JWK keyed by its <c>Kid</c>.</summary>
    /// <param name="privateJwk">Private JWK with non-null <c>Kid</c> and <c>D</c>.</param>
    public void Add(Jwk privateJwk)
    {
        ArgumentNullException.ThrowIfNull(privateJwk);
        if (string.IsNullOrEmpty(privateJwk.Kid))
            throw new ArgumentException("JWK 'kid' is required.", nameof(privateJwk));
        _byKid[privateJwk.Kid] = privateJwk;
    }

    /// <inheritdoc />
    public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default)
        => Task.FromResult(_byKid.GetValueOrDefault(kid));

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
    {
        IReadOnlyList<string> hits = kids.Where(_byKid.ContainsKey).ToArray();
        return Task.FromResult(hits);
    }
}
