using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.InteropTests.AspNetCore;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// One mailbox, two versions (#100 threat model "cross-version"): against the real
/// <c>AddMediator()</c> host, recipients speaking Message Pickup 3.0 and 4.0 share one queue and one
/// store. An empty queue answers a 4.0 fetch with an empty <c>delivery</c> and a 3.0 fetch with a
/// <c>status</c>; a 3.0 and a 4.0 recipient interleaving on one mediator, over HTTP and over a
/// WebSocket, each get exactly their own messages; one recipient switching versions between
/// requests sees one mailbox, and a lease taken in one version is acknowledged in the other; and a
/// 4.0 status gives its times in milliseconds on the wire, which the client gives in whole seconds.
/// </summary>
public sealed class PickupV4MailboxEndToEndTests
{
    public static TheoryData<bool> Transports => new() { false, true };

    [Fact]
    public async Task An_empty_queue_answers_4_0_with_an_empty_delivery_and_3_0_with_a_status_on_the_same_host()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness4 = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4);
        await using var harness3 = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V3);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness4);
        harness3.Hold(bob);
        harness3.Hold(recipient);

        var fetched4 = await harness4.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);
        var fetched3 = await harness3.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);

        fetched4.Drained.Should().BeTrue();
        fetched4.Items.Should().BeEmpty();
        fetched4.Status.Should().BeNull("4.0 answers an empty queue with an empty delivery, not a status (L29, L210)");
        fetched3.Drained.Should().BeTrue();
        fetched3.Status.Should().NotBeNull("3.0 answers an empty queue with its status (3.0 L143)");
        fetched3.Status!.MessageCount.Should().Be(0);

        // The same two answers on the wire, hand-rolled on the same host.
        var request4 = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var answer4 = await mediator.AskAsync(bob, request4);
        answer4.Delivery40(thid: request4.Id).Should().BeEmpty();
        answer4.Message.Attachments.Should().NotBeNull().And.BeEmpty("\"attachments\": [] is on the wire (L259)");
        var request3 = MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        (await mediator.AskAsync(bob, request3)).Status(request3.Id).MessageCount.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task A_3_0_and_a_4_0_recipient_interleaving_on_one_mediator_each_get_exactly_their_own_messages(bool webSocket)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness3 = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V3, webSocket);
        await using var harness4 = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket);
        var (bob3, r3) = await PickupE2E.EnrolAsync(mediator, harness3);
        var (bob4, r4) = await PickupE2E.EnrolAsync(mediator, harness4);
        var alice = await harness3.NewAliceAsync();
        var over3 = webSocket ? harness3.WebSocketEndpoint : null;
        var over4 = webSocket ? harness4.WebSocketEndpoint : null;

        // Sent and fetched in turns, so each version's requests run between the other's.
        await PickupE2E.SendAsync(mediator, alice, r3, "three-1");
        await PickupE2E.SendAsync(mediator, alice, r4, "four-1");
        await PickupE2E.SendAsync(mediator, alice, r3, "three-2");
        var first4 = await harness4.Pickup.FetchAsync(mediator.Did, bob4.Did, limit: 10, serviceEndpointOverride: over4);
        await PickupE2E.SendAsync(mediator, alice, r4, "four-2");
        var first3 = await harness3.Pickup.FetchAsync(mediator.Did, bob3.Did, limit: 1, serviceEndpointOverride: over3);
        var second4 = await harness4.Pickup.FetchAsync(mediator.Did, bob4.Did, limit: 10, serviceEndpointOverride: over4);
        var second3 = await harness3.Pickup.FetchAsync(mediator.Did, bob3.Did, limit: 10, serviceEndpointOverride: over3);
        var drained4 = await harness4.Pickup.FetchAsync(mediator.Did, bob4.Did, limit: 10, serviceEndpointOverride: over4);
        var drained3 = await harness3.Pickup.FetchAsync(mediator.Did, bob3.Did, limit: 10, serviceEndpointOverride: over3);

        PickupV4E2E.Contents(first4).Should().Equal("four-1");
        PickupV4E2E.Contents(first3).Should().Equal("three-1");
        PickupV4E2E.Contents(second4).Should().Equal("four-2");
        PickupV4E2E.Contents(second3).Should().Equal("three-2");
        foreach (var item in first4.Items.Concat(second4.Items))
            PickupV4E2E.Opened(item).RecipientKid.Should().StartWith(r4.Did + "#", "only the 4.0 recipient's messages reach it");
        foreach (var item in first3.Items.Concat(second3.Items))
            PickupV4E2E.Opened(item).RecipientKid.Should().StartWith(r3.Did + "#", "only the 3.0 recipient's messages reach it");
        drained4.Drained.Should().BeTrue();
        drained4.Status.Should().BeNull();
        drained3.Drained.Should().BeTrue();
        drained3.Status!.MessageCount.Should().Be(0);
        (await mediator.StatusAsync(bob3)).MessageCount.Should().Be(0);
        (await PickupV4E2E.Status40Async(mediator, bob4)).MessageCount.Should().Be(0);
        mediator.Store.MessageCount.Should().Be(0);
        if (webSocket)
        {
            harness3.Connects.Should().BeGreaterThan(0, "precondition: the 3.0 client went over its WebSocket");
            harness4.Connects.Should().BeGreaterThan(0, "precondition: the 4.0 client went over its WebSocket");
        }
        mediator.LeakingLogLines(r3.Did, r4.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task One_recipient_switching_versions_between_requests_sees_one_mailbox()
    {
        // Two containers over the same keys and DID: one pinned to 3.0, one to 4.0. The recipient
        // takes one message in 3.0, the next in 4.0, the last in 3.0 again.
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness3 = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V3);
        await using var harness4 = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness3);
        harness4.Hold(bob);
        harness4.Hold(recipient);
        var alice = await harness3.NewAliceAsync();
        foreach (var content in new[] { "n1", "n2", "n3" })
            await PickupE2E.SendAsync(mediator, alice, recipient, content);

        var status4 = await harness4.Pickup.GetStatusAsync(mediator.Did, bob.Did);
        var one = await harness3.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 1);
        var two = await harness4.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 1);
        var status3 = await harness3.Pickup.GetStatusAsync(mediator.Did, bob.Did);
        var three = await harness3.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);

        status4.MessageCount.Should().Be(3);
        PickupV4E2E.Contents(one).Should().Equal("n1");
        PickupV4E2E.Contents(two).Should().Equal(new[] { "n2" }, "4.0 sees what is left after the 3.0 fetch");
        status3.MessageCount.Should().Be(1, "3.0 counts what is left after the 4.0 fetch");
        PickupV4E2E.Contents(three).Should().Equal("n3");
        (await harness4.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10)).Drained.Should().BeTrue();
        (await harness4.Pickup.GetStatusAsync(mediator.Did, bob.Did)).MessageCount.Should().Be(0);
        mediator.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_lease_taken_in_one_version_is_acknowledged_in_the_other()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness3 = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V3);
        await using var harness4 = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness3);
        harness4.Hold(bob);
        harness4.Hold(recipient);
        var alice = await harness3.NewAliceAsync();
        await PickupE2E.SendAsync(mediator, alice, recipient, "leased in 3.0");
        await PickupE2E.SendAsync(mediator, alice, recipient, "leased in 4.0");

        // A 3.0 delivery-request leases the first message (nothing acknowledges it)...
        var request3 = MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 1);
        var leased3 = (await mediator.AskAsync(bob, request3)).Delivery(request3.Id).Should().ContainSingle().Subject;
        // ...and a 4.0 one the second.
        var request4 = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 1);
        var leased4 = (await mediator.AskAsync(bob, request4)).Delivery40(request4.Id).Should().ContainSingle().Subject;
        leased4.Id.Should().NotBe(leased3.Id);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(2, "leased, not acknowledged");

        // Over HTTP the mediator handles a POST before it answers it, so each acknowledgement has been
        // applied once its AckAsync has returned, and the store can be read right after.
        var afterAck4 = await harness4.Pickup.AckAsync(mediator.Did, bob.Did, new[] { leased3.Id });
        afterAck4.Should().BeNull("under 4.0 nothing answers an acknowledgement");
        mediator.Store.MessageCount.Should().Be(1, "the 4.0 acknowledgement removed the message leased through 3.0");
        var afterAck3 = await harness3.Pickup.AckAsync(mediator.Did, bob.Did, new[] { leased4.Id });

        PickupE2E.Answered(afterAck3).MessageCount.Should().Be(0, "the 3.0 acknowledgement removed the message leased through 4.0");
        mediator.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_4_0_status_carries_milliseconds_on_the_wire_and_the_client_gives_them_in_whole_seconds()
    {
        // The mediator's clock is at an instant with a sub-second part. The newest message arrives
        // 1.876 s after the oldest, at …001.999: rounding would give …002, the floor …001.
        var oldestAt = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_123);
        var clock = new TestClock(oldestAt);
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Clock = clock);
        await using var harness4 = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4);
        await using var harness3 = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V3);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness4);
        harness3.Hold(bob);
        var alice = await harness4.NewAliceAsync();
        await PickupE2E.SendAsync(mediator, alice, recipient, "oldest");
        clock.Advance(TimeSpan.FromMilliseconds(1_876));
        await PickupE2E.SendAsync(mediator, alice, recipient, "newest");

        var request = MessagePickup40.CreateStatusRequest(bob.Did, mediator.Did);
        var answer = await mediator.AskAsync(bob, request);
        var wire = answer.Status40(request.Id);
        var status4 = await harness4.Pickup.GetStatusAsync(mediator.Did, bob.Did);
        var status3 = await harness3.Pickup.GetStatusAsync(mediator.Did, bob.Did);

        wire.OldestReceivedTimeMs.Should().Be(1_800_000_000_123, "4.0 times are UTC epoch milliseconds (L39, L164)");
        wire.NewestReceivedTimeMs.Should().Be(1_800_000_001_999);
        answer.Message.Body!["newest_received_time"]!.GetValue<long>().Should().Be(1_800_000_001_999, "an integer member on the wire");
        status4.OldestReceivedTime.Should().Be(1_800_000_000);
        status4.NewestReceivedTime.Should().Be(1_800_000_001, "the client gives seconds, rounded down");
        status4.LongestWaitedSeconds.Should().BeNull();
        status3.OldestReceivedTime.Should().Be(1_800_000_000, "3.0 gives the same instants in seconds");
        status3.NewestReceivedTime.Should().Be(1_800_000_001);
        status3.LongestWaitedSeconds.Should().NotBeNull();
    }
}
