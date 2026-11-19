using System.Text.Json;
using DidComm.Exceptions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Internal;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// Which Coordinate Mediation version to speak to a mediator, from its Discover Features answer
/// (design §4.4, D-10, D-11): the query <see cref="MediationClient"/> sends, and the reading of the
/// <c>disclose</c> that answers it. Owner: engineer B.
/// </summary>
/// <remarks>
/// <para>
/// The reading is bounded before it works (L-055): at most <see cref="MaxInspected"/> disclosures
/// are looked at, in wire order, and the rest are never read; each one's <c>id</c> is checked for
/// length and characters before it is parsed; at most <see cref="MaxRolesInspected"/> roles are
/// read per disclosure.
/// </para>
/// <para>
/// <strong>The question</strong> is two queries, <see cref="QueryMatch3"/> and
/// <see cref="QueryMatch2"/>: each one matches its version however the mediator reads <c>match</c>
/// — as the spec's wildcard (a trailing <c>*</c>), as a regular expression that must match the
/// whole id (the Identus mediator), or as a prefix once <c>.*</c> is removed (the Blocktrust
/// mediator). One <c>coordinate-mediation/*</c> query matches nothing under the other two readings.
/// </para>
/// <para>
/// <strong>The answer.</strong> A disclosure counts when its <c>feature-type</c> is
/// <c>protocol</c>, its <c>id</c> is a protocol identifier URI, and its <c>roles</c> do not rule
/// the mediator role out. Absent or <c>null</c> roles say nothing, so the entry counts. A
/// <c>roles</c> list counts when it names <c>mediator</c>; when it names <c>recipient</c> and not
/// <c>mediator</c>, the peer has said it is the other side, and the entry does not count; when it
/// names neither (empty, or only names this protocol does not define, such as the Identus
/// mediator's <c>responder</c>), it says nothing about this protocol's roles, like an absent list,
/// and the entry counts (owner decision 2026-11-18, #99). Roles are compared ignoring case; a
/// <c>roles</c> member that is not an array does not count. Coordinate Mediation 3.x and 2.x are
/// recognised by major version; 3 wins over 2. Neither is
/// <see cref="MediationNotSupportedException"/>, whose text quotes no wire value and whose
/// <see cref="MediationNotSupportedException.DisclosedProtocols"/> lists the protocol disclosures
/// inspected, cleaned (every <c>protocol</c> entry, whatever its roles).
/// </para>
/// </remarks>
internal static class FeatureNegotiation
{
    /// <summary>The <c>match</c> of the first query: Coordinate Mediation 3.x.</summary>
    internal const string QueryMatch3 = "https://didcomm.org/coordinate-mediation/3.*";

    /// <summary>The <c>match</c> of the second query: Coordinate Mediation 2.x.</summary>
    internal const string QueryMatch2 = "https://didcomm.org/coordinate-mediation/2.*";

    /// <summary>The most disclosures inspected; the rest are never read.</summary>
    internal const int MaxInspected = 256;

    /// <summary>The most disclosed protocols reported on the exception.</summary>
    internal const int MaxReported = 32;

    /// <summary>The longest disclosed id looked at (a protocol identifier URI is far shorter).</summary>
    internal const int MaxIdLength = 256;

    /// <summary>The most <c>roles</c> entries read per disclosure.</summary>
    internal const int MaxRolesInspected = 32;

    private const string DisclosuresMember = "disclosures";
    private const string FeatureTypeMember = "feature-type";
    private const string IdMember = "id";
    private const string RolesMember = "roles";

    /// <summary>What the query expects back: a Discover Features 2.x <c>disclose</c>.</summary>
    internal static readonly MediatorExchangeSpec QuerySpec = new(
        "discover-features query",
        Array.AsReadOnly(new[] { MessageTypeUri.Parse(DiscoverFeatures.DiscloseType) }),
        ExpectsDelivery: false);

    private static readonly MessageTypeUri DiscloseUri = MessageTypeUri.Parse(DiscoverFeatures.DiscloseType);
    private static readonly ProtocolIdentifier Version3 = ProtocolIdentifier.Parse(CoordinateMediation.ProtocolUri);
    private static readonly ProtocolIdentifier Version2 = ProtocolIdentifier.Parse(CoordinateMediation20.ProtocolUri);

    /// <summary>
    /// The Discover Features question for the two Coordinate Mediation versions, from
    /// <paramref name="from"/> to <paramref name="mediatorDid"/>: one message with two queries,
    /// <see cref="QueryMatch3"/> then <see cref="QueryMatch2"/>. Its id is a fresh UUID v4
    /// (<see cref="DiscoverFeatures.CreateQuery"/> builds with <see cref="UuidV4MessageIdGenerator.Instance"/>).
    /// </summary>
    /// <param name="from">Our DID.</param>
    /// <param name="mediatorDid">The mediator's DID.</param>
    internal static Message CreateQuery(string from, string mediatorDid)
        => DiscoverFeatures.CreateQuery(
            from,
            mediatorDid,
            new FeatureQuery { FeatureType = DiscoverFeatures.FeatureTypeProtocol, Match = QueryMatch3 },
            new FeatureQuery { FeatureType = DiscoverFeatures.FeatureTypeProtocol, Match = QueryMatch2 });

    /// <summary>
    /// Choose the version from <paramref name="disclose"/>, the answer to our query
    /// <paramref name="requestId"/>.
    /// </summary>
    /// <param name="disclose">The mediator's answer.</param>
    /// <param name="requestId">The id of our query (for the exception).</param>
    /// <returns><see cref="CoordinateMediationVersion.V3"/> or <see cref="CoordinateMediationVersion.V2"/>.</returns>
    /// <exception cref="MediationNotSupportedException">No usable Coordinate Mediation disclosure in the mediator role.</exception>
    /// <exception cref="MediationProtocolException">The answer is not a <c>disclose</c>, or its body cannot be read.</exception>
    internal static CoordinateMediationVersion Choose(Message disclose, string requestId)
    {
        ArgumentNullException.ThrowIfNull(disclose);
        if (!WireJson.IsType(disclose, DiscloseUri))
        {
            throw new MediationProtocolException(
                $"The mediator answered the Discover Features query '{requestId}' with a message of another type.", requestId);
        }

        JsonElement body;
        try
        {
            body = WireJson.ToElement(disclose.Body);
        }
        catch (MalformedMessageException ex)
        {
            throw new MediationProtocolException(
                $"The mediator's answer to the Discover Features query '{requestId}' is malformed; see the inner exception.", requestId, innerException: ex);
        }

        var has3 = false;
        var has2 = false;
        var reported = new List<string>();
        // Discover Features is permissive (FR-PROTO-05): a body without a disclosures array, or an
        // entry of the wrong shape, discloses nothing rather than failing the answer.
        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty(DisclosuresMember, out var disclosures)
            && disclosures.ValueKind == JsonValueKind.Array)
        {
            var inspected = 0;
            foreach (var entry in disclosures.EnumerateArray())
            {
                if (inspected++ == MaxInspected)
                    break;
                if (!TryReadProtocol(entry, out var id, out var protocol))
                    continue;
                if (reported.Count < MaxReported && !reported.Contains(id, StringComparer.Ordinal))
                    reported.Add(id);
                if (!PlaysMediator(entry))
                    continue;
                has3 |= protocol.MatchesProtocolAndMajor(Version3);
                has2 |= protocol.MatchesProtocolAndMajor(Version2);
            }
        }

        if (has3)
            return CoordinateMediationVersion.V3;
        if (has2)
            return CoordinateMediationVersion.V2;
        throw new MediationNotSupportedException(
            $"The mediator disclosed {reported.Count} protocol(s), none of them Coordinate Mediation 3.0 or 2.0 in the mediator role (request '{requestId}'); see DisclosedProtocols.",
            requestId,
            reported.AsReadOnly());
    }

    // A protocol disclosure whose id is a clean protocol identifier URI: at most MaxIdLength
    // printable ASCII characters (checked before the PIURI pattern sees it), parsing as a PIURI.
    // Decoding an inspected id is bounded by the answer's own size (MaxReceiveBytes).
    private static bool TryReadProtocol(JsonElement entry, out string id, out ProtocolIdentifier protocol)
    {
        id = string.Empty;
        protocol = null!;
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty(FeatureTypeMember, out var featureType)
            || featureType.ValueKind != JsonValueKind.String
            || !featureType.ValueEquals(DiscoverFeatures.FeatureTypeProtocol)
            || !entry.TryGetProperty(IdMember, out var idValue)
            || idValue.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = idValue.GetString();
        if (string.IsNullOrEmpty(text) || text.Length > MaxIdLength)
            return false;
        foreach (var c in text)
        {
            if (c is < '\x21' or > '\x7E')
                return false;
        }
        if (!ProtocolIdentifier.TryParse(text, out var parsed))
            return false;
        id = text;
        protocol = parsed;
        return true;
    }

    // Whether the roles leave the mediator role open (owner decision 2026-11-18, #99). Absent or
    // null: says nothing, so it counts. Not an array: does not count. An array, read up to its
    // first MaxRolesInspected entries: naming mediator counts; naming recipient and not mediator is
    // the peer saying it is the other side, so it does not; naming neither (empty, or only roles
    // this protocol does not define, such as "responder") says nothing about this protocol's roles,
    // like an absent list, so it counts. Entries that are not strings name no role.
    private static bool PlaysMediator(JsonElement entry)
    {
        if (!entry.TryGetProperty(RolesMember, out var roles) || roles.ValueKind == JsonValueKind.Null)
            return true;
        if (roles.ValueKind != JsonValueKind.Array)
            return false;

        var mediator = false;
        var recipient = false;
        var read = 0;
        foreach (var role in roles.EnumerateArray())
        {
            if (read++ == MaxRolesInspected)
                break;
            if (role.ValueKind != JsonValueKind.String)
                continue;
            var name = role.GetString();
            mediator |= string.Equals(name, CoordinateMediation.MediatorRole, StringComparison.OrdinalIgnoreCase);
            recipient |= string.Equals(name, CoordinateMediation.RecipientRole, StringComparison.OrdinalIgnoreCase);
        }
        return mediator || !recipient;
    }
}
