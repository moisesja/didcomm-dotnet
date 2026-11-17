using DidComm.Mediator.Coordination;
using DidComm.Protocols;

namespace DidComm.Samples.MediatedAgent;

/// <summary>
/// A mediation policy that grants mediation only to peers the operator added to an allow-list,
/// and otherwise behaves like the library's default policy (FR-MED-05).
/// </summary>
/// <remarks>
/// <para>
/// Enrolment is open by default: anyone who can authcrypt a <c>mediate-request</c> gets a grant, and
/// strangers' grants can fill the mediator's bounds. A public mediator should replace the default
/// with a policy like this one, or one that rate-limits enrolment.
/// </para>
/// <para>
/// The policy compares DID subjects (a DID URL's path, query or fragment is ignored), ordinally,
/// and always walks the whole list: how long the check takes does not tell a caller how far down
/// the list its DID matched. The list is bounded (<see cref="MaxEntries"/>).
/// </para>
/// </remarks>
internal sealed class AllowListMediationPolicy : IMediationPolicy
{
    /// <summary>The most peers the list holds.</summary>
    public const int MaxEntries = 1024;

    private readonly IMediationPolicy _inner;
    private readonly object _gate = new();
    // Copy on write: readers take the current array and never see it change.
    private string[] _allowed = Array.Empty<string>();

    /// <summary>Wrap <paramref name="inner"/>: an allowed peer is decided by it.</summary>
    /// <param name="inner">The policy that decides for allowed peers.</param>
    public AllowListMediationPolicy(IMediationPolicy inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>How many peers are on the list.</summary>
    public int Count => Volatile.Read(ref _allowed).Length;

    /// <summary>Add a peer, for example when it signs up with the operator.</summary>
    /// <param name="did">The peer's DID (a DID URL is reduced to its DID).</param>
    /// <exception cref="InvalidOperationException">The list is full.</exception>
    public void Allow(string did)
    {
        var subject = SubjectOf(did) ?? throw new ArgumentException("Not a DID.", nameof(did));
        lock (_gate)
        {
            if (IsListed(_allowed, subject))
                return;
            if (_allowed.Length >= MaxEntries)
                throw new InvalidOperationException($"The allow-list is full ({MaxEntries} peers).");
            Volatile.Write(ref _allowed, [.. _allowed, subject]);
        }
    }

    /// <inheritdoc />
    public ValueTask<MediationDecision> OnMediateRequestAsync(string peerDid, ProtocolContext context, CancellationToken ct)
    {
        // peerDid is the DID of the key that authenticated the request, never the plaintext from.
        var subject = SubjectOf(peerDid);
        return subject is not null && IsListed(Volatile.Read(ref _allowed), subject)
            ? _inner.OnMediateRequestAsync(peerDid, context, ct)
            : new ValueTask<MediationDecision>(MediationDecision.Deny);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Registration stays the default's call. The protocol asks for no proof that a peer controls a
    /// DID it registers, so the first peer to register a DID gets it: a wallet registers its DID
    /// before handing it out.
    /// </remarks>
    public ValueTask<bool> MayRegisterAsync(string peerDid, string recipientDid, ProtocolContext context, CancellationToken ct)
        => _inner.MayRegisterAsync(peerDid, recipientDid, context, ct);

    // One pass over the whole list, no early exit: the flag is accumulated, never returned from
    // inside the loop.
    private static bool IsListed(string[] allowed, string subject)
    {
        var found = false;
        foreach (var entry in allowed)
            found |= string.Equals(entry, subject, StringComparison.Ordinal);
        return found;
    }

    // The DID subject of a DID or DID URL: everything before the first '/', '?' or '#'.
    private static string? SubjectOf(string? did)
    {
        if (string.IsNullOrEmpty(did) || !did.StartsWith("did:", StringComparison.Ordinal))
            return null;
        var end = did.IndexOfAny(new[] { '/', '?', '#' });
        return end < 0 ? did : did[..end];
    }
}
