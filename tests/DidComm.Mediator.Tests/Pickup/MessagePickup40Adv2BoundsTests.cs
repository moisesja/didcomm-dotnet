using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Pickup;
using DidComm.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// Red team ADV-2 for #100 (bounds and resources), mediator role. What a hostile sender or peer can
/// make the 4.0 mediator spend: the mailbox's encrypted-form check (#100 OD-1) on the worst JSON
/// shapes at the largest admissible size, the 4.0 <c>messages-received</c> id bound with poison
/// beyond it, repeated ids and ids of another grant, and <c>message_count_limit</c> against the byte
/// budget with many tiny or few large messages. An attack that held is a passing test; a finding is a
/// skipped test named after it. Timing and heap measurements are opt-in probes
/// (<c>DIDCOMM_ADV100_PROBES=1</c>): they read real time and the GC.
/// </summary>
public sealed class MessagePickup40Adv2BoundsTests(ITestOutputHelper output)
{
    // ---- the encrypted-form check (OD-1) on hostile shapes -------------------------------------

    /// <summary>The worst shapes for a strict JSON parse, each filled up to <paramref name="size"/> bytes, with whether the library's JWE rule (a root object with a ciphertext member) holds for them.</summary>
    internal static IEnumerable<(string Name, byte[] Bytes, bool Encrypted)> HostileShapes(int size)
    {
        yield return ("a flat ciphertext string", Ascii("{\"ciphertext\":\"" + new string('A', size - 17) + "\"}"), true);
        yield return ("a root object with one member per 8 bytes", Fill(size, "{\"ciphertext\":1", i => ",\"k" + i + "\":0", "}"), true);
        yield return ("one ciphertext object of many members", Fill(size, "{\"ciphertext\":{\"k\":0", i => ",\"k" + i + "\":0", "}}"), true);
        yield return ("many escaped member names", Fill(size, "{\"ciphertext\":1", i => ",\"\\u006b" + i + "\":0", "}"), true);
        yield return ("many small objects", Fill(size, "{\"ciphertext\":[{\"a\":0}", _ => ",{\"a\":0}", "]}"), true);
        yield return ("many objects of 17 members", Fill(size, "{\"ciphertext\":[0", _ => "," + Obj17, "]}"), true);
        yield return ("a ciphertext array of zeros", Fill(size, "{\"ciphertext\":[0", _ => ",0", "]}"), true);
        yield return ("a ciphertext number of every digit", Ascii("{\"ciphertext\":1" + new string('0', size - 16) + "}"), true);
        yield return ("long member names with a shared prefix", Fill(size, "{\"ciphertext\":1", i => ",\"" + new string('x', 1000) + i + "\":0", "}"), true);
        yield return ("63 nested arrays (the deepest the parse allows)", Ascii("{\"ciphertext\":" + new string('[', 62) + new string(']', 62) + "}"), true);
        yield return ("65 nested arrays", Ascii("{\"ciphertext\":" + new string('[', 64) + new string(']', 64) + "}"), false);
        yield return ("a nested array filled to the size", Ascii("{\"ciphertext\":" + new string('[', 62) + new string(']', 62) + new string(' ', size - 140) + "}"), true);
        yield return ("a wide object without ciphertext", Fill(size, "{\"k\":0", i => ",\"k" + i + "\":0", "}"), false);
        yield return ("many members then ciphertext twice", Fill(size - 40, "{\"ciphertext\":1", i => ",\"k" + i + "\":0", ",\"ciphertext\":2}"), false);
        yield return ("many members then a name that is an unpaired surrogate escape", Fill(size - 40, "{\"ciphertext\":1", i => ",\"k" + i + "\":0", ",\"\\uD800\":0}"), false);
        yield return ("a byte order mark before a JWE", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Ascii("{\"ciphertext\":\"" + new string('A', size - 20) + "\"}")).ToArray(), false);
        yield return ("a JWE then a second value", Ascii("{\"ciphertext\":\"" + new string('A', size - 20) + "\"}{}"), false);
        yield return ("an unterminated string the size of the bound", Ascii("{\"ciphertext\":\"" + new string('A', size - 15)), false);
    }

    [Fact]
    public async Task Adv2_every_hostile_shape_at_the_admissible_maximum_ends_in_an_outcome_and_only_a_ciphertext_object_is_stored()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var max = h.Fixture.Queue.MaxAdmissiblePayloadBytes;
        var expected = new List<string>();
        var actual = new List<string>();
        var stored = 0;
        long storedBytes = 0;

        foreach (var (name, bytes, encrypted) in HostileShapes(max))
        {
            bytes.Length.Should().BeLessThanOrEqualTo(max, name);
            // No capture: an exception out of DeliverAsync fails the test by itself.
            var result = await h.Fixture.Queue.DeliverAsync(h.Alice, bytes, new PickupDeliveryOptions(AllowLive: false));
            expected.Add(name + " → " + (encrypted ? PickupDeliveryOutcome.Queued : PickupDeliveryOutcome.NotEncrypted));
            actual.Add(name + " → " + result.Outcome);
            if (encrypted)
            {
                stored++;
                storedBytes += bytes.Length;
            }
        }

        actual.Should().Equal(expected);
        h.Store.Inner.MessageCount.Should().Be(stored);
        h.Store.Inner.TotalBytes.Should().Be(storedBytes, "what is retained is the payload bytes, nothing parsed");
    }

    /// <summary>
    /// ADV2-1 (Low, DidComm.Mediator / DidComm.Mediator.Client, unpublished): the encrypted-form rule
    /// is documented as "strict UTF-8 JSON" and as the rule "every unpacker" agrees with, but the
    /// strict parse does not validate the UTF-8 of string values or member names. A JWE-shaped object
    /// whose ciphertext holds bytes that are not UTF-8 is stored, counted against the grant's quota
    /// and delivered, while the pickup client's own unpack refuses those bytes outright
    /// (<c>DeliveredMessageUnpacker</c> decodes with a throwing UTF-8 decoder) and so never
    /// acknowledges it. Invariant broken: the mailbox holds only what the recipient's unpack can read
    /// as a JWE (OD-1, L-041). Fix: <c>System.Text.Unicode.Utf8.IsValid(payload.Span)</c> before the
    /// parse in <c>EncryptedForm.IsEncrypted</c> (one vectorised pass, bounded by the size check
    /// that runs first).
    /// </summary>
    [Fact]
    public async Task Adv2_1_a_JWE_shaped_object_whose_bytes_are_not_UTF_8_is_not_stored()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var forms = new[]
        {
            Ascii("{\"ciphertext\":\"").Concat(new byte[] { 0xFF, 0xFE }).Concat(Ascii("\"}")).ToArray(),
            Ascii("{\"ciphertext\":\"").Concat(new byte[] { 0xC0, 0xAF }).Concat(Ascii("\"}")).ToArray(), // overlong '/'
            Ascii("{\"ciphertext\":\"").Concat(new byte[] { 0xED, 0xA0, 0x80 }).Concat(Ascii("\"}")).ToArray(), // a surrogate in UTF-8
            Ascii("{\"ciphertext\":\"x\",\"").Concat(new byte[] { 0xFF }).Concat(Ascii("\":1}")).ToArray(), // in a member name
        };
        var outcomes = new List<PickupDeliveryOutcome>();

        foreach (var form in forms)
        {
            var decoding = Record.Exception(() => strict.GetString(form));
            decoding.Should().BeOfType<DecoderFallbackException>("the client's unpack cannot read these bytes at all");
            outcomes.Add((await h.Fixture.Queue.DeliverAsync(h.Alice, form, new PickupDeliveryOptions(AllowLive: false))).Outcome);
        }

        outcomes.Should().AllBeEquivalentTo(PickupDeliveryOutcome.NotEncrypted);
        h.Store.Inner.MessageCount.Should().Be(0);
    }

    [AdvProbeFact]
    public void Adv2_probe_the_encrypted_form_check_is_linear_in_size_and_retains_nothing()
    {
        const int Full = 564_000;
        const int Quarter = Full / 4;
        var lines = new List<string>();
        var worstMs = 0.0;
        var worstAllocRatio = 0.0;
        var worstScaling = 0.0;
        foreach (var ((name, full, _), (_, quarter, _)) in HostileShapes(Full).Zip(HostileShapes(Quarter)))
        {
            var (fullMs, fullAlloc) = Measure(full);
            var (quarterMs, _) = Measure(quarter);
            var scaling = quarterMs < 0.05 ? 0 : fullMs / quarterMs;
            worstMs = Math.Max(worstMs, fullMs);
            if (full.Length >= 16 * 1024)
                worstAllocRatio = Math.Max(worstAllocRatio, (double)fullAlloc / full.Length);
            worstScaling = Math.Max(worstScaling, scaling);
            lines.Add($"{name,-64} {full.Length,8} B {fullMs,8:F2} ms (quarter size {quarterMs,6:F2} ms, ×{scaling:F1}) alloc {fullAlloc,10:N0} B");
        }

        var before = LiveBytes();
        var keep = new List<bool>();
        foreach (var (_, bytes, _) in HostileShapes(Full))
            keep.Add(EncryptedForm.IsEncrypted(bytes));
        var retained = LiveBytes() - before;
        GC.KeepAlive(keep);

        foreach (var line in lines)
            output.WriteLine(line);
        output.WriteLine($"worst {worstMs:F2} ms per call; worst allocation {worstAllocRatio:F2}× the payload; worst 4×-size scaling ×{worstScaling:F1}; retained after the calls {retained:N0} B");

        worstScaling.Should().BeLessThan(12, "the parse is linear: four times the bytes, about four times the time (a quadratic step would read about 16)");
        worstAllocRatio.Should().BeLessThan(5, "the transient allocation stays within a few times the payload");
        retained.Should().BeLessThan(1_000_000, "nothing of a judged payload outlives the call");
    }

    // ---- messages-received (4.0): the id bound ----------------------------------------------------

    [Fact]
    public async Task Adv2_a_4_0_id_list_over_MaxAckIds_whose_entry_beyond_breaks_re_reading_is_refused_by_its_count()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync(o =>
        {
            o.MaxDeliveryBatch = 2;
            o.MaxAckIds = 3;
        });
        await h.EnrolAsync(h.Alice, h.Alice);
        var id = await h.QueueAsync(h.Alice, 100);
        h.ResetTraces();

        // L-055 poison: an unpaired surrogate escape, which throws when the entry is decoded. The
        // receive path's strict deserialization refuses such a body outright, so it reaches the
        // handler only as a host-built message does: put in after the unpack.
        JsonObject Poisoned() => new() { ["message_id_list"] = JsonNode.Parse("[\"" + id + "\",\"f1\",\"f2\",\"\\uD800\"]") };
        var thrown = await Record.ExceptionAsync(() => h.SendHandBuiltAsync(h.MessagesReceived40(h.Alice, new[] { id }), m => m.Body = Poisoned(), h.Http(h.Alice)));
        var parsed = Record.Exception(() => MessagePickup40.TryParseMessagesReceived(h.Raw40("messages-received", h.Alice, Poisoned()), 3, out _));
        var withinTheBound = Record.Exception(() => MessagePickup40.TryParseMessagesReceived(h.Raw40("messages-received", h.Alice, Poisoned()), 4, out _));

        thrown.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Be("messages-received 'message_id_list' has more than 3 entries.");
        parsed.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Be("messages-received 'message_id_list' has more than 3 entries.");
        output.WriteLine("within the bound: " + withinTheBound?.GetType().Name);
        withinTheBound.Should().BeOfType<MalformedMessageException>("within the bound the poisoned entry is read, and reading it fails: the count above came first");
        h.StoreTrace.Should().BeEmpty();
        (await h.DeliverableAsync(h.Alice)).Should().Equal(id);
    }

    [Fact]
    public async Task Adv2_MaxAckIds_copies_of_one_id_and_ids_of_another_grant_remove_only_the_peers_own_message_and_get_no_reply()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync(o =>
        {
            o.MaxDeliveryBatch = 10;
            o.MaxAckIds = 1000;
        });
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.EnrolAsync(h.Bob, h.Bob);
        var mine = await h.QueueAsync(h.Alice, 100, 1);
        var other = await h.QueueAsync(h.Bob, 100, 2);
        var kept = await h.QueueAsync(h.Alice, 100, 3);
        h.ResetTraces();

        var ids = Enumerable.Repeat(mine, 500).Concat(Enumerable.Repeat(other, 500)).ToArray();
        var outcome = await h.SendAsync(h.MessagesReceived40(h.Alice, ids), h.Http(h.Alice));

        outcome.ShouldBeNoReply40();
        h.StoreTrace.Should().Equal("AcknowledgeAsync");
        (await h.DeliverableAsync(h.Alice)).Should().Equal(kept);
        (await h.DeliverableAsync(h.Bob)).Should().Equal(new[] { other }, "an id of another grant matches nothing of this peer");
    }

    // ---- message_count_limit against the byte budget ---------------------------------------------

    [Fact]
    public async Task Adv2_the_largest_message_count_limit_over_many_tiny_messages_stops_at_MaxDeliveryBatch_within_max_delivery_bytes()
    {
        const int MaxDeliveryBytes = 64 * 1024;
        await using var h = await MessagePickupHandlerHarness.CreateAsync(o =>
        {
            o.MaxDeliveryBatch = 50;
            o.MaxDeliveryBytes = MaxDeliveryBytes;
        });
        await h.EnrolAsync(h.Alice, h.Alice);
        for (var i = 0; i < 60; i++)
            await h.Fixture.QueueAsync(h.Alice, StandInJwe.OfSize(StandInJwe.MinSize, (byte)i));
        var session = h.Fixture.Session(h.Alice, h.Mediator, live: false, register: false, pack: true);
        h.ResetTraces();

        var request = h.Raw40("delivery-request", h.Alice, JsonNode.Parse("{\"message_count_limit\":1e400}")!.AsObject());
        (await h.SendAsync(request, session)).ShouldBeNoReply40();

        h.Store.LeaseRequests.Should().ContainSingle().Which.MaxMessages.Should().Be(50);
        var attempt = session.Attempts.Should().ContainSingle().Subject;
        attempt.Result.Should().Be(DidComm.Transports.ReturnRoute.ReturnRouteSendResult.Sent);
        attempt.MaxPackedBytes.Should().Be(MaxDeliveryBytes);
        attempt.PackedBytes.Should().BeLessThanOrEqualTo(MaxDeliveryBytes);
        MessagePickup40.TryParseDelivery(attempt.Message, out var messages, out _).Should().BeTrue();
        messages!.Count.Should().Be(50);
        (await h.DeliverableAsync(h.Alice)).Should().HaveCount(10);
    }

    [Fact]
    public async Task Adv2_the_largest_message_count_limit_over_admissible_maximum_messages_delivers_one_at_a_time_within_max_delivery_bytes()
    {
        const int MaxDeliveryBytes = 64 * 1024;
        await using var h = await MessagePickupHandlerHarness.CreateAsync(o =>
        {
            o.MaxDeliveryBatch = 50;
            o.MaxDeliveryBytes = MaxDeliveryBytes;
        });
        await h.EnrolAsync(h.Alice, h.Alice);
        var max = h.Fixture.Queue.MaxAdmissiblePayloadBytes;
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
            ids.Add(await h.Fixture.QueueAsync(h.Alice, StandInJwe.OfSize(max, (byte)i)));
        var session = h.Fixture.Session(h.Alice, h.Mediator, live: false, register: false, pack: true);

        var counts = new List<int>();
        for (var round = 0; round < 3; round++)
        {
            (await h.SendAsync(h.DeliveryRequest40(h.Alice, int.MaxValue), session)).ShouldBeNoReply40();
            var attempt = session.Attempts.Last();
            attempt.Result.Should().Be(DidComm.Transports.ReturnRoute.ReturnRouteSendResult.Sent);
            attempt.PackedBytes.Should().BeLessThanOrEqualTo(MaxDeliveryBytes);
            MessagePickup40.TryParseDelivery(attempt.Message, out var messages, out _).Should().BeTrue();
            counts.Add(messages!.Count);
        }

        counts.Should().Equal(1, 1, 1);
        session.Attempts.Should().HaveCount(3, "the byte budget picks the batch before anything is packed: no trimming round trips");
        (await h.DeliverableAsync(h.Alice)).Should().BeEmpty("all three are leased");
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static readonly string Obj17 = "{" + string.Join(",", Enumerable.Range(0, 17).Select(k => "\"" + (char)('a' + k) + "\":0")) + "}";

    private static byte[] Ascii(string text) => Encoding.UTF8.GetBytes(text);

    // head, then as many items as fit, then tail; never over `size` bytes.
    private static byte[] Fill(int size, string head, Func<int, string> item, string tail)
    {
        var sb = new StringBuilder(head, size);
        for (var i = 0; ; i++)
        {
            var next = item(i);
            if (sb.Length + next.Length + tail.Length > size)
                break;
            sb.Append(next);
        }
        sb.Append(tail);
        return Ascii(sb.ToString());
    }

    private static (double Ms, long Alloc) Measure(byte[] payload)
    {
        _ = EncryptedForm.IsEncrypted(payload); // warm
        // The fastest of a few rounds: a GC or a scheduler pause in one of them is not the parse.
        const int Rounds = 7;
        var best = double.MaxValue;
        long alloc = 0;
        for (var i = 0; i < Rounds; i++)
        {
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            _ = EncryptedForm.IsEncrypted(payload);
            sw.Stop();
            alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return (best, alloc);
    }

    private static long LiveBytes()
    {
        for (var i = 0; i < 2; i++)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return info.HeapSizeBytes - info.FragmentedBytes;
    }
}

/// <summary>A <see cref="FactAttribute"/> that runs only when <c>DIDCOMM_ADV100_PROBES=1</c>: the ADV-2 measurements of #100 read real time and the GC.</summary>
internal sealed class AdvProbeFactAttribute : FactAttribute
{
    public AdvProbeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DIDCOMM_ADV100_PROBES") != "1")
            Skip = "Opt-in measurement: set DIDCOMM_ADV100_PROBES=1.";
    }
}
