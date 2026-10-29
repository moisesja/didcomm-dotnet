using DidComm.Composition;
using DidComm.Consistency;
using DidComm.Exceptions;
using DidComm.Jose;

namespace DidComm.Resolution;

/// <summary>
/// Takes a recipient DID's <see cref="DidCommServiceInfo"/> candidates, picks the preferred
/// entry, expands a DID-as-endpoint reference (FR-ROUTE-04) into a transport URI plus a forward
/// layer for the mediator prepended onto the recipient's routing keys, and materialises every
/// layer's public JWKs. Result: a fully-resolved <see cref="ExpandedRoute"/> the sender wrapping
/// algorithm can drive without further DID resolution.
/// </summary>
/// <remarks>
/// <para>
/// FR-ROUTE-04 mandates the prepend behavior and forbids recursive endpoint resolution: a
/// mediator's own service entry MUST publish a plain transport URI, not another DID. The
/// expander enforces that — a second DID-as-uri hop throws
/// <see cref="ConsistencyException"/> rather than recursing.
/// </para>
/// <para>
/// A mediator named by its DID gets ONE forward layer, encrypted to all of its keyAgreement keys
/// on one curve (FR-ENC-19; routing.md: "the keyAgreement keys of the mediator are implicitly
/// prepended", and its Example 1 encrypts the forward to "the keyAgreement keys within the
/// mediator DID Document"). That is what didcomm-python and didcomm-jvm do, and it lets the
/// mediator unwrap on any device it runs. The same holds for a routing key given as a bare DID.
/// A routing key given as a DID URL names exactly one key and gets a layer for that key alone.
/// </para>
/// <para>
/// The recipient's document, which a peer controls, decides how many layers there are and what the
/// references say, so the expander bounds both before it resolves any mediator or routing-key DID
/// (the recipient's own keys and service entry, which carries the route, are resolved by the caller
/// before the expander runs): at most
/// <see cref="MaxForwardLayers"/> layers, and each routing-key reference (and a mediator DID) within
/// the FR-API-09 identifier limits, refused without being quoted. Each distinct DID is resolved
/// once per expansion.
/// </para>
/// </remarks>
internal static class MediatorEndpointExpander
{
    /// <summary>
    /// The most forward layers one route may need, the mediator-as-endpoint layer included. Each
    /// layer is a new anoncrypt envelope carrying the whole envelope inside it as base64url, so the
    /// message grows by about a third per layer, compounding, and a layer can address up to
    /// <see cref="RecipientKeySelection.MaxKeysPerDid"/> keys. Real routes use one or two mediators;
    /// 8 leaves room for longer chains and keeps the growth under about tenfold.
    /// </summary>
    internal const int MaxForwardLayers = 8;

    /// <summary>
    /// Expand the first candidate in <paramref name="candidates"/> into a fully-resolved
    /// route. Throws <see cref="DidResolutionException"/> when the candidate list is empty.
    /// </summary>
    /// <param name="candidates">DIDCommMessaging entries from the recipient's document, in preference order. The first is selected; subsequent ones become fallback URIs.</param>
    /// <param name="serviceResolver">Resolver used to dereference a mediator-as-DID-endpoint.</param>
    /// <param name="keyService">Resolver used to materialise the mediator's <c>keyAgreement</c> keys and each routing key into public JWKs.</param>
    /// <param name="recipientDid">The recipient DID — used in error messages and to flag a recursive endpoint loop.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<ExpandedRoute> ExpandAsync(
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
        // FR-ROUTE-08 failover candidates must be usable transport URIs. A DID-as-endpoint
        // fallback would need its own expansion (resolve the mediator, prepend its key); that is
        // out of scope for a flat failover list, so drop DID-valued fallbacks rather than surface
        // a DID where a transport URI is expected.
        var fallbackUris = candidates.Skip(1).Select(c => c.Uri).Where(uri => !LooksLikeDid(uri)).ToArray();

        // Bound the route before resolving anything in it: the layer count, then each reference's
        // length. A refusal names counts only, never a reference from the peer's document.
        var mediatorAsEndpoint = LooksLikeDid(primary.Uri);
        var layerCount = primary.RoutingKeys.Count + (mediatorAsEndpoint ? 1 : 0);
        if (layerCount > MaxForwardLayers)
        {
            throw new DidResolutionException(
                recipientDid,
                $"its DIDCommMessaging service needs {layerCount} forward layers ({primary.RoutingKeys.Count} routing keys{(mediatorAsEndpoint ? " and a mediator named as the endpoint" : "")}), more than the maximum of {MaxForwardLayers}");
        }
        if (mediatorAsEndpoint && !IdentifierLimits.IsWithinKeyIdLimits(primary.Uri))
            throw new DidResolutionException(recipientDid, OverLimitReason("the mediator DID named as its endpoint", primary.Uri));
        foreach (var routingKey in primary.RoutingKeys)
        {
            if (!IdentifierLimits.IsWithinKeyIdLimits(routingKey))
                throw new DidResolutionException(recipientDid, OverLimitReason("a routing key reference", routingKey));
        }

        string transportUri;
        var layers = new List<ForwardLayer>(layerCount);
        // Each distinct DID's keyAgreement keys, so a DID named by several layers is resolved once.
        var keysByDid = new Dictionary<string, IReadOnlyList<Jwk>>(StringComparer.Ordinal);

        if (mediatorAsEndpoint)
        {
            // FR-ROUTE-04: mediator-as-DID-endpoint. Resolve the mediator's own service entry
            // and prepend a layer addressed to its keyAgreement keys.
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

            var mediatorKeys = await KeysOfAsync(primary.Uri, keyService, keysByDid, ct).ConfigureAwait(false);
            if (mediatorKeys.Count == 0)
                throw new DidResolutionException(primary.Uri, "mediator-as-endpoint has no keyAgreement keys to prepend");

            layers.Add(new ForwardLayer(
                primary.Uri,
                RecipientKeySelection.AllOnPreferredCurve(primary.Uri, mediatorKeys, "mediator-as-endpoint")));
            // Per FR-ROUTE-04 / spec §Using a DID as an endpoint: only the mediator's
            // *keyAgreement* keys are implicitly prepended — the mediator's own routingKeys
            // are intentionally ignored here because they only apply when the mediator is
            // itself the message recipient.
        }
        else
        {
            transportUri = primary.Uri;
        }

        foreach (var routingKey in primary.RoutingKeys)
            layers.Add(await ResolveRoutingKeyLayerAsync(routingKey, keyService, keysByDid, ct).ConfigureAwait(false));

        return new ExpandedRoute(transportUri, layers, fallbackUris);
    }

    /// <summary>
    /// The forward layer for one <c>routingKeys</c> entry. A DID URL with a fragment names one key,
    /// which must be among its DID's keyAgreement keys. A bare DID stands for that mediator: every
    /// keyAgreement key it publishes on one curve, as for a mediator-as-endpoint.
    /// </summary>
    /// <param name="reference">The routing-key entry as the recipient's document gives it, within the FR-API-09 identifier limits.</param>
    /// <param name="keyService">Resolver for the routing key's DID.</param>
    /// <param name="keysByDid">The keys already resolved in this expansion, by DID.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task<ForwardLayer> ResolveRoutingKeyLayerAsync(
        string reference,
        IDidKeyService keyService,
        Dictionary<string, IReadOnlyList<Jwk>> keysByDid,
        CancellationToken ct)
    {
        var hashIndex = reference.IndexOf('#');
        if (hashIndex == 0)
            throw new DidResolutionException(reference, "routing key reference is relative (fragment-only); a routing key must be a DID or a DID URL with a fragment");

        if (hashIndex > 0)
        {
            var subjectDid = reference[..hashIndex];
            var subjectKeys = await KeysOfAsync(subjectDid, keyService, keysByDid, ct).ConfigureAwait(false);
            var match = subjectKeys.FirstOrDefault(j => string.Equals(j.Kid, reference, StringComparison.Ordinal))
                ?? throw new DidResolutionException(subjectDid, $"routing key '{reference}' is not declared in keyAgreement");
            return new ForwardLayer(subjectDid, new[] { match });
        }

        if (!LooksLikeDid(reference) || IdentifierLimits.DidPartLength(reference) != reference.Length)
            throw new DidResolutionException(reference, "routing key reference is neither a DID nor a DID URL with a fragment");

        var keys = await KeysOfAsync(reference, keyService, keysByDid, ct).ConfigureAwait(false);
        if (keys.Count == 0)
            throw new DidResolutionException(reference, "routing key DID has no keyAgreement keys");
        return new ForwardLayer(reference, RecipientKeySelection.AllOnPreferredCurve(reference, keys, "routing key DID"));
    }

    // A DID's keyAgreement keys, resolved at most once per expansion.
    private static async Task<IReadOnlyList<Jwk>> KeysOfAsync(
        string did,
        IDidKeyService keyService,
        Dictionary<string, IReadOnlyList<Jwk>> keysByDid,
        CancellationToken ct)
    {
        if (!keysByDid.TryGetValue(did, out var keys))
        {
            keys = await keyService.GetVerificationMethodsAsync(did, VerificationRelationship.KeyAgreement, ct).ConfigureAwait(false);
            keysByDid[did] = keys;
        }
        return keys;
    }

    // The refusal for a reference over the FR-API-09 identifier limits: the bound it broke and the
    // reference's length, never the reference.
    private static string OverLimitReason(string what, string value)
        => value.Length > IdentifierLimits.MaxKeyIdLength
            ? $"{what} in its DIDCommMessaging service is {value.Length} characters long, over the maximum of {IdentifierLimits.MaxKeyIdLength} (FR-API-09); it is not quoted"
            : $"{what} in its DIDCommMessaging service has a DID part longer than the maximum of {IdentifierLimits.MaxDidLength} characters (FR-API-09); it is not quoted";

    private static bool LooksLikeDid(string uri) =>
        !string.IsNullOrEmpty(uri) && uri.StartsWith("did:", StringComparison.Ordinal);
}
