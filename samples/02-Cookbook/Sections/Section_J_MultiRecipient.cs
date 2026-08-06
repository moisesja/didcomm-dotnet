using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Samples.Shared;
using DidComm.Secrets;
using DidComm.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Encrypts one message for several recipients at once. A DIDComm JWE encrypts the body a
/// single time and then wraps the same content-encryption key once per recipient, so sending
/// to Bob AND Carol costs one envelope — not two — and each of them decrypts it with their
/// own key, unaware of nothing: the recipient list is visible in the envelope, and the unpack
/// metadata surfaces every recipient key id.
/// </summary>
/// <remarks>
/// <para>
/// The section mints a third identity (Carol) alongside the shared Alice and Bob, packs one
/// authcrypt envelope to both recipients, counts the <c>recipients</c> entries on the wire,
/// and unpacks it — printing which key actually decrypted and the full
/// <c>AllRecipientKids</c> list so you can see both readers named in the one envelope.
/// </para>
/// <para>
/// Maps to PRD §14.2 task <strong>J</strong> (FR-API-01 — multi-recipient packing).
/// </para>
/// </remarks>
public static class Section_J_MultiRecipient
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("J", "Multi-recipient (one envelope, several readers)");

        // Mint Carol inside the section and hand her private keys to the shared secrets
        // resolver, the same way section O adds its mediator. The CookbookContext always
        // registers an InMemorySecretsResolver, so the cast is safe.
        var sp = ctx.ServiceProvider;
        var carol = await PeerIdentityFactory.CreateAsync(
            sp.GetRequiredService<IDidManager>(),
            sp.GetRequiredService<IKeyGenerator>(),
            sp.GetRequiredService<ICryptoProvider>());
        var secrets = (InMemorySecretsResolver)sp.GetRequiredService<ISecretsResolver>();
        foreach (var jwk in carol.Privates)
            secrets.Add(jwk);
        ctx.Narrator.Step($"Minted carol = {Truncate(carol.Did)}");

        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did, carol.Did)
            .WithBody(JsonNode.Parse("""{"content":"One envelope, two readers."}""")!.AsObject())
            .Build();

        ctx.Narrator.Step("Pack once for both recipients: the body is encrypted once, the key wrapped per recipient.");
        var packed = await ctx.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did, carol.Did },
            From: ctx.Alice.Did));

        // The envelope itself shows the fan-out: one 'recipients' entry per reader.
        var wireRecipients = JsonNode.Parse(packed.Message)!["recipients"]!.AsArray();
        ctx.Narrator.Value("Recipients on the wire", wireRecipients.Count);

        ctx.Narrator.Step("Unpack: the metadata names every addressed key, plus the one that actually decrypted here.");
        var unpacked = await ctx.Client.UnpackAsync(packed.Message);
        ctx.Narrator.Value("RecipientKid (decrypted with)", unpacked.RecipientKid);
        ctx.Narrator.Value("AllRecipientKids.Count", unpacked.AllRecipientKids.Count);
        foreach (var kid in unpacked.AllRecipientKids)
            ctx.Narrator.Value("AllRecipientKids[]", kid);

        ctx.Narrator.Note("Recipient DIDs are visible in the envelope — multi-recipient saves bytes, it does not hide the audience from each other.");
    }

    private static string Truncate(string did) => did.Length <= 64 ? did : did[..61] + "…";
}
