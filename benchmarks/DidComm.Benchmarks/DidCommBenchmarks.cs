using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;

namespace DidComm.Benchmarks;

/// <summary>
/// The NFR-07 hot-path suite: anoncrypt pack (1 recipient), authcrypt pack (1 recipient),
/// unpack, and did:key resolution. Identities are did:peer:2 / did:key, so "resolution" is
/// in-process DID parsing — there is no network anywhere, matching NFR-07's "excluding
/// network" framing while keeping the measured path exactly what a real app executes.
/// </summary>
[MemoryDiagnoser]
public class DidCommBenchmarks
{
    private ServiceProvider _sp = null!;
    private DidCommClient _client = null!;
    private IDidKeyService _keyService = null!;
    private string _aliceDid = null!;
    private string[] _bobRecipients = null!;
    private string _didKey = null!;
    private Message _message = null!;
    private string _packedAuthcrypt = null!;
    private PackEncryptedOptions _anonOptions = null!;
    private PackEncryptedOptions _authOptions = null!;

    [GlobalSetup]
    public void Setup() => SetupAsync().GetAwaiter().GetResult();

    private async Task SetupAsync()
    {
        var secrets = new InMemorySecretsResolver();
        var services = new ServiceCollection();
        services.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(secrets));
        _sp = services.BuildServiceProvider();

        var manager = _sp.GetRequiredService<IDidManager>();
        var keyGen = _sp.GetRequiredService<IKeyGenerator>();
        var crypto = _sp.GetRequiredService<ICryptoProvider>();
        _keyService = _sp.GetRequiredService<IDidKeyService>();
        _client = _sp.GetRequiredService<DidCommClient>();

        // One client holds both identities' private keys so it can play sender and recipient;
        // that is a benchmark convenience only — the measured crypto path is identical.
        var alice = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        var bob = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        foreach (var jwk in alice.Privates)
            secrets.Add(jwk);
        foreach (var jwk in bob.Privates)
            secrets.Add(jwk);
        _aliceDid = alice.Did;
        _bobRecipients = new[] { bob.Did };

        // A did:key for the resolution benchmark (Ed25519 with the derived X25519 keyAgreement).
        var edPair = keyGen.Generate(KeyType.Ed25519);
        var created = await manager.CreateAsync(new NetDid.Method.Key.DidKeyCreateOptions
        {
            KeyType = KeyType.Ed25519,
            ExistingKey = new KeyPairSigner(edPair, crypto),
            EnableEncryptionKeyDerivation = true,
        });
        _didKey = created.Did.Value ?? throw new InvalidOperationException("did:key mint returned no DID.");

        _message = new MessageBuilder()
            .WithType("https://example.com/protocols/bench/1.0/ping")
            .WithFrom(_aliceDid)
            .WithTo(bob.Did)
            .WithBody(JsonNode.Parse("""{"note":"didcomm-dotnet NFR-07 benchmark payload"}""")!.AsObject())
            .Build();

        _anonOptions = new PackEncryptedOptions(Recipients: _bobRecipients);
        _authOptions = new PackEncryptedOptions(Recipients: _bobRecipients, From: _aliceDid);
        _packedAuthcrypt = (await _client.PackEncryptedAsync(_message, _authOptions)).Message;
    }

    [GlobalCleanup]
    public void Cleanup() => _sp.Dispose();

    /// <summary>NFR-07 target: &lt; 2 ms P99.</summary>
    [Benchmark]
    public Task<PackEncryptedResult> AnoncryptPack_1Recipient()
        => _client.PackEncryptedAsync(_message, _anonOptions);

    /// <summary>NFR-07 target: &lt; 3 ms P99.</summary>
    [Benchmark]
    public Task<PackEncryptedResult> AuthcryptPack_1Recipient()
        => _client.PackEncryptedAsync(_message, _authOptions);

    /// <summary>NFR-07 target: &lt; 2 ms P99 (authcrypt envelope, the most expensive unpack).</summary>
    [Benchmark]
    public Task<UnpackResult> Unpack_Authcrypt()
        => _client.UnpackAsync(_packedAuthcrypt);

    /// <summary>NFR-07 target: &lt; 0.1 ms P99 (in-process did:key parse + projection).</summary>
    [Benchmark]
    public Task<IReadOnlyList<Jwk>> Resolve_DidKey_KeyAgreement()
        => _keyService.GetVerificationMethodsAsync(_didKey, VerificationRelationship.KeyAgreement);
}
