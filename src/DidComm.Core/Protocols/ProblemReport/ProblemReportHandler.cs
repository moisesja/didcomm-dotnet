using DidComm.Consistency;
using DidComm.Diagnostics;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Protocols.ProblemReport;

/// <summary>
/// Handler for Report Problem 2.0 (FR-PROTO-07/08/10).
/// </summary>
/// <remarks>
/// <para>
/// Problem-reports are informational by design — they tell the peer something went wrong on
/// a thread, but they do not themselves require an application-level reply. So this handler's
/// "happy path" is to read the code, record it against the failing thread's FR-PROTO-10 budget in
/// the dedicated <see cref="CascadeBudgetStore"/> (#36) for error-sorter codes, and return <c>null</c>.
/// </para>
/// <para>
/// The one exception is the FR-PROTO-10 cascade guard: once a thread accumulates more than
/// <see cref="ProblemReportOptions.CascadeThreshold"/> inbound error reports, the handler
/// emits exactly ONE <see cref="ProblemReport.MaxErrorsExceededCode"/>
/// (<c>e.p.req.max-errors-exceeded</c>) to halt the cascade, then returns <c>null</c> for
/// every subsequent report on the same <c>thid</c>.
/// </para>
/// <para>
/// The cascade stop is sent from the DID whose key decrypted the inbound report (its DID subject),
/// so a different tenant placed first in a multi-recipient <c>to</c> array cannot be made the
/// sender; a plaintext report or a direct call falls back to the first <c>to</c> entry, as the Trust
/// Ping and Discover Features responders do.
/// </para>
/// <para>
/// Any sender can cause each of the handler's log lines once per report, so each is rate-limited:
/// the first and every 1000th at its own level, the rest at Debug. They write the report's
/// <c>pthid</c> only bounded (<c>LogValues.Id</c>) and its <c>code</c> only when it is short lower
/// kebab-case (<c>LogValues.ProblemCode</c>); both are the sender's text.
/// </para>
/// </remarks>
public sealed class ProblemReportHandler : IProtocolHandler
{
    private readonly ProblemReportOptions _options;
    private readonly CascadeBudgetStore _cascadeBudget;
    private readonly ILogger<ProblemReportHandler>? _logger;
    private long _malformedCodes;  // occurrences of the malformed-code line (RateLimitedLog)
    private long _errorReports;    // occurrences of the counted-report line
    private long _cascadeTrips;    // occurrences of the cascade-trip line

    /// <summary>Construct the handler with explicit options, an optional dedicated cascade-budget store, and an optional logger.</summary>
    /// <param name="options">Handler options (cascade threshold, etc.). Validated eagerly at ctor so a misconfig surfaces at DI resolution rather than as silently-degraded cascade-guard behaviour.</param>
    /// <param name="cascadeBudget">
    /// The dedicated FR-PROTO-10 cascade-budget store (#36), kept separate from the dispatcher's general
    /// thread store. Defaults to a fresh process-local instance; the DI setup registers one as a
    /// singleton so the budget's lifetime is decoupled from the handler's (a handler accidentally
    /// registered non-singleton would otherwise lose its budget per request).
    /// </param>
    /// <param name="logger">Optional structured logger.</param>
    public ProblemReportHandler(
        IOptions<ProblemReportOptions> options,
        CascadeBudgetStore? cascadeBudget = null,
        ILogger<ProblemReportHandler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _options.Validate();
        _cascadeBudget = cascadeBudget ?? new CascadeBudgetStore();
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProtocolUri => ProblemReport.ProtocolUri;

    /// <inheritdoc />
    public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(message.Type, ProblemReport.MessageType, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<Message?>(null);

        var codeString = ProblemReport.ReadCode(message);
        if (string.IsNullOrEmpty(codeString) || !ProblemCode.TryParse(codeString, out var code))
        {
            var level = RateLimitedLog.Level(ref _malformedCodes, LogLevel.Information, out var malformed);
            _logger?.Log(
                level,
                "Received a problem-report with an absent or malformed code; ignoring (FR-PROTO-08). Pthid={Pthid}; {Count} so far.",
                LogValues.Id(message.Pthid), malformed);
            return Task.FromResult<Message?>(null);
        }

        // Only errors count toward the cascade budget. Warnings (sorter = 'w') are recorded
        // in logs but don't trip the guard.
        if (!code.IsError)
            return Task.FromResult<Message?>(null);

        // The FAILING thread the cascade guard tracks is named by the report's `pthid`
        // (FR-PROTO-07 — pthid REQUIRED). The report itself starts a fresh thread (its `thid`
        // is its own `id`), so context.Thread is the wrong scope; we resolve the failing
        // thread's state directly via the store.
        if (string.IsNullOrEmpty(message.Pthid))
            return Task.FromResult<Message?>(null);

        // FR-PROTO-10 cascade guard. The whole increment + threshold check + trip decision is done
        // ATOMICALLY inside the dedicated, bounded cascade-budget store (#36) — separate from the
        // dispatcher's general thread store (which a cheap-thid flood evicts) and self-synchronizing on
        // a stable pthid-keyed lock (so concurrent reports on the same pthid can't double-emit, even
        // under the store's LRU eviction). A report is repliable iff it has a usable reply target.
        // Our end is the DID whose key decrypted the report, else (plaintext, a direct call) the
        // first `to` entry. A decrypted report without `to` is still ours (DIDComm v2.1
        // message_structure.md, `to`: "If the header is omitted, each recipient SHOULD assume they
        // are the only recipient").
        var responder = DidSubject.DidSubjectOf(context.Received.RecipientKid)
            ?? (message.To is { Count: > 0 } && !string.IsNullOrEmpty(message.To[0]) ? message.To[0] : null);
        bool repliable = !string.IsNullOrEmpty(message.From) && responder is not null;
        var step = _cascadeBudget.RecordErrorReport(message.Pthid, _options.CascadeThreshold, repliable);

        if (step.Log)
        {
            var level = RateLimitedLog.Level(ref _errorReports, LogLevel.Information, out var reports);
            _logger?.Log(
                level,
                "Inbound problem-report on thread {Pthid}: code={Code}, errorCount={Count}/{Threshold}; {Reports} such lines so far.",
                LogValues.Id(message.Pthid), LogValues.ProblemCode(code.Value), step.Count, _options.CascadeThreshold, reports);
        }

        if (!step.Emit)
            return Task.FromResult<Message?>(null);

        var errorCountSnapshot = step.Count;

        // Build + log the cascade-stop OUTSIDE the per-thread lock — MessageBuilder allocations
        // and logging shouldn't contend on the lock.
        // The stop is sent by `responder` (above), as the other built-in responders do: `to` only
        // names the intended audience, and a recipient cannot tell whether the first entry is
        // itself (message_structure.md `to`).
        var stop = ProblemReport.Create(
            from: responder!,
            to: message.From!,
            code: ProblemReport.MaxErrorsExceededCode,
            pthid: message.Pthid,
            comment: "Per-thread error budget exceeded ({1} reports). Halting further responses on this thread.",
            args: new[] { errorCountSnapshot.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var tripLevel = RateLimitedLog.Level(ref _cascadeTrips, LogLevel.Warning, out var trips);
        _logger?.Log(
            tripLevel,
            "Cascade guard tripped on thread {Pthid} after {Count} errors; emitting {Code}. {Trips} trips so far.",
            LogValues.Id(message.Pthid), errorCountSnapshot, ProblemReport.MaxErrorsExceededCode, trips);
        return Task.FromResult<Message?>(stop);
    }
}
