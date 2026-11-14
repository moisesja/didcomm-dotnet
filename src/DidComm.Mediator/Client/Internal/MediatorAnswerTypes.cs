using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Transports.ReturnRoute;

namespace DidComm.Mediator.Client.Internal;

/// <summary>What the matcher does with an answer type (design §4.2).</summary>
internal enum MediatorAnswerKind
{
    /// <summary>An answer that completes a request expecting it.</summary>
    Answer,

    /// <summary>A Message Pickup <c>delivery</c>: with a thread it answers a <c>delivery-request</c>; without one it is a live push.</summary>
    Delivery,

    /// <summary>A Report Problem 2.x message: completes the request its <c>pthid</c> (else <c>thid</c>) names, whatever it expected.</summary>
    ProblemReport,
}

/// <summary>
/// The message types a mediator answers the clients' requests with — and so the only types the
/// matcher looks at (design §4.2 step 1): Discover Features 2.0 <c>disclose</c>; Coordinate
/// Mediation 3.0 <c>mediate-grant</c>, <c>mediate-deny</c>, <c>recipient-update-response</c>,
/// <c>recipient</c> and 2.0 <c>mediate-grant</c>, <c>mediate-deny</c>, <c>keylist-update-response</c>,
/// <c>keylist</c>; Message Pickup 3.0 <c>status</c> and <c>delivery</c>; Report Problem 2.0
/// <c>problem-report</c>. Matching is the registry's (FR-PROTO-01: case and punctuation ignored, any
/// minor of the major). Each entry carries a constant name, which is what a log line writes —
/// never the type as it arrived. Every <see cref="MediatorExchangeSpec.Expected"/> type must be one
/// of these (checked when the request is sent).
/// </summary>
internal static class MediatorAnswerTypes
{
    private static readonly (MessageTypeUri Uri, string Name, MediatorAnswerKind Kind)[] Table =
    {
        Entry(DiscoverFeatures.DiscloseType, "disclose"),
        Entry(CoordinateMediation.MediateGrantType, "mediate-grant"),
        Entry(CoordinateMediation.MediateDenyType, "mediate-deny"),
        Entry(CoordinateMediation.RecipientUpdateResponseType, "recipient-update-response"),
        Entry(CoordinateMediation.RecipientType, "recipient"),
        Entry(MediatorServiceProtocols.CoordinateMediation20 + "/mediate-grant", "mediate-grant (2.0)"),
        Entry(MediatorServiceProtocols.CoordinateMediation20 + "/mediate-deny", "mediate-deny (2.0)"),
        Entry(MediatorServiceProtocols.CoordinateMediation20 + "/keylist-update-response", "keylist-update-response (2.0)"),
        Entry(MediatorServiceProtocols.CoordinateMediation20 + "/keylist", "keylist (2.0)"),
        Entry(MessagePickup.StatusType, "status"),
        Entry(MessagePickup.DeliveryType, "delivery", MediatorAnswerKind.Delivery),
        Entry(Protocols.ProblemReport.ProblemReport.MessageType, "problem-report", MediatorAnswerKind.ProblemReport),
    };

    /// <summary>The parsed Message Pickup 3.0 <c>delivery</c> type.</summary>
    internal static readonly MessageTypeUri Delivery = MessageTypeUri.Parse(MessagePickup.DeliveryType);

    /// <summary>The answer type <paramref name="type"/> matches, with its constant name and kind; <c>false</c> for any other or an unparseable type.</summary>
    /// <param name="type">The parsed type of an inbound message.</param>
    /// <param name="name">The constant name of the matched answer type.</param>
    /// <param name="kind">What the matcher does with it.</param>
    internal static bool TryClassify(MessageTypeUri type, out string name, out MediatorAnswerKind kind)
    {
        ArgumentNullException.ThrowIfNull(type);
        foreach (var entry in Table)
        {
            if (entry.Uri.Matches(type))
            {
                name = entry.Name;
                kind = entry.Kind;
                return true;
            }
        }
        name = string.Empty;
        kind = default;
        return false;
    }

    /// <summary>
    /// Refuse a spec the matcher could never complete: an expected type outside this table, or
    /// <see cref="MediatorExchangeSpec.ExpectsDelivery"/> disagreeing with whether
    /// <see cref="MediatorExchangeSpec.Expected"/> names <c>delivery</c>. A programming error of the
    /// facade that defines the spec, so it fails the first request, before anything is sent.
    /// </summary>
    /// <param name="spec">The spec.</param>
    /// <exception cref="ArgumentException">The spec is inconsistent (the message names the spec, a constant).</exception>
    internal static void Validate(MediatorExchangeSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Expected is not { Count: > 0 } expected)
            throw new ArgumentException($"The exchange spec '{spec.Name}' expects no answer type.", nameof(spec));
        var namesDelivery = false;
        foreach (var type in expected)
        {
            if (type is null || !TryClassify(type, out _, out var kind))
                throw new ArgumentException($"The exchange spec '{spec.Name}' expects a type that is not a mediator answer type the matcher looks at.", nameof(spec));
            namesDelivery |= kind == MediatorAnswerKind.Delivery;
        }
        if (namesDelivery != spec.ExpectsDelivery)
            throw new ArgumentException($"The exchange spec '{spec.Name}' sets ExpectsDelivery to {spec.ExpectsDelivery} but its expected types {(namesDelivery ? "name" : "do not name")} 'delivery'.", nameof(spec));
    }

    /// <summary>Whether <paramref name="spec"/> expects <paramref name="type"/>.</summary>
    /// <param name="spec">The pending request's spec.</param>
    /// <param name="type">The answer's parsed type.</param>
    internal static bool Expects(MediatorExchangeSpec spec, MessageTypeUri type)
    {
        foreach (var expected in spec.Expected)
        {
            if (expected.Matches(type))
                return true;
        }
        return false;
    }

    private static (MessageTypeUri, string, MediatorAnswerKind) Entry(string type, string name, MediatorAnswerKind kind = MediatorAnswerKind.Answer)
        => (MessageTypeUri.Parse(type), name, kind);
}
