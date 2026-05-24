using DidComm.Composition;
using DidComm.Crypto;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols.Rotation;
using DidComm.Resolution;
using DidComm.Secrets;

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
    private readonly DidCommOptions _options;
    private readonly DefaultCryptoProvider _cryptoProvider;

    /// <summary>Initialize the facade.</summary>
    /// <param name="secrets">Consumer-supplied private-key resolver (FR-SEC-01).</param>
    /// <param name="keyService">DID resolution + verification-method extraction (FR-DID-01..05).</param>
    /// <param name="options">Process-wide options (FR-API-05/06 knobs).</param>
    public DidCommClient(ISecretsResolver secrets, IDidKeyService keyService, DidCommOptions options)
        : this(secrets, keyService, options, new DefaultCryptoProvider()) { }

    /// <summary>Initialize the facade with a custom crypto provider; used by tests.</summary>
    internal DidCommClient(
        ISecretsResolver secrets,
        IDidKeyService keyService,
        DidCommOptions options,
        DefaultCryptoProvider cryptoProvider)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(keyService);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cryptoProvider);
        _secrets = secrets;
        _keyService = keyService;
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
    /// <c>ProtectSender</c>. Enforces FR-ENC-09 (no GCM/XC20P for authcrypt).
    /// </summary>
    /// <param name="message">The plaintext message.</param>
    /// <param name="options">Recipient list, sender/signer DIDs, content-encryption choice.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string> PackEncryptedAsync(Message message, PackEncryptedOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Recipients is null || options.Recipients.Count == 0)
            throw new ArgumentException("At least one recipient DID is required.", nameof(options));

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

        return EnvelopeWriter.PackEncrypted(parameters, _cryptoProvider);
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
            fromPrior = await FromPriorValidator.ValidateAsync(
                message.FromPrior, message.From, _keyService, _cryptoProvider, ct).ConfigureAwait(false);
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
