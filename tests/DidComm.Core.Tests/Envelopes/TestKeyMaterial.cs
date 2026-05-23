using DidComm.Jose;
using DidComm.Secrets;
using NetDid.Core;
using NetDid.Core.Crypto;
using NetDidJwkConverter = NetDid.Core.Jwk.JwkConverter;

namespace DidComm.Tests.Envelopes;

/// <summary>
/// Round-trip test material: generates fresh keypairs per curve and packs them into the
/// DIDComm <see cref="Jwk"/> shape the envelope layer expects. Also implements the two
/// internal lookup contracts so tests can drive <c>JweParser</c> / <c>EnvelopeReader</c>
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
        var privateNetDid = NetDidJwkConverter.ToPrivateJwk(pair);
        var publicNetDid = NetDidJwkConverter.ToPublicJwk(pair);
        var priv = new Jwk
        {
            Kty = privateNetDid.Kty,
            Crv = privateNetDid.Crv,
            X = privateNetDid.X,
            Y = privateNetDid.Y,
            D = privateNetDid.D,
            Kid = kid,
        };
        var pub = new Jwk
        {
            Kty = publicNetDid.Kty,
            Crv = publicNetDid.Crv,
            X = publicNetDid.X,
            Y = publicNetDid.Y,
            Kid = kid,
        };
        return new TestKeyMaterial(priv, pub);
    }
}

internal sealed class DictionarySecretsLookup : IInternalSecretsLookup
{
    private readonly Dictionary<string, Jwk> _byKid;

    public DictionarySecretsLookup(IEnumerable<Jwk> privateJwks)
    {
        _byKid = privateJwks.ToDictionary(j => j.Kid!, StringComparer.Ordinal);
    }

    public Jwk? TryGet(string kid) => _byKid.GetValueOrDefault(kid);

    public IReadOnlyList<string> FindPresent(IEnumerable<string> kids)
        => kids.Where(k => _byKid.ContainsKey(k)).ToArray();
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
