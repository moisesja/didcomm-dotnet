using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Protocols.ProblemReport;
using DidComm.Protocols.Empty;
using DidComm.Protocols.Trace;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetCrypto;
using NetDid.Core;

// The static API class shares its name with its namespace segment; alias it for clarity.
using ProblemReportApi = DidComm.Protocols.ProblemReport.ProblemReport;
using TraceApi = DidComm.Protocols.Trace.Trace;
using ThreadState = DidComm.Threading.ThreadState;

namespace DidComm.Samples.ProblemsAndProtocols;

/// <summary>
/// How a DIDComm agent talks about failure — and how it grows new protocols (PRD §14.3
/// sample 07, tasks U/W/X). The tour walks Report Problem 2.0 end to end: the structured
/// problem-code taxonomy, building and reading reports with <c>{n}</c> comment interpolation,
/// escalating a warning into an error, the per-thread cascade guard that stops report storms,
/// and the bad-lang factories. It then shows the header-only Empty 1.0 ACK, teaches the agent
/// the spec's <c>lets_do_lunch</c> protocol with a custom <c>IProtocolHandler</c>, watches
/// inbound problem-reports through a read-only <c>IProtocolObserver</c>, advertises a custom
/// goal-code through Discover Features, and finishes on Trace 2.0's off-by-default posture.
/// <see cref="Main"/> is the CLI; <see cref="RunAsync"/> is the testable seam invoked by the
/// InteropTests smoke test (FR-DX-02, no process spawn, fully offline).
/// </summary>
public static class Program
{
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
            Console.Error.WriteLine($"ProblemsAndProtocols failed: {ex}");
            return 1;
        }
    }

    /// <summary>Run the whole tour, writing the narration to <paramref name="output"/>.</summary>
    /// <param name="output">Destination for narrator output. <c>null</c> uses <see cref="Console.Out"/>.</param>
    public static async Task RunAsync(TextWriter? output = null)
    {
        var narrator = output is null ? new Narrator() : new Narrator(output);

        // One offline container for the whole tour. Everything protocol-related in this sample
        // is wired HERE, at composition time — that is the deliberate design of the DI surface:
        // handlers, observers, feature providers, and tracing are all opt-in builder calls.
        var secrets = new InMemorySecretsResolver();
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            // The spec-defined handlers: Trust Ping, Empty, Discover Features, Report Problem.
            b.AddBuiltInProtocols();
            // A protocol of our own — the spec's running lets_do_lunch example (FR-PROTO-03).
            b.AddProtocol<LunchHandler>();
            // A read-only side channel that watches inbound report-problem traffic WITHOUT
            // replacing the built-in handler (FR-PROTO-12).
            b.AddProtocolObserver<ProblemReportAuditObserver>();
            // Advertise our lunch goal-code through Discover Features (FR-PROTO-05).
            b.AddFeatureProvider<LunchGoalCodeProvider>();
            // Trace 2.0 is OFF unless an operator opts in — and opting in REQUIRES an
            // explicit allowlist of report URIs (FR-PROTO-11a). Section 10 explores this.
            b.EnableTracing(o =>
            {
                o.Enabled = true;
                o.AllowedReportingUris.Add("https://trace.example/didcomm-reports");
            });
            // Drop the per-thread error budget from its default 5 to 2 so the cascade guard
            // trips visibly in three messages (section 4). Standard Options pattern.
            b.Services.Configure<ProblemReportOptions>(o => o.CascadeThreshold = 2);
        });
        await using var sp = services.BuildServiceProvider();

        // Two did:peer:2 identities; the DID document lives inside the DID string, so nothing
        // in this sample touches a network (FR-DX-02).
        var manager = sp.GetRequiredService<IDidManager>();
        var keyGen = sp.GetRequiredService<IKeyGenerator>();
        var crypto = sp.GetRequiredService<ICryptoProvider>();
        var alice = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        var bob = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        foreach (var key in alice.Privates.Concat(bob.Privates))
            secrets.Add(key);

        var client = sp.GetRequiredService<DidCommClient>();
        var dispatcher = sp.GetRequiredService<ProtocolDispatcher>();
        var options = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;

        Taxonomy(narrator);
        await BuildAndReadAsync(narrator, client, alice.Did, bob.Did);
        Escalation(narrator, alice.Did, bob.Did);
        await CascadeGuardAsync(narrator, client, dispatcher, options, alice.Did, bob.Did);
        BadLang(narrator, alice.Did, bob.Did);
        await EmptyAckAsync(narrator, client, dispatcher, options, alice.Did, bob.Did);
        await LetsDoLunchAsync(narrator, client, dispatcher, options, alice.Did, bob.Did);
        await ObserverAsync(narrator, sp.GetRequiredService<ProblemReportAuditObserver>());
        await DiscoverGoalCodeAsync(narrator, client, dispatcher, options, alice.Did, bob.Did);
        Tracing(narrator, sp.GetRequiredService<TraceOptions>(), alice.Did, bob.Did);
    }

    // ── 1. The problem-code taxonomy ────────────────────────────────────────────────────

    private static void Taxonomy(Narrator narrator)
    {
        narrator.Section("1", "The problem-code taxonomy (FR-PROTO-08)");

        // A problem-code is machine-readable structure, not prose: <sorter>.<scope>.<descriptor>.
        // The sorter says how bad it is (e = the affected state is dead, w = we can continue);
        // the scope says how much is affected (p = the whole protocol, m = just this message);
        // the descriptor is a dot-path into the spec's failure-cause tree.
        var parsed = ProblemCode.Parse("e.p.xfer.cant-use-endpoint");
        narrator.Step("Parse e.p.xfer.cant-use-endpoint into its parts.");
        narrator.Value("Sorter / Scope / Descriptor", $"{parsed.Sorter} / {parsed.Scope} / {parsed.Descriptor}");
        narrator.Value("IsError", parsed.IsError);
        narrator.Value("IsProtocolScoped", parsed.IsProtocolScoped);
        narrator.Value("Full Value round-trips", parsed.Value == "e.p.xfer.cant-use-endpoint");

        // Matching is structural — segment by segment at the dots — so "e.p.xfer" matches this
        // code but "e.p.xf" does not (it is not a string prefix test).
        narrator.Step("Prefix matching is per-segment, not per-character.");
        narrator.Value("StartsWith(\"e.p.xfer\")", parsed.StartsWith("e.p.xfer"));
        narrator.Value("StartsWith(\"e.p.xf\")", parsed.StartsWith("e.p.xf"));

        // A warning uses the same shape with sorter 'w'. Scope can also be a free-form protocol
        // state name; the descriptor tree tolerates extensions by design.
        var warning = ProblemCode.Parse("w.m.xfer.slow");
        narrator.Value("w.m.xfer.slow IsWarning / IsMessageScoped", $"{warning.IsWarning} / {warning.IsMessageScoped}");

        // Malformed codes never make it onto the wire: TryParse refuses them up front.
        narrator.Value("TryParse(\"oops\")", ProblemCode.TryParse("oops", out _));
        narrator.Value("TryParse(\"x.p.bad-sorter\")", ProblemCode.TryParse("x.p.bad-sorter", out _));
    }

    // ── 2. Build and read a problem-report ──────────────────────────────────────────────

    private static async Task BuildAndReadAsync(Narrator narrator, DidCommClient client, string aliceDid, string bobDid)
    {
        narrator.Section("2", "Build and read a problem-report (FR-PROTO-07)");

        // pthid is REQUIRED: a problem-report is always ABOUT another thread — the one that
        // failed — and pthid is how the receiver connects the complaint to it.
        const string failingThid = "thread-that-failed-1";
        var report = ProblemReportApi.Create(
            from: aliceDid,
            to: bobDid,
            code: "e.p.xfer.cant-use-endpoint",
            pthid: failingThid,
            comment: "Unable to use the {1} endpoint for {2}.",
            args: new[] { "https://agents.r.us/inbox", bobDid },
            escalateTo: "mailto:admin@sad-agent.example");
        narrator.Step("Alice reports she cannot reach an endpoint on Bob's behalf.");
        narrator.Value("Type", report.Type);
        narrator.Value("Pthid", report.Pthid);
        narrator.Value("body.code", ProblemReportApi.ReadCode(report));
        narrator.Value("body.escalate_to", report.Body?[ProblemReportApi.EscalateToField]?.GetValue<string>());

        // Omitting pthid is a construction error, not a wire-time surprise.
        try
        {
            ProblemReportApi.Create(from: aliceDid, to: bobDid, code: "e.p.xfer", pthid: "");
            narrator.Value("Create without pthid", "UNEXPECTED — no exception");
        }
        catch (ArgumentException)
        {
            narrator.Value("Create without pthid", "refused (ArgumentException — pthid is REQUIRED)");
        }

        // The report travels like any message — here authcrypt, so Bob knows who is complaining.
        var packed = (await client.PackEncryptedAsync(report, new PackEncryptedOptions(
            Recipients: new[] { bobDid }, From: aliceDid))).Message;
        var received = await client.UnpackAsync(packed);
        narrator.Step("Bob unpacks and renders the comment against args ({n} is 1-based).");
        narrator.Value("Rendered comment", ProblemReportApi.RenderComment(received.Message));
        narrator.Value("Report sender authenticated", received.Authenticated);

        // Interpolation is forgiving in both directions: a placeholder with no matching arg
        // renders as a literal '?', and args no placeholder references are appended so they
        // are never silently lost.
        var missing = ProblemReportApi.Create(
            from: aliceDid, to: bobDid, code: "e.m.msg.unpaired", pthid: failingThid,
            comment: "Field {1} clashed with {3}.",
            args: new[] { "thid" });
        narrator.Value("Missing arg renders '?'", ProblemReportApi.RenderComment(missing));

        var extras = ProblemReportApi.Create(
            from: aliceDid, to: bobDid, code: "e.m.msg.extra", pthid: failingThid,
            comment: "Only {1} is referenced.",
            args: new[] { "first", "second", "third" });
        narrator.Value("Extra args are appended", ProblemReportApi.RenderComment(extras));
    }

    // ── 3. Warning → error escalation ───────────────────────────────────────────────────

    private static void Escalation(Narrator narrator, string aliceDid, string bobDid)
    {
        narrator.Section("3", "Warning → error escalation (FR-PROTO-09)");

        // Bob warned Alice that transfers were slow (w.m.xfer.slow). The situation did not
        // recover, so Alice escalates: same scope or wider, sorter flips to 'e'. The helper
        // preserves the original scope, so the escalation can never quietly narrow the blast
        // radius the warning claimed.
        var originalWarning = ProblemCode.Parse("w.m.xfer.slow");
        var escalated = ProblemReportApi.Escalate(
            from: aliceDid,
            to: bobDid,
            originalCode: originalWarning,
            escalatedDescriptor: "xfer.failed",
            pthid: "thread-that-failed-1",
            comment: "The slow transfer never completed.");
        narrator.Step($"Escalate {originalWarning} — the scope ('{originalWarning.Scope}') is preserved.");
        narrator.Value("Escalated code", ProblemReportApi.ReadCode(escalated));

        // Escalation only makes sense FROM a warning; handing it an error is a caller bug.
        try
        {
            ProblemReportApi.Escalate(aliceDid, bobDid, ProblemCode.Parse("e.p.xfer.failed"), "xfer.failed", "t1");
            narrator.Value("Escalating an error", "UNEXPECTED — no exception");
        }
        catch (ArgumentException)
        {
            narrator.Value("Escalating an error", "refused (only warnings escalate)");
        }
    }

    // ── 4. The cascade guard ────────────────────────────────────────────────────────────

    private static async Task CascadeGuardAsync(
        Narrator narrator, DidCommClient client, ProtocolDispatcher dispatcher, DidCommOptions options,
        string aliceDid, string bobDid)
    {
        narrator.Section("4", "The cascade guard (FR-PROTO-10)");

        // Problem-reports about problem-reports can storm forever. The built-in handler keeps a
        // per-failing-thread error budget in a dedicated CascadeBudgetStore; once a thread
        // crosses the threshold, Bob sends exactly ONE e.p.req.max-errors-exceeded notice and
        // then goes silent on that thread. This container set CascadeThreshold = 2 (default 5).
        const string stormThid = "storming-thread-1";
        narrator.Step("Alice fires four error reports on the same failing thread (budget = 2).");

        DispatchOutcome? outcome = null;
        for (var i = 1; i <= 4; i++)
        {
            var report = ProblemReportApi.Create(
                from: aliceDid, to: bobDid,
                code: "e.p.xfer.cant-use-endpoint", pthid: stormThid,
                comment: "Delivery attempt {1} failed.", args: new[] { i.ToString() });
            var packed = (await client.PackEncryptedAsync(report, new PackEncryptedOptions(
                Recipients: new[] { bobDid }, From: aliceDid))).Message;
            var received = await client.UnpackAsync(packed);
            outcome = await dispatcher.DispatchAsync(received, client, options);
            narrator.Value($"report #{i} outcome", outcome.Result);

            if (outcome.Result == DispatchResult.ReplyProduced)
            {
                // The one-and-only cascade stop: code, thread, and the breach count in the comment.
                narrator.Value("  Cascade-stop code", ProblemReportApi.ReadCode(outcome.Reply!));
                narrator.Value("  Cascade-stop pthid", outcome.Reply!.Pthid);
                narrator.Value("  Cascade-stop comment", ProblemReportApi.RenderComment(outcome.Reply!));
            }
        }
        narrator.Value("Post-trip reports stay silent", outcome!.Result == DispatchResult.NoReply);
        narrator.Note("After the trip the handler returns null for every further report on that pthid — the storm dies at Bob's edge.");
    }

    // ── 5. Bad-lang reports ─────────────────────────────────────────────────────────────

    private static void BadLang(Narrator narrator, string aliceDid, string bobDid)
    {
        narrator.Section("5", "Bad-lang reports (FR-I18N-04)");

        // When a peer asks for languages this agent cannot produce, the polite answer is a
        // problem-report naming the languages that ARE available. The warning form says "the
        // protocol can continue in a fallback language"; the fatal form ends the interaction.
        var warnReport = ProblemReportApi.CreateBadLang(
            from: bobDid, to: aliceDid,
            pthid: "chess-thread-1",
            availableLangs: new[] { "en", "es" });
        narrator.Step("Warning form — the thread can continue in a fallback language.");
        narrator.Value("Code", ProblemReportApi.ReadCode(warnReport));
        narrator.Value("Comment", ProblemReportApi.RenderComment(warnReport));

        var fatalReport = ProblemReportApi.CreateBadLang(
            from: bobDid, to: aliceDid,
            pthid: "chess-thread-1",
            availableLangs: new[] { "en", "es" },
            fatal: true);
        narrator.Value("Fatal form code", ProblemReportApi.ReadCode(fatalReport));

        // The thread-aware factory reads the FR-I18N-02 thread state directly: it only builds
        // a report when the thread HAS a recorded preference and none of it is satisfiable.
        var demandingThread = new ThreadState("chess-thread-1") { AcceptLang = new[] { "ja" } };
        var fromThread = ProblemReportApi.CreateBadLangForThread(
            from: bobDid, to: aliceDid, thread: demandingThread, availableLangs: new[] { "en", "es" });
        narrator.Step("Thread-aware form — accept-lang [ja] vs available [en, es].");
        narrator.Value("Report produced", fromThread is not null);
        narrator.Value("Report pthid == thread.Thid", fromThread?.Pthid == demandingThread.Thid);

        // A satisfiable preference produces NO report (en-US is satisfied by en).
        var happyThread = new ThreadState("chess-thread-2") { AcceptLang = new[] { "en-US" } };
        var noReport = ProblemReportApi.CreateBadLangForThread(
            from: bobDid, to: aliceDid, thread: happyThread, availableLangs: new[] { "en", "es" });
        narrator.Value("Satisfiable preference produces", noReport is null ? "<null> (no report warranted)" : "UNEXPECTED report");
    }

    // ── 6. The Empty 1.0 header-only ACK ────────────────────────────────────────────────

    private static async Task EmptyAckAsync(
        Narrator narrator, DidCommClient client, ProtocolDispatcher dispatcher, DidCommOptions options,
        string aliceDid, string bobDid)
    {
        narrator.Section("6", "Empty 1.0 — the header-only ACK (FR-PROTO-06)");

        // Alice asks for a receipt: please_ack rides on any message. Bob has nothing of
        // substance to say back, so he answers with an Empty — a message that is ALL headers:
        // the Empty 1.0 type, the thread id, and the ack[] naming what he received.
        var request = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(aliceDid)
            .WithTo(bobDid)
            .WithBody(JsonNode.Parse("""{"content":"Please confirm you got this."}""")!.AsObject())
            .WithPleaseAck()
            .Build();
        narrator.Step("Alice requests an ACK (please_ack).");
        narrator.Value("please_ack present", request.PleaseAck is not null);

        var ack = Message.Empty()
            .WithFrom(bobDid)
            .WithTo(aliceDid)
            .WithThid(request.Id)
            .WithAck(request.Id)
            .Build();
        narrator.Step("Bob replies with a header-only Empty 1.0 carrying ack[].");
        narrator.Value("Empty type", ack.Type == EmptyProtocol.MessageType);
        narrator.Value("Body", ack.Body);
        narrator.Value("ack[] names the request", ack.Ack?.Contains(request.Id));

        // Round-trip it and hand it to the dispatcher: the built-in EmptyHandler consumes it
        // (an ACK needs no answer — answering ACKs is how loops start).
        var packed = (await client.PackEncryptedAsync(ack, new PackEncryptedOptions(
            Recipients: new[] { aliceDid }, From: bobDid))).Message;
        var received = await client.UnpackAsync(packed);
        var outcome = await dispatcher.DispatchAsync(received, client, options);
        narrator.Value("Empty dispatch outcome", outcome.Result);
        narrator.Value("Handled by", outcome.Handler?.ProtocolUri);
    }

    // ── 7. A custom protocol: lets_do_lunch ─────────────────────────────────────────────

    private static async Task LetsDoLunchAsync(
        Narrator narrator, DidCommClient client, ProtocolDispatcher dispatcher, DidCommOptions options,
        string aliceDid, string bobDid)
    {
        narrator.Section("7", "A custom protocol — lets_do_lunch (FR-PROTO-03)");

        // The spec's running example of an application protocol. Teaching the agent a new
        // protocol is one class (LunchHandler, below) plus one builder call:
        // b.AddProtocol<LunchHandler>() — exactly how the built-ins are registered.
        var proposal = new MessageBuilder()
            .WithType(LunchHandler.ProposalType)
            .WithFrom(aliceDid)
            .WithTo(bobDid)
            .WithBody(JsonNode.Parse("""{"when":"2026-08-03T12:30:00Z","where":"Fisherman's Wharf"}""")!.AsObject())
            .Build();
        narrator.Step("Alice proposes lunch; the envelope is ordinary authcrypt.");

        var packed = (await client.PackEncryptedAsync(proposal, new PackEncryptedOptions(
            Recipients: new[] { bobDid }, From: aliceDid))).Message;
        var received = await client.UnpackAsync(packed);

        // The dispatcher routes by the message type's protocol identifier — no switch
        // statements in application code, ever.
        var outcome = await dispatcher.DispatchAsync(received, client, options);
        narrator.Value("Dispatched to", outcome.Handler?.ProtocolUri);
        narrator.Value("Reply type", outcome.Reply?.Type);
        narrator.Value("Reply thid == proposal.id", outcome.Reply?.Thid == proposal.Id);
        narrator.Value("Bob accepted", outcome.Reply?.Body?["accepted"]?.GetValue<bool>());
    }

    // ── 8. Watching traffic without owning it ───────────────────────────────────────────

    private static async Task ObserverAsync(Narrator narrator, ProblemReportAuditObserver observer)
    {
        narrator.Section("8", "A read-only observer on report-problem traffic (FR-PROTO-12)");

        // The built-in ProblemReportHandler OWNS the report-problem protocol (it ran the
        // cascade guard in section 4). This observer registered via AddProtocolObserver<T>()
        // saw every one of those inbound reports too — on a decoupled background queue that
        // can never change a dispatch outcome or block a reply. Delivery is asynchronous, so
        // we wait on the observer's own signal rather than sleeping.
        var delivered = await observer.WaitForAsync(count: 4, TimeSpan.FromSeconds(30));
        narrator.Value("All four cascade reports observed", delivered);
        narrator.Value("Observer filter", observer.ProtocolUriFilter);
        narrator.Value("Observed problem-report count", observer.Observed.Count);
        narrator.Value("Observed codes (distinct)", string.Join(", ", observer.Observed.Select(o => o.Code).Distinct()));
        narrator.Value("Every observation was authenticated", observer.Observed.All(o => o.Authenticated));
        narrator.Note("The handler still handled; the observer only watched. That is the whole contract.");
    }

    // ── 9. Advertising a custom goal-code ───────────────────────────────────────────────

    private static async Task DiscoverGoalCodeAsync(
        Narrator narrator, DidCommClient client, ProtocolDispatcher dispatcher, DidCommOptions options,
        string aliceDid, string bobDid)
    {
        narrator.Section("9", "Advertising the lunch goal-code via Discover Features (FR-PROTO-05)");

        // AddFeatureProvider<T>() extends what the built-in DiscoverFeaturesHandler discloses.
        // Alice queries Bob for goal-codes under org.example.*; the reply includes the lunch
        // goal-code our LunchGoalCodeProvider (below) contributes.
        var query = DiscoverFeatures.CreateQuery(aliceDid, bobDid,
            new FeatureQuery { FeatureType = DiscoverFeatures.FeatureTypeGoalCode, Match = "org.example.*" });

        var packed = (await client.PackEncryptedAsync(query, new PackEncryptedOptions(
            Recipients: new[] { bobDid }, From: aliceDid))).Message;
        var received = await client.UnpackAsync(packed);
        var outcome = await dispatcher.DispatchAsync(received, client, options);

        narrator.Value("Disclose produced", outcome.Result == DispatchResult.ReplyProduced);
        var disclosures = outcome.Reply is null
            ? Array.Empty<FeatureDisclosure>()
            : DiscoverFeatures.ReadDisclosures(outcome.Reply).ToArray();
        foreach (var d in disclosures)
            narrator.Value("- disclosed", $"{d.FeatureType}: {d.Id}");
    }

    // ── 10. Trace 2.0 — off unless an operator says otherwise ───────────────────────────

    private static void Tracing(Narrator narrator, TraceOptions configured, string aliceDid, string bobDid)
    {
        narrator.Section("10", "Trace 2.0 — the opt-in posture (FR-PROTO-11/11a)");

        // A trace header asks the recipient to POST a trace-report to a peer-chosen URI. That
        // is a tracking vector and an SSRF amplifier if honored blindly — so by default the
        // library ignores it completely: with fresh (unconfigured) TraceOptions, a trace
        // header alone produces NO report.
        var traced = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(aliceDid)
            .WithTo(bobDid)
            .WithBody(JsonNode.Parse("""{"content":"trace me"}""")!.AsObject())
            .Build();
        traced.AdditionalHeaders = new Dictionary<string, JsonElement>
        {
            [TraceApi.HeaderName] = JsonSerializer.SerializeToElement(
                new Dictionary<string, string> { [TraceApi.ReportUriField] = "https://trace.example/didcomm-reports" }),
        };

        narrator.Step("Default posture: no EnableTracing call means no trace-report, ever.");
        narrator.Value("ShouldReport (defaults)", TraceObserver.ShouldReport(traced, new TraceOptions(), out _));

        // This container DID opt in via b.EnableTracing(...) with an explicit allowlist, so
        // the same header is now honored — the dispatcher logs the authorized report intent.
        narrator.Step("This container opted in with an allowlist; the same header is honored.");
        var honored = TraceObserver.ShouldReport(traced, configured, out var reportUri);
        narrator.Value("ShouldReport (opted in, allowlisted)", honored);
        narrator.Value("Report URI", reportUri);

        // A report_uri OFF the allowlist is silently dropped even though tracing is enabled.
        traced.AdditionalHeaders[TraceApi.HeaderName] = JsonSerializer.SerializeToElement(
            new Dictionary<string, string> { [TraceApi.ReportUriField] = "https://attacker.example/exfil" });
        narrator.Value("ShouldReport (opted in, NOT allowlisted)", TraceObserver.ShouldReport(traced, configured, out _));

        // And you cannot opt in sloppily: Enabled = true with an empty allowlist is refused at
        // composition time, not discovered in production.
        try
        {
            new ServiceCollection().AddDidComm(b =>
            {
                b.UseNetDidResolver();
                b.UseSecretsResolver(new InMemorySecretsResolver());
                b.EnableTracing(o => o.Enabled = true); // no AllowedReportingUris
            });
            narrator.Value("EnableTracing without an allowlist", "UNEXPECTED — accepted");
        }
        catch (InvalidOperationException)
        {
            narrator.Value("EnableTracing without an allowlist", "refused at composition time");
        }
    }
}

/// <summary>
/// The custom handler for the spec's <c>lets_do_lunch</c> example protocol: Bob accepts every
/// proposal with <c>{"accepted": true}</c> threaded back to the proposal's id. One class plus
/// <c>b.AddProtocol&lt;LunchHandler&gt;()</c> is the entire extension story (FR-PROTO-03).
/// </summary>
public sealed class LunchHandler : IProtocolHandler
{
    /// <summary>The protocol identifier the dispatcher routes on.</summary>
    public const string ProtocolUriValue = "https://didcomm.org/lets-do-lunch/1.0";

    /// <summary>The proposal message type.</summary>
    public const string ProposalType = "https://didcomm.org/lets-do-lunch/1.0/proposal";

    /// <summary>The response message type.</summary>
    public const string ResponseType = "https://didcomm.org/lets-do-lunch/1.0/response";

    /// <inheritdoc />
    public string ProtocolUri => ProtocolUriValue;

    /// <inheritdoc />
    public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        // Only proposals get an answer, and only when there is someone to answer to.
        if (!string.Equals(message.Type, ProposalType, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<Message?>(null);
        if (string.IsNullOrEmpty(message.From) || message.To is not { Count: > 0 })
            return Task.FromResult<Message?>(null);

        var reply = new MessageBuilder()
            .WithType(ResponseType)
            .WithFrom(message.To[0])
            .WithTo(message.From)
            .WithThid(message.Id)
            .WithBody(new JsonObject { ["accepted"] = true })
            .Build();
        return Task.FromResult<Message?>(reply);
    }
}

/// <summary>
/// A read-only audit trail over inbound report-problem traffic (FR-PROTO-12). Registered via
/// <c>b.AddProtocolObserver&lt;ProblemReportAuditObserver&gt;()</c>, it records each observed
/// report's code and envelope-authentication flag without ever replacing — or even touching —
/// the built-in <c>ProblemReportHandler</c>. Delivery arrives on a background pump, so the
/// sample waits on <see cref="WaitForAsync"/> instead of sleeping.
/// </summary>
public sealed class ProblemReportAuditObserver : IProtocolObserver
{
    private readonly ConcurrentQueue<(string Code, bool Authenticated)> _observed = new();
    private readonly SemaphoreSlim _arrived = new(0);

    /// <summary>Least privilege: this observer sees only report-problem traffic.</summary>
    public string? ProtocolUriFilter => ProblemReportApi.ProtocolUri;

    /// <summary>Everything observed so far, in arrival order.</summary>
    public IReadOnlyList<(string Code, bool Authenticated)> Observed => _observed.ToArray();

    /// <inheritdoc />
    public Task OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct)
    {
        _observed.Enqueue((ProblemReportApi.ReadCode(observation.Message) ?? "<none>", observation.Authenticated));
        _arrived.Release();
        return Task.CompletedTask;
    }

    /// <summary>Wait until <paramref name="count"/> observations have arrived (or the timeout elapses).</summary>
    /// <param name="count">The number of deliveries to wait for.</param>
    /// <param name="timeout">Upper bound on the wait.</param>
    public async Task<bool> WaitForAsync(int count, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            for (var i = 0; i < count; i++)
                await _arrived.WaitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// A consumer-supplied Discover Features provider (FR-PROTO-05): advertises the lunch
/// goal-code so peers querying <c>goal-code</c> features learn this agent will negotiate
/// lunch. Registered via <c>b.AddFeatureProvider&lt;LunchGoalCodeProvider&gt;()</c>.
/// </summary>
public sealed class LunchGoalCodeProvider : IFeatureProvider
{
    /// <summary>The goal-code this agent advertises.</summary>
    public const string GoalCode = "org.example.lunch";

    /// <inheritdoc />
    public string FeatureType => DiscoverFeatures.FeatureTypeGoalCode;

    /// <inheritdoc />
    public IEnumerable<FeatureDisclosure> Disclose(string match, ProtocolContext context)
    {
        if (FeatureMatch.Matches(match, GoalCode))
            yield return new FeatureDisclosure { FeatureType = FeatureType, Id = GoalCode };
    }
}
