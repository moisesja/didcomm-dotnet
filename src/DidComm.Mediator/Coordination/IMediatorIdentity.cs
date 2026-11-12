namespace DidComm.Mediator.Coordination;

/// <summary>
/// The DIDs this mediator hands out in a <c>mediate-grant</c> as <c>routing_did</c>: where senders
/// address forwards for its recipients (FR-MED-07).
/// </summary>
/// <remarks>
/// The default implementation returns <see cref="CoordinateMediationOptions.RoutingDids"/>.
/// Register your own before <c>AddCoordinateMediation()</c> to supply them from elsewhere. When the
/// list is empty the grant carries the DID that decrypted the <c>mediate-request</c> — the mediator
/// DID the recipient already addressed. Each entry must be a bare DID of at most 2048 characters
/// whose DID document has a <c>DIDCommMessaging</c> service with an <c>http(s)</c> or <c>ws(s)</c>
/// endpoint; a grant that could not be built from the list is not sent. The list is also read for
/// every <c>recipient-update</c>, to refuse the mediator's own DIDs as recipients: if reading it
/// throws, that update is answered <c>server_error</c> for every entry and nothing is applied.
/// Implementations are singletons and MUST be thread-safe.
/// </remarks>
public interface IMediatorIdentity
{
    /// <summary>The routing DIDs, in the order the grant lists them. May be empty.</summary>
    IReadOnlyList<string> RoutingDids { get; }
}

/// <summary>The <see cref="IMediatorIdentity"/> built from <see cref="CoordinateMediationOptions.RoutingDids"/>.</summary>
internal sealed class ConfiguredMediatorIdentity : IMediatorIdentity
{
    public ConfiguredMediatorIdentity(IEnumerable<string> routingDids)
    {
        ArgumentNullException.ThrowIfNull(routingDids);
        // Distinct: registering the same configuration twice must not double the list.
        RoutingDids = Array.AsReadOnly(routingDids.Distinct(StringComparer.Ordinal).ToArray());
    }

    public IReadOnlyList<string> RoutingDids { get; }
}
