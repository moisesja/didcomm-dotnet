using DidComm.Messages;
using DidComm.Protocols.OutOfBand;

// L-014: alias the static OutOfBand API class so the same-named namespace doesn't shadow it.
using OutOfBandApi = DidComm.Protocols.OutOfBand.OutOfBand;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Builds an Out-of-Band 2.0 invitation, turns it into the URL that sits behind a QR code,
/// decodes it on the other side, then shows the two things that make OOB work: the short-URL
/// form a sender hosts when an invitation is too big for a QR code, and the rule that the
/// invitation's id becomes the response's <c>pthid</c> so replies correlate back to it.
/// </summary>
/// <remarks>
/// <para>
/// An invitation travels out of band — printed as a QR code, emailed, or texted — so it never
/// carries private data; it just bootstraps a connection. The recipient decodes it and starts a
/// follow-up protocol that points back at the invitation through <c>pthid</c>. There is no
/// handler to register: nothing about an invitation arrives through the normal receive pipeline.
/// </para>
/// <para>Maps to PRD §14.2 task <strong>V</strong> (FR-OOB-01..05).</para>
/// </remarks>
public static class Section_V_OutOfBandInvitation
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("V", "Out-of-Band invitation (build + URL encode/decode)");

        // Alice builds an invitation. goal_code / goal / accept go in the message body; the
        // invitation id is what a response will reference as its pthid.
        var invitation = OutOfBandApi.CreateInvitation(
            from: ctx.Alice.Did,
            goal: "Connect with Faber College",
            goalCode: "connect",
            accept: new[] { "didcomm/v2" });
        ctx.Narrator.Step($"Alice builds an invitation (id={invitation.Id[..8]}…, goalCode={invitation.GoalCode}).");

        // Encode it as the URL that sits behind a QR code, then decode it on Bob's side.
        var url = OutOfBandApi.ToUrl(invitation, "https://faber.example/oob");
        ctx.Narrator.Value("Invitation URL (truncated)", url.Length <= 72 ? url : url[..69] + "…");

        var decoded = OutOfBandApi.FromUrl(url);
        ctx.Narrator.Value("Decoded from == Alice", decoded.From == ctx.Alice.Did);
        ctx.Narrator.Value("Decoded goal round-trips", decoded.Goal == invitation.Goal);
        ctx.Narrator.Value("Decoded accept", string.Join(",", decoded.Accept));

        // Short-URL form: when an invitation is too big for a clean QR code, the sender stores
        // the full plaintext under an id and serves it on an HTTP GET (see MapDidCommOobEndpoint).
        // The recipient scans only "…?_oobid=<id>" and fetches the rest. We use the in-memory
        // store directly here; Section's interop test drives the real HTTP endpoint.
        var store = new InMemoryOobInvitationStore();
        var oobId = Guid.NewGuid().ToString("D");
        store.Store(oobId, await ctx.Client.PackPlaintextAsync(invitation.Message));

        var shortUrl = OutOfBandApi.ToShortUrl("https://faber.example/oob", oobId);
        ctx.Narrator.Value("Short URL", shortUrl);

        OutOfBandApi.TryGetShortFormId(shortUrl, out var parsedId);
        var fetched = OutOfBandApi.FromPlaintext(store.Retrieve(parsedId)!);
        ctx.Narrator.Value("Short-form retrieval recovers id", fetched.Id == invitation.Id);

        // FR-OOB-03: Bob's response carries the invitation id as its pthid, which lets a single
        // invitation spawn several independent threads. Bob also attaches a web_redirect so Alice
        // can send him somewhere once the follow-up protocol concludes (FR-OOB-05).
        var response = Message.Empty()
            .WithFrom(ctx.Bob.Did)
            .WithTo(invitation.From!)
            .WithPthid(invitation.Id)
            .Build();
        OutOfBandApi.AddWebRedirect(response, new WebRedirect("OK", "https://faber.example/welcome"));

        ctx.Narrator.Value("Response.pthid == invitation.id", response.Pthid == invitation.Id);
        ctx.Narrator.Value("web_redirect parses back", OutOfBandApi.ReadWebRedirect(response)?.RedirectUrl);
        ctx.Narrator.Note("The invitation is plaintext by design — never put private data in a QR code (FR-OOB privacy).");
    }
}
