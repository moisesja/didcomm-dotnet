using DidComm.Exceptions;
using DidComm.Messages;

namespace DidComm.Threading;

/// <summary>
/// The thread a reply joins (FR-THR-01). DIDComm v2.1 threading: a message that begins a thread
/// MAY declare its own <c>thid</c>, and when it declares none its <c>id</c> is its <c>thid</c>
/// (threading.md L15); every later message in the thread MUST carry that same <c>thid</c>
/// (threading.md L17).
/// </summary>
internal static class ThreadIds
{
    /// <summary>
    /// The <c>thid</c> a reply to <paramref name="request"/> carries: the request's <c>thid</c> when
    /// it declares one, else its <c>id</c>. The value is returned as the request carries it.
    /// </summary>
    /// <remarks>
    /// A message that came through unpack was validated there, so its <c>thid</c> is either absent
    /// or valid. A message built by hand and passed straight to a reply builder was not: a
    /// <c>thid</c> it declares that is not a valid thread id (empty, or not entirely unreserved URI
    /// characters, as <c>id</c> must be) is refused rather than ignored, because falling back to the
    /// <c>id</c> would put the reply on a thread the requester never named. The refusal quotes no
    /// value.
    /// </remarks>
    /// <param name="request">The request being answered.</param>
    /// <exception cref="MalformedMessageException">The request declares a <c>thid</c> that is not a valid thread id.</exception>
    internal static string ResponseThreadId(Message request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Thid is null)
            return request.Id;
        if (!UnreservedUriChars.IsUnreserved(request.Thid))
            throw new MalformedMessageException("The request's 'thid' is not a valid thread id (FR-MSG-11).");
        return request.Thid;
    }
}
