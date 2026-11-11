using DidComm.Consistency;
using DidComm.Facade;

namespace DidComm.Transports.ReturnRoute;

/// <summary>Why an inbound cannot open a return-route session (FR-RR-03).</summary>
internal enum ReturnRouteIneligibility
{
    /// <summary>Eligible.</summary>
    None,

    /// <summary>The envelope was not both encrypted and sender-authenticated (plaintext, signed-only or anoncrypt).</summary>
    NotEncryptedAndAuthenticated,

    /// <summary>The result claims authentication but names no sender key id.</summary>
    NoSenderKid,

    /// <summary>The sender key id is not a DID or DID URL.</summary>
    SenderKidNotDid,

    /// <summary>A signer key id is present but is not a DID or DID URL.</summary>
    SignerKidNotDid,

    /// <summary>The signer key id names a different DID subject than the sender key id.</summary>
    SignerMismatch,

    /// <summary>The decrypting key id is not a DID or DID URL.</summary>
    RecipientKidNotDid,

    /// <summary>The message is a Routing 2.0 message (<c>forward</c>): never a session, FR-RR-02.</summary>
    Forward,
}

/// <summary>
/// The identity half of return-route eligibility (FR-RR-03): from a verified
/// <see cref="UnpackResult"/>, the peer DID subject (of the authcrypt sender key id) and the local
/// DID subject (of the decrypting key id). Pure; the transport decides what to do with the answer.
/// </summary>
/// <remarks>
/// The peer is fixed to the sender key id of an authcrypt layer. <see cref="UnpackResult.Authenticated"/>
/// alone does not prove one — a verified signature also sets it — so a non-empty
/// <see cref="UnpackResult.SenderKid"/> is required and a signer-only binding (signed-only or
/// <c>anoncrypt(sign(…))</c>) is not accepted. When a signer key id is also present it must name
/// the same subject (FR-CONSIST-05, defense in depth).
/// </remarks>
internal static class ReturnRouteEligibility
{
    /// <summary>
    /// Derive the session identities from <paramref name="inbound"/>. Returns <c>true</c> with both
    /// DID subjects, or <c>false</c> with the first failing rule in <paramref name="reason"/>.
    /// </summary>
    /// <param name="inbound">The unpack result of the inbound message.</param>
    /// <param name="peerDid">The DID subject of the authenticated sender.</param>
    /// <param name="localDid">The DID subject of the decrypting key.</param>
    /// <param name="reason">Why the inbound is ineligible; <see cref="ReturnRouteIneligibility.None"/> on success.</param>
    public static bool TryGetPeer(UnpackResult inbound, out string peerDid, out string localDid, out ReturnRouteIneligibility reason)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        peerDid = string.Empty;
        localDid = string.Empty;

        if (!inbound.Encrypted || !inbound.Authenticated)
        {
            reason = ReturnRouteIneligibility.NotEncryptedAndAuthenticated;
            return false;
        }
        if (string.IsNullOrEmpty(inbound.SenderKid))
        {
            reason = ReturnRouteIneligibility.NoSenderKid;
            return false;
        }
        var sender = DidSubject.DidSubjectOf(inbound.SenderKid);
        if (sender is null)
        {
            reason = ReturnRouteIneligibility.SenderKidNotDid;
            return false;
        }
        if (!string.IsNullOrEmpty(inbound.SignerKid))
        {
            var signer = DidSubject.DidSubjectOf(inbound.SignerKid);
            if (signer is null)
            {
                reason = ReturnRouteIneligibility.SignerKidNotDid;
                return false;
            }
            if (!string.Equals(signer, sender, StringComparison.Ordinal))
            {
                reason = ReturnRouteIneligibility.SignerMismatch;
                return false;
            }
        }
        var local = DidSubject.DidSubjectOf(inbound.RecipientKid);
        if (local is null)
        {
            reason = ReturnRouteIneligibility.RecipientKidNotDid;
            return false;
        }
        if (MediatorServiceProtocols.IsReplayExempt(inbound.Message?.Type))
        {
            reason = ReturnRouteIneligibility.Forward;
            return false;
        }

        peerDid = sender;
        localDid = local;
        reason = ReturnRouteIneligibility.None;
        return true;
    }
}
