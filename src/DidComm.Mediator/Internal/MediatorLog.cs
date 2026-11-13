using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Internal;

/// <summary>
/// The logging and cancellation habits shared by the mediator-role handlers: a log call host code
/// cannot turn into a behaviour change, message ids that are bounded before they are written, the
/// "first and every 1000th" rule for lines any stranger can cause, and the rule for what leaves a
/// fault boundary when the dispatch token is cancelled.
/// </summary>
internal static class MediatorLog
{
    /// <summary>Message ids longer than this are replaced in log lines, not written.</summary>
    internal const int MaxLoggedIdLength = 128;

    /// <summary>A line any stranger can cause is written at Warning the first time and every this-many-th time, at Debug otherwise.</summary>
    internal const long RateLimitEvery = 1000;

    /// <summary>Write a log line; a throwing logging provider changes nothing.</summary>
    public static void Write(ILogger? logger, LogLevel level, string message, params object?[] args)
    {
        if (logger is null)
            return;
        try
        {
            logger.Log(level, message, args);
        }
        catch
        {
            // Logging providers are host code; they must not change dispatch.
        }
    }

    /// <summary>
    /// A message id for a log line. Ids are attacker-chosen and not length-bounded by the message
    /// model, so a long one is replaced, not written.
    /// </summary>
    public static string LogId(string? messageId)
        => messageId is null ? "(none)"
            : messageId.Length <= MaxLoggedIdLength ? messageId
            : $"(id of {messageId.Length} characters)";

    /// <summary>
    /// Count one more occurrence of a stranger-triggerable event and say at which level to log it:
    /// Warning for the first and every <see cref="RateLimitEvery"/>-th, Debug otherwise.
    /// </summary>
    public static LogLevel RateLimited(ref long counter, out long count)
    {
        count = Interlocked.Increment(ref counter);
        return count == 1 || count % RateLimitEvery == 0 ? LogLevel.Warning : LogLevel.Debug;
    }

    /// <summary>
    /// Inside a fault boundary: when the dispatch token is cancelled, cancellation is what leaves,
    /// never the collaborator's own exception (a driver's "cancelled by user" error carries its text).
    /// </summary>
    public static void ThrowIfCancelled(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
    }
}
