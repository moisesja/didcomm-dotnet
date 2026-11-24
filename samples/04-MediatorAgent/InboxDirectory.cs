using DidComm.Mediator;

namespace DidComm.Samples.MediatorAgent;

/// <summary>
/// The operator's list of recipient inboxes, for the mediator's own push to a recipient's endpoint
/// (<see cref="MediatorOptions.DirectPush"/>, section 9): for each recipient that enrolled with the
/// mediator, the address of its own HTTP inbox.
/// </summary>
/// <remarks>
/// <para>
/// When a recipient has no live connection, the mediator asks this directory where the recipient's
/// inbox is, just before it pushes a stored message there. It never sends to an address a message
/// carries.
/// </para>
/// <para>
/// Only the operator fills it (<see cref="TryAdd"/>), from what the recipient agreed with the
/// operator when it enrolled. Never fill it from a peer's message or from a DID document: otherwise
/// a stranger could make the mediator post messages to an address of the stranger's choosing. The
/// DIDComm specification leaves this agreement between a recipient and its mediator to them
/// (routing.md, "Mediator Process").
/// </para>
/// <para>
/// The key is the DID the recipient enrolled with (the grant's <c>PeerDid</c>), not the DIDs it
/// registered under the grant and gave to senders: one entry covers every DID a recipient
/// registers. The directory is bounded (<see cref="MaxEntries"/>) and thread-safe, and it takes
/// nothing in its constructor: a directory must never take the protocol dispatcher, a protocol
/// handler, <c>DidCommClient</c> or the pickup queue.
/// </para>
/// <para>
/// Treat the addresses as you would any secret a recipient gave you: an inbox address can be a
/// secret capability URL. The mediator's own log lines never name it, and a push does not go
/// through .NET's HTTP client request logging (it uses only the innermost handler of the
/// <c>"didcomm"</c> client). The host's own sends through that client are still logged there
/// (categories <c>System.Net.Http.HttpClient.didcomm.*</c>, at Information level).
/// </para>
/// </remarks>
public sealed class InboxDirectory : IRecipientEndpointDirectory
{
    /// <summary>The most inboxes this directory keeps.</summary>
    public const int MaxEntries = 16;

    private readonly object _gate = new();
    private readonly Dictionary<string, Uri> _inboxes = new(StringComparer.Ordinal);

    /// <summary>The number of inboxes in the directory.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _inboxes.Count;
        }
    }

    /// <summary>
    /// Record where the recipient that enrolled as <paramref name="enrolledDid"/> receives its
    /// messages. Returns <c>false</c> when the DID is not a bare DID, the inbox is not an absolute
    /// address, or the directory is full and the DID is not already in it.
    /// </summary>
    /// <param name="enrolledDid">The DID the recipient enrolled with (the grant's peer DID): a bare DID, with no path, query or fragment.</param>
    /// <param name="inbox">The recipient's own inbox. The mediator pushes only to <c>http</c> or <c>https</c> addresses its outbound guard allows.</param>
    public bool TryAdd(string enrolledDid, Uri inbox)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        if (enrolledDid is null || InboxRelaySink.DidSubjectOf(enrolledDid) != enrolledDid || !inbox.IsAbsoluteUri)
            return false;

        lock (_gate)
        {
            if (_inboxes.Count >= MaxEntries && !_inboxes.ContainsKey(enrolledDid))
                return false;
            _inboxes[enrolledDid] = inbox;
            return true;
        }
    }

    /// <inheritdoc />
    public ValueTask<Uri?> FindAsync(string grantPeerDid, CancellationToken ct)
    {
        Uri? inbox;
        lock (_gate)
            _inboxes.TryGetValue(grantPeerDid, out inbox);
        return ValueTask.FromResult(inbox);
    }
}
