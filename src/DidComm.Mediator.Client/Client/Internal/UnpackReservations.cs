using System.Diagnostics.CodeAnalysis;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// The bounds on host unpack calls of one container (#111, I-1, I-2, D-4, D-7): at most
/// <see cref="MaxReserved"/> calls started and not yet ended in all, and per (mediator, mailbox,
/// sender) at most <see cref="MaxPerSender"/> for a named sender (<see cref="SenderPartition"/>) or
/// <see cref="MaxPerAnonymous"/> for the mailbox's anonymous partition. Also where a refused take
/// reaches the messages that were let run past their share (owner decision A1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What a reservation counts:</strong> one host unpack call (the DID and secrets resolvers
/// behind it), from before its work item exists until it ends for any reason: whether its worker
/// still waits for it or has let it go at its share or at the budget.
/// </para>
/// <para>
/// <strong>When it is taken:</strong> by <see cref="TryTake"/>, the check and the take in one step
/// under one lock, before the work item is created. A refusal starts nothing: the message fails at
/// once and is not acknowledged.
/// </para>
/// <para>
/// <strong>When it is released:</strong> exactly once (<see cref="Reservation.Release"/>): by the
/// worker as soon as the call it awaited has ended, before it takes its next message (D-13); by a
/// continuation on the call's task when it ends after its worker let it go (including a call whose
/// token was cancelled before it ran); or directly when the work item is never created.
/// </para>
/// <para>
/// <strong>Refusals and extended messages (A1).</strong> A message whose share's time is up while no
/// other message of its delivery waits is let run on (<see cref="Reservation.TryExtend"/>), unless a
/// take was refused for its partition or for the container since it was reserved. Each refusal is
/// counted, and it cuts the extended messages it is waiting behind: a refusal for a partition cuts
/// that partition's extended messages, a refusal for the container cuts every extended message. The
/// cuts are made after the lock, on the thread pool, so no host callback runs under the lock or on the
/// refused caller's stack. A cut keeps the reservation until the call ends. At most one registration
/// per reservation, so the extended messages number at most <see cref="MaxReserved"/>.
/// </para>
/// <para>
/// One leaf lock protects the total, the per-partition counts and refusals, the container's
/// refusals and the extended registrations: no other lock is taken while it is held, and no host
/// code runs under it. A partition's entry is removed when its count reaches zero, so the table never
/// holds more than <see cref="MaxReserved"/> entries. One instance per unpacker, so one per channel
/// (container).
/// </para>
/// </remarks>
internal sealed class UnpackReservations
{
    /// <summary>C: host unpack calls started and not yet ended in one container, all mailboxes and senders together.</summary>
    internal const int MaxReserved = 64;

    /// <summary>P for a named sender: host unpack calls started and not yet ended per (mediator, mailbox, sender DID).</summary>
    internal const int MaxPerSender = 4;

    /// <summary>P for the anonymous partition: host unpack calls started and not yet ended per (mediator, mailbox) for messages without a named sender.</summary>
    internal const int MaxPerAnonymous = 16;

    private readonly object _gate = new();
    private readonly Dictionary<(UInt128 Scope, string? Sender), Partition> _partitions = new();
    private readonly TaskScheduler _releaseOn;
    private int _total;
    private long _containerRefusals;
    private int _extended;

    /// <summary>Create the reservations of one container.</summary>
    /// <param name="releaseOn">Where the release continuation of <see cref="Reservation.ReleaseWhen"/> runs: <see cref="TaskScheduler.Default"/> (inline on the thread that ends the call) unless a test holds it back to prove the worker gives its reservation back itself (D-13).</param>
    internal UnpackReservations(TaskScheduler? releaseOn = null) => _releaseOn = releaseOn ?? TaskScheduler.Default;

    /// <summary>Which bound refused a reservation.</summary>
    internal enum Refusal
    {
        /// <summary>Nothing was refused.</summary>
        None,

        /// <summary>The message's partition (its sender, or the mailbox's anonymous partition) is at its bound.</summary>
        Partition,

        /// <summary>The container is at <see cref="MaxReserved"/>.</summary>
        Container,
    }

    /// <summary>How many host unpack calls are reserved now: started and not yet ended (test seam).</summary>
    internal int Reserved
    {
        get
        {
            lock (_gate)
                return _total;
        }
    }

    /// <summary>How many partitions hold a reservation now (test seam).</summary>
    internal int Partitions
    {
        get
        {
            lock (_gate)
                return _partitions.Count;
        }
    }

    /// <summary>How many reserved calls are let run past their share now (test seam).</summary>
    internal int Extended
    {
        get
        {
            lock (_gate)
                return _extended;
        }
    }

    /// <summary>
    /// Reserve one host unpack call for <paramref name="sender"/> in <paramref name="scope"/>, or
    /// refuse: the check and the take are one step. A refusal is counted and cuts the extended
    /// messages it waits behind (see the remarks).
    /// </summary>
    /// <param name="scope">The mediator and mailbox the message came from.</param>
    /// <param name="sender">The sender's DID subject (<see cref="SenderPartition.Of"/>), or <c>null</c> for the mailbox's anonymous partition.</param>
    /// <param name="reservation">The reservation, released exactly once; <c>null</c> when refused.</param>
    /// <param name="refusal">Which bound refused; <see cref="Refusal.None"/> when taken.</param>
    /// <returns>Whether the reservation was taken.</returns>
    internal bool TryTake(DeliveryScope scope, string? sender, [NotNullWhen(true)] out Reservation? reservation, out Refusal refusal)
    {
        var key = (SlowMemory.ScopeKeyOf(scope), sender);
        var limit = sender is null ? MaxPerAnonymous : MaxPerSender;
        List<CancellationTokenSource>? cuts = null;
        lock (_gate)
        {
            _partitions.TryGetValue(key, out var partition);
            if (partition is not null && partition.Count >= limit)
            {
                partition.Refusals++;
                TakeExtended(partition, ref cuts);
                refusal = Refusal.Partition;
            }
            else if (_total >= MaxReserved)
            {
                _containerRefusals++;
                foreach (var each in _partitions.Values)
                    TakeExtended(each, ref cuts);
                refusal = Refusal.Container;
            }
            else
            {
                if (partition is null)
                {
                    partition = new Partition();
                    _partitions.Add(key, partition);
                }
                partition.Count++;
                _total++;
                reservation = new Reservation(this, key, partition, partition.Refusals, _containerRefusals);
                refusal = Refusal.None;
                return true;
            }
        }
        if (cuts is not null)
        {
            foreach (var source in cuts)
                ThreadPool.UnsafeQueueUserWorkItem(static s => GuardedDeadline.CancelQuietly(s), source, preferLocal: false);
        }
        reservation = null;
        return false;
    }

    // Under _gate: the partition's extended registrations move to `cuts` and end.
    private void TakeExtended(Partition partition, ref List<CancellationTokenSource>? cuts)
    {
        if (partition.Extended is not { Count: > 0 } extended)
            return;
        cuts ??= new List<CancellationTokenSource>();
        foreach (var each in extended)
        {
            cuts.Add(each.Cut!);
            each.Cut = null;
        }
        _extended -= extended.Count;
        extended.Clear();
    }

    private void Give(Reservation reservation)
    {
        lock (_gate)
        {
            var partition = reservation.Partition;
            if (reservation.Cut is not null)
            {
                partition.Extended!.Remove(reservation);
                reservation.Cut = null;
                _extended--;
            }
            if (--partition.Count == 0)
                _partitions.Remove(reservation.Key);
            _total--;
        }
    }

    /// <summary>One partition's state, under the owner's lock: its reservations, the refusals made for it, and its extended reservations.</summary>
    internal sealed class Partition
    {
        /// <summary>Reservations held.</summary>
        internal int Count;

        /// <summary>Takes refused for this partition while it held reservations.</summary>
        internal long Refusals;

        /// <summary>The reservations let run past their share, waiting for a refusal to cut them.</summary>
        internal List<Reservation>? Extended;
    }

    /// <summary>
    /// One reserved host unpack call. <see cref="Release"/> gives it back at most once, whoever calls
    /// it first: the worker that awaited the call, or the continuation <see cref="ReleaseWhen"/>
    /// attached. It references its owner, its partition and, while it is extended, its share's token
    /// source; never a delivery (I-4).
    /// </summary>
    internal sealed class Reservation
    {
        private readonly UnpackReservations _owner;
        private readonly long _partitionRefusalsAtTake;
        private readonly long _containerRefusalsAtTake;
        private int _released;

        internal Reservation(UnpackReservations owner, (UInt128 Scope, string? Sender) key, Partition partition, long partitionRefusals, long containerRefusals)
        {
            _owner = owner;
            Key = key;
            Partition = partition;
            _partitionRefusalsAtTake = partitionRefusals;
            _containerRefusalsAtTake = containerRefusals;
        }

        /// <summary>The partition's key.</summary>
        internal (UInt128 Scope, string? Sender) Key { get; }

        /// <summary>The partition (its entry stays while this reservation is held).</summary>
        internal Partition Partition { get; }

        /// <summary>Under the owner's lock: the share's token source while the call is extended, else <c>null</c>.</summary>
        internal CancellationTokenSource? Cut { get; set; }

        /// <summary>Give the reservation back; every call after the first does nothing.</summary>
        internal void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _owner.Give(this);
        }

        /// <summary>
        /// Let the call run past its share (A1), or not: <c>false</c> when a take was refused for this
        /// call's partition or for the container since it was reserved (something waits behind it), or
        /// when it was already released. Otherwise <paramref name="share"/> is registered, and the next
        /// refusal for its partition or for the container cancels it.
        /// </summary>
        /// <param name="share">The call's share token source.</param>
        /// <returns>Whether the call is extended.</returns>
        internal bool TryExtend(CancellationTokenSource share)
        {
            ArgumentNullException.ThrowIfNull(share);
            lock (_owner._gate)
            {
                if (Volatile.Read(ref _released) != 0
                    || Partition.Refusals > _partitionRefusalsAtTake
                    || _owner._containerRefusals > _containerRefusalsAtTake)
                    return false;
                if (Cut is null)
                {
                    (Partition.Extended ??= new List<Reservation>()).Add(this);
                    _owner._extended++;
                }
                Cut = share;
                return true;
            }
        }

        /// <summary>
        /// Give the reservation back when <paramref name="host"/> ends, whatever its outcome (its
        /// fault is observed, so a call let go that fails later is never an unobserved exception).
        /// Runs at once when it has already ended.
        /// </summary>
        /// <param name="host">The host unpack call's task.</param>
        internal void ReleaseWhen(Task host)
        {
            ArgumentNullException.ThrowIfNull(host);
            host.ContinueWith(
                static (t, state) =>
                {
                    _ = t.Exception;
                    ((Reservation)state!).Release();
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                _owner._releaseOn);
        }
    }
}
