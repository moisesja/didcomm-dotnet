using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetCrypto;
using NetDid.Core;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>
/// A real DI container with <c>AddCoordinateMediation()</c>, real did:peer identities and the real
/// <see cref="ProtocolDispatcher"/> — no transport. One container plays every party: it holds all
/// private keys, so it can pack as Alice and unpack as the mediator.
/// </summary>
public sealed class MediatorTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private MediatorTestHost(ServiceProvider provider, InMemorySecretsResolver secrets)
    {
        _provider = provider;
        Secrets = secrets;
    }

    public IServiceProvider Services => _provider;
    public InMemorySecretsResolver Secrets { get; }
    public DidCommClient Client => _provider.GetRequiredService<DidCommClient>();
    public ProtocolDispatcher Dispatcher => _provider.GetRequiredService<ProtocolDispatcher>();
    public DidCommOptions Options => _provider.GetRequiredService<IOptions<DidCommOptions>>().Value;
    public IMediationStore Store => _provider.GetRequiredService<IMediationStore>();

    public static MediatorTestHost Create(
        Action<CoordinateMediationOptions>? configure = null,
        Action<IServiceCollection>? before = null,
        Action<DidCommBuilder>? after = null,
        bool builtIns = true)
    {
        var secrets = new InMemorySecretsResolver();
        var services = new ServiceCollection();
        before?.Invoke(services);
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            if (builtIns)
                b.AddBuiltInProtocols();
            b.AddCoordinateMediation(configure);
            after?.Invoke(b);
        });
        return new MediatorTestHost(services.BuildServiceProvider(), secrets);
    }

    public async Task<PeerIdentity> NewPeerAsync()
    {
        var peer = await PeerIdentityFactory.CreateAsync(
            _provider.GetRequiredService<IDidManager>(),
            _provider.GetRequiredService<IKeyGenerator>(),
            _provider.GetRequiredService<ICryptoProvider>());
        foreach (var jwk in peer.Privates)
            Secrets.Add(jwk);
        return peer;
    }

    /// <summary>Authcrypt <paramref name="message"/> from its <c>from</c> to <paramref name="mediatorDid"/>, unpack it as the mediator and dispatch it.</summary>
    public async Task<DispatchOutcome> SendAuthcryptAsync(Message message, string mediatorDid)
    {
        var packed = await Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { mediatorDid }, From: message.From));
        var unpacked = await Client.UnpackAsync(packed.Message);
        return await Dispatcher.DispatchAsync(unpacked, Client, Options);
    }

    /// <summary>Anoncrypt <paramref name="message"/> to <paramref name="mediatorDid"/> (no sender key), unpack and dispatch it.</summary>
    public async Task<DispatchOutcome> SendAnoncryptAsync(Message message, string mediatorDid)
    {
        var packed = await Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { mediatorDid }));
        var unpacked = await Client.UnpackAsync(packed.Message);
        return await Dispatcher.DispatchAsync(unpacked, Client, Options);
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
