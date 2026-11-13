using System.Diagnostics.Metrics;
using DidComm.Diagnostics;

namespace DidComm.Protocols.Routing;

/// <summary>
/// The <c>didcomm.forward.outcomes</c> counter on the <see cref="DidCommDiagnostics.MeterName"/>
/// meter: one count per forward, tagged with a <see cref="ForwardOutcomes"/> value. Thread-safe;
/// recording never throws.
/// </summary>
internal sealed class ForwardMetrics : IDisposable
{
    private readonly Counter<long> _outcomes;
    private readonly bool _ownsMeter;

    /// <summary>Create the counter.</summary>
    /// <param name="meterFactory">The host's meter factory, which then owns the meter; <c>null</c> to create a meter this instance owns and disposes.</param>
    public ForwardMetrics(IMeterFactory? meterFactory)
    {
        if (meterFactory is not null)
        {
            Meter = meterFactory.Create(DidCommDiagnostics.MeterName);
        }
        else
        {
            Meter = new Meter(DidCommDiagnostics.MeterName);
            _ownsMeter = true;
        }

        _outcomes = Meter.CreateCounter<long>(ForwardOutcomes.ForwardOutcomesInstrument, unit: "{forward}");
    }

    /// <summary>The meter the counter belongs to.</summary>
    public Meter Meter { get; }

    /// <summary>Count <paramref name="count"/> forwards that ended with <paramref name="outcome"/>.</summary>
    /// <param name="outcome">A <see cref="ForwardOutcomes"/> value.</param>
    /// <param name="count">How many forwards.</param>
    public void Record(string outcome, long count = 1)
    {
        try
        {
            _outcomes.Add(count, new KeyValuePair<string, object?>(ForwardOutcomes.OutcomeTag, outcome));
        }
        catch
        {
            // Metric listeners are host code; they must not change how a forward is handled.
        }
    }

    /// <summary>Dispose the meter when this instance created it; a factory's meter is the factory's to dispose.</summary>
    public void Dispose()
    {
        if (_ownsMeter)
            Meter.Dispose();
    }
}
