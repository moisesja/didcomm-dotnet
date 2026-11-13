using DidComm.Extensions.DependencyInjection;

namespace DidComm.Mediator;

/// <summary>Registers a complete DIDComm mediator on a <see cref="DidCommBuilder"/>.</summary>
public static class MediatorBuilderExtensions
{
    /// <summary>
    /// Make this agent a complete mediator (FR-MED-07, FR-PICK-09/10): Coordinate Mediation 3.0,
    /// the routing mediator that relays forwards, Message Pickup 3.0, and the delivery pipeline
    /// that puts each relayed message in the recipient's queue (pushing it live, or directly to the
    /// recipient's endpoint, when it can). Map its endpoints with <c>MapDidCommMediator()</c> from
    /// the <c>DidComm.Mediator.AspNetCore</c> package.
    /// </summary>
    /// <remarks>
    /// The mediator needs at least one routing DID, set in
    /// <see cref="MediatorOptions.CoordinateMediation"/> or supplied by an <c>IMediatorIdentity</c>
    /// registered before this call. <paramref name="configure"/> may run several times, on
    /// different instances, so it must only assign values (see <see cref="MediatorOptions"/>).
    /// </remarks>
    /// <param name="builder">The DIDComm builder.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
    public static DidCommBuilder AddMediator(this DidCommBuilder builder, Action<MediatorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        throw new NotImplementedException("#95 C1");
    }
}
