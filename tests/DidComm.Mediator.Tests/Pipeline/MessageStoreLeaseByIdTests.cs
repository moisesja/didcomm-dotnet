using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// <see cref="InMemoryMessageStore.LeaseByIdAsync"/> (Q-1): the select-and-mark of one record named
/// by id, under exactly the rules of <c>LeaseAsync</c> — held by that grant, not expired, due, not
/// under a live lease, not passed over — else <c>null</c> and nothing changed.
/// </summary>
public sealed class MessageStoreLeaseByIdTests
{
    private const string Grant = "did:example:lease-by-id-grant";
    private const string Recipient = "did:example:lease-by-id-recipient";
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static async Task<InMemoryMessageStore> StoreWithAsync(params QueuedMessage[] messages)
    {
        var store = new InMemoryMessageStore();
        foreach (var message in messages)
            (await store.EnqueueAsync(message, null, T0, CancellationToken.None)).Should().Be(EnqueueOutcome.Enqueued);
        return store;
    }

    private static QueuedMessage Record(string id, DateTimeOffset? notBefore = null, DateTimeOffset? expiresAt = null, string grant = Grant)
        => new(id, grant, Recipient, new byte[] { 1, 2, 3 }, T0, notBefore, expiresAt);

    private static MessageLease Lease(TimeSpan length) => new(null, T0 + length);

    private static Task<IReadOnlyList<QueuedMessage>> PickupAsync(InMemoryMessageStore store, DateTimeOffset now)
        => store.LeaseAsync(new MessageLeaseRequest(Grant, null, 10, int.MaxValue, now, new MessageLease("pickup", now + TimeSpan.FromMinutes(1))), CancellationToken.None).AsTask();

    [Fact]
    public async Task A_deliverable_record_is_taken_and_marked_so_a_pickup_does_not_get_it()
    {
        var store = await StoreWithAsync(Record("a"), Record("b"));

        var taken = await store.LeaseByIdAsync(Grant, "b", Lease(TimeSpan.FromMinutes(1)), T0, CancellationToken.None);

        taken.Should().NotBeNull();
        taken!.Id.Should().Be("b");
        taken.Payload.ToArray().Should().Equal(1, 2, 3);
        (await PickupAsync(store, T0)).Select(m => m.Id).Should().Equal("a");
    }

    [Fact]
    public async Task Taking_the_same_record_twice_gives_it_to_the_first_taker_only()
    {
        var store = await StoreWithAsync(Record("a"));

        (await store.LeaseByIdAsync(Grant, "a", Lease(TimeSpan.FromMinutes(1)), T0, CancellationToken.None)).Should().NotBeNull();
        (await store.LeaseByIdAsync(Grant, "a", Lease(TimeSpan.FromMinutes(1)), T0, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_takes_of_one_record_never_both_succeed()
    {
        for (var round = 0; round < 200; round++)
        {
            var store = await StoreWithAsync(Record("a"));
            var takes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.LeaseByIdAsync(Grant, "a", Lease(TimeSpan.FromMinutes(1)), T0, CancellationToken.None).AsTask())));

            takes.Count(t => t is not null).Should().Be(1);
        }
    }

    [Fact]
    public async Task An_unknown_id_or_one_held_under_another_grant_is_a_miss()
    {
        var store = await StoreWithAsync(Record("a"), Record("other", grant: "did:example:other-grant"));

        (await store.LeaseByIdAsync(Grant, "missing", Lease(TimeSpan.FromMinutes(1)), T0, CancellationToken.None)).Should().BeNull();
        (await store.LeaseByIdAsync(Grant, "other", Lease(TimeSpan.FromMinutes(1)), T0, CancellationToken.None)).Should().BeNull();
        (await store.LeaseByIdAsync("did:example:nobody", "a", Lease(TimeSpan.FromMinutes(1)), T0, CancellationToken.None)).Should().BeNull();
        store.MessageCount.Should().Be(2, "a miss changes nothing");
    }

    [Fact]
    public async Task An_expired_record_is_not_taken_and_may_be_removed()
    {
        var store = await StoreWithAsync(Record("a", expiresAt: T0 + TimeSpan.FromSeconds(10)));

        (await store.LeaseByIdAsync(Grant, "a", Lease(TimeSpan.FromMinutes(1)), T0 + TimeSpan.FromSeconds(9), CancellationToken.None)).Should().NotBeNull("one tick before its expiry it is still deliverable");

        var expired = await StoreWithAsync(Record("b", expiresAt: T0 + TimeSpan.FromSeconds(10)));
        (await expired.LeaseByIdAsync(Grant, "b", Lease(TimeSpan.FromMinutes(1)), T0 + TimeSpan.FromSeconds(10), CancellationToken.None)).Should().BeNull("now >= ExpiresAt is expired");
        expired.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_record_not_yet_due_is_not_taken_until_it_is()
    {
        var store = await StoreWithAsync(Record("a", notBefore: T0 + TimeSpan.FromSeconds(5)));

        (await store.LeaseByIdAsync(Grant, "a", Lease(TimeSpan.FromMinutes(1)), T0 + TimeSpan.FromSeconds(4), CancellationToken.None)).Should().BeNull();
        (await store.LeaseByIdAsync(Grant, "a", Lease(TimeSpan.FromMinutes(1)), T0 + TimeSpan.FromSeconds(5), CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_record_under_a_live_lease_is_not_taken_and_one_whose_lease_ran_out_is()
    {
        var store = await StoreWithAsync(Record("a"));
        (await PickupAsync(store, T0)).Should().ContainSingle();

        (await store.LeaseByIdAsync(Grant, "a", Lease(TimeSpan.FromMinutes(2)), T0 + TimeSpan.FromSeconds(59), CancellationToken.None)).Should().BeNull("the pickup's lease still holds");
        (await store.LeaseByIdAsync(Grant, "a", Lease(TimeSpan.FromMinutes(2)), T0 + TimeSpan.FromMinutes(1), CancellationToken.None)).Should().NotBeNull("the pickup's lease ended");
    }

    [Fact]
    public async Task A_passed_over_record_is_not_taken_before_its_instant()
    {
        var store = await StoreWithAsync(Record("a"));
        var pickup = new MessageLease("pickup", T0 + TimeSpan.FromMinutes(1));
        await store.LeaseAsync(new MessageLeaseRequest(Grant, null, 10, int.MaxValue, T0, pickup), CancellationToken.None);
        (await store.PassOverAsync(Grant, new[] { "a" }, pickup.Id, T0 + TimeSpan.FromMinutes(5), CancellationToken.None)).Should().Be(1);

        (await store.LeaseByIdAsync(Grant, "a", Lease(TimeSpan.FromMinutes(10)), T0 + TimeSpan.FromMinutes(4), CancellationToken.None)).Should().BeNull();
        (await store.LeaseByIdAsync(Grant, "a", Lease(TimeSpan.FromMinutes(10)), T0 + TimeSpan.FromMinutes(5), CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task The_lease_is_given_back_by_its_id_only_and_then_a_pickup_gets_the_record()
    {
        var store = await StoreWithAsync(Record("a"));
        var push = Lease(TimeSpan.FromMinutes(1));
        (await store.LeaseByIdAsync(Grant, "a", push, T0, CancellationToken.None)).Should().NotBeNull();

        (await store.ReleaseAsync(Grant, new[] { "a" }, "another-lease", CancellationToken.None)).Should().Be(0);
        (await PickupAsync(store, T0)).Should().BeEmpty();
        (await store.ReleaseAsync(Grant, new[] { "a" }, push.Id, CancellationToken.None)).Should().Be(1);
        (await PickupAsync(store, T0)).Should().ContainSingle();
    }

    [Fact]
    public async Task Null_arguments_and_a_cancelled_token_change_nothing()
    {
        var store = await StoreWithAsync(Record("a"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await store.Awaiting(s => s.LeaseByIdAsync(null!, "a", Lease(TimeSpan.FromMinutes(1)), T0, CancellationToken.None).AsTask()).Should().ThrowAsync<ArgumentNullException>();
        await store.Awaiting(s => s.LeaseByIdAsync(Grant, null!, Lease(TimeSpan.FromMinutes(1)), T0, CancellationToken.None).AsTask()).Should().ThrowAsync<ArgumentNullException>();
        await store.Awaiting(s => s.LeaseByIdAsync(Grant, "a", null!, T0, CancellationToken.None).AsTask()).Should().ThrowAsync<ArgumentNullException>();
        await store.Awaiting(s => s.LeaseByIdAsync(Grant, "a", Lease(TimeSpan.FromMinutes(1)), T0, cts.Token).AsTask()).Should().ThrowAsync<OperationCanceledException>();

        (await PickupAsync(store, T0)).Should().ContainSingle("nothing was marked");
    }
}
