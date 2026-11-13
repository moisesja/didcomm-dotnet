namespace DidComm.Mediator.Pickup;

/// <summary>
/// Settings of the Message Pickup mediator role: how much one <c>delivery</c> may carry, how long a
/// delivered message waits for its acknowledgement, and the bounds of the in-memory store
/// (FR-PICK-03, -04, -10).
/// </summary>
public sealed class MessagePickupOptions
{
    /// <summary>
    /// The largest number of messages one <c>delivery</c> carries (FR-PICK-03). A
    /// <c>delivery-request</c> asking for more gets this many. Default 10.
    /// </summary>
    public int MaxDeliveryBatch { get; set; } = 10;

    /// <summary>
    /// The largest packed <c>delivery</c> the mediator sends, in bytes of the encrypted envelope
    /// (<c>max_delivery_bytes</c>, FR-PICK-03). <c>null</c> (the default) means
    /// <c>DidCommOptions.MaxReceiveBytes</c> (1 MiB): set it to what your recipients accept, since a
    /// recipient discards a response over its own receive limit. It decides two things: a batch is
    /// cut short so that its packed size stays within it (measured on the packed bytes, not
    /// estimated), and a message too large to be delivered alone within it is not accepted into the
    /// queue at all (<see cref="MessagePickupQueue.MaxAdmissiblePayloadBytes"/>; a little over half
    /// of this value, because a delivered message is base64-encoded twice on the way). A value too
    /// small to carry any message is refused when the role is registered or first used.
    /// </summary>
    public int? MaxDeliveryBytes { get; set; }

    /// <summary>
    /// The largest number of ids one <c>messages-received</c> may carry (<c>max_ack_ids</c>,
    /// FR-PICK-10). A longer list is refused as malformed from its length alone: no id is decoded
    /// and the store is not called. <c>null</c> (the default) means
    /// <see cref="MaxDeliveryBatch"/> × 10. Must be at least <see cref="MaxDeliveryBatch"/>, so a
    /// full batch can be acknowledged in one message.
    /// </summary>
    public int? MaxAckIds { get; set; }

    /// <summary>
    /// How long a delivered message waits for its <c>messages-received</c> before it may be
    /// delivered again (FR-PICK-04). Over HTTP this is the only way a lease ends; over a WebSocket
    /// it also ends when the connection closes. Default 60 seconds; at most 24 hours. It is also the
    /// first period a message that does not fit the recipient's envelope is passed over for (the
    /// period doubles each time such a message is tried again).
    /// </summary>
    public TimeSpan LeaseTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The largest number of messages <see cref="InMemoryMessageStore"/> holds for one grant
    /// (<c>max_queue_messages</c>, FR-PICK-10): waiting, delivered-but-unacknowledged and not-yet-due
    /// messages all count. A message beyond it is not accepted. Default 100.
    /// </summary>
    public int MaxQueueMessages { get; set; } = 100;

    /// <summary>
    /// The largest payload total, in bytes, <see cref="InMemoryMessageStore"/> holds for one grant
    /// (<c>max_queue_bytes</c>, FR-PICK-10), counted like <see cref="MaxQueueMessages"/>. Default
    /// 8 MiB.
    /// </summary>
    public long MaxQueueBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>
    /// The largest number of messages <see cref="InMemoryMessageStore"/> holds for all grants
    /// together. Default 5 000. The in-memory store is meant for samples, tests and small hosts;
    /// its defaults are modest on purpose.
    /// </summary>
    public int MaxStoreMessages { get; set; } = 5_000;

    /// <summary>
    /// The largest payload total, in bytes, <see cref="InMemoryMessageStore"/> holds for all grants
    /// together. Default 128 MiB. Worst-case memory of the store at the defaults is this plus the
    /// keys of <see cref="MaxStoreMessages"/> records (two DIDs of up to 2048 characters and an id
    /// each, about 8 KiB): roughly 170 MiB.
    /// </summary>
    public long MaxStoreBytes { get; set; } = 128L * 1024 * 1024;

    /// <summary>
    /// The mailbox age limit: how long the queue keeps a message for its recipient, counted from
    /// when the queue accepted it, on the mediator's clock. It applies to every message held,
    /// whether waiting, delivered but not yet acknowledged, or passed over for being too large. A
    /// message older than this is never delivered and is removed the same way an expired message is
    /// (FR-PICK-10). Default 30 days; <c>null</c> turns the limit off. A recipient that stays offline
    /// longer than the limit loses those messages without notice, so set it to the longest absence
    /// your recipients need to survive. A message whose own <c>expires_time</c> comes first expires
    /// then. Must be greater than zero.
    /// </summary>
    /// <remarks>
    /// The queue reads this once, when it is built, and writes the resulting instant onto each
    /// record as it accepts it (<see cref="QueuedMessage.ExpiresAt"/>): the limit in force at
    /// admission is fixed on the record. Changing the setting later — or restarting with another
    /// value over a persistent <see cref="IMessageStore"/> — does not shorten or lengthen what is
    /// already held. A message whose not-before time (a forward's <c>delay_milli</c>) lies beyond
    /// the limit ages out before it is due, so keep the limit longer than the longest delay honoured.
    /// </remarks>
    public TimeSpan? MaxMessageAge { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Check the settings; called when the role is registered and when it is first used.</summary>
    /// <exception cref="ArgumentException">A bound is below 1, <see cref="MaxAckIds"/> is below <see cref="MaxDeliveryBatch"/>, <see cref="LeaseTimeout"/> is not within (0, 24 h], <see cref="MaxMessageAge"/> is set and not greater than zero, a store-wide bound is below its per-grant bound, or <see cref="MaxDeliveryBytes"/> is too small to carry any message.</exception>
    public void Validate()
    {
        RequirePositive(MaxDeliveryBatch, nameof(MaxDeliveryBatch));
        RequirePositive(MaxQueueMessages, nameof(MaxQueueMessages));
        RequirePositive(MaxQueueBytes, nameof(MaxQueueBytes));
        RequirePositive(MaxStoreMessages, nameof(MaxStoreMessages));
        RequirePositive(MaxStoreBytes, nameof(MaxStoreBytes));

        if (MaxAckIds is { } maxAckIds && maxAckIds < MaxDeliveryBatch)
            throw new ArgumentException($"The value must be at least {nameof(MaxDeliveryBatch)}, so a full batch can be acknowledged in one message.", nameof(MaxAckIds));
        if (LeaseTimeout <= TimeSpan.Zero || LeaseTimeout > TimeSpan.FromHours(24))
            throw new ArgumentException("The value must be greater than zero and at most 24 hours.", nameof(LeaseTimeout));
        if (MaxMessageAge is { } maxMessageAge && maxMessageAge <= TimeSpan.Zero)
            throw new ArgumentException("The value must be greater than zero, or null for no limit.", nameof(MaxMessageAge));
        if (MaxStoreMessages < MaxQueueMessages)
            throw new ArgumentException($"The value must be at least {nameof(MaxQueueMessages)}.", nameof(MaxStoreMessages));
        if (MaxStoreBytes < MaxQueueBytes)
            throw new ArgumentException($"The value must be at least {nameof(MaxQueueBytes)}.", nameof(MaxStoreBytes));

        if (MaxDeliveryBytes is { } maxDeliveryBytes && PickupSizing.ComputeBatchPayloadBudget(maxDeliveryBytes, MaxDeliveryBatch) < 1)
            throw new ArgumentException(PickupSizing.TooSmallMessage, nameof(MaxDeliveryBytes));
    }

    /// <summary>The effective <see cref="MaxAckIds"/>: the configured value, or <see cref="MaxDeliveryBatch"/> × 10.</summary>
    internal int EffectiveMaxAckIds => MaxAckIds ?? (int)Math.Min(int.MaxValue, (long)MaxDeliveryBatch * 10);

    private static void RequirePositive(long value, string name)
    {
        if (value < 1)
            throw new ArgumentException("The value must be at least 1.", name);
    }
}
