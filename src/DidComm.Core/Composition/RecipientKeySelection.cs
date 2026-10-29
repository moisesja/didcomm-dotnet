using DidComm.Consistency;
using DidComm.Exceptions;
using DidComm.Jose;

namespace DidComm.Composition;

/// <summary>
/// Chooses the keyAgreement keys an envelope is encrypted to (FR-ENC-19, DD-07). The spec says the
/// default recipients of an envelope SHOULD include every keyAgreement key representing the
/// recipient, so the recipient can decrypt on any device it controls; a forward layer likewise
/// addresses every keyAgreement key of the mediator. One JWE has one ephemeral key, so all of its
/// recipient keys share one curve: the selection takes every usable key on one negotiated curve.
/// Shared by the facade's pack path, <see cref="Resolution.MediatorEndpointExpander"/> and the
/// mediator's rewrap in <see cref="Protocols.Routing.ForwardProcessor"/>.
/// </summary>
/// <remarks>
/// Every step is a linear pass over the key list with no regular expression and no DID parsing, so
/// the cost of selecting from a peer's DID document grows with the document and nothing else.
/// </remarks>
internal static class RecipientKeySelection
{
    /// <summary>
    /// The most keys taken from one DID's keyAgreement list for one envelope, in document order:
    /// for a bare DID in the recipients, a mediator named by its DID, a bare-DID routing key, or a
    /// rewrap target. Keys the caller pins by DID URL are taken as given and are not counted. Each
    /// key costs the sender one ECDH operation and the envelope one recipient entry, and the
    /// recipient's DID document, which a peer controls, sets how many there are. 64 is far above any
    /// real device count and still bounds that work.
    /// </summary>
    public const int MaxKeysPerDid = 64;

    // Curve preference for negotiation (FR-ENC-04): X25519 first, then the NIST curves by size.
    private static readonly string[] CurvePreference =
    {
        JoseAlgorithms.CrvX25519,
        JoseAlgorithms.CrvP256,
        JoseAlgorithms.CrvP384,
        JoseAlgorithms.CrvP521,
    };

    /// <summary>
    /// The keys of one DID's keyAgreement list that an envelope can name: those whose <c>kid</c> is
    /// a key id of <paramref name="did"/> itself, each kid once (ordinal, first wins), in document
    /// order. A JWE recipient entry is addressed by its kid, so a key without one cannot be used.
    /// Keys on a curve this library does not encrypt to are kept here and simply never match the
    /// negotiated curve. A kid over the FR-API-09 identifier limits is kept too: it is the
    /// recipient's own key id, and only receivers that bound key ids pass it over.
    /// </summary>
    /// <remarks>
    /// A key whose kid names another DID is left out. A DID document can list any key under its own
    /// keyAgreement, and an envelope holds each kid once, first wins, so without this rule one
    /// recipient could publish another recipient's kid with key material of its own and take that
    /// recipient's place in the envelope. Such an entry was never deliverable: the kid's DID does
    /// not vouch for the material, and a didcomm-dotnet receiver refuses a recipient kid whose DID
    /// is not in <c>to</c> (FR-CONSIST-02). The kid's DID part is everything before its first
    /// <c>/</c>, <c>;</c>, <c>?</c> or <c>#</c> (<see cref="IdentifierLimits.DidPartLength"/>), so
    /// the test is a prefix compare and one linear scan, never a DID parse.
    /// </remarks>
    /// <param name="did">The DID whose document gave <paramref name="keys"/>.</param>
    /// <param name="keys">That DID's keyAgreement keys, in document order.</param>
    public static IReadOnlyList<Jwk> Addressable(string did, IReadOnlyList<Jwk> keys)
    {
        ArgumentException.ThrowIfNullOrEmpty(did);
        ArgumentNullException.ThrowIfNull(keys);
        var result = new List<Jwk>(keys.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (string.IsNullOrEmpty(key.Kid) || !IsKeyIdOf(did, key.Kid) || !seen.Add(key.Kid))
                continue;
            result.Add(key);
        }
        return result;
    }

    /// <summary>
    /// The highest-preference curve (X25519, P-256, P-384, P-521) that every recipient set has a key
    /// on and, for authcrypt, the sender set has a key on too. Returns <c>null</c> when there is none;
    /// callers surface that as an explicit failure rather than splitting the envelope.
    /// </summary>
    /// <param name="recipientSets">The candidate keys of each recipient entry.</param>
    /// <param name="senderKeys">The sender's held keyAgreement keys (authcrypt only).</param>
    /// <param name="requireSender">Whether the curve must also be one the sender holds a key on.</param>
    public static string? PickCommonCurve(
        IEnumerable<IReadOnlyList<Jwk>> recipientSets,
        IReadOnlyList<Jwk> senderKeys,
        bool requireSender)
    {
        ArgumentNullException.ThrowIfNull(recipientSets);
        ArgumentNullException.ThrowIfNull(senderKeys);

        foreach (var crv in CurvePreference)
        {
            var allRecipientsCovered = true;
            foreach (var set in recipientSets)
            {
                if (!HasCurve(set, crv))
                {
                    allRecipientsCovered = false;
                    break;
                }
            }
            if (!allRecipientsCovered)
                continue;

            if (requireSender && !HasCurve(senderKeys, crv))
                continue;

            return crv;
        }

        return null;
    }

    /// <summary>
    /// The keys on <paramref name="crv"/>, in order, at most <see cref="MaxKeysPerDid"/> of them.
    /// </summary>
    /// <param name="keys">One DID's addressable keys (see <see cref="Addressable"/>).</param>
    /// <param name="crv">The negotiated curve.</param>
    /// <param name="overCap">How many keys on the curve were left out by the cap.</param>
    public static IReadOnlyList<Jwk> OnCurve(IReadOnlyList<Jwk> keys, string crv, out int overCap)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrEmpty(crv);
        var result = new List<Jwk>();
        overCap = 0;
        foreach (var key in keys)
        {
            if (!string.Equals(key.Crv, crv, StringComparison.Ordinal))
                continue;
            if (result.Count < MaxKeysPerDid)
                result.Add(key);
            else
                overCap++;
        }
        return result;
    }

    /// <summary>
    /// Every addressable key of a single DID on its own preferred curve, capped at
    /// <see cref="MaxKeysPerDid"/>: the recipients of a forward layer addressed to a mediator, or of
    /// a mediator's rewrap to the next hop, where one DID is the whole audience.
    /// </summary>
    /// <param name="did">The DID the keys belong to; only keys whose kid names it are taken (see <see cref="Addressable"/>).</param>
    /// <param name="keys">Its keyAgreement keys, in document order; at least one.</param>
    /// <param name="role">What the DID is to the caller, for the error (e.g. <c>mediator-as-endpoint</c>).</param>
    /// <exception cref="DidResolutionException">No key has a kid of <paramref name="did"/>, or none is on a supported curve.</exception>
    public static IReadOnlyList<Jwk> AllOnPreferredCurve(string did, IReadOnlyList<Jwk> keys, string role)
    {
        ArgumentException.ThrowIfNullOrEmpty(did);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrEmpty(role);

        var addressable = Addressable(did, keys);
        if (addressable.Count == 0)
            throw new DidResolutionException(did, $"{role} has no keyAgreement key with a key id of this DID");

        var crv = PickCommonCurve(new[] { addressable }, Array.Empty<Jwk>(), requireSender: false)
            ?? throw new DidResolutionException(did, $"{role} has no keyAgreement key on a supported curve (X25519, P-256, P-384, P-521)");

        return OnCurve(addressable, crv, out _);
    }

    // Whether kid's DID part is exactly did: kid starts with did, and its first DID URL delimiter
    // (or its end) comes right after it.
    private static bool IsKeyIdOf(string did, string kid)
        => kid.StartsWith(did, StringComparison.Ordinal) && IdentifierLimits.DidPartLength(kid) == did.Length;

    private static bool HasCurve(IReadOnlyList<Jwk> keys, string crv)
    {
        foreach (var key in keys)
        {
            if (string.Equals(key.Crv, crv, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
