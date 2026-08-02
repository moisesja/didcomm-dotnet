using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Hides who is talking, not just what is said. A plain authcrypt envelope names the sender's
/// key id (<c>skid</c>) in its outer, unencrypted JOSE header — Bob needs it to decrypt, but
/// it also means every mediator and network observer carrying the envelope learns which of
/// Alice's keys (and therefore which DID) sent it. <c>ProtectSender = true</c> wraps the
/// authcrypt envelope in an outer anoncrypt layer, moving the <c>skid</c> inside the
/// ciphertext where only Bob can see it.
/// </summary>
/// <remarks>
/// <para>
/// The section packs the same message twice — plain authcrypt and protected — and decodes
/// the outermost JOSE header of each so you can see the difference on the wire: the plain
/// envelope leaks <c>skid</c> to anyone holding the bytes; the protected envelope's outer
/// header is anonymous (ECDH-ES, no <c>skid</c>). Bob's unpack is unchanged — he peels the
/// anonymous layer, finds the authcrypt inside, and still gets <c>Authenticated = true</c>.
/// The parties being blinded are the ones in the middle: mediators and observers.
/// </para>
/// <para>
/// Maps to PRD §14.2 task <strong>H</strong> (anoncrypt(authcrypt(...)) — hide skid from mediators).
/// </para>
/// </remarks>
public static class Section_H_ProtectSender
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("H", "Protect the sender (anoncrypt wraps authcrypt)");

        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"content":"Mediators shouldn't learn who is talking."}""")!.AsObject())
            .Build();

        // Baseline first: plain authcrypt. Its outer protected header carries 'skid' in the
        // clear — that's Alice's key id, visible to every hop between her and Bob.
        ctx.Narrator.Step("Plain authcrypt: decode the outer JOSE header any mediator can read.");
        var plain = await ctx.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice.Did));
        var plainHeader = DecodeOuterProtectedHeader(plain.Message);
        ctx.Narrator.Value("Outer alg", plainHeader["alg"]?.GetValue<string>());
        ctx.Narrator.Value("Outer skid", plainHeader["skid"]?.GetValue<string>());

        // Now the same message with ProtectSender: the authcrypt envelope (skid and all) is
        // itself encrypted under an outer anoncrypt layer.
        ctx.Narrator.Step("ProtectSender = true: the outer header is now anonymous — skid moved inside the ciphertext.");
        var hidden = await ctx.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice.Did,
            ProtectSender: true));
        var hiddenHeader = DecodeOuterProtectedHeader(hidden.Message);
        ctx.Narrator.Value("Outer alg", hiddenHeader["alg"]?.GetValue<string>());       // ECDH-ES: anonymous
        ctx.Narrator.Value("Outer skid", hiddenHeader["skid"]?.GetValue<string>());     // gone from the outside

        // Bob is unaffected: he unwraps both layers and still verifies Alice as the sender.
        var unpacked = await ctx.Client.UnpackAsync(hidden.Message);
        ctx.Narrator.Value("Authenticated", unpacked.Authenticated);
        ctx.Narrator.Value("AnonymousSender", unpacked.AnonymousSender);   // describes the OUTER layer
        ctx.Narrator.Value("Stack", string.Join(" ⊃ ", unpacked.Stack));   // two Encrypted layers

        ctx.Narrator.Note("ProtectSender hides WHO from the middle (mediators, observers), not from Bob — he still authenticates Alice after peeling the outer layer.");
    }

    /// <summary>Parse a packed JWE (general JSON serialization) and decode its base64url 'protected' header.</summary>
    private static JsonObject DecodeOuterProtectedHeader(string packedJwe)
    {
        var envelope = JsonNode.Parse(packedJwe)!.AsObject();
        var protectedB64 = envelope["protected"]!.GetValue<string>();
        var padded = protectedB64.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return JsonNode.Parse(Convert.FromBase64String(padded))!.AsObject();
    }
}
