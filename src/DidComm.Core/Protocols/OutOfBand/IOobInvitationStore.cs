using System.Collections.Concurrent;

namespace DidComm.Protocols.OutOfBand;

/// <summary>
/// Server-side store for the short-URL form of an out-of-band invitation (FR-OOB-04). The
/// sender keeps the full plaintext invitation under a generated id and serves it on an HTTP
/// GET when a recipient dereferences a <c>?_oobid=&lt;id&gt;</c> URL.
/// </summary>
/// <remarks>
/// The spec forbids public URL shorteners for OOB (privacy); the sender hosts its own
/// retrieval endpoint instead — see <c>MapDidCommOobEndpoint</c> in <c>DidComm.AspNetCore</c>.
/// </remarks>
public interface IOobInvitationStore
{
    /// <summary>Store the plaintext invitation JSON under <paramref name="oobId"/> (idempotent — last write wins).</summary>
    /// <param name="oobId">The opaque short-form id embedded in the <c>?_oobid=</c> URL.</param>
    /// <param name="plaintextInvitationJson">The full invitation, already serialized to plaintext JSON.</param>
    void Store(string oobId, string plaintextInvitationJson);

    /// <summary>Return the stored plaintext invitation JSON for <paramref name="oobId"/>, or <c>null</c> when unknown.</summary>
    /// <param name="oobId">The short-form id to resolve.</param>
    string? Retrieve(string oobId);

    /// <summary>Remove the entry for <paramref name="oobId"/>; returns <c>true</c> when one was present.</summary>
    /// <param name="oobId">The short-form id to evict.</param>
    bool Remove(string oobId);
}

/// <summary>
/// In-memory <see cref="IOobInvitationStore"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Demo / single-process grade — mirrors <c>InMemoryThreadStateStore</c>; production deployments
/// behind multiple instances should back this with a shared store with appropriate expiry.
/// </summary>
public sealed class InMemoryOobInvitationStore : IOobInvitationStore
{
    private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Store(string oobId, string plaintextInvitationJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(oobId);
        ArgumentException.ThrowIfNullOrEmpty(plaintextInvitationJson);
        _store[oobId] = plaintextInvitationJson;
    }

    /// <inheritdoc />
    public string? Retrieve(string oobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(oobId);
        return _store.TryGetValue(oobId, out var json) ? json : null;
    }

    /// <inheritdoc />
    public bool Remove(string oobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(oobId);
        return _store.TryRemove(oobId, out _);
    }
}
