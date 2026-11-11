namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// Settings for the FR-RR-08 inbound replay guard. Configure through
/// <c>services.AddOptions&lt;ReplayProtectionOptions&gt;().Configure(...)</c>; the
/// <see cref="InMemoryReplayGuard"/> validates the instance it is built with.
/// </summary>
public sealed class ReplayProtectionOptions
{
    /// <summary>
    /// How long an admitted <c>(sender, message id)</c> is remembered, and how old a timestamped
    /// message may be before it is refused as <see cref="ReplayAdmission.TooOld"/>. Default 5 minutes
    /// (FR-RR-08 <c>replay_window</c>).
    /// </summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether an in-scope message without <c>created_time</c> is refused
    /// (<see cref="ReplayAdmission.MissingCreatedTime"/>). Default <c>true</c> (FR-RR-08 (2)). When
    /// <c>false</c> such a message is admitted and remembered for <see cref="Window"/> — the documented
    /// limitation being that an identical captured request is accepted again once the window has
    /// passed.
    /// </summary>
    public bool RequireCreatedTime { get; set; } = true;

    /// <summary>
    /// Entries the guard holds in total; at the bound a new entry is refused as
    /// <see cref="ReplayAdmission.Saturated"/> (not admitted — fail closed; nothing unexpired is ever
    /// evicted to make room). Default 32 768 ≈ 109 admitted in-scope messages per second sustained
    /// over the 5-minute window on one mediator host; worst-case memory ≈ 40 MB with 512-character
    /// ids, ≈ 6 MB with UUIDs. A busier host raises it.
    /// </summary>
    public int MaxEntries { get; set; } = 32_768;

    /// <summary>
    /// Entries one sender DID may hold; its next message is refused as
    /// <see cref="ReplayAdmission.Saturated"/>. Default 1024 ≈ 3.4 in-scope messages per second over
    /// the window (pickup polling plus acknowledgements) — far above legitimate use, far below
    /// <see cref="MaxEntries"/> so one peer cannot starve the rest.
    /// </summary>
    public int MaxEntriesPerPeer { get; set; } = 1024;

    /// <summary>
    /// Longest message id the guard stores; a longer id is refused as
    /// <see cref="ReplayAdmission.MessageIdTooLong"/> before anything is allocated. Default 512
    /// (14× a UUID — FR-MSG-02 bounds the id's alphabet, not its length, and no compliant id needs
    /// more), so an oversized id can never be stored <see cref="MaxEntries"/> times.
    /// </summary>
    public int MaxMessageIdLength { get; set; } = 512;

    /// <summary>
    /// Check the settings: <see cref="Window"/> positive and at most <see cref="int.MaxValue"/>
    /// seconds; <see cref="MaxEntries"/>, <see cref="MaxEntriesPerPeer"/> and
    /// <see cref="MaxMessageIdLength"/> at least 1; <see cref="MaxEntriesPerPeer"/> no larger than
    /// <see cref="MaxEntries"/>. Throws <see cref="InvalidOperationException"/> naming the offending
    /// property.
    /// </summary>
    public void Validate()
    {
        if (Window <= TimeSpan.Zero || Window.TotalSeconds > int.MaxValue)
            throw new InvalidOperationException($"{nameof(ReplayProtectionOptions)}.{nameof(Window)} must be positive and at most int.MaxValue seconds.");
        if (MaxEntries < 1)
            throw new InvalidOperationException($"{nameof(ReplayProtectionOptions)}.{nameof(MaxEntries)} must be at least 1.");
        if (MaxEntriesPerPeer < 1)
            throw new InvalidOperationException($"{nameof(ReplayProtectionOptions)}.{nameof(MaxEntriesPerPeer)} must be at least 1.");
        if (MaxEntriesPerPeer > MaxEntries)
            throw new InvalidOperationException($"{nameof(ReplayProtectionOptions)}.{nameof(MaxEntriesPerPeer)} must not exceed {nameof(MaxEntries)}.");
        if (MaxMessageIdLength < 1)
            throw new InvalidOperationException($"{nameof(ReplayProtectionOptions)}.{nameof(MaxMessageIdLength)} must be at least 1.");
    }
}
