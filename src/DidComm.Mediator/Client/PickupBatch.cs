using DidComm.Mediator.Pickup;

namespace DidComm.Mediator.Client;

/// <summary>
/// What one <see cref="PickupClient.FetchAsync"/> returned: the delivered messages, in the order the
/// mediator sent them, and whether the mediator had nothing (more) to deliver (FR-PICK-11).
/// </summary>
/// <remarks>
/// The items that unpacked were acknowledged before the call returned, so the mediator no longer
/// holds them. The failed ones were not: they come back after the mediator's lease. A message the
/// clients already returned once is acknowledged again and left out.
/// </remarks>
public sealed class PickupBatch
{
    /// <summary>Initialize a batch.</summary>
    /// <param name="items">The items, in delivery order.</param>
    /// <param name="drained">Whether the mediator had nothing deliverable for the request.</param>
    /// <param name="status">The mediator's <c>status</c> answer, or <c>null</c> when it answered with a delivery.</param>
    internal PickupBatch(IReadOnlyList<PickupItem> items, bool drained, PickupStatus? status)
    {
        Items = items;
        Drained = drained;
        Status = status;
    }

    /// <summary>
    /// The delivered messages, in the order the mediator sent them: the ones that unpacked
    /// (acknowledged) and the ones that did not (left with the mediator). Repeats of messages
    /// returned earlier are left out.
    /// </summary>
    public IReadOnlyList<PickupItem> Items { get; }

    /// <summary>
    /// <c>true</c> when the mediator had nothing deliverable for this request: it answered with a
    /// <c>status</c>, or with a delivery that carried no message. Call
    /// <see cref="PickupClient.FetchAsync"/> until it is <c>true</c> to take everything queued.
    /// </summary>
    public bool Drained { get; }

    /// <summary>The mediator's <c>status</c>, when it answered the request with one; <c>null</c> when it answered with a delivery.</summary>
    public PickupStatus? Status { get; }
}
