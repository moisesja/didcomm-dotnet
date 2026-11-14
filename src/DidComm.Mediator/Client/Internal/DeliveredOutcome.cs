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
    private DeliveredOutcome(UnpackResult? unpacked, int layersOpened, UInt128 dedupeKey, Exception? error)
    {
        Unpacked = unpacked;
        LayersOpened = layersOpened;
        DedupeKey = dedupeKey;
        Error = error;
    }

    /// <summary>The innermost unpack result (every re-wrap layer opened); <c>null</c> on failure.</summary>
    internal UnpackResult? Unpacked { get; }

    /// <summary>How many self-addressed forward layers were opened (0 for a pass-through delivery, and on failure).</summary>
    internal int LayersOpened { get; }

    /// <summary>The <see cref="DuplicateFilter"/> key of the message (<see cref="DuplicateFilter.KeyOf"/>), before the filter scopes it to the mailbox it was fetched from; meaningful only on success.</summary>
    internal UInt128 DedupeKey { get; }

    /// <summary>Why the message did not unpack (the unpack exception, or a <see cref="TimeoutException"/> for the deadline and the abandoned-unpack bound); <c>null</c> on success.</summary>
    internal Exception? Error { get; }

    /// <summary>Whether the message unpacked.</summary>
    internal bool Succeeded => Unpacked is not null;

    /// <summary>A message that unpacked.</summary>
    /// <param name="unpacked">The innermost unpack result.</param>
    /// <param name="layersOpened">How many re-wrap layers were opened.</param>
    /// <param name="dedupeKey">Its duplicate-filter key.</param>
    internal static DeliveredOutcome Success(UnpackResult unpacked, int layersOpened, UInt128 dedupeKey)
    {
        ArgumentNullException.ThrowIfNull(unpacked);
        return new DeliveredOutcome(unpacked, layersOpened, dedupeKey, error: null);
    }

    /// <summary>A message that did not unpack.</summary>
    /// <param name="error">Why.</param>
    internal static DeliveredOutcome Failure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new DeliveredOutcome(unpacked: null, layersOpened: 0, dedupeKey: default, error);
    }
}
