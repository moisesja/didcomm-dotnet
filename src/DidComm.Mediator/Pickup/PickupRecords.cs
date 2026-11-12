namespace DidComm.Mediator.Pickup;

/// <summary>
/// The body of a Message Pickup <c>status</c> message: what a mediator holds for a recipient
/// (FR-PICK-02). Only <paramref name="MessageCount"/> is required by the protocol; a member left
/// <c>null</c> is not written.
/// </summary>
/// <param name="MessageCount">The number of messages waiting (<c>message_count</c>). Never negative.</param>
/// <param name="RecipientDid">The DID the status is about (<c>recipient_did</c>): present exactly when the <c>status-request</c> named one. At most 2048 characters (FR-API-09).</param>
/// <param name="LongestWaitedSeconds">How long the oldest waiting message has been held, in seconds (<c>longest_waited_seconds</c>).</param>
/// <param name="NewestReceivedTime">When the newest waiting message arrived, in UTC epoch seconds (<c>newest_received_time</c>).</param>
/// <param name="OldestReceivedTime">When the oldest waiting message arrived, in UTC epoch seconds (<c>oldest_received_time</c>).</param>
/// <param name="TotalBytes">The total size of the waiting messages in bytes (<c>total_bytes</c>).</param>
/// <param name="LiveDelivery">Whether live delivery is on for the connection the status travels on (<c>live_delivery</c>).</param>
public sealed record PickupStatus(
    int MessageCount,
    string? RecipientDid = null,
    long? LongestWaitedSeconds = null,
    long? NewestReceivedTime = null,
    long? OldestReceivedTime = null,
    long? TotalBytes = null,
    bool? LiveDelivery = null)
{
    /// <summary>The numbers and the flag only: the recipient DID is not printed, so a logged instance carries no wire identifier.</summary>
    public override string ToString()
        => $"PickupStatus {{ MessageCount = {MessageCount}, LongestWaitedSeconds = {LongestWaitedSeconds}, NewestReceivedTime = {NewestReceivedTime}, OldestReceivedTime = {OldestReceivedTime}, TotalBytes = {TotalBytes}, LiveDelivery = {LiveDelivery} }}";
}

/// <summary>
/// One message carried by a <c>delivery</c>: an attachment <c>{"id": …, "data": {"base64": …}}</c>
/// (FR-PICK-03/04).
/// </summary>
/// <param name="Id">The mediator's id for the message (the attachment <c>id</c>). Opaque: the recipient sends it back in <c>messages-received</c> and reads nothing else into it. Unreserved URI characters, at most 2048.</param>
/// <param name="Payload">The message itself: an encrypted DIDComm message exactly as the mediator received it. Not inspected here.</param>
public sealed record DeliveredMessage(string Id, ReadOnlyMemory<byte> Payload)
{
    /// <summary>The payload length only: neither the id nor the payload is printed, so a logged instance carries no wire value.</summary>
    public override string ToString() => $"DeliveredMessage {{ Payload = {Payload.Length} bytes }}";
}
