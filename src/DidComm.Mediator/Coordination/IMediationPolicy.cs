using DidComm.Protocols;

namespace DidComm.Mediator.Coordination;

/// <summary>The answer to a <c>mediate-request</c>.</summary>
/// <remarks><see cref="Deny"/> is the zero value, so a decision nobody set denies.</remarks>
public enum MediationDecision
{
    /// <summary>Answer <c>mediate-deny</c>; nothing is stored.</summary>
    Deny = 0,

    /// <summary>Record a grant and answer <c>mediate-grant</c>.</summary>
    Grant = 1,
}

/// <summary>
/// The host's say in who gets mediation and which DIDs they may register (FR-MED-05, D5). Register
/// an implementation before <c>AddCoordinateMediation()</c>; the default,
/// <see cref="DefaultMediationPolicy"/>, grants everyone and allows every registration.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What the policy does not decide.</strong> That a DID belongs to one grant only is the
/// store's rule, enforced atomically (<see cref="IMediationStore"/>); a policy cannot relax it. The
/// policy is an extra gate in front: an allow-list, a rate limit, a paid plan, or proof that the
/// peer controls the DID it registers (the protocol asks for none, so the first peer to register a
/// DID gets it).
/// </para>
/// <para>
/// <strong>Who is asking.</strong> <c>peerDid</c> is always the DID subject of the authcrypt
/// sender key, never the plaintext <c>from</c>. Requests without an authcrypt layer never reach the
/// policy. An implementation is a singleton and MUST be thread-safe. If it throws, nothing is
/// granted or registered (a <c>mediate-request</c> gets no reply, a <c>recipient-update</c> entry
/// gets <c>server_error</c>) and only the exception's type name is logged.
/// </para>
/// </remarks>
public interface IMediationPolicy
{
    /// <summary>
    /// Decide a <c>mediate-request</c> from a peer that holds no grant. A peer that already holds
    /// one is answered with that grant again and the policy is not asked.
    /// </summary>
    /// <param name="peerDid">The requester's DID subject.</param>
    /// <param name="context">The dispatch context of the request (envelope metadata, options, session).</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<MediationDecision> OnMediateRequestAsync(string peerDid, ProtocolContext context, CancellationToken ct);

    /// <summary>
    /// Whether <paramref name="peerDid"/> may register <paramref name="recipientDid"/> (an
    /// <c>add</c>). Asked once per entry, only for a peer that holds a grant and only for a bare DID
    /// that is not one of the mediator's own; <c>false</c> answers <c>client_error</c>. Removals are
    /// not asked about.
    /// </summary>
    /// <param name="peerDid">The DID subject of the peer that holds the grant.</param>
    /// <param name="recipientDid">The bare DID it wants forwards accepted for.</param>
    /// <param name="context">The dispatch context of the request.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<bool> MayRegisterAsync(string peerDid, string recipientDid, ProtocolContext context, CancellationToken ct);
}

/// <summary>
/// The default <see cref="IMediationPolicy"/>: grant every authenticated peer and allow every
/// registration the store accepts. Right for a closed deployment or a sample; a mediator open to
/// the public should replace it (see <see cref="InMemoryMediationStore"/> on what an unlimited
/// policy lets strangers fill).
/// </summary>
public sealed class DefaultMediationPolicy : IMediationPolicy
{
    /// <inheritdoc />
    public ValueTask<MediationDecision> OnMediateRequestAsync(string peerDid, ProtocolContext context, CancellationToken ct)
        => new(MediationDecision.Grant);

    /// <inheritdoc />
    public ValueTask<bool> MayRegisterAsync(string peerDid, string recipientDid, ProtocolContext context, CancellationToken ct)
        => new(true);
}
