using DidComm.Secrets;
using NetCrypto;
using JwkConversion = DataProofsDotnet.Jose.JwkConversion;

namespace DidComm.Tests.Envelopes;

/// <summary>
/// Round-trip test material: generates fresh keypairs per curve and packs them into the
/// <see cref="Jwk"/> shape (DataProofsDotnet.Jose) the envelope layer expects. Also implements the
/// two internal lookup contracts so tests can drive the JOSE parsers / <c>EnvelopeReader</c>
/// without standing up the Phase 3 resolver.
/// </summary>
internal sealed class TestKeyMaterial
{
    private static readonly IKeyGenerator _generator = new DefaultKeyGenerator();

    public Jwk PrivateJwk { get; }
    public Jwk PublicJwk { get; }

    private TestKeyMaterial(Jwk privateJwk, Jwk publicJwk)
    {
        PrivateJwk = privateJwk;
        PublicJwk = publicJwk;
    }

    public static TestKeyMaterial Generate(KeyType keyType, string kid)
    {
        var pair = _generator.Generate(keyType);
        var priv = JwkConversion.ToPrivateJwk(pair, kid);
        var pub = JwkConversion.ToPublicJwk(pair.KeyType, pair.PublicKey, kid);
        return new TestKeyMaterial(priv, pub);
    }
}

internal sealed class DictionarySecretsLookup : ISecretsResolver
{
    private readonly Dictionary<string, Jwk> _byKid;

    public DictionarySecretsLookup(IEnumerable<Jwk> privateJwks)
    {
        _byKid = privateJwks.ToDictionary(j => j.Kid!, StringComparer.Ordinal);
    }

    public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default)
        => Task.FromResult(_byKid.GetValueOrDefault(kid));

    public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(kids.Where(k => _byKid.ContainsKey(k)).ToArray());
}

internal sealed class DictionarySenderKeyLookup : IInternalSenderKeyLookup
{
    private readonly Dictionary<string, Jwk> _byKid;

    public DictionarySenderKeyLookup(IEnumerable<Jwk> publicJwks)
    {
        _byKid = publicJwks.ToDictionary(j => j.Kid!, StringComparer.Ordinal);
    }

    public Jwk? TryGet(string skid) => _byKid.GetValueOrDefault(skid);
}
