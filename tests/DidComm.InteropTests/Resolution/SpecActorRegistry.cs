using System.Text.Json;
using DidComm.Jose;
using DidComm.Secrets;
using NetDid.Core.Crypto;
using NetDidJwkConverter = NetDid.Core.Jwk.JwkConverter;

namespace DidComm.InteropTests.Resolution;

/// <summary>
/// Test-only registry: loads the vendored DIDComm spec Appendix-A secrets for Alice + Bob
/// from <c>fixtures/secrets/{alice,bob}.json</c> and exposes them as DidComm's internal
/// <see cref="IInternalSecretsLookup"/> and <see cref="IInternalSenderKeyLookup"/> contracts.
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
        return registry;
    }

    public IInternalSecretsLookup Secrets => new DictionarySecretsLookup(_privates);
    public IInternalSenderKeyLookup SenderKeys => new DictionarySenderKeyLookup(_publics);
    public Func<string, Jwk?> SignerKeys => kid => _publics.GetValueOrDefault(kid);

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

    private sealed class DictionarySecretsLookup : IInternalSecretsLookup
    {
        private readonly Dictionary<string, Jwk> _byKid;
        public DictionarySecretsLookup(Dictionary<string, Jwk> byKid) => _byKid = byKid;
        public Jwk? TryGet(string kid) => _byKid.GetValueOrDefault(kid);
        public IReadOnlyList<string> FindPresent(IEnumerable<string> kids)
            => kids.Where(_byKid.ContainsKey).ToArray();
    }

    private sealed class DictionarySenderKeyLookup : IInternalSenderKeyLookup
    {
        private readonly Dictionary<string, Jwk> _byKid;
        public DictionarySenderKeyLookup(Dictionary<string, Jwk> byKid) => _byKid = byKid;
        public Jwk? TryGet(string skid) => _byKid.GetValueOrDefault(skid);
    }
}
