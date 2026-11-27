using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT1-C (#111): the live stream at a leasing mediator reaches an honest message queued before it
/// started, behind messages that fail after a lookup of about 0.6 s (the slow threshold τ before
/// owner decision C removed it: every failure is now remembered): within twenty minutes of
/// simulated time at a 10 s lease, and in the first cycle at the mediator's default lease.
/// Simulated time (<see cref="SimClock"/>, <see cref="StreamDriver"/>); the real unpacker and its
/// memory run on that clock. Nothing sleeps; real time is only a hang guard.
/// </summary>
public sealed class LiveStreamSlowSenderRt1CTests(ITestOutputHelper output)
{
    private const int Junk = 99;
    private static readonly TimeSpan Horizon = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Ninety-nine messages that each fail <paramref name="ticksBelow"/> ticks before 0.6 s, then Alice's:
    /// at a 10 s lease the stream yields Alice's message within twenty minutes of simulated time.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(1_000_000)]
    public async Task The_stream_at_a_short_lease_reaches_a_queued_message_behind_failures_just_under_six_tenths_of_a_second(int ticksBelow)
    {
        var result = await RunAsync(TimeSpan.FromSeconds(10), late: Rt1CExchange.Threshold - TimeSpan.FromTicks(ticksBelow));

        result.Reached.Should().BeTrue("the queued message is reached within {0}", Horizon);
    }

    /// <summary>The same queue, each failing at exactly 0.6 s: every one is remembered, and the honest message is reached.</summary>
    [Fact]
    public async Task The_stream_at_a_short_lease_reaches_a_queued_message_behind_failures_at_six_tenths_of_a_second()
    {
        var result = await RunAsync(TimeSpan.FromSeconds(10), late: Rt1CExchange.Threshold);

        result.Reached.Should().BeTrue();
        result.HostCalls.Should().Be(Junk + 1, "each is opened once, then held back while its pause lasts");
    }

    /// <summary>The same queue just under 0.6 s at the mediator's default lease: the honest message is reached in the first cycle.</summary>
    [Fact]
    public async Task The_stream_at_the_default_lease_reaches_a_queued_message_behind_failures_just_under_six_tenths_of_a_second()
    {
        var result = await RunAsync(new MessagePickupOptions().LeaseTimeout, late: Rt1CExchange.Threshold - TimeSpan.FromTicks(1));

        result.Reached.Should().BeTrue();
        result.Cycles.Should().Be(1);
    }

    private async Task<Result> RunAsync(TimeSpan lease, TimeSpan late)
    {
        var exchange = new Rt1CExchange(late);
        var mediator = new LeasingMediator(exchange.Sim, lease);
        for (var i = 0; i < Junk; i++)
            mediator.Enqueue($"j{i}", $"late:j{i}");
        mediator.Enqueue("honest", "ok:honest");
        await using var driver = new StreamDriver(exchange.Sim);

        await driver.RunAsync(() => driver.Items.Any(i => i.Item.Id == "honest" && i.Item.Unpacked is not null), Horizon);

        var reached = driver.Items.Any(i => i.Item.Id == "honest" && i.Item.Unpacked is not null);
        var cycles = exchange.Sim.Log.Count(r => r.Type == PickupClientLiveStreamTests.LiveOn);
        var distinct = driver.Items.Where(i => i.Item.Unpacked is null).Select(i => i.Item.Id).Distinct(StringComparer.Ordinal).Count();
        output.WriteLine($"lease {lease.TotalSeconds} s, late {late.TotalMilliseconds} ms: reached {reached} at {exchange.Sim.Clock.Now}; {cycles} cycles, {exchange.Sim.Log.Count(r => r.Type == PickupClientLiveStreamTests.Drain)} drain rounds, {distinct} of {Junk} in front seen, {exchange.HostCalls} host calls");
        foreach (var load in exchange.Sim.Loads.Take(12))
            output.WriteLine($"  {load.At} {(load.RequestId is null ? "push" : "drain")}: {string.Join(",", load.Ids)}");
        return new Result(reached, cycles, distinct, exchange.HostCalls);
    }

    private sealed record Result(bool Reached, int Cycles, int DistinctSeen, int HostCalls);
}

/// <summary>
/// RT1-C: a <see cref="SimExchange"/> whose unpacker's host step also knows <c>late:{text}</c>: it
/// waits <see cref="Late"/> on the clock, honouring its token, then fails (a sender whose DID resolves
/// in that time and whose message does not decrypt). The stream talks to <see cref="Sim"/>; only its
/// unpacker is this one's.
/// </summary>
internal sealed class Rt1CExchange
{
    private static readonly System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    private readonly ConcurrentDictionary<long, CancellationToken> _waits = new();
    private readonly System.Reflection.FieldInfo _simHostCalls = typeof(SimExchange).GetField("_hostCalls", Private)!;
    private readonly object _gate = new();
    private long _ids;

    public Rt1CExchange(TimeSpan late)
    {
        Late = late;
        Sim = new SimExchange();
        // The SimExchange's unpacker, rebuilt on the same clock with this host step.
        // Its settling rule counts the calls made and the ones waiting on the clock: this host step
        // keeps both of the SimExchange's counts.
        typeof(SimExchange).GetField("<Unpacker>k__BackingField", Private)!.SetValue(Sim, new DeliveredMessageUnpacker(HostAsync, Sim.Options.UnpackTimeout, Sim.Clock));
        typeof(SimExchange).GetField("_stalls", Private)!.SetValue(Sim, _waits);
    }

    /// <summary>The former slow threshold at the default UnpackTimeout ((B − B/4) / 25 = 0.6 s).</summary>
    public static TimeSpan Threshold { get; } = TimeSpan.FromMilliseconds(600);

    public TimeSpan Late { get; }

    public SimExchange Sim { get; }

    public int HostCalls => Sim.HostCalls;


    private async Task<DeliveredUnpack> HostAsync(string packed, CancellationToken ct)
    {
        var text = Unwrap(packed);
        if (text.StartsWith("late:", StringComparison.Ordinal))
        {
            // The wait stops counting as waiting on the timer's own thread, before the clock can be
            // moved again: the settling rule then waits for this call's outcome to be recorded.
            var id = Interlocked.Increment(ref _ids);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waits[id] = ct;
            using var timer = Sim.Clock.CreateTimer(_ =>
            {
                _waits.TryRemove(id, out var _);
                done.TrySetResult();
            }, null, Late, Timeout.InfiniteTimeSpan);
            using var cancelled = ct.Register(() =>
            {
                _waits.TryRemove(id, out var _);
                done.TrySetCanceled(ct);
            });
            CountCall();
            await done.Task.ConfigureAwait(false);
            throw new MalformedMessageException("The delivered message did not decrypt (test).");
        }
        CountCall();
        var outcome = FakePickupExchange.DefaultOutcome(text);
        return outcome.Unpacked is { } result ? new DeliveredUnpack(result, text, outcome.LayersOpened) : throw outcome.Error!;
    }

    private void CountCall()
    {
        lock (_gate)
            _simHostCalls.SetValue(Sim, (int)_simHostCalls.GetValue(Sim)! + 1);
    }

    private static string Unwrap(string packed)
    {
        try
        {
            if (JsonNode.Parse(packed) is JsonObject envelope && envelope["ciphertext"] is JsonValue value && value.TryGetValue<string>(out var text))
                return text;
        }
        catch (JsonException)
        {
        }
        return packed;
    }
}
