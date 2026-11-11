using System.Text.Json;
using DidComm.Consistency;
using DidComm.Messages;

namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// The parsed <c>return_route</c> / <c>return_route_thread</c> header pair of one message
/// (PRD §8a, FR-RR-01). <see cref="TryRead"/> never throws: a malformed header is reported as
/// <see cref="None"/> with a <c>false</c> return so the caller can log it (by message id only) and
/// dispatch anyway — the header can never fail a message.
/// </summary>
/// <remarks>
/// <para>
/// The constants and the parser live on this struct rather than on a static class named
/// <c>ReturnRoute</c>: such a class would shadow the <c>DidComm.Transports.ReturnRoute</c> namespace
/// for every consumer that imports it.
/// </para>
/// <para>
/// The library neither owns nor locks the <see cref="Message"/> it reads (FR-CONSIST-04 note): a
/// caller that mutates <see cref="Message.AdditionalHeaders"/> concurrently with a read sees an
/// unspecified result, which is that caller's bug, not a wire condition.
/// </para>
/// </remarks>
/// <param name="Mode">The requested mode. <see cref="ReturnRouteMode.None"/> when the header is absent or malformed.</param>
/// <param name="Thread">The <c>return_route_thread</c> value; non-null only when <paramref name="Mode"/> is <see cref="ReturnRouteMode.Thread"/>.</param>
public readonly record struct ReturnRouteHeader(ReturnRouteMode Mode, string? Thread)
{
    /// <summary>The absent / <c>none</c> header: no messages are returned over the connection.</summary>
    public static readonly ReturnRouteHeader None = new(ReturnRouteMode.None, null);

    /// <summary>The header name: <c>return_route</c>.</summary>
    public const string HeaderName = "return_route";

    /// <summary>The companion header carrying the thread for <see cref="ReturnRouteMode.Thread"/>: <c>return_route_thread</c>.</summary>
    public const string ThreadHeaderName = "return_route_thread";

    /// <summary>The wire value for <see cref="ReturnRouteMode.None"/>.</summary>
    public const string NoneValue = "none";

    /// <summary>The wire value for <see cref="ReturnRouteMode.All"/>.</summary>
    public const string AllValue = "all";

    /// <summary>The wire value for <see cref="ReturnRouteMode.Thread"/>.</summary>
    public const string ThreadValue = "thread";

    /// <summary>
    /// The longest <c>return_route_thread</c> value accepted (2048 characters, the same bound as a
    /// DID, FR-API-09). A longer value is treated as a malformed header: parsed as <see cref="None"/>,
    /// never stored, never logged.
    /// </summary>
    public const int MaxThreadLength = IdentifierLimits.MaxDidLength;

    /// <summary>
    /// Read the return-route header of <paramref name="message"/>. Returns <c>true</c> when the
    /// header is absent or well-formed and <c>false</c> when a present header was treated as
    /// <see cref="None"/> because it was malformed: a non-string value, a value other than
    /// <c>none</c> / <c>all</c> / <c>thread</c> (case-sensitive), or <c>thread</c> without a usable
    /// <c>return_route_thread</c> (absent, not a string, blank, or longer than
    /// <see cref="MaxThreadLength"/>). The caller logs on <c>false</c>, by message id only. Never
    /// throws except <see cref="ArgumentNullException"/> for a null message.
    /// </summary>
    /// <param name="message">The message whose headers to read.</param>
    /// <param name="header">The parsed header; <see cref="None"/> when absent or malformed.</param>
    public static bool TryRead(Message message, out ReturnRouteHeader header)
    {
        ArgumentNullException.ThrowIfNull(message);
        header = None;

        var headers = message.AdditionalHeaders;
        if (headers is null || !headers.TryGetValue(HeaderName, out var element))
            return true;
        if (element.ValueKind != JsonValueKind.String)
            return false;

        switch (element.GetString())
        {
            case NoneValue:
                return true;
            case AllValue:
                header = new ReturnRouteHeader(ReturnRouteMode.All, null);
                return true;
            case ThreadValue:
                if (!headers.TryGetValue(ThreadHeaderName, out var threadElement)
                    || threadElement.ValueKind != JsonValueKind.String)
                {
                    return false;
                }
                var thread = threadElement.GetString();
                if (string.IsNullOrWhiteSpace(thread) || thread.Length > MaxThreadLength)
                    return false;
                header = new ReturnRouteHeader(ReturnRouteMode.Thread, thread);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Read the return-route header of <paramref name="message"/>, treating a malformed header as
    /// <see cref="None"/> silently. The dispatcher uses this so the endpoint, which already logged
    /// the malformed value once through <see cref="TryRead"/>, is the only logging site.
    /// </summary>
    /// <param name="message">The message whose headers to read.</param>
    public static ReturnRouteHeader Read(Message message)
    {
        TryRead(message, out var header);
        return header;
    }
}
