using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The two housekeeping calls of <see cref="MessagePickupQueue"/>: <c>RemoveAsync</c>, for a
/// pipeline that delivered a retained message another way (FR-PICK-09), and
/// <c>PurgeExpiredAsync</c>, which gives the memory of expired messages back early (FR-ROUTE-09).
/// </summary>
public sealed class MessagePickupLiveRemovePurgeTests
{
    // ---- RemoveAsync ----------------------------------------------------------------------

    [Fact]
    public async Task Remove_takes_a_waiting_message_out_once_and_gives_its_room_back()
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.MaxQueueMessages = 1);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(40));
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(40))).Outcome.Should().Be(PickupDeliveryOutcome.QueueFull);

        (await fixture.Queue.RemoveAsync(result.GrantPeerDid!, result.MessageId!)).Should().BeTrue();

        fixture.Memory.MessageCount.Should().Be(0);
        fixture.Memory.TotalBytes.Should().Be(0);
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator)).MessageCount.Should().Be(0);
        (await fixture.Queue.RemoveAsync(result.GrantPeerDid!, result.MessageId!)).Should().BeFalse("it is no longer held");
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(40))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
    }

    public static TheoryData<string> HeldStates => new() { "pushed live", "delivered by request", "not yet due" };

    [Theory]
    [MemberData(nameof(HeldStates))]
    public async Task Remove_takes_a_message_out_whatever_its_state_and_a_later_acknowledgement_is_a_harmless_miss(string state)
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        string id;
        switch (state)
        {
            case "pushed live":
                await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
                var pushed = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(40));
                pushed.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
                id = pushed.MessageId!;
                break;
            case "delivered by request":
                id = await fixture.QueueAsync(peer, PickupFixture.Payload(40));
                (await fixture.FetchAsync(peer, mediator, socket, 10))!.Value.Messages.Should().ContainSingle();
                break;
            default:
                id = await fixture.QueueAsync(peer, PickupFixture.Payload(40), new PickupDeliveryOptions(NotBefore: fixture.Clock.Now + TimeSpan.FromHours(1)));
                break;
        }
        var other = await fixture.QueueAsync(peer, PickupFixture.Payload(41), new PickupDeliveryOptions(AllowLive: false));

        (await fixture.Queue.RemoveAsync(peer, id)).Should().BeTrue();

        fixture.Memory.MessageCount.Should().Be(1);
        fixture.Memory.TotalBytes.Should().Be(41);
        // The recipient acknowledges what it was sent before the removal: nothing to remove, still a status.
        var status = await fixture.StatusOfAsync(MessagePickup.CreateMessagesReceived(peer, mediator, new[] { id }), mediator, socket);
        status.MessageCount.Should().Be(1);
        fixture.Memory.MessageCount.Should().Be(1, "the acknowledgement of a removed message removes nothing else");
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Which.Id.Should().Be(other);

        // Closing the session that held the lease finds nothing of it.
        var released = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.FakeStore!.OnReleaseLeases = async (owner, ct) =>
        {
            var count = await fixture.FakeStore.Inner.ReleaseLeasesAsync(owner, ct);
            released.TrySetResult(count);
            return count;
        };
        socket.Close();
        if (state != "not yet due")
            (await released.Task.WaitAsync(MessagePickupLive.Deadline)).Should().Be(0);
        fixture.Memory.MessageCount.Should().Be(1);
    }

    public static TheoryData<string> Misses => new()
    {
        "an id nobody holds",
        "an id held under another grant",
        "the right id under the wrong grant",
        "the right id under a DID URL of its grant",
        "an empty id",
        "an empty grant",
        "a very long id",
    };

    [Theory]
    [MemberData(nameof(Misses))]
    public async Task Remove_of_what_the_grant_does_not_hold_is_false_and_removes_nothing(string what)
    {
        await using var fixture = PickupFixture.Create();
        var mediator = (await fixture.NewPeerAsync()).Did;
        var alice = (await fixture.NewPeerAsync()).Did;
        var bob = (await fixture.NewPeerAsync()).Did;
        await fixture.EnrolAsync(alice, mediator, alice);
        await fixture.EnrolAsync(bob, mediator, bob);
        var alices = await fixture.QueueAsync(alice, PickupFixture.Payload(10, 1));
        var bobs = await fixture.QueueAsync(bob, PickupFixture.Payload(11, 2));
        var (grant, id) = what switch
        {
            "an id nobody holds" => (alice, "no-such-id"),
            "an id held under another grant" => (alice, bobs),
            "the right id under the wrong grant" => (bob, alices),
            "the right id under a DID URL of its grant" => (alice + "#key-1", alices),
            "an empty id" => (alice, string.Empty),
            "an empty grant" => (string.Empty, alices),
            _ => (alice, new string('x', 100_000)),
        };

        (await fixture.Queue.RemoveAsync(grant, id)).Should().BeFalse(what);

        fixture.Memory.MessageCount.Should().Be(2);
        (await MessagePickupLive.FetchNowAsync(fixture, alice, mediator)).Should().ContainSingle().Which.Id.Should().Be(alices);
        (await MessagePickupLive.FetchNowAsync(fixture, bob, mediator)).Should().ContainSingle().Which.Id.Should().Be(bobs);
        MessagePickupLive.ShouldNotLeak(fixture.PickupLogs, alice, bob, alices, bobs);
    }

    public static TheoryData<string> StoreFaults => new() { "throws", "answers a faulted task", "throws its own OperationCanceledException" };

    [Theory]
    [MemberData(nameof(StoreFaults))]
    public async Task Remove_answers_false_when_the_store_fails_and_does_not_throw(string how)
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var id = await fixture.QueueAsync(peer, PickupFixture.Payload(10));
        fixture.FakeStore!.OnAcknowledge = (_, _, _) => how switch
        {
            "throws" => throw new HandlerBoomException(HandlerBoomException.Marker),
            "answers a faulted task" => new ValueTask<int>(Task.FromException<int>(new HandlerBoomException(HandlerBoomException.Marker))),
            _ => throw new OperationCanceledException(HandlerBoomException.Marker),
        };

        var removed = await fixture.Queue.RemoveAsync(peer, id);

        removed.Should().BeFalse();
        fixture.Memory.MessageCount.Should().Be(1, "what could not be removed is still held");
        var line = MessagePickupLive.QueueLogs(fixture, LogLevel.Error).Should().ContainSingle().Subject;
        line.State["ExceptionType"].Should().Be(how == "throws its own OperationCanceledException" ? nameof(OperationCanceledException) : nameof(HandlerBoomException));
        line.Exception.Should().BeNull();
        MessagePickupLive.ShouldNotLeak(fixture.PickupLogs, peer, mediator, id);

        // The store recovers: the same call works.
        fixture.FakeStore.OnAcknowledge = null;
        (await fixture.Queue.RemoveAsync(peer, id)).Should().BeTrue();
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Remove_reports_whether_the_store_removed_anything(int storeAnswer, bool expected)
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (_, peer) = await fixture.EnrolledAsync();
        fixture.FakeStore!.OnAcknowledge = (_, _, _) => new ValueTask<int>(storeAnswer);

        (await fixture.Queue.RemoveAsync(peer, "some-id")).Should().Be(expected);

        var call = fixture.FakeStore.Acknowledged.Should().ContainSingle().Subject;
        call.GrantPeerDid.Should().Be(peer);
        call.Ids.Should().Equal("some-id");
    }

    [Fact]
    public async Task Remove_refuses_null_arguments()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var queue = fixture.Queue;

        (await FluentActions.Awaiting(async () => await queue.RemoveAsync(null!, "id")).Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("grantPeerDid");
        (await FluentActions.Awaiting(async () => await queue.RemoveAsync("did:example:grant", null!)).Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("messageId");
        fixture.FakeStore!.Calls.Should().BeEmpty();
    }

    // ---- PurgeExpiredAsync ----------------------------------------------------------------

    [Fact]
    public async Task Purge_removes_every_expired_message_of_every_grant_in_any_state_and_nothing_else()
    {
        await using var fixture = PickupFixture.Create();
        var mediator = (await fixture.NewPeerAsync()).Did;
        var alice = (await fixture.NewPeerAsync()).Did;
        var bob = (await fixture.NewPeerAsync()).Did;
        await fixture.EnrolAsync(alice, mediator, alice);
        await fixture.EnrolAsync(bob, mediator, bob);
        var soon = new PickupDeliveryOptions(ExpiresTime: (fixture.Clock.Now + TimeSpan.FromSeconds(10)).ToUnixTimeSeconds());
        var later = new PickupDeliveryOptions(ExpiresTime: (fixture.Clock.Now + TimeSpan.FromSeconds(100)).ToUnixTimeSeconds());
        var socket = fixture.Session(alice, mediator, live: true);

        var requested = await fixture.QueueAsync(alice, PickupFixture.Payload(10, 1), soon);        // delivered by request, unacknowledged
        (await fixture.FetchAsync(alice, mediator, socket, 10))!.Value.Messages.Should().ContainSingle();
        await fixture.QueueAsync(alice, PickupFixture.Payload(10, 2), soon);                       // waiting
        await MessagePickupLive.SetLiveAsync(fixture, alice, mediator, socket);
        (await fixture.Queue.DeliverAsync(alice, PickupFixture.Payload(10, 3), soon)).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive); // pushed live, unacknowledged
        await fixture.QueueAsync(bob, PickupFixture.Payload(10, 4), soon);                         // another grant
        var keptLater = await fixture.QueueAsync(bob, PickupFixture.Payload(10, 5), later);
        var keptForever = await fixture.QueueAsync(bob, PickupFixture.Payload(10, 6));
        fixture.Memory.MessageCount.Should().Be(6);

        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(0, "nothing has expired yet");
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(0, "the expiry second itself is still good");

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(4);

        fixture.Memory.MessageCount.Should().Be(2);
        fixture.Memory.TotalBytes.Should().Be(20);
        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(0, "a second sweep finds nothing");
        (await MessagePickupLive.AckAsync(fixture, alice, mediator, requested)).MessageCount.Should().Be(0, "acknowledging a purged message is a harmless miss");
        (await MessagePickupLive.FetchNowAsync(fixture, bob, mediator)).Select(m => m.Id).Should().Equal(keptLater, keptForever);

        fixture.Clock.Advance(TimeSpan.FromSeconds(90));
        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(1, "expiry does not wait for a lease");
        fixture.Memory.MessageCount.Should().Be(1);
    }

    [Theory]
    [InlineData(2040)]
    [InlineData(2015)]
    public async Task Purge_judges_expiry_by_the_configured_clock_not_the_system_clock(int year)
    {
        var store = new MessagePickupLivePurgeStore();
        await using var fixture = PickupFixture.Create(services: s => s.AddSingleton<IMessageStore>(store));
        var (_, peer) = await fixture.EnrolledAsync();
        // Far from today in either direction, so the two clocks can never agree by accident.
        fixture.Clock.Now = new DateTimeOffset(year, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var expires = fixture.Clock.Now + TimeSpan.FromDays(1);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(10), new PickupDeliveryOptions(ExpiresTime: expires.ToUnixTimeSeconds()))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);

        // By the configured clock it has a day left — in 2015 the system clock would call it long expired.
        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(0);
        store.Inner.MessageCount.Should().Be(1);

        // A day and a second later by the configured clock — in 2040 the system clock would call it years early.
        fixture.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(1);
        store.Inner.MessageCount.Should().Be(0);

        store.PurgedAt.Should().Equal(expires - TimeSpan.FromDays(1), expires + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Purge_applies_the_expiry_skew_the_message_was_accepted_with()
    {
        await using var fixture = PickupFixture.Create(didComm: o => o.ExpiresClockSkew = TimeSpan.FromSeconds(30));
        var (_, peer) = await fixture.EnrolledAsync();
        var expires = fixture.Clock.Now + TimeSpan.FromSeconds(10);
        await fixture.QueueAsync(peer, PickupFixture.Payload(10), new PickupDeliveryOptions(ExpiresTime: expires.ToUnixTimeSeconds()));

        fixture.Clock.Now = expires + TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(999);
        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(0, "now − skew is still the expiry second");
        fixture.Clock.Now = expires + TimeSpan.FromSeconds(31);
        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Purge_lets_a_store_fault_reach_the_caller()
    {
        var store = new MessagePickupLivePurgeStore { OnPurgeExpired = (_, _) => throw new HandlerBoomException("the purge failed") };
        await using var fixture = PickupFixture.Create(services: s => s.AddSingleton<IMessageStore>(store));

        // Unlike a delivery, a sweep is the host's own call: it is told why it failed.
        await FluentActions.Awaiting(async () => await fixture.Queue.PurgeExpiredAsync()).Should().ThrowAsync<HandlerBoomException>();

        store.OnPurgeExpired = null;
        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(0, "an empty store has nothing to purge");
    }
}
