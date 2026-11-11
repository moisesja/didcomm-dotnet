namespace DidComm.Protocols.Routing;

/// <summary>
/// Output of <see cref="ForwardProcessor.ProcessAsync(Facade.UnpackResult, CancellationToken)"/> — what a
/// mediator's transport layer needs to know to relay a forward (PRD §8, FR-ROUTE-05).
/// </summary>
/// <param name="NextHop">The DID (or DID URL) the onward payload must be sent to — value of <c>body.next</c> on the inbound forward.</param>
/// <param name="OnwardPacked">The bytes to transmit to <paramref name="NextHop"/>. In pass-through mode these are the inner attachment verbatim; in rewrap mode they are a freshly-anoncrypted <c>forward</c> wrapping that attachment (FR-ROUTE-06).</param>
/// <param name="ExpiresTime">Optional <c>expires_time</c> propagated from the inbound forward. <c>null</c> when the sender did not set one.</param>
/// <param name="Delay">Optional spec-defined <c>delay_milli</c> hint resolved to a <see cref="TimeSpan"/>. Negative input values are randomised between zero and <c>|n|</c> as the spec specifies; either way the hold is capped at <see cref="int.MaxValue"/> milliseconds (about 24.8 days) so a hostile hint cannot overflow; <c>null</c> when absent or not an integer.</param>
public sealed record ForwardProcessingResult(
    string NextHop,
    byte[] OnwardPacked,
    long? ExpiresTime,
    TimeSpan? Delay)
{
    /// <summary>
    /// The earliest moment the onward payload may be delivered (FR-ROUTE-10): the receipt time on
    /// the <see cref="Facade.DidCommOptions"/> clock plus <see cref="Delay"/>, resolved once so a
    /// queue can store it as an absolute not-before. Receipt time means: <see cref="ForwardHandler"/>
    /// stamps it when the forward is admitted, inline on the request and before any queueing, so
    /// time spent waiting for the transport acknowledgement or queued behind a slow sink never
    /// moves it; the public <see cref="ForwardProcessor.ProcessAsync(Facade.UnpackResult, CancellationToken)"/>
    /// overloads stamp it when they are called. <c>null</c> when the forward carried no
    /// <c>delay_milli</c>. The handler never sleeps on it; honoring the hint is the sink's (or
    /// queue's) job and never delays the transport acknowledgement.
    /// </summary>
    public DateTimeOffset? NotBefore { get; init; }

    /// <summary>
    /// Whether the forward's own <see cref="ExpiresTime"/> has passed at <paramref name="now"/>,
    /// allowing <paramref name="skew"/> (FR-ROUTE-09). Uses the same arithmetic as the FR-API-05
    /// unpack check so the two cannot drift. Always <c>false</c> when there is no expiry.
    /// </summary>
    /// <param name="now">The current time on the mediator's clock (<c>DidCommOptions.Clock</c>).</param>
    /// <param name="skew">The clock-skew allowance (<c>DidCommOptions.ExpiresClockSkew</c>); whole seconds, floored.</param>
    public bool IsExpired(DateTimeOffset now, TimeSpan skew) => IsExpiredAt(ExpiresTime, now, skew);

    /// <summary>
    /// The FR-API-05 expiry rule, shared by <see cref="IsExpired"/> and the handler's inline
    /// pre-queue check: <c>now − floor(skew) &gt; expires_time</c>, in epoch seconds.
    /// </summary>
    /// <param name="expiresTime">The forward's <c>expires_time</c> in epoch seconds, or <c>null</c> for none.</param>
    /// <param name="now">The current time.</param>
    /// <param name="skew">The skew allowance; whole seconds, floored.</param>
    internal static bool IsExpiredAt(long? expiresTime, DateTimeOffset now, TimeSpan skew)
    {
        if (expiresTime is not long expires)
            return false;
        var nowSeconds = now.ToUnixTimeSeconds();
        var skewSeconds = (long)Math.Floor(skew.TotalSeconds);
        return nowSeconds - skewSeconds > expires;
    }
}
