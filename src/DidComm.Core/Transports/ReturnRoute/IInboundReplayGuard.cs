namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// The FR-RR-08 inbound replay guard: an atomic check-and-insert keyed on the authenticated
/// sender's DID subject and the message id. A transport calls it once per in-scope inbound,
/// after unpack and before dispatch; a handler must never call it again (a second admission of
/// the same message refuses itself).
/// </summary>
/// <remarks>
/// <para>
/// Scope is the transport's decision (the AspNetCore endpoints: an authcrypted message — one with
/// an authcrypt layer, not merely a verified signature — that carries <c>return_route</c> or whose
/// type is a mediator-service protocol per <see cref="MediatorServiceProtocols"/>, except Routing
/// 2.0 <c>forward</c>). The guard only answers the question "first sight within the retention?".
/// </para>
/// <para>
/// Dependency rule: an implementation must not take <c>ProtocolHandlerRegistry</c>,
/// <c>ProtocolDispatcher</c>, <c>DidCommClient</c> or an <c>IProtocolHandler</c> in its constructor —
/// handlers may depend on the guard, and the registry factory walks every handler, so such a
/// dependency would cycle the DI graph. With <c>AddDidComm</c>'s registrations, a cycle through the
/// handler registry, the dispatcher or a handler fails the first resolution with an
/// <see cref="InvalidOperationException"/>; it is still a bug to fix. The in-memory default depends
/// on options only.
/// </para>
/// </remarks>
public interface IInboundReplayGuard
{
    /// <summary>
    /// Admit <paramref name="messageId"/> from <paramref name="peerDidSubject"/> exactly once within
    /// the retention. The caller passes one clock reading per inbound (<c>DidCommOptions.Now()</c>,
    /// read once for the admission; the FR-API-05 expiry check at unpack read the clock earlier), so
    /// every judgement the guard makes about the message uses that one instant. Readings of
    /// concurrent inbounds can reach an implementation out of order, and a host clock can step back:
    /// an implementation must still never admit a message it may have forgotten
    /// (<see cref="InMemoryReplayGuard"/> refuses anything whose entry would already have been
    /// purged).
    /// </summary>
    /// <param name="peerDidSubject">The bare DID subject of the authenticated sender (the sender key id's DID subject).</param>
    /// <param name="messageId">The message <c>id</c>.</param>
    /// <param name="createdTimeEpochSeconds">The message <c>created_time</c>, or <c>null</c> when absent.</param>
    /// <param name="now">The current time as the transport observed it for this inbound.</param>
    ReplayAdmission TryAdmit(string peerDidSubject, string messageId, long? createdTimeEpochSeconds, DateTimeOffset now);
}
