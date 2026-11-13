using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Transports.ReturnRoute;

/// <summary>
/// #91 adversarial pass (plan F15 / H5) against the Core return-route types: the header parser fed
/// values no valid thread id can have (FR-MSG-11) and values far over the cap, and the FR-RR-08
/// replay guard driven to its window, retention, clock and budget edges with maximal keys.
/// </summary>
public sealed class ReturnRouteAdversarialTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static long S(DateTimeOffset t) => t.ToUnixTimeSeconds();

    // ---- return_route_thread -----------------------------------------------------------------

    private static Message WithThread(string threadJson)
        => new()
        {
            Id = "m1",
            Type = "https://didcomm.org/test/1.0/m",
            AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [ReturnRouteHeader.HeaderName] = JsonDocument.Parse("\"thread\"").RootElement.Clone(),
                [ReturnRouteHeader.ThreadHeaderName] = JsonDocument.Parse(threadJson).RootElement.Clone(),
            },
        };

    [Theory]
    [InlineData("\"rr thread\"")]          // space
    [InlineData("\"rr/thread\"")]          // gen-delim
    [InlineData("\"rr\\nthread\"")]        // control character (log-line injection shape)
    [InlineData("\"rr{thread}\"")]
    [InlineData("\"rr%41thread\"")]        // percent-encoding is not an unreserved character
    [InlineData("\"rr:thread\"")]          // a DID-shaped value
    [InlineData("\"r\\u00e9thread\"")]     // non-ASCII
    public void A_thread_value_no_valid_thid_can_have_is_a_malformed_header(string threadJson)
    {
        // FR-MSG-11: a thread id has the id constraints (RFC 3986 unreserved characters only), and
        // MessageBuilder.WithReturnRoute refuses anything else — so the reader must not surface such
        // a value to handlers as context.ReturnRoute.Thread / session.Thread either. FR-RR-01: a
        // `thread` without a usable return_route_thread is `none` and logged.
        ReturnRouteHeader.TryRead(WithThread(threadJson), out var header).Should().BeFalse();
        header.Should().Be(ReturnRouteHeader.None);
        header.Thread.Should().BeNull("the value is never surfaced");
    }

    [Fact]
    public void The_builder_and_the_reader_agree_on_every_value_the_builder_accepts()
    {
        foreach (var thread in new[] { "a", "thid-1", "A.b_c~d-0", new string('z', ReturnRouteHeader.MaxThreadLength) })
        {
            var message = new MessageBuilder().WithType("https://didcomm.org/test/1.0/m").WithReturnRoute(ReturnRouteMode.Thread, thread).Build();
            ReturnRouteHeader.TryRead(message, out var header).Should().BeTrue();
            header.Should().Be(new ReturnRouteHeader(ReturnRouteMode.Thread, thread));
        }
    }

    [Fact]
    public void An_escaped_thread_value_is_read_by_its_unescaped_characters()
    {
        // "\u0061bc" is the thread id "abc" — escaping changes the wire bytes, not the value.
        ReturnRouteHeader.TryRead(WithThread("\"\\u0061bc\""), out var header).Should().BeTrue();
        header.Thread.Should().Be("abc");
        // 2048 escaped characters are 12 288 raw bytes but exactly at the cap.
        var atCap = string.Concat(Enumerable.Repeat("\\u0061", ReturnRouteHeader.MaxThreadLength));
        ReturnRouteHeader.TryRead(WithThread("\"" + atCap + "\""), out var escaped).Should().BeTrue();
        escaped.Thread.Should().HaveLength(ReturnRouteHeader.MaxThreadLength);
        ReturnRouteHeader.TryRead(WithThread("\"" + atCap + "\\u0061\""), out var over).Should().BeFalse();
        over.Should().Be(ReturnRouteHeader.None);
    }

    [Fact]
    public void A_thread_value_far_over_the_cap_is_refused_without_materialising_it()
    {
        // F15(c): a ~1 MiB return_route_thread (under any sane MaxReceiveBytes) is refused as
        // malformed, and the parser does not copy it into a string to find that out.
        var huge = WithThread("\"" + new string('a', 1 << 20) + "\"");
        ReturnRouteHeader.TryRead(huge, out _); // warm up the JIT for this path

        var before = GC.GetAllocatedBytesForCurrentThread();
        var ok = ReturnRouteHeader.TryRead(huge, out var header);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        ok.Should().BeFalse();
        header.Should().Be(ReturnRouteHeader.None);
        allocated.Should().BeLessThan(16 * 1024, "an over-cap value is rejected from its raw length, not after a 2 MiB string copy");
    }

    [Fact]
    public void A_huge_return_route_value_is_refused_without_materialising_it()
    {
        var message = new Message
        {
            Id = "m1",
            Type = "https://didcomm.org/test/1.0/m",
            AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [ReturnRouteHeader.HeaderName] = JsonDocument.Parse("\"" + new string('a', 1 << 20) + "\"").RootElement.Clone(),
            },
        };
        ReturnRouteHeader.TryRead(message, out _);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var ok = ReturnRouteHeader.TryRead(message, out var header);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        ok.Should().BeFalse();
        header.Should().Be(ReturnRouteHeader.None);
        allocated.Should().BeLessThan(16 * 1024, "the mode is compared in place, not copied out first");
    }

    // ---- replay guard: window, retention, clock ------------------------------------------------

    // These suites predate ReplayProtectionOptions.ClockSkew (#105) and run at ClockSkew = 0 unless a
    // test asks for a skew, so they are also the "ClockSkew = 0 reproduces the old rule" differential.
    private static InMemoryReplayGuard Guard(ReplayProtectionOptions? options = null, TimeSpan? skew = null)
    {
        options ??= new ReplayProtectionOptions();
        options.ClockSkew = skew ?? TimeSpan.Zero;
        return new InMemoryReplayGuard(options);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void Admission_edges_are_exact(int skewSeconds)
    {
        var g = Guard(skew: TimeSpan.FromSeconds(skewSeconds));
        var now = T;
        g.TryAdmit("did:example:a", "edge-old", S(now) - 300, now).Should().Be(ReplayAdmission.Admitted, "created = now − Window is inside");
        g.TryAdmit("did:example:a", "edge-older", S(now) - 301, now).Should().Be(ReplayAdmission.TooOld, "created = now − Window − 1 is outside");
        g.TryAdmit("did:example:a", "edge-future", S(now) + skewSeconds, now).Should().Be(ReplayAdmission.Admitted, "created = now + skew is inside");
        g.TryAdmit("did:example:a", "edge-futurer", S(now) + skewSeconds + 1, now).Should().Be(ReplayAdmission.FutureDated, "created = now + skew + 1 is outside");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void At_every_second_from_admission_to_past_retention_a_replay_is_refused(int skewSeconds)
    {
        // FR-RR-08 (4): no instant at which a captured request is neither remembered nor too old —
        // swept second by second, with a purge-triggering insert from another peer at each step.
        var g = Guard(skew: TimeSpan.FromSeconds(skewSeconds));
        var created = S(T);
        g.TryAdmit("did:example:a", "m", created, T).Should().Be(ReplayAdmission.Admitted);

        for (var t = -skewSeconds - 3; t <= 300 + skewSeconds + 3; t++)
        {
            var now = T.AddSeconds(t);
            g.TryAdmit("did:example:noise", "n" + t, S(now), now); // drives the purge at this instant
            var replay = g.TryAdmit("did:example:a", "m", created, now);
            replay.Should().NotBe(ReplayAdmission.Admitted, "at offset {0}s the replay must be refused", t);
            var expected = t < -skewSeconds ? ReplayAdmission.FutureDated
                : t > 300 ? ReplayAdmission.TooOld
                : ReplayAdmission.Duplicate;
            replay.Should().Be(expected, "at offset {0}s", t);
        }
    }

    [Fact]
    public void A_ClockSkew_changed_to_a_negative_value_after_construction_has_no_effect()
    {
        var options = new ReplayProtectionOptions { ClockSkew = TimeSpan.Zero };
        var g = new InMemoryReplayGuard(options);
        options.ClockSkew = TimeSpan.FromSeconds(-90);
        g.TryAdmit("did:example:a", "now", S(T), T).Should().Be(ReplayAdmission.Admitted);
        g.TryAdmit("did:example:a", "plus1", S(T) + 1, T).Should().Be(ReplayAdmission.FutureDated);
        g.TryAdmit("did:example:a", "now", S(T), T.AddSeconds(300)).Should().Be(ReplayAdmission.Duplicate, "retention is created + Window + 0");
        g.TryAdmit("did:example:a", "now", S(T), T.AddSeconds(301)).Should().Be(ReplayAdmission.TooOld);
    }

    [Theory]
    [InlineData(0L, ReplayAdmission.TooOld)]
    [InlineData(-1L, ReplayAdmission.TooOld)]
    [InlineData(long.MinValue, ReplayAdmission.TooOld)]
    [InlineData(long.MaxValue, ReplayAdmission.FutureDated)]
    [InlineData(long.MaxValue - 1, ReplayAdmission.FutureDated)]
    public void Extreme_created_time_values_are_refused_and_never_overflow(long created, ReplayAdmission expected)
    {
        var g = Guard(skew: TimeSpan.FromSeconds(int.MaxValue));
        g.TryAdmit("did:example:a", "x", created, T).Should().Be(expected);
        g.Count.Should().Be(0);
    }

    [Fact]
    public void The_largest_admissible_expiry_does_not_wrap()
    {
        // created + Window + skew at its maximum: the latest clock and the widest window and skew
        // Validate() allows (the guard copies the validated values, so nothing larger can reach the
        // arithmetic). A wrap to a negative expiry would purge the entry on the next call and
        // re-admit the replay.
        var options = new ReplayProtectionOptions { Window = TimeSpan.FromSeconds(int.MaxValue) };
        var g = Guard(options, skew: TimeSpan.FromSeconds(int.MaxValue));
        var now = DateTimeOffset.MaxValue;
        var created = S(now) + int.MaxValue;

        g.TryAdmit("did:example:a", "max", created, now).Should().Be(ReplayAdmission.Admitted);
        g.TryAdmit("did:example:b", "purge-trigger", S(now), now).Should().Be(ReplayAdmission.Admitted);
        g.TryAdmit("did:example:a", "max", created, now).Should().Be(ReplayAdmission.Duplicate);
        g.Count.Should().Be(2);

        // The earliest instant on a guard of its own: on this one it would be a clock step back of ten
        // thousand years, which is refused (TooOld) by design.
        var earliest = DateTimeOffset.MinValue;
        var fresh = Guard(new ReplayProtectionOptions { Window = TimeSpan.FromSeconds(int.MaxValue) }, skew: TimeSpan.FromSeconds(int.MaxValue));
        fresh.TryAdmit("did:example:c", "min", S(earliest), earliest).Should().Be(ReplayAdmission.Admitted);
        fresh.TryAdmit("did:example:c", "min", S(earliest), earliest).Should().Be(ReplayAdmission.Duplicate);
        g.TryAdmit("did:example:c", "min", S(earliest), earliest).Should().Be(ReplayAdmission.TooOld);
    }

    [Fact]
    public void A_clock_that_moves_backwards_keeps_the_entry_and_refuses_the_replay()
    {
        var g = Guard(skew: TimeSpan.FromSeconds(120));
        g.TryAdmit("did:example:a", "m", S(T), T).Should().Be(ReplayAdmission.Admitted);

        g.TryAdmit("did:example:a", "m", S(T), T.AddSeconds(-60)).Should().Be(ReplayAdmission.Duplicate, "back by less than the skew: still remembered");
        g.TryAdmit("did:example:a", "m", S(T), T.AddSeconds(-121)).Should().Be(ReplayAdmission.FutureDated, "back by more than the skew: refused on its timestamp");
        g.TryAdmit("did:example:a", "m", S(T), T.AddSeconds(10)).Should().Be(ReplayAdmission.Duplicate, "the clock coming back purged nothing");
        g.Count.Should().Be(1);
    }

    [Fact]
    public void A_clock_that_jumps_forward_past_the_retention_and_back_does_not_readmit()
    {
        // Before #105 this was the documented W-1 limitation: the forward jump purged the entry and,
        // the clock back, nothing remembered the request. The guard now refuses any timestamped
        // message whose entry would have expired before the latest purge instant.
        var g = Guard();
        g.TryAdmit("did:example:a", "m", S(T), T).Should().Be(ReplayAdmission.Admitted);
        var jumped = T.AddSeconds(301);
        g.TryAdmit("did:example:b", "purge-trigger", S(jumped), jumped).Should().Be(ReplayAdmission.Admitted);
        g.Count.Should().Be(1, "the forward jump purged the first entry");

        g.TryAdmit("did:example:a", "m", S(T), T).Should().Be(ReplayAdmission.TooOld, "it may have been forgotten");
    }

    [Fact]
    public void After_a_step_back_beyond_Window_plus_skew_fresh_messages_are_refused_until_the_clock_catches_up()
    {
        // The price of the guarantee above, pinned to the second: purged at T + 301 (Window 300,
        // skew 0), so any message with created + 300 < T + 301 is refused, a fresh one included.
        var g = Guard();
        var jumped = T.AddSeconds(301);
        g.TryAdmit("did:example:b", "purge-trigger", S(jumped), jumped).Should().Be(ReplayAdmission.Admitted);

        g.TryAdmit("did:example:a", "fresh-at-T", S(T), T).Should().Be(ReplayAdmission.TooOld);
        g.TryAdmit("did:example:a", "fresh-at-T+1", S(T) + 1, T.AddSeconds(1)).Should().Be(ReplayAdmission.Admitted);
        new InMemoryReplayGuard(new ReplayProtectionOptions { ClockSkew = TimeSpan.Zero })
            .TryAdmit("did:example:a", "fresh-at-T", S(T), T).Should().Be(ReplayAdmission.Admitted, "a rebuilt guard (a restart) starts from its first reading");
    }

    [Fact]
    public void A_request_refused_as_saturated_is_admitted_once_when_capacity_returns_and_never_again()
    {
        // The refused delivery was never executed, so its later admission is its first, not a replay;
        // after that it is remembered like any other.
        var g = Guard(new ReplayProtectionOptions { MaxEntries = 1, MaxEntriesPerPeer = 1 });
        var older = S(T) - 290; // expires at T + 10
        g.TryAdmit("did:example:a", "first", older, T).Should().Be(ReplayAdmission.Admitted);

        g.TryAdmit("did:example:b", "second", S(T), T).Should().Be(ReplayAdmission.Saturated);
        g.TryAdmit("did:example:b", "second", S(T), T.AddSeconds(5)).Should().Be(ReplayAdmission.Saturated, "still full; refused again, not remembered");
        g.SaturationCount.Should().Be(2);

        var later = T.AddSeconds(11);
        g.TryAdmit("did:example:b", "second", S(T), later).Should().Be(ReplayAdmission.Admitted);
        g.TryAdmit("did:example:b", "second", S(T), later).Should().Be(ReplayAdmission.Duplicate);
        g.TryAdmit("did:example:a", "first", older, later).Should().Be(ReplayAdmission.TooOld);
    }

    // ---- replay guard: budget under maximal keys -------------------------------------------------

    private static string MaximalSubject(int i)
        => "did:example:" + i.ToString("D8") + new string('x', 2048 - "did:example:".Length - 8);

    private static string MaximalId(int i) => i.ToString("D8") + new string('i', 512 - 8);

    [Fact]
    public void A_fresh_DID_flood_with_maximal_keys_is_bounded_and_never_displaces_existing_entries()
    {
        var options = new ReplayProtectionOptions { MaxEntries = 512, MaxEntriesPerPeer = 64 };
        var g = Guard(options);
        for (var p = 0; p < 4; p++)
            for (var m = 0; m < 8; m++)
                g.TryAdmit(MaximalSubject(p), MaximalId(m), S(T), T).Should().Be(ReplayAdmission.Admitted);

        var admitted = 0;
        var saturated = 0;
        for (var p = 4; p < 4 + 4 * options.MaxEntries; p++)
        {
            var outcome = g.TryAdmit(MaximalSubject(p), MaximalId(0), S(T), T);
            if (outcome == ReplayAdmission.Admitted) admitted++;
            else if (outcome == ReplayAdmission.Saturated) saturated++;
            g.Count.Should().BeLessThanOrEqualTo(options.MaxEntries);
        }

        admitted.Should().Be(options.MaxEntries - 32);
        saturated.Should().Be(4 * options.MaxEntries - admitted);
        g.SaturationCount.Should().Be(saturated);
        g.PeerCount.Should().BeLessThanOrEqualTo(options.MaxEntries, "buckets are bounded by the same budget");
        for (var p = 0; p < 4; p++)
            for (var m = 0; m < 8; m++)
                g.TryAdmit(MaximalSubject(p), MaximalId(m), S(T), T).Should().Be(ReplayAdmission.Duplicate, "an unexpired entry is never evicted to make room");
        g.TryAdmit(MaximalSubject(0), MaximalId(0) + "x", S(T), T).Should().Be(ReplayAdmission.MessageIdTooLong, "513 characters");
    }

    [Fact]
    public void One_sender_cannot_exceed_its_bucket_even_with_maximal_keys()
    {
        var options = new ReplayProtectionOptions { MaxEntries = 256, MaxEntriesPerPeer = 16 };
        var g = Guard(options);
        for (var m = 0; m < 64; m++)
            g.TryAdmit(MaximalSubject(1), MaximalId(m), S(T), T).Should().Be(m < 16 ? ReplayAdmission.Admitted : ReplayAdmission.Saturated);
        g.Count.Should().Be(16);
        g.TryAdmit(MaximalSubject(2), MaximalId(0), S(T), T).Should().Be(ReplayAdmission.Admitted, "another sender still has room");
    }

    [Fact]
    public void The_guard_keeps_one_copy_of_a_senders_subject_however_many_ids_it_remembers()
    {
        // The endpoint passes a fresh subject string per message (DidSubjectOf parses the key id each
        // time). Retaining that instance per entry would cost up to 4 KB (2048 UTF-16 chars) per entry
        // on top of the id — four times the documented per-entry budget for one busy sender.
        var g = Guard();
        g.TryAdmit(new string(MaximalSubject(7).AsSpan()), "first", S(T), T).Should().Be(ReplayAdmission.Admitted);

        var second = AdmitWithFreshSubject(g, "second");
        var third = AdmitWithFreshSubject(g, "third");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        second.IsAlive.Should().BeFalse("the entry must reuse the sender's existing key, not retain the caller's copy");
        third.IsAlive.Should().BeFalse();
        g.TryAdmit(MaximalSubject(7), "second", S(T), T).Should().Be(ReplayAdmission.Duplicate);
        g.TryAdmit(MaximalSubject(7), "third", S(T), T).Should().Be(ReplayAdmission.Duplicate);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AdmitWithFreshSubject(InMemoryReplayGuard g, string id)
    {
        var subject = new string(MaximalSubject(7).AsSpan());
        g.TryAdmit(subject, id, S(T), T).Should().Be(ReplayAdmission.Admitted);
        return new WeakReference(subject);
    }
}
