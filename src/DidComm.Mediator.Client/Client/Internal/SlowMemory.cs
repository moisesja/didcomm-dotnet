using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// The memory of delivered messages that did not open (#111, I-9..I-12, D-2, D-3, D-8): per
/// (mediator, mailbox), the mediator's attachment ids of messages whose unpack ran and failed on
/// its own, or had the whole budget and did not open (the unpacker decides: S1 and S2, I-9), each
/// with a pause that doubles each time it does so again. While its pause lasts, a remembered message
/// is not opened again (<see cref="HeldBack"/>), so a delivery made only of such messages ends at
/// once and a fetch loop reaches what is behind them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What an entry counts:</strong> how many times the message did not open (its strikes) and
/// the clock reading its pause is counted from (<c>struckAt</c>: the last strike's, or a lower one
/// read at a repeat failure after the clock stepped back). It is written only by the unpacker, from
/// an outcome it observed itself (<see cref="Strike"/>), and removed when the message opens
/// (<see cref="Forget"/>). Nothing a sender writes enters a key or decides a write.
/// </para>
/// <para>
/// <strong>The pause:</strong> a message is held back while
/// <c>struckAt ≤ now &lt; struckAt + min(cap, t0 · 2^(strikes − 1))</c>. A reading below
/// <c>struckAt</c> (a clock that stepped back) counts as due (I-12), so after a step back the message
/// is opened once more
/// (<c>SlowMemoryTests.M2_a_reading_below_the_strikes_is_due_and_a_stepped_back_clock_never_holds_back_past_the_pause</c>).
/// When that opening fails too, it is the same episode, not a new one (<see cref="Strike"/>): the
/// entry keeps its strikes and is stamped with the lower reading, so the message is held back for
/// the same pause again, counted from that failure, not for the next, doubled one
/// (<c>SlowMemoryClockStepTests.A_repeat_failure_read_below_the_last_strike_keeps_the_strikes</c>,
/// <c>…A_repeat_failure_read_below_the_last_strike_is_stamped_with_the_lower_reading</c>;
/// <c>SlowMemoryRt2BTests.After_a_clock_step_back_a_message_that_fails_again_is_held_back_for_the_same_pause_again_and_not_for_the_doubled_one</c>).
/// So a step back can make a message due later than without it: in the measured case half a minute
/// later, at minute 30.5 instead of minute 30
/// (<c>SlowMemoryClockStepTests.After_a_step_back_the_message_is_due_one_pause_after_its_repeat_failure</c>).
/// Each further step back costs one more opening
/// (<c>…A_clock_that_steps_back_five_times_costs_one_opening_per_step_and_the_pause_keeps_its_length</c>).
/// </para>
/// <para>
/// <strong>Keys (D-3, D-8):</strong> a partition is SHA-256, truncated to 128 bits, over the mediator's
/// DID and the mailbox's DID (both from the host's own call, compared exactly); an entry is the same
/// hash over the partition's key and the attachment id's exact characters. The mediator is in the
/// key because attachment ids are unique only per mediator.
/// </para>
/// <para>
/// <strong>Bounds (I-11):</strong> at most <c>r</c> partitions of at most <c>q</c> entries. Beyond
/// <c>q</c>, a partition drops its least recently seen entry; beyond <c>r</c>, the least recently used
/// partition goes. Both orders come from a sequence counter, never from a clock. What an eviction
/// costs: one more opening of that message when it is delivered again, as after a restart (the
/// memory is in-process only). With more than <c>q</c> messages that never open queued in one
/// mailbox, each new entry evicts one, so they are not all held back at once and a fetch loop alone
/// does not reach the message behind them; a host that discards the failed items it has given up
/// on with <c>AckAsync</c>, never an inconclusive one, does
/// (R-4:
/// <c>SlowSenderFetchQa1Tests.Residual_the_message_behind_more_never_opening_messages_than_the_memory_keeps_is_reached_once_the_host_discards_them</c>).
/// A partition whose last entry is forgotten is removed.
/// </para>
/// <para>
/// <strong>What the memory does not outlast (R-5):</strong> it lives in the process, and its longest
/// pause is <c>cap</c> (256 budgets in production, about 85 minutes at the default budget). With
/// sixteen or more messages that never open in front of another in one delivery, deliveries further
/// apart than that pause (a loop run every two hours; a lease of two hours) or a new process for
/// each loop find nothing held back, open the sixteen again, and do not return the message behind
/// them: not in twelve runs, and not in two simulated days
/// (<c>SlowSenderSpacedDeliveriesRt3Tests.Residual_with_sixteen_or_more_in_front_in_one_delivery_and_deliveries_further_apart_than_the_longest_pause_or_a_new_process_each_time_the_message_behind_is_not_returned</c>).
/// Run again five minutes later in the same process, a fetch loop returns it in its second run
/// (<c>…With_sixteen_or_more_in_front_in_one_delivery_a_fetch_loop_run_every_five_minutes_in_one_process_returns_the_message_behind_them_in_its_second_run</c>).
/// This library's mediator delivers at most ten per fetch at its defaults, so it takes another
/// mediator, or a larger <c>MessagePickupOptions.MaxDeliveryBatch</c>.
/// </para>
/// <para>
/// One leaf lock protects the partitions and the counter: no other lock is taken while it is held,
/// no host code runs under it (the clock is read before it), and each operation is O(q + r) under it.
/// One instance per unpacker, so one per channel (container).
/// </para>
/// </remarks>
internal sealed class SlowMemory
{
    /// <summary>Q: the most entries one (mediator, mailbox) partition keeps.</summary>
    internal const int EntriesPerPartition = 256;

    /// <summary>R: the most (mediator, mailbox) partitions kept.</summary>
    internal const int MaxPartitions = 256;

    private readonly object _gate = new();
    private readonly Dictionary<UInt128, Partition> _partitions = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _t0;
    private readonly TimeSpan _cap;
    private readonly int _q;
    private readonly int _r;
    private long _seq;

    /// <summary>Create a memory.</summary>
    /// <param name="time">The clock the pauses are read on.</param>
    /// <param name="t0">The first pause.</param>
    /// <param name="cap">The longest pause.</param>
    /// <param name="q">The most entries per partition.</param>
    /// <param name="r">The most partitions.</param>
    internal SlowMemory(TimeProvider time, TimeSpan t0, TimeSpan cap, int q, int r)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(t0, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, t0);
        ArgumentOutOfRangeException.ThrowIfLessThan(q, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(r, 1);
        _time = time;
        _t0 = t0;
        _cap = cap;
        _q = q;
        _r = r;
    }

    /// <summary>The first pause.</summary>
    internal TimeSpan FirstPause => _t0;

    /// <summary>The longest pause.</summary>
    internal TimeSpan LongestPause => _cap;

    /// <summary>How many messages are remembered now, in all partitions (test seam).</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                var count = 0;
                foreach (var partition in _partitions.Values)
                    count += partition.Entries.Count;
                return count;
            }
        }
    }

    /// <summary>How many (mediator, mailbox) partitions are kept now (test seam).</summary>
    internal int Partitions
    {
        get
        {
            lock (_gate)
                return _partitions.Count;
        }
    }

    /// <summary>
    /// The pause after <paramref name="strikes"/> strikes: <c>min(cap, t0 · 2^(strikes − 1))</c>.
    /// </summary>
    /// <param name="strikes">How many times the message did not open (at least 1).</param>
    internal TimeSpan PauseAfter(int strikes)
    {
        var ticks = _t0.Ticks;
        for (var k = 1; k < strikes && ticks < _cap.Ticks; k++)
            ticks = ticks > _cap.Ticks / 2 ? _cap.Ticks : ticks * 2;
        return TimeSpan.FromTicks(Math.Min(ticks, _cap.Ticks));
    }

    /// <summary>
    /// For each id of one delivery, whether the message is held back now; under one lock for the
    /// whole delivery. A known id counts as seen (it moves to the back of its partition's eviction
    /// order) and its partition as used. A <c>null</c> id is never held back. Creates nothing.
    /// </summary>
    /// <param name="scope">The mediator and mailbox the delivery came from.</param>
    /// <param name="ids">The delivered messages' attachment ids, in delivery order.</param>
    /// <returns>One flag per id, in the same order.</returns>
    internal bool[] HeldBack(DeliveryScope scope, IReadOnlyList<string?> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var held = new bool[ids.Count];
        if (ids.Count == 0)
            return held;
        var partitionKey = ScopeKeyOf(scope);
        var keys = new UInt128?[ids.Count];
        for (var i = 0; i < ids.Count; i++)
            keys[i] = ids[i] is { } id ? EntryKeyOf(partitionKey, id) : null;
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            if (!_partitions.TryGetValue(partitionKey, out var partition))
                return held;
            partition.Used = ++_seq;
            for (var i = 0; i < keys.Length; i++)
            {
                if (keys[i] is not { } key)
                    continue;
                ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(partition.Entries, key);
                if (Unsafe.IsNullRef(ref entry))
                    continue;
                entry.Seen = ++_seq;
                held[i] = IsPaused(entry, now);
            }
        }
        return held;
    }

    /// <summary>
    /// Remember that the message <paramref name="id"/> did not open: a new entry with one strike, or
    /// one more strike on the entry it has, stamped with the clock's reading now. Two cases add no
    /// strike, because they are the episode the entry already counts. An entry still inside its pause
    /// is left as it is: deliveries of the same message that ran at the same time saw one episode
    /// (RT1B-2). An entry whose last strike was stamped with a higher reading than now (a host clock
    /// that stepped back, or two deliveries whose readings reach the memory out of order) keeps its
    /// strikes and is stamped with the reading now, so its pause, of the same length, is counted from
    /// the lower reading. In every case the entry counts as seen (it moves to the back of its
    /// partition's eviction order) and its partition as used. Does nothing for a <c>null</c> id.
    /// </summary>
    /// <param name="scope">The mediator and mailbox the message came from.</param>
    /// <param name="id">Its attachment id.</param>
    internal void Strike(DeliveryScope scope, string? id)
    {
        if (id is null)
            return;
        var partitionKey = ScopeKeyOf(scope);
        var key = EntryKeyOf(partitionKey, id);
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            if (!_partitions.TryGetValue(partitionKey, out var partition))
            {
                if (_partitions.Count >= _r)
                    _partitions.Remove(LeastRecentlyUsedPartition());
                partition = new Partition();
                _partitions.Add(partitionKey, partition);
            }
            partition.Used = ++_seq;
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(partition.Entries, key, out var existed);
            entry.Seen = ++_seq;
            if (!existed)
            {
                entry.Strikes = 1;
                entry.StruckAt = now;
                if (partition.Entries.Count > _q)
                    partition.Entries.Remove(LeastRecentlySeenEntry(partition));
                return;
            }
            if (now < entry.StruckAt)
            {
                // The same episode on a clock that stepped back: no further strike, and the pause,
                // of the same length, runs from the lower reading.
                entry.StruckAt = now;
                return;
            }
            if (IsPaused(entry, now))
                return; // the same episode, seen by another delivery inside the pause (RT1B-2)
            entry.Strikes = entry.Strikes == int.MaxValue ? int.MaxValue : entry.Strikes + 1;
            entry.StruckAt = now;
        }
    }

    /// <summary>Forget the message <paramref name="id"/> (it opened). Does nothing for a <c>null</c> or unknown id.</summary>
    /// <param name="scope">The mediator and mailbox the message came from.</param>
    /// <param name="id">Its attachment id.</param>
    internal void Forget(DeliveryScope scope, string? id)
    {
        if (id is null)
            return;
        var partitionKey = ScopeKeyOf(scope);
        var key = EntryKeyOf(partitionKey, id);
        lock (_gate)
        {
            if (!_partitions.TryGetValue(partitionKey, out var partition))
                return;
            if (partition.Entries.Remove(key) && partition.Entries.Count == 0)
                _partitions.Remove(partitionKey);
        }
    }

    /// <summary>
    /// The key of a (mediator, mailbox) partition: SHA-256 truncated to 128 bits over the two DIDs,
    /// length-prefixed. Also the scope of <see cref="UnpackReservations"/>.
    /// </summary>
    /// <param name="scope">The mediator and mailbox.</param>
    internal static UInt128 ScopeKeyOf(DeliveryScope scope)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("didcomm-dotnet/pickup-slow/scope\n"u8);
        AppendField(hash, Encoding.UTF8.GetBytes(scope.MediatorDid ?? string.Empty));
        AppendField(hash, Encoding.UTF8.GetBytes(scope.LocalDid ?? string.Empty));
        return Digest(hash);
    }

    // The entry's key: over the partition's key and the id's exact UTF-16 code units, so ids that
    // differ in any character (case included, D-8) never share a key, which a lossy encoding of an
    // unpaired surrogate could not promise.
    private static UInt128 EntryKeyOf(UInt128 partitionKey, string id)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("didcomm-dotnet/pickup-slow/entry\n"u8);
        Span<byte> scope = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(scope, partitionKey);
        hash.AppendData(scope);
        AppendField(hash, MemoryMarshal.AsBytes(id.AsSpan()));
        return Digest(hash);
    }

    private static void AppendField(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static UInt128 Digest(IncrementalHash hash)
    {
        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return BinaryPrimitives.ReadUInt128BigEndian(digest[..16]);
    }

    private bool IsPaused(in Entry entry, long now)
    {
        if (now < entry.StruckAt)
            return false; // fail open: a reading below the strike's is due (I-12)
        return _time.GetElapsedTime(entry.StruckAt, now) < PauseAfter(entry.Strikes);
    }

    // Under _gate.
    private UInt128 LeastRecentlyUsedPartition()
    {
        var oldest = default(UInt128);
        var used = long.MaxValue;
        foreach (var (key, partition) in _partitions)
        {
            if (partition.Used < used)
            {
                used = partition.Used;
                oldest = key;
            }
        }
        return oldest;
    }

    // Under _gate.
    private static UInt128 LeastRecentlySeenEntry(Partition partition)
    {
        var oldest = default(UInt128);
        var seen = long.MaxValue;
        foreach (var (key, entry) in partition.Entries)
        {
            if (entry.Seen < seen)
            {
                seen = entry.Seen;
                oldest = key;
            }
        }
        return oldest;
    }

    private sealed class Partition
    {
        internal Dictionary<UInt128, Entry> Entries { get; } = new();

        internal long Used { get; set; }
    }

    private struct Entry
    {
        internal int Strikes;
        internal long StruckAt;
        internal long Seen;
    }
}
