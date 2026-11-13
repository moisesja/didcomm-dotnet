using System.Globalization;
using System.Text.Json;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using AwesomeAssertions;
using Xunit;
using static DidComm.Tests.Protocols.Routing.ForwardTestEnvelopes;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// The FR-ROUTE-10 delay cap (#95, JC-1): <see cref="ForwardProcessorOptions.MaxDelay"/> shortens a
/// longer positive <c>delay_milli</c> to the cap and limits <c>|n|</c> of a negative one to the cap
/// BEFORE the one uniform draw. Pinned at the arithmetic (<see cref="ForwardProcessor.ExtractDelay"/>),
/// through the public <see cref="ForwardProcessor.ProcessAsync(UnpackResult, CancellationToken)"/>
/// overload, and through <see cref="ForwardHandler"/> with an injected clock, where the
/// <see cref="ForwardProcessingResult.NotBefore"/> reaching the sink is the receipt time plus
/// <c>min(n, cap)</c>.
/// </summary>
public sealed class ForwardDelayCapTests
{
    private const string Mediator = "did:example:mediator";
    private const string Bob = "did:example:bob";
    private const string Inner = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";
    private static readonly TimeSpan Cap = TimeSpan.FromHours(1);
    private static readonly long CapMs = (long)Cap.TotalMilliseconds;
    private static readonly DateTimeOffset Fixed = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    // ── Positive hints ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1L, -1L)]          // cap − 1 ms: honoured as is
    [InlineData(0L, 0L)]            // exactly the cap
    [InlineData(1L, 0L)]            // cap + 1 ms: shortened to the cap
    public void Positive_hint_around_the_cap_is_held_for_min_of_n_and_cap(long offsetFromCap, long expectedOffsetFromCap)
    {
        var delay = ForwardProcessor.ExtractDelay(WithDelay(Forward(Bob), CapMs + offsetFromCap), Cap);

        delay.Should().Be(TimeSpan.FromMilliseconds(CapMs + expectedOffsetFromCap));
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData(1_000_000_000_000_000L)]
    public void Huge_positive_hint_is_shortened_to_the_cap(long huge)
    {
        ForwardProcessor.ExtractDelay(WithDelay(Forward(Bob), huge), Cap).Should().Be(Cap);
    }

    [Fact]
    public void Fractional_cap_is_floored_to_whole_milliseconds()
    {
        var cap = TimeSpan.FromMilliseconds(10) + TimeSpan.FromTicks(9_999); // 10.9999 ms

        ForwardProcessor.ExtractDelay(WithDelay(Forward(Bob), 11), cap).Should().Be(TimeSpan.FromMilliseconds(10));
    }

    // ── Negative hints: the clamp precedes the draw ───────────────────────────────────────

    [Fact]
    public void Negative_hint_ten_times_the_cap_draws_uniformly_under_the_cap_never_clamped_after_the_draw()
    {
        // |n| = 10 × cap. Clamping |n| before the draw gives a uniform draw in [0, cap]: the chance
        // of hitting the cap exactly is 1 / (cap + 1) per draw. Clamping AFTER a draw in [0, |n|]
        // would put ~90 % of the draws exactly on the cap — this test tells the two apart.
        var forward = WithDelay(Forward(Bob), -10 * CapMs);
        const int draws = 1000;
        var atCap = 0;
        var distinct = new HashSet<TimeSpan>();

        for (var i = 0; i < draws; i++)
        {
            var delay = ForwardProcessor.ExtractDelay(forward, Cap);
            delay.Should().NotBeNull();
            delay!.Value.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero).And.BeLessThanOrEqualTo(Cap);
            if (delay.Value == Cap)
                atCap++;
            distinct.Add(delay.Value);
        }

        atCap.Should().BeLessThan(draws / 100, "fewer than 1 % of the draws may land exactly on the cap");
        distinct.Count.Should().BeGreaterThan(draws / 2, "the draws must spread over the range, not collapse onto a point");
    }

    [Fact]
    public void Negative_hint_within_the_cap_draws_within_its_own_magnitude()
    {
        var forward = WithDelay(Forward(Bob), -500);

        for (var i = 0; i < 200; i++)
            ForwardProcessor.ExtractDelay(forward, Cap)!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void Long_MinValue_with_a_cap_draws_under_the_cap_and_never_throws()
    {
        var forward = WithDelay(Forward(Bob), long.MinValue);

        for (var i = 0; i < 200; i++)
            ForwardProcessor.ExtractDelay(forward, Cap)!.Value.Should().BeLessThanOrEqualTo(Cap);
    }

    // ── Cap zero and no cap ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(750L)]
    [InlineData(long.MaxValue)]
    [InlineData(-750L)]
    [InlineData(long.MinValue)]
    [InlineData(0L)]
    public void Zero_cap_makes_every_hint_due_at_once(long millis)
    {
        ForwardProcessor.ExtractDelay(WithDelay(Forward(Bob), millis), TimeSpan.Zero).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Null_cap_keeps_the_int_MaxValue_millisecond_bound()
    {
        ForwardProcessor.ExtractDelay(WithDelay(Forward(Bob), long.MaxValue), maxDelay: null)
            .Should().Be(TimeSpan.FromMilliseconds(int.MaxValue));
        ForwardProcessor.ExtractDelay(WithDelay(Forward(Bob), int.MaxValue), maxDelay: null)
            .Should().Be(TimeSpan.FromMilliseconds(int.MaxValue));
        ForwardProcessor.ExtractDelay(WithDelay(Forward(Bob), 750), maxDelay: null)
            .Should().Be(TimeSpan.FromMilliseconds(750));
        for (var i = 0; i < 50; i++)
        {
            ForwardProcessor.ExtractDelay(WithDelay(Forward(Bob), long.MinValue), maxDelay: null)!.Value
                .Should().BeLessThan(TimeSpan.FromMilliseconds(int.MaxValue));
        }
    }

    [Fact]
    public void Cap_at_the_library_bound_behaves_like_no_cap_for_a_positive_hint()
    {
        ForwardProcessor.ExtractDelay(WithDelay(Forward(Bob), long.MaxValue), TimeSpan.FromMilliseconds(int.MaxValue))
            .Should().Be(TimeSpan.FromMilliseconds(int.MaxValue));
    }

    [Fact]
    public void Absent_hint_is_null_whatever_the_cap()
    {
        ForwardProcessor.ExtractDelay(Forward(Bob), Cap).Should().BeNull();
        ForwardProcessor.ExtractDelay(Forward(Bob), TimeSpan.Zero).Should().BeNull();
    }

    // ── Validation ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1L)]
    [InlineData((long)int.MaxValue + 1)]
    public void MaxDelay_outside_zero_to_int_MaxValue_milliseconds_is_refused_by_Validate_and_the_processor(long millis)
    {
        var options = new ForwardProcessorOptions { MaxDelay = TimeSpan.FromMilliseconds(millis) };

        options.Invoking(o => o.Validate()).Should().Throw<ArgumentException>().Which.ParamName.Should().Be(nameof(ForwardProcessorOptions.MaxDelay));
        FluentActions.Invoking(() => NewProcessor(options)).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(int.MaxValue)]
    public void MaxDelay_at_either_end_of_the_range_is_accepted(long millis)
    {
        var options = new ForwardProcessorOptions { MaxDelay = TimeSpan.FromMilliseconds(millis) };

        options.Invoking(o => o.Validate()).Should().NotThrow();
        FluentActions.Invoking(() => NewProcessor(options)).Should().NotThrow();
    }

    // ── Through the public ProcessAsync overload ──────────────────────────────────────────

    [Theory]
    [InlineData(-1L, -1L)]
    [InlineData(0L, 0L)]
    [InlineData(1L, 0L)]
    public async Task Public_ProcessAsync_applies_the_cap_to_Delay_and_NotBefore(long offsetFromCap, long expectedOffsetFromCap)
    {
        var processor = NewProcessor(
            new ForwardProcessorOptions { MaxDelay = Cap },
            didCommOptions: new DidCommOptions { Clock = () => Fixed });

        var result = await processor.ProcessAsync(AsDecryptedByMediator(WithDelay(Forward(Bob), CapMs + offsetFromCap)));

        var expected = TimeSpan.FromMilliseconds(CapMs + expectedOffsetFromCap);
        result.Delay.Should().Be(expected);
        result.NotBefore.Should().Be(Fixed + expected);
    }

    [Fact]
    public async Task Public_ProcessAsync_draws_a_negative_hint_under_the_cap()
    {
        var processor = NewProcessor(
            new ForwardProcessorOptions { MaxDelay = TimeSpan.FromMilliseconds(100) },
            didCommOptions: new DidCommOptions { Clock = () => Fixed });

        var result = await processor.ProcessAsync(AsDecryptedByMediator(WithDelay(Forward(Bob), long.MinValue)));

        result.Delay!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(100));
        result.NotBefore.Should().Be(Fixed + result.Delay.Value);
    }

    // ── Through ForwardHandler, injected clock ────────────────────────────────────────────

    [Theory]
    [InlineData(-1L, -1L)]
    [InlineData(0L, 0L)]
    [InlineData(1L, 0L)]
    [InlineData(long.MaxValue, null)]
    public async Task Handler_hands_the_sink_NotBefore_at_receipt_plus_min_of_n_and_cap(long hint, long? expectedOffsetFromCap)
    {
        // `hint` is an offset from the cap, except long.MaxValue, which is sent as is.
        var millis = hint == long.MaxValue ? long.MaxValue : CapMs + hint;
        var expected = TimeSpan.FromMilliseconds(CapMs + (expectedOffsetFromCap ?? 0));
        await using var h = new ForwardHandlerTests.Harness(processorOptions: new ForwardProcessorOptions { MaxDelay = Cap });
        var receivedAt = h.Now;

        await h.Dispatch(AsDecryptedByMediator(WithDelay(Forward(Bob), millis)));
        h.Now = receivedAt.AddHours(5); // time spent queued never moves the schedule
        await h.Handler.FlushAsync(TimeSpan.FromSeconds(30));

        var result = h.Sink.Results.Should().ContainSingle().Which;
        result.Delay.Should().Be(expected);
        result.NotBefore.Should().Be(receivedAt + expected);
    }

    [Fact]
    public async Task Handler_without_a_cap_keeps_todays_bound()
    {
        await using var h = new ForwardHandlerTests.Harness();

        await h.Dispatch(AsDecryptedByMediator(WithDelay(Forward(Bob), long.MaxValue)));
        await h.Handler.FlushAsync(TimeSpan.FromSeconds(30));

        h.Sink.Results.Should().ContainSingle().Which.NotBefore.Should().Be(h.Now.AddMilliseconds(int.MaxValue));
    }

    [Fact]
    public async Task Handler_draws_a_negative_hint_once_under_the_cap()
    {
        await using var h = new ForwardHandlerTests.Harness(processorOptions: new ForwardProcessorOptions { MaxDelay = TimeSpan.FromMilliseconds(250) });

        await h.Dispatch(AsDecryptedByMediator(WithDelay(Forward(Bob), -10_000)));
        await h.Handler.FlushAsync(TimeSpan.FromSeconds(30));

        var result = h.Sink.Results.Should().ContainSingle().Which;
        result.Delay!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(250));
        result.NotBefore.Should().Be(h.Now + result.Delay.Value, "the delay reported and the schedule come from the same single draw");
    }

    // ── The not-before sum saturates (Q-5) ────────────────────────────────────────────────

    [Fact]
    public void NotBeforeOf_adds_the_hold_and_is_null_without_one()
    {
        ForwardProcessor.NotBeforeOf(Fixed, null).Should().BeNull();
        ForwardProcessor.NotBeforeOf(Fixed, TimeSpan.Zero).Should().Be(Fixed);
        ForwardProcessor.NotBeforeOf(Fixed, Cap).Should().Be(Fixed + Cap);
        ForwardProcessor.NotBeforeOf(DateTimeOffset.MinValue, TimeSpan.FromSeconds(5)).Should().Be(DateTimeOffset.MinValue.AddSeconds(5));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(5_000L)]
    [InlineData(int.MaxValue)]
    public void NotBeforeOf_pins_to_the_end_of_time_instead_of_throwing(long holdMs)
    {
        var lastSecond = DateTimeOffset.MaxValue - TimeSpan.FromSeconds(1);
        var expected = holdMs <= 1_000 ? lastSecond.AddMilliseconds(holdMs) : DateTimeOffset.MaxValue;

        ForwardProcessor.NotBeforeOf(lastSecond, TimeSpan.FromMilliseconds(holdMs)).Should().Be(expected);
        ForwardProcessor.NotBeforeOf(DateTimeOffset.MaxValue, TimeSpan.FromMilliseconds(holdMs)).Should().Be(DateTimeOffset.MaxValue);
    }

    [Fact]
    public void NotBeforeOf_counts_the_room_left_in_a_positive_offsets_clock_reading()
    {
        // UTC six hours before the end of time, read in +05:00: the local reading has one hour left.
        var reading = new DateTimeOffset(DateTime.MaxValue.AddHours(-1), TimeSpan.FromHours(5));

        ForwardProcessor.NotBeforeOf(reading, TimeSpan.FromMinutes(30)).Should().Be(reading.AddMinutes(30));
        ForwardProcessor.NotBeforeOf(reading, TimeSpan.FromHours(2)).Should().Be(DateTimeOffset.MaxValue, "the sum would not be representable in its own offset");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static Message Forward(string next) => ForwardMessage.Create(Mediator, next, new[] { Inner });

    private static Message WithDelay(Message forward, long millis)
    {
        forward.AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["delay_milli"] = JsonDocument.Parse(millis.ToString(CultureInfo.InvariantCulture)).RootElement.Clone(),
        };
        return forward;
    }
}
