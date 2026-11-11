using DidComm.Consistency;
using DidComm.Facade;

namespace DidComm.Transports.ReturnRoute;

/// <summary>What <see cref="InboundReplayGate.Run"/> decided for one inbound (FR-RR-08).</summary>
internal enum ReplayGateOutcome
{
    /// <summary>Not in the FR-RR-08 scope: proceed without admission (the guard was not called).</summary>
    OutOfScope,

    /// <summary>In scope and admitted by the guard: proceed.</summary>
    Admitted,

    /// <summary>In scope, but the sender key id has no DID subject or the message id is empty: refused before the guard.</summary>
    NoSenderSubject,

    /// <summary>In scope, and the guard threw: refused (fail closed).</summary>
    GuardFault,

    /// <summary>In scope, and the guard answered anything but <see cref="ReplayAdmission.Admitted"/>: refused.</summary>
    Refused,
}

/// <summary>The outcome of <see cref="InboundReplayGate.Run"/>.</summary>
/// <param name="Outcome">The decision.</param>
/// <param name="Replay">The guard's answer when the guard was called and returned; <c>null</c> otherwise.</param>
/// <param name="GuardFaultType">The exception type name when the guard threw (<see cref="ReplayGateOutcome.GuardFault"/>); <c>null</c> otherwise.</param>
internal readonly record struct ReplayGateResult(ReplayGateOutcome Outcome, ReplayAdmission? Replay, string? GuardFaultType)
{
    /// <summary>Whether the message may proceed to dispatch.</summary>
    internal bool Proceeds => Outcome is ReplayGateOutcome.OutOfScope or ReplayGateOutcome.Admitted;
}

/// <summary>
/// The FR-RR-08 replay-admission rule, shared by every receive path that applies it: the
/// AspNetCore registry-aware endpoints and the client's inbound consumer (WebSocket pushes). Scope:
/// an authcrypted message (an authcrypt layer with a sender key id, not merely a verified
/// signature) that carries <c>return_route</c> other than <c>none</c> or whose type belongs to a
/// mediator-service protocol; Routing 2.0 <c>forward</c> is exempt. In scope, the message is admitted
/// at most once per (sender DID subject, message id) through the <see cref="IInboundReplayGuard"/>.
/// </summary>
/// <remarks>
/// A signed-only or <c>anoncrypt(sign(…))</c> message, like plaintext and anoncrypt, is out of scope:
/// it can neither open a session nor mutate mediator state, so it passes through and its handler
/// decides. The admission key is the authcrypt sender key id's DID subject, never the plaintext
/// <c>from</c> (L-034). The clock is read only for an in-scope message.
/// </remarks>
internal static class InboundReplayGate
{
    /// <summary>Whether <paramref name="unpacked"/>, with the parsed <paramref name="header"/>, is in the FR-RR-08 scope.</summary>
    /// <param name="unpacked">The verified unpack result.</param>
    /// <param name="header">Its parsed <c>return_route</c> header (<see cref="ReturnRouteHeader.None"/> when absent or malformed).</param>
    internal static bool IsInScope(UnpackResult unpacked, ReturnRouteHeader header)
    {
        ArgumentNullException.ThrowIfNull(unpacked);
        var type = unpacked.Message.Type;
        var authcrypted = unpacked.Authenticated && !string.IsNullOrEmpty(unpacked.SenderKid);
        return authcrypted
            && !MediatorServiceProtocols.IsReplayExempt(type)
            && (header.Mode != ReturnRouteMode.None || MediatorServiceProtocols.IsMediatorServiceType(type));
    }

    /// <summary>
    /// Apply FR-RR-08 to <paramref name="unpacked"/>. Never throws for a guard fault: a throwing
    /// guard (host code when the host registered its own) fails closed as
    /// <see cref="ReplayGateOutcome.GuardFault"/>.
    /// </summary>
    /// <param name="unpacked">The verified unpack result.</param>
    /// <param name="header">Its parsed <c>return_route</c> header.</param>
    /// <param name="guard">The FR-RR-08 guard.</param>
    /// <param name="options">The facade options; the clock (<c>DidCommOptions.Now()</c>) is read once, only in scope.</param>
    internal static ReplayGateResult Run(UnpackResult unpacked, ReturnRouteHeader header, IInboundReplayGuard guard, DidCommOptions options)
    {
        ArgumentNullException.ThrowIfNull(unpacked);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(options);

        if (!IsInScope(unpacked, header))
            return new ReplayGateResult(ReplayGateOutcome.OutOfScope, null, null);

        // Keyed on the authcrypt sender key id's subject — never on the plaintext `from`. A sender key
        // id that is not a DID (or an empty id) fails closed (L-034).
        var message = unpacked.Message;
        var peerSubject = DidSubject.DidSubjectOf(unpacked.SenderKid);
        if (peerSubject is null || string.IsNullOrEmpty(message.Id))
            return new ReplayGateResult(ReplayGateOutcome.NoSenderSubject, null, null);

        ReplayAdmission replay;
        try
        {
            replay = guard.TryAdmit(peerSubject, message.Id, message.CreatedTime, options.Now());
        }
        catch (Exception ex)
        {
            return new ReplayGateResult(ReplayGateOutcome.GuardFault, null, ex.GetType().Name);
        }

        return replay == ReplayAdmission.Admitted
            ? new ReplayGateResult(ReplayGateOutcome.Admitted, replay, null)
            : new ReplayGateResult(ReplayGateOutcome.Refused, replay, null);
    }
}
