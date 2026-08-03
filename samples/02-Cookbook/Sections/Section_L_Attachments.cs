using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Carries payloads alongside a message with the three attachment shapes DIDComm defines:
/// inline JSON (structured data right in the message), inline base64 (arbitrary bytes), and
/// linked-with-hash (the content lives at a URL; the message carries a digest so the fetched
/// bytes can be integrity-checked). All three ride through an encrypted round-trip untouched.
/// </summary>
/// <remarks>
/// <para>
/// Choose by size and locality: inline JSON for small structured payloads, base64 for small
/// binary ones, and links for anything big — the envelope stays light and the hash keeps the
/// out-of-band fetch honest. The pairing of <c>links</c> with <c>hash</c> is not a style
/// choice: a link without a digest is an invitation to content substitution, so the library
/// refuses to build one (FR-ATT-03) — the section shows that refusal as a caught error.
/// </para>
/// <para>
/// Maps to PRD §14.2 task <strong>L</strong> (FR-ATT-02/03/04 — attachment shapes and validation).
/// </para>
/// </remarks>
public static class Section_L_Attachments
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("L", "Attachments (inline json / base64 / linked-with-hash)");

        // Shape 1 — inline JSON: structured data travels as-is inside the message.
        var report = new Attachment
        {
            Id = "report",
            MediaType = "application/json",
            Data = new AttachmentData { Json = JsonNode.Parse("""{"total":42}""") },
        };

        // Shape 2 — inline base64: arbitrary bytes, base64url-encoded per the JOSE family.
        // This one also carries the optional descriptive headers — a human-readable description,
        // the filename to save under, a format hint for when the media type alone is ambiguous,
        // and the last-modified time of the original file (FR-ATT-02).
        var logoBytes = Encoding.UTF8.GetBytes("pretend-this-is-a-png");
        var logo = new Attachment
        {
            Id = "logo",
            Description = "The ACME logo, as discussed",
            Filename = "acme-logo.png",
            MediaType = "image/png",
            Format = "https://example.org/formats/logo-v1",
            LastModifiedTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ByteCount = logoBytes.Length,
            Data = new AttachmentData { Base64 = Base64UrlEncode(logoBytes) },
        };

        // Vendor-specific extras the spec doesn't define ride in AdditionalData — on the
        // attachment and on its data object alike — and survive the wire untouched, like the
        // message-level AdditionalHeaders of section B.
        logo.AdditionalData = new Dictionary<string, JsonElement>
        {
            ["x-acme-asset-id"] = JsonSerializer.SerializeToElement("logo-2026"),
        };
        logo.Data.AdditionalData = new Dictionary<string, JsonElement>
        {
            ["x-acme-render-hint"] = JsonSerializer.SerializeToElement("dark-background"),
        };

        // Attachments assembled outside the builder validate on demand, exactly like messages:
        // Validate() (on the attachment and on its data) throws if the shape rules don't hold.
        logo.Validate();
        logo.Data.Validate();

        // Shape 3 — linked with hash: the content stays at a URL and the message pins its
        // digest, so the recipient verifies whatever it later fetches. The hash here is a real
        // sha2-256 multihash of the linked bytes, multibase-encoded (u = base64url).
        var videoBytes = Encoding.UTF8.GetBytes("pretend-this-is-a-large-mp4");
        var video = new Attachment
        {
            Id = "video",
            MediaType = "video/mp4",
            Data = new AttachmentData
            {
                Links = new List<string> { "https://cdn.example/x.mp4" },
                Hash = MultibaseSha256Multihash(videoBytes),
            },
        };

        ctx.Narrator.Step("Attach all three shapes and round-trip them through an authcrypt envelope.");
        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"content":"Three attachments enclosed."}""")!.AsObject())
            .WithAttachment(report)
            .WithAttachment(logo)
            .WithAttachment(video)
            .Build();

        var packed = await ctx.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice.Did));
        var unpacked = await ctx.Client.UnpackAsync(packed.Message);

        var received = unpacked.Message.Attachments!;
        ctx.Narrator.Value("Attachments.Count", received.Count);
        ctx.Narrator.Value("report (inline json)", received[0].Data.Json?.ToJsonString());
        ctx.Narrator.Value("logo (base64, decoded)", Encoding.UTF8.GetString(Base64UrlDecode(received[1].Data.Base64!)));
        ctx.Narrator.Value("logo.Description", received[1].Description);
        ctx.Narrator.Value("logo.Filename", received[1].Filename);
        ctx.Narrator.Value("logo.Format", received[1].Format);
        ctx.Narrator.Value("logo.LastModifiedTime", received[1].LastModifiedTime);
        ctx.Narrator.Value("logo x-acme-asset-id", received[1].AdditionalData?["x-acme-asset-id"]);
        ctx.Narrator.Value("logo x-acme-render-hint", received[1].Data.AdditionalData?["x-acme-render-hint"]);
        // The Jws slot is where a detached signature over the attachment content would ride
        // (signed attachments, FR-ATT-04); these attachments are unsigned, so it is null.
        ctx.Narrator.Value("logo.Data.Jws (unsigned ⇒ null)", received[1].Data.Jws);
        ctx.Narrator.Value("video (link)", received[2].Data.Links![0]);
        ctx.Narrator.Value("video (hash)", received[2].Data.Hash);

        // The guard rail: a link with no hash cannot be built — integrity is not optional.
        ctx.Narrator.Step("A linked attachment without a hash is refused at Build() time.");
        try
        {
            new MessageBuilder()
                .WithType("https://didcomm.org/basicmessage/2.0/message")
                .WithAttachment(new Attachment
                {
                    Id = "unpinned",
                    Data = new AttachmentData { Links = new List<string> { "https://cdn.example/y.bin" } },
                })
                .Build();
            ctx.Narrator.Note("UNEXPECTED: the hash-less link was not refused.");
        }
        catch (MalformedMessageException ex)
        {
            ctx.Narrator.Note($"Refused as designed: {ex.Message}");
        }

        ctx.Narrator.Note("Inline JSON for small structured data, base64 for small bytes, links+hash for anything big — the digest makes the out-of-band fetch verifiable.");
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string base64Url)
    {
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(padded);
    }

    /// <summary>Digest bytes with SHA-256 and wrap the result as a multibase(base64url) multihash (0x12 0x20 prefix).</summary>
    private static string MultibaseSha256Multihash(byte[] content)
    {
        var digest = SHA256.HashData(content);
        var multihash = new byte[2 + digest.Length];
        multihash[0] = 0x12;              // multihash code: sha2-256
        multihash[1] = (byte)digest.Length;
        digest.CopyTo(multihash, 2);
        return "u" + Base64UrlEncode(multihash);
    }
}
