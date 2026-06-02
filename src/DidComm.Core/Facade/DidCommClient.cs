using DidComm.Composition;
using DidComm.Crypto;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols.Rotation;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Transports;

namespace DidComm.Facade;

/// <summary>
/// The public DIDComm v2.1 Pack/Unpack surface (FR-API-01..08). Resolves DID strings via the
/// registered <see cref="IDidKeyService"/>, pulls private keys via <see cref="ISecretsResolver"/>,
/// and delegates the JOSE composition to the internal envelope layer. Thread-safe (NFR-03) —
/// register as a singleton.
/// </summary>
public sealed class DidCommClient
{
    private readonly ISecretsResolver _secrets;
    private readonly IDidKeyService _keyService;
    private readonly IServiceEndpointResolver? _serviceResolver;
    private readonly ITransportRouter? _transportRouter;
    private readonly DidCommOptions _options;
    private readonly DefaultCryptoProvider _cryptoProvider;

    /// <summary>Initialize the facade. Routing (FR-ROUTE-*) is unavailable without an <see cref="IServiceEndpointResolver"/>; for that, use the <see cref="DidCommClient(ISecretsResolver, IDidKeyService, IServiceEndpointResolver, DidCommOptions)"/> overload or register the facade through <c>AddDidComm</c>.</summary>
    /// <param name="secrets">Consumer-supplied private-key resolver (FR-SEC-01).</param>
    /// <param name="keyService">DID resolution + verification-method extraction (FR-DID-01..05).</param>
    /// <param name="options">Process-wide options (FR-API-05/06 knobs).</param>
    public DidCommClient(ISecretsResolver secrets, IDidKeyService keyService, DidCommOptions options)
        : this(secrets, keyService, serviceResolver: null, transportRouter: null, options, new DefaultCryptoProvider()) { }

    /// <summary>Initialize the facade with the Phase 4 routing surface enabled.</summary>
    /// <param name="secrets">Consumer-supplied private-key resolver (FR-SEC-01).</param>
    /// <param name="keyService">DID resolution + verification-method extraction (FR-DID-01..05).</param>
    /// <param name="serviceResolver">Routing-service resolver (FR-ROUTE-03/04). Required for <c>PackEncryptedAsync(..., Forward: true)</c>.</param>
    /// <param name="options">Process-wide options.</param>
    public DidCommClient(
        ISecretsResolver secrets,
        IDidKeyService keyService,
        IServiceEndpointResolver serviceResolver,
        DidCommOptions options)
        : this(secrets, keyService, serviceResolver, transportRouter: null, options, new DefaultCryptoProvider())
    {
        ArgumentNullException.ThrowIfNull(serviceResolver);
    }

    /// <summary>Initialize the facade with both routing and a transport router (Phase 5 — enables <see cref="SendAsync"/>).</summary>
    /// <param name="secrets">Consumer-supplied private-key resolver (FR-SEC-01).</param>
    /// <param name="keyService">DID resolution + verification-method extraction (FR-DID-01..05).</param>
    /// <param name="serviceResolver">Routing-service resolver (FR-ROUTE-03/04).</param>
    /// <param name="transportRouter">Transport router (FR-TRN-01). Required for <see cref="SendAsync"/>.</param>
    /// <param name="options">Process-wide options.</param>
    public DidCommClient(
        ISecretsResolver secrets,
        IDidKeyService keyService,
        IServiceEndpointResolver serviceResolver,
        ITransportRouter transportRouter,
        DidCommOptions options)
        : this(secrets, keyService, serviceResolver, transportRouter, options, new DefaultCryptoProvider())
    {
        ArgumentNullException.ThrowIfNull(serviceResolver);
        ArgumentNullException.ThrowIfNull(transportRouter);
    }

    /// <summary>Initialize the facade with a custom crypto provider; used by tests.</summary>
    internal DidCommClient(
        ISecretsResolver secrets,
        IDidKeyService keyService,
        IServiceEndpointResolver? serviceResolver,
        ITransportRouter? transportRouter,
        DidCommOptions options,
        DefaultCryptoProvider cryptoProvider)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(keyService);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cryptoProvider);
        _secrets = secrets;
        _keyService = keyService;
        _serviceResolver = serviceResolver;
        _transportRouter = transportRouter;
        _options = options;
        _cryptoProvider = cryptoProvider;
    }

    /// <summary>
    /// Pack <paramref name="message"/> as a plaintext DIDComm JWM
    /// (<c>application/didcomm-plain+json</c>). Refuses to emit when
    /// <see cref="Message.FromPrior"/> is set (FR-ROT-03: rotation messages MUST be encrypted).
    /// </summary>
    /// <param name="message">The plaintext message.</param>
    /// <param name="ct">Cancellation token (unused for the plaintext path — present for shape symmetry).</param>
    public Task<string> PackPlaintextAsync(Message message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        RejectFromPriorOnUnprotectedEnvelope(message, "PackPlaintextAsync");
        RejectUnsupportedDidsOnMessage(message);
        return Task.FromResult(EnvelopeWriter.PackPlaintext(message));
    }

    /// <summary>
    /// Pack <paramref name="message"/> as a signed DIDComm envelope (JWS over the deterministic
    /// canonical bytes of the inner plaintext). The library selects a private key authorized
    /// under <paramref name="signFrom"/>'s <c>authentication</c> relationship; if no such key
    /// is held by the registered <see cref="ISecretsResolver"/> a
    /// <see cref="SecretNotFoundException"/> is thrown.
    /// </summary>
    /// <param name="message">The plaintext message.</param>
    /// <param name="signFrom">The signer DID (no fragment).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string> PackSignedAsync(Message message, string signFrom, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(signFrom);
        RejectFromPriorOnUnprotectedEnvelope(message, "PackSignedAsync");
        _keyService.RejectUnsupportedMethod(signFrom);
        RejectUnsupportedDidsOnMessage(message);

        var signerPriv = await PickSignerPrivateKeyAsync(signFrom, ct).ConfigureAwait(false);
        var parameters = new PackSignedParameters(message, new[] { signerPriv });
        return EnvelopeWriter.PackSigned(parameters, _cryptoProvider);
    }

    /// <summary>
    /// Pack <paramref name="message"/> as an encrypted DIDComm envelope per
    /// <paramref name="options"/> — anoncrypt when <c>From</c> is null, authcrypt otherwise.
    /// Optional inner JWS via <c>SignFrom</c>; optional outer anoncrypt layer via
    /// <c>ProtectSender</c>. Enforces FR-ENC-09 (no GCM/XC20P for authcrypt). When
    /// <see cref="PackEncryptedOptions.Forward"/> is <c>true</c> the result additionally
    /// applies Routing Protocol 2.0 forward wrapping (FR-ROUTE-02) and surfaces the transport
    /// URI on <see cref="PackEncryptedResult.ServiceEndpoint"/>.
    /// </summary>
    /// <param name="message">The plaintext message.</param>
    /// <param name="options">Recipient list, sender/signer DIDs, content-encryption choice, optional forward toggle.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PackEncryptedResult> PackEncryptedAsync(Message message, PackEncryptedOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Recipients is null || options.Recipients.Count == 0)
            throw new ArgumentException("At least one recipient DID is required.", nameof(options));

        if (options.Forward)
        {
            // FR-ROUTE-02 (Phase 4): forward wrapping shape checks happen up front so the caller
            // gets a fast, deterministic error before any DID resolution / curve selection runs.
            if (options.Recipients.Count != 1)
            {
                throw new InvalidOperationException(
                    "Phase 4 supports forward wrapping for single-recipient packs only. Drop Forward = true or call PackEncryptedAsync once per recipient.");
            }
            if (_serviceResolver is null)
            {
                throw new InvalidOperationException(
                    "Forward = true requires an IServiceEndpointResolver. Use the (ISecretsResolver, IDidKeyService, IServiceEndpointResolver, DidCommOptions) constructor, or register the facade via AddDidComm + UseNetDidResolver.");
            }
        }

        foreach (var did in options.Recipients)
            _keyService.RejectUnsupportedMethod(did);
        if (options.From is not null)
            _keyService.RejectUnsupportedMethod(options.From);
        if (options.SignFrom is not null)
            _keyService.RejectUnsupportedMethod(options.SignFrom);
        RejectUnsupportedDidsOnMessage(message);

        var encJoseAlg = MapContentEncryption(options.Enc, isAuthcrypt: options.From is not null);

        var recipientPublics = new Dictionary<string, IReadOnlyList<Jwk>>(StringComparer.Ordinal);
        foreach (var did in options.Recipients)
        {
            var keys = await _keyService.GetVerificationMethodsAsync(did, VerificationRelationship.KeyAgreement, ct).ConfigureAwait(false);
            if (keys.Count == 0)
                throw new DidResolutionException(did, "no keyAgreement keys available");
            recipientPublics[did] = keys;
        }

        IReadOnlyList<Jwk> senderPublics = Array.Empty<Jwk>();
        if (options.From is not null)
        {
            var allSenderPubs = await _keyService.GetVerificationMethodsAsync(options.From, VerificationRelationship.KeyAgreement, ct).ConfigureAwait(false);
            if (allSenderPubs.Count == 0)
                throw new DidResolutionException(options.From, "no keyAgreement keys available for sender");

            // Filter to keys for which the registered ISecretsResolver actually holds the private half.
            var senderKids = allSenderPubs.Where(k => k.Kid is not null).Select(k => k.Kid!).ToArray();
            var heldKids = await _secrets.FindPresentAsync(senderKids, ct).ConfigureAwait(false);
            var heldKidSet = new HashSet<string>(heldKids, StringComparer.Ordinal);
            senderPublics = allSenderPubs.Where(k => k.Kid is not null && heldKidSet.Contains(k.Kid!)).ToArray();
            if (senderPublics.Count == 0)
                throw new SecretNotFoundException($"{options.From} (no held authcrypt sender private key for any of {allSenderPubs.Count} keyAgreement keys)");
        }

        var chosenCurve = PickCommonCurve(recipientPublics.Values, senderPublics, requireSender: options.From is not null);
        if (chosenCurve is null)
        {
            throw new InvalidOperationException(
                "No common keyAgreement curve across recipients" + (options.From is not null ? " and sender." : "."));
        }

        var chosenRecipients = new List<Jwk>(recipientPublics.Count);
        foreach (var did in options.Recipients)
        {
            chosenRecipients.Add(recipientPublics[did].First(k => k.Crv == chosenCurve));
        }

        Jwk? senderPrivateJwk = null;
        string? skid = null;
        if (options.From is not null)
        {
            var senderPubJwk = senderPublics.First(k => k.Crv == chosenCurve);
            skid = senderPubJwk.Kid;
            senderPrivateJwk = await _secrets.FindAsync(senderPubJwk.Kid!, ct).ConfigureAwait(false)
                ?? throw new SecretNotFoundException(senderPubJwk.Kid!);
        }

        IReadOnlyList<Jwk>? signerJwks = null;
        if (options.SignFrom is not null)
        {
            var signerPriv = await PickSignerPrivateKeyAsync(options.SignFrom, ct).ConfigureAwait(false);
            signerJwks = new[] { signerPriv };
        }

        var parameters = new PackEncryptedParameters(
            Message: message,
            Recipients: chosenRecipients,
            ContentEncryption: encJoseAlg,
            SenderPrivateJwk: senderPrivateJwk,
            Skid: skid,
            SignerPrivateJwks: signerJwks,
            ProtectSender: options.ProtectSender);

        var innerPacked = EnvelopeWriter.PackEncrypted(parameters, _cryptoProvider);

        if (!options.Forward)
            return new PackEncryptedResult(innerPacked, ServiceEndpoint: null, Array.Empty<string>());

        // FR-ROUTE-02 sender path: input-shape checks already validated up front. Resolve the
        // (single) recipient's DIDCommMessaging service, expand any mediator-as-DID endpoint
        // (FR-ROUTE-04), then wrap forward layers.
        var recipientDid = options.Recipients[0];
        var candidates = await _serviceResolver!.ResolveAsync(recipientDid, ct).ConfigureAwait(false);
        var route = await MediatorEndpointExpander.ExpandAsync(candidates, _serviceResolver, _keyService, recipientDid, ct).ConfigureAwait(false);

        if (route.RoutingKeyJwks.Count == 0)
        {
            // No routing keys → direct delivery; surface the endpoint URI but skip wrapping.
            return new PackEncryptedResult(innerPacked, route.TransportUri, route.FallbackUris);
        }

        var wrapped = ForwardWrapper.Wrap(innerPacked, route.RoutingKeyJwks, recipientDid, _cryptoProvider);
        return new PackEncryptedResult(wrapped, route.TransportUri, route.FallbackUris);
    }

    /// <summary>
    /// Pack <paramref name="message"/> with <see cref="PackEncryptedOptions.Forward"/> = <c>true</c>
    /// (unless <paramref name="options"/> overrides the endpoint), then deliver the packed bytes
    /// via the registered <see cref="ITransportRouter"/> (FR-TRN-01). Requires both a
    /// <see cref="IServiceEndpointResolver"/> and a <see cref="ITransportRouter"/> to be wired in
    /// (use <c>AddDidComm(b =&gt; b.UseNetDidResolver().UseHttpTransport()...)</c>).
    /// </summary>
    /// <param name="message">The plaintext message.</param>
    /// <param name="options">Recipient list, sender/signer DIDs, content-encryption choice, optional explicit endpoint override.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SendResult> SendAsync(Message message, SendOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Recipients is null || options.Recipients.Count == 0)
            throw new ArgumentException("At least one recipient DID is required.", nameof(options));
        if (_transportRouter is null)
        {
            throw new InvalidOperationException(
                "SendAsync requires an ITransportRouter. Register a transport via builder.UseHttpTransport()/UseWebSocketTransport() or pass one to the (secrets, keyService, serviceResolver, transportRouter, options) constructor (FR-TRN-01).");
        }

        // When the caller supplies an explicit endpoint, we skip the FR-ROUTE-02 forward
        // wrapping path: the sender already knows where to send the inner envelope. Useful
        // for tests, for direct (non-mediated) recipients, and for transports built on top
        // of a known peer URI. Otherwise we pack with Forward = true so the facade resolves
        // the recipient's DIDCommMessaging service and surfaces a transport URI.
        var packOptions = new PackEncryptedOptions(
            Recipients: options.Recipients,
            From: options.From,
            SignFrom: options.SignFrom,
            Enc: options.Enc,
            ProtectSender: options.ProtectSender,
            Forward: options.ServiceEndpointOverride is null);

        var packed = await PackEncryptedAsync(message, packOptions, ct).ConfigureAwait(false);

        var endpointUri = options.ServiceEndpointOverride;
        if (endpointUri is null)
        {
            if (packed.ServiceEndpoint is null)
            {
                throw new InvalidOperationException(
                    $"Recipient '{options.Recipients[0]}' has no resolvable DIDCommMessaging service endpoint and no ServiceEndpointOverride was supplied. Configure a routing service on the recipient's DID document, or pass SendOptions.ServiceEndpointOverride (FR-ROUTE-03).");
            }
            if (!Uri.TryCreate(packed.ServiceEndpoint, UriKind.Absolute, out endpointUri))
            {
                throw new TransportException(
                    $"Resolved service endpoint is not an absolute URI: '{packed.ServiceEndpoint}' (FR-ROUTE-03).");
            }

            // SSRF defense: this endpoint host came from the recipient's DID document and is
            // therefore attacker-influenced. Reject private / loopback / metadata destinations
            // before POSTing the packed envelope. The ServiceEndpointOverride branch above is
            // caller-supplied and trusted, so it intentionally skips this gate.
            new OutboundEndpointGuard(_options.OutboundEndpointPolicy).Validate(endpointUri);
        }

        var payload = Encoding.UTF8.GetBytes(packed.Message);
        var mediaType = ForwardConstants.PayloadMediaType;
        var request = new TransportRequest(endpointUri, payload, mediaType);
        var transportResult = await _transportRouter.SendAsync(request, ct).ConfigureAwait(false);
        return new SendResult(packed, transportResult, endpointUri);
    }

    /// <summary>
    /// Unpack <paramref name="packed"/>, auto-detecting the envelope shape (plaintext / signed
    /// / encrypted) and recursively unwrapping nested compositions per FR-API-03. Enforces
    /// FR-API-05 expiry, FR-API-06 size limit, FR-DID-06 did:web rejection, and the
    /// FR-CONSIST-01..06 addressing-consistency rules (FR-CONSIST-06 is resolver-backed).
    /// </summary>
    /// <remarks>
    /// <strong>Support boundary:</strong> the unpack pipeline drives the consumer-supplied
    /// <see cref="ISecretsResolver"/> and <see cref="IDidKeyService"/> through a sync-over-async
    /// bridge (the JOSE composition layer is synchronous — see PRD §7). This is safe in hosts
    /// with no <see cref="System.Threading.SynchronizationContext"/> — ASP.NET Core, console,
    /// generic-host worker services (the supported targets). Calling this under a captured
    /// synchronization context (legacy WPF/WinForms or a custom context) can deadlock if a
    /// resolver implementation has an inner <c>await</c> without <c>ConfigureAwait(false)</c>.
    /// Invoke from such contexts via <c>Task.Run(() =&gt; client.UnpackAsync(...))</c>.
    /// </remarks>
    /// <param name="packed">The packed DIDComm message.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<UnpackResult> UnpackAsync(string packed, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packed);
        if (Encoding.UTF8.GetByteCount(packed) > _options.MaxReceiveBytes)
        {
            throw new MalformedMessageException(
                $"Packed message exceeds DidCommOptions.MaxReceiveBytes (FR-API-06). Limit={_options.MaxReceiveBytes} bytes.");
        }

        ct.ThrowIfCancellationRequested();

        var secretsLookup = new SyncSecretsAdapter(_secrets);
        var senderLookup = DidKeyServiceLookups.SenderKeyLookup(_keyService);
        var signerLookup = DidKeyServiceLookups.SignerKeyLookup(_keyService);
        var resolverCheck = BuildResolverAuthorizationPredicate();

        var internalResult = EnvelopeReader.Unpack(packed, secretsLookup, senderLookup, signerLookup, _cryptoProvider, resolverCheck);
        var message = internalResult.Message;

        if (message.From is not null)
            _keyService.RejectUnsupportedMethod(message.From);
        if (message.To is not null)
        {
            foreach (var to in message.To)
                _keyService.RejectUnsupportedMethod(to);
        }

        if (message.ExpiresTime is long expires)
        {
            var nowSeconds = _options.Now().ToUnixTimeSeconds();
            var skewSeconds = (long)Math.Floor(_options.ExpiresClockSkew.TotalSeconds);
            if (nowSeconds - skewSeconds > expires)
            {
                throw new MalformedMessageException(
                    $"Message expired at {expires} (epoch seconds); current clock {nowSeconds} exceeds it with skew={skewSeconds}s (FR-API-05).");
            }
        }

        FromPriorClaims? fromPrior = null;
        if (message.FromPrior is not null && message.From is not null)
        {
            // FR-ROT-03 — from_prior MUST arrive inside an encrypt layer.
            if (!internalResult.Encrypted)
            {
                throw new ConsistencyException(
                    "Message carries 'from_prior' but was not encrypted (FR-ROT-03). Drop.");
            }
            // FR-ROT-03 hardening: the rotation binds to the post-rotation sender via sub == from, but
            // 'from' is only cryptographically authenticated when the envelope authenticates the sender
            // (authcrypt skid or an inner signature). On a plain anoncrypt envelope 'from' is attacker-
            // settable, so a captured rotation JWT could be replayed under a spoofed sender. Require the
            // carrying envelope to actually authenticate the sender.
            if (!internalResult.Authenticated)
            {
                throw new ConsistencyException(
                    "Message carries 'from_prior' but the sender is not authenticated (anoncrypt). A rotation " +
                    "assertion MUST ride on an authcrypt or signed envelope (FR-ROT-03). Drop.");
            }
            var validated = await FromPriorValidator.ValidateAsync(
                message.FromPrior, message.From, _keyService, _cryptoProvider, ct).ConfigureAwait(false);
            ValidateFromPriorFreshness(validated);
            fromPrior = validated;
        }

        return new UnpackResult(
            Message: message,
            Stack: internalResult.Stack,
            Encrypted: internalResult.Encrypted,
            Authenticated: internalResult.Authenticated,
            NonRepudiation: internalResult.NonRepudiation,
            AnonymousSender: internalResult.AnonymousSender,
            ContentEncryption: internalResult.ContentEncryption,
            KeyWrap: internalResult.KeyWrap,
            SignatureAlgorithm: internalResult.SignatureAlgorithm,
            SignerKid: internalResult.SignerKid,
            SenderKid: internalResult.SenderKid,
            RecipientKid: internalResult.RecipientKid,
            AllRecipientKids: internalResult.AllRecipientKids,
            FromPrior: fromPrior);
    }

    private async Task<Jwk> PickSignerPrivateKeyAsync(string signerDid, CancellationToken ct)
    {
        var pubs = await _keyService.GetVerificationMethodsAsync(signerDid, VerificationRelationship.Authentication, ct).ConfigureAwait(false);
        if (pubs.Count == 0)
            throw new DidResolutionException(signerDid, "no authentication keys available");

        var presentKids = await _secrets.FindPresentAsync(pubs.Where(k => k.Kid is not null).Select(k => k.Kid!), ct).ConfigureAwait(false);
        if (presentKids.Count == 0)
            throw new SecretNotFoundException($"{signerDid} (no authentication key held)");

        var kid = presentKids[0];
        var priv = await _secrets.FindAsync(kid, ct).ConfigureAwait(false)
            ?? throw new SecretNotFoundException(kid);
        return priv;
    }

    private void RejectUnsupportedDidsOnMessage(Message message)
    {
        if (message.From is not null)
            _keyService.RejectUnsupportedMethod(message.From);
        if (message.To is not null)
        {
            foreach (var to in message.To)
                _keyService.RejectUnsupportedMethod(to);
        }
    }

    private static void RejectFromPriorOnUnprotectedEnvelope(Message message, string method)
    {
        if (message.FromPrior is not null)
        {
            throw new InvalidOperationException(
                $"{method} refused: messages carrying 'from_prior' MUST be encrypted (FR-ROT-03). Call PackEncryptedAsync instead.");
        }
    }

    // FR-ROT-05 freshness: when the rotation JWT carries exp / nbf, reject an expired or not-yet-valid
    // assertion (with the configured clock skew). Full out-of-order pre-rotation detection still needs
    // per-relationship state and is the application's responsibility — it has iss/iat/exp to compare.
    private void ValidateFromPriorFreshness(FromPriorClaims claims)
    {
        var nowSeconds = _options.Now().ToUnixTimeSeconds();
        var skewSeconds = (long)Math.Floor(_options.ExpiresClockSkew.TotalSeconds);
        if (claims.Exp is long exp && nowSeconds - skewSeconds > exp)
        {
            throw new ConsistencyException(
                $"from_prior JWT expired at {exp} (epoch seconds); current clock {nowSeconds} exceeds it with skew={skewSeconds}s (FR-ROT-05).");
        }
        if (claims.Nbf is long nbf && nowSeconds + skewSeconds < nbf)
        {
            throw new ConsistencyException(
                $"from_prior JWT is not yet valid (nbf={nbf} epoch seconds); current clock {nowSeconds} with skew={skewSeconds}s.");
        }
    }

    private static string MapContentEncryption(ContentEncryptionAlgorithm enc, bool isAuthcrypt) => enc switch
    {
        ContentEncryptionAlgorithm.A256CbcHs512 => JoseAlgorithms.A256CbcHs512,
        ContentEncryptionAlgorithm.A256Gcm when isAuthcrypt => throw new InvalidOperationException(
            "A256GCM is forbidden for authcrypt envelopes (FR-ENC-09). Use A256CBC-HS512 instead."),
        ContentEncryptionAlgorithm.XC20P when isAuthcrypt => throw new InvalidOperationException(
            "XC20P is forbidden for authcrypt envelopes (FR-ENC-09). Use A256CBC-HS512 instead."),
        ContentEncryptionAlgorithm.A256Gcm => JoseAlgorithms.A256Gcm,
        ContentEncryptionAlgorithm.XC20P => JoseAlgorithms.XC20P,
        _ => throw new ArgumentOutOfRangeException(nameof(enc), enc, "Unknown ContentEncryptionAlgorithm."),
    };

    /// <summary>
    /// Build the FR-CONSIST-06 resolver-backed authorization predicate. The closure does a
    /// sync-over-async <see cref="IDidKeyService.IsKeyAuthorizedAsync"/> call — safe under
    /// .NET 10's no-synchronization-context runtime, and warm against the
    /// <c>CachingDidResolver</c> for resolvers wrapped in net-did's DI builder. See the
    /// <see cref="UnpackAsync"/> support-boundary note for the synchronization-context caveat.
    /// </summary>
    private Func<string, string, string, bool> BuildResolverAuthorizationPredicate()
    {
        var keyService = _keyService;
        return (assertedDid, kid, relationship) =>
        {
            var rel = string.Equals(relationship, "authentication", StringComparison.Ordinal)
                ? VerificationRelationship.Authentication
                : VerificationRelationship.KeyAgreement;
            return keyService.IsKeyAuthorizedAsync(assertedDid, kid, rel)
                .ConfigureAwait(false).GetAwaiter().GetResult();
        };
    }

    /// <summary>
    /// Pick the highest-preference curve that every recipient set contains (and, if authcrypt,
    /// the sender set also contains). Preference order: X25519 → P-256 → P-384 → P-521.
    /// Returns <c>null</c> when no common curve exists — the facade surfaces that as an
    /// explicit failure rather than silently splitting envelopes (per-curve splitting lands in
    /// a later iteration if real fixtures require it).
    /// </summary>
    private static string? PickCommonCurve(
        IEnumerable<IReadOnlyList<Jwk>> recipientSets,
        IReadOnlyList<Jwk> senderKeys,
        bool requireSender)
    {
        var preference = new[]
        {
            JoseAlgorithms.CrvX25519,
            JoseAlgorithms.CrvP256,
            JoseAlgorithms.CrvP384,
            JoseAlgorithms.CrvP521,
        };

        foreach (var crv in preference)
        {
            var allRecipientsCovered = true;
            foreach (var set in recipientSets)
            {
                if (!set.Any(k => string.Equals(k.Crv, crv, StringComparison.Ordinal)))
                {
                    allRecipientsCovered = false;
                    break;
                }
            }
            if (!allRecipientsCovered)
                continue;

            if (requireSender && !senderKeys.Any(k => string.Equals(k.Crv, crv, StringComparison.Ordinal)))
                continue;

            return crv;
        }

        return null;
    }
}
