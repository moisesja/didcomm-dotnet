using System.Collections.Frozen;
using System.Text.Json;
using DidComm.Composition;
using DidComm.Consistency;
using DidComm.Diagnostics;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Json;
using DidComm.Messages;
using DidComm.Protocols.Rotation;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DpEnc = DataProofsDotnet.Jose.Encryption;
using DpSig = DataProofsDotnet.Jose.Signing;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Facade;

/// <summary>
/// The public DIDComm v2.1 Pack/Unpack surface (FR-API-01..08). Resolves DID strings via the
/// registered <see cref="IDidKeyService"/>, pulls private keys via <see cref="ISecretsResolver"/>,
/// and delegates the JOSE composition to the internal envelope layer. Thread-safe (NFR-03) —
/// register as a singleton.
/// </summary>
/// <example>
/// The 25-line authcrypt round-trip in <c>samples/01-Quickstart</c> is the fastest introduction; <c>samples/02-Cookbook</c> demonstrates every facade operation task by task.
/// </example>
public sealed partial class DidCommClient
{
    private readonly ISecretsResolver _secrets;
    private readonly IDidKeyService _keyService;
    private readonly IServiceEndpointResolver? _serviceResolver;
    private readonly ITransportRouter? _transportRouter;
    private readonly DidCommOptions _options;
    private readonly JoseCryptoProvider _cryptoProvider;
    // Structured diagnostics (NFR-04). Defaults to NullLogger so every pre-existing construction
    // path keeps compiling and behaving identically; AddDidComm wires the host's logger in DI.
    private readonly ILogger<DidCommClient> _logger;
    // Resolves a kid to an opaque-or-extractable JWS signer / ECDH handle (FR-SEC-06). The opaque
    // path is selected when the registered ISecretsResolver also implements IOpaqueKeyResolver (or
    // one is registered separately), so non-extractable (HSM/KMS/keystore) keys never surface 'd'.
    private readonly KeyOperationResolver _keyOps;
    // DidCommOptions.OwnIdentifiers, normalized to bare DID subjects and frozen at construction
    // (FR-CONSIST-04, #59). Prebuilt so per-message addressing work scales with the wire's 'to'
    // list only, never with the declared roster — see SnapshotOwnIdentifiers.
    private readonly FrozenSet<string> _ownDidSubjects;

    /// <summary>Initialize the facade. Routing (FR-ROUTE-*) is unavailable without an <see cref="IServiceEndpointResolver"/>; for that, use the <see cref="DidCommClient(ISecretsResolver, IDidKeyService, IServiceEndpointResolver, DidCommOptions)"/> overload or register the facade through <c>AddDidComm</c>.</summary>
    /// <param name="secrets">Consumer-supplied private-key resolver (FR-SEC-01).</param>
    /// <param name="keyService">DID resolution + verification-method extraction (FR-DID-01..05).</param>
    /// <param name="options">Process-wide options (FR-API-05/06 knobs).</param>
    public DidCommClient(ISecretsResolver secrets, IDidKeyService keyService, DidCommOptions options)
        : this(secrets, keyService, serviceResolver: null, transportRouter: null, options, new JoseCryptoProvider()) { }

    /// <summary>
    /// Initialize the facade with a structured logger for the SHOULD-level advisories the client
    /// emits (currently the FR-SIG-05 warning when a standalone signed message lacks <c>to</c>).
    /// A new overload rather than an optional parameter so apps compiled against earlier releases
    /// still bind to the existing constructors unchanged. DI consumers get the logger wired
    /// automatically by <c>AddDidComm</c>.
    /// </summary>
    /// <param name="secrets">Consumer-supplied private-key resolver (FR-SEC-01).</param>
    /// <param name="keyService">DID resolution + verification-method extraction (FR-DID-01..05).</param>
    /// <param name="options">Process-wide options (FR-API-05/06 knobs).</param>
    /// <param name="logger">Structured logger; pass <see cref="NullLogger{T}.Instance"/> to silence advisories.</param>
    /// <example>
    /// <code>
    /// using var factory = LoggerFactory.Create(b => b.AddConsole());
    /// var client = new DidCommClient(secrets, keyService, options, factory.CreateLogger&lt;DidCommClient&gt;());
    /// </code>
    /// </example>
    public DidCommClient(ISecretsResolver secrets, IDidKeyService keyService, DidCommOptions options, ILogger<DidCommClient> logger)
        : this(secrets, keyService, serviceResolver: null, transportRouter: null, options, new JoseCryptoProvider(), opaqueKeys: null, logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
    }

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
        : this(secrets, keyService, serviceResolver, transportRouter: null, options, new JoseCryptoProvider())
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
        : this(secrets, keyService, serviceResolver, transportRouter, options, new JoseCryptoProvider())
    {
        ArgumentNullException.ThrowIfNull(serviceResolver);
        ArgumentNullException.ThrowIfNull(transportRouter);
    }

    /// <summary>Initialize the facade with a custom crypto provider (and optional opaque key resolver / logger); used by tests and DI.</summary>
    internal DidCommClient(
        ISecretsResolver secrets,
        IDidKeyService keyService,
        IServiceEndpointResolver? serviceResolver,
        ITransportRouter? transportRouter,
        DidCommOptions options,
        JoseCryptoProvider cryptoProvider,
        IOpaqueKeyResolver? opaqueKeys = null,
        ILogger<DidCommClient>? logger = null)
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
        _logger = logger ?? NullLogger<DidCommClient>.Instance;
        // Prefer an explicitly-registered IOpaqueKeyResolver; otherwise auto-detect when the secrets
        // resolver itself implements it (the common case — e.g. a keystore-backed resolver). Either
        // way the extractable path is preserved when no opaque capability is present (FR-SEC-06).
        var opaque = opaqueKeys ?? secrets as IOpaqueKeyResolver;
        _keyOps = new KeyOperationResolver(secrets, opaque, cryptoProvider);
        _ownDidSubjects = SnapshotOwnIdentifiers(options.OwnIdentifiers);
    }

    /// <summary>
    /// Resolve <see cref="DidCommOptions.OwnIdentifiers"/> to a frozen set of DID subjects, once
    /// (FR-CONSIST-04, #59). Every per-message cost of the recipient-addressing check is paid here
    /// instead: parsing each declared DID URL down to its subject, deduplicating, and building the
    /// lookup. Unpack then walks only the wire's <c>to</c> list against this set, so an agent
    /// declaring a large tenant roster does not turn each unauthenticated inbound message into a
    /// roster-sized parse — the amplification and latency-visible roster signal that made a
    /// per-message loop over the raw strings unacceptable.
    /// </summary>
    /// <remarks>
    /// Snapshotting also decouples the check from the caller's live, mutable collection: enumerating
    /// that mid-unpack would throw if the app appended to it, turning an advisory check into a
    /// dropped message. An entry that is not a parseable DID / DID URL is rejected here rather than
    /// silently skipped per message: skipping is right for the check itself (a typo must not make
    /// every message warn), but it renders a misconfigured agent indistinguishable from an
    /// unconfigured one, leaving the signal permanently dead with no indication. Failing at
    /// construction turns that into a startup error the operator can actually see.
    /// </remarks>
    private static FrozenSet<string> SnapshotOwnIdentifiers(IReadOnlyCollection<string>? configured)
    {
        if (configured is null || configured.Count == 0) return FrozenSet<string>.Empty;

        var subjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identifier in configured)
        {
            // Checked first and without quoting the entry (FR-API-09, #80): DidSubjectOf treats an
            // over-long value as "not a DID", and the error below would then copy all of it into the
            // exception message.
            if (identifier is { Length: > IdentifierLimits.MaxKeyIdLength })
            {
                throw new ArgumentException(
                    $"DidCommOptions.OwnIdentifiers contains an entry of {identifier.Length} characters, which exceeds the maximum DID or DID URL length of {IdentifierLimits.MaxKeyIdLength} characters (FR-API-09, FR-CONSIST-04).",
                    nameof(configured));
            }

            var subject = DidSubject.DidSubjectOf(identifier)
                ?? throw new ArgumentException(
                    $"DidCommOptions.OwnIdentifiers contains '{identifier}', which is not a parseable DID or DID URL (FR-CONSIST-04).",
                    nameof(configured));
            subjects.Add(subject);
        }
        return subjects.ToFrozenSet(StringComparer.Ordinal);
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
        // NFR-05: zero-cost when unobserved — StartActivity returns null with no listener and
        // every tag write below is null-guarded.
        using var activity = DidCommDiagnostics.Source.StartActivity(DidCommDiagnostics.PackPlaintextActivity);
        activity?.SetTag(DidCommDiagnostics.MessageTypeTag, message.Type);
        try
        {
            RejectFromPriorOnUnprotectedEnvelope(message, "PackPlaintextAsync");
            RejectUnsupportedDidsOnMessage(message);
            return Task.FromResult(EnvelopeWriter.PackPlaintext(message));
        }
        catch (Exception ex)
        {
            DidCommDiagnostics.RecordFailure(activity, ex);
            LogPackFailed(_logger, "plaintext", ex.GetType().Name);
            throw;
        }
    }

    /// <summary>
    /// Pack <paramref name="message"/> as a signed DIDComm envelope (JWS over the deterministic
    /// canonical bytes of the inner plaintext). The library selects a private key authorized
    /// under <paramref name="signFrom"/>'s <c>authentication</c> relationship; if no such key
    /// is held by the registered <see cref="ISecretsResolver"/> a
    /// <see cref="SecretNotFoundException"/> is thrown.
    /// </summary>
    /// <remarks>
    /// Per FR-SIG-05, a standalone signed message SHOULD contain a <c>to</c> header — a signed
    /// envelope names no recipient of its own, so without <c>to</c> anyone holding it can
    /// surreptitiously forward it to an unintended audience. This method still packs a
    /// <c>to</c>-less message (SHOULD, not MUST) but emits a structured warning through the
    /// configured logger. Contrast FR-SIG-06: for sign-then-encrypt the inner <c>to</c> is a
    /// MUST and is enforced, not warned.
    /// </remarks>
    /// <param name="message">The plaintext message. SHOULD carry a non-empty <c>to</c> (FR-SIG-05).</param>
    /// <param name="signFrom">The signer DID (no fragment).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string> PackSignedAsync(Message message, string signFrom, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(signFrom);
        using var activity = DidCommDiagnostics.Source.StartActivity(DidCommDiagnostics.PackSignedActivity);
        activity?.SetTag(DidCommDiagnostics.MessageTypeTag, message.Type);
        try
        {
            RejectFromPriorOnUnprotectedEnvelope(message, "PackSignedAsync");
            _keyService.RejectUnsupportedMethod(signFrom);
            RejectUnsupportedDidsOnMessage(message);

            // FR-SIG-05 (SHOULD): warn — don't refuse — when a STANDALONE signed message lacks 'to'.
            // The signed-inside-encrypted path never reaches this method (its inner 'to' is FR-SIG-06's
            // MUST, enforced by the envelope layer), so this warning fires only for standalone packs.
            if (message.To is not { Count: > 0 })
                LogStandaloneSignedMissingTo(_logger, message.Id);

            var signer = await PickSignerAsync(signFrom, ct).ConfigureAwait(false);
            var parameters = new PackSignedParameters(message, new[] { signer });
            return await EnvelopeWriter.PackSignedAsync(parameters, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DidCommDiagnostics.RecordFailure(activity, ex);
            if (ex is not OperationCanceledException)
                LogPackFailed(_logger, "signed", ex.GetType().Name);
            throw;
        }
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
    /// <remarks>
    /// <para>
    /// <strong>Which keys the envelope is encrypted to (FR-ENC-19).</strong> One curve is negotiated
    /// for the whole envelope: the first of X25519, P-256, P-384 and P-521 that every
    /// <see cref="PackEncryptedOptions.Recipients"/> entry has a key on and, for authcrypt, that the
    /// sender holds a key on. Each DID entry then contributes every one of its <c>keyAgreement</c>
    /// keys on that curve whose key id names that DID, in document order, so the recipient can
    /// decrypt on any device holding any of them: at most 64 automatically selected keys for each
    /// bare DID. A DID URL entry (<c>did#fragment</c>) contributes exactly that key, taken as given;
    /// a pinned key never counts toward a bare DID's 64, whether the pin comes before or after the
    /// bare DID, and because it is the entry's only candidate, its curve becomes the envelope's.
    /// Entries are taken in order and each kid appears once. The signed inner layer
    /// (<see cref="PackEncryptedOptions.SignFrom"/>) and the outer anoncrypt layer
    /// (<see cref="PackEncryptedOptions.ProtectSender"/>) use the same recipient keys. Keys left out
    /// for another curve or the cap are reported by a Debug log with counts only.
    /// </para>
    /// <para>
    /// <strong>Order with <see cref="PackEncryptedOptions.Forward"/>.</strong> The recipient's
    /// DIDCommMessaging service entry is read once, right after the recipient's keys and before the
    /// sender's keys, the signer and the pack; any mediator or routing-key DID the route names is
    /// resolved after the pack, from that same entry.
    /// </para>
    /// </remarks>
    /// <param name="message">The plaintext message.</param>
    /// <param name="options">Recipient list, sender/signer DIDs, content-encryption choice, optional forward toggle.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">No recipient, or an entry that is empty, longer than 4096 characters (not quoted), or a DID URL with a path, query, parameters or an empty fragment.</exception>
    /// <exception cref="UnsupportedDidMethodException">A <see cref="PackEncryptedOptions.Recipients"/> entry, <see cref="PackEncryptedOptions.From"/>, <see cref="PackEncryptedOptions.SignFrom"/>, or the message's <c>from</c> or a <c>to</c> entry is a <c>did:web</c> DID (DD-08).</exception>
    /// <exception cref="DidResolutionException">A DID does not resolve or is not a valid DID; a recipient DID has no keyAgreement key with a key id of that DID; a pinned key is not one of its DID's keyAgreement keys; the authcrypt sender has no keyAgreement keys or the signer no authentication keys. With <see cref="PackEncryptedOptions.Forward"/>, also: the recipient has no DIDCommMessaging service; its route needs more than 8 forward layers; or a mediator or routing key cannot be used (its DID does not resolve or has no DIDCommMessaging service, the reference is malformed or over the FR-API-09 identifier limits, a routing key is not declared in keyAgreement, or no key has a key id of that DID on a supported curve).</exception>
    /// <exception cref="ConsistencyException">With <see cref="PackEncryptedOptions.Forward"/>, the mediator named as the recipient's endpoint publishes a DID as its own endpoint; recursive endpoint resolution is forbidden (FR-ROUTE-04).</exception>
    /// <exception cref="SecretNotFoundException">The authcrypt sender holds the private key of none of its usable keyAgreement keys, or the signer of none of its authentication keys.</exception>
    /// <exception cref="InvalidOperationException">No curve is common to every entry (and the authcrypt sender); a forbidden content encryption for authcrypt; or <see cref="PackEncryptedOptions.Forward"/> with entries naming more than one DID or without an <see cref="IServiceEndpointResolver"/>.</exception>
    public async Task<PackEncryptedResult> PackEncryptedAsync(Message message, PackEncryptedOptions options, CancellationToken ct = default)
        => (await PackEncryptedInstrumentedAsync(message, options, refuseForwardLayers: false, ct).ConfigureAwait(false)).Result;

    /// <summary>
    /// <see cref="PackEncryptedAsync"/> with its span, failure log and argument checks, also
    /// reporting how many forward layers were wrapped. <see cref="SendAsync"/> calls it directly so
    /// it can refuse a routed recipient for a return-route send (FR-RR-07) and learn whether the
    /// route is direct; the pack keeps its own child span either way.
    /// </summary>
    /// <param name="message">The plaintext message.</param>
    /// <param name="options">The pack options.</param>
    /// <param name="refuseForwardLayers">With <see cref="PackEncryptedOptions.Forward"/>: throw <see cref="InvalidOperationException"/> when the recipient's selected service entry needs a forward layer, right after it is read and before packing.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<(PackEncryptedResult Result, int ForwardLayers)> PackEncryptedInstrumentedAsync(
        Message message, PackEncryptedOptions options, bool refuseForwardLayers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(options);
        using var activity = DidCommDiagnostics.Source.StartActivity(DidCommDiagnostics.PackEncryptedActivity);
        if (activity is not null)
        {
            // NFR-04: header-level metadata only — type URI, JOSE identifiers, a count. Never
            // the recipient DIDs, the body, or key material.
            activity.SetTag(DidCommDiagnostics.MessageTypeTag, message.Type);
            activity.SetTag(DidCommDiagnostics.AlgTag, options.From is not null ? JoseAlgorithms.Ecdh1PuA256Kw : JoseAlgorithms.EcdhEsA256Kw);
            activity.SetTag(DidCommDiagnostics.EncTag, EncTagValue(options.Enc));
            activity.SetTag(DidCommDiagnostics.RecipientsTag, options.Recipients?.Count ?? 0);
        }
        try
        {
            return await PackEncryptedCoreAsync(message, options, refuseForwardLayers, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DidCommDiagnostics.RecordFailure(activity, ex);
            if (ex is not OperationCanceledException)
                LogPackFailed(_logger, "encrypted", ex.GetType().Name);
            throw;
        }
    }

    // The un-instrumented body of PackEncryptedAsync; the instrumented wrapper above owns the span,
    // failure log, and argument null checks. Returns the result and the number of forward layers.
    private async Task<(PackEncryptedResult Result, int ForwardLayers)> PackEncryptedCoreAsync(
        Message message, PackEncryptedOptions options, bool refuseForwardLayers, CancellationToken ct)
    {
        if (options.Recipients is null || options.Recipients.Count == 0)
            throw new ArgumentException("At least one recipient DID is required.", nameof(options));

        // Each entry is a DID, meaning all of its keys, or a DID URL pinning one key (FR-ENC-19).
        // Split by a linear scan before anything is resolved, so a malformed entry fails fast and only
        // bare DIDs ever reach the key service.
        var entries = new RecipientEntry[options.Recipients.Count];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = ParseRecipientEntry(options.Recipients[i], nameof(options));

        if (options.Forward)
        {
            // FR-ROUTE-02 (Phase 4): forward wrapping shape checks happen up front so the caller
            // gets a fast, deterministic error before any DID resolution / curve selection runs.
            // The route and body.next belong to one DID, so every entry must name the same one;
            // pinning keys of that DID is fine.
            if (!AllNameOneDid(entries))
            {
                throw new InvalidOperationException(
                    "Forward wrapping supports single-recipient packs only: every Recipients entry must name the same DID. Drop Forward = true or call PackEncryptedAsync once per recipient.");
            }
            if (_serviceResolver is null)
            {
                throw new InvalidOperationException(
                    "Forward = true requires an IServiceEndpointResolver. Use the (ISecretsResolver, IDidKeyService, IServiceEndpointResolver, DidCommOptions) constructor, or register the facade via AddDidComm + UseNetDidResolver.");
            }
        }

        foreach (var entry in entries)
            _keyService.RejectUnsupportedMethod(entry.Did);
        if (options.From is not null)
            _keyService.RejectUnsupportedMethod(options.From);
        if (options.SignFrom is not null)
            _keyService.RejectUnsupportedMethod(options.SignFrom);
        RejectUnsupportedDidsOnMessage(message);

        var encJoseAlg = MapContentEncryption(options.Enc, isAuthcrypt: options.From is not null);

        // Resolve each distinct DID once, keeping the keys an envelope can name: those whose kid is a
        // key id of that DID, each kid once, in document order (FR-ENC-19). A key another DID's kid
        // names is left out, so one recipient's document cannot take a co-recipient's kid.
        var addressableByDid = new Dictionary<string, IReadOnlyList<Jwk>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (addressableByDid.ContainsKey(entry.Did))
                continue;
            var keys = await _keyService.GetVerificationMethodsAsync(entry.Did, VerificationRelationship.KeyAgreement, ct).ConfigureAwait(false);
            if (keys.Count == 0)
                throw new DidResolutionException(entry.Did, "no keyAgreement keys available");
            var addressable = RecipientKeySelection.Addressable(entry.Did, keys);
            if (addressable.Count == 0)
                throw new DidResolutionException(entry.Did, $"none of its {keys.Count} keyAgreement keys has a key id of this DID");
            addressableByDid[entry.Did] = addressable;
        }

        // Each entry's candidates: every addressable key of a bare DID, or exactly the pinned one.
        // Curve negotiation runs over these, so a pinned key decides the curve for everyone.
        var entryKeys = new IReadOnlyList<Jwk>[entries.Length];
        for (var i = 0; i < entries.Length; i++)
            entryKeys[i] = CandidatesOf(entries[i], addressableByDid[entries[i].Did]);

        // FR-ROUTE-02/03: with Forward, the recipient's service entry is read here, once, right after
        // its keys and before the sender's keys, the signer or the pack (JC-14). ExpandAsync below
        // consumes this same list, so the refusal and the route judge one read of one entry (L-039),
        // counted by the expander's own rule (L-041). FR-RR-07: a return-route send to a recipient
        // reached through routing keys or a mediator is refused before any mediator or routing-key
        // DID is resolved and before anything is packed; the message names no DID.
        IReadOnlyList<DidCommServiceInfo>? serviceEntries = null;
        if (options.Forward)
        {
            serviceEntries = await _serviceResolver!.ResolveAsync(entries[0].Did, ct).ConfigureAwait(false);
            if (refuseForwardLayers && serviceEntries.Count > 0 && MediatorEndpointExpander.ForwardLayerCount(serviceEntries[0]) > 0)
                throw new InvalidOperationException(RoutedReturnRouteRefusal);
        }

        IReadOnlyList<Jwk> senderPublics = Array.Empty<Jwk>();
        var senderOverLimit = 0;
        if (options.From is not null)
        {
            var allSenderPubs = await _keyService.GetVerificationMethodsAsync(options.From, VerificationRelationship.KeyAgreement, ct).ConfigureAwait(false);
            if (allSenderPubs.Count == 0)
                throw new DidResolutionException(options.From, "no keyAgreement keys available for sender");

            // Filter to keys the agent actually holds the private half of — opaque (keystore) or
            // extractable. FindPresent answers held-ness without exposing key material (FR-SEC-06).
            // Keys whose kid is over the FR-API-09 identifier limits are left out first (#80): every
            // didcomm-dotnet receiver refuses such an skid, so emitting it would only produce an
            // undeliverable message.
            (var senderKids, senderOverLimit) = OwnKidsWithinLimits(allSenderPubs);
            var heldKids = await _keyOps.FindPresentAsync(senderKids, ct).ConfigureAwait(false);
            // ISecretsResolver promises a subset of what it was asked. Keep only kids we asked about,
            // so a resolver that lists everything it holds cannot bring back a key left out above.
            var heldKidSet = new HashSet<string>(heldKids, StringComparer.Ordinal);
            heldKidSet.IntersectWith(senderKids);
            senderPublics = allSenderPubs.Where(k => k.Kid is not null && heldKidSet.Contains(k.Kid!)).ToArray();
            if (senderPublics.Count == 0)
            {
                throw senderOverLimit == 0
                    ? new SecretNotFoundException($"{options.From} (no held authcrypt sender private key for any of {allSenderPubs.Count} keyAgreement keys)")
                    : new SecretNotFoundException(options.From,
                        $"No usable authcrypt sender private key for '{options.From}': {senderOverLimit} of its {allSenderPubs.Count} keyAgreement keys have kids over the FR-API-09 identifier limits and were not considered, and no remaining key is held.");
            }
        }

        var chosenCurve = RecipientKeySelection.PickCommonCurve(entryKeys, senderPublics, requireSender: options.From is not null);
        if (chosenCurve is null)
        {
            throw new InvalidOperationException(
                "No common keyAgreement curve across recipients" + (options.From is not null ? " and sender." : ".")
                + (senderOverLimit > 0
                    ? $" {senderOverLimit} of the sender's keyAgreement keys were not considered because their kids are over the FR-API-09 identifier limits."
                    : ""));
        }

        var chosenRecipients = SelectRecipientKeys(entries, entryKeys, chosenCurve);

        DpEnc.IEcdhKey? senderKey = null;
        string? skid = null;
        if (options.From is not null)
        {
            var senderPubJwk = senderPublics.First(k => k.Crv == chosenCurve);
            skid = senderPubJwk.Kid;
            // Opaque (keystore) or extractable ECDH handle for the sender-static 1PU derivation; the
            // private scalar never leaves custody on the opaque path (FR-SEC-06).
            senderKey = await _keyOps.ResolveKeyAgreementAsync(senderPubJwk.Kid!, ct).ConfigureAwait(false)
                ?? throw new SecretNotFoundException(senderPubJwk.Kid!);
        }

        IReadOnlyList<DpSig.JwsSigner>? signers = null;
        if (options.SignFrom is not null)
        {
            var signer = await PickSignerAsync(options.SignFrom, ct).ConfigureAwait(false);
            signers = new[] { signer };
        }

        var parameters = new PackEncryptedParameters(
            Message: message,
            Recipients: chosenRecipients,
            ContentEncryption: encJoseAlg,
            SenderKey: senderKey,
            Skid: skid,
            Signers: signers,
            ProtectSender: options.ProtectSender);

        var innerPacked = await EnvelopeWriter.PackEncryptedAsync(parameters, _cryptoProvider, ct).ConfigureAwait(false);

        if (!options.Forward)
            return (new PackEncryptedResult(innerPacked, ServiceEndpoint: null, Array.Empty<string>()), 0);

        // FR-ROUTE-02 sender path: input-shape checks already validated up front, and the (single)
        // recipient DID's DIDCommMessaging service was read above. Expand any mediator-as-DID
        // endpoint (FR-ROUTE-04) from that same list, then wrap forward layers. body.next is the bare
        // DID even when the entries pinned keys of it.
        var recipientDid = entries[0].Did;
        var route = await MediatorEndpointExpander.ExpandAsync(serviceEntries!, _serviceResolver!, _keyService, recipientDid, ct).ConfigureAwait(false);

        if (route.Layers.Count == 0)
        {
            // No routing keys → direct delivery; surface the endpoint URI but skip wrapping.
            return (new PackEncryptedResult(innerPacked, route.TransportUri, route.FallbackUris), 0);
        }

        var wrapped = await ForwardWrapper.WrapAsync(innerPacked, route.Layers, recipientDid, _cryptoProvider, ct).ConfigureAwait(false);
        return (new PackEncryptedResult(wrapped, route.TransportUri, route.FallbackUris), route.Layers.Count);
    }

    /// <summary>
    /// Pack <paramref name="message"/> with <see cref="PackEncryptedOptions.Forward"/> = <c>true</c>
    /// (unless <paramref name="options"/> overrides the endpoint), then deliver the packed bytes
    /// via the registered <see cref="ITransportRouter"/> (FR-TRN-01). Requires both a
    /// <see cref="IServiceEndpointResolver"/> and a <see cref="ITransportRouter"/> to be wired in
    /// (use <c>AddDidComm(b =&gt; b.UseNetDidResolver().UseHttpTransport()...)</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The caller's message is never modified.</strong> When the send adds or changes a
    /// header (<see cref="SendOptions.ReturnRoute"/>, or the FR-RR-09 <c>created_time</c> below), it
    /// does so on a copy, and the copy is what is packed and sent.
    /// </para>
    /// <para>
    /// <strong><c>created_time</c> (FR-RR-09).</strong> A message that goes out with
    /// <c>return_route</c> <c>all</c> or <c>thread</c>, or whose type belongs to a mediator-service
    /// protocol (Coordinate Mediation 2.0/3.0, Message Pickup 3.0/4.0), falls under the receiver's
    /// FR-RR-08 replay admission, which refuses such a message without <c>created_time</c>. When it
    /// has none, the copy is stamped from <see cref="DidCommOptions.Clock"/>. A value the message
    /// already carries is sent unchanged, however old.
    /// </para>
    /// <para>
    /// <strong>Return route (FR-RR-07).</strong> With <c>all</c> or <c>thread</c> (from
    /// <see cref="SendOptions.ReturnRoute"/> or already on the message), the send is refused, in this
    /// order: with <see cref="ArgumentException"/> before anything is resolved when the recipients
    /// name more than one DID; and, when the endpoint is resolved rather than overridden, with
    /// <see cref="InvalidOperationException"/> when the recipient is reached through routing keys or
    /// a mediator, after the recipient's keys and service entry are read and before the message is
    /// packed or any mediator, sender key or secret is looked up. Over HTTP, an authcrypt send then
    /// reads the response body (at most <see cref="DidCommOptions.MaxReceiveBytes"/>) and surfaces
    /// the reply in <see cref="SendResult.Returned"/>; see that property for the identity rule.
    /// </para>
    /// <para>
    /// FR-RR-06: when the endpoint — the override, or the one resolved from the recipient's
    /// <c>DIDCommMessaging</c> service — is <c>didcomm:transport/queue</c>
    /// (<see cref="Transports.ReturnRoute.QueueTransport"/>), <c>SendAsync</c> throws a
    /// <see cref="TransportException"/> (scheme <c>didcomm</c>) before any transport activity; the
    /// recipient's keys and service entry are resolved and the message is packed first, because the
    /// endpoint lives in that service entry. The sender holds the message for pickup.
    /// </para>
    /// </remarks>
    /// <param name="message">The plaintext message.</param>
    /// <param name="options">Recipient list, sender/signer DIDs, content-encryption choice, optional explicit endpoint override, optional return route.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">No recipient or a malformed recipient entry; <see cref="SendOptions.ReturnRouteThread"/> missing, malformed or over-long with <see cref="ReturnRouteMode.Thread"/>, or set with any other mode; <c>return_route</c> <c>all</c> or <c>thread</c> with recipients naming more than one DID; or the message could not be copied.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="SendOptions.ReturnRoute"/> is not a defined <see cref="ReturnRouteMode"/>.</exception>
    /// <exception cref="InvalidOperationException">No <see cref="ITransportRouter"/> is wired in; <c>return_route</c> <c>all</c> or <c>thread</c> to a recipient reached through routing keys or a mediator; or any pack-stage refusal of <see cref="PackEncryptedAsync"/>.</exception>
    /// <exception cref="TransportException">The endpoint is the queue URI; the transport failed; or the request was accepted (status <c>200</c>) but the message returned for an <c>all</c> / <c>thread</c> send exceeded <see cref="DidCommOptions.MaxReceiveBytes"/>, was not received in time, or could not be unpacked.</exception>
    public async Task<SendResult> SendAsync(Message message, SendOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(options);
        using var activity = DidCommDiagnostics.Source.StartActivity(DidCommDiagnostics.SendActivity);
        if (activity is not null)
        {
            activity.SetTag(DidCommDiagnostics.MessageTypeTag, message.Type);
            activity.SetTag(DidCommDiagnostics.RecipientsTag, options.Recipients?.Count ?? 0);
        }
        try
        {
            var result = await SendCoreAsync(message, options, ct).ConfigureAwait(false);
            // The scheme is only known once routing resolved an endpoint; a bounded vocabulary
            // (https/wss/...), never the endpoint host or path (NFR-04).
            activity?.SetTag(DidCommDiagnostics.TransportSchemeTag, result.EndpointUsed.Scheme);
            return result;
        }
        catch (Exception ex)
        {
            DidCommDiagnostics.RecordFailure(activity, ex);
            if (ex is not OperationCanceledException)
                LogSendFailed(_logger, ex.GetType().Name);
            throw;
        }
    }

    // The un-instrumented body of SendAsync; the public wrapper above owns the span, failure
    // log, and argument null checks. The nested pack keeps its own child span.
    private async Task<SendResult> SendCoreAsync(Message message, SendOptions options, CancellationToken ct)
    {
        if (options.Recipients is null || options.Recipients.Count == 0)
            throw new ArgumentException("At least one recipient DID is required.", nameof(options));
        if (_transportRouter is null)
        {
            throw new InvalidOperationException(
                "SendAsync requires an ITransportRouter. Register a transport via builder.UseHttpTransport()/UseWebSocketTransport() or pass one to the (secrets, keyService, serviceResolver, transportRouter, options) constructor (FR-TRN-01).");
        }

        // Shape checks on the return-route options, before any I/O (L-008). No value is echoed.
        if (options.ReturnRoute is { } requestedMode)
        {
            if (!Enum.IsDefined(requestedMode))
                throw new ArgumentOutOfRangeException(nameof(options), "SendOptions.ReturnRoute is not a defined ReturnRouteMode.");
            ReturnRouteHeaderWriter.Validate(requestedMode, options.ReturnRouteThread, nameof(options), nameof(options));
        }
        else if (options.ReturnRouteThread is not null)
        {
            throw new ArgumentException("SendOptions.ReturnRouteThread is only valid with ReturnRoute = ReturnRouteMode.Thread.", nameof(options));
        }

        // FR-RR-06: the queue URI names no transport. A caller-supplied override is judged on its
        // shape before any packing; the resolved endpoint is judged right after the pack below,
        // because it lives in the recipient's service entry. Either way SendAsync throws before
        // any transport activity — the recipient's keys and service entry are resolved and the
        // message is packed first.
        if (QueueTransport.IsQueueTransport(options.ServiceEndpointOverride))
            throw new TransportException(QueueTransport.RefusalMessage, httpStatusCode: null, scheme: "didcomm");

        // JC-15: the header override and FR-RR-09 stamping happen on a snapshot, only when needed.
        var outgoing = PrepareOutgoing(message, options);

        // The effective header is read from what is actually sent. A malformed value counts as
        // `none`, as the receiver reads it (FR-RR-01/05); absent stays null.
        ReturnRouteMode? effectiveMode = outgoing.AdditionalHeaders?.ContainsKey(ReturnRouteHeader.HeaderName) == true
            ? ReturnRouteHeader.Read(outgoing).Mode
            : null;
        var asksReturn = effectiveMode is ReturnRouteMode.All or ReturnRouteMode.Thread;

        // JC-4: a return route binds one peer. Checked by a linear scan before anything is resolved.
        if (asksReturn && !AllNameOneDid(ParseRecipientEntries(options.Recipients, nameof(options))))
        {
            throw new ArgumentException(
                "A return_route of 'all' or 'thread' requires Recipients naming exactly one DID (FR-RR-07): replies come back over one connection, from one peer.",
                nameof(options));
        }

        // When the caller supplies an explicit endpoint, we skip the FR-ROUTE-02 forward
        // wrapping path: the sender already knows where to send the inner envelope. Useful
        // for tests, for direct (non-mediated) recipients, and for transports built on top
        // of a known peer URI. Otherwise we pack with Forward = true so the facade resolves
        // the recipient's DIDCommMessaging service and surfaces a transport URI. With an override
        // there is no route, so the return-route refusal never applies.
        var packOptions = new PackEncryptedOptions(
            Recipients: options.Recipients,
            From: options.From,
            SignFrom: options.SignFrom,
            Enc: options.Enc,
            ProtectSender: options.ProtectSender,
            Forward: options.ServiceEndpointOverride is null);

        var (packed, forwardLayers) = await PackEncryptedInstrumentedAsync(outgoing, packOptions, refuseForwardLayers: asksReturn, ct).ConfigureAwait(false);

        if (QueueTransport.IsQueueTransport(packed.ServiceEndpoint))
            throw new TransportException(QueueTransport.RefusalMessage, httpStatusCode: null, scheme: "didcomm");

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

        // JC-18: connection affinity only for an authcrypt send delivered directly (no forward
        // layer) to recipients naming one DID; a routed or anoncrypt send never carries identities.
        // The pack succeeded, so every entry is well-formed here.
        string? localDid = null;
        string? peerDid = null;
        if (forwardLayers == 0 && options.From is not null)
        {
            var entries = ParseRecipientEntries(options.Recipients, nameof(options));
            if (AllNameOneDid(entries))
            {
                var local = DidSubject.DidSubjectOf(options.From);
                var peer = DidSubject.DidSubjectOf(entries[0].Did);
                if (local is not null && peer is not null)
                {
                    localDid = local;
                    peerDid = peer;
                }
            }
        }

        // JC-3/JC-5: an in-band response is read only when this send asked for one and can verify it
        // (an authcrypt send with a sending DID to compare against).
        var expectsReturned = asksReturn && localDid is not null;

        var payload = Encoding.UTF8.GetBytes(packed.Message);
        var request = new TransportRequest(endpointUri, payload, ForwardConstants.PayloadMediaType)
        {
            ReturnRoute = effectiveMode,
            LocalDid = localDid,
            PeerDid = peerDid,
            MaxResponseBytes = expectsReturned ? _options.MaxReceiveBytes : null,
        };
        var transportResult = await _transportRouter.SendAsync(request, ct).ConfigureAwait(false);

        // A payload on any other send is ignored: never unpacked, never resolved against.
        var returned = expectsReturned
            ? await EvaluateReturnedAsync(outgoing.Id, transportResult, endpointUri, peerDid!, localDid!, ct).ConfigureAwait(false)
            : null;
        return new SendResult(packed, transportResult, endpointUri) { Returned = returned };
    }

    /// <summary>
    /// The message to pack for <see cref="SendAsync"/> (JC-15): the caller's own instance when the
    /// send changes nothing, otherwise a snapshot (serialize → deserialize) with the
    /// <see cref="SendOptions.ReturnRoute"/> header written and <c>created_time</c> stamped when
    /// the snapshot falls under FR-RR-09. The caller's message is never mutated.
    /// </summary>
    /// <param name="message">The caller's message.</param>
    /// <param name="options">The send options (already shape-checked).</param>
    private Message PrepareOutgoing(Message message, SendOptions options)
    {
        if (options.ReturnRoute is null && !(message.CreatedTime is null && OutgoingCreatedTime.IsInScope(message)))
            return message;

        Message snapshot;
        try
        {
            var json = JsonSerializer.Serialize(message, DidCommJson.Default);
            snapshot = JsonSerializer.Deserialize<Message>(json, DidCommJson.Default)
                ?? throw new JsonException("The message deserialized to null.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ArgumentException("The message could not be copied for sending (its headers or body do not serialize).", nameof(message), ex);
        }

        if (options.ReturnRoute is { } mode)
            ReturnRouteHeaderWriter.Write(snapshot, mode, options.ReturnRouteThread, nameof(options), nameof(options));
        // Re-evaluated on the snapshot, so an overriding header decides the scope.
        OutgoingCreatedTime.StampIfInScope(snapshot, _options.Now());
        return snapshot;
    }

    /// <summary>
    /// Turn the transport's in-band response to an <c>all</c> / <c>thread</c> send into
    /// <see cref="SendResult.Returned"/> (FR-RR-07): a discarded body or one that does not unpack
    /// fails the send with <see cref="TransportException"/> (status as reported, normally 200); a
    /// message that fails the identity rule is logged by the request's id and the reason and
    /// dropped; anything else is returned as unpacked. No dispatch and no replay admission: the
    /// caller asked for it.
    /// </summary>
    /// <param name="requestId">The id of the message that was sent (never a wire value).</param>
    /// <param name="transportResult">The transport's result.</param>
    /// <param name="endpoint">The endpoint used, for the exception's scheme.</param>
    /// <param name="peerDid">The DID subject the send addressed.</param>
    /// <param name="localDid">The DID subject that sent it.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<UnpackResult?> EvaluateReturnedAsync(
        string requestId, TransportResult transportResult, Uri endpoint, string peerDid, string localDid, CancellationToken ct)
    {
        if (transportResult.ResponseDiscarded)
        {
            throw new TransportException(
                $"The request was accepted (HTTP {transportResult.HttpStatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "status unknown"}), but the returned message exceeded DidCommOptions.MaxReceiveBytes or could not be read in time and was discarded (FR-RR-07).",
                httpStatusCode: transportResult.HttpStatusCode,
                scheme: endpoint.Scheme);
        }

        if (transportResult.ResponsePayload is not { IsEmpty: false } payload)
            return null;

        UnpackResult unpacked;
        try
        {
            unpacked = await UnpackAsync(Encoding.UTF8.GetString(payload.Span), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // The outer text names no DID; the typed inner exception keeps the FR-API-07 detail.
            throw new TransportException(
                "The request was accepted, but the returned message could not be unpacked (FR-RR-07, FR-API-07).",
                ex,
                transportResult.HttpStatusCode,
                endpoint.Scheme);
        }

        if (!ReturnRouteIdentity.IsFromPeerToLocal(unpacked, peerDid, localDid, out var refusal))
        {
            LogReturnedDiscarded(_logger, requestId, refusal.ToString());
            return null;
        }
        return unpacked;
    }

    /// <summary>
    /// Deliver bytes that are already packed to <paramref name="endpoint"/> through the registered
    /// <see cref="ITransportRouter"/> (FR-TRN-01), for a caller that packed or received the envelope
    /// itself, such as a mediator relaying a forwarded message (FR-MED-01). Nothing is packed,
    /// resolved or read back: the request carries no return-route fields, so no transport reads a
    /// response body and no return-route connection is used.
    /// </summary>
    /// <remarks>
    /// The SSRF guard (<see cref="DidCommOptions.OutboundEndpointPolicy"/>) is always applied to
    /// <paramref name="endpoint"/>, because a relay target comes from a DID document or a wire
    /// message. The queue URI <c>didcomm:transport/queue</c> is refused (FR-RR-06). Failures are
    /// recorded on the same span and logged the same way as <see cref="SendAsync"/>.
    /// </remarks>
    /// <param name="packed">The packed envelope bytes (UTF-8 JSON); must not be empty.</param>
    /// <param name="endpoint">The absolute transport URI to deliver to.</param>
    /// <param name="mediaType">The envelope's DIDComm media type: one of <see cref="MediaTypes.Encrypted"/>, <see cref="MediaTypes.Signed"/> or <see cref="MediaTypes.Plaintext"/> (the <c>application/</c> prefix may be omitted, FR-MSG-06).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The transport's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> or <paramref name="mediaType"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="packed"/> is empty, <paramref name="endpoint"/> is not absolute, or <paramref name="mediaType"/> is not a DIDComm media type.</exception>
    /// <exception cref="InvalidOperationException">No <see cref="ITransportRouter"/> is wired in.</exception>
    /// <exception cref="TransportException"><paramref name="endpoint"/> is the queue URI or a blocked destination, or the transport failed.</exception>
    public async Task<TransportResult> SendPackedAsync(ReadOnlyMemory<byte> packed, Uri endpoint, string mediaType, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(mediaType);
        using var activity = DidCommDiagnostics.Source.StartActivity(DidCommDiagnostics.SendActivity);
        try
        {
            if (packed.IsEmpty)
                throw new ArgumentException("The packed message is empty.", nameof(packed));
            if (!endpoint.IsAbsoluteUri)
                throw new ArgumentException("The endpoint must be an absolute URI.", nameof(endpoint));
            var canonicalType = CanonicalDidCommMediaType(mediaType)
                ?? throw new ArgumentException(
                    $"The media type must be a DIDComm media type: {MediaTypes.Encrypted}, {MediaTypes.Signed} or {MediaTypes.Plaintext} (FR-TRN-02).",
                    nameof(mediaType));
            if (_transportRouter is null)
            {
                throw new InvalidOperationException(
                    "SendPackedAsync requires an ITransportRouter. Register a transport via builder.UseHttpTransport()/UseWebSocketTransport() or pass one to the (secrets, keyService, serviceResolver, transportRouter, options) constructor (FR-TRN-01).");
            }
            if (QueueTransport.IsQueueTransport(endpoint))
                throw new TransportException(QueueTransport.RefusalMessage, httpStatusCode: null, scheme: "didcomm");

            // Always: a relay target is attacker-influenced (JC-20).
            new OutboundEndpointGuard(_options.OutboundEndpointPolicy).Validate(endpoint);

            // A bounded vocabulary (https/wss/...), never the endpoint host or path (NFR-04).
            activity?.SetTag(DidCommDiagnostics.TransportSchemeTag, endpoint.Scheme);
            return await _transportRouter.SendAsync(new TransportRequest(endpoint, packed, canonicalType), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DidCommDiagnostics.RecordFailure(activity, ex);
            if (ex is not OperationCanceledException)
                LogSendFailed(_logger, ex.GetType().Name);
            throw;
        }
    }

    // The canonical constant a DIDComm media type matches (FR-MSG-06 normalization), or null.
    private static string? CanonicalDidCommMediaType(string mediaType)
        => MediaTypes.Matches(mediaType, MediaTypes.Encrypted) ? MediaTypes.Encrypted
            : MediaTypes.Matches(mediaType, MediaTypes.Signed) ? MediaTypes.Signed
            : MediaTypes.Matches(mediaType, MediaTypes.Plaintext) ? MediaTypes.Plaintext
            : null;

    /// <summary>The facade's options, for the library's own receive and session paths (the FR-RR-09 stamping clock).</summary>
    internal DidCommOptions Options => _options;

    /// <summary>
    /// The refusal of a return-route send to a routed recipient (FR-RR-02, FR-RR-07). Names no DID.
    /// </summary>
    internal const string RoutedReturnRouteRefusal =
        "A return_route of 'all' or 'thread' is not valid for a recipient reached through routing keys or a mediator: the return_route extension applies only between directly connected agents (FR-RR-02, FR-RR-07). Send without it, or to the recipient's direct endpoint (ServiceEndpointOverride).";

    /// <summary>
    /// Unpack <paramref name="packed"/>, auto-detecting the envelope shape (plaintext / signed
    /// / encrypted) and recursively unwrapping nested compositions per FR-API-03. Enforces
    /// FR-API-05 expiry, FR-API-06 size limit, FR-DID-06 did:web rejection, and the
    /// FR-CONSIST-01..06 addressing-consistency rules (FR-CONSIST-06 is resolver-backed;
    /// FR-CONSIST-04 is advisory — evaluated against <see cref="DidCommOptions.OwnIdentifiers"/>
    /// and the decrypting kid, surfaced as <see cref="UnpackResult.RecipientAddressing"/>, and
    /// never rejects the message).
    /// </summary>
    /// <remarks>
    /// <strong>Support boundary:</strong> the recipient (secret-key) decrypt path runs natively async
    /// as of 1.1.0 — ECDH key agreement flows through opaque-or-extractable <c>IEcdhKey</c> handles
    /// (FR-SEC-06), so the consumer-supplied <see cref="ISecretsResolver"/> is now <c>await</c>ed
    /// directly (no sync-over-async bridge, no deadlock risk under a captured
    /// <see cref="System.Threading.SynchronizationContext"/>). The remaining sync-over-async at the
    /// seam is confined to <em>public-key</em> DID resolution (<see cref="IDidKeyService"/>) feeding
    /// DataProofs' synchronous JWS-verify / sender-key contracts; that is safe in hosts with no
    /// synchronization context — ASP.NET Core, console, generic-host worker services (the supported
    /// targets) — and, as before, such a host should still ensure resolver implementations use
    /// <c>ConfigureAwait(false)</c>.
    /// </remarks>
    /// <param name="packed">The packed DIDComm message.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<UnpackResult> UnpackAsync(string packed, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packed);
        using var activity = DidCommDiagnostics.Source.StartActivity(DidCommDiagnostics.UnpackActivity);
        try
        {
            var result = await UnpackCoreAsync(packed, ct).ConfigureAwait(false);
            if (activity is not null)
            {
                // Tags are only knowable after the envelope is opened; all header-level (NFR-04).
                activity.SetTag(DidCommDiagnostics.MessageTypeTag, result.Message.Type);
                if (result.KeyWrap is not null)
                    activity.SetTag(DidCommDiagnostics.AlgTag, result.KeyWrap);
                if (result.ContentEncryption is not null)
                    activity.SetTag(DidCommDiagnostics.EncTag, result.ContentEncryption);
                if (result.AllRecipientKids is { Count: > 0 } kids)
                    activity.SetTag(DidCommDiagnostics.RecipientsTag, kids.Count);
            }
            return result;
        }
        catch (Exception ex)
        {
            DidCommDiagnostics.RecordFailure(activity, ex);
            if (ex is not OperationCanceledException)
                LogUnpackFailed(_logger, ex.GetType().Name);
            throw;
        }
    }

    // The un-instrumented body of UnpackAsync; the public wrapper above owns the span and
    // failure log.
    private async Task<UnpackResult> UnpackCoreAsync(string packed, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(packed) > _options.MaxReceiveBytes)
        {
            throw new MalformedMessageException(
                $"Packed message exceeds DidCommOptions.MaxReceiveBytes (FR-API-06). Limit={_options.MaxReceiveBytes} bytes.");
        }

        ct.ThrowIfCancellationRequested();

        // Per-unpack key-binding context (#56): when the registered key service can project
        // same-document bindings (IDidKeyBindingService — NetDidKeyService does), the sender/signer
        // lookups below capture the exact binding each JOSE key came from, and the envelope layer
        // authorizes identity against that captured evidence instead of re-resolving (which could
        // observe a different document version). The legacy resolver predicate remains for custom
        // key services without the capability. The context lives and dies with this call — no
        // static, AsyncLocal, or cross-message state.
        var bindingContext = new UnpackKeyBindingContext(_keyService);
        var senderLookup = bindingContext.SenderLookup;
        var signerLookup = bindingContext.SignerLookup;
        var resolverCheck = BuildResolverAuthorizationPredicate();

        // The recipient (secret-key) path is now fully async: ECDH key agreement runs through the
        // KeyOperationResolver's opaque-or-extractable IEcdhKey handles, so the pre-1.1.0
        // SyncSecretsAdapter sync-over-async bridge is gone. Sender/signer lookups stay sync — they are
        // public-key DID resolutions and DataProofs' IJweSenderKeyResolver / JwsParser contracts for
        // them are synchronous (not a secret-key path).
        var internalResult = await EnvelopeReader.UnpackAsync(
            packed, _keyOps, senderLookup, signerLookup, _cryptoProvider, resolverCheck, bindingContext,
            _ownDidSubjects, ct).ConfigureAwait(false);
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
        if (message.FromPrior is not null)
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
            // carrying envelope to actually authenticate the sender. The relationship-termination form
            // (FR-ROT-06) rides WITHOUT 'from' — there is no post-rotation identity to bind, and its
            // authorization is the JWT's own signature by the prior DID's authentication key (verified
            // below), so the envelope-authentication requirement does not apply to it.
            if (message.From is not null && !internalResult.Authenticated)
            {
                throw new ConsistencyException(
                    "Message carries 'from_prior' but the sender is not authenticated (anoncrypt). A rotation " +
                    "assertion MUST ride on an authcrypt or signed envelope (FR-ROT-03). Drop.");
            }
            // With 'from' absent this validates the termination form: the JWT MUST omit 'sub'
            // (FR-ROT-06), and a rotation-shaped JWT on a from-less message is rejected inside.
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
            FromPrior: fromPrior)
        {
            SenderKeyBinding = internalResult.SenderKeyBinding,
            SignerKeyBinding = internalResult.SignerKeyBinding,
            RecipientKeyBinding = internalResult.RecipientKeyBinding,
            RecipientAddressing = internalResult.RecipientAddressing,
        };
    }

    private async Task<DpSig.JwsSigner> PickSignerAsync(string signerDid, CancellationToken ct)
    {
        var pubs = await _keyService.GetVerificationMethodsAsync(signerDid, VerificationRelationship.Authentication, ct).ConfigureAwait(false);
        if (pubs.Count == 0)
            throw new DidResolutionException(signerDid, "no authentication keys available");

        // Keys whose kid is over the FR-API-09 identifier limits are left out before the keystore is
        // asked (#80): every didcomm-dotnet receiver refuses such a signer kid, so signing with one
        // would only produce an undeliverable message.
        var (candidateKids, overLimit) = OwnKidsWithinLimits(pubs);
        var presentKids = await _keyOps.FindPresentAsync(candidateKids, ct).ConfigureAwait(false);
        // ISecretsResolver promises a subset of what it was asked. Take the resolver's first answer
        // that we asked about, so a resolver that lists everything it holds cannot bring back a key
        // left out above.
        var candidates = new HashSet<string>(candidateKids, StringComparer.Ordinal);
        var kid = presentKids.FirstOrDefault(candidates.Contains);
        if (kid is null)
        {
            throw overLimit == 0
                ? new SecretNotFoundException($"{signerDid} (no authentication key held)")
                : new SecretNotFoundException(signerDid,
                    $"No usable signing key for '{signerDid}': {overLimit} of its {pubs.Count} authentication keys have kids over the FR-API-09 identifier limits and were not considered, and no remaining key is held.");
        }

        // Opaque (keystore) or extractable JWS signer for the chosen authentication key (FR-SEC-06).
        return await _keyOps.ResolveSignerAsync(kid, ct).ConfigureAwait(false)
            ?? throw new SecretNotFoundException(kid);
    }

    /// <summary>
    /// The kids of this agent's own published keys that a receiver will accept, in document order,
    /// and how many were left out for being over the FR-API-09 identifier limits (#80). Keys with no
    /// kid are skipped, as before. Used for the signer and authcrypt-sender selection only; a
    /// recipient's kids are the recipient's business and are packed unchanged.
    /// </summary>
    private static (string[] Kids, int OverLimit) OwnKidsWithinLimits(IReadOnlyList<Jwk> keys)
    {
        var kids = new List<string>(keys.Count);
        var overLimit = 0;
        foreach (var key in keys)
        {
            if (key.Kid is null)
                continue;
            if (IdentifierLimits.IsWithinKeyIdLimits(key.Kid))
                kids.Add(key.Kid);
            else
                overLimit++;
        }
        return (kids.ToArray(), overLimit);
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

    /// <summary>
    /// FR-SIG-05 advisory (compile-time generated via <see cref="LoggerMessageAttribute"/> —
    /// the logging pattern this repo is standardizing on): a standalone signed message SHOULD
    /// carry <c>to</c>; without it, anyone holding the envelope can forward it to an audience
    /// the signer never addressed.
    /// </summary>
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Standalone signed message '{MessageId}' has no 'to' header. DIDComm v2.1 SHOULD include 'to' on standalone signed messages (FR-SIG-05) to limit surreptitious forwarding.")]
    private static partial void LogStandaloneSignedMissingTo(ILogger logger, string messageId);

    /// <summary>
    /// Pack-failure breadcrumb (NFR-06). Debug level: the exception itself propagates to the
    /// caller (FR-API-07 taxonomy) and the span already records error status, so this exists
    /// for log-only hosts. Only the envelope form and exception <em>type</em> name are logged —
    /// exception messages can embed header contents and are excluded (NFR-04).
    /// </summary>
    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Pack ({Form}) failed with {ErrorType}.")]
    private static partial void LogPackFailed(ILogger logger, string form, string errorType);

    /// <summary>Unpack-failure breadcrumb (NFR-06); same redaction rules as <see cref="LogPackFailed"/>.</summary>
    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Debug,
        Message = "Unpack failed with {ErrorType}.")]
    private static partial void LogUnpackFailed(ILogger logger, string errorType);

    /// <summary>
    /// Send-failure breadcrumb (NFR-06). Warning level — a send failure is operationally
    /// actionable (network / endpoint / routing) in a way pack/unpack faults are not. The
    /// endpoint itself is NOT logged: it can come from a DID document and identify a peer.
    /// </summary>
    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "Send failed with {ErrorType}.")]
    private static partial void LogSendFailed(ILogger logger, string errorType);

    /// <summary>
    /// FR-ENC-19 breadcrumb: some of a recipient's keyAgreement keys were not encrypted to, because
    /// they are on a curve other than the one every recipient (and an authcrypt sender) shares, or
    /// because the DID publishes more than <see cref="RecipientKeySelection.MaxKeysPerDid"/>. A
    /// device holding only such a key cannot decrypt the message. Debug level, and counts and the
    /// curve name only: never a DID or kid (NFR-04).
    /// </summary>
    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Debug,
        Message = "Encrypted to {RecipientKeyCount} recipient keys on {Curve}; left out {OffCurveCount} keyAgreement keys on other curves and {OverCapCount} over the allowance of {MaxKeysPerDid} automatically selected keys per recipient DID (FR-ENC-19).")]
    private static partial void LogRecipientKeysSkipped(ILogger logger, int recipientKeyCount, string curve, int offCurveCount, int overCapCount, int maxKeysPerDid);

    /// <summary>
    /// A message returned for a return-route send was discarded because it failed the identity rule
    /// (FR-RR-07, JC-6): not authcrypt, or not from the addressed DID to the sending DID. Warning
    /// level; the line carries the id of the message the caller sent and the reason name only, never
    /// a DID, key id or anything from the returned bytes (NFR-04).
    /// </summary>
    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "The message returned for {MessageId} was discarded: {Refusal} (FR-RR-07).")]
    private static partial void LogReturnedDiscarded(ILogger logger, string messageId, string refusal);

    /// <summary>
    /// The JOSE identifier for a content-encryption choice, for span tags only. Unlike
    /// <see cref="MapContentEncryption"/> this never throws — a forbidden combination must
    /// still tag the span before the core path rejects it.
    /// </summary>
    private static string EncTagValue(ContentEncryptionAlgorithm enc) => enc switch
    {
        ContentEncryptionAlgorithm.A256CbcHs512 => JoseAlgorithms.A256CbcHs512,
        ContentEncryptionAlgorithm.A256Gcm => JoseAlgorithms.A256Gcm,
        ContentEncryptionAlgorithm.XC20P => JoseAlgorithms.XC20P,
        _ => enc.ToString(),
    };

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
    /// Build the FR-CONSIST-06 resolver-backed authorization predicate. Now that the unpack pipeline is
    /// async end-to-end, the predicate calls <see cref="IDidKeyService.IsKeyAuthorizedAsync"/> natively
    /// (no sync-over-async), warm against the <c>CachingDidResolver</c> for resolvers wrapped in
    /// net-did's DI builder.
    /// </summary>
    private Func<string, string, string, CancellationToken, Task<bool>> BuildResolverAuthorizationPredicate()
    {
        var keyService = _keyService;
        return (assertedDid, kid, relationship, ct) =>
        {
            var rel = string.Equals(relationship, "authentication", StringComparison.Ordinal)
                ? VerificationRelationship.Authentication
                : VerificationRelationship.KeyAgreement;
            return keyService.IsKeyAuthorizedAsync(assertedDid, kid, rel, ct);
        };
    }

    // One PackEncryptedOptions.Recipients entry: the DID it names and, for a DID URL, the key id it
    // pins (the whole entry). A bare DID has no pin and stands for all of its keys.
    private readonly record struct RecipientEntry(string Did, string? PinnedKid);

    /// <summary>
    /// Split a <see cref="PackEncryptedOptions.Recipients"/> entry into its DID and, for a DID URL
    /// with a fragment, the key id it pins (FR-ENC-19). A linear scan for the first DID URL
    /// delimiter; no regular expression or DID parse runs here.
    /// </summary>
    /// <remarks>
    /// A pin names a key by its absolute key id, exactly as the recipient's DID document gives it,
    /// so a path, query, parameters, an empty fragment or a relative reference are refused rather
    /// than guessed at. An over-long entry is refused first and without quoting it (FR-API-09, #80).
    /// </remarks>
    /// <param name="entry">The entry as the caller wrote it.</param>
    /// <param name="paramName">The caller's parameter name, for the exception.</param>
    private static RecipientEntry ParseRecipientEntry(string? entry, string paramName)
    {
        if (string.IsNullOrEmpty(entry))
            throw new ArgumentException("Recipients contains a null or empty entry.", paramName);

        if (entry.Length > IdentifierLimits.MaxKeyIdLength)
        {
            throw new ArgumentException(
                $"Recipients contains an entry of {entry.Length} characters, which exceeds the maximum DID URL length of {IdentifierLimits.MaxKeyIdLength} characters (FR-API-09).",
                paramName);
        }

        var didLength = IdentifierLimits.DidPartLength(entry);
        if (didLength == entry.Length)
            return new RecipientEntry(entry, PinnedKid: null);

        if (didLength == 0 || entry[didLength] != '#' || didLength == entry.Length - 1)
        {
            throw new ArgumentException(
                $"Recipients entry '{entry}' is neither a DID nor a DID URL of the form did#fragment. A DID URL pins one keyAgreement key by its absolute key id; a path, query, parameters, an empty fragment or a relative reference are not supported (FR-ENC-19).",
                paramName);
        }

        return new RecipientEntry(entry[..didLength], PinnedKid: entry);
    }

    // Every entry of a recipient list, parsed (FR-ENC-19); throws ArgumentException for a malformed one.
    private static RecipientEntry[] ParseRecipientEntries(IReadOnlyList<string> recipients, string paramName)
    {
        var entries = new RecipientEntry[recipients.Count];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = ParseRecipientEntry(recipients[i], paramName);
        return entries;
    }

    private static bool AllNameOneDid(RecipientEntry[] entries)
    {
        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Did, entries[0].Did, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>
    /// An entry's candidate keys: every addressable key of a bare DID, or exactly the pinned key,
    /// which must be one of the DID's keyAgreement keys.
    /// </summary>
    /// <param name="entry">The parsed entry.</param>
    /// <param name="addressable">The entry DID's addressable keyAgreement keys.</param>
    private static IReadOnlyList<Jwk> CandidatesOf(RecipientEntry entry, IReadOnlyList<Jwk> addressable)
    {
        if (entry.PinnedKid is null)
            return addressable;

        foreach (var key in addressable)
        {
            if (string.Equals(key.Kid, entry.PinnedKid, StringComparison.Ordinal))
                return new[] { key };
        }
        throw new DidResolutionException(entry.Did, $"'{entry.PinnedKid}' is not one of its keyAgreement keys");
    }

    /// <summary>
    /// The envelope's recipient keys (FR-ENC-19): for each entry in order, its candidates on the
    /// negotiated curve in document order, each kid once across the whole envelope. A bare DID
    /// contributes at most <see cref="RecipientKeySelection.MaxKeysPerDid"/> automatically selected
    /// keys; a key the caller pinned by DID URL anywhere in the list is taken as given and never
    /// counts toward that allowance, so the set does not depend on whether the pin or the bare DID
    /// comes first. A repeated bare DID adds nothing. When a bare DID had keys left out for being on
    /// another curve or over the allowance, a Debug log says how many, with counts only (NFR-04).
    /// </summary>
    /// <param name="entries">The parsed recipient entries.</param>
    /// <param name="entryKeys">Each entry's candidate keys, index-aligned with <paramref name="entries"/>.</param>
    /// <param name="curve">The negotiated curve.</param>
    private List<Jwk> SelectRecipientKeys(RecipientEntry[] entries, IReadOnlyList<Jwk>[] entryKeys, string curve)
    {
        // A pinned kid is exempt from the bare-DID allowance wherever the pin appears, so a bare
        // entry that comes before the pin must know about it too. Otherwise the pin's key would use
        // up one of the 64 and the set would depend on the caller's ordering (#83 review).
        var pinnedKids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.PinnedKid is not null)
                pinnedKids.Add(entry.PinnedKid);
        }

        var chosen = new List<Jwk>();
        var seenKids = new HashSet<string>(StringComparer.Ordinal);
        var seenBareDids = new HashSet<string>(StringComparer.Ordinal);
        var offCurve = 0;
        var overCap = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            var bare = entries[i].PinnedKid is null;
            if (bare && !seenBareDids.Add(entries[i].Did))
                continue;

            var taken = 0;
            foreach (var key in entryKeys[i])
            {
                if (!string.Equals(key.Crv, curve, StringComparison.Ordinal))
                {
                    offCurve++;
                    continue;
                }
                if (seenKids.Contains(key.Kid!))
                    continue;
                if (bare && !pinnedKids.Contains(key.Kid!))
                {
                    if (taken == RecipientKeySelection.MaxKeysPerDid)
                    {
                        overCap++;
                        continue;
                    }
                    taken++;
                }
                seenKids.Add(key.Kid!);
                chosen.Add(key);
            }
        }

        if (offCurve > 0 || overCap > 0)
            LogRecipientKeysSkipped(_logger, chosen.Count, curve, offCurve, overCap, RecipientKeySelection.MaxKeysPerDid);
        return chosen;
    }
}
