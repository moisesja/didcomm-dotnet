namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// Settings for the FR-RR-08 inbound replay guard. Configure through
/// <c>services.AddOptions&lt;ReplayProtectionOptions&gt;().Configure(...)</c>; the
/// <see cref="InMemoryReplayGuard"/> validates the instance it is built with.
/// </summary>
public sealed class ReplayProtectionOptions
{
    /// <summary>
    /// How old a timestamped message may be before it is refused as
    /// <see cref="ReplayAdmission.TooOld"/>, and the base of how long an admitted
    /// <c>(sender, message id)</c> is remembered: until <c>created_time + Window + ClockSkew</c>
    /// (see <see cref="ClockSkew"/>). Default 5 minutes (FR-RR-08 <c>replay_window</c>). Whole
    /// seconds, floored.
    /// </summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How far ahead of this host's clock a message's <c>created_time</c> may be before it is
    /// refused as <see cref="ReplayAdmission.FutureDated"/>, so that a peer whose clock runs a little
    /// fast is not dropped; an admitted entry is also kept this much longer than
    /// <see cref="Window"/>. Default 60 seconds (FR-RR-08 <c>replay_clock_skew</c>). Whole seconds,
    /// floored: a sub-second value counts as zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the replay rule's own tolerance. FR-API-05 <c>expires_time</c> checking has a separate
    /// one, <see cref="DidComm.Facade.DidCommOptions.ExpiresClockSkew"/> (default zero), so widening this
    /// never lets an expired message through.
    /// </para>
    /// <para>
    /// It costs budget, never integrity: a captured request is still admitted at most once, and is
    /// refused on age once <c>now &gt; created_time + Window</c>. An entry for a message stamped now
    /// is kept <c>Window + ClockSkew</c> (6 minutes at the defaults); one stamped
    /// <c>now + ClockSkew</c>, the latest admitted, <c>Window + 2 × ClockSkew</c> (7 minutes) — so a
    /// sender that dates its messages ahead holds each of its entries that long. Size
    /// <see cref="MaxEntries"/> to the accepted rate × (<see cref="Window"/> + <c>ClockSkew</c>).
    /// </para>
    /// <para>
    /// Read by <see cref="InMemoryReplayGuard"/> on every call, on whichever side runs it: the
    /// receive endpoints, and the client's inbound consumer for what arrives on a WebSocket session
    /// socket. One container has one value for both. A host-registered
    /// <see cref="IInboundReplayGuard"/> applies its own rule.
    /// </para>
    /// </remarks>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether an in-scope message without <c>created_time</c> is refused
    /// (<see cref="ReplayAdmission.MissingCreatedTime"/>). Default <c>true</c> (FR-RR-08 (2)). When
    /// <c>false</c> such a message is admitted and remembered for <see cref="Window"/> from its arrival
    /// (<see cref="ClockSkew"/> does not apply: there is no timestamp to be early) — the documented
    /// limitation being that an identical captured request is accepted again once the window has
    /// passed.
    /// </summary>
    public bool RequireCreatedTime { get; set; } = true;

    /// <summary>
    /// Entries the guard holds in total; at the bound a new entry is refused as
    /// <see cref="ReplayAdmission.Saturated"/> (not admitted — fail closed; nothing unexpired is ever
    /// evicted to make room). Default 32 768 ≈ 91 admitted in-scope messages per second sustained
    /// over the 6 minutes an entry is kept at the defaults (<see cref="Window"/> +
    /// <see cref="ClockSkew"/>) on one mediator host. Memory of <see cref="InMemoryReplayGuard"/> at
    /// this bound (measured): ≈ 35 MB when a few dozen senders hold it with 512-character ids, ≈ 6 MB
    /// with UUID ids, and up to ≈ 170 MB when every entry belongs to a different sender with a
    /// maximal (2048-character) DID and a 512-character id — the most a flood of fresh DIDs can
    /// reach, because each sender's DID subject is kept once while it has entries. A busier host
    /// raises it; a host that must cap memory lower lowers it.
    /// </summary>
    public int MaxEntries { get; set; } = 32_768;

    /// <summary>
    /// Entries one sender DID may hold; its next message is refused as
    /// <see cref="ReplayAdmission.Saturated"/>. Default 1024 ≈ 2.8 in-scope messages per second over
    /// the 6 minutes an entry is kept (pickup polling plus acknowledgements) — far above legitimate
    /// use, far below
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
    /// seconds; <see cref="ClockSkew"/> zero or more and at most <see cref="int.MaxValue"/> seconds;
    /// <see cref="MaxEntries"/>, <see cref="MaxEntriesPerPeer"/> and
    /// <see cref="MaxMessageIdLength"/> at least 1; <see cref="MaxEntriesPerPeer"/> no larger than
    /// <see cref="MaxEntries"/>. Throws <see cref="InvalidOperationException"/> naming the offending
    /// property.
    /// </summary>
    public void Validate()
    {
        if (Window <= TimeSpan.Zero || Window.TotalSeconds > int.MaxValue)
            throw new InvalidOperationException($"{nameof(ReplayProtectionOptions)}.{nameof(Window)} must be positive and at most int.MaxValue seconds.");
        if (ClockSkew < TimeSpan.Zero || ClockSkew.TotalSeconds > int.MaxValue)
            throw new InvalidOperationException($"{nameof(ReplayProtectionOptions)}.{nameof(ClockSkew)} must be zero or more and at most int.MaxValue seconds.");
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
