using DidComm.Facade;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// The bounded memory of delivered messages already returned to the application, so a redelivery
/// is acknowledged but not returned twice (design D-15, §4.9). Keys are SHA-256 truncated to 128
/// bits: over (authenticated sender DID subject, decrypting DID subject, inner message id) for an
/// authenticated message — a stranger reusing someone's message id cannot suppress it — and over the
/// innermost packed bytes for an anonymous one. FIFO eviction at <see cref="Capacity"/>; capacity 0
/// turns it off (<see cref="Contains"/> is always <c>false</c>, <see cref="Add"/> does nothing).
/// Retained memory is O(capacity × 16 B) whatever the wire values (L-062). Thread-safe (one lock).
/// A key is added only at a commit point: in a fetch right before the acknowledgement, in a live
/// stream when the consumer asks for the next item. Owner: engineer A.
/// </summary>
internal sealed class DuplicateFilter
{
    /// <summary>Create a filter.</summary>
    /// <param name="capacity">How many keys to remember (<see cref="MediatorClientOptions.DedupeCapacity"/>, already validated); 0 = off.</param>
    internal DuplicateFilter(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        Capacity = capacity;
    }

    /// <summary>How many keys the filter remembers; 0 = off.</summary>
    internal int Capacity { get; }

    /// <summary>The key of a message that unpacked (see the class summary for what it covers).</summary>
    /// <param name="unpacked">The innermost unpack result.</param>
    /// <param name="innermostPacked">The packed text the innermost message was unpacked from (keyed for an anonymous sender).</param>
    internal static UInt128 KeyOf(UnpackResult unpacked, string innermostPacked)
        => throw new NotImplementedException("#96 engineer A: DuplicateFilter.KeyOf (design D-15).");

    /// <summary>Whether <paramref name="key"/> is remembered.</summary>
    /// <param name="key">A key from <see cref="KeyOf"/>.</param>
    internal bool Contains(UInt128 key)
        => throw new NotImplementedException("#96 engineer A: DuplicateFilter.Contains (design §4.9).");

    /// <summary>Remember <paramref name="key"/>, forgetting the oldest key when full; a key already remembered is not added twice.</summary>
    /// <param name="key">A key from <see cref="KeyOf"/>.</param>
    internal void Add(UInt128 key)
        => throw new NotImplementedException("#96 engineer A: DuplicateFilter.Add (design §4.9).");
}
