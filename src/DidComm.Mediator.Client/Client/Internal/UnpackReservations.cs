using System.Diagnostics.CodeAnalysis;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// The bounds on host unpack calls of one container (#111, I-1, I-2, D-4, D-7): at most
/// <see cref="MaxReserved"/> calls started and not yet ended in all, and per (mediator, mailbox,
/// sender) at most <see cref="MaxPerSender"/> for a named sender (<see cref="SenderPartition"/>) or
/// <see cref="MaxPerAnonymous"/> for the mailbox's anonymous partition.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What a reservation counts:</strong> one host unpack call (the DID and secrets resolvers
/// behind it), from before its work item exists until it ends for any reason: whether its worker
/// still waits for it or its delivery has ended and let it go.
/// </para>
/// <para>
/// <strong>When it is taken:</strong> by <see cref="TryTake"/>, the check and the take in one step
/// under one lock, before the work item is created. A refusal starts nothing: the message fails at
/// once and is not acknowledged. A refusal changes nothing else: it is not counted, and it ends no
/// call that is running.
/// </para>
/// <para>
/// <strong>When it is released:</strong> exactly once (<see cref="Reservation.Release"/>): by the
/// worker as soon as the call it awaited has ended, before it takes its next message (D-13); by a
/// continuation on the call's task when it ends after its delivery did (including a call whose
/// token was cancelled before it ran); or directly when the work item is never created.
/// </para>
/// <para>
/// <strong>What a reservation still held does to the next delivery (R-13):</strong> a call whose
/// token was cancelled keeps its reservation until it ends, and a call that never got a thread keeps
/// it until the thread pool reaches its cancelled work item
/// (<c>InconclusiveBusyPoolRt4Tests.A_host_call_that_never_got_a_thread_keeps_its_reservation_until_the_pool_reaches_it_and_its_senders_next_message_is_refused_meanwhile_and_started_after</c>).
/// A delivery that arrives before that finds the partition's count where the delivery before left
/// it, and <see cref="TryTake"/> refuses its messages of that partition beyond the reservations
/// still free (they are inconclusive); other partitions are not affected
/// (<c>DeliveredUnpackNextDeliveryRt3Tests.While_the_lookups_of_the_delivery_before_are_still_ending_the_next_delivery_starts_what_the_bound_of_sixteen_leaves_free_and_refuses_the_rest</c>,
/// <c>…While_one_senders_lookups_of_the_delivery_before_are_still_ending_another_senders_message_opens_and_nothing_more_is_remembered</c>).
/// </para>
/// <para>
/// One leaf lock protects the total and the per-partition counts: no other lock is taken while it is
/// held, and no host code runs under it. A partition's entry is removed when its count reaches zero,
/// so the table never holds more than <see cref="MaxReserved"/> entries. One instance per unpacker,
/// so one per channel (container).
/// </para>
/// </remarks>
internal sealed class UnpackReservations
{
    /// <summary>
    /// C: host unpack calls started and not yet ended in one container, all mailboxes and senders
    /// together: 256 (#111, owner decision D). One sender at its bound of
    /// <see cref="MaxPerSender"/> in a mailbox needs 256 / 16 = 16 mailboxes to hold it all, and so
    /// do 16 senders at their bound in one mailbox.
    /// </summary>
    internal const int MaxReserved = 256;

    /// <summary>
    /// P for a named sender: host unpack calls started and not yet ended per (mediator, mailbox,
    /// sender DID): 16 (#111, owner decision G; I-23). Not below
    /// <see cref="DeliveredMessageUnpacker.FetchConcurrency"/>, how many messages one delivery opens
    /// at once, and the two change together: with a lower bound, messages of one sender are refused
    /// inside one delivery.
    /// </summary>
    internal const int MaxPerSender = 16;

    /// <summary>P for the anonymous partition: host unpack calls started and not yet ended per (mediator, mailbox) for messages without a named sender.</summary>
    internal const int MaxPerAnonymous = 16;

    private readonly object _gate = new();
    private readonly Dictionary<(UInt128 Scope, string? Sender), Partition> _partitions = new();
    private readonly TaskScheduler _releaseOn;
    private int _total;

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

    /// <summary>
    /// Reserve one host unpack call for <paramref name="sender"/> in <paramref name="scope"/>, or
    /// refuse: the check and the take are one step.
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
        lock (_gate)
        {
            _partitions.TryGetValue(key, out var partition);
            if (partition is not null && partition.Count >= limit)
            {
                refusal = Refusal.Partition;
            }
            else if (_total >= MaxReserved)
            {
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
                reservation = new Reservation(this, key, partition);
                refusal = Refusal.None;
                return true;
            }
        }
        reservation = null;
        return false;
    }

    private void Give(Reservation reservation)
    {
        lock (_gate)
        {
            if (--reservation.Partition.Count == 0)
                _partitions.Remove(reservation.Key);
            _total--;
        }
    }

    /// <summary>One partition's state, under the owner's lock: the reservations held for it.</summary>
    internal sealed class Partition
    {
        /// <summary>Reservations held.</summary>
        internal int Count;
    }

    /// <summary>
    /// One reserved host unpack call. <see cref="Release"/> gives it back at most once, whoever calls
    /// it first: the worker that awaited the call, or the continuation <see cref="ReleaseWhen"/>
    /// attached. It references its owner and its partition; never a delivery (I-4).
    /// </summary>
    internal sealed class Reservation
    {
        private readonly UnpackReservations _owner;
        private int _released;

        internal Reservation(UnpackReservations owner, (UInt128 Scope, string? Sender) key, Partition partition)
        {
            _owner = owner;
            Key = key;
            Partition = partition;
        }

        /// <summary>The partition's key.</summary>
        internal (UInt128 Scope, string? Sender) Key { get; }

        /// <summary>The partition (its entry stays while this reservation is held).</summary>
        internal Partition Partition { get; }

        /// <summary>Give the reservation back; every call after the first does nothing.</summary>
        internal void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _owner.Give(this);
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
