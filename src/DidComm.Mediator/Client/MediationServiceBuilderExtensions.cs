using DidComm.Mediator.Coordination;
using DidComm.Resolution;

namespace DidComm.Mediator.Client;

/// <summary>
/// Adds <c>DidCommServiceBuilder.ForMediation(grant)</c>: the service entry of a DID reached
/// through a mediator that granted you mediation (FR-ROUTE-04, FR-MED-09).
/// </summary>
public static class MediationServiceBuilderExtensions
{
    /// <summary>Static members added to <see cref="DidCommServiceBuilder"/>.</summary>
    extension(DidCommServiceBuilder)
    {
        /// <summary>
        /// Start the service entry of a DID reached through the mediator that issued
        /// <paramref name="grant"/>: the grant's first routing DID becomes the entry's <c>uri</c>, as
        /// <see cref="DidCommServiceBuilder.ForMediator"/> does with it. Register the DID with the
        /// mediator (<see cref="MediationClient.UpdateRecipientsAsync"/>) before you publish it: a
        /// mediator does not accept messages for a DID it does not know (this library's mediator
        /// drops them).
        /// </summary>
        /// <param name="grant">The grant <see cref="MediationClient.RequestMediationAsync"/> returned.</param>
        /// <returns>A builder for the entry; set what differs from the defaults and call <see cref="DidCommServiceBuilder.Build"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="grant"/> is <c>null</c>, or so is its first routing DID.</exception>
        /// <exception cref="ArgumentException">The grant names no routing DID, or its first routing DID is not a bare DID of at most 2048 characters.</exception>
        public static DidCommServiceBuilder ForMediation(MediationGrant grant)
        {
            ArgumentNullException.ThrowIfNull(grant);
            if (grant.RoutingDids is not { Count: > 0 } routingDids)
                throw new ArgumentException("The grant names no routing DID.", nameof(grant));
            return DidCommServiceBuilder.ForMediator(routingDids[0]);
        }
    }
}
