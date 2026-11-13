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
    /// <summary>
    /// The longest delay a forward's <c>delay_milli</c> can ask this mediator to hold a message for
    /// (FR-ROUTE-10). A longer positive delay is shortened to it. For a negative value, which asks
    /// for a random delay of up to its magnitude, the magnitude is capped to this before the random
    /// delay is drawn. No forward is refused because of its delay. <c>null</c> (the default) keeps
    /// the library's hard bound of <see cref="int.MaxValue"/> milliseconds (about 24.8 days).
    /// <c>AddMediator()</c> sets it from the mediator's own setting.
    /// </summary>
    public TimeSpan? MaxDelay { get; init; }

    /// <summary>Validate the option combination.</summary>
    /// <exception cref="ArgumentException">Extra routing keys are combined with <see cref="RewrapMode.ReanoncryptToNext"/>, or <see cref="MaxDelay"/> is negative or longer than <see cref="int.MaxValue"/> milliseconds.</exception>
    public void Validate()
    {
        if (Mode == RewrapMode.ReanoncryptToNext && ExtraRecipientRoutingKeys is { Count: > 0 })
        {
            throw new ArgumentException(
                "ExtraRecipientRoutingKeys cannot be combined with RewrapMode.ReanoncryptToNext — pick one transformation per mediator hop.",
                nameof(ExtraRecipientRoutingKeys));
        }

        if (MaxDelay is { } maxDelay && (maxDelay < TimeSpan.Zero || maxDelay > TimeSpan.FromMilliseconds(int.MaxValue)))
        {
            throw new ArgumentException(
                "The value must be zero or more and at most int.MaxValue milliseconds.",
                nameof(MaxDelay));
        }
    }
}
