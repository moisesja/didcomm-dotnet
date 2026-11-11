using System.Collections.Concurrent;
using DidComm.Consistency;

namespace DidComm.Protocols.Routing;

/// <summary>
/// Process-local <see cref="IForwardRecipientRegistry"/>: the host registers the DIDs it mediates
/// for (sample 04's recipient directory; FR-ROUTE-11). Starts empty — until something is
/// registered, every forward is dropped.
/// </summary>
/// <remarks>
/// Entries are stored by DID subject (PRD §4.3 <c>DidSubjectOf</c>), so registering a DID URL
/// registers its bare DID. Lookups are a single ordinal hash probe whose cost does not depend on
/// whether the DID is present — the FR-ROUTE-11 "constant-work" lookup. Thread-safe; a singleton.
/// </remarks>
public sealed class InMemoryForwardRecipientRegistry : IForwardRecipientRegistry
{
    private readonly ConcurrentDictionary<string, byte> _recipients = new(StringComparer.Ordinal);

    /// <summary>Register a recipient. A DID URL is reduced to its DID subject; re-registration is a no-op.</summary>
    /// <param name="didOrDidUrl">The recipient's DID or a DID URL under it.</param>
    /// <exception cref="ArgumentException">When <paramref name="didOrDidUrl"/> is not a DID or DID URL (the value is not echoed).</exception>
    public void Register(string didOrDidUrl)
    {
        ArgumentNullException.ThrowIfNull(didOrDidUrl);
        var subject = DidSubject.DidSubjectOf(didOrDidUrl)
            ?? throw new ArgumentException("The value is not a DID or DID URL.", nameof(didOrDidUrl));
        _recipients.TryAdd(subject, 0);
    }

    /// <summary>Remove a recipient. Returns <c>false</c> when it was not registered or the value is not a DID or DID URL.</summary>
    /// <param name="didOrDidUrl">The recipient's DID or a DID URL under it.</param>
    public bool Unregister(string didOrDidUrl)
    {
        var subject = DidSubject.DidSubjectOf(didOrDidUrl);
        return subject is not null && _recipients.TryRemove(subject, out _);
    }

    /// <summary>The number of registered recipients.</summary>
    public int Count => _recipients.Count;

    /// <inheritdoc />
    public ValueTask<bool> IsRegisteredAsync(string recipientDid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recipientDid);
        return new ValueTask<bool>(_recipients.ContainsKey(recipientDid));
    }
}
