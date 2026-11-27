namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// Whose delivery is being opened: the mediator that delivered it and the DID whose mailbox it came
/// from (the <c>from</c> of the fetch or live stream). Both come from the host's own call, never from
/// the wire (#111). The slow-message memory and the unpack reservations are kept per scope.
/// </summary>
/// <param name="MediatorDid">The mediator's DID (bare), as the host passed it.</param>
/// <param name="LocalDid">The mailbox's DID (bare): the <c>from</c> of the fetch or live stream.</param>
internal readonly record struct DeliveryScope(string MediatorDid, string LocalDid);

/// <summary>One delivered message to open: the mediator's attachment id and its decoded bytes.</summary>
/// <param name="Id">The attachment id exactly as delivered (opaque, compared exactly), or <c>null</c> when there is none to remember it by.</param>
/// <param name="Payload">The delivered bytes.</param>
internal readonly record struct DeliveredInput(string? Id, ReadOnlyMemory<byte> Payload);
