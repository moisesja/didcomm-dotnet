namespace DidComm.Facade;

/// <summary>
/// Structured return shape of <c>DidCommClient.PackEncryptedAsync</c> (Phase 4 / FR-ROUTE-02).
/// When forwarding is enabled the facade also surfaces the transport URI the outermost
/// envelope is destined for, plus any FR-ROUTE-08 fallback URIs the recipient's service block
/// advertises.
/// </summary>
/// <remarks>
/// <para>
/// The structured shape replaces the Phase 3 <c>Task&lt;string&gt;</c> return: both for direct
/// (non-forwarded) and forwarded packs the transport binding (Phase 5) needs to know whether
/// it can send <c>Message</c> straight to the recipient's URI or to a mediator's URI. The
/// <see cref="ServiceEndpoint"/> property is non-<c>null</c> only when <c>Forward = true</c>
/// (or when the application explicitly resolves the recipient's endpoint and passes it back
/// in); a non-forwarded pack leaves it <c>null</c> so the consumer's send path stays
/// unambiguous.
/// </para>
/// </remarks>
/// <param name="Message">The packed envelope (JWE / JWS / plaintext JSON, depending on composition). Hand this to a transport.</param>
/// <param name="ServiceEndpoint">The transport URI to send <see cref="Message"/> to. Non-<c>null</c> when the facade resolved a route (i.e. <c>Forward = true</c>); <c>null</c> otherwise.</param>
/// <param name="FallbackServiceEndpoints">Additional candidate URIs in preference order (FR-ROUTE-08 failover input for Phase 5 transports). Empty for non-forwarded packs and for forwarded packs whose recipient publishes only one endpoint.</param>
public sealed record PackEncryptedResult(
    string Message,
    string? ServiceEndpoint,
    IReadOnlyList<string> FallbackServiceEndpoints);
