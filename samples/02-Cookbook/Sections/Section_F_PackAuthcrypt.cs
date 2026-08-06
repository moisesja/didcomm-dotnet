using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Packs an authcrypt envelope — the default posture for DIDComm traffic between parties who
/// know each other. One layer gives you both properties at once: only Bob can read the
/// message, and Bob can cryptographically verify Alice sent it (the ECDH-1PU key agreement
/// mixes Alice's static key into the decryption, so a successful decrypt IS the sender
/// check).
/// </summary>
/// <remarks>
/// <para>
/// Authcrypt authenticates Alice <em>to Bob</em> without being provable to anyone else —
/// Bob cannot take the envelope to a third party as evidence, because Bob himself could have
/// forged it (he holds the other half of the shared secret). That deniability is a feature;
/// when you need transferable proof instead, add a signature (section G).
/// </para>
/// <para>
/// Maps to PRD §14.2 task <strong>F</strong> (FR-API-01 — authcrypt; ECDH-1PU).
/// </para>
/// </remarks>
public static class Section_F_PackAuthcrypt
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("F", "Pack authcrypt (confidential + sender authenticated — the default)");

        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"content":"Only Bob reads this, and Bob knows it's Alice."}""")!.AsObject())
            .Build();

        ctx.Narrator.Step("PackEncryptedAsync with From set ⇒ authcrypt (ECDH-1PU key agreement).");
        var options = new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice.Did);

        // The options record reads back exactly what you asked for — worth logging next to the
        // pack call, since the unset members show the defaults you accepted: A256CBC-HS512
        // content encryption, no inner signature, no anoncrypt wrap, no mediator forwarding.
        ctx.Narrator.Value("Options.Recipients", string.Join(", ", options.Recipients));
        ctx.Narrator.Value("Options.From", options.From);
        ctx.Narrator.Value("Options.SignFrom (null ⇒ no inner signature)", options.SignFrom);
        ctx.Narrator.Value("Options.Enc (default cipher)", options.Enc);
        ctx.Narrator.Value("Options.ProtectSender (anoncrypt wrap?)", options.ProtectSender);
        ctx.Narrator.Value("Options.Forward (wrap for mediators?)", options.Forward);

        var packed = await ctx.Client.PackEncryptedAsync(message, options);

        var unpacked = await ctx.Client.UnpackAsync(packed.Message);
        ctx.Narrator.Value("Encrypted", unpacked.Encrypted);
        ctx.Narrator.Value("Authenticated", unpacked.Authenticated);   // decrypting proved the sender
        ctx.Narrator.Value("AnonymousSender", unpacked.AnonymousSender);
        ctx.Narrator.Value("KeyWrap", unpacked.KeyWrap);               // ECDH-1PU = the authenticated derivation
        ctx.Narrator.Value("SenderKid", unpacked.SenderKid);           // which Alice key vouched

        ctx.Narrator.Note("Reach for authcrypt by default. It authenticates Alice to Bob only — deniable, not provable to third parties (that's section G).");
    }
}
