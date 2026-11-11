using DidComm.Protocols;
using DidComm.Protocols.Routing;

namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// The protocol identifiers whose messages are in the FR-RR-08 replay-protection scope even when
/// they carry no <c>return_route</c> header — the mediator-service protocols a peer uses to
/// enrol with and pick up from a mediator — plus the one exemption, Routing 2.0 <c>forward</c>.
/// </summary>
/// <remarks>
/// Matching follows the registry's rule (FR-PROTO-01/02): the protocol name is compared ignoring
/// case and <c>-</c> / <c>_</c> punctuation and the major version must match; any minor matches.
/// Both predicates use <c>TryParse</c> and answer <c>false</c> for anything unparseable (never
/// throw).
/// </remarks>
public static class MediatorServiceProtocols
{
    /// <summary>Coordinate Mediation 2.0 (the Aries-era protocol, CM 2.0 compatibility).</summary>
    public const string CoordinateMediation20 = "https://didcomm.org/coordinate-mediation/2.0";

    /// <summary>Coordinate Mediation 3.0.</summary>
    public const string CoordinateMediation30 = "https://didcomm.org/coordinate-mediation/3.0";

    /// <summary>Message Pickup 3.0.</summary>
    public const string MessagePickup30 = "https://didcomm.org/messagepickup/3.0";

    /// <summary>Message Pickup 4.0.</summary>
    public const string MessagePickup40 = "https://didcomm.org/message-pickup/4.0";

    private static readonly string[] AllIdentifiers =
    {
        CoordinateMediation20,
        CoordinateMediation30,
        MessagePickup30,
        MessagePickup40,
    };

    private static readonly ProtocolIdentifier[] Parsed =
        AllIdentifiers.Select(ProtocolIdentifier.Parse).ToArray();

    private static readonly ProtocolIdentifier Routing = ProtocolIdentifier.Parse(ForwardConstants.ProtocolIdentifier);

    /// <summary>The four mediator-service protocol identifiers, as written above.</summary>
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(AllIdentifiers);

    /// <summary>
    /// Whether <paramref name="messageType"/> is a message of one of the mediator-service protocols
    /// (same name and major version as one of <see cref="All"/>). <c>false</c> for <c>null</c> or an
    /// unparseable type.
    /// </summary>
    /// <param name="messageType">The message type URI (MTURI) from the wire.</param>
    public static bool IsMediatorServiceType(string? messageType)
    {
        if (!TryGetProtocol(messageType, out var protocol))
            return false;
        foreach (var candidate in Parsed)
        {
            if (candidate.MatchesProtocolAndMajor(protocol))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="messageType"/> is exempt from FR-RR-08 replay admission: a Routing
    /// 2.0 message (<c>forward</c>). A forward carries no sender identity of its own and is relayed
    /// by content, so admission keyed on its outer envelope would only refuse legitimate re-sends.
    /// <c>false</c> for <c>null</c> or an unparseable type.
    /// </summary>
    /// <param name="messageType">The message type URI (MTURI) from the wire.</param>
    public static bool IsReplayExempt(string? messageType)
        => TryGetProtocol(messageType, out var protocol) && Routing.MatchesProtocolAndMajor(protocol);

    private static bool TryGetProtocol(string? messageType, out ProtocolIdentifier protocol)
    {
        protocol = null!;
        if (!MessageTypeUri.TryParse(messageType, out var mturi) || mturi is null)
            return false;
        if (!ProtocolIdentifier.TryParse(mturi.ProtocolIdentifier, out var parsed))
            return false;
        protocol = parsed;
        return true;
    }
}
