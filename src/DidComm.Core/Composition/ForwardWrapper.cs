using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Composition;

/// <summary>
/// Sender-side <c>forward</c> wrapping per Routing Protocol 2.0 (PRD §8 / FR-ROUTE-02). Takes
/// an already-encrypted-for-final-recipient payload (N₀) and the routing-key JWKs in
/// outer-to-inner order, then loops in reverse to build the onion: for each routing key it
/// composes a fresh <c>forward</c> message wrapping the current ciphertext as an attachment,
/// then anoncrypts that forward for the routing key.
/// </summary>
/// <remarks>
/// <para>
/// The wrapping always uses anoncrypt (no <c>skid</c>) per the spec — every forward layer is
/// anonymously addressed because the routing keys belong to mediators that have no business
/// learning the original sender.
/// </para>
/// <para>
/// The content-encryption algorithm is fixed to <c>A256CBC-HS512</c> for forward layers: it is
/// the FR-ENC-05 floor for anoncrypt and the only algorithm guaranteed to be supported by
/// every legal recipient curve. Senders that pinned a different ciphertext alg on the
/// inner-most layer (e.g. A256GCM for size) still get A256CBC-HS512 onion layers — the inner
/// payload is opaque to mediators anyway.
/// </para>
/// </remarks>
internal static class ForwardWrapper
{
    /// <summary>
    /// Wrap <paramref name="innerPackedPayload"/> in N forward layers, one per routing key,
    /// outermost first as listed in <paramref name="routingKeyJwksOuterToInner"/>.
    /// </summary>
    /// <param name="innerPackedPayload">The packed (encrypted) payload for the final recipient — wrapped unchanged inside the innermost forward.</param>
    /// <param name="routingKeyJwksOuterToInner">Routing-key JWKs in OUTER-to-INNER order (the list from <c>ResolvedRoute</c>). The loop runs in reverse so the outermost wrap addresses index 0.</param>
    /// <param name="finalRecipientDid">The DID of the FINAL recipient; used as <c>body.next</c> on the innermost forward.</param>
    /// <param name="cryptoProvider">JOSE crypto provider (NetCrypto-backed).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The fully-wrapped outermost JWE JSON ready to hand to a transport.</returns>
    public static async Task<string> WrapAsync(
        string innerPackedPayload,
        IReadOnlyList<Jwk> routingKeyJwksOuterToInner,
        string finalRecipientDid,
        JoseCryptoProvider cryptoProvider,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(innerPackedPayload);
        ArgumentNullException.ThrowIfNull(routingKeyJwksOuterToInner);
        ArgumentException.ThrowIfNullOrEmpty(finalRecipientDid);
        ArgumentNullException.ThrowIfNull(cryptoProvider);

        if (routingKeyJwksOuterToInner.Count == 0)
            throw new ArgumentException("ForwardWrapper requires at least one routing key — direct delivery should bypass this layer entirely.", nameof(routingKeyJwksOuterToInner));

        var current = innerPackedPayload;

        // Iterate INNER → OUTER (last entry first). At each step the current ciphertext becomes
        // the attachment of a new forward addressed to the next-outer routing key.
        // body.next is the DID one hop INWARD from the current routing key. For the innermost
        // wrap that's the final recipient; for outer wraps it's the kid the previous (more
        // inward) layer was encrypted for.
        var nextHopDid = finalRecipientDid;
        for (var i = routingKeyJwksOuterToInner.Count - 1; i >= 0; i--)
        {
            var routingKey = routingKeyJwksOuterToInner[i];
            var forwardMessage = ForwardMessage.Create(
                mediator: ExtractDidFromKid(routingKey.Kid),
                next: nextHopDid,
                packedPayloads: new[] { current });

            current = await EnvelopeWriter.PackEncryptedAsync(
                new PackEncryptedParameters(
                    Message: forwardMessage,
                    Recipients: new[] { routingKey },
                    ContentEncryption: JoseAlgorithms.A256CbcHs512),
                cryptoProvider,
                ct).ConfigureAwait(false);

            // Next outer wrap's body.next is the BARE DID of the routing key we just encrypted for —
            // body.next is a DID, not a key id, so strip the #fragment (mirrors the `mediator` field).
            nextHopDid = ExtractDidFromKid(routingKey.Kid);
        }

        return current;
    }

    private static string ExtractDidFromKid(string? kid)
    {
        if (string.IsNullOrEmpty(kid))
            throw new InvalidOperationException("Routing-key JWK is missing 'kid'; cannot derive the mediator DID for the forward message 'to' header.");
        var hash = kid.IndexOf('#');
        return hash < 0 ? kid : kid[..hash];
    }
}
