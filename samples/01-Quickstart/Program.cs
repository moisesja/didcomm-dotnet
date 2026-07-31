using System.Text.Json.Nodes;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;

namespace DidComm.Samples.Quickstart;

/// <summary>
/// The README quickstart, as a project that actually compiles and runs (FR-DX-05).
/// <see cref="RunAsync"/> is the quickstart verbatim — what the README shows is this method's
/// body, so the two cannot drift. It returns its <see cref="UnpackResult"/> so the smoke test
/// can assert on the outcome without capturing console output.
/// </summary>
public static class Program
{
    /// <summary>CLI entry point.</summary>
    public static async Task<int> Main()
    {
        try
        {
            await RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Quickstart failed: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// Mint two <c>did:peer:2</c> identities, send Alice → Bob as authcrypt (confidential *and*
    /// sender-authenticated — the default you want), unpack it, and print what the envelope proved.
    /// </summary>
    /// <returns>The unpack result, so callers can assert on it.</returns>
    public static async Task<UnpackResult> RunAsync()
    {
        // An in-memory secrets resolver stands in for your KMS/HSM. Nothing here touches a network:
        // did:peer:2 encodes its own DID document, so resolution is a local parse.
        var secrets = new InMemorySecretsResolver();
        var services = new ServiceCollection();
        services.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(secrets));
        await using var sp = services.BuildServiceProvider();

        var alice = await PeerIdentityFactory.CreateAsync(
            sp.GetRequiredService<IDidManager>(), sp.GetRequiredService<IKeyGenerator>(), sp.GetRequiredService<ICryptoProvider>());
        var bob = await PeerIdentityFactory.CreateAsync(
            sp.GetRequiredService<IDidManager>(), sp.GetRequiredService<IKeyGenerator>(), sp.GetRequiredService<ICryptoProvider>());
        foreach (var key in alice.Privates.Concat(bob.Privates))
            secrets.Add(key);

        var client = sp.GetRequiredService<DidCommClient>();
        var message = new MessageBuilder()
            .WithType("https://example.com/protocols/hello/1.0/greeting")
            .WithFrom(alice.Did)
            .WithTo(bob.Did)
            .WithBody(new JsonObject { ["text"] = "Hello, Bob." })
            .Build();

        var packed = await client.PackEncryptedAsync(message, new PackEncryptedOptions([bob.Did], From: alice.Did));
        var received = await client.UnpackAsync(packed.Message);

        Console.WriteLine($"body          : {received.Message.Body}");
        Console.WriteLine($"authenticated : {received.Authenticated}");   // the sender proved control of Alice's key
        Console.WriteLine($"encrypted     : {received.Encrypted}");
        Console.WriteLine($"sender kid    : {received.SenderKid}");
        Console.WriteLine($"addressed to  : {received.RecipientAddressing}");

        return received;
    }
}
