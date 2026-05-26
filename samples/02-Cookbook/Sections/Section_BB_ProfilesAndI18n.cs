using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Profiles;
using DidComm.Threading;
using Microsoft.Extensions.DependencyInjection;

// `DidComm.Profiles.Profiles` is shadowed by the `using DidComm.Profiles` namespace import;
// alias the constants class to refer to it unambiguously.
using ProfilesConst = DidComm.Profiles.Profiles;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Two halves: profile negotiation (which DIDComm dialect to speak) and i18n (which
/// language to speak it in). For profiles, we ask <see cref="ProfileNegotiator"/> to pick a
/// dialect from a hypothetical peer's <c>accept</c> list. For i18n, Alice opens a chess
/// thread in French and the <see cref="IThreadStateStore"/> remembers her language
/// preference so any follow-up on the same <c>thid</c> stays in French — and crucially, a
/// concurrent thread is unaffected.
/// </summary>
/// <remarks>
/// <para>
/// The PRD's chess-comment example ("C'est échec et mat.") is the canonical i18n flow; we
/// emit it on Alice's first message, then show that the stored preference is the right tool
/// for the future protocol handler to localize a reply on the same thread.
/// </para>
/// <para>Maps to PRD §14.2 task <strong>BB</strong> (FR-PROF-01/02, FR-I18N-01..03).</para>
/// </remarks>
public static class Section_BB_ProfilesAndI18n
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("BB", "Profiles & i18n");

        // --- Profiles -----------------------------------------------------------------
        // A peer advertises an accept list on its service endpoint or OOB invitation. We
        // pick the most preferred dialect we can speak.
        var acceptList = new[] { "didcomm/aip1", "didcomm/v2", "didcomm/v3" };
        var chosen = ProfileNegotiator.Choose(acceptList);
        ctx.Narrator.Step($"Peer accept = [{string.Join(", ", acceptList)}]");
        ctx.Narrator.Value("Chosen profile", chosen);
        ctx.Narrator.Value("Negotiator.IsSupported(didcomm/v2)", ProfileNegotiator.IsSupported(ProfilesConst.DidCommV2));

        // No overlap ⇒ negotiator returns null and the caller would emit a problem-report.
        var noMatch = ProfileNegotiator.Choose(new[] { "didcomm/aip1", "didcomm/v3" });
        ctx.Narrator.Value("Peer offers no v2 → Choose returns", noMatch);

        // --- i18n ---------------------------------------------------------------------
        // Alice opens a chess thread in French, advertising "fr" then "en" as a fallback.
        var chess = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithLang("fr")
            .WithAcceptLang("fr", "en")
            .WithBody(JsonNode.Parse("""{"comment":"C'est échec et mat."}""")!.AsObject())
            .Build();

        ctx.Narrator.Step("Alice packs the chess move with lang=fr and accept-lang=[fr,en].");
        var packed = (await ctx.Client.PackEncryptedAsync(chess, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice.Did))).Message;
        var bobReceives = await ctx.Client.UnpackAsync(packed);
        ctx.Narrator.Value("Bob unpacks lang", bobReceives.Message.Lang);
        ctx.Narrator.Value("Bob unpacks accept-lang", string.Join(",", bobReceives.Message.AcceptLang ?? new List<string>()));

        // Bob persists Alice's preference for the THREAD (FR-I18N-02). The thid here is
        // the chess message id (the spec says id becomes thid for the first message).
        var store = ctx.ServiceProvider.GetRequiredService<IThreadStateStore>();
        var chessThread = store.GetOrCreate(bobReceives.Message.Id);
        chessThread.AcceptLang = bobReceives.Message.AcceptLang?.ToArray();
        ctx.Narrator.Value("Thread state — accept-lang (chess thread)", string.Join(",", chessThread.AcceptLang ?? Array.Empty<string>()));

        // FR-I18N-02 acceptance test: a concurrent thread does NOT inherit Alice's pref.
        var otherThread = store.GetOrCreate("unrelated-thread-id");
        ctx.Narrator.Value("Thread state — accept-lang (unrelated thread)", otherThread.AcceptLang is null ? "<null>" : string.Join(",", otherThread.AcceptLang));
        ctx.Narrator.Note("FR-I18N-02 honored: the chess thread's preference does not leak across threads.");
    }
}
