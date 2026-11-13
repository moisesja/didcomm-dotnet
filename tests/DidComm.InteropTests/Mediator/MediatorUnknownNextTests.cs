using System.Diagnostics;
using System.Net;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// #95 acceptance, "unknown <c>next</c> → 202, nothing queued, no outbound message; timing equal to
/// the known-recipient path within the existing timing-test tolerance" (FR-ROUTE-11, D4).
/// The class runs in the serial timing collection: under the parallel test load (2-core CI
/// runners) the medians measure contention, not the mediator.
/// </summary>
[Collection(QaSerialTiming.Name)]
public sealed class MediatorUnknownNextTests
{
    [Fact]
    public async Task An_unknown_next_is_answered_202_and_nothing_is_queued_pushed_or_reported()
    {
        // Direct push is on and the registered peer has an endpoint, so any outbound message would show.
        var network = new MediatorNetwork();
        await using var inbox = await InboxServer.StartAsync(network);
        var directory = new TestEndpointDirectory();
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Network = network;
            o.Mediator = m => m.DirectPush = true;
            o.ConfigureServices = services => services.AddSingleton<IRecipientEndpointDirectory>(directory);
        });
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        directory.Map(bob.Did, inbox.Endpoint("/bob-inbox"));
        var stranger = await mediator.NewPeerAsync();
        var alice = await mediator.NewSenderAsync();

        var (packed, forward) = await alice.ForwardAsync(mediator.Did, stranger.Did, await alice.AuthcryptAsync(alice.Note(stranger.Did), stranger.Did));
        var response = await mediator.PostAsync(packed);

        response.Status.Should().Be(HttpStatusCode.Accepted, "an unregistered next is never reported to the sender (FR-ROUTE-11)");
        response.Body.Should().BeEmpty();
        await mediator.FlushAsync();
        await mediator.PushesIdleAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedUnknown);
        mediator.Store.MessageCount.Should().Be(0);
        mediator.PushOutcomes.Should().BeEmpty();
        directory.Calls.Should().Be(0);
        inbox.Entered.Should().Be(0, "no outbound message");
        mediator.Logs.Entries.Should().Contain(e => e.Contains(forward.Id) && e.Contains("not a registered recipient"));
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Response_time_does_not_tell_a_known_next_from_an_unknown_one()
    {
        // The FR-MED-08 method (InMemoryMediationStoreTests): warm-up, interleaved batches over the
        // real HTTP endpoint, a flush between batches so the relay work never overlaps the
        // measurement, medians, ratio < 3 either way. The quotas are raised so a full queue never
        // changes the known path.
        const int WarmUp = 10;
        const int Batches = 8;
        const int PerBatch = 10;
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m =>
        {
            m.Pickup.MaxQueueMessages = 10_000;
            m.Pickup.MaxQueueBytes = 512L * 1024 * 1024;
            m.Pickup.MaxStoreMessages = 10_000;
            m.Pickup.MaxStoreBytes = 512L * 1024 * 1024;
        });
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var stranger = await mediator.NewPeerAsync();
        var alice = await mediator.NewSenderAsync();
        // The same inner payload on both paths: only next differs.
        var inner = await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did);

        async Task<List<string>> PackAsync(string next, int count)
        {
            var packed = new List<string>(count);
            for (var i = 0; i < count; i++)
                packed.Add((await alice.ForwardAsync(mediator.Did, next, inner)).Packed);
            return packed;
        }

        var total = WarmUp + Batches * PerBatch;
        var known = await PackAsync(bob.Did, total);
        var unknown = await PackAsync(stranger.Did, total);

        async Task<double> TimeAsync(string packed)
        {
            var watch = Stopwatch.StartNew();
            var response = await mediator.PostAsync(packed);
            watch.Stop();
            response.Status.Should().Be(HttpStatusCode.Accepted);
            return watch.Elapsed.TotalMilliseconds;
        }

        for (var i = 0; i < WarmUp; i++)
        {
            await TimeAsync(known[i]);
            await TimeAsync(unknown[i]);
        }
        await mediator.FlushAsync();

        var knownTimes = new List<double>();
        var unknownTimes = new List<double>();
        for (var batch = 0; batch < Batches; batch++)
        {
            for (var i = 0; i < PerBatch; i++)
            {
                var index = WarmUp + batch * PerBatch + i;
                // Alternate which path goes first, so neither always follows the other.
                if ((index & 1) == 0)
                {
                    knownTimes.Add(await TimeAsync(known[index]));
                    unknownTimes.Add(await TimeAsync(unknown[index]));
                }
                else
                {
                    unknownTimes.Add(await TimeAsync(unknown[index]));
                    knownTimes.Add(await TimeAsync(known[index]));
                }
            }
            await mediator.FlushAsync();
        }

        mediator.Outcome(ForwardOutcomes.Queued).Should().Be(total);
        mediator.Outcome(ForwardOutcomes.DroppedUnknown).Should().Be(total);
        var knownMedian = Median(knownTimes);
        var unknownMedian = Median(unknownTimes);
        var ratio = Math.Max(knownMedian, unknownMedian) / Math.Min(knownMedian, unknownMedian);
        ratio.Should().BeLessThan(3.0, "known median {0:F3} ms, unknown median {1:F3} ms", knownMedian, unknownMedian);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }
}
