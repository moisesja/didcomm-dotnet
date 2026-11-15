using DidComm.Mediator.Internal;
using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// The log lines of <see cref="PickupClient"/> and its live streams (#96 design §6.1). Every line
/// carries only our own request ids, constant message names, counts, and exception type names —
/// never a DID, a key id, an attachment or queue id, a message id the mediator or a sender chose, or
/// an exception's text. Lines a mediator can cause again and again are rate-limited (Warning the
/// first time and every 1000th, Debug otherwise, per client), and every call goes through
/// <see cref="MediatorLog.Write"/>, so a throwing logging provider changes nothing.
/// </summary>
internal sealed class PickupLog
{
    private const int MaxTypeNames = 4;

    private readonly ILogger? _logger;
    private long _beyondLimit;
    private long _unusableIds;
    private long _repeatedIds;
    private long _unpackFailures;
    private long _ackFailures;
    private long _cycleFailures;
    private long _pushesIgnored;
    private long _takenDropped;
    private long _endFailures;
    private long _filterMismatches;

    /// <summary>Create the log of one client.</summary>
    /// <param name="logger">The client's logger, or <c>null</c> for none.</param>
    internal PickupLog(ILogger? logger) => _logger = logger;

    /// <summary>A delivery carried more attachments than were asked for; the rest were not read or acknowledged.</summary>
    internal void BeyondLimit(string? requestId, int count)
    {
        var level = MediatorLog.RateLimited(ref _beyondLimit, out var n);
        MediatorLog.Write(_logger, level, "Delivery for request {RequestId} carried {Count} message(s) beyond the limit; they were not opened or acknowledged and stay with the mediator. {Total} such deliveries.", Id(requestId), count, n);
    }

    /// <summary>Attachments without a usable id were ignored (they cannot be acknowledged).</summary>
    internal void UnusableIds(string? requestId, int count)
    {
        var level = MediatorLog.RateLimited(ref _unusableIds, out var n);
        MediatorLog.Write(_logger, level, "Delivery for request {RequestId} carried {Count} message(s) without a usable id; they were ignored. {Total} such deliveries.", Id(requestId), count, n);
    }

    /// <summary>Attachments repeating an earlier attachment's id were ignored (the first wins).</summary>
    internal void RepeatedIds(string? requestId, int count)
    {
        var level = MediatorLog.RateLimited(ref _repeatedIds, out var n);
        MediatorLog.Write(_logger, level, "Delivery for request {RequestId} repeated the id of {Count} message(s); the repeats were ignored. {Total} such deliveries.", Id(requestId), count, n);
    }

    /// <summary>Delivered messages did not unpack; they are returned as failed items and not acknowledged.</summary>
    internal void UnpackFailures(string? requestId, IEnumerable<Exception> errors)
    {
        var types = new List<string>();
        var count = 0;
        foreach (var error in errors)
        {
            count++;
            var name = error.GetType().Name;
            if (types.Count < MaxTypeNames && !types.Contains(name, StringComparer.Ordinal))
                types.Add(name);
        }
        if (count == 0)
            return;
        var level = MediatorLog.RateLimited(ref _unpackFailures, out var n);
        MediatorLog.Write(_logger, level, "{Count} delivered message(s) for request {RequestId} did not unpack ({Types}); they were not acknowledged and come back after the mediator's lease. {Total} such batches.", count, Id(requestId), string.Join(", ", types), n);
    }

    /// <summary>Delivered messages were decrypted for another DID than the request's filter.</summary>
    internal void FilterMismatches(string requestId, int count)
    {
        var level = MediatorLog.RateLimited(ref _filterMismatches, out var n);
        MediatorLog.Write(_logger, level, "{Count} delivered message(s) for request {RequestId} were for another DID than its recipient_did filter; they were not acknowledged. {Total} such batches.", count, Id(requestId), n);
    }

    /// <summary>An acknowledgement failed; the messages come back and are then recognised as repeats.</summary>
    internal void AckFailed(string requestId, int count, Exception error)
    {
        var level = MediatorLog.RateLimited(ref _ackFailures, out var n);
        MediatorLog.Write(_logger, level, "Acknowledgement {RequestId} of {Count} message(s) failed ({ExceptionType}); the mediator delivers them again and they are recognised as repeats. {Total} such failures.", Id(requestId), count, error.GetType().Name, n);
    }

    /// <summary>A fetch finished.</summary>
    internal void Fetched(string requestId, int returned, int failed, int repeats)
        => MediatorLog.Write(_logger, LogLevel.Debug, "Fetch {RequestId}: {Returned} message(s) returned and acknowledged, {Failed} failed, {Repeats} repeat(s) acknowledged.", Id(requestId), returned, failed, repeats);

    /// <summary>A live stream started.</summary>
    internal void LiveStarted()
        => MediatorLog.Write(_logger, LogLevel.Debug, "Live delivery stream started.");

    /// <summary>A live stream ended.</summary>
    internal void LiveEnded(int unacknowledged)
        => MediatorLog.Write(_logger, LogLevel.Debug, "Live delivery stream ended; {Count} confirmed message(s) left unacknowledged.", unacknowledged);

    /// <summary>A step of a live stream's cycle failed; the cycle runs again after the delay.</summary>
    internal void CycleFailed(string requestId, string name, Exception error, int inARow, TimeSpan retryIn)
    {
        var level = MediatorLog.RateLimited(ref _cycleFailures, out var n);
        MediatorLog.Write(_logger, level, "Live delivery request {RequestId} ({Name}) failed ({ExceptionType}), {InARow} in a row; retrying in {RetryIn}. {Total} such failures.", Id(requestId), name, error.GetType().Name, inARow, retryIn, n);
    }

    /// <summary>The mediator answered <c>live-delivery-change</c> with live delivery off; the stream keeps polling.</summary>
    internal void LiveNotInEffect(string requestId)
        => MediatorLog.Write(_logger, LogLevel.Warning, "The mediator answered live delivery request {RequestId} with live delivery off; the stream only polls.", Id(requestId));

    /// <summary>A push that is not a readable delivery was ignored (not acknowledged).</summary>
    internal void PushIgnored(string reason)
    {
        var level = MediatorLog.RateLimited(ref _pushesIgnored, out var n);
        MediatorLog.Write(_logger, level, "A live push was ignored ({Reason}); it is not acknowledged. {Total} such pushes.", reason, n);
    }

    /// <summary>Messages delivered again while this stream still held them were skipped.</summary>
    internal void HeldSkipped(int count)
        => MediatorLog.Write(_logger, LogLevel.Debug, "{Count} message(s) the live stream already holds were delivered again and skipped.", count);

    /// <summary>Confirmed messages were forgotten unacknowledged because too many were waiting for an acknowledgement.</summary>
    internal void TakenDropped(int count)
    {
        var level = MediatorLog.RateLimited(ref _takenDropped, out var n);
        MediatorLog.Write(_logger, level, "{Count} confirmed message(s) were forgotten unacknowledged (too many waiting for an acknowledgement); they come back and are recognised as repeats. {Total} such events.", count, n);
    }

    /// <summary>A step of a live stream's end failed.</summary>
    internal void EndFailed(string name, Exception error)
    {
        var level = MediatorLog.RateLimited(ref _endFailures, out var n);
        MediatorLog.Write(_logger, level, "Ending a live delivery stream: {Name} failed ({ExceptionType}). {Total} such failures.", name, error.GetType().Name, n);
    }

    private static string Id(string? requestId) => requestId is null ? "(live push)" : MediatorLog.LogId(requestId);
}
