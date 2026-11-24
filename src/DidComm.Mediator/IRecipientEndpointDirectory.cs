namespace DidComm.Mediator;

/// <summary>
/// Where the host has registered each recipient's own endpoint, for the mediator's optional direct
/// push (<see cref="MediatorOptions.DirectPush"/>, step 3 of the delivery order, FR-PICK-09). The
/// mediator asks it for a recipient's endpoint just before it pushes a stored message there, and
/// never sends to an address a message names.
/// </summary>
/// <remarks>
/// <para>
/// The host registers one implementation before <c>AddMediator()</c>; it is needed only while
/// direct push is on, and nothing resolves it otherwise. It is filled only by the operator: the
/// DIDComm specification leaves the coordination of a recipient's endpoint with its mediator out of
/// its scope (routing.md, "Mediator Process"). An implementation is a singleton and must be
/// thread-safe. Like the stores, it must not take the protocol dispatcher, a protocol handler,
/// <c>DidCommClient</c>, the pickup queue, or the mediator's forward sink or push workers in its
/// constructor: the push workers are built from it, and those lead back to them (resolve what it
/// needs from <see cref="IServiceProvider"/> when it is called instead).
/// </para>
/// <para>
/// It is asked once per push, before any message is taken, so a change applies from the next push.
/// A <c>null</c> answer, an endpoint that may not be pushed to, or an exception all mean "no
/// endpoint": nothing is taken and the recipient's messages wait for pickup. The call counts against
/// the push's deadline (<see cref="MediatorOptions.PushTimeout"/>); an implementation that does slow
/// work should honour the token.
/// </para>
/// <para>
/// It may be asked about a DID that holds no grant, or no longer does: a push woken just before the
/// grant was revoked, or by a peer that turned live delivery on before (or without) enrolling, asks
/// before anything is taken. It then answers <c>null</c>, or anything at all: nothing is ever sent
/// for a DID with no grant — the push finds nothing to take, and a message still held under a
/// revoked grant is removed as unregistered, never pushed.
/// </para>
/// </remarks>
public interface IRecipientEndpointDirectory
{
    /// <summary>
    /// The endpoint registered for the recipient that enrolled under <paramref name="grantPeerDid"/>,
    /// or <c>null</c> when it has none — also when that DID holds no grant (it may never have
    /// enrolled, or no longer does; see the remarks). Only an absolute <c>http</c> or <c>https</c> URI
    /// that the HTTP transport accepts is pushed to; anything else counts as no endpoint. An endpoint
    /// the outbound address check refuses is not pushed to either: that counts as a failed push and
    /// pauses the recipient's direct pushes.
    /// </summary>
    /// <param name="grantPeerDid">The DID subject of a recipient's mediation grant (<c>MediationGrant.PeerDid</c>), usually one that holds a grant now: a bare DID of at most 2048 characters.</param>
    /// <param name="ct">Cancelled when the push's deadline passes or the mediator stops.</param>
    /// <returns>The recipient's endpoint, or <c>null</c> for none.</returns>
    ValueTask<Uri?> FindAsync(string grantPeerDid, CancellationToken ct);
}
