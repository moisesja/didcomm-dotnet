using System.IO;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NetDid.Core;
using NetDid.Core.Crypto;

namespace DidComm.Samples.Cookbook;

/// <summary>
/// The setup every cookbook section needs. Builds the DI container, mints three test
/// identities — <c>alice</c>, <c>bob</c>, and <c>alice2</c> (the identity Alice rotates to
/// in the rotation example) — loads their private keys into an in-memory secrets resolver,
/// and resolves the <see cref="DidCommClient"/> sections will use to pack and unpack.
/// </summary>
/// <remarks>
/// In a real application this work happens once at startup. The cookbook does it once per
/// run because every section uses the same Alice and Bob.
/// </remarks>
public sealed class CookbookContext : IAsyncDisposable
{
    private readonly ServiceProvider _serviceProvider;

    /// <summary>The Cookbook narrator (writes section banners + key=value frames).</summary>
    public Narrator Narrator { get; }

    /// <summary>Alice's original identity — the sender in the metadata and did:web sections.</summary>
    public PeerIdentity Alice { get; }

    /// <summary>Bob's identity — the recipient in every section.</summary>
    public PeerIdentity Bob { get; }

    /// <summary>The identity Alice rotates to in the rotation section. Holds its own key material.</summary>
    public PeerIdentity Alice2 { get; }

    /// <summary>The wired <see cref="DidCommClient"/>.</summary>
    public DidCommClient Client { get; }

    /// <summary>
    /// The DI container hosting the facade. Exposed so a section that needs additional
    /// services from the same graph (e.g. section O builds a per-section
    /// <see cref="DidCommClient"/> with a custom <see cref="DidComm.Resolution.IServiceEndpointResolver"/>)
    /// doesn't have to bootstrap a parallel container.
    /// </summary>
    public IServiceProvider ServiceProvider => _serviceProvider;

    private CookbookContext(
        ServiceProvider sp,
        Narrator narrator,
        PeerIdentity alice,
        PeerIdentity bob,
        PeerIdentity alice2,
        DidCommClient client)
    {
        _serviceProvider = sp;
        Narrator = narrator;
        Alice = alice;
        Bob = bob;
        Alice2 = alice2;
        Client = client;
    }

    /// <summary>Bootstrap a new context. <paramref name="output"/> overrides the default console writer.</summary>
    /// <param name="output">Where narrator output is written. <c>null</c> ⇒ <see cref="Console.Out"/>.</param>
    public static async Task<CookbookContext> BuildAsync(TextWriter? output = null)
    {
        var narrator = output is null ? new Narrator() : new Narrator(output);
        var secrets = new InMemorySecretsResolver();

        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
        });

        var sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<IDidManager>();
        var keyGen = sp.GetRequiredService<IKeyGenerator>();
        var crypto = sp.GetRequiredService<ICryptoProvider>();

        var alice = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        var bob = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        var alice2 = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);

        foreach (var jwk in alice.Privates) secrets.Add(jwk);
        foreach (var jwk in bob.Privates) secrets.Add(jwk);
        foreach (var jwk in alice2.Privates) secrets.Add(jwk);

        var client = sp.GetRequiredService<DidCommClient>();
        return new CookbookContext(sp, narrator, alice, bob, alice2, client);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _serviceProvider.DisposeAsync();
}
