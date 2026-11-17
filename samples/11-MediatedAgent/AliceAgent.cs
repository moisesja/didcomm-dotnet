using System.Text.Json.Nodes;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;

namespace DidComm.Samples.MediatedAgent;

/// <summary>
/// Alice: an ordinary DIDComm agent with the HTTP transport. She knows nothing about mediators;
/// everything she needs to reach Bob is in Bob's DID.
/// </summary>
internal sealed class AliceAgent : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private AliceAgent(ServiceProvider services, string did)
    {
        _services = services;
        Did = did;
        Client = services.GetRequiredService<DidCommClient>();
    }

    public string Did { get; }

    public DidCommClient Client { get; }

    public static async Task<AliceAgent> CreateAsync()
    {
        var secrets = new InMemorySecretsResolver();
        var services = new ServiceCollection();
        services.AddDidComm(b => b
            .UseNetDidResolver()
            .UseSecretsResolver(secrets)
            // Plain http only because this demo runs on loopback; production stays on https.
            .UseHttpTransport(o => o.AllowedSchemes = new[] { "http", "https" })
            // The address Alice posts to comes from Bob's DID (and the mediator's): data a stranger
            // chose. The outbound guard refuses loopback and private addresses unless allowed;
            // open exactly this one host.
            .Configure(o => o.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1")));
        var provider = services.BuildServiceProvider();
        try
        {
            var identity = await PeerIdentityFactory.CreateAsync(
                provider.GetRequiredService<IDidManager>(),
                provider.GetRequiredService<IKeyGenerator>(),
                provider.GetRequiredService<ICryptoProvider>()).ConfigureAwait(false);
            foreach (var key in identity.Privates)
                secrets.Add(key);
            return new AliceAgent(provider, identity.Did);
        }
        catch
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>What Alice's resolver reads from a DID before anything is sent: its DIDComm service.</summary>
    /// <param name="did">The DID to look up.</param>
    public async Task<DidCommServiceInfo> ResolveServiceAsync(string did)
        => (await _services.GetRequiredService<IServiceEndpointResolver>().ResolveAsync(did).ConfigureAwait(false))[0];

    /// <summary>Send a basic message, authcrypted, to <paramref name="to"/>.</summary>
    /// <param name="to">The recipient's DID.</param>
    /// <param name="content">The text.</param>
    public Task<SendResult> SendAsync(string to, string content)
    {
        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(Did)
            .WithTo(to)
            .WithBody(new JsonObject { ["content"] = content })
            .Build();
        // Forward is on by default: SendAsync reads Bob's service, sees a mediator's DID there,
        // wraps one forward for the mediator and posts it to the mediator's own address.
        return Client.SendAsync(message, new SendOptions(Recipients: new[] { to }, From: Did));
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
