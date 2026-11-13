using DidComm.Messages;

namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// A return-route session (PRD §8a, FR-RR-03): the binding between an authenticated peer DID and
/// the connection its inbound arrived on, through which this agent may return messages to that
/// peer. Opened by a transport only for an inbound that was encrypted with an authcrypt layer,
/// whose sender key id resolved to a DID subject, that carried <c>return_route: all</c>, is not a
/// <c>forward</c> and passed FR-RR-08 replay admission. Surfaced to handlers as
/// <c>ProtocolContext.Session</c> and, for live WebSocket sessions, through
/// <see cref="IReturnRouteSessionRegistry.Find"/>.
/// </summary>
/// <remarks>
/// A session exposes identity facts and one operation. It never exposes the socket, the raw
/// bytes or the inbound. The trust boundary is the authcrypt layer, not the connection:
/// <see cref="PeerDid"/> comes from the verified sender key id, never from the plaintext
/// <c>from</c> and never from the connection, and everything sent through the session is
/// authcrypted from <see cref="LocalDid"/> to <see cref="PeerDid"/> — a hijacked socket receives
/// ciphertext it cannot open.
/// </remarks>
public interface IReturnRouteSession
{
    /// <summary>The DID subject of the authenticated sender (the DID subject of the inbound's sender key id). Never a DID URL.</summary>
    string PeerDid { get; }

    /// <summary>The DID subject of the key that decrypted the inbound — this agent's own identity for the session, by construction.</summary>
    string LocalDid { get; }

    /// <summary>
    /// The current mode: <see cref="ReturnRouteMode.All"/> while pushes are allowed, or
    /// <see cref="ReturnRouteMode.None"/> once the peer downgraded the session (or it closed). Never
    /// <see cref="ReturnRouteMode.Thread"/>: a <c>thread</c> request opens no session (DD-12).
    /// </summary>
    ReturnRouteMode Mode { get; }

    /// <summary>The last <c>return_route_thread</c> value seen on this session, surfaced for handlers only; it does not scope delivery (DD-12).</summary>
    string? Thread { get; }

    /// <summary>The transport this session delivers on.</summary>
    ReturnRouteTransport Transport { get; }

    /// <summary><c>true</c> for a WebSocket session (pushes continue while the socket stays open); <c>false</c> for HTTP (one response at most).</summary>
    bool IsLive { get; }

    /// <summary>
    /// Cancelled when the session ends: the HTTP response was written, the socket closed, or the
    /// registry evicted it. The authoritative per-session end signal: it fires without waiting for
    /// the registry's lifecycle notifications, which are best-effort
    /// (<see cref="IReturnRouteSessionRegistry"/>), so per-session state such as message-pickup live
    /// mode should key on it.
    /// </summary>
    CancellationToken Closed { get; }

    /// <summary>
    /// Try to return <paramref name="message"/> to the peer on this session. Returns <c>true</c>
    /// when the message was accepted for transmission — on HTTP it is held as the 200 body of the
    /// request that opened the session; on a WebSocket it was queued to the socket writer.
    /// Delivery is not acknowledged at this layer: <c>true</c> means queued, not delivered, and
    /// queued pushes are discarded when the session closes without sending them — on HTTP that is
    /// a request that ends in the uniform 400 instead (the handler threw after the push) or is
    /// aborted; on a WebSocket, a socket that closes or is evicted. Returns <c>false</c>, and never throws
    /// (except <see cref="ArgumentNullException"/> and <see cref="OperationCanceledException"/> on
    /// <paramref name="ct"/>), when <see cref="Mode"/> is not <see cref="ReturnRouteMode.All"/>;
    /// the session is closed; an HTTP session already holds a message; a WebSocket session has no
    /// free push slot (<see cref="ReturnRouteSessionOptions.MaxPendingPushes"/> pushes are already
    /// being packed or queued — checked before the snapshot, so such a call costs no snapshot, key
    /// resolution or encryption); the message is not addressed to exactly
    /// <c>[</c><see cref="PeerDid"/><c>]</c> from <see cref="LocalDid"/> (compared as DID subjects);
    /// or the snapshot or the pack failed (logged by exception type name). The message is
    /// snapshotted before validation and packing, so a
    /// caller that keeps mutating the instance cannot race the check; it is authcrypted from
    /// <see cref="LocalDid"/> to <see cref="PeerDid"/> with no forward wrapping. The packed size is
    /// not limited here: the call is <see cref="TrySendAsync(Message, int, CancellationToken)"/>
    /// with <see cref="int.MaxValue"/> as the bound, answering <c>true</c> for
    /// <see cref="ReturnRouteSendResult.Sent"/>.
    /// </summary>
    /// <param name="message">The plaintext message to return; must be addressed from <see cref="LocalDid"/> to exactly <see cref="PeerDid"/>.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    ValueTask<bool> TrySendAsync(Message message, CancellationToken ct);

    /// <summary>
    /// Try to return <paramref name="message"/> to the peer on this session, unless its packed
    /// envelope is larger than <paramref name="maxPackedBytes"/>. The contract is that of
    /// <see cref="TrySendAsync(Message, CancellationToken)"/> — the same checks in the same order,
    /// the same snapshot, the same authcrypt from <see cref="LocalDid"/> to <see cref="PeerDid"/>,
    /// the same "queued, not delivered" — with <see cref="ReturnRouteSendResult.Sent"/> for its
    /// <c>true</c>, <see cref="ReturnRouteSendResult.NotSent"/> for its <c>false</c>, and one more
    /// answer: <see cref="ReturnRouteSendResult.TooLarge"/> when the packed envelope is larger than
    /// the bound. The size is measured after packing, on the bytes handed to the transport, so it
    /// is exact, not an estimate. A message that is too large is neither sent nor queued, and the
    /// call leaves the session as it found it — the HTTP slot it claimed, or the WebSocket push
    /// slot it took, is given back — so a smaller message can be sent right after.
    /// </summary>
    /// <remarks>
    /// Used by message-pickup <c>delivery</c> batches and live pushes (FR-PICK-03), whose packed
    /// size must stay within a byte budget: on <see cref="ReturnRouteSendResult.TooLarge"/> they
    /// trim the batch and retry. The session measures because the session packs — a caller holds
    /// only the plaintext. <see cref="ReturnRouteSendResult.TooLarge"/> costs a full pack (snapshot,
    /// key resolution, encryption); <see cref="ReturnRouteSendResult.NotSent"/> is mostly decided
    /// before the pack and says nothing about the size.
    /// </remarks>
    /// <param name="message">The plaintext message to return; must be addressed from <see cref="LocalDid"/> to exactly <see cref="PeerDid"/>.</param>
    /// <param name="maxPackedBytes">
    /// The largest packed envelope the caller accepts, in bytes as handed to the transport: the
    /// UTF-8 bytes of the encrypted envelope (the HTTP <c>200</c> body, the WebSocket message). At
    /// least 1; <see cref="int.MaxValue"/> is no bound.
    /// </param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>What happened to the message; only <see cref="ReturnRouteSendResult.Sent"/> means the session carries it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxPackedBytes"/> is less than 1; checked before the session is read or changed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled. Nothing else is thrown: every other failure is <see cref="ReturnRouteSendResult.NotSent"/>.</exception>
    ValueTask<ReturnRouteSendResult> TrySendAsync(Message message, int maxPackedBytes, CancellationToken ct);
}
