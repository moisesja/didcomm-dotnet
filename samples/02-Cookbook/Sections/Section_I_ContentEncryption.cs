using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Chooses the content-encryption cipher explicitly. Every encrypted pack takes an
/// <c>Enc</c> option with three values — <c>A256CbcHs512</c> (the default, and the only one
/// legal for authcrypt), <c>A256Gcm</c>, and <c>XC20P</c> — and the section shows both the
/// happy path (an anoncrypt envelope packed with each alternative) and the guard rail: asking
/// for GCM on an authcrypt envelope is refused at pack time with an error naming the rule.
/// </summary>
/// <remarks>
/// <para>
/// Why the refusal? Authcrypt's ECDH-1PU key agreement is only specified over the
/// CBC-with-HMAC family; pairing it with a GCM/XC20P content cipher would step outside the
/// proof the construction rests on, so the spec forbids the combination and the library
/// enforces it before any crypto runs (FR-ENC-09). If you never touch <c>Enc</c>, the default
/// is correct for every shape.
/// </para>
/// <para>
/// Maps to PRD §14.2 task <strong>I</strong> (FR-ENC-05..09 — content-encryption selection).
/// </para>
/// </remarks>
public static class Section_I_ContentEncryption
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("I", "Choose content encryption explicitly");

        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"content":"Same payload, three ciphers."}""")!.AsObject())
            .Build();

        // Anoncrypt accepts all three ContentEncryptionAlgorithm values. Pack with the two
        // non-default ones and read the negotiated cipher back off the unpack metadata.
        ctx.Narrator.Step("Anoncrypt with each explicit cipher choice.");
        foreach (var enc in new[] { ContentEncryptionAlgorithm.A256Gcm, ContentEncryptionAlgorithm.XC20P })
        {
            var packed = await ctx.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
                Recipients: new[] { ctx.Bob.Did },
                Enc: enc));
            var unpacked = await ctx.Client.UnpackAsync(packed.Message);
            ctx.Narrator.Value($"Enc: {enc}", unpacked.ContentEncryption);
        }

        // The default needs no option at all — and it is the one authcrypt requires.
        ctx.Narrator.Step("Authcrypt with the default (A256CbcHs512) — the only cipher authcrypt allows.");
        var authMessage = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"content":"Default cipher."}""")!.AsObject())
            .Build();
        var auth = await ctx.Client.PackEncryptedAsync(authMessage, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice.Did));
        ctx.Narrator.Value("Authcrypt default enc", (await ctx.Client.UnpackAsync(auth.Message)).ContentEncryption);

        // The guard rail: GCM (or XC20P) with authcrypt is rejected before any crypto runs.
        ctx.Narrator.Step("Authcrypt + A256Gcm is a forbidden combination — the pack call refuses it.");
        try
        {
            await ctx.Client.PackEncryptedAsync(authMessage, new PackEncryptedOptions(
                Recipients: new[] { ctx.Bob.Did },
                From: ctx.Alice.Did,
                Enc: ContentEncryptionAlgorithm.A256Gcm));
            ctx.Narrator.Note("UNEXPECTED: the forbidden combination was not refused.");
        }
        catch (InvalidOperationException ex)
        {
            ctx.Narrator.Note($"Refused as designed: {ex.Message}");
        }

        ctx.Narrator.Note("Leave Enc alone unless a peer requires a specific anoncrypt cipher — the default is valid for every envelope shape.");
    }
}
