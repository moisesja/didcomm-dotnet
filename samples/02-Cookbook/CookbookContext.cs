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
/// One-time DI + identity bootstrap shared by every cookbook section. Mints three peer
/// identities (<c>alice</c>, <c>bob</c>, and <c>alice2</c> — the post-rotation identity used
/// by Section N), seeds an <see cref="InMemorySecretsResolver"/>, and constructs the
/// <see cref="DidCommClient"/> via <c>AddDidComm(...)</c>.
/// </summary>
public sealed class CookbookContext : IAsyncDisposable
{
    private readonly ServiceProvider _serviceProvider;

    /// <summary>The Cookbook narrator (writes section banners + key=value frames).</summary>
    public Narrator Narrator { get; }

    /// <summary>Alice's pre-rotation identity (sender / signer in Sections K and AA).</summary>
    public PeerIdentity Alice { get; }

    /// <summary>Bob's identity (recipient in every section).</summary>
    public PeerIdentity Bob { get; }

    /// <summary>Alice's post-rotation identity (Section N — <c>from_prior</c>).</summary>
    public PeerIdentity Alice2 { get; }

    /// <summary>The wired <see cref="DidCommClient"/>.</summary>
    public DidCommClient Client { get; }

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
