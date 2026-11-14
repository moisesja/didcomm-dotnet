using System.Globalization;
using System.Text;
using DidComm.Exceptions;
using DidComm.Facade;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// Unpacks delivered messages under the bounds of design D-19 / §4.9: one deadline
/// (<see cref="Timeout"/>, a <see cref="GuardedDeadline"/> on the injected <see cref="TimeProvider"/>
/// plus <c>WaitAsync</c>, never a <c>CancelAfter</c> token handed to host code) covering every
/// re-wrap layer; at most <see cref="FetchConcurrency"/> unpacks at a time per batch; and at most
/// <see cref="MaxAbandoned"/> abandoned unpacks of this unpacker (one per channel, so one per
/// container) still running, beyond which a new unpack fails at once as a failure outcome without
/// starting. An unpack that does not honour cancellation keeps its abandoned slot until it ends
/// (L-059). A bad message is a failure outcome, never an exception; only the caller's cancellation
/// throws. Owner: engineer A.
/// </summary>
internal sealed class DeliveredMessageUnpacker
{
    /// <summary>How many messages of one fetch are unpacked at the same time.</summary>
    internal const int FetchConcurrency = 4;

    /// <summary>The bound on abandoned unpacks (deadline passed, work still running) before new unpacks fail fast.</summary>
    internal const int MaxAbandoned = 16;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private int _abandoned;

    /// <summary>Create an unpacker.</summary>
    /// <param name="unpack">The unpack step: <see cref="DidCommClient.UnpackDeliveredAsync"/> in production, a gated fake in tests.</param>
    /// <param name="timeout">The per-message deadline (<see cref="MediatorClientOptions.UnpackTimeout"/>).</param>
    /// <param name="time">The clock of the deadline timers.</param>
    internal DeliveredMessageUnpacker(Func<string, CancellationToken, Task<DeliveredUnpack>> unpack, TimeSpan timeout, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(unpack);
        ArgumentNullException.ThrowIfNull(time);
        Unpack = unpack;
        Timeout = timeout;
        Time = time;
    }

    /// <summary>The unpack step.</summary>
    internal Func<string, CancellationToken, Task<DeliveredUnpack>> Unpack { get; }

    /// <summary>The per-message deadline.</summary>
    internal TimeSpan Timeout { get; }

    /// <summary>The clock of the deadline timers.</summary>
    internal TimeProvider Time { get; }

    /// <summary>How many abandoned unpacks are still running now (test seam).</summary>
    internal int Abandoned => Volatile.Read(ref _abandoned);

    /// <summary>Unpack one delivered message (UTF-8 packed bytes).</summary>
    /// <param name="payload">The delivered bytes.</param>
    /// <param name="ct">The caller's cancellation; it alone makes the call throw.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    internal async Task<DeliveredOutcome> UnpackAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // The bound is checked before any work starts (L-055, L-059).
        if (Volatile.Read(ref _abandoned) >= MaxAbandoned)
        {
            return DeliveredOutcome.Failure(new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                $"The delivered message was not unpacked: {MaxAbandoned} earlier unpacks passed their deadline and are still running (unpack capacity exhausted).")));
        }

        string packed;
        try
        {
            packed = StrictUtf8.GetString(payload.Span);
        }
        catch (DecoderFallbackException ex)
        {
            return DeliveredOutcome.Failure(new MalformedMessageException("The delivered message is not valid UTF-8.", ex));
        }
        if (packed.Length == 0)
            return DeliveredOutcome.Failure(new MalformedMessageException("The delivered message is empty."));

        using var deadline = new GuardedDeadline(Time, Timeout, ct);
        Task<DeliveredUnpack> work;
        try
        {
            work = Unpack(packed, deadline.Token);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            return DeliveredOutcome.Failure(ex);
        }

        try
        {
            var unpacked = await work.WaitAsync(deadline.Token).ConfigureAwait(false);
            return DeliveredOutcome.Success(unpacked.Result, unpacked.LayersOpened, DuplicateFilter.KeyOf(unpacked.Result, unpacked.InnermostPacked));
        }
        catch (OperationCanceledException) when (deadline.Token.IsCancellationRequested)
        {
            if (!work.IsCompleted)
                Abandon(work);
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            return DeliveredOutcome.Failure(new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                $"The delivered message was not unpacked within {Timeout} (MediatorClientOptions.UnpackTimeout).")));
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            return DeliveredOutcome.Failure(ex);
        }
    }

    /// <summary>Unpack a batch, at most <see cref="FetchConcurrency"/> at a time; the outcomes are in the order of <paramref name="payloads"/>.</summary>
    /// <param name="payloads">The delivered bytes, in delivery order.</param>
    /// <param name="ct">The caller's cancellation; it alone makes the call throw.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    internal async Task<IReadOnlyList<DeliveredOutcome>> UnpackAllAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        ct.ThrowIfCancellationRequested();
        if (payloads.Count == 0)
            return Array.Empty<DeliveredOutcome>();

        var outcomes = new DeliveredOutcome[payloads.Count];
        using var gate = new SemaphoreSlim(FetchConcurrency, FetchConcurrency);
        var running = new Task[payloads.Count];
        for (var i = 0; i < payloads.Count; i++)
            running[i] = UnpackOneAsync(i);
        // Every task ends: each unpack is bounded by its deadline, and a gate wait by ct.
        await Task.WhenAll(running).ConfigureAwait(false);
        return outcomes;

        async Task UnpackOneAsync(int index)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                outcomes[index] = await UnpackAsync(payloads[index], ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    // The unpack passed its deadline but still runs: it keeps an abandoned slot until it ends.
    private void Abandon(Task work)
    {
        Interlocked.Increment(ref _abandoned);
        work.ContinueWith(
            static (_, state) => Interlocked.Decrement(ref ((DeliveredMessageUnpacker)state!)._abandoned),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
