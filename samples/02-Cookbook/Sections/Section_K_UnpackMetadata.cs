using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Shows everything you learn about an incoming DIDComm message after unpacking it.
/// </summary>
/// <remarks>
/// <para>
/// The example sends a message from <c>alice</c> to <c>bob</c> using the most protective
/// envelope shape DIDComm supports: the body is encrypted so only Bob can read it, the
/// envelope authenticates Alice as the sender, and an inner signature gives Bob a
/// non-repudiable proof that Alice produced this exact content. That's three security
/// properties stacked in one packed message — which means the receive side has the richest
/// possible set of metadata to inspect.
/// </para>
/// <para>
/// On the receive side the section prints every property of <c>UnpackResult</c> in turn:
/// what kind of envelope arrived (<c>Encrypted</c>, <c>Authenticated</c>,
/// <c>NonRepudiation</c>, <c>AnonymousSender</c>), which JOSE algorithms were used, which
/// kid actually decrypted and verified, and the inner plaintext fields. Read the console
/// output alongside the source to see how each flag maps to a layer in the envelope.
/// </para>
/// <para>
/// Maps to PRD §14.2 task <strong>K</strong> (FR-API-04 — unpack metadata surface).
/// </para>
/// </remarks>
public static class Section_K_UnpackMetadata
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("K", "Unpack and inspect metadata");

        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"content":"Hi Bob — this is the metadata-rich envelope."}""")!.AsObject())
            .Build();

        // Build the maximally-protective shape: encrypt to bob with alice as the authenticated
        // sender, AND attach an inner signature so bob has non-repudiable proof Alice wrote this.
        ctx.Narrator.Step("Pack: encrypt for Bob, authenticate Alice as sender, add an inner signature.");
        var packed = await ctx.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice.Did,
            SignFrom: ctx.Alice.Did));

        ctx.Narrator.Step($"Unpack as Bob ({packed.Length} bytes on the wire).");
        var result = await ctx.Client.UnpackAsync(packed);

        // Print every UnpackResult property so the reader can see how each one maps to a layer.
        ctx.Narrator.Value("Encrypted",          result.Encrypted);          // body confidential? (JWE)
        ctx.Narrator.Value("Authenticated",      result.Authenticated);      // sender cryptographically named? (authcrypt)
        ctx.Narrator.Value("NonRepudiation",     result.NonRepudiation);     // sender provably signed? (JWS)
        ctx.Narrator.Value("AnonymousSender",    result.AnonymousSender);    // outer encrypt layer hid the sender? (anoncrypt)
        ctx.Narrator.Value("ContentEncryption",  result.ContentEncryption);  // body cipher (A256CBC-HS512 / A256GCM / XC20P)
        ctx.Narrator.Value("KeyWrap",            result.KeyWrap);            // ECDH-ES (anon) or ECDH-1PU (auth)
        ctx.Narrator.Value("SignatureAlgorithm", result.SignatureAlgorithm); // JWS alg of the inner signature
        ctx.Narrator.Value("SignerKid",          result.SignerKid);          // which Alice key signed
        ctx.Narrator.Value("SenderKid",          result.SenderKid);          // which Alice key authcrypted
        ctx.Narrator.Value("RecipientKid",       result.RecipientKid);       // which Bob key actually decrypted
        ctx.Narrator.Value("AllRecipientKids.Count", result.AllRecipientKids.Count);
        ctx.Narrator.Value("Stack",              string.Join(" ⊃ ", result.Stack));   // envelope shape, outermost first
        ctx.Narrator.Value("FromPrior",          result.FromPrior);          // populated only on rotation messages (Section N)
        ctx.Narrator.Value("Message.From",       result.Message.From);
        ctx.Narrator.Value("Message.Body[content]", result.Message.Body?["content"]);

        ctx.Narrator.Note("Three flags are true at once because three layers stack: the outer JWE gives Encrypted+Authenticated, the inner JWS adds NonRepudiation.");
    }
}
