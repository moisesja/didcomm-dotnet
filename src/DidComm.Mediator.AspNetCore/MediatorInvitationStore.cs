using DidComm.Mediator.Pipeline;
using DidComm.Protocols.OutOfBand;

namespace DidComm.Mediator.AspNetCore;

/// <summary>
/// The read-only <see cref="IOobInvitationStore"/> behind the mediator's invitation endpoint: it
/// serves the invitation the mediator built at startup (<see cref="MediatorInvitationHolder"/>)
/// under <see cref="MediatorEndpointRouteBuilderExtensions.InvitationOobId"/> only, and nothing
/// before the mediator has started.
/// </summary>
internal sealed class MediatorInvitationStore : IOobInvitationStore
{
    private readonly MediatorInvitationHolder _holder;

    /// <summary>Serve the invitation held by <paramref name="holder"/>.</summary>
    /// <param name="holder">The mediator's invitation holder (a singleton registered by <c>AddMediator()</c>).</param>
    internal MediatorInvitationStore(MediatorInvitationHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        _holder = holder;
    }

    /// <summary>Not supported: the mediator builds its invitation itself when it starts.</summary>
    /// <param name="oobId">Ignored.</param>
    /// <param name="plaintextInvitationJson">Ignored.</param>
    /// <exception cref="NotSupportedException">Always.</exception>
    public void Store(string oobId, string plaintextInvitationJson)
        => throw new NotSupportedException("The mediator's invitation store is read-only: the mediator builds its invitation when it starts.");

    /// <summary>
    /// The mediator's invitation (plaintext JSON) when <paramref name="oobId"/> is exactly
    /// <see cref="MediatorEndpointRouteBuilderExtensions.InvitationOobId"/> and the mediator has
    /// built it; otherwise <c>null</c> (the endpoint answers <c>404</c>).
    /// </summary>
    /// <param name="oobId">The <c>_oobid</c> value of the request.</param>
    public string? Retrieve(string oobId)
    {
        if (!string.Equals(oobId, MediatorEndpointRouteBuilderExtensions.InvitationOobId, StringComparison.Ordinal))
            return null;
        var json = _holder.PlaintextJson;
        return string.IsNullOrEmpty(json) ? null : json;
    }

    /// <summary>Not supported: the mediator's invitation lives as long as the mediator.</summary>
    /// <param name="oobId">Ignored.</param>
    /// <exception cref="NotSupportedException">Always.</exception>
    public bool Remove(string oobId)
        => throw new NotSupportedException("The mediator's invitation store is read-only: the mediator's invitation lives as long as the mediator.");
}
