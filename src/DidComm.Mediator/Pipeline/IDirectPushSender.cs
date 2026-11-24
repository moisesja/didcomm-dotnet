namespace DidComm.Mediator.Pipeline;

/// <summary>
/// Sends a direct push (#110, D6 step 3): one transport send of a stored payload to the endpoint the
/// host's <see cref="IRecipientEndpointDirectory"/> returned, outside the host's own transport.
/// </summary>
internal interface IDirectPushSender
{
    /// <summary>
    /// Whether <paramref name="endpoint"/> may be pushed to, decided before any record is taken and
    /// under the push deadline: absolute, <c>http</c> or <c>https</c>, a scheme the HTTP transport
    /// accepts (else <see cref="PushTarget.NotUsable"/>), and not refused by the outbound address check
    /// (else <see cref="PushTarget.Refused"/>), which looks a host name up asynchronously.
    /// </summary>
    /// <param name="endpoint">The directory's answer.</param>
    /// <param name="ct">The push's deadline.</param>
    /// <returns>The verdict, with a fixed reason word when it is not <see cref="PushTarget.Accepted"/> (never the URI).</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled during the check.</exception>
    ValueTask<PushTargetCheck> CheckTargetAsync(Uri? endpoint, CancellationToken ct);

    /// <summary>
    /// One attempt: a POST of <paramref name="payload"/> to <paramref name="endpoint"/>, with no retry,
    /// outside any shared circuit breaker, following only 307 redirects up to the transport's bound,
    /// each hop checked by the outbound guard.
    /// </summary>
    /// <param name="endpoint">An endpoint <see cref="CheckTargetAsync"/> accepted.</param>
    /// <param name="payload">The stored encrypted message, sent unchanged.</param>
    /// <param name="ct">The push's deadline.</param>
    /// <returns>What the endpoint answered at the end of the chain; see <see cref="DirectSendResult"/>.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="Exception">Any other fault (no answer, a timeout, a refused connection): the attempt is spent and the message stays for pickup.</exception>
    Task<DirectSendResult> SendAsync(Uri endpoint, ReadOnlyMemory<byte> payload, CancellationToken ct);
}

/// <summary>What a direct push's endpoint answered at the end of the chain (#110).</summary>
internal enum DirectSendResult
{
    /// <summary>A 2xx: delivered.</summary>
    Delivered,

    /// <summary>
    /// A 4xx other than 408 and 429: the endpoint's definite "not this message". The message is not
    /// delivered, and nothing says the endpoint is unwell, so its recipient's next message goes on
    /// at once.
    /// </summary>
    Refused,

    /// <summary>
    /// Any other answer that is not a 2xx: a 5xx, 408 or 429 (which say "later"), a 3xx that is not
    /// followed (301, 302, 303, 308, a 307 without a usable <c>Location</c>), or more 307s than the
    /// bound. The message is not delivered, and the endpoint is treated as unwell: its recipient's
    /// direct pushes pause.
    /// </summary>
    Failed,
}

/// <summary>What the endpoint check before the take decided (#110).</summary>
internal enum PushTarget
{
    /// <summary>The endpoint may be pushed to.</summary>
    Accepted,

    /// <summary>The directory has no endpoint for the recipient.</summary>
    NoEndpoint,

    /// <summary>Not an endpoint a push can use: not absolute, not <c>http</c> or <c>https</c>, or a scheme the HTTP transport does not accept.</summary>
    NotUsable,

    /// <summary>The outbound address check refused it: a blocked IP literal, a name that resolves to a blocked address or does not resolve, or a host that cannot be put in its wire form.</summary>
    Refused,
}

/// <summary>The verdict of <see cref="IDirectPushSender.CheckTargetAsync"/>.</summary>
/// <param name="Verdict">What was decided.</param>
/// <param name="Reason">A fixed word naming why it is not accepted (never the URI); empty when accepted.</param>
internal readonly record struct PushTargetCheck(PushTarget Verdict, string Reason);

/// <summary>What a push worker needs for the direct step: the host's directory and the sender, built on first use.</summary>
/// <param name="Directory">The host's directory.</param>
/// <param name="Sender">The sender, built on the first direct push (or at start-up).</param>
internal sealed record DirectPushRoute(IRecipientEndpointDirectory Directory, Lazy<IDirectPushSender> Sender);
