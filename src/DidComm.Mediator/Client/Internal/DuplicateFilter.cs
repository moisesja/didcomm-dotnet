using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DidComm.Consistency;
using DidComm.Facade;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// The bounded memory of delivered messages already returned to the application, so a redelivery
/// is acknowledged but not returned twice (design D-15, §4.9). A message's key
/// (<see cref="KeyOf"/>) is SHA-256 truncated to 128 bits: over (authenticated sender DID subject,
/// decrypting DID subject, inner message id) for an authenticated message — a stranger reusing
/// someone's message id cannot suppress it — and over the innermost packed bytes for an anonymous
/// one. What the filter remembers is that key scoped to the mailbox it was fetched from (the DID
/// subject of the <c>from</c> of the fetch or live stream; ADV1-1): a copy of one DID's message that
/// a third party relays into another DID's mailbox can never make the genuine owner's copy count as
/// a repeat, while the copies of one message in one mailbox (a wallet whose DIDs share one grant)
/// are still returned once. FIFO eviction at <see cref="Capacity"/>; capacity 0 turns it off
/// (<see cref="Contains"/> is always <c>false</c>, <see cref="Add"/> does nothing). Retained memory is
/// O(capacity × 16 B) whatever the wire values (L-062). Thread-safe (one lock). A key is added only at
/// a commit point: in a fetch right before the acknowledgement, in a live stream when the consumer
/// asks for the next item. Owner: engineer A.
/// </summary>
internal sealed class DuplicateFilter
{
    private readonly object _gate = new();
    private readonly HashSet<UInt128> _keys = new();
    private readonly Queue<UInt128> _order = new();

    /// <summary>Create a filter.</summary>
    /// <param name="capacity">How many keys to remember (<see cref="MediatorClientOptions.DedupeCapacity"/>, already validated); 0 = off.</param>
    internal DuplicateFilter(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        Capacity = capacity;
    }

    /// <summary>How many keys the filter remembers; 0 = off.</summary>
    internal int Capacity { get; }

    /// <summary>How many keys are remembered now (test seam).</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
                return _keys.Count;
        }
    }

    /// <summary>The key of a message that unpacked (see the class summary for what it covers), before it is scoped to a mailbox (<see cref="InMailbox"/>).</summary>
    /// <param name="unpacked">The innermost unpack result.</param>
    /// <param name="innermostPacked">The packed text the innermost message was unpacked from (keyed for an anonymous sender).</param>
    internal static UInt128 KeyOf(UnpackResult unpacked, string innermostPacked)
    {
        ArgumentNullException.ThrowIfNull(unpacked);
        ArgumentNullException.ThrowIfNull(innermostPacked);

        // The authenticated sender is the authcrypt sender key's DID subject, else the verified
        // signer's. Message ids are unique per sender only (DIDComm v2.1 §Message Headers), so the
        // sender is part of the key: a stranger who reuses someone else's message id produces a
        // different key and cannot suppress that message. Without an authenticated sender there is
        // no identity to bind the id to, so the key is over the exact bytes delivered: a replayed
        // or redelivered copy of the same message matches, a different message with the same id
        // does not.
        var sender = unpacked.Authenticated || unpacked.NonRepudiation
            ? DidSubject.DidSubjectOf(!string.IsNullOrEmpty(unpacked.SenderKid) ? unpacked.SenderKid : unpacked.SignerKid)
            : null;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (sender is not null)
        {
            hash.AppendData("didcomm-dotnet/pickup-dedupe/authenticated\n"u8);
            AppendField(hash, sender);
            AppendField(hash, DidSubject.DidSubjectOf(unpacked.RecipientKid) ?? string.Empty);
            AppendField(hash, unpacked.Message.Id ?? string.Empty);
        }
        else
        {
            hash.AppendData("didcomm-dotnet/pickup-dedupe/anonymous\n"u8);
            AppendField(hash, innermostPacked);
        }

        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return BinaryPrimitives.ReadUInt128BigEndian(digest[..16]);
    }

    /// <summary>
    /// The key the filter remembers: <paramref name="messageKey"/> scoped to the mailbox it was
    /// fetched from, SHA-256 truncated to 128 bits over (the mailbox's DID subject, the message key).
    /// </summary>
    /// <param name="mailboxDid">The DID whose mailbox the fetch or live stream reads: its <c>from</c> (a bare DID).</param>
    /// <param name="messageKey">A key from <see cref="KeyOf"/>.</param>
    internal static UInt128 InMailbox(string mailboxDid, UInt128 messageKey)
    {
        ArgumentNullException.ThrowIfNull(mailboxDid);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("didcomm-dotnet/pickup-dedupe/mailbox\n"u8);
        AppendField(hash, DidSubject.DidSubjectOf(mailboxDid) ?? mailboxDid);
        Span<byte> key = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(key, messageKey);
        hash.AppendData(key);

        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return BinaryPrimitives.ReadUInt128BigEndian(digest[..16]);
    }

    /// <summary>Whether the message <paramref name="messageKey"/> was already returned from the mailbox of <paramref name="mailboxDid"/>.</summary>
    /// <param name="mailboxDid">The DID whose mailbox the fetch or live stream reads: its <c>from</c>.</param>
    /// <param name="messageKey">A key from <see cref="KeyOf"/>.</param>
    internal bool Contains(string mailboxDid, UInt128 messageKey)
    {
        if (Capacity == 0)
            return false;
        var key = InMailbox(mailboxDid, messageKey);
        lock (_gate)
            return _keys.Contains(key);
    }

    /// <summary>
    /// Remember that the message <paramref name="messageKey"/> was returned from the mailbox of
    /// <paramref name="mailboxDid"/>, forgetting the oldest key when full; a key already remembered
    /// is not added twice.
    /// </summary>
    /// <param name="mailboxDid">The DID whose mailbox the fetch or live stream reads: its <c>from</c>.</param>
    /// <param name="messageKey">A key from <see cref="KeyOf"/>.</param>
    internal void Add(string mailboxDid, UInt128 messageKey)
    {
        if (Capacity == 0)
            return;
        var key = InMailbox(mailboxDid, messageKey);
        lock (_gate)
        {
            if (!_keys.Add(key))
                return;
            _order.Enqueue(key);
            while (_order.Count > Capacity)
                _keys.Remove(_order.Dequeue());
        }
    }

    // Length-prefixed UTF-8 fields, so ("ab", "c") and ("a", "bc") never hash alike.
    private static void AppendField(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
