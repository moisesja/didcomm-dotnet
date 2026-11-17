using System.Diagnostics.Metrics;
using DidComm.Diagnostics;

namespace DidComm.Samples.MediatedAgent;

/// <summary>
/// Counts the forwarded messages one mediator has stored, from the mediator's own metrics: the
/// <c>didcomm.forward.outcomes</c> counter with outcome <c>Queued</c>. In production these metrics go
/// to OpenTelemetry or <c>dotnet-counters</c>; the sample reads them in process so it can wait until a
/// message is in the recipient's queue.
/// </summary>
/// <remarks>
/// The <c>202</c> a sender gets means "received", not "stored": the mediator answers before it relays
/// or queues anything (FR-ROUTE-11). The counter is recorded after the message is stored.
/// Only the instruments of the given mediator's meter factory are read, so other mediators in the
/// same process are not counted.
/// </remarks>
internal sealed class StoredForwardsWatcher : IDisposable
{
    private const string OutcomesInstrument = "didcomm.forward.outcomes";
    private const string OutcomeTag = "didcomm.forward.outcome";
    private const string Queued = "Queued";

    private readonly MeterListener _listener = new();
    private readonly object _gate = new();
    private readonly List<(long Target, TaskCompletionSource Done)> _waiters = new();
    private long _stored;

    /// <summary>Start listening to the meters <paramref name="meterFactory"/> created.</summary>
    /// <param name="meterFactory">The mediator host's meter factory.</param>
    public StoredForwardsWatcher(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, meterFactory)
                && instrument.Meter.Name == DidCommDiagnostics.MeterName
                && instrument.Name == OutcomesInstrument)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.Start();
    }

    /// <summary>How many forwarded messages have been stored so far.</summary>
    public long Stored
    {
        get
        {
            lock (_gate)
                return _stored;
        }
    }

    /// <summary>Completes once <paramref name="total"/> forwarded messages have been stored in all.</summary>
    /// <param name="total">The count to wait for. Read <see cref="Stored"/> before the send, then wait for one more.</param>
    public Task WhenStoredAsync(long total)
    {
        lock (_gate)
        {
            if (_stored >= total)
                return Task.CompletedTask;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((total, done));
            return done.Task;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();

    private void OnMeasurement(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        foreach (var tag in tags)
        {
            if (tag.Key != OutcomeTag || tag.Value is not Queued)
                continue;
            lock (_gate)
            {
                _stored += value;
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].Target <= _stored)
                    {
                        _waiters[i].Done.TrySetResult();
                        _waiters.RemoveAt(i);
                    }
                }
            }
        }
    }
}
