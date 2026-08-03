using System.Text.Json.Nodes;
using DidComm.Messages;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Packs a message as bare plaintext — the JWM form with no encryption and no signature.
/// Useful for debugging, logging, and inspecting what the inner payload of every other
/// envelope shape looks like; never appropriate for anything you actually send to a peer.
/// </summary>
/// <remarks>
/// <para>
/// A plaintext pack has no confidentiality (anyone on the path reads it), no sender
/// authenticity (anyone can claim any <c>from</c>), and no integrity (anyone can rewrite it
/// in flight). The section round-trips the plaintext through <c>UnpackAsync</c> so you can
/// see all three security flags come back <c>false</c> — the receive side tells you exactly
/// how little a plaintext message proves.
/// </para>
/// <para>
/// Maps to PRD §14.2 task <strong>C</strong> (FR-API-01 — plaintext pack; media type
/// <c>application/didcomm-plain+json</c>).
/// </para>
/// </remarks>
public static class Section_C_PackPlaintext
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("C", "Pack plaintext (debug/inspection only)");

        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"content":"Readable by anyone on the path."}""")!.AsObject())
            .Build();

        ctx.Narrator.Step("PackPlaintextAsync emits the message as-is — application/didcomm-plain+json.");
        var plain = await ctx.Client.PackPlaintextAsync(message);
        ctx.Narrator.Value("PackedMessage", plain);

        // Unpack it to see what the receive side can (and cannot) conclude.
        var unpacked = await ctx.Client.UnpackAsync(plain);
        ctx.Narrator.Value("Encrypted", unpacked.Encrypted);              // nobody's confidentiality
        ctx.Narrator.Value("Authenticated", unpacked.Authenticated);      // 'from' is just a claim
        ctx.Narrator.Value("NonRepudiation", unpacked.NonRepudiation);    // nothing was signed

        // Transports label DIDComm payloads with a media type. Peers may send it with parameters
        // or stray casing, so compare through MediaTypes: Normalize strips parameters/whitespace
        // and lowercases, and Matches does the tolerant comparison against a canonical constant
        // in one call. (FR-TRN-04)
        ctx.Narrator.Step("Recognize the media type tolerantly with MediaTypes.Normalize / Matches.");
        var received = "Application/DIDComm-Plain+JSON; charset=utf-8";
        ctx.Narrator.Value("Normalize(received)", MediaTypes.Normalize(received));
        ctx.Narrator.Value("Matches(received, Plaintext)", MediaTypes.Matches(received, MediaTypes.Plaintext));

        ctx.Narrator.Note("Plaintext is for debugging and inspection only: no confidentiality, no authenticity, no integrity. Send authcrypt (section F) in production.");
    }
}
