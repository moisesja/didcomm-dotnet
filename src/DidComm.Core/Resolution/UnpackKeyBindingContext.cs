using DidComm.Secrets;

namespace DidComm.Resolution;

/// <summary>
/// Per-unpack key-lookup context (#56). One instance is created inside each
/// <see cref="Facade.DidCommClient.UnpackAsync"/> call; it serves the envelope layer's sender and
/// signer public-key lookups and — when the registered <see cref="IDidKeyService"/> implements
/// <see cref="IDidKeyBindingService"/> — captures the exact <see cref="ResolvedKeyBinding"/>
/// returned for each kid, so the envelope layer can later authorize sender/signer identity against
/// the very evidence the crypto used instead of re-resolving (the TOCTOU the two-resolution design
/// allowed).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately no statics, no <c>AsyncLocal</c>, no TTL/eviction, and no cross-message state:
/// the instance's lifetime is exactly one unpack operation, so concurrent unpacks — even for the
/// same kid — hold independent evidence and cannot consume or overwrite each other's.
/// </para>
/// <para>
/// Captures are memoized per (kid, relationship): if the envelope layer asks for the same kid
/// twice within one operation it receives the same JWK instance, resolved once — key material
/// cannot shift mid-operation. Every lookup is awaited with the unpack's cancellation token,
/// before the JOSE layer's synchronous callbacks run; those callbacks answer from the result (see
/// <c>EnvelopeReader</c>).
/// </para>
/// <para>
/// When the key service lacks the capability, the lookups delegate to the legacy
/// <see cref="DidKeyServiceLookups"/> behavior and no bindings are captured.
/// </para>
/// </remarks>
internal sealed class UnpackKeyBindingContext
{
    private readonly IDidKeyBindingService? _bindingService;
    private readonly Dictionary<(string Kid, VerificationRelationship Relationship), ResolvedKeyBinding?> _captured = new();
    private readonly object _gate = new();

    public UnpackKeyBindingContext(IDidKeyService keyService)
    {
        ArgumentNullException.ThrowIfNull(keyService);
        _bindingService = keyService as IDidKeyBindingService;
        if (_bindingService is null)
        {
            SenderLookup = DidKeyServiceLookups.SenderKeyLookup(keyService);
            SignerLookup = DidKeyServiceLookups.SignerKeyLookup(keyService);
        }
        else
        {
            SenderLookup = new CapturingSenderLookup(this);
            SignerLookup = async (kid, ct) => (await CaptureAsync(kid, VerificationRelationship.Authentication, ct).ConfigureAwait(false))?.PublicJwk;
        }
    }

    /// <summary>True when the registered key service can supply same-document bindings.</summary>
    public bool ProvenanceCapable => _bindingService is not null;

    /// <summary>Authcrypt sender public-key lookup for the JWE layer (captures on the capability path).</summary>
    public IInternalSenderKeyLookup SenderLookup { get; }

    /// <summary>Signer public-key lookup for the JWS layer (captures on the capability path).</summary>
    public Func<string, CancellationToken, ValueTask<Jwk?>> SignerLookup { get; }

    /// <summary>
    /// The binding captured for (<paramref name="kid"/>, <paramref name="relationship"/>) during
    /// this operation's key lookups, or null when none was captured.
    /// </summary>
    public ResolvedKeyBinding? GetCaptured(string kid, VerificationRelationship relationship)
    {
        lock (_gate)
        {
            return _captured.TryGetValue((kid, relationship), out var binding) ? binding : null;
        }
    }

    /// <summary>
    /// Resolve and capture the recipient binding once, after the decrypting recipient kid is
    /// known. Capability path only.
    /// </summary>
    public async Task<ResolvedKeyBinding?> CaptureRecipientBindingAsync(string kid, CancellationToken ct)
        => _bindingService is null ? null : await CaptureAsync(kid, VerificationRelationship.KeyAgreement, ct).ConfigureAwait(false);

    /// <summary>
    /// The binding for (<paramref name="kid"/>, <paramref name="relationship"/>): the one already
    /// captured by this operation, or one resolution with <paramref name="ct"/>, captured.
    /// Capability path only.
    /// </summary>
    private async ValueTask<ResolvedKeyBinding?> CaptureAsync(string kid, VerificationRelationship relationship, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_captured.TryGetValue((kid, relationship), out var existing))
                return existing;
        }

        var binding = await _bindingService!.ResolveKeyBindingAsync(kid, relationship, ct).ConfigureAwait(false);
        return Store(kid, relationship, binding);
    }

    private ResolvedKeyBinding? Store(string kid, VerificationRelationship relationship, ResolvedKeyBinding? binding)
    {
        lock (_gate)
        {
            // First capture wins: if a concurrent lookup for the same kid raced this one, keep
            // the evidence the earlier caller may already have handed to the JOSE layer.
            if (_captured.TryGetValue((kid, relationship), out var existing))
                return existing;
            _captured[(kid, relationship)] = binding;
            return binding;
        }
    }

    private sealed class CapturingSenderLookup : IInternalSenderKeyLookup
    {
        private readonly UnpackKeyBindingContext _context;
        public CapturingSenderLookup(UnpackKeyBindingContext context) => _context = context;

        public async ValueTask<Jwk?> ResolveAsync(string skid, CancellationToken ct)
            => (await _context.CaptureAsync(skid, VerificationRelationship.KeyAgreement, ct).ConfigureAwait(false))?.PublicJwk;
    }
}
