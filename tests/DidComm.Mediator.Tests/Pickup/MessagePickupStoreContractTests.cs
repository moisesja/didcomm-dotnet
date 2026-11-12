using System.Collections;
using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Xunit;
using static DidComm.Mediator.Tests.Pickup.MessagePickupStoreSupport.MessagePickupStoreKit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The argument and cancellation half of the <see cref="IMessageStore"/> contract, as
/// <see cref="InMemoryMessageStore"/> keeps it: a missing argument and a cancelled token are
/// refused before anything changes, and a method that changes several records either throws before
/// the first change or returns the truth ("no throw after a partial change").
/// </summary>
public sealed class MessagePickupStoreContractTests
{
    private const string Owner = "socket";

    /// <summary>Three records of Alice — "a" and "b" leased to <see cref="Owner"/>, "c" waiting — and one of Bob.</summary>
    private static async Task<InMemoryMessageStore> SeededAsync()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b", "c");
        await store.AddAsync(Bob, "x");
        (await store.LeaseIdsAsync(Take(max: 2, owner: Owner))).Should().Equal("a", "b");
        return store;
    }

    /// <summary>The seeded store is exactly as <see cref="SeededAsync"/> left it. Ends the leases to prove they were there, so it is the last step of a test.</summary>
    private static async Task RequireUntouchedAsync(InMemoryMessageStore store)
    {
        (store.MessageCount, store.TotalBytes).Should().Be((4, 40L));
        (await store.GetStatusAsync(Alice, null, T0, None)).Should().Be(new QueueStatus(3, 30, T0, T0));
        (await store.GetStatusAsync(Bob, null, T0, None)).Should().Be(new QueueStatus(1, 10, T0, T0));
        // A lease that is over as it is taken shows what is deliverable without holding it.
        (await store.LeaseIdsAsync(Take(Alice, until: T0))).Should().Equal(new[] { "c" }, "\"a\" and \"b\" are still leased, \"c\" is still waiting");
        (await store.LeaseIdsAsync(Take(Bob, until: T0))).Should().Equal("x");
        (await store.ReleaseLeasesAsync(Owner, None)).Should().Be(2, "the owner still holds its two leases");
        (await store.LeaseIdsAsync(Take(Alice, until: T0))).Should().Equal("a", "b", "c");
    }

    // ---- missing arguments ----------------------------------------------------------------

    public static TheoryData<string> NullArguments => new()
    {
        "enqueue: message",
        "enqueue: message.Id",
        "enqueue: message.GrantPeerDid",
        "enqueue: message.RecipientDid",
        "lease: request",
        "lease: request.GrantPeerDid",
        "lease: request.Lease",
        "acknowledge: grantPeerDid",
        "acknowledge: messageIds",
        "release: grantPeerDid",
        "release: messageIds",
        "release-leases: leaseOwner",
        "status: grantPeerDid",
        "purge-recipient: grantPeerDid",
        "purge-recipient: recipientDid",
        "purge-grant: grantPeerDid",
    };

    [Theory]
    [MemberData(nameof(NullArguments))]
    public async Task A_missing_argument_is_an_ArgumentNullException_and_changes_nothing(string which)
    {
        var store = await SeededAsync();
        var ids = new[] { "a", "c" };

        Func<Task> act = which switch
        {
            "enqueue: message" => async () => await store.EnqueueAsync(null!, null, T0, None),
            "enqueue: message.Id" => async () => await store.EnqueueAsync(Message("m") with { Id = null! }, null, T0, None),
            "enqueue: message.GrantPeerDid" => async () => await store.EnqueueAsync(Message("m") with { GrantPeerDid = null! }, null, T0, None),
            "enqueue: message.RecipientDid" => async () => await store.EnqueueAsync(Message("m") with { RecipientDid = null! }, null, T0, None),
            "lease: request" => async () => await store.LeaseAsync(null!, None),
            "lease: request.GrantPeerDid" => async () => await store.LeaseAsync(Take() with { GrantPeerDid = null! }, None),
            "lease: request.Lease" => async () => await store.LeaseAsync(Take() with { Lease = null! }, None),
            "acknowledge: grantPeerDid" => async () => await store.AcknowledgeAsync(null!, ids, None),
            "acknowledge: messageIds" => async () => await store.AcknowledgeAsync(Alice, null!, None),
            "release: grantPeerDid" => async () => await store.ReleaseAsync(null!, ids, None),
            "release: messageIds" => async () => await store.ReleaseAsync(Alice, null!, None),
            "release-leases: leaseOwner" => async () => await store.ReleaseLeasesAsync(null!, None),
            "status: grantPeerDid" => async () => await store.GetStatusAsync(null!, null, T0, None),
            "purge-recipient: grantPeerDid" => async () => await store.PurgeRecipientAsync(null!, Alice, None),
            "purge-recipient: recipientDid" => async () => await store.PurgeRecipientAsync(Alice, null!, None),
            "purge-grant: grantPeerDid" => async () => await store.PurgeGrantAsync(null!, None),
            _ => throw new ArgumentOutOfRangeException(nameof(which)),
        };

        await act.Should().ThrowAsync<ArgumentNullException>();
        await RequireUntouchedAsync(store);
    }

    [Fact]
    public async Task The_optional_arguments_may_be_absent()
    {
        var store = await SeededAsync();

        // No lease on enqueue, no recipient filter on lease and status, no owner on a lease.
        (await store.EnqueueAsync(Message("d"), null, T0, None)).Should().Be(EnqueueOutcome.Enqueued);
        (await store.GetStatusAsync(Alice, null, T0, None)).MessageCount.Should().Be(4);
        (await store.LeaseIdsAsync(Take(recipient: null, owner: null))).Should().Equal("c", "d");
    }

    // ---- no throw after a partial change --------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_null_id_in_the_list_makes_Acknowledge_throw_before_anything_is_removed(int nullAt)
    {
        var store = await SeededAsync();
        var ids = new List<string> { "a", "c" };
        ids.Insert(nullAt, null!);

        var act = async () => await store.AcknowledgeAsync(Alice, ids, None);

        await act.Should().ThrowAsync<ArgumentNullException>();
        await RequireUntouchedAsync(store);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_null_id_in_the_list_makes_Release_throw_before_any_lease_is_ended(int nullAt)
    {
        var store = await SeededAsync();
        var ids = new List<string> { "a", "b" };
        ids.Insert(nullAt, null!);

        var act = async () => await store.ReleaseAsync(Alice, ids, None);

        await act.Should().ThrowAsync<ArgumentNullException>();
        await RequireUntouchedAsync(store);
    }

    [Theory]
    [InlineData("acknowledge")]
    [InlineData("release")]
    public async Task A_list_that_changes_while_it_is_read_cannot_make_the_store_throw_after_a_partial_change(string method)
    {
        // The caller's list is not the store's: another thread may still be writing to it. Here the
        // second entry reads as an id the first time it is looked at and as null from then on. The
        // contract leaves the store two ways out — throw with nothing changed, or return the truth.
        var store = await SeededAsync();
        var ids = new FlickeringIds("a", "b");

        Exception? thrown = null;
        var returned = -1;
        try
        {
            returned = method == "acknowledge"
                ? await store.AcknowledgeAsync(Alice, ids, None)
                : await store.ReleaseAsync(Alice, ids, None);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        if (thrown is not null)
        {
            // It threw, so nothing may have changed. Said in one line first; the full check follows.
            var deliverable = (await store.LeaseIdsAsync(Take(Alice, until: T0))).Length;
            (Held: store.MessageCount, Deliverable: deliverable).Should().Be(
                (4, 1), $"{method} threw {thrown.GetType().Name}, and a method that throws must not have removed a record or ended a lease before it did (records held, records of Alice not under a lease)");
            await RequireUntouchedAsync(store);
            return;
        }

        // It did not throw, so the number it returned is what happened.
        if (method == "acknowledge")
        {
            store.MessageCount.Should().Be(4 - returned);
            (await store.GetStatusAsync(Alice, null, T0, None)).MessageCount.Should().Be(3 - returned);
        }
        else
        {
            store.MessageCount.Should().Be(4);
            (await store.LeaseIdsAsync(Take(Alice, until: T0))).Should().HaveCount(1 + returned);
        }
    }

    // ---- cancellation ---------------------------------------------------------------------

    public static TheoryData<string> Methods => new()
    {
        "enqueue", "enqueue-with-lease", "lease", "acknowledge", "release", "release-leases", "status", "purge-recipient", "purge-grant", "purge-expired",
    };

    [Theory]
    [MemberData(nameof(Methods))]
    public async Task A_cancelled_token_is_an_OperationCanceledException_and_changes_nothing(string method)
    {
        var store = await SeededAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var ct = cancelled.Token;

        Func<Task> act = method switch
        {
            "enqueue" => async () => await store.EnqueueAsync(Message("d"), null, T0, ct),
            "enqueue-with-lease" => async () => await store.EnqueueAsync(Message("d"), new MessageLease(Owner, Far), T0, ct),
            "lease" => async () => await store.LeaseAsync(Take(owner: Owner), ct),
            "acknowledge" => async () => await store.AcknowledgeAsync(Alice, new[] { "a", "c" }, ct),
            "release" => async () => await store.ReleaseAsync(Alice, new[] { "a", "b" }, ct),
            "release-leases" => async () => await store.ReleaseLeasesAsync(Owner, ct),
            "status" => async () => await store.GetStatusAsync(Alice, null, T0, ct),
            "purge-recipient" => async () => await store.PurgeRecipientAsync(Alice, Alice, ct),
            "purge-grant" => async () => await store.PurgeGrantAsync(Alice, ct),
            // Judged at an instant at which nothing has an expiry to pass; the call must not get that far.
            "purge-expired" => async () => await store.PurgeExpiredAsync(DateTimeOffset.MaxValue, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        await RequireUntouchedAsync(store);
    }

    [Fact]
    public async Task A_cancelled_token_does_not_apply_expiry_either()
    {
        var at = T0.AddMinutes(5);
        var store = NewStore();
        await store.AddAsync(Message("expiring", expiresAt: at));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await FluentActions.Awaiting(async () => await store.PurgeExpiredAsync(at, cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(async () => await store.GetStatusAsync(Alice, null, at, cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(async () => await store.LeaseAsync(Take(now: at), cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(async () => await store.EnqueueAsync(Message("new"), null, at, cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();

        store.MessageCount.Should().Be(1, "nothing was judged, so nothing was removed");
        (await store.PurgeExpiredAsync(at, None)).Should().Be(1);
    }

    // ---- what a record or a request prints -------------------------------------------------

    [Fact]
    public void A_printed_record_request_or_status_carries_no_did_no_id_and_no_payload()
    {
        const string grant = "did:example:grant-that-must-not-be-printed";
        const string recipient = "did:example:recipient-that-must-not-be-printed";
        const string id = "queue-id-that-must-not-be-printed";
        var payload = "payload-that-must-not-be-printed"u8.ToArray();
        var record = new QueuedMessage(id, grant, recipient, payload, T0, T0.AddSeconds(1), T0.AddHours(1));
        var request = new MessageLeaseRequest(grant, recipient, 3, 1_000, T0, new MessageLease("owner-token", Far));
        var status = new QueueStatus(2, 64, T0, T0.AddSeconds(5));

        // However it gets into a log line: ToString, interpolation, or a structured-logging argument.
        var printed = new[] { record.ToString(), $"{record}", string.Format("{0}", record), request.ToString(), $"{request}", status.ToString() };

        foreach (var text in printed)
        {
            text.Should().NotContain("must-not-be-printed").And.NotContain("did:").And.NotContain("owner-token");
            text.Should().NotContain(Convert.ToBase64String(payload)).And.NotContain(Convert.ToHexString(payload));
        }
        record.ToString().Should().Contain($"{payload.Length} bytes", "the length is all a log needs of a payload");
        request.ToString().Should().Contain("MaxMessages = 3").And.Contain("MaxPayloadBytes = 1000");
    }

    /// <summary>
    /// A list whose last entry reads as an id on its first read and as <c>null</c> on every later
    /// one: what a store sees when the caller's list is changed under it.
    /// </summary>
    private sealed class FlickeringIds(params string[] ids) : IReadOnlyList<string>
    {
        private int _readsOfLast;

        public int Count => ids.Length;

        public string this[int index]
            => index == ids.Length - 1 && Interlocked.Increment(ref _readsOfLast) > 1 ? null! : ids[index];

        public IEnumerator<string> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
                yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
