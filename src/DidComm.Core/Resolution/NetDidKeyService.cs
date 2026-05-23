using DidComm.Crypto.KeyAgreement;
using DidComm.Exceptions;
using DidComm.Jose;
using NetDid.Core;
using NetDid.Core.Model;
using NetDid.Core.Parsing;

namespace DidComm.Resolution;

/// <summary>
/// <see cref="IDidKeyService"/> implementation that wraps a <see cref="NetDid.Core.IDidResolver"/>.
/// Per FR-DID-01 net-did owns DID resolution; this adapter projects the resolved
/// <c>keyAgreement</c> / <c>authentication</c> relationships into the JWK shape the DIDComm
/// JOSE layer consumes (FR-DID-02 / FR-DID-03).
/// </summary>
/// <remarks>
/// <para>
/// The adapter holds no cache of its own (FR-DID-04 "no double-caching") — callers register a
/// <c>CachingDidResolver</c> via <c>NetDid.Extensions.DependencyInjection</c> when caching is
/// desired.
/// </para>
/// <para>
/// <c>did:web</c> is rejected up front per FR-DID-06 / DD-08 with
/// <see cref="UnsupportedDidMethodException"/>; failures during net-did resolution surface as
/// <see cref="DidResolutionException"/>; off-curve EC points encountered while materialising
/// JWKs throw <see cref="System.Security.Cryptography.CryptographicException"/> through
/// <see cref="JwkConversion.ExtractPublicKey(Jwk)"/> (FR-ENC-03, inherited from net-did's
/// <c>EcPointValidator</c>).
/// </para>
/// </remarks>
public sealed class NetDidKeyService : IDidKeyService
{
    private readonly IDidResolver _resolver;

    /// <summary>Initialise the adapter with the net-did resolver to delegate to.</summary>
    /// <param name="resolver">A composite (or single-method) <see cref="IDidResolver"/>.</param>
    public NetDidKeyService(IDidResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    /// <inheritdoc />
    public void RejectUnsupportedMethod(string did)
    {
        ArgumentException.ThrowIfNullOrEmpty(did);
        var method = DidParser.ExtractMethod(did)
            ?? throw new DidResolutionException(did, "value is not a syntactically valid DID");

        if (string.Equals(method, "web", StringComparison.Ordinal))
        {
            throw new UnsupportedDidMethodException(
                "web",
                did,
                "did:web is intentionally rejected per DD-08 (no verifiable history or pre-rotation defense — see PRD §15). Use did:webvh instead");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(
        string did,
        VerificationRelationship relationship,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(did);
        RejectUnsupportedMethod(did);

        var result = await _resolver.ResolveAsync(did, ct: ct).ConfigureAwait(false);
        if (result.DidDocument is null)
        {
            var error = result.ResolutionMetadata?.Error ?? "resolver returned no document";
            throw new DidResolutionException(did, error);
        }

        var entries = relationship switch
        {
            VerificationRelationship.KeyAgreement => result.DidDocument.KeyAgreement,
            VerificationRelationship.Authentication => result.DidDocument.Authentication,
            _ => throw new ArgumentOutOfRangeException(nameof(relationship), relationship, "Unknown VerificationRelationship."),
        };

        if (entries is null || entries.Count == 0)
            return Array.Empty<Jwk>();

        var keys = new List<Jwk>(entries.Count);
        foreach (var entry in entries)
        {
            var method = ResolveVerificationMethod(result.DidDocument, entry, did);
            var jwk = TryMaterialise(method, relationship);
            if (jwk is not null)
                keys.Add(jwk);
        }

        return keys;
    }

    /// <inheritdoc />
    public async Task<bool> IsKeyAuthorizedAsync(
        string did,
        string kid,
        VerificationRelationship relationship,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(kid);
        var keys = await GetVerificationMethodsAsync(did, relationship, ct).ConfigureAwait(false);
        foreach (var key in keys)
        {
            if (string.Equals(key.Kid, kid, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Dereference a verification-relationship entry: when it's a fragment reference, look the
    /// id up in the document's top-level <c>verificationMethod</c> array; when it's embedded,
    /// return it as-is.
    /// </summary>
    private static VerificationMethod ResolveVerificationMethod(
        DidDocument doc,
        VerificationRelationshipEntry entry,
        string did)
    {
        if (!entry.IsReference)
            return entry.EmbeddedMethod!;

        var reference = entry.Reference!;
        if (doc.VerificationMethod is not null)
        {
            foreach (var vm in doc.VerificationMethod)
            {
                if (string.Equals(vm.Id, reference, StringComparison.Ordinal))
                    return vm;
            }
        }

        throw new DidResolutionException(
            did,
            $"verification-relationship reference '{reference}' is not present in the document's verificationMethod array");
    }

    /// <summary>
    /// Convert a <see cref="VerificationMethod"/> to a DIDComm <see cref="Jwk"/>, filtering out
    /// curves the JOSE layer cannot use for <paramref name="relationship"/>. Returns <c>null</c>
    /// when the method's curve is unsupported (allows mixed-curve documents to still surface
    /// usable keys); throws when the method itself is malformed.
    /// </summary>
    private static Jwk? TryMaterialise(VerificationMethod method, VerificationRelationship relationship)
    {
        if (method.PublicKeyJwk is null)
        {
            // Multibase-only methods are not consumable by the Phase 3 facade.
            return null;
        }

        var crv = method.PublicKeyJwk.Crv;
        if (string.IsNullOrEmpty(crv))
            return null;

        // Filter by relationship: keyAgreement needs an ECDH-usable curve, authentication needs a signing curve.
        if (!IsSupported(crv, relationship))
            return null;

        return new Jwk
        {
            Kty = method.PublicKeyJwk.Kty ?? string.Empty,
            Crv = crv,
            X = method.PublicKeyJwk.X,
            Y = method.PublicKeyJwk.Y,
            Kid = method.Id,
            Alg = method.PublicKeyJwk.Alg,
            Use = method.PublicKeyJwk.Use,
        };
    }

    private static bool IsSupported(string crv, VerificationRelationship relationship)
    {
        try
        {
            _ = relationship == VerificationRelationship.KeyAgreement
                ? KeyTypeMapper.FromCurveForKeyAgreement(crv)
                : KeyTypeMapper.FromCurveForSigning(crv);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
