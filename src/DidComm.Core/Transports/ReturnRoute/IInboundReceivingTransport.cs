namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// A transport that can tell whether the messages a peer pushes back on the connection a
/// return-route send opened reach the registered <see cref="IInboundFrameConsumer"/> (PRD §8a,
/// FR-RR-07). Internal: the recipient-side mediator client asks it, through
/// <see cref="Facade.DidCommClient.ReceivesPushesOn"/>, before it sends a request whose answer can
/// only come back that way, so a set-up that would discard the answer is refused before anything is
/// sent instead of timing out (#96). A transport that does not implement it is "cannot tell".
/// </summary>
internal interface IInboundReceivingTransport
{
    /// <summary>
    /// Whether a message pushed on a return-route session socket this transport opens is handed to
    /// the inbound consumer. Read when a send is about to start; the answer applies to the sends
    /// that start after it.
    /// </summary>
    bool ReceivesInbound { get; }
}
