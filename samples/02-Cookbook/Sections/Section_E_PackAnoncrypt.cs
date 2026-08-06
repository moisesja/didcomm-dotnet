using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Packs an anoncrypt envelope: encrypted so only Bob can read it, but with no sender
/// identity anywhere in the envelope. Selecting it is a single decision — leave <c>From</c>
/// out of the pack options and the library derives the anonymous key agreement (ECDH-ES)
/// instead of the authenticated one.
/// </summary>
/// <remarks>
/// <para>
/// Anoncrypt is for the moments when the sender has no DID relationship with the recipient
/// yet (a first contact) or explicitly must not be identifiable. The trade-off is spoofable
/// origin: Bob learns nothing about who sent the message, so any <c>from</c>-like claim in
/// the body is unverified. The unpack metadata makes this explicit —
/// <c>AnonymousSender</c> is <c>true</c> and <c>Authenticated</c> is <c>false</c>.
/// </para>
/// <para>
/// Maps to PRD §14.2 task <strong>E</strong> (FR-MSG-08 — From omitted ⇒ anoncrypt).
/// </para>
/// </remarks>
public static class Section_E_PackAnoncrypt
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("E", "Pack anoncrypt (confidential, anonymous sender)");

        // No 'from' on the message and no From in the options — that omission IS the
        // anoncrypt selection (FR-MSG-08).
        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"content":"For Bob's eyes, from nobody in particular."}""")!.AsObject())
            .Build();

        ctx.Narrator.Step("PackEncryptedAsync with From = null ⇒ anoncrypt (ECDH-ES key agreement).");
        var packed = await ctx.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did }));

        var unpacked = await ctx.Client.UnpackAsync(packed.Message);
        ctx.Narrator.Value("Encrypted", unpacked.Encrypted);
        ctx.Narrator.Value("AnonymousSender", unpacked.AnonymousSender);   // the envelope names no sender
        ctx.Narrator.Value("Authenticated", unpacked.Authenticated);       // so Bob cannot verify one
        ctx.Narrator.Value("KeyWrap", unpacked.KeyWrap);                   // ECDH-ES = the anonymous derivation

        ctx.Narrator.Note("Use anoncrypt for first contact or when the sender must stay anonymous; anything claiming an origin inside the body is unverified.");
    }
}
