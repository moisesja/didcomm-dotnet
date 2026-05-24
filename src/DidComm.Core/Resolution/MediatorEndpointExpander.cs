using DidComm.Exceptions;
using DidComm.Jose;

namespace DidComm.Resolution;

/// <summary>
/// Takes a recipient DID's <see cref="DidCommServiceInfo"/> candidates, picks the preferred
/// entry, expands a DID-as-endpoint reference (FR-ROUTE-04) into a transport URI plus the
/// mediator's <c>keyAgreement</c> key prepended onto the routing-key list, and materialises
/// every routing key into a public JWK. Result: a fully-resolved
/// <see cref="ResolvedRoute"/> the sender wrapping algorithm can drive without further DID
/// resolution.
/// </summary>
/// <remarks>
/// <para>
/// FR-ROUTE-04 mandates the prepend behavior and forbids recursive endpoint resolution: a
/// mediator's own service entry MUST publish a plain transport URI, not another DID. The
/// expander enforces that — a second DID-as-uri hop throws
/// <see cref="ConsistencyException"/> rather than recursing.
/// </para>
/// <para>
/// When multiple mediator <c>keyAgreement</c> keys exist, only the FIRST is prepended. The
/// spec text reads "the keyAgreement keys of the mediator are implicitly prepended"; the
/// pragmatic interpretation matched by the major reference implementations is to pick the
/// preferred key rather than emit one forward layer per mediator key. If interop fixtures
/// later demand per-key layers we can revisit.
/// </para>
/// </remarks>
internal static class MediatorEndpointExpander
{
    /// <summary>
    /// Expand the first candidate in <paramref name="candidates"/> into a fully-resolved
    /// route. Throws <see cref="DidResolutionException"/> when the candidate list is empty.
    /// </summary>
    /// <param name="candidates">DIDCommMessaging entries from the recipient's document, in preference order. The first is selected; subsequent ones become fallback URIs.</param>
    /// <param name="serviceResolver">Resolver used to dereference a mediator-as-DID-endpoint.</param>
    /// <param name="keyService">Resolver used to materialise the mediator's <c>keyAgreement</c> and each routing-key DID URL into a public JWK.</param>
    /// <param name="recipientDid">The recipient DID — used in error messages and to flag a recursive endpoint loop.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<ResolvedRoute> ExpandAsync(
        IReadOnlyList<DidCommServiceInfo> candidates,
        IServiceEndpointResolver serviceResolver,
        IDidKeyService keyService,
        string recipientDid,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(serviceResolver);
        ArgumentNullException.ThrowIfNull(keyService);
        ArgumentException.ThrowIfNullOrEmpty(recipientDid);

        if (candidates.Count == 0)
            throw new DidResolutionException(recipientDid, "no DIDCommMessaging service entries available");

        var primary = candidates[0];
        var fallbackUris = candidates.Skip(1).Select(c => c.Uri).ToArray();

        string transportUri;
        var combinedRoutingKeyKids = new List<string>();

        if (LooksLikeDid(primary.Uri))
        {
            // FR-ROUTE-04: mediator-as-DID-endpoint. Resolve the mediator's own service entry
            // and prepend its first keyAgreement key.
            var mediatorServices = await serviceResolver.ResolveAsync(primary.Uri, ct).ConfigureAwait(false);
            if (mediatorServices.Count == 0)
                throw new DidResolutionException(primary.Uri, $"mediator-as-endpoint '{primary.Uri}' has no DIDCommMessaging service");

            var mediatorService = mediatorServices[0];
            if (LooksLikeDid(mediatorService.Uri))
            {
                throw new ConsistencyException(
                    $"FR-ROUTE-04: mediator '{primary.Uri}' publishes a DID as its own serviceEndpoint uri ('{mediatorService.Uri}'). Recursive endpoint resolution is forbidden — mediators must use plain transport URIs.");
            }

            transportUri = mediatorService.Uri;

            var mediatorKeys = await keyService.GetVerificationMethodsAsync(primary.Uri, VerificationRelationship.KeyAgreement, ct).ConfigureAwait(false);
            if (mediatorKeys.Count == 0)
                throw new DidResolutionException(primary.Uri, "mediator-as-endpoint has no keyAgreement keys to prepend");

            var primaryKid = mediatorKeys[0].Kid
                ?? throw new DidResolutionException(primary.Uri, "mediator-as-endpoint keyAgreement key has no kid");

            combinedRoutingKeyKids.Add(primaryKid);
            combinedRoutingKeyKids.AddRange(primary.RoutingKeys);
            // Per FR-ROUTE-04 / spec §Using a DID as an endpoint: only the mediator's
            // *keyAgreement* keys are implicitly prepended — the mediator's own routingKeys
            // are intentionally ignored here because they only apply when the mediator is
            // itself the message recipient.
        }
        else
        {
            transportUri = primary.Uri;
            combinedRoutingKeyKids.AddRange(primary.RoutingKeys);
        }

        var routingKeyJwks = await ResolveRoutingKeyJwksAsync(combinedRoutingKeyKids, keyService, ct).ConfigureAwait(false);

        return new ResolvedRoute(transportUri, routingKeyJwks, fallbackUris);
    }

    private static async Task<IReadOnlyList<Jwk>> ResolveRoutingKeyJwksAsync(
        IReadOnlyList<string> kids,
        IDidKeyService keyService,
        CancellationToken ct)
    {
        if (kids.Count == 0)
            return Array.Empty<Jwk>();

        var resolvedKeys = new List<Jwk>(kids.Count);
        foreach (var kid in kids)
        {
            var hashIndex = kid.IndexOf('#');
            if (hashIndex < 0)
                throw new DidResolutionException(kid, "routing key reference is not a DID URL with a fragment");

            var subjectDid = kid[..hashIndex];
            var subjectKeys = await keyService.GetVerificationMethodsAsync(subjectDid, VerificationRelationship.KeyAgreement, ct).ConfigureAwait(false);
            var match = subjectKeys.FirstOrDefault(j => string.Equals(j.Kid, kid, StringComparison.Ordinal))
                ?? throw new DidResolutionException(subjectDid, $"routing key '{kid}' is not declared in keyAgreement");
            resolvedKeys.Add(match);
        }
        return resolvedKeys;
    }

    private static bool LooksLikeDid(string uri) =>
        !string.IsNullOrEmpty(uri) && uri.StartsWith("did:", StringComparison.Ordinal);
}
