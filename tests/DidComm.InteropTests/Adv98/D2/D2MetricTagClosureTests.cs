using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.Diagnostics;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using Xunit;

namespace DidComm.InteropTests.Adv98.D2;

/// <summary>
/// #98 D2 attack 7: under hostile forwards (unknown, non-DID and marker-bearing <c>next</c>,
/// expired, held, over-quota) and a live push, every tag the library's meter records is one of the
/// documented keys, and every value is in its closed vocabulary (docs/codebase-architecture.md
/// "Observability"). No tag carries a DID or any wire value.
/// </summary>
[Collection(QaSerialTiming.Name)]
public sealed class D2MetricTagClosureTests
{
    private static readonly HashSet<string> ForwardValues = new(StringComparer.Ordinal)
    {
        "Queued", "Dropped-Malformed", "Dropped-NotEncrypted", "Dropped-Expired", "Dropped-Unknown", "Dropped-Saturated",
        "Dropped-Unacknowledged", "Dropped-Shutdown", "Dropped-TooLarge", "Dropped-Quota", "Faulted",
    };

    private static readonly HashSet<string> PushValues = new(StringComparer.Ordinal)
    {
        "Pushed-Live", "Push-Failed", "Push-Skipped-Saturated", "Push-Skipped-NoLiveSession", "Push-Skipped-Unregistered",
        "Push-Skipped-Cooldown", "Push-Skipped-Shutdown",
    };

    [Fact]
    public async Task Every_metric_tag_under_hostile_traffic_is_a_documented_key_with_a_closed_value()
    {
        var tags = new ConcurrentQueue<(string Instrument, string Key, string? Value)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == DidCommDiagnostics.MeterName)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, t, _) =>
        {
            if (t.Length == 0)
                tags.Enqueue((instrument.Name, "(none)", null));
            foreach (var tag in t)
                tags.Enqueue((instrument.Name, tag.Key, tag.Value?.ToString()));
        });
        listener.SetMeasurementEventCallback<int>((instrument, _, t, _) =>
        {
            foreach (var tag in t)
                tags.Enqueue((instrument.Name, tag.Key, tag.Value?.ToString()));
        });
        listener.Start();

        var marker = "D2MKMETRIC" + Guid.NewGuid().ToString("N")[..8];
        await using (var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.Pickup.MaxQueueMessages = 2))
        {
            var bob = await mediator.NewPeerAsync();
            await mediator.EnrolAsync(bob);
            var alice = await mediator.NewSenderAsync();
            var inner = await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did);

            // Statuses are not this test's subject (an expired envelope is refused at unpack).
            foreach (var next in new[] { "did:example:" + marker, "https://" + marker.ToLowerInvariant() + ".example/", bob.Did + "#" + marker, mediator.Did })
                await mediator.PostAsync((await alice.ForwardAsync(mediator.Did, next, inner)).Packed);
            await mediator.PostAsync((await alice.ForwardAsync(mediator.Did, bob.Did, inner, Qa.Expires(mediator.Clock.Now.AddSeconds(-5)))).Packed);
            for (var i = 0; i < 4; i++)
                await Qa.PostForwardAsync(mediator, (await alice.ForwardAsync(mediator.Did, bob.Did, inner)).Packed); // two queued, two over quota
            await mediator.FlushAsync();

            // A live push: Bob goes live and acknowledges, then one more forward is pushed.
            using var socket = await mediator.ConnectAsync();
            await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, socket, bob);
            await mediator.FlushAsync();
            await mediator.PushesIdleAsync();
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
            await mediator.PushesIdleAsync();
        }
        listener.RecordObservableInstruments();

        var seen = tags.ToArray();
        seen.Should().Contain(t => t.Key == ForwardOutcomes.OutcomeTag && t.Value == ForwardOutcomes.DroppedUnknown, "positive control: the listener saw the forward outcomes");
        seen.Should().NotContain(t => (t.Value ?? string.Empty).Contains("D2MK", StringComparison.OrdinalIgnoreCase) || (t.Value ?? string.Empty).Contains("did:", StringComparison.Ordinal));
        foreach (var t in seen.Where(t => t.Key != "(none)"))
        {
            (t.Key is ForwardOutcomes.OutcomeTag or MediatorMetrics.PushOutcomeTag).Should().BeTrue("{0} records only the documented tag keys, got {1}", t.Instrument, t.Key);
            var vocabulary = t.Key == ForwardOutcomes.OutcomeTag ? ForwardValues : PushValues;
            vocabulary.Should().Contain(t.Value!, "{0}/{1} is a closed vocabulary", t.Instrument, t.Key);
        }
    }
}
