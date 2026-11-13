using System.Text;
using System.Text.Json.Nodes;
using DidComm.Consistency;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Jose;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Protocols.Routing;

/// <summary>
/// Mediator-side <c>forward</c> processing (PRD §8 / FR-ROUTE-05/06/07/10/11). A pure
/// transformation: takes the <see cref="UnpackResult"/> of an inbound forward, validates that it
/// is a forward the mediator may relay, drops <c>please_ack</c> (FR-ROUTE-07), and produces a
/// <see cref="ForwardProcessingResult"/> describing where the onward bytes should go.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Inputs.</strong> The primary entry point is
/// <see cref="ProcessAsync(UnpackResult, CancellationToken)"/>, fed with the host's own unpack of
/// the inbound envelope (the ASP.NET Core endpoint and the <see cref="ProtocolDispatcher"/> both
/// have one). The <see cref="ProcessAsync(string, CancellationToken)"/> overload is a convenience
/// for hosts that hold the packed bytes: it unpacks with the mediator's
/// <see cref="DidCommClient"/> and continues on the same path.
/// </para>
/// <para>
/// <strong>Posture (FR-ROUTE-11).</strong> A forward is relayed only when its outer envelope was
/// encrypted and decrypted with a key this mediator holds: <see cref="UnpackResult.Encrypted"/>
/// and a non-empty <see cref="UnpackResult.RecipientKid"/>. A plaintext or signed-only forward,
/// or a synthetic result without a recipient kid, is refused with <see cref="ConsistencyException"/>.
/// The checks run in a fixed order: forward parse (a malformed or over-long <c>next</c> throws
/// <see cref="MalformedMessageException"/> first, FR-API-09), posture, attachment count,
/// attachment decode, then the transformation.
/// </para>
/// <para>
/// <strong>Transformations.</strong> Pass-through relays the attachment verbatim unless the
/// <see cref="IRecipientRoutingKeyProvider"/> returns extra routing keys for the DID subject of
/// <c>next</c>, in which case the payload is wrapped once per key (FR-ROUTE-05 second paragraph).
/// FR-ROUTE-06 rewrap mode (<see cref="ForwardProcessorOptions.Mode"/> =
/// <see cref="RewrapMode.ReanoncryptToNext"/>) re-anoncrypts the attachment for the next hop and
/// never consults the provider. The processor performs no transport I/O.
/// </para>
/// <para>
/// <strong>Scheduling hints.</strong> <c>expires_time</c> is propagated; <c>delay_milli</c> is
/// resolved to <see cref="ForwardProcessingResult.Delay"/> and, on the processor's
/// <see cref="DidCommOptions"/> clock, to the absolute <see cref="ForwardProcessingResult.NotBefore"/>
/// (FR-ROUTE-10). The hold honoured is at most <see cref="ForwardProcessorOptions.MaxDelay"/>: a
/// longer positive hint is shortened to it, and for a negative hint the magnitude is limited to it
/// before the one random draw. The public <c>ProcessAsync</c> overloads resolve the schedule when
/// they are called — hosts such as <c>samples/04-MediatorAgent</c> call them at receipt, so that is
/// the receipt time. <see cref="ForwardHandler"/> resolves it through the same processor, inline at
/// admission and before any queueing, and hands the fixed schedule to the internal overload, so a
/// forward that waits behind a slow sink keeps the not-before it was given on arrival. The
/// three-argument constructor uses the system clock.
/// </para>
/// </remarks>
/// <example>
/// The mediator role — unwrap a forward, emit the onward payload — runs in <c>samples/04-MediatorAgent</c> and <c>samples/02-Cookbook</c> (section O).
/// </example>
public sealed class ForwardProcessor
{
    private readonly DidCommClient _client;
    private readonly IDidKeyServiceForFacadeOnlyAlias _keyService; // alias so the public ctor reads cleanly.
    private readonly JoseCryptoProvider _cryptoProvider;
    private readonly ForwardProcessorOptions _options;
    private readonly TimeSpan? _maxDelay;
    private readonly IRecipientRoutingKeyProvider _routingKeys;
    private readonly DidCommOptions _didCommOptions;

    /// <summary>
    /// Initialise the processor with the routing keys from <see cref="ForwardProcessorOptions.ExtraRecipientRoutingKeys"/>
    /// (applied to every recipient) and the system clock for <see cref="ForwardProcessingResult.NotBefore"/>.
    /// </summary>
    /// <param name="client">The mediator's <see cref="DidCommClient"/> — must be configured with the mediator's own <c>ISecretsResolver</c> so it can decrypt forwards addressed to mediator keys.</param>
    /// <param name="keyService">Used for rewrap-mode next-hop key lookups. Optional only when <see cref="ForwardProcessorOptions.Mode"/> is <see cref="RewrapMode.PassThrough"/> with no extra recipient routing keys.</param>
    /// <param name="options">Per-mediator processing options.</param>
    public ForwardProcessor(
        DidCommClient client,
        Resolution.IDidKeyService keyService,
        ForwardProcessorOptions options)
        : this(client, keyService, options, new StaticRecipientRoutingKeyProvider(options?.ExtraRecipientRoutingKeys), new DidCommOptions(), new JoseCryptoProvider()) { }

    /// <summary>Test-only constructor that accepts a shared crypto provider.</summary>
    internal ForwardProcessor(
        DidCommClient client,
        Resolution.IDidKeyService keyService,
        ForwardProcessorOptions options,
        JoseCryptoProvider cryptoProvider)
        : this(client, keyService, options, new StaticRecipientRoutingKeyProvider(options?.ExtraRecipientRoutingKeys), new DidCommOptions(), cryptoProvider) { }

    /// <summary>
    /// Initialise the processor with a per-recipient routing-key provider and the clock that stamps
    /// <see cref="ForwardProcessingResult.NotBefore"/>. This is the constructor <c>AddRoutingMediator</c> uses.
    /// </summary>
    /// <param name="client">The mediator's <see cref="DidCommClient"/> — must be configured with the mediator's own <c>ISecretsResolver</c> so it can decrypt forwards addressed to mediator keys.</param>
    /// <param name="keyService">Used for rewrap-mode next-hop key lookups.</param>
    /// <param name="options">Per-mediator processing options. <see cref="ForwardProcessorOptions.ExtraRecipientRoutingKeys"/> is NOT read on this path — <paramref name="routingKeys"/> is the source of extra wraps.</param>
    /// <param name="routingKeys">Per-recipient extra routing keys, consulted in pass-through mode only.</param>
    /// <param name="didCommOptions">The options whose <see cref="DidCommOptions.Clock"/> stamps <see cref="ForwardProcessingResult.NotBefore"/>.</param>
    public ForwardProcessor(
        DidCommClient client,
        Resolution.IDidKeyService keyService,
        ForwardProcessorOptions options,
        IRecipientRoutingKeyProvider routingKeys,
        DidCommOptions didCommOptions)
        : this(client, keyService, options, routingKeys, didCommOptions, new JoseCryptoProvider()) { }

    /// <summary>Master constructor every other overload delegates to.</summary>
    /// <param name="client">The mediator's facade.</param>
    /// <param name="keyService">Rewrap-mode key lookups.</param>
    /// <param name="options">Per-mediator processing options; validated here.</param>
    /// <param name="routingKeys">Per-recipient extra routing keys.</param>
    /// <param name="didCommOptions">Clock source for <see cref="ForwardProcessingResult.NotBefore"/>.</param>
    /// <param name="cryptoProvider">The JOSE crypto provider for rewrap and extra-key wraps.</param>
    internal ForwardProcessor(
        DidCommClient client,
        Resolution.IDidKeyService keyService,
        ForwardProcessorOptions options,
        IRecipientRoutingKeyProvider routingKeys,
        DidCommOptions didCommOptions,
        JoseCryptoProvider cryptoProvider)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(keyService);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(routingKeys);
        ArgumentNullException.ThrowIfNull(didCommOptions);
        ArgumentNullException.ThrowIfNull(cryptoProvider);
        options.Validate();
        _client = client;
        _keyService = new KeyServiceAdapter(keyService);
        _options = options;
        _maxDelay = options.MaxDelay; // validated above; read once (L-060)
        _routingKeys = routingKeys;
        _didCommOptions = didCommOptions;
        _cryptoProvider = cryptoProvider;
    }

    /// <summary>
    /// Convenience overload: unpack <paramref name="packed"/> with the mediator's
    /// <see cref="DidCommClient"/>, then process it exactly as
    /// <see cref="ProcessAsync(UnpackResult, CancellationToken)"/> does. A forward that arrives as
    /// plaintext or signed-only therefore unpacks fine and is then refused with
    /// <see cref="ConsistencyException"/> (FR-ROUTE-11).
    /// </summary>
    /// <param name="packed">The packed envelope as received from the inbound transport.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ForwardProcessingResult> ProcessAsync(string packed, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packed);

        var unpacked = await _client.UnpackAsync(packed, ct).ConfigureAwait(false);
        return await ProcessAsync(unpacked, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Process one inbound forward from the host's own unpack of it. Validation order: forward
    /// parse, encryption posture, attachment count, attachment decode, transformation. The
    /// <c>delay_milli</c> schedule (<see cref="ForwardProcessingResult.Delay"/> /
    /// <see cref="ForwardProcessingResult.NotBefore"/>) is resolved on this call, on the
    /// processor's clock — call it at receipt, as the hosts that use it directly do.
    /// </summary>
    /// <param name="unpacked">The unpack outcome of the inbound envelope — envelope metadata included, so the FR-ROUTE-11 posture can be judged.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ConsistencyException">The message is not a <c>forward</c>; or the envelope was not encrypted (plaintext or signed-only); or it was not decrypted with a key this mediator holds (no <see cref="UnpackResult.RecipientKid"/>); or the forward carries more than one attachment or an attachment with no data (FR-ROUTE-05/11). None of these messages quote wire values.</exception>
    /// <exception cref="MalformedMessageException">The forward violates FR-ROUTE-01 (missing <c>next</c> or attachments), its <c>next</c> exceeds the FR-API-09 bounds, its attachment's <c>data.base64</c> is not base64 or decodes to no bytes, or — in rewrap mode or with extra routing keys, where the payload is re-embedded in a fresh forward — the payload is not a JSON document or <c>next</c> has no DID part. Pass-through with no extra keys relays the bytes opaquely and never parses them.</exception>
    /// <exception cref="DidResolutionException">Rewrap mode only: the next hop resolves to no usable <c>keyAgreement</c> key.</exception>
    /// <exception cref="CryptoException">Rewrap mode or extra routing keys: the onward envelope could not be built.</exception>
    public Task<ForwardProcessingResult> ProcessAsync(UnpackResult unpacked, CancellationToken ct = default)
        => ProcessCoreAsync(unpacked, schedule: null, ct);

    /// <summary>
    /// Process one inbound forward with a schedule the caller already resolved. Used by
    /// <see cref="ForwardHandler"/>, which reads the clock and samples <c>delay_milli</c> once,
    /// inline at admission, so the time a forward spends queued never moves its
    /// <see cref="ForwardProcessingResult.NotBefore"/>. Validation and transformation are those
    /// of <see cref="ProcessAsync(UnpackResult, CancellationToken)"/>; only the schedule differs.
    /// </summary>
    /// <param name="unpacked">The unpack outcome of the inbound envelope.</param>
    /// <param name="delay">The resolved <c>delay_milli</c> hint (<see cref="ResolveDelay"/>), or <c>null</c> when absent.</param>
    /// <param name="notBefore">Receipt time plus <paramref name="delay"/>, or <c>null</c> when there is no delay.</param>
    /// <param name="ct">Cancellation token.</param>
    internal Task<ForwardProcessingResult> ProcessAsync(UnpackResult unpacked, TimeSpan? delay, DateTimeOffset? notBefore, CancellationToken ct)
        => ProcessCoreAsync(unpacked, (delay, notBefore), ct);

    private async Task<ForwardProcessingResult> ProcessCoreAsync(
        UnpackResult unpacked,
        (TimeSpan? Delay, DateTimeOffset? NotBefore)? schedule,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(unpacked);

        // 1. Is it a forward at all? TryParse also enforces FR-ROUTE-01 / FR-API-09 (throws
        //    MalformedMessageException) before anything else, so the cap is checked before the
        //    posture and no over-long value is ever touched again. Do not echo the inbound type.
        if (!ForwardMessage.TryParse(unpacked.Message, out var next, out var attachments))
        {
            throw new ConsistencyException(
                $"ForwardProcessor received a non-forward message; the mediator role only handles {ForwardConstants.ForwardTypeUri}.");
        }

        // 2./3. FR-ROUTE-11 posture: relay only what was encrypted to a key this mediator holds. A
        //       decrypt with a non-owned kid cannot produce an UnpackResult, so a non-empty
        //       RecipientKid is the proof of a held-key decrypt. Never put the kid in the message.
        if (!unpacked.Encrypted)
            throw new ConsistencyException(
                "Forward was not encrypted (plaintext or signed-only); a mediator relays only forwards decrypted with a held key (FR-ROUTE-11).");

        if (string.IsNullOrEmpty(unpacked.RecipientKid))
            throw new ConsistencyException(
                "Forward was not decrypted with a key this mediator holds (FR-ROUTE-11).");

        // FR-ROUTE-07: please_ack on a forward is silently ignored at the mediator. The contract is
        // that the processor does NOT propagate an ack request onward — achieved trivially by never
        // reading `please_ack` here and never adding one to the rewrap envelope.

        if (attachments.Count > 1)
            throw new ConsistencyException(
                $"Forward message carries {attachments.Count} attachments; this mediator relays exactly one packed payload per forward (FR-ROUTE-05). Re-send each payload in its own forward.");

        var innerPayloadBytes = ExtractAttachmentBytes(attachments[0]);
        var expiresTime = unpacked.Message.ExpiresTime;
        // FR-ROUTE-10: resolve the hint to an absolute not-before once, on the mediator's clock —
        // here when the public overload is the receipt point, or earlier by the handler, which
        // passes the schedule it fixed at admission. Nothing ever sleeps on it.
        var (delay, notBefore) = schedule ?? ResolveSchedule(unpacked.Message);

        if (_options.Mode == RewrapMode.ReanoncryptToNext)
        {
            var rewrapped = await ReEmbedAsync(() => RewrapToNextAsync(innerPayloadBytes, next, ct)).ConfigureAwait(false);
            return new ForwardProcessingResult(next, rewrapped, expiresTime, delay) { NotBefore = notBefore };
        }

        // Pass-through: extra routing keys are per recipient, keyed by the DID subject of `next`.
        // A `next` that is not a DID/DID URL (within the FR-API-09 cap) skips the lookup and passes
        // through verbatim — the handler drops such forwards before the processor ever runs.
        var subject = DidSubject.DidSubjectOf(next);
        IReadOnlyList<Jwk> extra = subject is null
            ? Array.Empty<Jwk>()
            : await _routingKeys.GetAsync(subject, ct).ConfigureAwait(false);
        if (extra.Count > 0)
        {
            var wrapped = await ReEmbedAsync(() => WrapForExtraKeysAsync(innerPayloadBytes, next, extra, ct)).ConfigureAwait(false);
            return new ForwardProcessingResult(next, wrapped, expiresTime, delay) { NotBefore = notBefore };
        }

        return new ForwardProcessingResult(next, innerPayloadBytes, expiresTime, delay) { NotBefore = notBefore };
    }

    /// <summary>
    /// Open a forward addressed to this mediator itself — the receiver side of rewrapping
    /// (FR-ROUTE-06; DIDComm v2.1 §Routing "Rewrapping": "the outer envelope is a forward message
    /// where to is the receiver itself"): take its single attachment exactly as it is (no rewrap, no
    /// extra routing keys) and unpack it with the mediator's own <see cref="DidCommClient"/>, which
    /// holds the keys forwards to this mediator are encrypted to. The attachment rules are those of
    /// <see cref="ProcessAsync(UnpackResult, CancellationToken)"/>: one attachment, decodable, not
    /// empty. The unpack applies every receive rule (size, FR-API-05 expiry, consistency).
    /// </summary>
    /// <param name="selfAddressed">The forward whose <c>next</c> is the DID that decrypted it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ConsistencyException">Not a forward, more than one attachment, or an attachment with no data.</exception>
    /// <exception cref="MalformedMessageException">The attachment is not valid base64url or decodes to no bytes; or the unpack refuses what is inside.</exception>
    /// <exception cref="DidCommException">Any other refusal of the unpack (it cannot be decrypted with a held key, a key cannot be resolved, ...).</exception>
    internal async Task<UnpackResult> PeelAsync(UnpackResult selfAddressed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(selfAddressed);
        if (!ForwardMessage.TryParse(selfAddressed.Message, out _, out var attachments))
        {
            throw new ConsistencyException(
                $"Only a {ForwardConstants.ForwardTypeUri} addressed to this mediator can be opened.");
        }
        if (attachments.Count > 1)
        {
            throw new ConsistencyException(
                $"Forward message carries {attachments.Count} attachments; a forward addressed to this mediator must carry exactly one packed message (FR-ROUTE-05).");
        }
        var inner = ExtractAttachmentBytes(attachments[0]);
        return await _client.UnpackAsync(Encoding.UTF8.GetString(inner), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Run a transformation that re-embeds the onward payload in a fresh forward. The forward
    /// builder parses the payload as JSON and requires a non-empty <c>to</c>, and it reports both
    /// as <see cref="ArgumentException"/> — the right type for its own callers, but here both inputs
    /// came off the wire (the attachment bytes; <c>next</c> reduced to its DID part), so a peer's
    /// violation surfaces as the documented malformed-input type and never as a raw argument fault.
    /// </summary>
    /// <param name="transform">The rewrap or extra-key wrap to run.</param>
    private static async Task<byte[]> ReEmbedAsync(Func<Task<byte[]>> transform)
    {
        try
        {
            return await transform().ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new MalformedMessageException(
                "Forward payload cannot be re-wrapped for the next hop: the attachment is not a JSON-serialized envelope, or 'next' has no DID part (FR-ROUTE-01).", ex);
        }
    }

    private static byte[] ExtractAttachmentBytes(Messages.Attachment attachment)
    {
        if (attachment.Data.Json is JsonNode json)
            return Encoding.UTF8.GetBytes(json.ToJsonString());

        if (!string.IsNullOrEmpty(attachment.Data.Base64))
        {
            byte[] decoded;
            try
            {
                // Relaxed decode: attachment data.base64 has no spec no-pad requirement (FR-ATT-02,
                // cf. Aries RFC 0017), and a mediator only relays the bytes for the recipient to
                // re-parse strictly — so tolerate '=' padding rather than refuse a peer's forward.
                // Genuinely-invalid base64 still maps to the documented malformed-input type (a raw
                // FormatException must not escape ProcessAsync). (#24 + PR #38 review.)
                decoded = Base64Url.DecodeRelaxed(attachment.Data.Base64);
            }
            catch (FormatException ex)
            {
                throw new MalformedMessageException(
                    "Forward attachment 'data.base64' is not valid base64url.", ex);
            }
            // White space decodes to nothing: there is no packed message to relay, and an empty
            // payload is not one (#95 Q-7). The sender's mistake, not the mediator's fault.
            if (decoded.Length == 0)
                throw new MalformedMessageException("Forward attachment 'data.base64' decodes to no bytes; there is no packed message to relay.");
            return decoded;
        }

        throw new ConsistencyException(
            "Forward attachment is missing both 'data.json' and 'data.base64'. The mediator has nothing to relay.");
    }

    private (TimeSpan? Delay, DateTimeOffset? NotBefore) ResolveSchedule(Messages.Message message)
    {
        var delay = ResolveDelay(message);
        return (delay, delay is null ? null : NotBeforeOf(_didCommOptions.Now(), delay));
    }

    /// <summary>
    /// The absolute not-before of a hold (FR-ROUTE-10): <paramref name="receivedAt"/> plus
    /// <paramref name="delay"/>, pinned to <see cref="DateTimeOffset.MaxValue"/> when the sum would
    /// run past the end of time (a clock set close to it), so it never throws; <c>null</c> when
    /// there is no hold. The one sum the handler and the public overloads use.
    /// </summary>
    /// <param name="receivedAt">The receipt reading of the mediator's clock.</param>
    /// <param name="delay">The resolved hold (never negative), or <c>null</c>.</param>
    internal static DateTimeOffset? NotBeforeOf(DateTimeOffset receivedAt, TimeSpan? delay)
    {
        if (delay is not { } hold)
            return null;
        // Both the UTC instant and the clock reading in receivedAt's own offset must stay
        // representable; the larger of the two leaves the least room.
        var room = DateTime.MaxValue.Ticks - Math.Max(receivedAt.UtcTicks, receivedAt.Ticks);
        return hold.Ticks > room ? DateTimeOffset.MaxValue : receivedAt + hold;
    }

    /// <summary>
    /// Resolve the forward's <c>delay_milli</c> header to the hold this processor honours: the one
    /// draw, under <see cref="ForwardProcessorOptions.MaxDelay"/> (see
    /// <see cref="ExtractDelay(Messages.Message, TimeSpan?)"/>). <see cref="ForwardHandler"/> calls
    /// it once per forward, inline at admission; the public overloads call it on the request they
    /// serve. Never throws.
    /// </summary>
    /// <param name="message">The inbound forward.</param>
    internal TimeSpan? ResolveDelay(Messages.Message message) => ExtractDelay(message, _maxDelay);

    /// <summary>
    /// Resolve the forward's <c>delay_milli</c> header to a hold (FR-ROUTE-10): absent, non-integer
    /// or non-numeric → <c>null</c>. The cap is <paramref name="maxDelay"/> in whole milliseconds
    /// (floored, within <c>[0, int.MaxValue]</c>), or <see cref="int.MaxValue"/> milliseconds when it
    /// is <c>null</c>. A non-negative value is held for <c>min(n, cap)</c>. A negative value asks for
    /// a random hold of up to <c>|n|</c>: <c>|n|</c> is limited to the cap first, and only then is the
    /// one uniform draw in <c>[0, min(|n|, cap)]</c> made — clamping after the draw would put most
    /// of the probability exactly on the cap. All arithmetic is in <see cref="long"/>; never throws.
    /// </summary>
    /// <param name="message">The inbound forward.</param>
    /// <param name="maxDelay">The longest hold honoured; <c>null</c> for the library bound of <see cref="int.MaxValue"/> milliseconds.</param>
    internal static TimeSpan? ExtractDelay(Messages.Message message, TimeSpan? maxDelay)
    {
        if (message.AdditionalHeaders is null) return null;
        if (!message.AdditionalHeaders.TryGetValue("delay_milli", out var element)) return null;
        if (element.ValueKind != System.Text.Json.JsonValueKind.Number) return null;
        if (!element.TryGetInt64(out var millis)) return null;

        // The cap in whole milliseconds. Integer division floors a positive TimeSpan; a negative one
        // (refused by Validate, but this method is static) clamps to zero. int.MaxValue ms (~24.8
        // days) is the library's hard bound: a hostile hint past ~2^53 ms overflows TimeSpan, and one
        // past the year 9999 overflows the NotBefore addition — neither may throw out of here.
        long capMs = maxDelay is { } cap
            ? Math.Clamp(cap.Ticks / TimeSpan.TicksPerMillisecond, 0L, int.MaxValue)
            : int.MaxValue;

        if (millis < 0)
        {
            // Spec: a negative value is randomized between 0 and |n|, uniform. long.MinValue has no
            // positive magnitude. Clamp |n| to the cap BEFORE the draw, so the draw is uniform over
            // the range actually honoured; int.MaxValue - 1 keeps the exclusive upper bound in range.
            var magnitude = millis == long.MinValue ? long.MaxValue : -millis;
            var inclusiveMax = Math.Min(Math.Min(magnitude, capMs), int.MaxValue - 1L);
            var sample = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, (int)inclusiveMax + 1);
            return TimeSpan.FromMilliseconds(sample);
        }

        return TimeSpan.FromMilliseconds(Math.Min(millis, capMs));
    }

    private async Task<byte[]> RewrapToNextAsync(byte[] innerPacked, string nextHop, CancellationToken ct)
    {
        // FR-ROUTE-06 rewrap (DIDComm v2.1 §Routing "Rewrapping"): build a fresh forward whose
        // attachment is the inbound payload, then anoncrypt it to the next hop. The fresh forward is
        // addressed to the next hop itself — to == next == the next hop — so the next hop receives a
        // double-packaged message: it opens this layer and handles what is inside, as its own
        // message when it is the final recipient, or as a forward to relay when it is a mediator (a
        // didcomm-dotnet mediator's ForwardHandler peels such a self-addressed layer). This mediator
        // stays uncommitted to which the next hop is, and the onion keeps its size. `next` can arrive as a DID
        // URL — a fragment (outer onion layers carry the routing-key kid), a path or a query — so it
        // is reduced to its DID subject (PRD §4.3), the DID the handler found registered, for the
        // message headers and the keyAgreement lookup. A `next` with no DID part is malformed.
        var nextDid = DidSubject.DidSubjectOf(nextHop)
            ?? throw new MalformedMessageException("Forward 'next' has no DID part; it cannot be rewrapped for the next hop (FR-ROUTE-01).");
        var freshForward = ForwardMessage.Create(
            mediator: nextDid, next: nextDid,
            packedPayloads: new[] { Encoding.UTF8.GetString(innerPacked) });

        var nextKeys = await _keyService.GetVerificationMethodsAsync(nextDid, Resolution.VerificationRelationship.KeyAgreement, ct).ConfigureAwait(false);
        if (nextKeys.Count == 0)
            throw new DidResolutionException(nextDid, "rewrap target has no keyAgreement keys");

        // Every keyAgreement key of the next hop on its preferred curve, the same selection a sender
        // makes (FR-ENC-19): keys without a kid, or whose kid names another DID, are skipped, each
        // kid once, at most RecipientKeySelection.MaxKeysPerDid. A target with no key on a supported curve is refused
        // here rather than handed to the envelope writer as a mixed-curve list.
        var wrapped = await Composition.EnvelopeWriter.PackEncryptedAsync(
            new Composition.PackEncryptedParameters(
                Message: freshForward,
                Recipients: Composition.RecipientKeySelection.AllOnPreferredCurve(nextDid, nextKeys, "rewrap target"),
                ContentEncryption: JoseAlgorithms.A256CbcHs512),
            _cryptoProvider,
            ct).ConfigureAwait(false);
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
        var wrapped = await Composition.ForwardWrapper.WrapAsync(
            innerPackedPayload: Encoding.UTF8.GetString(innerPacked),
            routingKeyJwksOuterToInner: extraRoutingKeys,
            finalRecipientDid: nextHop,
            cryptoProvider: _cryptoProvider,
            ct: ct).ConfigureAwait(false);
        return Encoding.UTF8.GetBytes(wrapped);
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
