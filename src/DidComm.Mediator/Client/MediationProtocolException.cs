using DidComm.Exceptions;

namespace DidComm.Mediator.Client;

/// <summary>
/// A mediator answered a request of <see cref="MediationClient"/> or <see cref="PickupClient"/> with a
/// problem report, or with an answer that is malformed or does not match the request (FR-MED-09,
/// FR-PICK-11). The message text never repeats a value the mediator sent: the request id (ours)
/// and, for a problem report, its well-formed code are on the properties.
/// </summary>
/// <remarks>
/// A malformed answer carries the parse failure as <see cref="Exception.InnerException"/>
/// (usually a <see cref="MalformedMessageException"/>). Repeating the request is safe; whether it
/// helps depends on the code (for example <c>e.m.live-mode-not-supported</c> will not change).
/// </remarks>
/// <example>
/// <code>
/// catch (MediationProtocolException ex) when (ex.ProblemCode == "e.m.live-mode-not-supported")
/// {
///     // Poll with FetchAsync instead.
/// }
/// </code>
/// </example>
public class MediationProtocolException : DidCommException
{
    /// <summary>Initialize with the request the answer was for.</summary>
    /// <param name="message">What went wrong, without any value from the wire.</param>
    /// <param name="requestId">The id of our request.</param>
    /// <param name="problemCode">The sanitized problem code (see <see cref="ProblemCode"/>), or <c>null</c>. The caller sanitizes it.</param>
    /// <param name="innerException">The parse failure behind a malformed answer, or <c>null</c>.</param>
    internal MediationProtocolException(string message, string requestId, string? problemCode = null, Exception? innerException = null)
        : base(message, innerException!)
    {
        RequestId = requestId;
        ProblemCode = problemCode;
    }

    /// <summary>The id of the request the mediator answered: a UUID this agent generated, safe to log.</summary>
    public string RequestId { get; }

    /// <summary>
    /// The code of the mediator's problem report (for example <c>e.m.live-mode-not-supported</c>),
    /// when the answer was one and its code is well formed: at most 128 characters of <c>a</c>–<c>z</c>,
    /// <c>0</c>–<c>9</c>, <c>.</c>, <c>_</c> and <c>-</c>, parsing as a problem code. <c>null</c>
    /// otherwise, including for an answer that was malformed or did not match the request.
    /// </summary>
    public string? ProblemCode { get; }
}
