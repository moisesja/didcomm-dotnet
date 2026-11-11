using System.Text.Json;
using DidComm.Messages;

namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// Writes the <c>return_route</c> / <c>return_route_thread</c> header pair (PRD §8a, FR-RR-01):
/// the one implementation behind <see cref="MessageBuilder.WithReturnRoute"/> and
/// <c>SendOptions.ReturnRoute</c>, so both produce byte-identical headers and enforce the same
/// shape rules.
/// </summary>
internal static class ReturnRouteHeaderWriter
{
    /// <summary>
    /// Check a mode and thread pair without writing anything. Throws
    /// <see cref="ArgumentOutOfRangeException"/> for an undefined <paramref name="mode"/>, and
    /// <see cref="ArgumentException"/> for <see cref="ReturnRouteMode.Thread"/> without a usable
    /// thread (null or whitespace, not made of RFC 3986 unreserved characters, or longer than
    /// <see cref="ReturnRouteHeader.MaxThreadLength"/>) or for a thread given with another mode.
    /// No exception message quotes the thread value.
    /// </summary>
    /// <param name="mode">The requested mode.</param>
    /// <param name="thread">The thread id; required for <see cref="ReturnRouteMode.Thread"/>, must be <c>null</c> otherwise.</param>
    /// <param name="modeParamName">The caller's parameter name for <paramref name="mode"/>, for the exception.</param>
    /// <param name="threadParamName">The caller's parameter name for <paramref name="thread"/>, for the exception.</param>
    internal static void Validate(ReturnRouteMode mode, string? thread, string modeParamName = "mode", string threadParamName = "thread")
    {
        _ = WireValue(mode, modeParamName);

        if (mode == ReturnRouteMode.Thread)
        {
            if (string.IsNullOrWhiteSpace(thread))
                throw new ArgumentException("return_route 'thread' requires a return_route_thread value.", threadParamName);
            if (!UnreservedUriChars.IsUnreserved(thread))
                throw new ArgumentException("return_route_thread must consist of RFC 3986 unreserved characters only (FR-MSG-11).", threadParamName);
            if (thread.Length > ReturnRouteHeader.MaxThreadLength)
                throw new ArgumentException($"return_route_thread must be at most {ReturnRouteHeader.MaxThreadLength} characters.", threadParamName);
        }
        else if (thread is not null)
        {
            throw new ArgumentException("A return_route_thread value is only valid with ReturnRouteMode.Thread.", threadParamName);
        }
    }

    /// <summary>
    /// Validate (as <see cref="Validate"/>) and write the header pair into
    /// <paramref name="message"/>'s <see cref="Message.AdditionalHeaders"/> as JSON strings, so the
    /// serialized message carries exactly <c>"return_route":"all"</c>. Replaces an existing
    /// <c>return_route</c>; writes <c>return_route_thread</c> for <see cref="ReturnRouteMode.Thread"/>
    /// and removes a stale one for any other mode. Nothing is written when validation fails.
    /// </summary>
    /// <param name="message">The message to write into (the caller owns it).</param>
    /// <param name="mode">The requested mode.</param>
    /// <param name="thread">The thread id for <see cref="ReturnRouteMode.Thread"/>; <c>null</c> otherwise.</param>
    /// <param name="modeParamName">The caller's parameter name for <paramref name="mode"/>, for the exception.</param>
    /// <param name="threadParamName">The caller's parameter name for <paramref name="thread"/>, for the exception.</param>
    internal static void Write(Message message, ReturnRouteMode mode, string? thread, string modeParamName = "mode", string threadParamName = "thread")
    {
        ArgumentNullException.ThrowIfNull(message);
        Validate(mode, thread, modeParamName, threadParamName);
        var value = WireValue(mode, modeParamName);

        message.AdditionalHeaders ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        message.AdditionalHeaders[ReturnRouteHeader.HeaderName] = JsonSerializer.SerializeToElement(value);
        if (mode == ReturnRouteMode.Thread)
            message.AdditionalHeaders[ReturnRouteHeader.ThreadHeaderName] = JsonSerializer.SerializeToElement(thread);
        else
            message.AdditionalHeaders.Remove(ReturnRouteHeader.ThreadHeaderName);
    }

    private static string WireValue(ReturnRouteMode mode, string modeParamName) => mode switch
    {
        ReturnRouteMode.None => ReturnRouteHeader.NoneValue,
        ReturnRouteMode.All => ReturnRouteHeader.AllValue,
        ReturnRouteMode.Thread => ReturnRouteHeader.ThreadValue,
        _ => throw new ArgumentOutOfRangeException(modeParamName, "Not a defined ReturnRouteMode."),
    };
}
