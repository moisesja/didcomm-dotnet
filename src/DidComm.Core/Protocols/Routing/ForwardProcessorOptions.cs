using DidComm.Jose;

namespace DidComm.Protocols.Routing;

/// <summary>
/// Per-mediator options for <see cref="ForwardProcessor"/> (PRD §8 / FR-ROUTE-05/06).
/// </summary>
/// <param name="Mode">Selects pass-through vs FR-ROUTE-06 rewrap.</param>
/// <param name="ExtraRecipientRoutingKeys">Recipient-supplied routing keys configured out of band with this mediator (FR-ROUTE-05 second paragraph). When non-empty, the mediator wraps the inner payload once per key (in reverse order) BEFORE relaying. Mutually exclusive with <paramref name="Mode"/> = <see cref="RewrapMode.ReanoncryptToNext"/> — combining the two raises at construction time.</param>
public sealed record ForwardProcessorOptions(
    RewrapMode Mode = RewrapMode.PassThrough,
    IReadOnlyList<Jwk>? ExtraRecipientRoutingKeys = null)
{
    /// <summary>Validate the option combination. Throws when the two modes are mutually exclusive.</summary>
    public void Validate()
    {
        if (Mode == RewrapMode.ReanoncryptToNext && ExtraRecipientRoutingKeys is { Count: > 0 })
        {
            throw new ArgumentException(
                "ExtraRecipientRoutingKeys cannot be combined with RewrapMode.ReanoncryptToNext — pick one transformation per mediator hop.",
                nameof(ExtraRecipientRoutingKeys));
        }
    }
}
