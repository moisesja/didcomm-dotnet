using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// PRD §14.2 — <strong>K. Unpack and inspect metadata (FR-API-04)</strong>.
/// Packs an authcrypt-with-inner-sign envelope alice → bob (the most metadata-rich legal
/// composition) and prints every field on the resulting <see cref="UnpackResult"/>.
/// </summary>
public static class Section_K_UnpackMetadata
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("K", "Unpack and inspect metadata (FR-API-04)");

        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"content":"Hi Bob — this is the metadata-rich envelope."}""")!.AsObject())
            .Build();

        ctx.Narrator.Step("Pack authcrypt(sign(plaintext)) — alice→bob with inner JWS by alice.");
        var packed = await ctx.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice.Did,
            SignFrom: ctx.Alice.Did));

        ctx.Narrator.Step($"Unpack as bob ({packed.Length} bytes on the wire).");
        var result = await ctx.Client.UnpackAsync(packed);

        ctx.Narrator.Value("Encrypted", result.Encrypted);
        ctx.Narrator.Value("Authenticated", result.Authenticated);
        ctx.Narrator.Value("NonRepudiation", result.NonRepudiation);
        ctx.Narrator.Value("AnonymousSender", result.AnonymousSender);
        ctx.Narrator.Value("ContentEncryption", result.ContentEncryption);
        ctx.Narrator.Value("KeyWrap", result.KeyWrap);
        ctx.Narrator.Value("SignatureAlgorithm", result.SignatureAlgorithm);
        ctx.Narrator.Value("SignerKid", result.SignerKid);
        ctx.Narrator.Value("SenderKid", result.SenderKid);
        ctx.Narrator.Value("RecipientKid", result.RecipientKid);
        ctx.Narrator.Value("AllRecipientKids.Count", result.AllRecipientKids.Count);
        ctx.Narrator.Value("Stack", string.Join(" ⊃ ", result.Stack));
        ctx.Narrator.Value("FromPrior", result.FromPrior);
        ctx.Narrator.Value("Message.From", result.Message.From);
        ctx.Narrator.Value("Message.Body[content]", result.Message.Body?["content"]);

        ctx.Narrator.Note("Encrypted=true (outer JWE) + Authenticated=true (authcrypt 1PU) + NonRepudiation=true (inner JWS).");
    }
}
