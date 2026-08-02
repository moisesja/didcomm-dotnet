using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Combines both protections in one pack call: a signature inside an authcrypt envelope.
/// Setting <c>SignFrom</c> on the encrypted-pack options makes the library sign the plaintext
/// first (JWS) and then encrypt the signed form — so Bob gets confidentiality, sender
/// authentication, AND a transferable proof that Alice produced exactly this content.
/// </summary>
/// <remarks>
/// <para>
/// This is the shape for content someone may later need to hold the sender to: an approval,
/// an order, a consent record. Plain authcrypt (section F) is deniable by design; the inner
/// signature removes that deniability — Bob can show the verified signature to a third party.
/// Sign-then-encrypt (never encrypt-then-sign) is the only composition the spec allows, and
/// the one pack call gets the order right for you.
/// </para>
/// <para>
/// Maps to PRD §14.2 task <strong>G</strong> (FR-SIG-06 — sign-then-encrypt composition).
/// </para>
/// </remarks>
public static class Section_G_SignThenEncrypt
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("G", "Sign-then-encrypt (add non-repudiation)");

        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"content":"Secret, authenticated, and provable."}""")!.AsObject())
            .Build();

        ctx.Narrator.Step("PackEncryptedAsync with SignFrom set: sign the plaintext, then encrypt the signed form.");
        var packed = await ctx.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice.Did,
            SignFrom: ctx.Alice.Did));

        var unpacked = await ctx.Client.UnpackAsync(packed.Message);
        ctx.Narrator.Value("Encrypted", unpacked.Encrypted);
        ctx.Narrator.Value("Authenticated", unpacked.Authenticated);
        ctx.Narrator.Value("NonRepudiation", unpacked.NonRepudiation);  // the inner JWS verified
        ctx.Narrator.Value("SignerKid", unpacked.SignerKid);
        ctx.Narrator.Value("Stack", string.Join(" ⊃ ", unpacked.Stack)); // Encrypted ⊃ Signed ⊃ Plaintext

        ctx.Narrator.Note("Use SignFrom when the recipient may need transferable proof of authorship. Skip it when deniability is desirable — signatures are forever.");
    }
}
