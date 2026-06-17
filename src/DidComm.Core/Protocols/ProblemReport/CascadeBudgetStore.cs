using System.Linq;

namespace DidComm.Protocols.ProblemReport;

/// <summary>
/// Bounded, thread-safe, process-local tracker for the FR-PROTO-10 cascade budget, kept
/// <strong>separate</strong> from the dispatcher's general thread-state store.
/// </summary>
/// <remarks>
/// <para>
/// The dispatcher writes one entry per inbound message's <c>thid</c> into the general store. Routing
/// the cascade budget through that same store let a flood of cheap fresh <c>thid</c>s evict — and
/// reset to zero — a victim <c>pthid</c>'s error budget, defeating the cascade guard (#36). This
/// tracker holds the budget separately, keyed only by the failing thread's <c>pthid</c> (so only
/// error reports create entries).
/// </para>
/// <para>
/// It <strong>owns its concurrency</strong>: <see cref="RecordErrorReport"/> does the whole
/// increment + threshold check + trip decision atomically under a single lock keyed by the pthid
/// string. (An earlier design locked on the per-pthid state object borrowed from an LRU store; under
/// eviction two concurrent callers could lock <em>different</em> instances for the same pthid and
/// double-emit the cascade-stop — the lock seam must not be an evictable value.) The single lock
/// serializes all callers; that is a deliberate trade-off — problem reports are rare events, so a
/// global lock is simpler and safe. A high-throughput in-process host would stripe locks by pthid; a
/// multi-host deployment supplies a distributed budget (same frontier as the eviction residual below).
/// </para>
/// <para>
/// Bounded at <see cref="DefaultMaxEntries"/> with approximate-LRU eviction that <em>prefers</em>
/// evicting not-yet-tripped entries, so a tripped thread's silenced decision survives a flood of
/// fresh pthids — without exempting tripped entries from eviction (if every entry is tripped the
/// oldest are still removed, so the store never grows without limit, per #36). Residual: a determined
/// attacker can still pressure the bound with distinct-pthid <em>error reports</em> (each does real
/// per-message work), but that is far more expensive than the original cheap-thid reset; a
/// distributed/persistent budget is the path to a hard guarantee for horizontally-scaled hosts.
/// </para>
/// </remarks>
public sealed class CascadeBudgetStore
{
    /// <summary>Default cap on tracked failing-thread budgets.</summary>
    public const int DefaultMaxEntries = 10_000;

    private sealed class Entry
    {
        public int ErrorCount;
        public bool NoticeSent;
        public long LastTouched;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _maxEntries;
    private readonly int _lowWaterMark;
    private long _tick;

    /// <summary>Create a cascade-budget tracker bounded at <paramref name="maxEntries"/> failing threads.</summary>
    /// <param name="maxEntries">Cap on tracked budgets; must be positive. Defaults to <see cref="DefaultMaxEntries"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEntries"/> is not positive.</exception>
    public CascadeBudgetStore(int maxEntries = DefaultMaxEntries)
    {
        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxEntries), maxEntries, "Cascade-budget capacity must be > 0.");
        }

        _maxEntries = maxEntries;
        // Trim toward ~90%, but ALWAYS strictly below the cap so Evict() actually frees a slot. A naive
        // Math.Max(1, …) leaves lowWater == cap for maxEntries == 1 (1/10 == 0): Evict() removes
        // Count − lowWater == 0 entries, the new entry is added anyway, and the store grows past its cap
        // without bound. Clamp to maxEntries − 1 (PR #40 review).
        _lowWaterMark = Math.Min(maxEntries - 1, Math.Max(1, maxEntries - Math.Max(1, maxEntries / 10)));
    }

    /// <summary>
    /// Atomically record one inbound error problem-report on the failing thread named by
    /// <paramref name="pthid"/> and return the FR-PROTO-10 cascade-guard decision.
    /// </summary>
    /// <param name="pthid">The failing thread's <c>pthid</c> (FR-PROTO-07, non-empty).</param>
    /// <param name="threshold">The per-thread error budget (<see cref="ProblemReportOptions.CascadeThreshold"/>).</param>
    /// <param name="repliable">Whether this report has a usable reply target (a cascade-stop can be addressed).</param>
    /// <returns>
    /// <see cref="CascadeStep.Emit"/> = build and send the cascade-stop; <see cref="CascadeStep.Log"/> =
    /// the count advanced and should be logged; <see cref="CascadeStep.Count"/> = the (clamped) count.
    /// </returns>
    internal CascadeStep RecordErrorReport(string pthid, int threshold, bool repliable)
    {
        lock (_gate)
        {
            var entry = GetOrCreate(pthid);

            // Notice already emitted → fully silent: no increment, no log, no work.
            if (entry.NoticeSent)
                return new CascadeStep(Emit: false, Log: false, Count: entry.ErrorCount);

            // Clamp at threshold+1: once a thread has breached, stop incrementing AND stop logging per
            // message — otherwise a sustained unrepliable (from-less) stream grows the counter and
            // floods the log forever, the trip permanently deferred (#29). The breach is counted and
            // logged exactly once.
            bool atCap = entry.ErrorCount > threshold;
            if (!atCap)
                entry.ErrorCount++;
            int count = entry.ErrorCount;

            if (count > threshold)
            {
                // Breach. A cascade-stop needs a repliable target; an anoncrypt/addressless report
                // DEFERS emission (NoticeSent stays false) so a later repliable report on the same
                // pthid can fire it, while the counter stays clamped so the unrepliable stream can't flood.
                if (!repliable)
                    return new CascadeStep(Emit: false, Log: !atCap, Count: count);

                entry.NoticeSent = true;
                return new CascadeStep(Emit: true, Log: !atCap, Count: count);
            }

            return new CascadeStep(Emit: false, Log: !atCap, Count: count);
        }
    }

    private Entry GetOrCreate(string pthid) // caller holds _gate
    {
        if (_entries.TryGetValue(pthid, out var entry))
        {
            entry.LastTouched = ++_tick;
            return entry;
        }

        if (_entries.Count >= _maxEntries)
            Evict();

        entry = new Entry { LastTouched = ++_tick };
        _entries[pthid] = entry;
        return entry;
    }

    private void Evict() // caller holds _gate
    {
        // Remove down to the low-water mark, preferring not-yet-tripped entries (NoticeSent=false
        // sorts first) and then oldest-touched. This keeps a tripped thread's silenced decision sticky
        // against a flood of fresh, non-tripped pthids — without exempting tripped entries (if all are
        // tripped the oldest are still removed, so the store can never grow without limit, #36).
        var victims = _entries
            .OrderBy(kv => kv.Value.NoticeSent)
            .ThenBy(kv => kv.Value.LastTouched)
            .Take(_entries.Count - _lowWaterMark)
            .Select(kv => kv.Key)
            .ToArray();

        foreach (var key in victims)
            _entries.Remove(key);
    }

    /// <summary>Current number of tracked failing-thread budgets. Exposed for tests.</summary>
    internal int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Read a thread's current budget without mutating it. Exposed for tests.</summary>
    internal (int ErrorCount, bool NoticeSent) Peek(string pthid)
    {
        lock (_gate)
            return _entries.TryGetValue(pthid, out var e) ? (e.ErrorCount, e.NoticeSent) : (0, false);
    }
}

/// <summary>The cascade-guard decision returned by <see cref="CascadeBudgetStore.RecordErrorReport"/>.</summary>
internal readonly record struct CascadeStep(bool Emit, bool Log, int Count);
