namespace DidComm.Exceptions;

/// <summary>
/// Thrown for any cryptographic failure inside the envelope layer: decrypt rejection
/// (tampered ciphertext / tag), signature verification failure, off-curve <c>epk</c>
/// detection (FR-ENC-03), key-wrap unwrap failure, ECDH on mismatched curves, etc.
/// Per FR-API-07 callers see this category exception instead of a raw
/// <see cref="CryptographicException"/> so they can handle crypto errors uniformly.
/// </summary>
public sealed class CryptoException : DidCommException
{
    /// <summary>Initialize with a message.</summary>
    /// <param name="message">Human-readable failure reason.</param>
    public CryptoException(string message) : base(message) { }

    /// <summary>Initialize with a message and the underlying cryptographic exception.</summary>
    /// <param name="message">Human-readable failure reason.</param>
    /// <param name="innerException">The underlying exception (typically <see cref="CryptographicException"/>).</param>
    public CryptoException(string message, Exception innerException) : base(message, innerException) { }
}
