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
    private DeliveredOutcome(UnpackResult? unpacked, int layersOpened, UInt128 dedupeKey, Exception? error, bool inconclusive)
    {
        Unpacked = unpacked;
        LayersOpened = layersOpened;
        DedupeKey = dedupeKey;
        Error = error;
        Inconclusive = inconclusive;
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
    /// Whether the message did not open and this delivery was not counted against it
    /// (<see cref="PickupItem.Inconclusive"/>, which the fetch path and the live stream copy from
    /// here). It says what the memory says: the unpacker sets it for a failed message exactly when it
    /// neither struck the message in this delivery nor held it back, and the message is not
    /// malformed. That is: the budget ran out before its turn came (no host call was created, the
    /// start rule turned it away, or its call's code had not started); it was refused for capacity;
    /// its code started later than the start allowance and was still running when the budget ended;
    /// or the host's clock failed where the strike was decided or made. <c>false</c> on success, for
    /// a failure of its own (S1), for a message that had the whole budget (S2), for a held-back one
    /// and for a malformed one.
    /// </summary>
    internal bool Inconclusive { get; }

    /// <summary>Whether the message unpacked.</summary>
    internal bool Succeeded => Unpacked is not null;

    /// <summary>A message that unpacked.</summary>
    /// <param name="unpacked">The innermost unpack result.</param>
    /// <param name="layersOpened">How many re-wrap layers were opened.</param>
    /// <param name="dedupeKey">Its duplicate-filter key.</param>
    internal static DeliveredOutcome Success(UnpackResult unpacked, int layersOpened, UInt128 dedupeKey)
    {
        ArgumentNullException.ThrowIfNull(unpacked);
        return new DeliveredOutcome(unpacked, layersOpened, dedupeKey, error: null, inconclusive: false);
    }

    /// <summary>A message that did not unpack.</summary>
    /// <param name="error">Why.</param>
    /// <param name="inconclusive">Whether this delivery was not counted against the message (<see cref="Inconclusive"/>); <c>false</c> for a message that was struck in this delivery, that is held back, or that is malformed.</param>
    internal static DeliveredOutcome Failure(Exception error, bool inconclusive = false)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new DeliveredOutcome(unpacked: null, layersOpened: 0, dedupeKey: default, error, inconclusive);
    }
}
