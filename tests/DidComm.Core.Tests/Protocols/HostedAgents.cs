using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetCrypto;
using NetDid.Core;

namespace DidComm.Tests.Protocols;

/// <summary>How <see cref="HostedAgents.ReceiveAsync"/> packs Alice's message before the host unpacks it.</summary>
public enum AliceEnvelope
{
    Plaintext,
    SignedOnly,
    Authcrypt,
    AnoncryptOfAuthcrypt,
    Anoncrypt,
    AuthcryptOfSigned,
    AnoncryptOfSigned,
}

/// <summary>
/// Two real containers built with <c>AddDidComm</c> and did:peer identities: Alice's side holds only
/// Alice's keys; the host holds Bob's and Carol's (two tenants of one process) and runs the built-in
/// protocols on the DI-resolved <see cref="ProtocolDispatcher"/>. Messages travel through a real
/// pack on Alice's side and a real unpack on the host, so the dispatcher sees exactly what a
/// transport would hand it.
/// </summary>
internal sealed class HostedAgents : IAsyncDisposable
{
    private readonly ServiceProvider _alice;
    private readonly ServiceProvider _host;

    private HostedAgents(ServiceProvider alice, ServiceProvider host, string aliceDid, string bobDid, string carolDid)
    {
        _alice = alice;
        _host = host;
        Alice = aliceDid;
        Bob = bobDid;
        Carol = carolDid;
    }

    public string Alice { get; }

    public string Bob { get; }

    /// <summary>A second tenant of the host: its keys are held next to Bob's.</summary>
    public string Carol { get; }

    public DidCommClient AliceClient => _alice.GetRequiredService<DidCommClient>();

    public DidCommClient HostClient => _host.GetRequiredService<DidCommClient>();

    public IServiceProvider Host => _host;

    public ProtocolDispatcher Dispatcher => _host.GetRequiredService<ProtocolDispatcher>();

    public DidCommOptions HostOptions => _host.GetRequiredService<IOptions<DidCommOptions>>().Value;

    /// <param name="configureHost">Extra host registrations (handlers, options), applied before <c>AddDidComm</c> so they win over its TryAdd defaults.</param>
    public static async Task<HostedAgents> CreateAsync(Action<IServiceCollection>? configureHost = null)
    {
        var aliceSecrets = new InMemorySecretsResolver();
        var hostSecrets = new InMemorySecretsResolver();

        var minting = new ServiceCollection();
        minting.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
        });
        PeerIdentity alice, bob, carol;
        await using (var sp = minting.BuildServiceProvider())
        {
            var manager = sp.GetRequiredService<IDidManager>();
            var keys = sp.GetRequiredService<IKeyGenerator>();
            var crypto = sp.GetRequiredService<ICryptoProvider>();
            alice = await PeerIdentityFactory.CreateAsync(manager, keys, crypto);
            bob = await PeerIdentityFactory.CreateAsync(manager, keys, crypto);
            carol = await PeerIdentityFactory.CreateAsync(manager, keys, crypto);
        }
        foreach (var jwk in alice.Privates)
            aliceSecrets.Add(jwk);
        foreach (var jwk in bob.Privates.Concat(carol.Privates))
            hostSecrets.Add(jwk);

        var aliceServices = new ServiceCollection();
        aliceServices.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(aliceSecrets);
        });

        var hostServices = new ServiceCollection();
        configureHost?.Invoke(hostServices);
        hostServices.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(hostSecrets);
            b.AddBuiltInProtocols();
        });

        return new HostedAgents(aliceServices.BuildServiceProvider(), hostServices.BuildServiceProvider(), alice.Did, bob.Did, carol.Did);
    }

    /// <summary>Pack <paramref name="message"/> on Alice's side (JWE recipient: <paramref name="recipient"/>, Bob by default) and unpack it on the host.</summary>
    public async Task<UnpackResult> ReceiveAsync(Message message, AliceEnvelope envelope = AliceEnvelope.Authcrypt, string? recipient = null)
    {
        var to = new[] { recipient ?? Bob };
        var client = AliceClient;
        var packed = envelope switch
        {
            AliceEnvelope.Plaintext => await client.PackPlaintextAsync(message),
            AliceEnvelope.SignedOnly => await client.PackSignedAsync(message, Alice),
            AliceEnvelope.Authcrypt => (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: to, From: Alice))).Message,
            AliceEnvelope.AnoncryptOfAuthcrypt => (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: to, From: Alice, ProtectSender: true))).Message,
            AliceEnvelope.Anoncrypt => (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: to))).Message,
            AliceEnvelope.AuthcryptOfSigned => (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: to, From: Alice, SignFrom: Alice))).Message,
            AliceEnvelope.AnoncryptOfSigned => (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: to, SignFrom: Alice))).Message,
            _ => throw new ArgumentOutOfRangeException(nameof(envelope)),
        };
        return await HostClient.UnpackAsync(packed);
    }

    /// <summary>Pack and unpack on the host, then dispatch on the host's DI-resolved dispatcher.</summary>
    public async Task<DispatchOutcome> SendToHostAsync(Message message, AliceEnvelope envelope = AliceEnvelope.Authcrypt)
        => await Dispatcher.DispatchAsync(await ReceiveAsync(message, envelope), HostClient, HostOptions);

    /// <summary>Authcrypt <paramref name="reply"/> on the host from its own <c>from</c> to its <c>to</c>, and unpack it on Alice's side.</summary>
    public async Task<UnpackResult> ReplyToAliceAsync(Message reply)
    {
        var packed = await HostClient.PackEncryptedAsync(reply, new PackEncryptedOptions(Recipients: reply.To!.ToArray(), From: reply.From));
        return await AliceClient.UnpackAsync(packed.Message);
    }

    public async ValueTask DisposeAsync()
    {
        await _alice.DisposeAsync();
        await _host.DisposeAsync();
    }
}
