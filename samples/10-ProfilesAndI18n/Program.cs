using System.IO;
using System.Text.Json.Nodes;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Profiles;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Threading;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;

// Static API classes that share a name with their namespace segment, aliased for clarity.
using OutOfBandApi = DidComm.Protocols.OutOfBand.OutOfBand;
using ProblemReportApi = DidComm.Protocols.ProblemReport.ProblemReport;
using ProfilesConst = DidComm.Profiles.Profiles;
using ThreadState = DidComm.Threading.ThreadState;

namespace DidComm.Samples.ProfilesAndI18n;

/// <summary>
/// Which dialect to speak, and in which language (PRD §14.3 sample 10, task BB). The profile
/// half reads the <c>accept</c> array a peer advertises — on an Out-of-Band invitation and on
/// its DID document's <c>DIDCommMessaging</c> service — and negotiates a shared DIDComm
/// profile with <c>ProfileNegotiator</c>, including the mismatch path that ends in a
/// problem-report (FR-PROF-01/02). The i18n half plays the spec's chess example: Alice opens
/// a thread in French with <c>lang</c>/<c>accept-lang</c>, Bob records the preference as
/// thread-scoped state (<c>IThreadStateStore</c>) and honors it for every human-readable
/// string he emits on that thread — while a concurrent thread stays unaffected — and answers
/// an unsatisfiable preference with a <c>w.msg.bad-lang</c> problem-report
/// (FR-I18N-01..04).
/// <see cref="Main"/> is the CLI; <see cref="RunAsync"/> is the testable seam invoked by the
/// InteropTests smoke test (FR-DX-02, no process spawn, fully offline).
/// </summary>
public static class Program
{
    /// <summary>The chess line Bob can produce, per language he supports. His whole "i18n catalog".</summary>
    private static readonly IReadOnlyDictionary<string, string> BobsCatalog = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "Checkmate. Well played.",
        ["fr"] = "Échec et mat. Bien joué.",
    };

    /// <summary>Bob's default language when a thread has no recorded preference.</summary>
    private const string BobsDefaultLang = "en";

    /// <summary>CLI entry point — writes to <see cref="Console.Out"/> and exits 0 on success.</summary>
    public static async Task<int> Main()
    {
        try
        {
            await RunAsync(Console.Out).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ProfilesAndI18n failed: {ex}");
            return 1;
        }
    }

    /// <summary>Run the whole flow, writing the narration to <paramref name="output"/>.</summary>
    /// <param name="output">Destination for narrator output. <c>null</c> uses <see cref="Console.Out"/>.</param>
    public static async Task RunAsync(TextWriter? output = null)
    {
        var narrator = output is null ? new Narrator() : new Narrator(output);

        // Two agents, two containers, two key sets — a real conversation, not a mirror.
        // Bob's DID embeds a DIDCommMessaging service whose accept array advertises the
        // profile he speaks; did:peer:2 encodes it all in the DID string, so every resolution
        // below is offline (FR-DX-02).
        var (aliceSp, aliceClient, alice) = await BootAgentAsync(service: null);
        var (bobSp, bobClient, bob) = await BootAgentAsync(service: new DidCommServiceSpec(
            "https://bob.example/didcomm",
            Accept: new[] { ProfilesConst.DidCommV2 }));
        await using var _a = aliceSp;
        await using var _b = bobSp;

        await ProfilesOverOobAsync(narrator, bob);
        await ProfilesOverServiceAsync(narrator, aliceSp, bob);
        ProfileMismatch(narrator, alice.Did, bob.Did);
        var chessThid = await ChessOpeningAsync(narrator, aliceClient, bobClient, bobSp, alice, bob);
        await ThreadScopedPreferenceAsync(narrator, aliceClient, bobClient, bobSp, alice, bob, chessThid);
        await BadLangAsync(narrator, aliceClient, bobClient, bobSp, alice, bob);
    }

    // ── 1. Profiles over an OOB invitation ──────────────────────────────────────────────

    private static Task ProfilesOverOobAsync(Narrator narrator, PeerIdentity bob)
    {
        narrator.Section("1", "Profile selection over an OOB invitation's accept array (FR-PROF-01)");

        // A profile names a bundle of envelope/signing/routing choices — "didcomm/v2" is the
        // one this library emits. Bob's invitation advertises what he accepts; Alice picks a
        // dialect she can speak from that list, preferring HIS order.
        var invitation = OutOfBandApi.CreateInvitation(
            from: bob.Did,
            goal: "Play chess",
            goalCode: "org.example.chess",
            accept: new[] { ProfilesConst.DidCommAip1, ProfilesConst.DidCommV2 });
        var url = OutOfBandApi.ToUrl(invitation, "https://bob.example/invite");

        var scanned = OutOfBandApi.FromUrl(url);
        narrator.Step("Alice decodes the invitation and negotiates against its accept array.");
        narrator.Value("Invitation accept", string.Join(", ", scanned.Accept));
        var chosen = ProfileNegotiator.Choose(scanned.Accept);
        narrator.Value("Negotiated profile", chosen);
        narrator.Value("IsSupported(didcomm/v2)", ProfileNegotiator.IsSupported(ProfilesConst.DidCommV2));
        narrator.Value("IsSupported(didcomm/aip1)", ProfileNegotiator.IsSupported(ProfilesConst.DidCommAip1));

        // An absent accept array is not a mismatch: the peer simply makes no claim, and the
        // library assumes its own dialect is acceptable.
        narrator.Value("Choose(null — peer makes no claim)", ProfileNegotiator.Choose(null));
        return Task.CompletedTask;
    }

    // ── 2. Profiles over the DID document's service ─────────────────────────────────────

    private static async Task ProfilesOverServiceAsync(Narrator narrator, ServiceProvider aliceSp, PeerIdentity bob)
    {
        narrator.Section("2", "The same accept array on Bob's DIDCommMessaging service");

        // The accept array also lives on the DID document's service endpoint — no invitation
        // needed. Alice resolves Bob's DID (offline: it is a did:peer:2) and reads the
        // service block through the routing layer's resolver.
        var endpointResolver = aliceSp.GetRequiredService<IServiceEndpointResolver>();
        var services = await endpointResolver.ResolveAsync(bob.Did);
        narrator.Value("DIDCommMessaging services found", services.Count);
        var service = services[0];
        narrator.Value("Service uri", service.Uri);
        narrator.Value("Service accept", string.Join(", ", service.Accept));
        narrator.Value("Negotiated from service accept", ProfileNegotiator.Choose(service.Accept));
    }

    // ── 3. Mismatch: no shared profile ──────────────────────────────────────────────────

    private static void ProfileMismatch(Narrator narrator, string aliceDid, string bobDid)
    {
        narrator.Section("3", "Profile mismatch — negotiate, then report (FR-PROF-02)");

        // A peer that only offers dialects we do not speak: Choose returns null instead of
        // guessing, and the caller says so with a problem-report on the failing thread.
        var offered = new[] { ProfilesConst.DidCommAip1, "didcomm/v3" };
        var chosen = ProfileNegotiator.Choose(offered);
        narrator.Value($"Choose([{string.Join(", ", offered)}])", chosen ?? "<null> (no shared profile)");

        var report = ProblemReportApi.Create(
            from: aliceDid,
            to: bobDid,
            code: "e.p.msg.unsupported",
            pthid: "profile-negotiation-thread-1",
            comment: "No mutually supported DIDComm profile; offered: {1}. This agent speaks {2}.",
            args: new[] { string.Join(", ", offered), ProfilesConst.DidCommV2 });
        narrator.Step("Alice reports the mismatch instead of silently failing.");
        narrator.Value("Report code", ProblemReportApi.ReadCode(report));
        narrator.Value("Report comment", ProblemReportApi.RenderComment(report));
    }

    // ── 4. The chess opening: lang and accept-lang ──────────────────────────────────────

    private static async Task<string> ChessOpeningAsync(
        Narrator narrator, DidCommClient aliceClient, DidCommClient bobClient, ServiceProvider bobSp,
        PeerIdentity alice, PeerIdentity bob)
    {
        narrator.Section("4", "Chess in French — lang / accept-lang headers (FR-I18N-01/03)");

        // The spec's example: the human-readable comment is French, the lang header says so
        // (FR-I18N-03 — receivers interpret protocol strings in that language), and
        // accept-lang ranks the languages Alice wants ANSWERS in.
        var move = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(alice.Did)
            .WithTo(bob.Did)
            .WithLang("fr")
            .WithAcceptLang("fr", "en")
            .WithBody(JsonNode.Parse("""{"comment":"C'est échec et mat."}""")!.AsObject())
            .Build();
        narrator.Step("Alice mates in French and advertises accept-lang = [fr, en].");

        var packed = (await aliceClient.PackEncryptedAsync(move, new PackEncryptedOptions(
            Recipients: new[] { bob.Did }, From: alice.Did))).Message;
        var bobView = await bobClient.UnpackAsync(packed);
        narrator.Value("Bob unpacks lang", bobView.Message.Lang);
        narrator.Value("Bob unpacks accept-lang", string.Join(", ", bobView.Message.AcceptLang ?? new List<string>()));
        narrator.Value("Comment (interpreted as French)", bobView.Message.Body?["comment"]?.GetValue<string>());

        // Bob records the preference AGAINST THE THREAD — the first message's id becomes the
        // thread id — and replies in the best language he can produce for it.
        var thid = bobView.Message.Thid ?? bobView.Message.Id;
        var store = bobSp.GetRequiredService<IThreadStateStore>();
        store.GetOrCreate(thid).AcceptLang = bobView.Message.AcceptLang?.ToArray();
        narrator.Value("Thread state recorded for thid", store.Get(thid)?.AcceptLang is { Count: > 0 });

        var reply = BobReplies(store, thid, bob.Did, alice.Did);
        var replyPacked = (await bobClient.PackEncryptedAsync(reply, new PackEncryptedOptions(
            Recipients: new[] { alice.Did }, From: bob.Did))).Message;
        var aliceView = await aliceClient.UnpackAsync(replyPacked);
        narrator.Step("Bob's reply honors the thread's preference.");
        narrator.Value("Reply lang", aliceView.Message.Lang);
        narrator.Value("Reply comment", aliceView.Message.Body?["comment"]?.GetValue<string>());
        return thid;
    }

    // ── 5. The preference is thread-scoped state ────────────────────────────────────────

    private static async Task ThreadScopedPreferenceAsync(
        Narrator narrator, DidCommClient aliceClient, DidCommClient bobClient, ServiceProvider bobSp,
        PeerIdentity alice, PeerIdentity bob, string chessThid)
    {
        narrator.Section("5", "The preference is thread-scoped (FR-I18N-02)");

        // Alice's SECOND message on the chess thread carries no language headers at all — she
        // declared her preference once and it holds until changed or the thread ends.
        var followUp = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(alice.Did)
            .WithTo(bob.Did)
            .WithThid(chessThid)
            .WithBody(JsonNode.Parse("""{"comment":"Une revanche ?"}""")!.AsObject())
            .Build();
        narrator.Step("Alice follows up on the SAME thread, with no accept-lang header.");
        var packed = (await aliceClient.PackEncryptedAsync(followUp, new PackEncryptedOptions(
            Recipients: new[] { bob.Did }, From: alice.Did))).Message;
        var bobView = await bobClient.UnpackAsync(packed);
        narrator.Value("Follow-up carries accept-lang", bobView.Message.AcceptLang is not null);

        var store = bobSp.GetRequiredService<IThreadStateStore>();
        var reply = BobReplies(store, bobView.Message.Thid!, bob.Did, alice.Did);
        var replyView = await aliceClient.UnpackAsync((await bobClient.PackEncryptedAsync(reply,
            new PackEncryptedOptions(Recipients: new[] { alice.Did }, From: bob.Did))).Message);
        narrator.Value("Second reply on chess thread — lang", replyView.Message.Lang);
        narrator.Value("Second reply honors the recorded preference", replyView.Message.Lang == "fr");

        // A CONCURRENT thread gets none of that: no recorded preference, so Bob answers in his
        // default language. Thread state must not leak sideways.
        var unrelated = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(alice.Did)
            .WithTo(bob.Did)
            .WithBody(JsonNode.Parse("""{"comment":"Different topic entirely."}""")!.AsObject())
            .Build();
        narrator.Step("A concurrent thread opens with no language preference.");
        var unrelatedView = await bobClient.UnpackAsync((await aliceClient.PackEncryptedAsync(unrelated,
            new PackEncryptedOptions(Recipients: new[] { bob.Did }, From: alice.Did))).Message);
        var unrelatedThid = unrelatedView.Message.Thid ?? unrelatedView.Message.Id;
        narrator.Value("Concurrent thread has recorded accept-lang", store.Get(unrelatedThid)?.AcceptLang is { Count: > 0 });

        var defaultReply = BobReplies(store, unrelatedThid, bob.Did, alice.Did);
        var defaultView = await aliceClient.UnpackAsync((await bobClient.PackEncryptedAsync(defaultReply,
            new PackEncryptedOptions(Recipients: new[] { alice.Did }, From: bob.Did))).Message);
        narrator.Value("Concurrent-thread reply lang", defaultView.Message.Lang);
        narrator.Value("Chess preference did not leak", defaultView.Message.Lang == BobsDefaultLang);
    }

    // ── 6. No acceptable language: w.msg.bad-lang ───────────────────────────────────────

    private static async Task BadLangAsync(
        Narrator narrator, DidCommClient aliceClient, DidCommClient bobClient, ServiceProvider bobSp,
        PeerIdentity alice, PeerIdentity bob)
    {
        narrator.Section("6", "No acceptable language — the bad-lang report (FR-I18N-04)");

        // Alice (on yet another thread) insists on Japanese only. Bob's catalog is en/fr, so
        // no preference is satisfiable — and the thread-aware factory turns exactly that state
        // into a w.msg.bad-lang report naming what Bob CAN produce.
        var demanding = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(alice.Did)
            .WithTo(bob.Did)
            .WithLang("ja")
            .WithAcceptLang("ja")
            .WithBody(JsonNode.Parse("""{"comment":"日本語でお願いします。"}""")!.AsObject())
            .Build();
        narrator.Step("Alice demands accept-lang = [ja]; Bob speaks en and fr.");
        var bobView = await bobClient.UnpackAsync((await aliceClient.PackEncryptedAsync(demanding,
            new PackEncryptedOptions(Recipients: new[] { bob.Did }, From: alice.Did))).Message);

        var store = bobSp.GetRequiredService<IThreadStateStore>();
        var thid = bobView.Message.Thid ?? bobView.Message.Id;
        var thread = store.GetOrCreate(thid);
        thread.AcceptLang = bobView.Message.AcceptLang?.ToArray();

        var report = ProblemReportApi.CreateBadLangForThread(
            from: bob.Did,
            to: alice.Did,
            thread: thread,
            availableLangs: BobsCatalog.Keys.ToArray());
        narrator.Value("Report produced", report is not null);

        // The report is a normal message: it travels authcrypt and Alice reads the code, the
        // failing thread, and the rendered comment listing Bob's languages.
        var aliceView = await aliceClient.UnpackAsync((await bobClient.PackEncryptedAsync(report!,
            new PackEncryptedOptions(Recipients: new[] { alice.Did }, From: bob.Did))).Message);
        narrator.Value("Report code", ProblemReportApi.ReadCode(aliceView.Message));
        narrator.Value("Report pthid == failing thid", aliceView.Message.Pthid == thid);
        narrator.Value("Report comment", ProblemReportApi.RenderComment(aliceView.Message));
        narrator.Note("The warning form (w.) says the thread MAY continue in a fallback language; the fatal form (e.msg.bad-lang) ends it.");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bob composes his chess reply for a thread: pick the best language from the thread's
    /// recorded <c>accept-lang</c> preference (FR-I18N-02) against his catalog — falling back
    /// to his default when the thread never declared one — and emit the localized string
    /// tagged with the matching <c>lang</c> header.
    /// </summary>
    private static Message BobReplies(IThreadStateStore store, string thid, string bobDid, string aliceDid)
    {
        var lang = PickLanguage(store.Get(thid));
        return new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(bobDid)
            .WithTo(aliceDid)
            .WithThid(thid)
            .WithLang(lang)
            .WithBody(new JsonObject { ["comment"] = BobsCatalog[lang] })
            .Build();
    }

    /// <summary>
    /// The first preferred tag Bob's catalog satisfies (exact or primary-subtag match, so
    /// <c>fr-CA</c> is served by <c>fr</c>); the default language when the thread has no
    /// usable preference.
    /// </summary>
    private static string PickLanguage(ThreadState? thread)
    {
        foreach (var preferred in thread?.AcceptLang ?? Array.Empty<string>())
        {
            if (BobsCatalog.ContainsKey(preferred))
                return preferred.ToLowerInvariant();
            var dash = preferred.IndexOf('-', StringComparison.Ordinal);
            var primary = dash < 0 ? preferred : preferred[..dash];
            if (BobsCatalog.ContainsKey(primary))
                return primary.ToLowerInvariant();
        }
        return BobsDefaultLang;
    }

    private static async Task<(ServiceProvider Provider, DidCommClient Client, PeerIdentity Identity)> BootAgentAsync(
        DidCommServiceSpec? service)
    {
        var secrets = new InMemorySecretsResolver();
        var services = new ServiceCollection();
        services.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(secrets));
        var provider = services.BuildServiceProvider();

        var identity = await PeerIdentityFactory.CreateAsync(
            provider.GetRequiredService<IDidManager>(),
            provider.GetRequiredService<IKeyGenerator>(),
            provider.GetRequiredService<ICryptoProvider>(),
            service);
        foreach (var key in identity.Privates)
            secrets.Add(key);

        return (provider, provider.GetRequiredService<DidCommClient>(), identity);
    }
}
