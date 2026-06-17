using DidComm.Facade;
using DidComm.Protocols;
using DidComm.Protocols.ProblemReport;
using DidComm.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// L-014.
using ProblemReportApi = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Walks the Report Problem 2.0 surface end to end: parse a structured problem-code,
/// interpolate a {n}-placeholder comment against args, escalate a warning to an error with
/// preserved scope, and trip the FR-PROTO-10 cascade guard by feeding the handler more
/// errors than its per-thread budget allows.
/// </summary>
/// <remarks>
/// <para>
/// The cookbook ratchets the cascade threshold down to 2 for this section so the trip is
/// visible in three packed messages. In production the default is 5 (matching
/// <c>sicpa-dlab/didcomm-python</c>).
/// </para>
/// <para>Maps to PRD §14.2 task <strong>U</strong> (FR-PROTO-07 / FR-PROTO-08 / FR-PROTO-09 / FR-PROTO-10).</para>
/// </remarks>
public static class Section_U_ProblemReport
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("U", "Report Problem (taxonomy, interpolation, escalation, cascade)");

        // 1. Parse and inspect a structured code.
        var parsed = ProblemCode.Parse("e.p.xfer.cant-use-endpoint");
        ctx.Narrator.Step("Parse e.p.xfer.cant-use-endpoint and inspect its taxonomy parts.");
        ctx.Narrator.Value("Sorter / Scope / Descriptor", $"{parsed.Sorter} / {parsed.Scope} / {parsed.Descriptor}");
        ctx.Narrator.Value("IsError / IsProtocolScoped", $"{parsed.IsError} / {parsed.IsProtocolScoped}");
        ctx.Narrator.Value("StartsWith(\"e.p.xfer\")", parsed.StartsWith("e.p.xfer"));

        // 2. Build a problem-report with placeholder interpolation.
        var failingThread = "lunch-thread-1";
        var report = ProblemReportApi.Create(
            from: ctx.Alice.Did,
            to: ctx.Bob.Did,
            code: "e.p.xfer.cant-use-endpoint",
            pthid: failingThread,
            comment: "Could not deliver to {1} for {2}.",
            args: new[] { "https://agents.r.us/inbox", ctx.Bob.Did });
        ctx.Narrator.Step("Alice builds a problem-report with {n} interpolation.");
        ctx.Narrator.Value("Interpolated comment", ProblemReportApi.RenderComment(report));

        // 3. Escalation helper — warnings → errors with preserved scope.
        var originalWarning = ProblemCode.Parse("w.m.xfer.slow");
        var escalated = ProblemReportApi.Escalate(
            from: ctx.Bob.Did, to: ctx.Alice.Did,
            originalCode: originalWarning,
            escalatedDescriptor: "xfer.failed",
            pthid: failingThread);
        ctx.Narrator.Step("Escalate a warning to an error with FR-PROTO-09 scope-preservation.");
        ctx.Narrator.Value("Escalated code", ProblemReportApi.ReadCode(escalated));

        // 4. Cascade guard — drop the threshold to 2 so we see the trip in 3 messages.
        // We build a per-section dispatcher with a tighter ProblemReportOptions to keep the
        // demo from polluting the shared cookbook state.
        var tightHandler = new ProblemReportHandler(Options.Create(new ProblemReportOptions { CascadeThreshold = 2 }));
        var localRegistry = new ProtocolHandlerRegistry();
        localRegistry.Register(tightHandler);
        var localStore = new InMemoryThreadStateStore();
        var dispatcher = new ProtocolDispatcher(localRegistry, localStore);
        var options = ctx.ServiceProvider.GetRequiredService<IOptions<DidCommOptions>>().Value;

        ctx.Narrator.Step("Fire three error reports on the same pthid (threshold = 2).");
        DispatchOutcome? trip = null;
        for (var i = 1; i <= 3; i++)
        {
            var packed = (await ctx.Client.PackEncryptedAsync(report, new PackEncryptedOptions(
                Recipients: new[] { ctx.Bob.Did }, From: ctx.Alice.Did))).Message;
            var unpacked = await ctx.Client.UnpackAsync(packed);
            trip = await dispatcher.DispatchAsync(unpacked, ctx.Client, options);
            ctx.Narrator.Value($"  report #{i} outcome", trip.Result);
        }

        ctx.Narrator.Value("Cascade-stop code", ProblemReportApi.ReadCode(trip!.Reply!));
        ctx.Narrator.Value("Cascade-stop pthid", trip.Reply!.Pthid);
        // The breach count rides on the cascade-stop notice itself (the count is communicated to the
        // peer, not read from internal state — the FR-PROTO-10 budget lives in a dedicated store, #36).
        ctx.Narrator.Value("Cascade-stop comment", ProblemReportApi.RenderComment(trip.Reply!));
        ctx.Narrator.Note("Beyond the trip the handler returns null for further reports on the same pthid (FR-PROTO-10).");
    }
}
