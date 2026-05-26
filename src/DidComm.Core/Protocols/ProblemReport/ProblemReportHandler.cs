using DidComm.Messages;
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
/// "happy path" is to read the code, increment <see cref="DidComm.Threading.ThreadState.ErrorCount"/>
/// for error-sorter codes, and return <c>null</c>.
/// </para>
/// <para>
/// The one exception is the FR-PROTO-10 cascade guard: once a thread accumulates more than
/// <see cref="ProblemReportOptions.CascadeThreshold"/> inbound error reports, the handler
/// emits exactly ONE <see cref="ProblemReport.MaxErrorsExceededCode"/>
/// (<c>e.p.req.max-errors-exceeded</c>) to halt the cascade, then returns <c>null</c> for
/// every subsequent report on the same <c>thid</c>.
/// </para>
/// </remarks>
public sealed class ProblemReportHandler : IProtocolHandler
{
    private readonly ProblemReportOptions _options;
    private readonly ILogger<ProblemReportHandler>? _logger;

    /// <summary>Construct the handler with explicit options + optional logger.</summary>
    /// <param name="options">Handler options (cascade threshold, etc.).</param>
    /// <param name="logger">Optional structured logger.</param>
    public ProblemReportHandler(IOptions<ProblemReportOptions> options, ILogger<ProblemReportHandler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
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
            _logger?.LogInformation(
                "Received a problem-report with an absent or malformed code; ignoring (FR-PROTO-08). Pthid={Pthid}",
                message.Pthid);
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
        var failingThread = context.Threads.GetOrCreate(message.Pthid);
        failingThread.ErrorCount++;
        _logger?.LogInformation(
            "Inbound problem-report on thread {Pthid}: code={Code}, errorCount={Count}/{Threshold}",
            message.Pthid, code.Value, failingThread.ErrorCount, _options.CascadeThreshold);

        // Already past the threshold — the cascade-stop notice was emitted on the trip, so
        // every subsequent report on this thread is silently ignored.
        if (failingThread.ErrorCount > _options.CascadeThreshold + 1)
            return Task.FromResult<Message?>(null);

        // Exactly one cascade-stop emission, the moment the count first exceeds the threshold.
        if (failingThread.ErrorCount == _options.CascadeThreshold + 1)
        {
            if (string.IsNullOrEmpty(message.From) || message.To is not { Count: > 0 } || string.IsNullOrEmpty(message.To[0]))
                return Task.FromResult<Message?>(null);
            var stop = ProblemReport.Create(
                from: message.To[0],
                to: message.From,
                code: ProblemReport.MaxErrorsExceededCode,
                pthid: message.Pthid,
                comment: "Per-thread error budget exceeded ({1} reports). Halting further responses on this thread.",
                args: new[] { failingThread.ErrorCount.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            _logger?.LogWarning(
                "Cascade guard tripped on thread {Pthid} after {Count} errors; emitting {Code}.",
                message.Pthid, failingThread.ErrorCount, ProblemReport.MaxErrorsExceededCode);
            return Task.FromResult<Message?>(stop);
        }

        return Task.FromResult<Message?>(null);
    }
}
