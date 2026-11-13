using System.Security.Cryptography;

namespace DidComm.Mediator.Pickup;

/// <summary>
/// The ids of queued messages (FR-PICK-04): what a <c>delivery</c> carries as the attachment
/// <c>id</c> and a <c>messages-received</c> sends back. Minted by the queue itself from the
/// operating system's CSPRNG, never by the container's <c>IMessageIdGenerator</c>, so a host's
/// sequential or guessable generator cannot make one recipient's ids predictable from another's.
/// </summary>
internal static class PickupIds
{
    /// <summary>The bytes of randomness in an id.</summary>
    internal const int RandomBytes = 16;

    /// <summary>
    /// A fresh id: 16 random bytes (128 bits) as 32 lowercase hex characters — unreserved URI
    /// characters only, within <see cref="PickupSizing.MaxMintedIdLength"/>. Thread-safe.
    /// </summary>
    internal static string NewRandomId()
    {
        Span<byte> bytes = stackalloc byte[RandomBytes];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }
}
