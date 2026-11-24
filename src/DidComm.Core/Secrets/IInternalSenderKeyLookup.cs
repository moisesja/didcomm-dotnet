namespace DidComm.Secrets;

/// <summary>
/// Minimal internal contract that the envelope layer uses to fetch the *public* key of a
/// remote sender — needed for authcrypt unpack (Zs = ECDH(local_priv, sender_pub)).
/// </summary>
/// <remarks>
/// Asynchronous, and given the unpack's cancellation token. DataProofsDotnet.Jose asks for the
/// sender's key through its synchronous <c>IJweSenderKeyResolver.TryGet</c>; the envelope layer
/// resolves the key through this contract first and lets the parser's callback answer from the
/// result (see <c>EnvelopeReader</c>), so this lookup is always awaited, never called from the
/// parser.
/// </remarks>
internal interface IInternalSenderKeyLookup
{
    /// <summary>The sender's public JWK for <paramref name="skid"/>, or <c>null</c> when unknown.</summary>
    /// <param name="skid">The sender key id the parser asked for (the <c>skid</c>, or the kid decoded from <c>apu</c>).</param>
    /// <param name="ct">The unpack's cancellation token.</param>
    ValueTask<Jwk?> ResolveAsync(string skid, CancellationToken ct);
}
