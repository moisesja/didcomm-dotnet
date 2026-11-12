using DidComm.Protocols.Routing;

namespace DidComm.Mediator.Coordination;

/// <summary>
/// Makes the mediation store the mediator's forward recipient directory (FR-ROUTE-11): a forward is
/// relayed when the DID subject of its <c>next</c> is registered under some grant. One call to
/// <see cref="IMediationStore.FindGrantByRecipientDidAsync"/> per forward and nothing else, so the
/// lookup costs what the store's lookup costs, hit or miss.
/// </summary>
internal sealed class MediationForwardRecipientRegistry : IForwardRecipientRegistry
{
    private readonly IMediationStore _store;

    public MediationForwardRecipientRegistry(IMediationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public ValueTask<bool> IsRegisteredAsync(string recipientDid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recipientDid);
        var lookup = _store.FindGrantByRecipientDidAsync(recipientDid, ct);
        return lookup.IsCompletedSuccessfully
            ? new ValueTask<bool>(lookup.Result is not null)
            : Awaited(lookup);

        static async ValueTask<bool> Awaited(ValueTask<MediationGrant?> pending)
            => await pending.ConfigureAwait(false) is not null;
    }
}
