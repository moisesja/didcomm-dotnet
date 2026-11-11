using DidComm.Consistency;
using DidComm.Facade;

namespace DidComm.Transports.ReturnRoute;

/// <summary>Why a message returned over a return-route connection is not accepted (FR-RR-07, JC-6).</summary>
internal enum ReturnedMessageRefusal
{
    /// <summary>Accepted.</summary>
    None,

    /// <summary>
    /// Not an authcrypt envelope from a DID-keyed sender to a DID-keyed recipient (plaintext,
    /// signed-only, anoncrypt or <c>anoncrypt(sign(…))</c>), or its signer names another DID
    /// subject than its sender (the FR-RR-03 identity half).
    /// </summary>
    NotAuthcrypt,

    /// <summary>A Routing 2.0 message (<c>forward</c>): never part of a return route (FR-RR-02).</summary>
    Forward,

    /// <summary>The authcrypt sender is not the DID the request addressed.</summary>
    PeerMismatch,

    /// <summary>The message was decrypted by a key of another local DID than the one that sent the request.</summary>
    LocalMismatch,

    /// <summary>Both the sender and the decrypting DID differ.</summary>
    PeerAndLocalMismatch,
}

/// <summary>
/// The client-side identity rule for a message returned over a return-route connection (FR-RR-07,
/// JC-6), shared by <c>DidCommClient.SendAsync</c> (HTTP <c>Returned</c>) and the inbound consumer
/// (WebSocket pushes): the message must be authcrypt whose sender key id names the peer the request
/// addressed and whose decrypting key id names the local DID that sent it. Pure.
/// </summary>
/// <remarks>
/// Both identities come from verified envelope key ids through
/// <see cref="ReturnRouteEligibility.TryGetPeer"/>, never from the plaintext <c>from</c> / <c>to</c>.
/// Both sides of each compare are DID subjects (PRD §4.3, L-034): the expected values are reduced to
/// their subjects here too, so a DID URL of the same subject matches. Both compares always run and
/// are combined without short-circuiting, so the work done does not depend on which one fails.
/// </remarks>
internal static class ReturnRouteIdentity
{
    /// <summary>
    /// Whether <paramref name="inbound"/> is authcrypt from <paramref name="expectedPeerDid"/> to
    /// <paramref name="expectedLocalDid"/>. Returns <c>false</c> with the reason otherwise.
    /// </summary>
    /// <param name="inbound">The unpack result of the returned or pushed message.</param>
    /// <param name="expectedPeerDid">The DID (or its subject) the request addressed.</param>
    /// <param name="expectedLocalDid">The DID (or its subject) that sent the request.</param>
    /// <param name="refusal">Why the message is not accepted; <see cref="ReturnedMessageRefusal.None"/> on success.</param>
    internal static bool IsFromPeerToLocal(UnpackResult inbound, string expectedPeerDid, string expectedLocalDid, out ReturnedMessageRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(inbound);

        if (!ReturnRouteEligibility.TryGetPeer(inbound, out var peer, out var local, out var why))
        {
            refusal = why == ReturnRouteIneligibility.Forward ? ReturnedMessageRefusal.Forward : ReturnedMessageRefusal.NotAuthcrypt;
            return false;
        }

        // Single pass, no early exit: both subjects are derived and both compares run, combined
        // with a non-short-circuiting '&'. A null expected subject never equals a parsed one.
        var expectedPeer = DidSubject.DidSubjectOf(expectedPeerDid);
        var expectedLocal = DidSubject.DidSubjectOf(expectedLocalDid);
        var peerOk = string.Equals(peer, expectedPeer, StringComparison.Ordinal);
        var localOk = string.Equals(local, expectedLocal, StringComparison.Ordinal);
        var ok = peerOk & localOk;

        refusal = ok ? ReturnedMessageRefusal.None
            : !peerOk & !localOk ? ReturnedMessageRefusal.PeerAndLocalMismatch
            : !peerOk ? ReturnedMessageRefusal.PeerMismatch
            : ReturnedMessageRefusal.LocalMismatch;
        return ok;
    }
}
