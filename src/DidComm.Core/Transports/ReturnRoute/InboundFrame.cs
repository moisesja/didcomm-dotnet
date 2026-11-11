namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// One whole message a transport received on a return-route session socket, handed to
/// <see cref="IInboundFrameConsumer.ConsumeAsync"/> (PRD §8a, FR-RR-07).
/// </summary>
/// <remarks>
/// The synthesized record equality compares <see cref="Payload"/> by memory identity (the same
/// underlying buffer and range), not by content.
/// </remarks>
/// <param name="Endpoint">The endpoint the session socket was opened to.</param>
/// <param name="LocalDid">The DID subject of the sender of the request that opened the socket: the DID a returned message should be encrypted to. The transport's attribution, not a property of the frame.</param>
/// <param name="PeerDid">The DID subject of the recipient of the request that opened the socket: the DID a returned message should come from. The transport's attribution, not a property of the frame.</param>
/// <param name="Payload">The received bytes, untrusted: at most the transport's receive cap.</param>
public sealed record InboundFrame(Uri Endpoint, string LocalDid, string PeerDid, ReadOnlyMemory<byte> Payload);
