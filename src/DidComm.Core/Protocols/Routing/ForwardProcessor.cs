using System.Text;
using System.Text.Json.Nodes;
using DidComm.Crypto;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Jose.Encryption;

namespace DidComm.Protocols.Routing;

/// <summary>
/// Mediator-side <c>forward</c> processing (PRD §8 / FR-ROUTE-05/06/07). A pure
/// transformation: takes a packed inbound DIDComm envelope, asks the consumer's
/// <see cref="DidCommClient"/> to decrypt it, validates that it is a forward, drops
/// <c>please_ack</c> (FR-ROUTE-07), and produces a <see cref="ForwardProcessingResult"/>
/// describing where the onward bytes should go.
/// </summary>
/// <remarks>
/// <para>
/// The processor performs no transport I/O. Hosts driving the mediator role hand it the bytes
/// they received and route the result to the next-hop transport (Phase 5 wiring). Keeping the
/// processor transport-agnostic lets one mediator service multiplex HTTP, WebSocket, or
/// any custom Phase 5 transport without re-implementing FR-ROUTE-05 each time.
/// </para>
/// <para>
/// FR-ROUTE-06 rewrap mode (constant-size onion) is enabled by setting
/// <see cref="ForwardProcessorOptions.Mode"/> to <see cref="RewrapMode.ReanoncryptToNext"/>.
/// In that mode the processor re-anoncrypts the inner attachment for the next-hop DID before
/// surfacing it on <see cref="ForwardProcessingResult.OnwardPacked"/>; the consumer's
/// transport sends the result to <see cref="ForwardProcessingResult.NextHop"/> as usual.
/// </para>
/// </remarks>
public sealed class ForwardProcessor
{
    private readonly DidCommClient _client;
    private readonly IDidKeyServiceForFacadeOnlyAlias _keyService; // alias so the public ctor reads cleanly.
    private readonly DefaultCryptoProvider _cryptoProvider;
    private readonly ForwardProcessorOptions _options;

    /// <summary>Initialise the processor.</summary>
    /// <param name="client">The mediator's <see cref="DidCommClient"/> — must be configured with the mediator's own <c>ISecretsResolver</c> so it can decrypt forwards addressed to mediator keys.</param>
    /// <param name="keyService">Used for rewrap-mode next-hop key lookups. Optional only when <see cref="ForwardProcessorOptions.Mode"/> is <see cref="RewrapMode.PassThrough"/> with no extra recipient routing keys.</param>
    /// <param name="options">Per-mediator processing options.</param>
    public ForwardProcessor(
        DidCommClient client,
        Resolution.IDidKeyService keyService,
        ForwardProcessorOptions options)
        : this(client, keyService, options, new DefaultCryptoProvider()) { }

    /// <summary>Test-only constructor that accepts a shared crypto provider.</summary>
    internal ForwardProcessor(
        DidCommClient client,
        Resolution.IDidKeyService keyService,
        ForwardProcessorOptions options,
        DefaultCryptoProvider cryptoProvider)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(keyService);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cryptoProvider);
        options.Validate();
        _client = client;
        _keyService = new KeyServiceAdapter(keyService);
        _options = options;
        _cryptoProvider = cryptoProvider;
    }

    /// <summary>
    /// Process one inbound forward envelope. Throws <see cref="ConsistencyException"/> when
    /// <paramref name="packed"/> unwraps to a non-forward message.
    /// </summary>
    /// <param name="packed">The packed envelope as received from the inbound transport.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ForwardProcessingResult> ProcessAsync(string packed, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packed);

        var unpack = await _client.UnpackAsync(packed, ct).ConfigureAwait(false);

        if (!ForwardMessage.TryParse(unpack.Message, out var next, out var attachments))
        {
            throw new ConsistencyException(
                $"ForwardProcessor.ProcessAsync received a non-forward message (type='{unpack.Message.Type}'). The mediator role only handles {ForwardConstants.ForwardTypeUri}.");
        }

        // FR-ROUTE-07: please_ack on a forward is silently ignored at the mediator. We log
        // nothing in core (no logger dependency in Phase 4); the contract is that the
        // processor does NOT propagate an ack request onward. This is achieved trivially by
        // never reading `please_ack` here and never adding one to the rewrap envelope.

        var innerPayloadBytes = ExtractAttachmentBytes(attachments[0]);
        var expiresTime = unpack.Message.ExpiresTime;
        var delay = ExtractDelay(unpack.Message);

        if (_options.Mode == RewrapMode.ReanoncryptToNext)
        {
            var rewrapped = await RewrapToNextAsync(innerPayloadBytes, next, ct).ConfigureAwait(false);
            return new ForwardProcessingResult(next, rewrapped, expiresTime, delay);
        }

        if (_options.ExtraRecipientRoutingKeys is { Count: > 0 } extra)
        {
            var wrapped = await WrapForExtraKeysAsync(innerPayloadBytes, next, extra, ct).ConfigureAwait(false);
            return new ForwardProcessingResult(next, wrapped, expiresTime, delay);
        }

        return new ForwardProcessingResult(next, innerPayloadBytes, expiresTime, delay);
    }

    private static byte[] ExtractAttachmentBytes(Messages.Attachment attachment)
    {
        if (attachment.Data.Json is JsonNode json)
            return Encoding.UTF8.GetBytes(json.ToJsonString());

        if (!string.IsNullOrEmpty(attachment.Data.Base64))
            return Convert.FromBase64String(attachment.Data.Base64);

        throw new ConsistencyException(
            "Forward attachment is missing both 'data.json' and 'data.base64'. The mediator has nothing to relay.");
    }

    private static TimeSpan? ExtractDelay(Messages.Message message)
    {
        if (message.AdditionalHeaders is null) return null;
        if (!message.AdditionalHeaders.TryGetValue("delay_milli", out var element)) return null;
        if (element.ValueKind != System.Text.Json.JsonValueKind.Number) return null;
        if (!element.TryGetInt64(out var millis)) return null;

        // Spec: negative value → randomize between 0 and |n|, uniform.
        if (millis < 0)
        {
            var bound = Math.Abs(millis);
            var sample = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, (int)Math.Min(bound + 1, int.MaxValue));
            return TimeSpan.FromMilliseconds(sample);
        }
        return TimeSpan.FromMilliseconds(millis);
    }

    private async Task<byte[]> RewrapToNextAsync(byte[] innerPacked, string nextHop, CancellationToken ct)
    {
        // FR-ROUTE-06 rewrap: build a fresh forward addressed to `next`, with the inner
        // payload as its single attachment, then anoncrypt the forward to `next`'s
        // keyAgreement key(s).
        var freshForward = ForwardMessage.Create(
            mediator: nextHop, next: nextHop,
            packedPayloads: new[] { Encoding.UTF8.GetString(innerPacked) });

        var nextKeys = await _keyService.GetVerificationMethodsAsync(nextHop, Resolution.VerificationRelationship.KeyAgreement, ct).ConfigureAwait(false);
        if (nextKeys.Count == 0)
            throw new DidResolutionException(nextHop, "rewrap target has no keyAgreement keys");

        var wrapped = Composition.EnvelopeWriter.PackEncrypted(
            new Composition.PackEncryptedParameters(
                Message: freshForward,
                Recipients: PickSameCurveKeys(nextKeys),
                ContentEncryption: JoseAlgorithms.A256CbcHs512),
            _cryptoProvider);
        return Encoding.UTF8.GetBytes(wrapped);
    }

    private async Task<byte[]> WrapForExtraKeysAsync(
        byte[] innerPacked,
        string nextHop,
        IReadOnlyList<Jwk> extraRoutingKeys,
        CancellationToken ct)
    {
        // FR-ROUTE-05 second paragraph: extra routing keys configured out-of-band trigger
        // additional forward wraps between mediator hops. Use the same outer-to-inner
        // wrapping logic as the sender (ForwardWrapper) by composing one forward per key in
        // reverse order. The mediator does NOT prepend any of its own keys here — it only
        // adds the recipient's pre-configured extras.
        await Task.CompletedTask.ConfigureAwait(false); // ConfigureAwait pattern; method is async for ct alignment with siblings.
        ct.ThrowIfCancellationRequested();
        var wrapped = Composition.ForwardWrapper.Wrap(
            innerPackedPayload: Encoding.UTF8.GetString(innerPacked),
            routingKeyJwksOuterToInner: extraRoutingKeys,
            finalRecipientDid: nextHop,
            cryptoProvider: _cryptoProvider);
        return Encoding.UTF8.GetBytes(wrapped);
    }

    private static IReadOnlyList<Jwk> PickSameCurveKeys(IReadOnlyList<Jwk> keys)
    {
        // Match the preference order DidCommClient uses for plain anoncrypt: take all keys on
        // the highest-preference curve present. For rewrap targets that publish a single
        // curve this collapses to the full list.
        var preference = new[]
        {
            JoseAlgorithms.CrvX25519, JoseAlgorithms.CrvP256,
            JoseAlgorithms.CrvP384,  JoseAlgorithms.CrvP521,
        };
        foreach (var crv in preference)
        {
            var matches = keys.Where(k => string.Equals(k.Crv, crv, StringComparison.Ordinal)).ToArray();
            if (matches.Length > 0) return matches;
        }
        return keys;
    }

    // Small adapter so the public ctor talks the public IDidKeyService contract while
    // internal code keeps using the same type-safe shape. Lets us reuse the resolver the
    // facade was constructed with without a second registration.
    private interface IDidKeyServiceForFacadeOnlyAlias
    {
        Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, Resolution.VerificationRelationship relationship, CancellationToken ct);
    }

    private sealed class KeyServiceAdapter : IDidKeyServiceForFacadeOnlyAlias
    {
        private readonly Resolution.IDidKeyService _inner;
        public KeyServiceAdapter(Resolution.IDidKeyService inner) => _inner = inner;
        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, Resolution.VerificationRelationship relationship, CancellationToken ct)
            => _inner.GetVerificationMethodsAsync(did, relationship, ct);
    }
}
