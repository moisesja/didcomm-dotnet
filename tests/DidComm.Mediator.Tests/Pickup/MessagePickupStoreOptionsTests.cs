using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Xunit;
using static DidComm.Mediator.Tests.Pickup.MessagePickupStoreSupport.MessagePickupStoreKit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// <see cref="MessagePickupOptions"/> (FR-PICK-03, -04, -10; D7): the documented defaults, and
/// <see cref="MessagePickupOptions.Validate"/> at the exact edge of every rule, each refusal naming
/// the setting at fault. <see cref="InMemoryMessageStore"/> applies the same check when it is built.
/// </summary>
public sealed class MessagePickupStoreOptionsTests
{
    private const int MiB = 1024 * 1024;

    private static void RequireRefused(MessagePickupOptions options, string setting)
        => FluentActions.Invoking(options.Validate).Should().Throw<ArgumentException>().Which.ParamName.Should().Be(setting);

    private static void RequireAccepted(MessagePickupOptions options)
        => FluentActions.Invoking(options.Validate).Should().NotThrow();

    // ---- defaults -------------------------------------------------------------------------

    [Fact]
    public void The_defaults_are_the_documented_ones_and_they_validate()
    {
        var options = new MessagePickupOptions();

        options.MaxDeliveryBatch.Should().Be(10);
        options.MaxDeliveryBytes.Should().BeNull("null means DidCommOptions.MaxReceiveBytes");
        options.MaxAckIds.Should().BeNull("null means MaxDeliveryBatch × 10");
        options.LeaseTimeout.Should().Be(TimeSpan.FromSeconds(60));
        options.MaxQueueMessages.Should().Be(100);
        options.MaxQueueBytes.Should().Be(8L * MiB);
        options.MaxStoreMessages.Should().Be(5_000);
        options.MaxStoreBytes.Should().Be(128L * MiB);
        options.EffectiveMaxAckIds.Should().Be(100);
        RequireAccepted(options);
    }

    [Theory]
    [InlineData(10, null, 100)]
    [InlineData(1, null, 10)]
    [InlineData(25, null, 250)]
    [InlineData(10, 10, 10)]                               // a configured value wins, down to the batch itself
    [InlineData(10, 4096, 4096)]
    [InlineData(214_748_364, null, 2_147_483_640)]
    [InlineData(214_748_365, null, int.MaxValue)]          // × 10 would pass int.MaxValue: pinned to it, never wrapped
    [InlineData(int.MaxValue, null, int.MaxValue)]
    public void EffectiveMaxAckIds_is_the_configured_value_or_ten_batches(int batch, int? maxAckIds, int expected)
    {
        var options = new MessagePickupOptions { MaxDeliveryBatch = batch, MaxAckIds = maxAckIds };

        options.EffectiveMaxAckIds.Should().Be(expected);
        RequireAccepted(options);
    }

    // ---- every bound is at least 1 --------------------------------------------------------

    private static MessagePickupOptions WithBound(string setting, long value)
    {
        var options = new MessagePickupOptions();
        var asInt = (int)Math.Clamp(value, int.MinValue, int.MaxValue);
        switch (setting)
        {
            case nameof(MessagePickupOptions.MaxDeliveryBatch): options.MaxDeliveryBatch = asInt; break;
            case nameof(MessagePickupOptions.MaxQueueMessages): options.MaxQueueMessages = asInt; break;
            case nameof(MessagePickupOptions.MaxQueueBytes): options.MaxQueueBytes = value; break;
            case nameof(MessagePickupOptions.MaxStoreMessages): options.MaxStoreMessages = asInt; break;
            case nameof(MessagePickupOptions.MaxStoreBytes): options.MaxStoreBytes = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(setting));
        }
        return options;
    }

    public static TheoryData<string, long> BoundsBelowOne
    {
        get
        {
            var data = new TheoryData<string, long>();
            foreach (var setting in new[]
                     {
                         nameof(MessagePickupOptions.MaxDeliveryBatch), nameof(MessagePickupOptions.MaxQueueMessages), nameof(MessagePickupOptions.MaxQueueBytes),
                         nameof(MessagePickupOptions.MaxStoreMessages), nameof(MessagePickupOptions.MaxStoreBytes),
                     })
            {
                data.Add(setting, 0);
                data.Add(setting, -1);
                data.Add(setting, long.MinValue);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(BoundsBelowOne))]
    public void A_bound_below_one_is_refused_by_name(string setting, long value)
    {
        RequireRefused(WithBound(setting, value), setting);
    }

    [Fact]
    public void Every_bound_at_exactly_one_is_accepted()
    {
        var smallest = new MessagePickupOptions
        {
            MaxDeliveryBatch = 1,
            MaxAckIds = 1,
            LeaseTimeout = Tick,
            MaxQueueMessages = 1,
            MaxQueueBytes = 1,
            MaxStoreMessages = 1,
            MaxStoreBytes = 1,
        };

        RequireAccepted(smallest);
        smallest.EffectiveMaxAckIds.Should().Be(1);
    }

    [Fact]
    public void The_largest_values_are_accepted()
    {
        RequireAccepted(new MessagePickupOptions
        {
            MaxDeliveryBatch = int.MaxValue,
            MaxAckIds = int.MaxValue,
            LeaseTimeout = TimeSpan.FromHours(24),
            MaxQueueMessages = int.MaxValue,
            MaxQueueBytes = long.MaxValue,
            MaxStoreMessages = int.MaxValue,
            MaxStoreBytes = long.MaxValue,
        });
    }

    // ---- MaxAckIds ------------------------------------------------------------------------

    [Theory]
    [InlineData(10, 9, false)]             // a full batch could not be acknowledged in one message
    [InlineData(10, 10, true)]
    [InlineData(10, 11, true)]
    [InlineData(10, int.MaxValue, true)]
    [InlineData(10, 0, false)]
    [InlineData(10, -1, false)]
    [InlineData(10, int.MinValue, false)]
    [InlineData(1, 1, true)]
    [InlineData(1, 0, false)]
    [InlineData(500, 499, false)]
    [InlineData(500, 500, true)]
    public void MaxAckIds_must_be_at_least_MaxDeliveryBatch(int batch, int maxAckIds, bool valid)
    {
        var options = new MessagePickupOptions { MaxDeliveryBatch = batch, MaxAckIds = maxAckIds };

        if (valid)
            RequireAccepted(options);
        else
            RequireRefused(options, nameof(MessagePickupOptions.MaxAckIds));
    }

    // ---- LeaseTimeout ---------------------------------------------------------------------

    public static TheoryData<long, bool> LeaseTimeouts => new()
    {
        { TimeSpan.MinValue.Ticks, false },
        { Timeout.InfiniteTimeSpan.Ticks, false },          // -1 ms: "never" is not a lease timeout
        { -1, false },
        { 0, false },
        { 1, true },                                         // one tick
        { TimeSpan.FromSeconds(60).Ticks, true },
        { TimeSpan.FromHours(24).Ticks - 1, true },
        { TimeSpan.FromHours(24).Ticks, true },              // exactly 24 hours
        { TimeSpan.FromHours(24).Ticks + 1, false },         // one tick over
        { TimeSpan.FromDays(365).Ticks, false },
        { TimeSpan.MaxValue.Ticks, false },
    };

    [Theory]
    [MemberData(nameof(LeaseTimeouts))]
    public void LeaseTimeout_must_be_above_zero_and_at_most_24_hours(long ticks, bool valid)
    {
        var options = new MessagePickupOptions { LeaseTimeout = TimeSpan.FromTicks(ticks) };

        if (valid)
            RequireAccepted(options);
        else
            RequireRefused(options, nameof(MessagePickupOptions.LeaseTimeout));
    }

    // ---- store-wide bounds against per-grant bounds ---------------------------------------

    [Theory]
    [InlineData(100, 99, false)]
    [InlineData(100, 100, true)]
    [InlineData(100, 101, true)]
    [InlineData(5_001, 5_000, false)]      // the per-grant bound raised past the default store-wide one
    [InlineData(1, 1, true)]
    public void MaxStoreMessages_must_be_at_least_MaxQueueMessages(int perGrant, int storeWide, bool valid)
    {
        var options = new MessagePickupOptions { MaxQueueMessages = perGrant, MaxStoreMessages = storeWide };

        if (valid)
            RequireAccepted(options);
        else
            RequireRefused(options, nameof(MessagePickupOptions.MaxStoreMessages));
    }

    [Theory]
    [InlineData(1_000L, 999L, false)]
    [InlineData(1_000L, 1_000L, true)]
    [InlineData(1_000L, 1_001L, true)]
    [InlineData(128L * MiB + 1, 128L * MiB, false)]    // the per-grant bound raised past the default store-wide one
    [InlineData(1L, 1L, true)]
    public void MaxStoreBytes_must_be_at_least_MaxQueueBytes(long perGrant, long storeWide, bool valid)
    {
        var options = new MessagePickupOptions { MaxQueueBytes = perGrant, MaxStoreBytes = storeWide };

        if (valid)
            RequireAccepted(options);
        else
            RequireRefused(options, nameof(MessagePickupOptions.MaxStoreBytes));
    }

    // ---- MaxDeliveryBytes -----------------------------------------------------------------

    /// <summary>The smallest <c>MaxDeliveryBytes</c> that carries any payload, found by asking the sizing for every value in turn.</summary>
    private static int SmallestDeliveryBytes(int batch)
    {
        for (var candidate = 1; candidate < 16 * MiB; candidate++)
        {
            if (PickupSizing.ComputeBatchPayloadBudget(candidate, batch) >= 1)
                return candidate;
        }
        throw new InvalidOperationException("No delivery size carries a payload.");
    }

    [Theory]
    // By hand, from the documented steps: the payload text needs 4 characters, so the plaintext is
    // 8192 + 96 × batch + 4 at least, which takes a whole number of 16-byte blocks plus one byte,
    // whose base64url text must fit beside the 32 KiB envelope allowance.
    [InlineData(1, 43_840)]
    [InlineData(10, 44_992)]
    [InlineData(100, 56_512)]
    public void MaxDeliveryBytes_is_accepted_from_the_smallest_value_that_carries_a_payload(int batch, int smallest)
    {
        SmallestDeliveryBytes(batch).Should().Be(smallest);

        RequireAccepted(new MessagePickupOptions { MaxDeliveryBatch = batch, MaxDeliveryBytes = smallest });
        RequireRefused(new MessagePickupOptions { MaxDeliveryBatch = batch, MaxDeliveryBytes = smallest - 1 }, nameof(MessagePickupOptions.MaxDeliveryBytes));

        // The same edge where the value is used: the sizing a queue is built with.
        new PickupSizing(new MessagePickupOptions { MaxDeliveryBatch = batch, MaxDeliveryBytes = smallest }, MiB).MaxAdmissiblePayloadBytes.Should().BeGreaterThanOrEqualTo(1);
        FluentActions.Invoking(() => new PickupSizing(new MessagePickupOptions { MaxDeliveryBatch = batch, MaxDeliveryBytes = smallest - 1 }, MiB))
            .Should().Throw<ArgumentException>().Which.ParamName.Should().Be(nameof(MessagePickupOptions.MaxDeliveryBytes));
    }

    [Theory]
    [InlineData(int.MinValue, false)]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(32 * 1024, false)]          // the envelope allowance alone
    [InlineData(40 * 1024, false)]          // plus the skeleton allowance: still nothing for a payload
    [InlineData(64 * 1024, true)]
    [InlineData(MiB, true)]
    [InlineData(int.MaxValue, true)]
    public void MaxDeliveryBytes_too_small_to_carry_any_message_is_refused(int maxDeliveryBytes, bool valid)
    {
        var options = new MessagePickupOptions { MaxDeliveryBytes = maxDeliveryBytes };

        if (valid)
            RequireAccepted(options);
        else
            RequireRefused(options, nameof(MessagePickupOptions.MaxDeliveryBytes));
    }

    [Fact]
    public void A_batch_too_large_for_MaxDeliveryBytes_is_refused_as_a_MaxDeliveryBytes_that_is_too_small()
    {
        // 64 KiB carries a batch of 10. A batch of 1000 needs 96 000 bytes for its descriptors alone.
        RequireAccepted(new MessagePickupOptions { MaxDeliveryBytes = 64 * 1024, MaxDeliveryBatch = 10 });
        RequireRefused(new MessagePickupOptions { MaxDeliveryBytes = 64 * 1024, MaxDeliveryBatch = 1_000 }, nameof(MessagePickupOptions.MaxDeliveryBytes));
        // Without a MaxDeliveryBytes of its own the check waits for MaxReceiveBytes: Validate cannot make it.
        RequireAccepted(new MessagePickupOptions { MaxDeliveryBatch = 1_000_000 });
    }

    // ---- the in-memory store applies the same check ---------------------------------------

    [Fact]
    public void The_store_constructor_validates_its_options()
    {
        FluentActions.Invoking(() => new InMemoryMessageStore(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new InMemoryMessageStore(new MessagePickupOptions { MaxQueueMessages = 0 }))
            .Should().Throw<ArgumentException>().Which.ParamName.Should().Be(nameof(MessagePickupOptions.MaxQueueMessages));
        FluentActions.Invoking(() => new InMemoryMessageStore(new MessagePickupOptions { MaxQueueBytes = 2, MaxStoreBytes = 1 }))
            .Should().Throw<ArgumentException>().Which.ParamName.Should().Be(nameof(MessagePickupOptions.MaxStoreBytes));
        // The options are checked as a whole, also the settings the store does not read itself.
        FluentActions.Invoking(() => new InMemoryMessageStore(new MessagePickupOptions { LeaseTimeout = TimeSpan.Zero }))
            .Should().Throw<ArgumentException>().Which.ParamName.Should().Be(nameof(MessagePickupOptions.LeaseTimeout));

        var store = new InMemoryMessageStore();
        (store.MessageCount, store.TotalBytes).Should().Be((0, 0L));
    }

    [Fact]
    public async Task The_store_reads_its_bounds_once_when_it_is_built()
    {
        var options = new MessagePickupOptions { MaxQueueMessages = 2, MaxQueueBytes = 25, MaxStoreMessages = 3, MaxStoreBytes = 35 };
        var store = new InMemoryMessageStore(options);

        // Changing the options afterwards changes nothing: not looser, and not broken either.
        options.MaxQueueMessages = 1_000;
        options.MaxQueueBytes = MiB;
        options.MaxStoreMessages = 0;
        options.MaxStoreBytes = -1;

        await store.AddAsync(Alice, "a", "b");
        (await store.EnqueueAsync(Message("c", size: 1), null, T0, None)).Should().Be(EnqueueOutcome.GrantMessageLimitReached);
        (await store.AcknowledgeAsync(Alice, new[] { "b" }, None)).Should().Be(1);
        (await store.EnqueueAsync(Message("c", size: 16), null, T0, None)).Should().Be(EnqueueOutcome.GrantByteLimitReached);
        (await store.EnqueueAsync(Message("c", size: 15), null, T0, None)).Should().Be(EnqueueOutcome.Enqueued);
        (await store.EnqueueAsync(Message("x", Bob, size: 11), null, T0, None)).Should().Be(EnqueueOutcome.StoreLimitReached);
        (await store.EnqueueAsync(Message("x", Bob, size: 10), null, T0, None)).Should().Be(EnqueueOutcome.Enqueued);
        (await store.EnqueueAsync(Message("y", Carol, size: 0), null, T0, None)).Should().Be(EnqueueOutcome.StoreLimitReached);
    }
}
