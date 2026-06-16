using DidComm.Consistency;
using DidComm.Crypto.KeyAgreement;
using DidComm.Exceptions;
using NetDid.Core;
using NetDid.Core.Model;
using NetDid.Core.Parsing;
using DpJwkConversion = DataProofsDotnet.Jose.JwkConversion;

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
/// <see cref="DidResolutionException"/>. Multikey verification methods are materialised via
/// <c>DataProofsDotnet.Jose.JwkConversion.FromMultikey</c>; the invalid-curve / off-curve defense
/// (FR-ENC-03) is applied by NetCrypto when the received ephemeral key is later imported during
/// decrypt (NetCrypto 1.1.0 guarantees the on-curve check in <c>JwkConverter.ExtractPublicKey</c>).
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
            {
                // Per W3C DID Core a VerificationMethod.id MAY be a relative DID URL like
                // "#key-1"; net-did's did:peer:2 resolver emits exactly that shape. The
                // envelope layer keys secrets by absolute DID URL, so normalize here.
                if (jwk.Kid is { } kid && kid.StartsWith('#'))
                    jwk.Kid = did + kid;
                keys.Add(jwk);
            }
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
        ArgumentException.ThrowIfNullOrEmpty(did);
        ArgumentException.ThrowIfNullOrEmpty(kid);
        RejectUnsupportedMethod(did);

        // FR-CONSIST-06 / DD-01 require more than string-matching the kid into the relationship:
        // the verification method must be genuinely *controlled by* the asserted DID. So we walk the
        // raw resolved verification methods (not the curve-projected JWKs, which drop `controller`)
        // and authorize the matching kid only when its id-subject AND its controller are the asserted
        // DID — rejecting a key that sits under the relationship but is controlled by a different DID,
        // or an embedded VM whose id belongs to another DID. (Issue #18.)
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
            return false;

        foreach (var entry in entries)
        {
            var method = ResolveVerificationMethod(result.DidDocument, entry, did);

            // VerificationMethod.id MAY be a relative DID URL ("#key-1"); normalize to absolute so it
            // matches the absolute kid the envelope layer uses and so DidSubjectOf can extract a DID.
            var methodId = method.Id is { } id && id.StartsWith('#') ? did + id : method.Id;
            if (!string.Equals(methodId, kid, StringComparison.Ordinal))
                continue;

            // Found the kid under the right relationship; authorize only if it is controlled by `did`
            // and is usable for the relationship's curve (preserving the prior curve-support filter).
            return IsControlledBy(did, methodId, method)
                && TryMaterialise(method, relationship) is not null;
        }

        return false;
    }

    /// <summary>
    /// FR-CONSIST-06 controller rule: a verification method is authorized for <paramref name="did"/>
    /// only when its (absolute) id resolves to the asserted DID subject AND its <c>controller</c>
    /// (when present) is the asserted DID. An absent controller falls back to the id-subject rule.
    /// </summary>
    private static bool IsControlledBy(string did, string absoluteMethodId, VerificationMethod method)
    {
        if (!string.Equals(DidSubject.DidSubjectOf(absoluteMethodId), did, StringComparison.Ordinal))
            return false; // cross-DID verification-method id

        var controller = method.Controller.Value;
        if (string.IsNullOrEmpty(controller))
            return true; // controller omitted — the id-subject rule above already bound it to `did`

        return string.Equals(DidSubject.DidSubjectOf(controller), did, StringComparison.Ordinal);
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
        var (kty, crv, x, y, alg, use) = ProjectMethod(method);
        if (string.IsNullOrEmpty(crv))
            return null;

        // Filter by relationship: keyAgreement needs an ECDH-usable curve, authentication needs a signing curve.
        if (!IsSupported(crv, relationship))
            return null;

        return new Jwk
        {
            Kty = kty,
            Crv = crv,
            X = x,
            Y = y,
            Kid = method.Id,
            Alg = alg,
            Use = use,
        };
    }

    /// <summary>
    /// Project a <see cref="VerificationMethod"/> onto the JOSE shape the JWK record uses,
    /// supporting both <c>JsonWebKey2020</c> (<see cref="VerificationMethod.PublicKeyJwk"/>)
    /// and <c>Multikey</c> (<see cref="VerificationMethod.PublicKeyMultibase"/>) — the two
    /// representations DIDComm-relevant DID methods ship today.
    /// </summary>
    private static (string Kty, string? Crv, string? X, string? Y, string? Alg, string? Use) ProjectMethod(VerificationMethod method)
    {
        if (method.PublicKeyJwk is not null)
        {
            var jwk = method.PublicKeyJwk;
            return (jwk.Kty ?? string.Empty, jwk.Crv, jwk.X, jwk.Y, jwk.Alg, jwk.Use);
        }

        if (!string.IsNullOrEmpty(method.PublicKeyMultibase))
        {
            try
            {
                // FromMultikey decodes the multibase + multicodec prefix and produces the public JWK
                // (replacing the old NetCid Multibase/Multicodec + NetDid JwkConverter chain).
                var jwk = DpJwkConversion.FromMultikey(method.PublicKeyMultibase, method.Id);
                return (jwk.Kty, jwk.Crv, jwk.X, jwk.Y, jwk.Alg, jwk.Use);
            }
            catch
            {
                // Unknown codec / invalid bytes / off-curve EC point → skip this VM entirely;
                // mixed-curve docs still surface their usable keys.
                return (string.Empty, null, null, null, null, null);
            }
        }

        return (string.Empty, null, null, null, null, null);
    }

    private static bool IsSupported(string crv, VerificationRelationship relationship)
        => relationship == VerificationRelationship.KeyAgreement
            ? KeyTypeMapper.IsKeyAgreementCurve(crv)
            : KeyTypeMapper.IsSigningCurve(crv);
}
