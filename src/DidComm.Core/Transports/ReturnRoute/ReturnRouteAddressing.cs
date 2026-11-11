using DidComm.Consistency;
using DidComm.Messages;

namespace DidComm.Transports.ReturnRoute;

/// <summary>Why a message may not be returned on a session (FR-RR-03 addressing rule).</summary>
internal enum ReturnRouteSendRefusal
{
    /// <summary>The message may be sent.</summary>
    None,

    /// <summary>The message has no <c>from</c>.</summary>
    NoFrom,

    /// <summary><c>from</c> is not the session's local DID (compared as DID subjects).</summary>
    FromNotLocalDid,

    /// <summary><c>to</c> is not exactly one entry naming the session's peer (compared as DID subjects).</summary>
    ToNotExactlyPeer,
}

/// <summary>
/// The addressing half of the return-route rule (FR-RR-03): a message returned on a session must be
/// from the session's local DID to exactly its peer, both compared as DID subjects (a DID URL of the
/// same subject is accepted). Reasons are enum values so no log line ever carries a DID.
/// </summary>
internal static class ReturnRouteAddressing
{
    /// <summary>Check <paramref name="message"/> against the session identities.</summary>
    /// <param name="message">The message a handler wants to return.</param>
    /// <param name="peerDid">The session's peer DID subject.</param>
    /// <param name="localDid">The session's local DID subject.</param>
    public static ReturnRouteSendRefusal Check(Message message, string peerDid, string localDid)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrEmpty(message.From))
            return ReturnRouteSendRefusal.NoFrom;
        if (!DidSubject.SameDidSubject(message.From, localDid))
            return ReturnRouteSendRefusal.FromNotLocalDid;
        if (message.To is not { Count: 1 } || !DidSubject.SameDidSubject(message.To[0], peerDid))
            return ReturnRouteSendRefusal.ToNotExactlyPeer;
        return ReturnRouteSendRefusal.None;
    }
}
