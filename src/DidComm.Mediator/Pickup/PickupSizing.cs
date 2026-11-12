namespace DidComm.Mediator.Pickup;

/// <summary>
/// The arithmetic behind <c>max_delivery_bytes</c> (FR-PICK-03): how many payload bytes fit in one
/// packed <c>delivery</c>. The result is one number, used twice — as the largest payload the queue
/// admits (so every retained message can be delivered alone) and as the byte budget of every
/// batch — which is what makes "an admitted message always fits" independent of the batch size.
/// </summary>
/// <remarks>
/// <para>
/// A payload of <c>n</c> bytes travels as base64url text inside the <c>delivery</c> JSON
/// (<c>⌈4n/3⌉</c> characters plus its attachment descriptor), and that JSON is then encrypted and
/// base64url-encoded again as the envelope's ciphertext. So the packed size is about 16/9 of the
/// payload, plus the parts that do not grow with it. Those parts are budgeted with fixed
/// allowances, generous on purpose:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="EnvelopeOverheadAllowance"/> for the JWE around the ciphertext: protected header, one <c>recipients[]</c> entry per key of the peer, iv, tag. About 1.1 KiB for a peer with one key.</description></item>
/// <item><description><see cref="DeliverySkeletonAllowance"/> for the <c>delivery</c> message without its attachments: three DIDs of up to 2048 characters, a thread id of up to <see cref="MaxThreadIdLength"/>, the fixed text.</description></item>
/// <item><description><see cref="AttachmentOverhead"/> per attachment: its descriptor, an id of up to <see cref="MaxMintedIdLength"/> characters, and the rounding of its base64.</description></item>
/// </list>
/// <para>
/// This is the estimate that picks a batch. It is not the guarantee: the session measures the
/// packed bytes and refuses a message over the budget
/// (<c>IReturnRouteSession.TrySendAsync(message, maxPackedBytes, ct)</c>), and the batch is then
/// trimmed. A peer whose own DID document makes its envelopes larger than the allowance (many
/// keys with long ids) gets smaller batches.
/// </para>
/// </remarks>
internal sealed class PickupSizing
{
    /// <summary>Bytes set aside for the JWE envelope around the ciphertext.</summary>
    internal const int EnvelopeOverheadAllowance = 32 * 1024;

    /// <summary>Bytes set aside for the <c>delivery</c> message without its attachments.</summary>
    internal const int DeliverySkeletonAllowance = 8 * 1024;

    /// <summary>Bytes set aside per attachment beyond the base64 of its payload.</summary>
    internal const int AttachmentOverhead = 96;

    /// <summary>The longest message id the mediator mints for a queued message.</summary>
    internal const int MaxMintedIdLength = 64;

    /// <summary>The longest request thread id a reply echoes; a longer one makes the request malformed.</summary>
    internal const int MaxThreadIdLength = 512;

    internal const string TooSmallMessage =
        "The value is too small to carry any message: a delivery needs room for its envelope and its own headers before the first payload byte.";

    public PickupSizing(MessagePickupOptions options, int maxReceiveBytes)
    {
        ArgumentNullException.ThrowIfNull(options);
        MaxDeliveryBytes = options.MaxDeliveryBytes ?? maxReceiveBytes;
        BatchPayloadBudget = ComputeBatchPayloadBudget(MaxDeliveryBytes, options.MaxDeliveryBatch);
        if (BatchPayloadBudget < 1)
            throw new ArgumentException(TooSmallMessage, nameof(MessagePickupOptions.MaxDeliveryBytes));
        MaxAdmissiblePayloadBytes = (int)Math.Min(Math.Max(0, maxReceiveBytes), BatchPayloadBudget);
        if (MaxAdmissiblePayloadBytes < 1)
            throw new ArgumentException("DidCommOptions.MaxReceiveBytes leaves no room for any queued message.", nameof(maxReceiveBytes));
    }

    /// <summary>The largest packed <c>delivery</c>, in bytes: the bound handed to the session's size-bounded send.</summary>
    public int MaxDeliveryBytes { get; }

    /// <summary>The largest payload total of one batch, in bytes: the byte budget of every lease.</summary>
    public long BatchPayloadBudget { get; }

    /// <summary>The largest payload the queue admits, in bytes: the smaller of the batch budget and <c>MaxReceiveBytes</c> (D7).</summary>
    public int MaxAdmissiblePayloadBytes { get; }

    /// <summary>
    /// The payload bytes that fit in a packed <c>delivery</c> of at most
    /// <paramref name="maxDeliveryBytes"/> carrying up to <paramref name="maxDeliveryBatch"/>
    /// attachments. Below 1 when nothing fits. Every division rounds down, so the result errs
    /// towards a smaller batch.
    /// </summary>
    public static long ComputeBatchPayloadBudget(long maxDeliveryBytes, int maxDeliveryBatch)
    {
        // The ciphertext whose base64url text fits beside the envelope allowance.
        var ciphertext = (maxDeliveryBytes - EnvelopeOverheadAllowance) / 4 * 3;
        // A block cipher pads the plaintext to a whole 16-byte block, by at least one byte.
        var plaintext = ciphertext / 16 * 16 - 1;
        // What is left for the base64 text of the payloads.
        var attachments = plaintext - DeliverySkeletonAllowance - (long)maxDeliveryBatch * AttachmentOverhead;
        return attachments / 4 * 3;
    }
}
