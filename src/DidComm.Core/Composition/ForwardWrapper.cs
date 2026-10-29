using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Composition;

/// <summary>
/// Sender-side <c>forward</c> wrapping per Routing Protocol 2.0 (PRD §8 / FR-ROUTE-02). Takes
/// an already-encrypted-for-final-recipient payload (N₀) and the forward layers in
/// outer-to-inner order, then loops in reverse to build the onion: for each layer it composes a
/// fresh <c>forward</c> message wrapping the current ciphertext as an attachment, then anoncrypts
/// that forward for the layer's keys.
/// </summary>
/// <remarks>
/// <para>
/// The wrapping always uses anoncrypt (no <c>skid</c>) per the spec — every forward layer is
/// anonymously addressed because the routing keys belong to mediators that have no business
/// learning the original sender.
/// </para>
/// <para>
/// A layer can address several keys of one mediator (a mediator named by its DID: all of its
/// keyAgreement keys on one curve, FR-ENC-19), so the mediator can unwrap on any device it runs.
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
    /// Wrap <paramref name="innerPackedPayload"/> in one forward layer per routing key, outermost
    /// first as listed in <paramref name="routingKeyJwksOuterToInner"/>. Each key gets its own
    /// layer, addressed to the DID in its <c>kid</c>.
    /// </summary>
    /// <param name="innerPackedPayload">The packed (encrypted) payload for the final recipient — wrapped unchanged inside the innermost forward.</param>
    /// <param name="routingKeyJwksOuterToInner">Routing-key JWKs in OUTER-to-INNER order. The loop runs in reverse so the outermost wrap addresses index 0.</param>
    /// <param name="finalRecipientDid">The DID of the FINAL recipient; used as <c>body.next</c> on the innermost forward.</param>
    /// <param name="cryptoProvider">JOSE crypto provider (NetCrypto-backed).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The fully-wrapped outermost JWE JSON ready to hand to a transport.</returns>
    public static Task<string> WrapAsync(
        string innerPackedPayload,
        IReadOnlyList<Jwk> routingKeyJwksOuterToInner,
        string finalRecipientDid,
        JoseCryptoProvider cryptoProvider,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(routingKeyJwksOuterToInner);
        if (routingKeyJwksOuterToInner.Count == 0)
            throw new ArgumentException("ForwardWrapper requires at least one routing key — direct delivery should bypass this layer entirely.", nameof(routingKeyJwksOuterToInner));

        var layers = new ForwardLayer[routingKeyJwksOuterToInner.Count];
        for (var i = 0; i < layers.Length; i++)
        {
            var routingKey = routingKeyJwksOuterToInner[i];
            layers[i] = new ForwardLayer(ExtractDidFromKid(routingKey.Kid), new[] { routingKey });
        }
        return WrapAsync(innerPackedPayload, layers, finalRecipientDid, cryptoProvider, ct);
    }

    /// <summary>
    /// Wrap <paramref name="innerPackedPayload"/> in N forward layers, outermost first as listed in
    /// <paramref name="layersOuterToInner"/>. Each layer's envelope is encrypted to all of that
    /// layer's keys.
    /// </summary>
    /// <param name="innerPackedPayload">The packed (encrypted) payload for the final recipient — wrapped unchanged inside the innermost forward.</param>
    /// <param name="layersOuterToInner">Forward layers in OUTER-to-INNER order (from <see cref="ExpandedRoute.Layers"/>). The loop runs in reverse so the outermost wrap is index 0.</param>
    /// <param name="finalRecipientDid">The DID of the FINAL recipient; used as <c>body.next</c> on the innermost forward.</param>
    /// <param name="cryptoProvider">JOSE crypto provider (NetCrypto-backed).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The fully-wrapped outermost JWE JSON ready to hand to a transport.</returns>
    public static async Task<string> WrapAsync(
        string innerPackedPayload,
        IReadOnlyList<ForwardLayer> layersOuterToInner,
        string finalRecipientDid,
        JoseCryptoProvider cryptoProvider,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(innerPackedPayload);
        ArgumentNullException.ThrowIfNull(layersOuterToInner);
        ArgumentException.ThrowIfNullOrEmpty(finalRecipientDid);
        ArgumentNullException.ThrowIfNull(cryptoProvider);

        if (layersOuterToInner.Count == 0)
            throw new ArgumentException("ForwardWrapper requires at least one forward layer — direct delivery should bypass this layer entirely.", nameof(layersOuterToInner));

        var current = innerPackedPayload;

        // Iterate INNER → OUTER (last entry first). At each step the current ciphertext becomes
        // the attachment of a new forward addressed to the next-outer layer's mediator.
        // body.next is the DID one hop INWARD from the current layer. For the innermost wrap
        // that's the final recipient; for outer wraps it's the bare DID of the mediator the
        // previous (more inward) layer was encrypted for — body.next is a DID, not a key id.
        var nextHopDid = finalRecipientDid;
        for (var i = layersOuterToInner.Count - 1; i >= 0; i--)
        {
            var layer = layersOuterToInner[i];
            if (layer.Recipients is not { Count: > 0 })
                throw new ArgumentException("Every forward layer needs at least one recipient key.", nameof(layersOuterToInner));

            var forwardMessage = ForwardMessage.Create(
                mediator: layer.Did,
                next: nextHopDid,
                packedPayloads: new[] { current });

            current = await EnvelopeWriter.PackEncryptedAsync(
                new PackEncryptedParameters(
                    Message: forwardMessage,
                    Recipients: layer.Recipients,
                    ContentEncryption: JoseAlgorithms.A256CbcHs512),
                cryptoProvider,
                ct).ConfigureAwait(false);

            nextHopDid = layer.Did;
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
