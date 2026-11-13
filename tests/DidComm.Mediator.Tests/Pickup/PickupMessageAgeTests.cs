using DidComm.Facade;
using DidComm.Mediator.Pickup;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// #95 JC-2 (design §4): the mailbox age limit (<see cref="MessagePickupOptions.MaxMessageAge"/>) is
/// folded into the record's <see cref="QueuedMessage.ExpiresAt"/> at admission —
/// <c>min(forward expiry, ReceivedAt + MaxMessageAge)</c> on the mediator's clock, saturating at the
/// end of time — so every existing expiry path applies it to waiting, leased and passed-over records
/// alike. Judged on an injected clock: at <c>age − 1 tick</c> a message is counted and deliverable, at
/// <c>age</c> it is neither and <see cref="MessagePickupQueue.PurgeExpiredAsync"/> removes it.
/// </summary>
public sealed class PickupMessageAgeTests
{
    private static readonly TimeSpan Age = TimeSpan.FromDays(30);

    [Fact]
    public async Task The_default_limit_is_30_days_and_is_written_onto_the_record()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (_, peer) = await fixture.EnrolledAsync();
        var admittedAt = fixture.Clock.Now;

        await fixture.QueueAsync(peer, PickupFixture.Payload(20));

        var record = fixture.FakeStore!.Enqueued.Should().ContainSingle().Which.Message;
        record.ReceivedAt.Should().Be(admittedAt);
        record.ExpiresAt.Should().Be(admittedAt + Age);
    }

    [Fact]
    public async Task One_tick_before_the_limit_a_message_is_counted_and_deliverable()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var id = await fixture.QueueAsync(peer, PickupFixture.Payload(20));

        fixture.Clock.Advance(Age - TimeSpan.FromTicks(1));

        (await MessagePickupLive.StatusAsync(fixture, peer, mediator)).MessageCount.Should().Be(1);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Which.Id.Should().Be(id);
    }

    [Fact]
    public async Task At_the_limit_a_message_is_not_counted_not_leased_and_PurgeExpiredAsync_removes_it()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        await fixture.QueueAsync(peer, PickupFixture.Payload(20));
        fixture.Clock.Advance(Age);
        fixture.Memory.MessageCount.Should().Be(1, "nothing has touched the store since: the record is still held");

        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(1);
        fixture.Memory.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task At_the_limit_status_and_delivery_request_see_nothing()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        await fixture.QueueAsync(peer, PickupFixture.Payload(20));
        fixture.Clock.Advance(Age);

        (await MessagePickupLive.StatusAsync(fixture, peer, mediator)).MessageCount.Should().Be(0);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().BeEmpty();
        fixture.FakeStore!.LeaseRequests.Should().NotBeEmpty("the request asked the store, which handed nothing out");
    }

    [Fact]
    public async Task The_store_full_sweep_frees_another_grants_aged_records()
    {
        await using var fixture = PickupFixture.Create(pickup: o =>
        {
            o.MaxQueueMessages = 2;
            o.MaxStoreMessages = 2;
        });
        var (mediator, alice) = await fixture.EnrolledAsync();
        var bob = (await fixture.NewPeerAsync()).Did;
        await fixture.EnrolAsync(bob, mediator, bob);
        await fixture.QueueAsync(alice, PickupFixture.Payload(20, 1));
        await fixture.QueueAsync(alice, PickupFixture.Payload(20, 2));
        (await fixture.Queue.DeliverAsync(bob, PickupFixture.Payload(20, 3))).Outcome.Should().Be(PickupDeliveryOutcome.QueueFull, "the store is full of Alice's messages");

        fixture.Clock.Advance(Age);
        var result = await fixture.Queue.DeliverAsync(bob, PickupFixture.Payload(20, 4));

        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued, "Alice's aged records no longer keep a message out");
        fixture.Memory.MessageCount.Should().Be(1, "the sweep removed both of Alice's aged records");
        (await MessagePickupLive.FetchNowAsync(fixture, bob, mediator)).Should().ContainSingle().Which.Id.Should().Be(result.MessageId);
    }

    [Fact]
    public async Task A_leased_record_ages_out_and_a_later_acknowledgement_is_ignored()
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.LeaseTimeout = TimeSpan.FromHours(24));
        var (mediator, peer) = await fixture.EnrolledAsync();
        var id = await fixture.QueueAsync(peer, PickupFixture.Payload(20));
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle("now under a 24 h lease");

        fixture.Clock.Advance(Age);

        (await MessagePickupLive.StatusAsync(fixture, peer, mediator)).MessageCount.Should().Be(0, "a delivered, unacknowledged message ages out too");
        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(0, "the status request already swept the grant");
        fixture.Memory.MessageCount.Should().Be(0);
        (await MessagePickupLive.AckAsync(fixture, peer, mediator, id)).MessageCount.Should().Be(0, "acknowledging an aged-out id changes nothing");
        fixture.Memory.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_record_under_a_live_lease_is_not_delivered_past_the_limit_even_when_its_lease_is_younger()
    {
        // Lease taken one minute before the limit, lasting 24 h: the age limit still ends the record.
        await using var fixture = PickupFixture.Create(pickup: o => o.LeaseTimeout = TimeSpan.FromHours(24));
        var (mediator, peer) = await fixture.EnrolledAsync();
        await fixture.QueueAsync(peer, PickupFixture.Payload(20));
        fixture.Clock.Advance(Age - TimeSpan.FromMinutes(1));
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle();

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(1, "expiry is judged on time alone, leased or not");
        fixture.Memory.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_passed_over_record_ages_out()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var id = await fixture.QueueAsync(peer, PickupFixture.Payload(20));
        var lease = new MessageLease(null, fixture.Clock.Now.AddMinutes(1));
        var leased = await fixture.Store.LeaseAsync(new MessageLeaseRequest(peer, null, 10, long.MaxValue, fixture.Clock.Now, lease), CancellationToken.None);
        leased.Should().ContainSingle().Which.Id.Should().Be(id);
        (await fixture.Store.PassOverAsync(peer, new[] { id }, lease.Id, DateTimeOffset.MaxValue, CancellationToken.None)).Should().Be(1);

        fixture.Clock.Advance(Age - TimeSpan.FromTicks(1));
        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(0, "passed over, but not yet aged");
        fixture.Clock.Advance(TimeSpan.FromTicks(1));

        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(1, "a pass-over mark does not outlive the age limit");
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator)).MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task With_the_limit_off_a_message_never_ages()
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.MaxMessageAge = null, fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var id = await fixture.QueueAsync(peer, PickupFixture.Payload(20));

        fixture.Clock.Advance(TimeSpan.FromDays(3650));

        fixture.FakeStore!.Enqueued.Should().ContainSingle().Which.Message.ExpiresAt.Should().BeNull();
        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(0);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Which.Id.Should().Be(id);
    }

    [Theory]
    [InlineData(10, 30 * 86400, 10)]               // the forward's own expiry comes first
    [InlineData(40 * 86400, 30 * 86400, 30 * 86400)] // the age limit comes first
    public async Task The_stored_expiry_is_the_earlier_of_the_forward_expiry_and_the_age_limit(long expiresInSeconds, long ageSeconds, long expectedSeconds)
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.MaxMessageAge = TimeSpan.FromSeconds(ageSeconds), fakeStore: true);
        var (_, peer) = await fixture.EnrolledAsync();
        var now = fixture.Clock.Now;
        var skew = (long)new DidCommOptions().ExpiresClockSkew.TotalSeconds;

        await fixture.QueueAsync(peer, PickupFixture.Payload(20), new PickupDeliveryOptions(ExpiresTime: now.ToUnixTimeSeconds() + expiresInSeconds));

        var expected = expectedSeconds == expiresInSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() + expiresInSeconds + skew + 1)   // FR-ROUTE-09 with ExpiresClockSkew
            : now.AddSeconds(expectedSeconds);
        fixture.FakeStore!.Enqueued.Should().ContainSingle().Which.Message.ExpiresAt.Should().Be(expected);
    }

    [Fact]
    public async Task Expired_at_admission_is_the_forward_expiry_alone()
    {
        // A forward expiring one second ago is refused; one with no expiry is accepted whatever the age limit.
        await using var fixture = PickupFixture.Create(pickup: o => o.MaxMessageAge = TimeSpan.FromTicks(1));
        var (_, peer) = await fixture.EnrolledAsync();
        var past = fixture.Clock.Now.ToUnixTimeSeconds() - (long)new DidCommOptions().ExpiresClockSkew.TotalSeconds - 2;

        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20), new PickupDeliveryOptions(ExpiresTime: past))).Outcome.Should().Be(PickupDeliveryOutcome.Expired);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20))).Outcome.Should().Be(PickupDeliveryOutcome.Queued, "a one-tick age limit starts counting at admission");
    }

    [Fact]
    public async Task Near_the_end_of_time_the_limit_saturates_instead_of_overflowing()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        fixture.Clock.Now = DateTimeOffset.MaxValue.AddSeconds(-1);

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20));

        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        fixture.FakeStore!.Enqueued.Should().ContainSingle().Which.Message.ExpiresAt.Should().Be(DateTimeOffset.MaxValue);
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator)).MessageCount.Should().Be(1, "not aged one second before the end of time");
    }

    [Fact]
    public async Task The_limit_is_read_once_when_the_queue_is_built()
    {
        // L-060: a host that changes the options object afterwards changes nothing.
        await using var fixture = PickupFixture.Create();
        var (_, peer) = await fixture.EnrolledAsync();
        var options = new MessagePickupOptions { MaxMessageAge = TimeSpan.FromHours(1) };
        var store = new InMemoryMessageStore(options);
        var clock = fixture.Clock;
        var queue = new MessagePickupQueue(store, fixture.Host.Store, fixture.Sessions, Options.Create(options), Options.Create(new DidCommOptions { Clock = () => clock.Now }));

        options.MaxMessageAge = null;
        var held = await queue.DeliverAsync(peer, PickupFixture.Payload(20));
        held.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        options.MaxMessageAge = TimeSpan.FromDays(3650);

        clock.Advance(TimeSpan.FromHours(1));
        (await queue.PurgeExpiredAsync()).Should().Be(1, "the one-hour limit read at construction is the one on the record");
    }
}
