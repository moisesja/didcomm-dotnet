using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Xunit;
using static DidComm.Mediator.Tests.Pickup.MessagePickupStoreSupport.MessagePickupStoreKit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// A lease is given back only by its taker (the <see cref="IMessageStore"/> contract, FR-PICK-04):
/// <see cref="IMessageStore.ReleaseAsync"/> names the lease it ends, and a record that has been
/// taken again since — under another lease — is left alone. Found by the QA pass: an unconditional
/// release let a late clean-up make a message that had just been delivered deliverable again.
/// </summary>
public sealed class MessagePickupStoreLeaseIdentityTests
{
    private static MessageLeaseRequest Request(MessageLease lease, DateTimeOffset? now = null, int max = int.MaxValue)
        => new(Alice, null, max, long.MaxValue, now ?? T0, lease);

    [Fact]
    public void Every_lease_has_its_own_id_unless_one_is_given()
    {
        var first = new MessageLease("socket", Far);
        var second = new MessageLease("socket", Far);

        first.Id.Should().NotBeNullOrEmpty().And.NotBe(second.Id);
        first.Should().NotBe(second, "two leases of the same owner and expiry are still two leases");
        (first with { ExpiresAt = Far.AddDays(1) }).Id.Should().Be(first.Id, "a copy is the same lease");
        new MessageLease(null, Far) { Id = "chosen" }.Id.Should().Be("chosen");
    }

    [Fact]
    public async Task A_release_ends_the_lease_it_names_and_no_other()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b", "c");
        var mine = new MessageLease(null, Far);
        (await store.LeaseIdsAsync(Request(mine, max: 2))).Should().Equal("a", "b");
        var theirs = new MessageLease(null, Far);
        (await store.LeaseIdsAsync(Request(theirs))).Should().Equal("c");

        // Naming a lease that holds none of these records changes nothing.
        (await store.ReleaseAsync(Alice, new[] { "a", "b", "c" }, "no-such-lease", None)).Should().Be(0);
        (await store.LeaseIdsAsync(Request(new MessageLease(null, Far)))).Should().BeEmpty();

        // Naming mine gives back mine only, even when the list also names a record of theirs.
        (await store.ReleaseAsync(Alice, new[] { "a", "b", "c" }, mine.Id, None)).Should().Be(2);
        (await store.LeaseIdsAsync(Request(new MessageLease(null, Far)))).Should().Equal("a", "b");
        (await store.ReleaseAsync(Alice, new[] { "c" }, theirs.Id, None)).Should().Be(1);
    }

    [Fact]
    public async Task A_late_release_cannot_end_the_lease_someone_else_took_after_the_first_ran_out()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "m");
        var first = new MessageLease(null, T0.AddSeconds(60));
        (await store.LeaseIdsAsync(Request(first))).Should().Equal("m");

        // The first lease runs out; a second request takes and delivers the message.
        var later = T0.AddSeconds(60);
        var second = new MessageLease(null, later.AddSeconds(60));
        (await store.LeaseIdsAsync(Request(second, later))).Should().Equal("m");

        // The first taker's clean-up arrives now. It must not make the message deliverable again.
        (await store.ReleaseAsync(Alice, new[] { "m" }, first.Id, None)).Should().Be(0);
        (await store.LeaseIdsAsync(Request(new MessageLease(null, Far), later.AddSeconds(1)))).Should().BeEmpty("the second lease is still running");

        (await store.ReleaseAsync(Alice, new[] { "m" }, second.Id, None)).Should().Be(1);
        (await store.LeaseIdsAsync(Request(new MessageLease(null, Far), later.AddSeconds(1)))).Should().Equal("m");
    }

    [Fact]
    public async Task A_late_release_cannot_end_a_lease_taken_after_the_owner_of_the_first_closed()
    {
        var store = NewStore();
        var pushed = new MessageLease("socket-a", Far);
        await store.AddAsync(Message("m"), pushed);

        // Socket A closes: everything it held is free again, and another connection takes the message.
        (await store.ReleaseLeasesAsync("socket-a", None)).Should().Be(1);
        var other = new MessageLease("socket-b", Far);
        (await store.LeaseIdsAsync(Request(other))).Should().Equal("m");

        // The push on socket A was refused and its clean-up runs only now.
        (await store.ReleaseAsync(Alice, new[] { "m" }, pushed.Id, None)).Should().Be(0);
        (await store.LeaseIdsAsync(Request(new MessageLease(null, Far)))).Should().BeEmpty();

        // Closing socket A again (a duplicate signal) does not touch socket B's lease either.
        (await store.ReleaseLeasesAsync("socket-a", None)).Should().Be(0);
        (await store.ReleaseLeasesAsync("socket-b", None)).Should().Be(1);
        (await store.LeaseIdsAsync(Request(new MessageLease(null, Far)))).Should().Equal("m");
    }

    [Fact]
    public async Task Releasing_by_owner_ends_every_lease_of_that_owner_whatever_their_ids()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b");
        (await store.LeaseIdsAsync(Request(new MessageLease("socket", Far), max: 1))).Should().Equal("a");
        (await store.LeaseIdsAsync(Request(new MessageLease("socket", Far), max: 1))).Should().Equal("b");

        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(2);
        (await store.LeaseIdsAsync(Request(new MessageLease(null, Far)))).Should().Equal("a", "b");
    }

    [Fact]
    public async Task The_lease_id_is_required_and_checked_before_anything_changes()
    {
        var store = NewStore();
        var lease = new MessageLease(null, Far);
        await store.AddAsync(Message("m"), lease);

        var act = async () => await store.ReleaseAsync(Alice, new[] { "m" }, null!, None);

        (await act.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("leaseId");
        (await store.LeaseIdsAsync(Request(new MessageLease(null, Far)))).Should().BeEmpty("the lease is still in place");
    }

    [Fact]
    public async Task An_acknowledgement_needs_no_lease_id_and_removes_under_any_lease()
    {
        var store = NewStore();
        await store.AddAsync(Message("m"), new MessageLease("socket", Far));

        (await store.AcknowledgeAsync(Alice, new[] { "m" }, None)).Should().Be(1);
        store.MessageCount.Should().Be(0);
    }
}
