namespace DidComm.Facade;

/// <summary>
/// How <see cref="DidCommClient.SendPackedAsync(ReadOnlyMemory{byte}, Uri, string, SendPackedOptions, System.Threading.CancellationToken)"/>
/// delivers bytes that are already packed.
/// </summary>
/// <example>
/// <code>
/// // One request to exactly this endpoint: no retry, no redirect followed, no circuit breaker.
/// await client.SendPackedAsync(packed, endpoint, MediaTypes.Encrypted, new SendPackedOptions { SingleAttempt = true }, ct);
/// </code>
/// </example>
public sealed class SendPackedOptions
{
    /// <summary>
    /// Make exactly one attempt (<see cref="Transports.TransportRequest.SingleAttempt"/>): a single
    /// request to exactly the endpoint given, with no retry, no redirect followed (a redirect answer
    /// is a failure, not a delivery) and outside the transport's circuit breaker, so a failing
    /// endpoint neither opens it for other sends nor is refused because of them. Use it when the
    /// caller counts attempts itself or must not reach any endpoint it did not choose, as the
    /// mediator's direct push does. Default <c>false</c>: the transport's own retry, redirect and
    /// circuit-breaker rules apply. The outbound endpoint guard applies either way.
    /// </summary>
    public bool SingleAttempt { get; init; }
}
