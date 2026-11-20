namespace DidComm.Mediator.Client;

/// <summary>
/// Which version of the Message Pickup protocol <see cref="PickupClient"/> speaks to a mediator
/// (FR-PICK-11, FR-PICK-12). Set it with <see cref="MediatorClientOptions.MessagePickupVersion"/>.
/// </summary>
public enum MessagePickupVersion
{
    /// <summary>
    /// Ask the mediator first, with Discover Features, and use 4.0 when it discloses it, 3.0
    /// otherwise (a disclosure whose roles name <c>recipient</c> without <c>mediator</c> does not
    /// count). The answer is remembered per mediator for an hour. A mediator that discloses neither
    /// version fails the call with <see cref="MediationNotSupportedException"/>; one that does not
    /// answer the query at all fails it with <see cref="TimeoutException"/> — pin a version for such
    /// a mediator.
    /// </summary>
    Negotiate = 0,

    /// <summary>Message Pickup 4.0 (<c>https://didcomm.org/message-pickup/4.0</c>), without asking first.</summary>
    V4 = 1,

    /// <summary>Message Pickup 3.0 (<c>https://didcomm.org/messagepickup/3.0</c>), without asking first.</summary>
    V3 = 2,
}
