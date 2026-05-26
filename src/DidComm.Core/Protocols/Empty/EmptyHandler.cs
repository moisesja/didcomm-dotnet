using DidComm.Messages;

namespace DidComm.Protocols.Empty;

/// <summary>
/// Handler for Empty 1.0 (FR-PROTO-06). The protocol is header-only: an empty message exists
/// to carry an <c>ack</c> (or another headers-only signal) without a body. The handler always
/// returns <c>null</c> — by spec there is nothing more to say once an empty is received — and
/// merely presents the protocol as "supported" to the registry / Discover Features (6.2b).
/// </summary>
public sealed class EmptyHandler : IProtocolHandler
{
    /// <inheritdoc />
    public string ProtocolUri => EmptyProtocol.ProtocolUri;

    /// <inheritdoc />
    public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
        => Task.FromResult<Message?>(null);
}
