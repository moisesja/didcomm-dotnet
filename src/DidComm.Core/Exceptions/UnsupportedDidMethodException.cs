namespace DidComm.Exceptions;

/// <summary>
/// Thrown when an entry point encounters a DID whose method is intentionally not supported by
/// this library (FR-DID-06 / FR-DID-07). The canonical case is <c>did:web</c>: per design
/// decision DD-08 the library refuses to resolve <c>did:web</c> at every entry point because
/// its trust model relies on DNS + Web PKI + domain control with no verifiable history or
/// pre-rotation, leaving the door open to silent key substitution; <c>did:webvh</c> is the
/// recommended replacement.
/// </summary>
/// <remarks>
/// The exception is raised at the API perimeter (pack/unpack arguments, every DID-bearing
/// header on the inner plaintext, mediator routing targets) before any envelope work is done,
/// so callers can react without leaking partial state.
/// </remarks>
public sealed class UnsupportedDidMethodException : DidCommException
{
    /// <summary>The DID method name (e.g. <c>"web"</c>).</summary>
    public string Method { get; }

    /// <summary>The full DID that was rejected.</summary>
    public string Did { get; }

    /// <summary>The reason text included in the formatted message.</summary>
    public string Reason { get; }

    /// <summary>Initialize with the offending method, DID, and a human-readable reason.</summary>
    /// <param name="method">The DID method (e.g. <c>"web"</c>).</param>
    /// <param name="did">The full DID string.</param>
    /// <param name="reason">Why this method is unsupported — surfaced to operators.</param>
    public UnsupportedDidMethodException(string method, string did, string reason)
        : base($"DID method '{method}' is not supported: {reason}. did='{did}'.")
    {
        Method = method;
        Did = did;
        Reason = reason;
    }
}
