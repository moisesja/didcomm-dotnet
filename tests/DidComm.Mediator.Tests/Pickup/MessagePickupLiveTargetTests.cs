using System.Text.Json;
using AwesomeAssertions;
using DidComm.Json;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// Where a live push goes (JC-13, FR-PICK-08/09): one target per message — the newest registered
/// session of the grant's peer that turned live delivery on and still allows pushes — found through
/// the session registry, never trusted further than the session's own identity; what the pushed
/// <c>delivery</c> looks like; and what happens when the chosen session does not take it.
/// </summary>
public sealed class MessagePickupLiveTargetTests
{
    [Fact]
    public async Task With_several_live_sessions_of_one_peer_exactly_one_push_goes_to_the_newest_registered()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var oldest = fixture.Session(peer, mediator, live: true);
        var middle = fixture.Session(peer, mediator, live: true);
        // Turned on in an order that is not the registration order: what counts is which was registered last.
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, middle);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, oldest);

        var first = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, 1));

        first.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        PushedIds(middle).Should().Equal(first.MessageId);
        oldest.Attempts.Should().BeEmpty("a lease has one owner: pushing to every session would duplicate the message");

        // A newer connection that turns live delivery on becomes the target.
        var newest = fixture.Session(peer, mediator, live: true);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, 2))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        newest.Attempts.Should().BeEmpty("registered, but it has not turned live delivery on");
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, newest);
        var third = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, 3));

        PushedIds(newest).Should().Equal(third.MessageId);
        PushedIds(middle).Should().HaveCount(2);
        oldest.Attempts.Should().BeEmpty();
        fixture.Memory.MessageCount.Should().Be(3, "one record per message, whatever the number of sessions");
    }

    [Fact]
    public async Task With_live_delivery_on_only_for_the_older_session_the_push_goes_to_the_older()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var older = fixture.Session(peer, mediator, live: true);
        var newer = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, older);

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20));

        result.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        PushedIds(older).Should().Equal(result.MessageId);
        newer.Attempts.Should().BeEmpty("the flag is per session");
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, newer)).LiveDelivery.Should().BeFalse();
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, older)).LiveDelivery.Should().BeTrue();
    }

    public static TheoryData<string> SkippedNewest => new()
    {
        "downgraded to return_route: none",
        "closed, still registered",
        "closed by its signal alone, still accepting sends",
        "turned live delivery off",
    };

    [Theory]
    [MemberData(nameof(SkippedNewest))]
    public async Task A_newest_session_that_no_longer_takes_pushes_is_skipped_in_favour_of_the_older_live_one(string state)
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var older = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, older);

        Func<int> attemptsOnNewest;
        if (state == "closed by its signal alone, still accepting sends")
        {
            var loose = new MessagePickupLiveLooseSession(peer, mediator);
            fixture.Sessions.Register(loose).Registered.Should().BeTrue();
            (await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, loose)).LiveDelivery.Should().BeTrue();
            loose.FireClosed();
            attemptsOnNewest = () => loose.Attempts.Count;
        }
        else
        {
            var newest = fixture.Session(peer, mediator, live: true);
            (await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, newest)).LiveDelivery.Should().BeTrue();
            switch (state)
            {
                case "downgraded to return_route: none":
                    newest.Downgrade();
                    break;
                case "closed, still registered":
                    newest.Close();
                    break;
                default:
                    (await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, newest, on: false)).LiveDelivery.Should().BeFalse();
                    break;
            }
            attemptsOnNewest = () => newest.Attempts.Count;
        }

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20));

        result.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive, state);
        PushedIds(older).Should().Equal(result.MessageId);
        attemptsOnNewest().Should().Be(0, "the registry order is unchanged; the session is simply not a target");
    }

    public static TheoryData<string> Refusals => new() { "NotSent", "TooLarge", "throws" };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_push_the_chosen_session_does_not_take_is_not_retried_elsewhere_the_message_is_queued_and_free_at_once(string refusal)
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var older = fixture.Session(peer, mediator, live: true);
        var newest = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, older);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, newest);
        var tried = 0;
        newest.OnSend = (_, _) =>
        {
            tried++;
            return refusal switch
            {
                "NotSent" => ReturnRouteSendResult.NotSent,
                "TooLarge" => ReturnRouteSendResult.TooLarge,
                _ => throw new HandlerBoomException(HandlerBoomException.Marker),
            };
        };
        var payload = PickupFixture.Payload(64, 3);

        var result = await fixture.Queue.DeliverAsync(peer, payload);

        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued, "the message was admitted before the push: a push that is not taken loses nothing");
        result.MessageId.Should().NotBeNull();
        result.GrantPeerDid.Should().Be(peer);
        tried.Should().Be(1);
        newest.Sent.Should().BeEmpty();
        older.Attempts.Should().BeEmpty("one target per message (JC-13): the older session is not a fallback");
        fixture.Memory.MessageCount.Should().Be(1);

        // Deliverable immediately: the lease it was admitted under is released, not left to time out.
        var released = fixture.FakeStore!.Released.Should().ContainSingle().Subject;
        released.GrantPeerDid.Should().Be(peer);
        released.Ids.Should().Equal(result.MessageId);
        var held = (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Subject;
        held.Id.Should().Be(result.MessageId);
        held.Payload.ToArray().Should().Equal(payload);
        MessagePickupLive.ShouldNotLeak(fixture.PickupLogs, peer, mediator, result.MessageId);
        if (refusal == "throws")
            MessagePickupLive.QueueLogs(fixture, LogLevel.Error).Should().ContainSingle().Which.State.Should().Contain("ExceptionType", nameof(HandlerBoomException));
    }

    [Fact]
    public async Task A_release_that_fails_after_a_refused_push_still_reports_Queued_and_the_lease_ends_by_time()
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.LeaseTimeout = TimeSpan.FromSeconds(20), fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        socket.OnSend = (_, _) => ReturnRouteSendResult.NotSent;
        fixture.FakeStore!.OnRelease = (_, _, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker);

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20));

        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        fixture.Memory.MessageCount.Should().Be(1);
        MessagePickupLive.QueueLogs(fixture, LogLevel.Warning).Should().ContainSingle().Which.State.Should().Contain("ExceptionType", nameof(HandlerBoomException));
        MessagePickupLive.ShouldNotLeak(fixture.PickupLogs, peer, mediator, result.MessageId);

        fixture.FakeStore.OnRelease = null;
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().BeEmpty("the lease could not be released");
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Which.Id.Should().Be(result.MessageId);
    }

    // ---- the registry is not trusted further than the session's own identity ---------------

    [Fact]
    public async Task A_session_filed_under_the_peers_key_whose_own_peer_is_someone_else_never_receives_the_message()
    {
        var registry = new MessagePickupLiveRegistry();
        await using var fixture = PickupFixture.Create(services: s => s.AddSingleton<IReturnRouteSessionRegistry>(registry));
        var mediator = (await fixture.NewPeerAsync()).Did;
        var alice = (await fixture.NewPeerAsync()).Did;
        var bob = (await fixture.NewPeerAsync()).Did;
        await fixture.EnrolAsync(alice, mediator, alice);
        await fixture.EnrolAsync(bob, mediator, bob);
        var bobSocket = fixture.Session(bob, mediator, live: true);
        (await MessagePickupLive.SetLiveAsync(fixture, bob, mediator, bobSocket)).LiveDelivery.Should().BeTrue();
        // The registry answers every question with Bob's socket.
        registry.OnFind = _ => new IReturnRouteSession[] { bobSocket };

        var forAlice = await fixture.Queue.DeliverAsync(alice, PickupFixture.Payload(20, 1));

        forAlice.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.Queued, forAlice.MessageId, alice));
        registry.Finds.Should().Equal(alice);
        bobSocket.Attempts.Should().BeEmpty("a push is encrypted to the session's own peer, so that is what must match");

        // Bob's own messages do reach it, and Alice's is still hers alone.
        var forBob = await fixture.Queue.DeliverAsync(bob, PickupFixture.Payload(20, 2));
        forBob.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        PushedIds(bobSocket).Should().Equal(forBob.MessageId);
        (await MessagePickupLive.FetchNowAsync(fixture, alice, mediator)).Should().ContainSingle().Which.Id.Should().Be(forAlice.MessageId);
    }

    public static TheoryData<string> BrokenRegistries => new() { "throws", "answers null", "answers nothing", "answers only null entries" };

    [Theory]
    [MemberData(nameof(BrokenRegistries))]
    public async Task A_registry_that_cannot_name_a_session_means_the_message_is_queued(string how)
    {
        var registry = new MessagePickupLiveRegistry();
        await using var fixture = PickupFixture.Create(services: s => s.AddSingleton<IReturnRouteSessionRegistry>(registry));
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, 1))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive, "the control: with a working registry the socket is the target");
        registry.OnFind = how switch
        {
            "throws" => _ => throw new HandlerBoomException(HandlerBoomException.Marker),
            "answers null" => _ => null,
            "answers nothing" => _ => Array.Empty<IReturnRouteSession>(),
            _ => _ => new IReturnRouteSession[] { null!, null! },
        };

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, 2));

        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued, how);
        socket.Sent.Should().ContainSingle("only the control was pushed");
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle("queued, not leased").Which.Id.Should().Be(result.MessageId);
        if (how == "throws")
            MessagePickupLive.QueueLogs(fixture, LogLevel.Warning).Should().ContainSingle().Which.State.Should().Contain("ExceptionType", nameof(HandlerBoomException));
        MessagePickupLive.ShouldNotLeak(fixture.PickupLogs, peer, mediator);
    }

    [Fact]
    public async Task Null_entries_in_the_registrys_answer_are_stepped_over()
    {
        var registry = new MessagePickupLiveRegistry();
        await using var fixture = PickupFixture.Create(services: s => s.AddSingleton<IReturnRouteSessionRegistry>(registry));
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        registry.OnFind = _ => new IReturnRouteSession[] { null!, socket, null! };

        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        socket.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task A_live_session_of_another_peer_never_receives_the_message_and_pushes_follow_the_grant_not_the_recipient_did()
    {
        await using var fixture = PickupFixture.Create();
        var mediator = (await fixture.NewPeerAsync()).Did;
        var alice = (await fixture.NewPeerAsync()).Did;
        var bob = (await fixture.NewPeerAsync()).Did;
        // Alice registered two DIDs: her own and — as a recipient DID of HER grant — Bob's. Bob has no grant.
        await fixture.EnrolAsync(alice, mediator, alice, bob);
        var bobSocket = fixture.Session(bob, mediator, live: true);
        (await MessagePickupLive.SetLiveAsync(fixture, bob, mediator, bobSocket)).LiveDelivery.Should().BeTrue("turning live delivery on needs no grant (JC-16)");

        var forAlice = await fixture.Queue.DeliverAsync(alice, PickupFixture.Payload(20, 1));
        var forBobsDid = await fixture.Queue.DeliverAsync(bob, PickupFixture.Payload(20, 2));

        forAlice.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.Queued, forAlice.MessageId, alice));
        forBobsDid.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.Queued, forBobsDid.MessageId, alice), "the DID is registered under Alice's grant, so the message is hers");
        bobSocket.Attempts.Should().BeEmpty("Bob authenticated as Bob: nothing held under Alice's grant is pushed to him");
        (await MessagePickupLive.StatusAsync(fixture, bob, mediator, bobSocket)).MessageCount.Should().Be(0);

        // Alice connects and turns live delivery on: the next message for either DID is pushed to her.
        var aliceSocket = fixture.Session(alice, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, alice, mediator, aliceSocket);
        var next = await fixture.Queue.DeliverAsync(bob, PickupFixture.Payload(20, 3));
        next.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        PushedIds(aliceSocket).Should().Equal(next.MessageId);
        aliceSocket.Sent.Should().ContainSingle().Which.To.Should().Equal(alice);
        bobSocket.Attempts.Should().BeEmpty();
    }

    // ---- the pushed delivery --------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData(200 * 1024)]
    public async Task A_live_delivery_has_one_attachment_no_thid_an_empty_body_and_goes_through_the_size_bounded_send(int? maxDeliveryBytes)
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.MaxDeliveryBytes = maxDeliveryBytes);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true, pack: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var payload = PickupFixture.Payload(1000, 7);
        var bound = maxDeliveryBytes ?? fixture.Host.Options.MaxReceiveBytes;

        var result = await fixture.Queue.DeliverAsync(peer, payload);

        result.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        var attempt = socket.Attempts.Should().ContainSingle("the bounded overload, once").Subject;
        attempt.MaxPackedBytes.Should().Be(bound, "max_delivery_bytes bounds a live delivery as it bounds a requested one");
        attempt.Result.Should().Be(ReturnRouteSendResult.Sent);
        attempt.PackedBytes.Should().BeInRange(1000, bound);

        var push = attempt.Message;
        push.Type.Should().Be(MessagePickup.DeliveryType);
        push.Thid.Should().BeNull("there is no delivery-request to answer (FR-PICK-08)");
        push.Pthid.Should().BeNull();
        push.From.Should().Be(mediator, "the session's local DID");
        push.To.Should().Equal(peer);
        push.Body.Should().NotBeNull();
        push.Body!.Count.Should().Be(0, "no recipient_did: no request named one");
        push.Attachments.Should().ContainSingle();
        push.Id.Should().NotBeNullOrEmpty().And.NotBe(result.MessageId, "the delivery's own id is not the id of what it carries");

        MessagePickup.TryParseDelivery(push, out var messages, out var recipientDid).Should().BeTrue();
        recipientDid.Should().BeNull();
        var delivered = messages!.Should().ContainSingle().Subject;
        delivered.Id.Should().Be(result.MessageId);
        delivered.Payload.ToArray().Should().Equal(payload);

        // On the wire: no thid member, an empty body object, the attachment as base64url without padding (JC-11).
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(push, DidCommJson.Default));
        wire.RootElement.TryGetProperty("thid", out _).Should().BeFalse();
        wire.RootElement.GetProperty("body").EnumerateObject().ToList().Should().BeEmpty();
        var attachment = wire.RootElement.GetProperty("attachments").EnumerateArray().ToList().Should().ContainSingle().Subject;
        attachment.GetProperty("id").GetString().Should().Be(result.MessageId);
        attachment.GetProperty("data").GetProperty("base64").GetString()
            .Should().Be(Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
    }

    [Fact]
    public async Task The_largest_admissible_message_pushed_live_fits_the_delivery_budget_once_really_packed()
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.MaxDeliveryBytes = 64 * 1024);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true, pack: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(fixture.Queue.MaxAdmissiblePayloadBytes));

        result.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive, "what is admitted can always be delivered alone");
        var attempt = socket.Attempts.Should().ContainSingle().Subject;
        attempt.MaxPackedBytes.Should().Be(64 * 1024);
        attempt.PackedBytes.Should().BeLessThanOrEqualTo(64 * 1024);
    }

    [Fact]
    public async Task A_live_delivery_comes_from_the_local_did_of_the_session_it_is_pushed_on()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var otherMediatorDid = (await fixture.NewPeerAsync()).Did;
        var first = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, first);
        // The same peer connects again, this time talking to another DID of the mediator.
        var second = fixture.Session(peer, otherMediatorDid, live: true, pack: true);
        (await MessagePickupLive.SetLiveAsync(fixture, peer, otherMediatorDid, second)).LiveDelivery.Should().BeTrue();

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20));

        result.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        var push = second.Sent.Should().ContainSingle().Subject;
        push.From.Should().Be(otherMediatorDid);
        push.To.Should().Equal(peer);
        first.Attempts.Should().BeEmpty();
    }

    private static IReadOnlyList<string> PushedIds(PickupFakeSession session)
    {
        var ids = new List<string>();
        foreach (var message in session.Sent)
        {
            MessagePickup.TryParseDelivery(message, out var messages, out _).Should().BeTrue("everything pushed onto a live session here is a delivery");
            ids.Add(messages!.Should().ContainSingle("a live delivery carries exactly one message").Subject.Id);
        }
        return ids;
    }
}
