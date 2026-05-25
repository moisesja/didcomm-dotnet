namespace DidComm.Exceptions;

/// <summary>
/// Raised when a transport operation (send or receive) fails. Per FR-API-07, callers
/// pattern-match on this category without depending on the concrete HTTP/WebSocket details.
/// Concrete failure paths include: a transport router with no transport registered for the
/// target URI's scheme (FR-TRN-01), a non-2xx HTTP response (FR-TRN-04/05), a 301/308 redirect
/// the transport refuses to follow (FR-TRN-06), and WebSocket connect / send / close errors
/// after the retry budget is exhausted (FR-TRN-11).
/// </summary>
public sealed class TransportException : DidCommException
{
    /// <summary>The HTTP status code that triggered the failure, when applicable.</summary>
    public int? HttpStatusCode { get; }

    /// <summary>The transport-scheme that handled (or failed to find) the request, when known.</summary>
    public string? Scheme { get; }

    /// <summary>Initialize with a message.</summary>
    /// <param name="message">Human-readable failure reason.</param>
    public TransportException(string message) : base(message) { }

    /// <summary>Initialize with a message and inner exception.</summary>
    /// <param name="message">Human-readable failure reason.</param>
    /// <param name="innerException">The underlying exception, if any.</param>
    public TransportException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Initialize with HTTP-status / scheme context for the FR-TRN-04..06 surface.</summary>
    /// <param name="message">Human-readable failure reason.</param>
    /// <param name="httpStatusCode">HTTP status that triggered the failure, or <c>null</c> for non-HTTP transports.</param>
    /// <param name="scheme">Scheme of the offending URI (e.g. <c>"https"</c>, <c>"wss"</c>), or <c>null</c> when unknown.</param>
    public TransportException(string message, int? httpStatusCode, string? scheme) : base(message)
    {
        HttpStatusCode = httpStatusCode;
        Scheme = scheme;
    }
}
