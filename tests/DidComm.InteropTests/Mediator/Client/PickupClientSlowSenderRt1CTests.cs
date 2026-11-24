using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.InteropTests.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// RT1-C (#111): a loop that fetches until drained, against this library's mediator on a TestServer
/// at a short lease, reaches an honest message queued behind messages that fail after a lookup of
/// about the slow threshold τ, within the bound the plan gives for messages that never open. The
/// client's clock is a manual one, moved one timer at a time by <see cref="SlowFetchLoop"/>; the
/// mediator's clock moves by the client's elapsed time only.
/// </summary>
public sealed class PickupClientSlowSenderRt1CTests(ITestOutputHelper output)
{
    private static readonly TimeSpan ShortLease = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The same queue with a lookup of exactly τ: each message is remembered after one opening, and
    /// the loop reaches Alice's message within the bound.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(10)]
    public async Task A_fetch_loop_at_a_short_lease_reaches_the_message_behind_failures_at_the_slow_threshold(int limit)
    {
        var result = await RunAsync(limit, lookup: Threshold);

        result.Reached.Should().BeTrue();
        result.Elapsed.Should().BeLessThanOrEqualTo(result.Bound);
        result.Openings.Should().Be(Junk, "each is opened once, then held back while its pause lasts");
    }

    private const int Junk = 99;

    // τ at the default UnpackTimeout: (B − B/4) / 25.
    private static TimeSpan Threshold
    {
        get
        {
            var budget = new MediatorClientOptions().UnpackTimeout;
            return TimeSpan.FromTicks((budget.Ticks - budget.Ticks / 4) / 25);
        }
    }

    private async Task<Result> RunAsync(int limit, TimeSpan lookup)
    {
        var (mediator, store) = await StartMediatorAsync(ShortLease);
        await using var _ = mediator;
        var time = new QaManualTimeProvider();
        var seam = new SlowSenderSeam(time);
        await using var harness = await SlowFetchLoop.HarnessAsync(mediator, time, seam);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var mallory = await harness.NewAliceAsync();
        var alice = await harness.NewAliceAsync();
        seam.LookupTakes(mallory.Did, lookup);
        for (var i = 0; i < Junk; i++)
            await PostJunkNamingAsync(mediator, mallory, mallory, recipient);
        var honest = await PickupE2E.SendAsync(mediator, alice, recipient, "honest");
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(Junk + 1, "everything fits the mediator's quota (MaxQueueMessages)");
        harness.Channel.Unpacker.SlowThreshold.Should().Be(Threshold);
        var budget = harness.Channel.Options.UnpackTimeout;
        var perFetch = Math.Min(limit, 16);
        var bound = budget * ((Junk + perFetch - 1) / perFetch);
        var loop = new SlowFetchLoop(mediator, harness, time, store, seam, bob.Did);

        var reached = false;
        while (!reached && loop.Elapsed <= bound && loop.Fetches < 2000)
        {
            var batch = await loop.FetchOnceAsync(limit);
            reached = batch.Items.Any(i => i.Unpacked?.Message.Id == honest.Id);
        }

        var distinct = loop.Items.Where(i => i.Unpacked is null).Select(i => i.Id).Distinct(StringComparer.Ordinal).Count();
        var openings = seam.Calls.Count(sender => sender == mallory.Did);
        output.WriteLine($"limit {limit}, lookup {lookup.TotalMilliseconds} ms, lease {ShortLease.TotalSeconds} s: reached {reached} after {loop.Fetches} fetches, {loop.Elapsed.TotalSeconds:0.##} s ({loop.Elapsed / ShortLease:0.#} leases); {distinct} of {Junk} distinct messages in front seen, {openings} openings; bound {bound.TotalSeconds} s");
        return new Result(reached, loop.Fetches, loop.Elapsed, bound, distinct, openings);
    }

    private sealed record Result(bool Reached, int Fetches, TimeSpan Elapsed, TimeSpan Bound, int DistinctSeen, int Openings);

    private static async Task<(MediatorTestServer Mediator, LeaseRecordingStore Store)> StartMediatorAsync(TimeSpan lease)
    {
        LeaseRecordingStore? store = null;
        var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Mediator = m => m.Pickup.LeaseTimeout = lease;
            o.ConfigureServices = services => LeaseRecordingStore.Register(services, s => store = s);
        });
        _ = mediator.App.Services.GetRequiredService<IMessageStore>();
        return (mediator, store!);
    }

    // The named sender's own authcrypted note with its ciphertext changed, forwarded by the poster:
    // the outer envelope names the sender's key in skid, its DID is resolved, and it fails to decrypt.
    private static async Task PostJunkNamingAsync(MediatorTestServer mediator, MediatorSender poster, MediatorSender named, TestPeer recipient)
    {
        var envelope = JsonNode.Parse(await named.AuthcryptAsync(named.Note(recipient.Did, "junk"), recipient.Did))!.AsObject();
        var ciphertext = envelope["ciphertext"]!.GetValue<string>();
        envelope["ciphertext"] = (ciphertext[0] == 'A' ? "B" : "A") + ciphertext[1..];
        var (packed, _) = await poster.ForwardAsync(mediator.Did, recipient.Did, envelope.ToJsonString());
        (await mediator.PostAsync(packed)).Status.Should().Be(System.Net.HttpStatusCode.Accepted);
        await mediator.FlushAsync();
    }
}
