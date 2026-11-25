using DidComm.Facade;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// The result of unpacking one delivered message (design §4.6, §4.9): the innermost unpack result
/// and its duplicate-filter key, or the failure. Unpacking never throws for a bad message — only
/// for the caller's cancellation — so every attachment becomes exactly one outcome. Owner: engineer A
/// (<see cref="DeliveredMessageUnpacker"/> produces it); B's and C's fakes build it directly.
/// </summary>
internal sealed class DeliveredOutcome
{
    private DeliveredOutcome(UnpackResult? unpacked, int layersOpened, UInt128 dedupeKey, Exception? error, bool notAttempted)
    {
        Unpacked = unpacked;
        LayersOpened = layersOpened;
        DedupeKey = dedupeKey;
        Error = error;
        NotAttempted = notAttempted;
    }

    /// <summary>The innermost unpack result (every re-wrap layer opened); <c>null</c> on failure.</summary>
    internal UnpackResult? Unpacked { get; }

    /// <summary>How many self-addressed forward layers were opened (0 for a pass-through delivery, and on failure).</summary>
    internal int LayersOpened { get; }

    /// <summary>The <see cref="DuplicateFilter"/> key of the message (<see cref="DuplicateFilter.KeyOf"/>), before the filter scopes it to the mailbox it was fetched from; meaningful only on success.</summary>
    internal UInt128 DedupeKey { get; }

    /// <summary>Why the message did not unpack (the unpack exception, or a <see cref="TimeoutException"/> for the delivery's budget, a refused reservation and a message held back as slow); <c>null</c> on success.</summary>
    internal Exception? Error { get; }

    /// <summary>
    /// Whether no attempt was made to open the message in this delivery, so that nothing was learned
    /// about it (<see cref="PickupItem.NotAttempted"/>, which the fetch path and the live stream copy
    /// from here). The unpacker sets it in two cases only: the delivery's budget ran out before the
    /// message's turn came (its host call was never created), and the message was refused for
    /// capacity. <c>false</c> on success and for every other failure.
    /// </summary>
    internal bool NotAttempted { get; }

    /// <summary>Whether the message unpacked.</summary>
    internal bool Succeeded => Unpacked is not null;

    /// <summary>A message that unpacked.</summary>
    /// <param name="unpacked">The innermost unpack result.</param>
    /// <param name="layersOpened">How many re-wrap layers were opened.</param>
    /// <param name="dedupeKey">Its duplicate-filter key.</param>
    internal static DeliveredOutcome Success(UnpackResult unpacked, int layersOpened, UInt128 dedupeKey)
    {
        ArgumentNullException.ThrowIfNull(unpacked);
        return new DeliveredOutcome(unpacked, layersOpened, dedupeKey, error: null, notAttempted: false);
    }

    /// <summary>A message that did not unpack.</summary>
    /// <param name="error">Why.</param>
    /// <param name="notAttempted">Whether no attempt was made to open it in this delivery (<see cref="NotAttempted"/>); <c>false</c> for a message that was tried, or that failed when it was tried before.</param>
    internal static DeliveredOutcome Failure(Exception error, bool notAttempted = false)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new DeliveredOutcome(unpacked: null, layersOpened: 0, dedupeKey: default, error, notAttempted);
    }
}
