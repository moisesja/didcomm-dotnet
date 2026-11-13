using System.Collections.ObjectModel;

namespace DidComm.Mediator.Coordination;

/// <summary>
/// A fixed list of routing DIDs for the mediator (FR-MED-07): the <see cref="IMediatorIdentity"/>
/// a host can register itself when the DIDs come from somewhere other than
/// <see cref="CoordinateMediationOptions.RoutingDids"/>.
/// </summary>
/// <remarks>
/// The list is checked and copied when the instance is built, so it never changes afterwards;
/// reading <see cref="RoutingDids"/> is cheap, thread-safe and never throws. The first DID is the
/// primary one: grants list it first and the mediator's invitation is issued from it.
/// </remarks>
public sealed class MediatorIdentity : IMediatorIdentity
{
    /// <summary>Create the identity from a list of routing DIDs.</summary>
    /// <param name="routingDids">At least one DID, each a bare DID (no path, query or fragment) of at most 2048 characters. A repeated DID is kept once, at its first position; the first DID is the primary one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="routingDids"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The list is empty, or an entry is not a bare DID. The DID is not quoted.</exception>
    public MediatorIdentity(IEnumerable<string> routingDids)
    {
        ArgumentNullException.ThrowIfNull(routingDids);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dids = new List<string>();
        var index = 0;
        foreach (var did in routingDids)
        {
            if (!BareDid.IsBareDid(did))
            {
                throw new ArgumentException(
                    $"Routing DID at index {index} must be a bare DID (no path, query or fragment) of at most 2048 characters.",
                    nameof(routingDids));
            }
            if (seen.Add(did))
                dids.Add(did);
            index++;
        }

        if (dids.Count == 0)
            throw new ArgumentException("A mediator needs at least one routing DID.", nameof(routingDids));

        RoutingDids = new ReadOnlyCollection<string>(dids);
    }

    /// <summary>Create the identity from routing DIDs given one by one.</summary>
    /// <param name="routingDids">At least one DID, each a bare DID (no path, query or fragment) of at most 2048 characters. A repeated DID is kept once, at its first position; the first DID is the primary one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="routingDids"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">No DID is given, or one is not a bare DID. The DID is not quoted.</exception>
    public MediatorIdentity(params string[] routingDids)
        : this((IEnumerable<string>)routingDids)
    {
    }

    /// <summary>The routing DIDs, primary first, without repeats. Never empty.</summary>
    public IReadOnlyList<string> RoutingDids { get; }
}
