namespace DidComm.Exceptions;

/// <summary>
/// Thrown by the facade when a private key required to satisfy a pack request — a signer key,
/// an authcrypt sender key, or a recipient private key for the local agent — cannot be
/// supplied by the registered <c>ISecretsResolver</c> (FR-SEC-01, FR-SEC-02). Decryption of an
/// inbound multi-recipient JWE does <em>not</em> raise this: a missing recipient secret there
/// simply means "the message wasn't addressed to a key we hold" and is treated as "skip".
/// </summary>
public sealed class SecretNotFoundException : DidCommException
{
    /// <summary>The key identifier (typically a DID URL with a fragment) that was missing.</summary>
    public string Kid { get; }

    /// <summary>Initialize with the missing kid.</summary>
    /// <param name="kid">The kid the secrets resolver could not provide.</param>
    public SecretNotFoundException(string kid)
        : base($"No private key found for kid '{kid}' — the registered ISecretsResolver returned null.")
    {
        Kid = kid;
    }
}
