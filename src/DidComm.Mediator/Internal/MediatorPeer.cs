using DidComm.Facade;
using DidComm.Transports.ReturnRoute;

namespace DidComm.Mediator.Internal;

/// <summary>
/// Who the peer of a mediator-service request is (FR-MED-05, FR-PICK-01): the DID subject of the
/// authcrypt sender key, with the DID subject of the key that decrypted the message as the local
/// side. One rule for every mediator-role handler, and the same rule that lets an inbound open a
/// return-route session (FR-RR-03), so "may be answered on the session" and "is a peer" can never
/// disagree (L-041).
/// </summary>
internal static class MediatorPeer
{
    /// <summary>
    /// <c>true</c> with the pair when <paramref name="received"/> carries an authcrypt layer;
    /// <c>false</c> for plaintext, anoncrypt, signed-only and <c>anoncrypt(sign)</c>. The plaintext
    /// <c>from</c> is never consulted.
    /// </summary>
    public static bool TryGet(UnpackResult received, out string peerDid, out string localDid)
        => ReturnRouteEligibility.TryGetPeer(received, out peerDid, out localDid, out _);
}
