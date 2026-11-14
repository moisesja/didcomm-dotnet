using DidComm.Facade;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// Unpacks delivered messages under the bounds of design D-19 / §4.9: one deadline
/// (<see cref="Timeout"/>, a guarded <see cref="TimeProvider"/> timer plus <c>WaitAsync</c>, never a
/// <c>CancelAfter</c> token handed to host code) covering every re-wrap layer; at most
/// <see cref="FetchConcurrency"/> unpacks at a time per batch; and at most
/// <see cref="MaxAbandoned"/> abandoned unpacks still running process-wide (beyond which a new
/// unpack fails at once as a failure outcome). An unpack that does not honour cancellation keeps its
/// abandoned slot until it ends (L-059). A bad message is a failure outcome, never an exception;
/// only the caller's cancellation throws. Owner: engineer A.
/// </summary>
internal sealed class DeliveredMessageUnpacker
{
    /// <summary>How many messages of one fetch are unpacked at the same time.</summary>
    internal const int FetchConcurrency = 4;

    /// <summary>The bound on abandoned unpacks (deadline passed, work still running) before new unpacks fail fast.</summary>
    internal const int MaxAbandoned = 16;

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

    /// <summary>Unpack one delivered message (UTF-8 packed bytes).</summary>
    /// <param name="payload">The delivered bytes.</param>
    /// <param name="ct">The caller's cancellation; it alone makes the call throw.</param>
    internal Task<DeliveredOutcome> UnpackAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        => throw new NotImplementedException("#96 engineer A: DeliveredMessageUnpacker.UnpackAsync (design §4.9).");

    /// <summary>Unpack a batch, at most <see cref="FetchConcurrency"/> at a time; the outcomes are in the order of <paramref name="payloads"/>.</summary>
    /// <param name="payloads">The delivered bytes, in delivery order.</param>
    /// <param name="ct">The caller's cancellation; it alone makes the call throw.</param>
    internal Task<IReadOnlyList<DeliveredOutcome>> UnpackAllAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct)
        => throw new NotImplementedException("#96 engineer A: DeliveredMessageUnpacker.UnpackAllAsync (design §4.9).");
}
