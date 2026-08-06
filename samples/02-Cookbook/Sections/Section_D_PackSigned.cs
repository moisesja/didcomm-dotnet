using System.Text.Json.Nodes;
using DidComm.Messages;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Packs a standalone signed envelope: a JWS over the plaintext, made with a key the sender's
/// DID advertises for authentication. Anyone can read the message, but everyone can also
/// verify — and later prove to a third party — that the sender produced exactly this content.
/// That property is non-repudiation, and it is the whole point of this shape: use it when the
/// message's origin must be provable (a public attestation, a signed invitation), not when
/// the content is secret.
/// </summary>
/// <remarks>
/// <para>
/// The message deliberately carries a <c>to</c> header. A signed envelope names no recipient
/// of its own, so without <c>to</c> anyone holding it could forward it to an audience the
/// signer never addressed and the signature would still verify. The spec says a standalone
/// signed message SHOULD carry <c>to</c>; the client still packs one without it, but emits a
/// structured warning through your logger (FR-SIG-05).
/// </para>
/// <para>
/// Maps to PRD §14.2 task <strong>D</strong> (FR-API-01, FR-SIG-05).
/// </para>
/// </remarks>
public static class Section_D_PackSigned
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("D", "Pack signed (non-repudiable, no confidentiality)");

        // 'to' is included on purpose — a signed message without it can be surreptitiously
        // forwarded to an unintended audience, so the client logs a warning when it's missing
        // (FR-SIG-05: SHOULD, not MUST — the pack still succeeds).
        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"content":"Alice provably said this."}""")!.AsObject())
            .Build();

        ctx.Narrator.Step("PackSignedAsync: the library picks a private key authorized under Alice's 'authentication' relationship.");
        var packed = await ctx.Client.PackSignedAsync(message, signFrom: ctx.Alice.Did);
        ctx.Narrator.Value("PackedBytes", packed.Length);
        ctx.Narrator.Value("Head", packed.Length <= 76 ? packed : packed[..76] + "…");

        var unpacked = await ctx.Client.UnpackAsync(packed);
        ctx.Narrator.Value("NonRepudiation", unpacked.NonRepudiation);        // provable authorship
        ctx.Narrator.Value("Encrypted", unpacked.Encrypted);                  // ...but everyone can read it
        ctx.Narrator.Value("SignatureAlgorithm", unpacked.SignatureAlgorithm);
        ctx.Narrator.Value("SignerKid", unpacked.SignerKid);

        ctx.Narrator.Note("Signed-only means public: no confidentiality. For secret AND provable content, sign inside encryption (section G).");
    }
}
