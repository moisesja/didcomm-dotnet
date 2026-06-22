using System.Text.Json;
using DidComm.Adapters.NetDid;
using DidComm.Jose;
using DidComm.Secrets;
using NetCrypto;

namespace DidComm.InteropTests.Resolution;

/// <summary>
/// Test-only registry: loads the vendored DIDComm spec Appendix-A secrets for Alice + Bob
/// from <c>fixtures/secrets/{alice,bob}.json</c> and exposes them as a public
/// <see cref="ISecretsResolver"/> (recipient secret-key path) plus the internal
/// <see cref="IInternalSenderKeyLookup"/> / signer-key lookup contracts.
/// </summary>
/// <remarks>
/// Singleton-style — fixture runners create one instance per test class and share it across
/// theory cases. The contents are static (vendored from the spec) so no per-test state
/// management is needed.
/// </remarks>
internal sealed class SpecActorRegistry
{
    private readonly Dictionary<string, Jwk> _privates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Jwk> _publics = new(StringComparer.Ordinal);

    public static SpecActorRegistry LoadDefault()
    {
        var fixturesRoot = FixtureCatalog.FixturesRoot;
        var registry = new SpecActorRegistry();
        registry.LoadFromFile(Path.Combine(fixturesRoot, "secrets", "alice.json"));
        registry.LoadFromFile(Path.Combine(fixturesRoot, "secrets", "bob.json"));
        // Phase 4 routing additions — mediator1/mediator2 reuse the same X25519 / P-256
        // private bytes as Bob (the public keys in mediator{1,2}.json match Bob's by design,
        // so the same `d` values decrypt). See fixtures/secrets/README.md for provenance.
        registry.LoadFromFile(Path.Combine(fixturesRoot, "secrets", "mediator1.json"));
        registry.LoadFromFile(Path.Combine(fixturesRoot, "secrets", "mediator2.json"));
        return registry;
    }

    public IInternalSenderKeyLookup SenderKeys => new DictionarySenderKeyLookup(_publics);
    public Func<string, Jwk?> SignerKeys => kid => _publics.GetValueOrDefault(kid);

    /// <summary>Exposes the loaded secrets through the public <see cref="ISecretsResolver"/> contract (the recipient secret-key path the async unpack drives).</summary>
    public ISecretsResolver AsSecretsResolver() => new DictionarySecretsResolver(_privates);

    /// <summary>
    /// Seed a <strong>non-extractable</strong> NetCrypto <see cref="InMemoryKeyStore"/> with the same
    /// Appendix-A private keys (alias == kid) and surface it through the opaque-capable
    /// <see cref="NetDidKeyStoreSecretsResolver"/> (FR-SEC-06). The private scalars are imported once
    /// and thereafter live only inside the keystore boundary — the resolver returns public-only JWKs
    /// and performs signing / ECDH through keystore handles. Lets the facade round-trip tests prove the
    /// opaque custody path with the very same key material as the extractable path.
    /// </summary>
    public NetDidKeyStoreSecretsResolver AsKeyStoreResolver()
    {
        var keyGen = new DefaultKeyGenerator();
        var store = new InMemoryKeyStore(keyGen, new DefaultCryptoProvider());
        foreach (var (kid, jwk) in _privates)
        {
            var keyType = CrvToKeyType(jwk.Crv!);
            var priv = DataProofsDotnet.Jose.Base64Url.Decode(jwk.D!);
            var keyPair = keyGen.FromPrivateKey(keyType, priv);
            store.ImportAsync(kid, keyPair).GetAwaiter().GetResult();
        }
        return new NetDidKeyStoreSecretsResolver(store);
    }

    private static KeyType CrvToKeyType(string crv) => crv switch
    {
        "Ed25519" => KeyType.Ed25519,
        "X25519" => KeyType.X25519,
        "P-256" => KeyType.P256,
        "P-384" => KeyType.P384,
        "P-521" => KeyType.P521,
        "secp256k1" => KeyType.Secp256k1,
        _ => throw new NotSupportedException($"Appendix-A crv '{crv}' has no NetCrypto KeyType mapping."),
    };

    public Jwk? GetPrivate(string kid) => _privates.GetValueOrDefault(kid);
    public Jwk? GetPublic(string kid) => _publics.GetValueOrDefault(kid);

    private void LoadFromFile(string path)
    {
        var content = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<List<JsonElement>>(content)
            ?? throw new InvalidDataException($"Secrets file did not parse: {path}");

        foreach (var entry in entries)
        {
            var kid = entry.GetProperty("kid").GetString()!;
            var privateJwk = new Jwk
            {
                Kid = kid,
                Kty = entry.GetProperty("kty").GetString()!,
                Crv = entry.TryGetProperty("crv", out var c) ? c.GetString() : null,
                X = entry.TryGetProperty("x", out var x) ? x.GetString() : null,
                Y = entry.TryGetProperty("y", out var y) ? y.GetString() : null,
                D = entry.TryGetProperty("d", out var d) ? d.GetString() : null,
            };
            var publicJwk = new Jwk
            {
                Kid = kid,
                Kty = privateJwk.Kty,
                Crv = privateJwk.Crv,
                X = privateJwk.X,
                Y = privateJwk.Y,
            };
            _privates[kid] = privateJwk;
            _publics[kid] = publicJwk;
        }
    }

    private sealed class DictionarySenderKeyLookup : IInternalSenderKeyLookup
    {
        private readonly Dictionary<string, Jwk> _byKid;
        public DictionarySenderKeyLookup(Dictionary<string, Jwk> byKid) => _byKid = byKid;
        public Jwk? TryGet(string skid) => _byKid.GetValueOrDefault(skid);
    }

    private sealed class DictionarySecretsResolver : ISecretsResolver
    {
        private readonly Dictionary<string, Jwk> _byKid;
        public DictionarySecretsResolver(Dictionary<string, Jwk> byKid) => _byKid = byKid;
        public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default)
            => Task.FromResult(_byKid.GetValueOrDefault(kid));
        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
        {
            IReadOnlyList<string> hits = kids.Where(_byKid.ContainsKey).ToArray();
            return Task.FromResult(hits);
        }
    }
}
