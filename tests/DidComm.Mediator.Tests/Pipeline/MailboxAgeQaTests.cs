using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// QA-1 (#95): the mailbox age limit (FR-PICK-10) at age − 1 tick, age and age + 1 tick on every
/// expiry path of the store the queue writes to; a not-before beyond the limit; the forward's own
/// expiry before and after the limit; what a host <see cref="IMessageStore"/> is handed as
/// <see cref="QueuedMessage.ExpiresAt"/>; clocks at the ends of time; and the option's validation.
/// </summary>
public sealed class MailboxAgeQaTests
{
    private const string Peer = "did:example:qa-age-peer";
    private const string Recipient = "did:example:qa-age-recipient";
    private static readonly TimeSpan Age = TimeSpan.FromHours(2);

    // ── the three instants around the limit ──────────────────────────────────────────────

    [Theory]
    [InlineData(-1L, true)]
    [InlineData(0L, false)]
    [InlineData(1L, false)]
    public async Task Around_the_limit_status_lease_and_purge_agree(long offsetTicks, bool deliverable)
    {
        await using var fixture = AgeFixture();
        await fixture.EnrolAsync(Peer, Recipient);
        var admitted = fixture.Time.GetUtcNow();
        var id = (await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(30))).MessageId!;
        var at = admitted + Age + TimeSpan.FromTicks(offsetTicks);

        var status = await fixture.Store.GetStatusAsync(Peer, null, at, CancellationToken.None);
        var leased = await fixture.Store.LeaseAsync(new MessageLeaseRequest(Peer, null, 10, int.MaxValue, at, new MessageLease(null, at.AddMinutes(1))), CancellationToken.None);

        status.MessageCount.Should().Be(deliverable ? 1 : 0);
        leased.Select(m => m.Id).Should().Equal(deliverable ? new[] { id } : Array.Empty<string>());
        (await fixture.Store.PurgeExpiredAsync(at, CancellationToken.None)).Should().Be(0, "status and lease already swept the grant, or nothing is due for removal yet");
        fixture.Store.MessageCount.Should().Be(deliverable ? 1 : 0);
    }

    [Theory]
    [InlineData(-1L, 0)]
    [InlineData(0L, 1)]
    [InlineData(1L, 1)]
    public async Task Around_the_limit_the_queues_own_purge_removes_exactly_what_has_aged_out(long offsetTicks, int removed)
    {
        await using var fixture = AgeFixture();
        await fixture.EnrolAsync(Peer, Recipient);
        await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(30));

        fixture.Time.Advance(Age + TimeSpan.FromTicks(offsetTicks));

        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(removed);
        fixture.Store.MessageCount.Should().Be(1 - removed);
    }

    [Fact]
    public async Task An_acknowledgement_after_the_limit_removes_nothing_that_could_still_be_delivered()
    {
        await using var fixture = AgeFixture();
        await fixture.EnrolAsync(Peer, Recipient);
        var id = (await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(30))).MessageId!;
        fixture.Time.Advance(Age);

        // The record is still held (nothing touched the grant); acknowledging it is harmless either way.
        (await fixture.Queue.RemoveAsync(Peer, id)).Should().BeTrue();
        fixture.Store.MessageCount.Should().Be(0);
    }

    // ── not-before beyond the limit ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_message_due_only_after_the_limit_is_never_counted_never_leased_and_is_purged_at_the_limit()
    {
        await using var fixture = AgeFixture();
        await fixture.EnrolAsync(Peer, Recipient);
        var admitted = fixture.Time.GetUtcNow();
        var notBefore = admitted + Age + TimeSpan.FromMinutes(1);

        var result = await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(30), new PickupDeliveryOptions(NotBefore: notBefore));
        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);

        foreach (var at in new[] { admitted, admitted + Age - TimeSpan.FromTicks(1), admitted + Age, notBefore, notBefore + TimeSpan.FromDays(1) })
        {
            (await fixture.Store.GetStatusAsync(Peer, null, at, CancellationToken.None)).MessageCount.Should().Be(0);
            (await fixture.Store.LeaseAsync(new MessageLeaseRequest(Peer, null, 10, int.MaxValue, at, new MessageLease(null, at.AddMinutes(1))), CancellationToken.None)).Should().BeEmpty();
        }
        fixture.Store.MessageCount.Should().Be(0, "the sweep at the limit removed it before it ever came due");
    }

    // ── the forward's own expiry against the limit ────────────────────────────────────────

    [Theory]
    [InlineData(-3600L)]    // its own expiry an hour before the limit: it comes first
    [InlineData(-1L)]       // its own expiry instant is the limit itself (expires_time + 1 s)
    [InlineData(0L)]        // one second after the limit: the limit comes first
    [InlineData(86_400L)]   // a day after the limit
    [InlineData(null)]
    public async Task A_host_store_is_handed_the_earlier_of_the_forward_expiry_and_the_age_limit(long? secondsAfterLimit)
    {
        var store = new HookedMessageStore();
        await using var fixture = AgeFixture(services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);
        var admitted = fixture.Time.GetUtcNow();
        long? expiresTime = secondsAfterLimit is { } s ? (admitted + Age).ToUnixTimeSeconds() + s : null;

        (await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(30), new PickupDeliveryOptions(ExpiresTime: expiresTime))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);

        var record = store.Enqueued.Should().ContainSingle().Which;
        record.ReceivedAt.Should().Be(admitted);
        // FR-ROUTE-09 as an instant: the start of second (expires_time + skew + 1); the fixture's skew is zero.
        var limit = admitted + Age;
        var expected = expiresTime is { } e && DateTimeOffset.FromUnixTimeSeconds(e + 1) < limit ? DateTimeOffset.FromUnixTimeSeconds(e + 1) : limit;
        record.ExpiresAt.Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task The_forward_expiry_handed_to_the_store_includes_the_whole_seconds_of_the_skew(int skewSeconds)
    {
        var store = new HookedMessageStore();
        await using var fixture = PushFixture.Create(
            configure: o => { o.DirectPush = false; o.Pickup.MaxMessageAge = null; },
            services: s => s.AddSingleton<IMessageStore>(store),
            didComm: o => o.ExpiresClockSkew = TimeSpan.FromSeconds(skewSeconds) + TimeSpan.FromMilliseconds(999));
        await fixture.EnrolAsync(Peer, Recipient);
        var expires = fixture.Time.GetUtcNow().ToUnixTimeSeconds() + 60;

        await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(30), new PickupDeliveryOptions(ExpiresTime: expires));

        store.Enqueued.Should().ContainSingle().Which.ExpiresAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(expires + skewSeconds + 1));
    }

    [Fact]
    public async Task With_the_limit_off_and_no_expiry_the_store_is_handed_no_expiry()
    {
        var store = new HookedMessageStore();
        await using var fixture = PushFixture.Create(
            configure: o => { o.DirectPush = false; o.Pickup.MaxMessageAge = null; },
            services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);

        await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(30));

        store.Enqueued.Should().ContainSingle().Which.ExpiresAt.Should().BeNull();
    }

    // ── the ends of time ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task At_the_start_of_time_the_limit_is_admission_plus_age()
    {
        var store = new HookedMessageStore();
        await using var fixture = AgeFixture(services: s => s.AddSingleton<IMessageStore>(store), didComm: o => o.Clock = () => DateTimeOffset.MinValue);
        await fixture.EnrolAsync(Peer, Recipient);

        (await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(30))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);

        store.Enqueued.Should().ContainSingle().Which.ExpiresAt.Should().Be(DateTimeOffset.MinValue + Age);
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-62_135_596_801L)] // one second before DateTimeOffset.MinValue
    public async Task An_expires_time_before_the_start_of_time_is_expired_at_admission_and_never_throws(long expiresTime)
    {
        await using var fixture = AgeFixture(didComm: o => o.Clock = () => DateTimeOffset.MinValue);
        await fixture.EnrolAsync(Peer, Recipient);

        (await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(30), new PickupDeliveryOptions(ExpiresTime: expiresTime))).Outcome
            .Should().Be(PickupDeliveryOutcome.Expired);
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(253_402_300_800L)] // the first second after DateTimeOffset.MaxValue
    public async Task An_expires_time_past_the_end_of_time_never_expires_and_the_age_limit_still_applies(long expiresTime)
    {
        var store = new HookedMessageStore();
        await using var fixture = AgeFixture(services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);
        var admitted = fixture.Time.GetUtcNow();

        (await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(30), new PickupDeliveryOptions(ExpiresTime: expiresTime))).Outcome
            .Should().Be(PickupDeliveryOutcome.Queued);

        store.Enqueued.Should().ContainSingle().Which.ExpiresAt.Should().Be(admitted + Age);
    }

    // ── the option ────────────────────────────────────────────────────────────────────────

    public static TheoryData<long> InvalidAges => new() { 0L, -1L, TimeSpan.MinValue.Ticks };

    [Theory]
    [MemberData(nameof(InvalidAges))]
    public void A_limit_that_is_not_greater_than_zero_is_refused_by_name(long ticks)
    {
        var options = new MessagePickupOptions { MaxMessageAge = TimeSpan.FromTicks(ticks) };

        options.Invoking(o => o.Validate()).Should().Throw<ArgumentException>().WithParameterName(nameof(MessagePickupOptions.MaxMessageAge));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    public void A_limit_from_one_tick_to_TimeSpan_MaxValue_is_accepted(long ticks)
    {
        new MessagePickupOptions { MaxMessageAge = TimeSpan.FromTicks(ticks) }.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Fact]
    public async Task A_limit_of_TimeSpan_MaxValue_saturates_at_the_end_of_time()
    {
        var store = new HookedMessageStore();
        await using var fixture = PushFixture.Create(
            configure: o => { o.DirectPush = false; o.Pickup.MaxMessageAge = TimeSpan.MaxValue; },
            services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);

        await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(30));

        store.Enqueued.Should().ContainSingle().Which.ExpiresAt.Should().Be(DateTimeOffset.MaxValue);
    }

    [Fact]
    public void Under_AddMediator_a_limit_equal_to_the_delay_cap_is_refused_and_one_tick_longer_is_not()
    {
        var equal = new MediatorOptions { MaxForwardDelay = TimeSpan.FromMinutes(10) };
        equal.Pickup.MaxMessageAge = TimeSpan.FromMinutes(10);
        equal.Invoking(o => o.Validate()).Should().Throw<ArgumentException>().Which.Message.Should().NotContain("00:10:00");

        var longer = new MediatorOptions { MaxForwardDelay = TimeSpan.FromMinutes(10) };
        longer.Pickup.MaxMessageAge = TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(1);
        longer.Invoking(o => o.Validate()).Should().NotThrow();

        var zeroCap = new MediatorOptions { MaxForwardDelay = TimeSpan.Zero };
        zeroCap.Pickup.MaxMessageAge = TimeSpan.FromTicks(1);
        zeroCap.Invoking(o => o.Validate()).Should().NotThrow();
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static PushFixture AgeFixture(Action<IServiceCollection>? services = null, Action<DidComm.Facade.DidCommOptions>? didComm = null)
        => PushFixture.Create(
            configure: o =>
            {
                o.DirectPush = false;
                o.Pickup.MaxMessageAge = Age;
            },
            services: services,
            didComm: o =>
            {
                o.ExpiresClockSkew = TimeSpan.Zero;
                didComm?.Invoke(o);
            });
}
