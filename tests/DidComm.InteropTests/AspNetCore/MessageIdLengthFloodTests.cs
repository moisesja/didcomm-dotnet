using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AwesomeAssertions;
using DidComm.InteropTests.Mediator;
using DidComm.Messages;
using DidComm.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// A stranger floods the HTTP endpoint with plaintext Trust Pings, each on a fresh thread id. The
/// dispatcher files every one in its thread store (10 000 entries) before the handler runs, so the
/// store's size follows the thread ids' length. At 512 characters the flood fills the store to its
/// cap with keys of exactly that length, and at 513 nothing reaches it. What one such entry holds is
/// measured in <c>IdKeyedStoreRetentionTests</c> (Core), in a process quiet enough for a heap
/// measurement: here, earlier tests' memory is still being released while this one runs, and a heap
/// difference would count it as the store's.
/// </summary>
[Collection(QaSerialTiming.Name)]
public sealed class MessageIdLengthFloodTests
{
    private const int Max = 512;
    private const int Posts = 12_000; // over the cap, so eviction runs during the flood
    private const int Workers = 32;

    private readonly ITestOutputHelper _out;

    public MessageIdLengthFloodTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task A_flood_of_plaintext_pings_on_fresh_512_character_thread_ids_fills_the_thread_store_to_its_cap_with_keys_of_that_length()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        var store = (InMemoryThreadStateStore)host.App.Services.GetRequiredService<IThreadStateStore>();

        var started = Stopwatch.GetTimestamp();
        var statuses = await FloodAsync(host, Max);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var count = store.Count;
        var floodKeys = FloodKeysHeld(store, Max);

        _out.WriteLine($"{Posts:N0} posts in {elapsed.TotalSeconds:N1} s; store holds {count:N0} entries, {floodKeys:N0} of them flood keys of 512 characters");
        statuses.Should().OnlyContain(s => s == HttpStatusCode.Accepted);
        // Eviction trims to 90 % of the cap; inserts that land while a pass runs may leave it over the
        // cap by at most one per concurrent sender.
        count.Should().BeInRange(InMemoryThreadStateStore.DefaultMaxEntries * 9 / 10, InMemoryThreadStateStore.DefaultMaxEntries + Workers, "the flood filled the store to its cap");
        floodKeys.Should().Be(count, "every entry the store holds is one of the flood's 512-character thread ids");
    }

    [Fact]
    public async Task The_same_flood_on_513_character_thread_ids_is_refused_and_leaves_the_thread_store_empty()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        var store = (InMemoryThreadStateStore)host.App.Services.GetRequiredService<IThreadStateStore>();

        var started = Stopwatch.GetTimestamp();
        var statuses = await FloodAsync(host, Max + 1);
        var elapsed = Stopwatch.GetElapsedTime(started);

        _out.WriteLine($"{Posts:N0} posts in {elapsed.TotalSeconds:N1} s; store holds {store.Count:N0} entries");
        statuses.Should().OnlyContain(s => s == HttpStatusCode.BadRequest);
        store.Count.Should().Be(0, "no thread id over 512 characters reaches the store");
    }

    /// <summary>Post <see cref="Posts"/> plaintext pings on distinct thread ids of <paramref name="length"/> characters, from <see cref="Workers"/> concurrent senders.</summary>
    private static async Task<IReadOnlyList<HttpStatusCode>> FloodAsync(ReturnRouteTestHost host, int length)
    {
        using var client = host.Server.CreateClient();
        var statuses = new HttpStatusCode[Posts];
        var next = -1;
        async Task WorkerAsync()
        {
            int i;
            while ((i = Interlocked.Increment(ref next)) < Posts)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/didcomm") { Content = new ByteArrayContent(Ping(i, length)) };
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.Plaintext);
                using var response = await client.SendAsync(request);
                statuses[i] = response.StatusCode;
            }
        }
        await Task.WhenAll(Enumerable.Range(0, Workers).Select(_ => Task.Run(WorkerAsync)));
        return statuses;
    }

    /// <summary>A plaintext ping that asks for no response, as a stranger writes it, on thread <see cref="Key"/>(<paramref name="i"/>).</summary>
    private static byte[] Ping(int i, int length)
        => Encoding.UTF8.GetBytes(
            "{\"id\":\"flood-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\",\"typ\":\"" + MediaTypes.Plaintext
            + "\",\"type\":\"https://didcomm.org/trust-ping/2.0/ping\",\"thid\":\"" + Key(i, length)
            + "\",\"body\":{\"response_requested\":false}}");

    /// <summary>Distinct, mixed case (the store compares ignoring case), exactly <paramref name="length"/> unreserved characters.</summary>
    private static string Key(int i, int length)
    {
        var prefix = "t" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + "-";
        return string.Create(length, prefix, static (span, p) =>
        {
            p.AsSpan().CopyTo(span);
            for (var j = p.Length; j < span.Length; j++)
                span[j] = j % 2 == 0 ? 'a' : 'B';
        });
    }

    /// <summary>How many of the flood's thread ids the store holds, each checked to be <paramref name="length"/> characters.</summary>
    private static int FloodKeysHeld(InMemoryThreadStateStore store, int length)
    {
        var held = 0;
        for (var i = 0; i < Posts; i++)
        {
            if (store.Get(Key(i, length)) is { } state)
            {
                state.Thid.Should().HaveLength(length);
                held++;
            }
        }
        return held;
    }
}
