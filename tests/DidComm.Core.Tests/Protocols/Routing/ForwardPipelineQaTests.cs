using System.Globalization;
using System.Text.Json;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using AwesomeAssertions;
using Xunit;
using static DidComm.Tests.Protocols.Routing.ForwardTestEnvelopes;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// QA-1 (#95): the FR-ROUTE-10 delay cap at every boundary, the shape of the random draw, the
/// schedule through <see cref="ForwardHandler"/> and the public processor overload, the option
/// validation and record semantics, and clocks near the ends of time. Written from the XML docs and
/// PRD FR-ROUTE-10, not from the existing tests.
/// </summary>
public sealed class ForwardPipelineQaTests
{
    private const string Mediator = "did:example:qa-mediator";
    private const string Bob = "did:example:bob"; // the harness registers this recipient
    private const string Inner = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";
    private static readonly TimeSpan Cap = TimeSpan.FromHours(1);
    private static readonly long CapMs = (long)Cap.TotalMilliseconds;
    private static readonly TimeSpan Flush = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Fixed = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    // ── ExtractDelay: the whole positive grid under a one-hour cap ────────────────────────

    public static TheoryData<long, long> PositiveGrid => new()
    {
        { 0L, 0L },
        { 1L, 1L },
        { CapMs - 1, CapMs - 1 },
        { CapMs, CapMs },
        { CapMs + 1, CapMs },
        { int.MaxValue, CapMs },
        { (long)int.MaxValue + 1, CapMs },
        { long.MaxValue, CapMs },
    };

    [Theory]
    [MemberData(nameof(PositiveGrid))]
    public void Positive_hints_are_held_for_min_of_n_and_the_cap(long hint, long expectedMs)
    {
        ForwardProcessor.ExtractDelay(WithDelay(Forward(Bob), hint), Cap).Should().Be(TimeSpan.FromMilliseconds(expectedMs));
    }

    public static TheoryData<long, long> NegativeGrid => new()
    {
        { -1L, 1L },
        { -CapMs, CapMs },
        { -(CapMs + 1), CapMs },
        { -(long)int.MaxValue, CapMs },
        { long.MinValue + 1, CapMs },
        { long.MinValue, CapMs },
    };

    [Theory]
    [MemberData(nameof(NegativeGrid))]
    public void Negative_hints_draw_within_zero_and_min_of_magnitude_and_cap(long hint, long upperMs)
    {
        var forward = WithDelay(Forward(Bob), hint);
        for (var i = 0; i < 300; i++)
        {
            var delay = ForwardProcessor.ExtractDelay(forward, Cap);
            delay.Should().NotBeNull();
            delay!.Value.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(upperMs));
            (delay.Value.Ticks % TimeSpan.TicksPerMillisecond).Should().Be(0, "a draw is a whole number of milliseconds");
        }
    }

    [Fact]
    public void Minus_one_draws_both_zero_and_one()
    {
        // [0, |n|] is inclusive at both ends (PRD FR-ROUTE-10): both values must occur.
        var forward = WithDelay(Forward(Bob), -1);
        var seen = new HashSet<TimeSpan>();
        for (var i = 0; i < 400 && seen.Count < 2; i++)
            seen.Add(ForwardProcessor.ExtractDelay(forward, Cap)!.Value);

        seen.Should().BeEquivalentTo(new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(1) });
    }

    [Fact]
    public void A_negative_hint_far_beyond_a_small_cap_draws_uniformly_over_every_value_up_to_the_cap()
    {
        // cap = 9 ms, |n| = 10^6 ms. Clamp-before-draw: 10 equally likely values, ~10 % each.
        // Clamp-after-draw: 9 ms with probability 1 − 9/10^6. Buckets must all be near 10 %.
        var cap = TimeSpan.FromMilliseconds(9);
        var forward = WithDelay(Forward(Bob), -1_000_000);
        var buckets = new int[10];
        const int draws = 20_000;
        for (var i = 0; i < draws; i++)
        {
            var ms = (int)ForwardProcessor.ExtractDelay(forward, cap)!.Value.TotalMilliseconds;
            ms.Should().BeInRange(0, 9);
            buckets[ms]++;
        }

        // Expected 2 000 per bucket, sd ≈ 42: ±500 is about 12 sd.
        buckets.Should().AllSatisfy(count => count.Should().BeInRange(1_500, 2_500));
    }

    // ── ExtractDelay: what is not an integer is not a hint ───────────────────────────────

    [Theory]
    [InlineData("500.5")]
    [InlineData("500.0")]
    [InlineData("1e3")]
    [InlineData("1E3")]
    [InlineData("-5e1")]
    [InlineData("\"500\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[500]")]
    [InlineData("{\"n\":500}")]
    [InlineData("9223372036854775808")]   // long.MaxValue + 1
    [InlineData("-9223372036854775809")]  // long.MinValue − 1
    [InlineData("1e400")]
    public void Anything_but_a_JSON_integer_within_long_is_no_hint_and_never_throws(string rawJson)
    {
        ForwardProcessor.ExtractDelay(WithRawDelay(Forward(Bob), rawJson), Cap).Should().BeNull();
        ForwardProcessor.ExtractDelay(WithRawDelay(Forward(Bob), rawJson), null).Should().BeNull();
    }

    [Fact]
    public void Minus_zero_is_a_zero_hold()
    {
        ForwardProcessor.ExtractDelay(WithRawDelay(Forward(Bob), "-0"), Cap).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void A_header_under_another_name_or_no_headers_is_no_hint()
    {
        var other = Forward(Bob);
        other.AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["Delay_Milli"] = JsonDocument.Parse("500").RootElement.Clone(),
            ["delay_milli "] = JsonDocument.Parse("500").RootElement.Clone(),
        };
        ForwardProcessor.ExtractDelay(other, Cap).Should().BeNull();

        var none = Forward(Bob);
        none.AdditionalHeaders = null;
        ForwardProcessor.ExtractDelay(none, Cap).Should().BeNull();
    }

    // ── ExtractDelay: the cap itself ──────────────────────────────────────────────────────

    public static TheoryData<TimeSpan?, long, long?> CapGrid => new()
    {
        // cap, hint, expected (null: negative draw checked separately)
        { null, (long)int.MaxValue + 1, int.MaxValue },
        { null, long.MaxValue, int.MaxValue },
        { null, 750, 750 },
        { TimeSpan.Zero, 1, 0 },
        { TimeSpan.Zero, long.MaxValue, 0 },
        { TimeSpan.FromMilliseconds(1), 2, 1 },
        { TimeSpan.FromMilliseconds(1), 1, 1 },
        { TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond - 1), 5, 0 },    // sub-millisecond floors to 0
        { TimeSpan.FromTicks(1), 5, 0 },
        { TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond + 1), 5, 1 },
        { TimeSpan.FromMilliseconds(int.MaxValue), long.MaxValue, int.MaxValue },
        { TimeSpan.FromMilliseconds(int.MaxValue), int.MaxValue - 1L, int.MaxValue - 1L },
        { TimeSpan.MaxValue, long.MaxValue, int.MaxValue },                // static method: clamped to the library bound
        { TimeSpan.FromMilliseconds(-5), 5, 0 },                           // static method: a negative cap clamps to zero
        { TimeSpan.MinValue, 5, 0 },
    };

    [Theory]
    [MemberData(nameof(CapGrid))]
    public void Every_cap_shape_is_applied_in_whole_milliseconds_and_never_throws(TimeSpan? cap, long hint, long? expectedMs)
    {
        ForwardProcessor.ExtractDelay(WithDelay(Forward(Bob), hint), cap).Should().Be(TimeSpan.FromMilliseconds(expectedMs!.Value));
    }

    [Theory]
    [InlineData(0L)]                    // cap 0 → always 0
    [InlineData(1L)]                    // cap 1 ms
    [InlineData(9_999L)]                // cap 0.9999 ms (ticks) → floors to 0
    public void A_negative_hint_under_a_tiny_cap_never_exceeds_the_floored_cap(long capTicks)
    {
        var cap = capTicks == 1 ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromTicks(capTicks);
        var flooredMs = cap.Ticks / TimeSpan.TicksPerMillisecond;
        for (var i = 0; i < 200; i++)
        {
            ForwardProcessor.ExtractDelay(WithDelay(Forward(Bob), long.MinValue), cap)!.Value
                .Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(flooredMs));
        }
    }

    // ── ForwardProcessorOptions ───────────────────────────────────────────────────────────

    public static TheoryData<long> InvalidMaxDelayTicks => new()
    {
        -1L,
        TimeSpan.MinValue.Ticks,
        TimeSpan.FromMilliseconds(int.MaxValue).Ticks + 1,
        TimeSpan.MaxValue.Ticks,
    };

    [Theory]
    [MemberData(nameof(InvalidMaxDelayTicks))]
    public void MaxDelay_just_outside_the_range_is_refused_by_name_without_its_value(long ticks)
    {
        var options = new ForwardProcessorOptions { MaxDelay = TimeSpan.FromTicks(ticks) };

        var ex = options.Invoking(o => o.Validate()).Should().Throw<ArgumentException>().Which;
        ex.ParamName.Should().Be(nameof(ForwardProcessorOptions.MaxDelay));
        ex.Message.Should().NotContain(ticks.ToString(CultureInfo.InvariantCulture));
        FluentActions.Invoking(() => NewProcessor(options)).Should().Throw<ArgumentException>();
    }

    public static TheoryData<long> ValidMaxDelayTicks => new()
    {
        0L,
        1L,
        TimeSpan.TicksPerMillisecond - 1,
        TimeSpan.FromHours(1).Ticks,
        TimeSpan.FromMilliseconds(int.MaxValue).Ticks,
    };

    [Theory]
    [MemberData(nameof(ValidMaxDelayTicks))]
    public void MaxDelay_inside_the_range_is_accepted(long ticks)
    {
        var options = new ForwardProcessorOptions { MaxDelay = TimeSpan.FromTicks(ticks) };
        options.Invoking(o => o.Validate()).Should().NotThrow();
        FluentActions.Invoking(() => NewProcessor(options)).Should().NotThrow();
    }

    [Fact]
    public void Options_are_a_value_record_and_with_copies_carry_MaxDelay()
    {
        var a = new ForwardProcessorOptions { MaxDelay = Cap };
        var b = new ForwardProcessorOptions { MaxDelay = Cap };
        var c = a with { MaxDelay = TimeSpan.Zero };
        var d = c with { Mode = RewrapMode.ReanoncryptToNext };

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
        c.Should().NotBe(a);
        c.MaxDelay.Should().Be(TimeSpan.Zero);
        d.MaxDelay.Should().Be(TimeSpan.Zero, "with keeps the init-only property");
        d.Mode.Should().Be(RewrapMode.ReanoncryptToNext);
        new ForwardProcessorOptions().Should().NotBe(a, "null and one hour are different caps");
    }

    [Fact]
    public async Task The_cap_is_read_once_a_with_copy_after_construction_changes_nothing()
    {
        var options = new ForwardProcessorOptions { MaxDelay = TimeSpan.FromMilliseconds(100) };
        var processor = NewProcessor(options, didCommOptions: new DidCommOptions { Clock = () => Fixed });
        _ = options with { MaxDelay = TimeSpan.Zero };

        var result = await processor.ProcessAsync(AsDecryptedByMediator(WithDelay(Forward(Bob), 5_000)));
        result.Delay.Should().Be(TimeSpan.FromMilliseconds(100));
    }

    // ── Through the public overload ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L)]
    [InlineData(long.MaxValue, 100L)]
    public async Task Public_overload_stamps_NotBefore_at_its_own_clock_reading_plus_the_capped_hold(long hint, long expectedMs)
    {
        var now = Fixed;
        var processor = NewProcessor(
            new ForwardProcessorOptions { MaxDelay = TimeSpan.FromMilliseconds(100) },
            didCommOptions: new DidCommOptions { Clock = () => now });

        var result = await processor.ProcessAsync(AsDecryptedByMediator(WithDelay(Forward(Bob), hint)));

        result.Delay.Should().Be(TimeSpan.FromMilliseconds(expectedMs));
        result.NotBefore.Should().Be(Fixed + TimeSpan.FromMilliseconds(expectedMs));
    }

    [Fact]
    public async Task Public_overload_without_a_hint_has_no_schedule()
    {
        var processor = NewProcessor(new ForwardProcessorOptions { MaxDelay = Cap });

        var result = await processor.ProcessAsync(AsDecryptedByMediator(Forward(Bob)));

        result.Delay.Should().BeNull();
        result.NotBefore.Should().BeNull();
    }

    // QA-1 DEFECT 3: the public overload documents ConsistencyException, MalformedMessageException,
    // DidResolutionException and CryptoException; with the clock near the end of time a delay hint
    // makes `Now() + delay` (ForwardProcessor.ResolveSchedule) throw ArgumentOutOfRangeException.
    // The coordinator saturates the same kind of sum (AgedOutAt); the processor does not.
    [Fact(Skip = "QA-1 DEFECT 3: ForwardProcessor.ProcessAsync throws an undocumented ArgumentOutOfRangeException when Now()+delay passes DateTimeOffset.MaxValue (ForwardProcessor.cs:301)")]
    public async Task Public_overload_near_the_end_of_time_saturates_NotBefore_instead_of_throwing()
    {
        var nearEnd = DateTimeOffset.MaxValue - TimeSpan.FromSeconds(1);
        var processor = NewProcessor(
            new ForwardProcessorOptions { MaxDelay = Cap },
            didCommOptions: new DidCommOptions { Clock = () => nearEnd });

        var result = await processor.ProcessAsync(AsDecryptedByMediator(WithDelay(Forward(Bob), 5_000)));

        result.NotBefore.Should().Be(DateTimeOffset.MaxValue);
    }

    [Fact]
    public async Task Public_overload_near_the_end_of_time_today_throws_ArgumentOutOfRange()
    {
        // Pins the observed behaviour behind DEFECT 3 (kept green so a fix shows up as a change).
        var nearEnd = DateTimeOffset.MaxValue - TimeSpan.FromSeconds(1);
        var processor = NewProcessor(
            new ForwardProcessorOptions { MaxDelay = Cap },
            didCommOptions: new DidCommOptions { Clock = () => nearEnd });

        var act = async () => await processor.ProcessAsync(AsDecryptedByMediator(WithDelay(Forward(Bob), 5_000)));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Public_overload_near_the_start_of_time_computes_NotBefore()
    {
        var processor = NewProcessor(
            new ForwardProcessorOptions { MaxDelay = Cap },
            didCommOptions: new DidCommOptions { Clock = () => DateTimeOffset.MinValue });

        var result = await processor.ProcessAsync(AsDecryptedByMediator(WithDelay(Forward(Bob), 5_000)));

        result.NotBefore.Should().Be(DateTimeOffset.MinValue + TimeSpan.FromSeconds(5));
    }

    // ── Through ForwardHandler, one clock reading ─────────────────────────────────────────

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(500L, 500L)]
    [InlineData(long.MaxValue, 3_600_000L)]
    public async Task Handler_fixes_NotBefore_at_the_receipt_reading_whatever_the_clock_does_later(long hint, long expectedMs)
    {
        await using var h = new ForwardHandlerTests.Harness(processorOptions: new ForwardProcessorOptions { MaxDelay = Cap });
        var gate = new TaskCompletionSource();
        var receivedAt = h.Now;

        await h.Dispatch(AsDecryptedByMediator(WithDelay(Forward(Bob), hint)), gate.Task);
        h.Now = receivedAt + TimeSpan.FromMinutes(30);  // the acknowledgement comes late
        gate.SetResult();
        await h.Handler.FlushAsync(Flush);

        var result = h.Sink.Results.Should().ContainSingle().Which;
        result.Delay.Should().Be(TimeSpan.FromMilliseconds(expectedMs));
        result.NotBefore.Should().Be(receivedAt + TimeSpan.FromMilliseconds(expectedMs));
    }

    [Fact]
    public async Task Handler_with_no_hint_hands_the_sink_no_schedule()
    {
        await using var h = new ForwardHandlerTests.Harness(processorOptions: new ForwardProcessorOptions { MaxDelay = Cap });

        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Handler.FlushAsync(Flush);

        var result = h.Sink.Results.Should().ContainSingle().Which;
        result.Delay.Should().BeNull();
        result.NotBefore.Should().BeNull();
    }

    [Fact]
    public async Task Handler_draws_a_negative_hint_once_per_forward_and_reports_the_same_draw_in_Delay_and_NotBefore()
    {
        await using var h = new ForwardHandlerTests.Harness(processorOptions: new ForwardProcessorOptions { MaxDelay = TimeSpan.FromMilliseconds(1_000) });

        for (var i = 0; i < 40; i++)
            await h.Dispatch(AsDecryptedByMediator(WithId(WithDelay(Forward(Bob), long.MinValue), $"neg-{i}")));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Results.Should().HaveCount(40);
        foreach (var result in h.Sink.Results)
        {
            result.Delay!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(1_000));
            result.NotBefore.Should().Be(h.Now + result.Delay.Value);
        }
        h.Sink.Results.Select(r => r.Delay).Distinct().Count().Should().BeGreaterThan(1, "40 draws over 1 001 values are not all the same");
    }

    // QA-1 DEFECT 3 (handler side): a valid, registered, unexpired forward is dropped as a
    // screening fault ("Faulted") when the clock reads within the delay of DateTimeOffset.MaxValue,
    // because `receivedAt + d` (ForwardHandler.cs:316) overflows inside the screening try.
    [Fact(Skip = "QA-1 DEFECT 3: ForwardHandler drops a valid forward as Faulted when receivedAt+delay overflows DateTimeOffset.MaxValue (ForwardHandler.cs:316)")]
    public async Task Handler_near_the_end_of_time_relays_with_a_saturated_NotBefore()
    {
        await using var h = new ForwardHandlerTests.Harness(processorOptions: new ForwardProcessorOptions { MaxDelay = Cap });
        h.Now = DateTimeOffset.MaxValue - TimeSpan.FromSeconds(1);

        await h.Dispatch(AsDecryptedByMediator(WithDelay(Forward(Bob), 5_000)));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Results.Should().ContainSingle().Which.NotBefore.Should().Be(DateTimeOffset.MaxValue);
    }

    [Fact]
    public async Task Handler_near_the_end_of_time_today_drops_the_forward_as_Faulted()
    {
        // Pins the observed behaviour behind DEFECT 3.
        await using var h = new ForwardHandlerTests.Harness(processorOptions: new ForwardProcessorOptions { MaxDelay = Cap });
        using var outcomes = ForwardOutcomeRecorder.For(h.Handler.Metrics.Meter);
        h.Now = DateTimeOffset.MaxValue - TimeSpan.FromSeconds(1);

        await h.Dispatch(AsDecryptedByMediator(WithDelay(Forward(Bob), 5_000)));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Results.Should().BeEmpty();
        outcomes[ForwardOutcomes.Faulted].Should().Be(1);
    }

    [Fact]
    public async Task Handler_near_the_end_of_time_without_a_hint_relays()
    {
        await using var h = new ForwardHandlerTests.Harness(processorOptions: new ForwardProcessorOptions { MaxDelay = Cap });
        h.Now = DateTimeOffset.MaxValue - TimeSpan.FromSeconds(1);

        await h.Dispatch(AsDecryptedByMediator(Forward(Bob)));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Results.Should().ContainSingle().Which.NotBefore.Should().BeNull();
    }

    [Fact]
    public async Task Handler_at_the_start_of_time_relays_with_its_delay()
    {
        await using var h = new ForwardHandlerTests.Harness(processorOptions: new ForwardProcessorOptions { MaxDelay = Cap });
        h.Now = DateTimeOffset.MinValue;

        await h.Dispatch(AsDecryptedByMediator(WithDelay(Forward(Bob), 5_000)));
        await h.Handler.FlushAsync(Flush);

        h.Sink.Results.Should().ContainSingle().Which.NotBefore.Should().Be(DateTimeOffset.MinValue + TimeSpan.FromSeconds(5));
    }

    // ── An attachment that decodes to nothing ─────────────────────────────────────────────

    // QA-1 DEFECT 5: a `data.base64` of white space (or "==") passes the IsNullOrEmpty check and
    // decodes to zero bytes. The processor relays an empty payload instead of refusing the forward as
    // malformed; behind AddMediator() the sink then gets ArgumentException from
    // MessagePickupQueue.DeliverAsync and counts a stranger's malformed forward as "Faulted" with an
    // Error line (MediatorForwardSink.cs:140-143), not as Dropped-Malformed.
    [Theory(Skip = "QA-1 DEFECT 5: a data.base64 that decodes to zero bytes is relayed as an empty payload instead of MalformedMessageException (ForwardProcessor.cs:276-285)")]
    [InlineData(" ")]
    [InlineData("    ")]
    [InlineData("\n")]
    public async Task An_attachment_that_decodes_to_no_bytes_is_refused_as_malformed(string base64)
    {
        var processor = NewProcessor();

        var act = async () => await processor.ProcessAsync(AsDecryptedByMediator(WithBase64Attachment(base64)));

        await act.Should().ThrowAsync<MalformedMessageException>();
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("    ")]
    [InlineData("\n")]
    public async Task An_attachment_that_decodes_to_no_bytes_is_today_relayed_empty(string base64)
    {
        // Pins the observed behaviour behind DEFECT 5.
        var processor = NewProcessor();

        var result = await processor.ProcessAsync(AsDecryptedByMediator(WithBase64Attachment(base64)));

        result.OnwardPacked.Should().BeEmpty();
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static Message Forward(string next) => ForwardMessage.Create(Mediator, next, new[] { Inner });

    private static Message WithId(Message message, string id)
    {
        message.Id = id;
        return message;
    }

    private static Message WithDelay(Message forward, long millis)
        => WithRawDelay(forward, millis.ToString(CultureInfo.InvariantCulture));

    private static Message WithRawDelay(Message forward, string rawJson)
    {
        forward.AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["delay_milli"] = JsonDocument.Parse(rawJson).RootElement.Clone(),
        };
        return forward;
    }

    private static Message WithBase64Attachment(string base64)
    {
        var forward = Forward(Bob);
        forward.Attachments = new List<Attachment>
        {
            new() { Id = "a1", Data = new AttachmentData { Base64 = base64 } },
        };
        return forward;
    }
}
