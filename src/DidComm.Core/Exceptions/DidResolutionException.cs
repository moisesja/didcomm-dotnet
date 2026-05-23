namespace DidComm.Exceptions;

/// <summary>
/// Thrown when the DID resolver fails to produce a usable DID document, or when the resolved
/// document is structurally valid but cannot be mapped to the cryptographic key material this
/// library understands (e.g. an unsupported curve under <c>keyAgreement</c>). FR-DID-01..05.
/// </summary>
/// <remarks>
/// This is distinct from <see cref="UnsupportedDidMethodException"/> (raised before resolution
/// is attempted, for methods we deliberately refuse) and from <see cref="CryptoException"/>
/// (raised for actual decrypt/verify failures).
/// </remarks>
public sealed class DidResolutionException : DidCommException
{
    /// <summary>The DID whose resolution or key extraction failed.</summary>
    public string Did { get; }

    /// <summary>The reason text included in the formatted message.</summary>
    public string Reason { get; }

    /// <summary>Initialize with the offending DID and a human-readable reason.</summary>
    /// <param name="did">The full DID string.</param>
    /// <param name="reason">Why resolution or key extraction failed.</param>
    public DidResolutionException(string did, string reason)
        : base($"DID resolution failed for '{did}': {reason}.")
    {
        Did = did;
        Reason = reason;
    }

    /// <summary>Initialize with the offending DID, a reason, and an inner exception.</summary>
    /// <param name="did">The full DID string.</param>
    /// <param name="reason">Why resolution or key extraction failed.</param>
    /// <param name="innerException">The underlying exception from the resolver, if any.</param>
    public DidResolutionException(string did, string reason, Exception innerException)
        : base($"DID resolution failed for '{did}': {reason}.", innerException)
    {
        Did = did;
        Reason = reason;
    }
}
