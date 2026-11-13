using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using DidComm.Protocols.Routing;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// An <see cref="IMeterFactory"/> whose meters carry the factory itself as their
/// <see cref="Meter.Scope"/>, so a listener can pick out exactly this test's instruments while other
/// tests record on meters of the same name in parallel.
/// </summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = new();

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options.Name, options.Version, options.Tags, scope: this);
        lock (_meters)
            _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        lock (_meters)
        {
            foreach (var meter in _meters)
                meter.Dispose();
            _meters.Clear();
        }
    }
}

/// <summary>
/// Collects the <c>didcomm.forward.outcomes</c> measurements of one test's instruments only: those
/// of a <see cref="TestMeterFactory"/>'s meters, or of one given <see cref="Meter"/>.
/// </summary>
internal sealed class ForwardOutcomeRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);
    private int _completed;

    private ForwardOutcomeRecorder(Func<Instrument, bool> select)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == ForwardOutcomes.ForwardOutcomesInstrument && select(instrument))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == ForwardOutcomes.OutcomeTag)
                    outcome = tag.Value as string;
            }
            _counts.AddOrUpdate(outcome ?? "(untagged)", value, (_, sum) => sum + value);
        });
        _listener.MeasurementsCompleted = (_, _) => Interlocked.Exchange(ref _completed, 1);
        _listener.Start();
    }

    /// <summary>Every forward-outcome instrument created through <paramref name="factory"/>.</summary>
    public static ForwardOutcomeRecorder For(TestMeterFactory factory) => new(i => ReferenceEquals(i.Meter.Scope, factory));

    /// <summary>The forward-outcome instrument of exactly <paramref name="meter"/>.</summary>
    public static ForwardOutcomeRecorder For(Meter meter) => new(i => ReferenceEquals(i.Meter, meter));

    /// <summary>The count recorded for <paramref name="outcome"/>.</summary>
    public long this[string outcome] => _counts.TryGetValue(outcome, out var count) ? count : 0;

    /// <summary>Every count recorded, whatever its outcome.</summary>
    public long Total => _counts.Values.Sum();

    /// <summary>The counts by outcome.</summary>
    public IReadOnlyDictionary<string, long> Counts => new Dictionary<string, long>(_counts, StringComparer.Ordinal);

    /// <summary>Whether the instrument's meter was disposed while this recorder listened.</summary>
    public bool Completed => Volatile.Read(ref _completed) == 1;

    public void Dispose() => _listener.Dispose();
}
