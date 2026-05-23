namespace DidComm.Exceptions;

/// <summary>
/// Base exception for every error surfaced by DidComm.Core. Per <c>FR-API-07</c>, concrete
/// failure paths derive from this type so callers can pattern-match on category
/// (<see cref="MalformedMessageException"/>, <see cref="ConsistencyException"/>, etc.)
/// without referencing low-level crypto or transport exceptions.
/// </summary>
public class DidCommException : Exception
{
    /// <summary>Initialize an empty exception.</summary>
    public DidCommException() { }

    /// <summary>Initialize with a message.</summary>
    /// <param name="message">Human-readable failure reason.</param>
    public DidCommException(string message) : base(message) { }

    /// <summary>Initialize with a message and inner exception.</summary>
    /// <param name="message">Human-readable failure reason.</param>
    /// <param name="innerException">The underlying exception, if any.</param>
    public DidCommException(string message, Exception innerException) : base(message, innerException) { }
}
