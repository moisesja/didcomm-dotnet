namespace DidComm.Diagnostics;

/// <summary>
/// How a value a stranger chose is written into a log line: a message id bounded in length, and an
/// exception by its type names only (its message can quote wire values — a DID, a line break).
/// Shared by Core, <c>DidComm.AspNetCore</c> and <c>DidComm.Mediator</c>, so every package bounds
/// ids the same way.
/// </summary>
internal static class LogValues
{
    /// <summary>Message ids longer than this are replaced in log lines, not written.</summary>
    internal const int MaxIdLength = 128;

    /// <summary>
    /// A message id for a log line. Ids are attacker-chosen and not length-bounded by the message
    /// model, so a long one is replaced by its length, not written.
    /// </summary>
    /// <param name="messageId">The id as it arrived, or <c>null</c>.</param>
    internal static string Id(string? messageId)
        => messageId is null ? "(none)"
            : messageId.Length <= MaxIdLength ? messageId
            : $"(id of {messageId.Length} characters)";

    /// <summary>The type name of the innermost exception of <paramref name="exception"/>'s chain.</summary>
    /// <param name="exception">The outer exception.</param>
    internal static string InnermostTypeName(Exception exception)
    {
        var inner = exception;
        while (inner.InnerException is { } next)
            inner = next;
        return inner.GetType().Name;
    }
}
