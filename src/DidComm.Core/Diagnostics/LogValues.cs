using System.Buffers;

namespace DidComm.Diagnostics;

/// <summary>
/// How a value a stranger chose is written into a log line or a span tag: a message id bounded in
/// length, a message type bounded in length and limited to URI characters, and an exception by its
/// type names only (its message can quote wire values — a DID, a line break).
/// Shared by Core, <c>DidComm.AspNetCore</c> and <c>DidComm.Mediator.Client</c> (whose <c>MediatorLog</c> the mediator
/// roles in <c>DidComm.Mediator</c> use too), so every package bounds ids the same way.
/// </summary>
internal static class LogValues
{
    /// <summary>Message ids longer than this are replaced in log lines, not written.</summary>
    internal const int MaxIdLength = 128;

    /// <summary>Message types longer than this are replaced on span tags, not written.</summary>
    internal const int MaxTypeLength = 256;

    // The characters a URI can hold (RFC 3986 §2): unreserved, reserved, and '%' for its escapes.
    private static readonly SearchValues<char> UriCharacters = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~:/?#[]@!$&'()*+,;=%");

    /// <summary>
    /// A message id for a log line. Ids are attacker-chosen and not length-bounded by the message
    /// model, so a long one is replaced by its length, not written.
    /// </summary>
    /// <param name="messageId">The id as it arrived, or <c>null</c>.</param>
    internal static string Id(string? messageId)
        => messageId is null ? "(none)"
            : messageId.Length <= MaxIdLength ? messageId
            : $"(id of {messageId.Length} characters)";

    /// <summary>
    /// A message type for a span tag. A stranger chooses the type, and a host that copies a span's
    /// tags into its log scopes writes the tag on every line inside the span. The type grammar lets
    /// the protocol name hold line breaks and terminal escapes, and bounds its length only by the
    /// receive limit. So the type is written as it is only when it is at most
    /// <see cref="MaxTypeLength"/> characters, all of them URI characters (RFC 3986); otherwise a
    /// placeholder that does not echo it.
    /// </summary>
    /// <param name="type">The type as it arrived, or <c>null</c>.</param>
    internal static string MessageType(string? type)
        => type is null ? "(no type)"
            : type.Length > MaxTypeLength ? $"(type of {type.Length} characters)"
            : type.AsSpan().ContainsAnyExcept(UriCharacters) ? "(type with characters outside a URI)"
            : type;

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
