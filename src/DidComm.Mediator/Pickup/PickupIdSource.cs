namespace DidComm.Mediator.Pickup;

/// <summary>
/// Test seam: when registered in the container (only code with access to this assembly's internals
/// can), <c>AddMessagePickup()</c> builds the queue with these ids instead of
/// <see cref="PickupIds.NewRandomId"/>. Whatever it returns is still checked before use: an id that
/// is not unreserved URI characters, or is longer than <see cref="PickupSizing.MaxMintedIdLength"/>,
/// fails the delivery.
/// </summary>
/// <param name="next">Mints the next id.</param>
internal sealed class PickupIdSource(Func<string> next)
{
    /// <summary>Mints the next id.</summary>
    public Func<string> Next { get; } = next ?? throw new ArgumentNullException(nameof(next));
}
