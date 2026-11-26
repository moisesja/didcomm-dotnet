using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="PickupItem.Inconclusive"/> on the items a fetch returns and a live stream yields
/// (#111): it is what the unpacker said of the message (<see cref="DeliveredOutcome.Inconclusive"/>,
/// whose kinds <see cref="DeliveredUnpackInconclusiveTests"/> covers), and it is false for the items
/// the two paths make themselves: an opened message, an entry with no usable payload, and a message
/// a <c>recipientDid</c> filter turned away. The fake exchange answers synchronously, and its unpack
/// step says of each message whether the delivery counted against it.
/// </summary>
public sealed class PickupItemInconclusiveTests
{
    // What the stand-in unpack step says of a message, by its payload text.
    private static Task<DeliveredOutcome> Unpack(string text, CancellationToken ct)
        => Task.FromResult(text switch
        {
            _ when text.StartsWith("inconclusive:", StringComparison.Ordinal) => DeliveredOutcome.Failure(new TimeoutException("The delivery did not count against the message."), inconclusive: true),
            _ when text.StartsWith("counted:", StringComparison.Ordinal) => DeliveredOutcome.Failure(new TimeoutException("The delivery counted against the message.")),
            _ => DefaultOutcome(text),
        });

    // One delivery with every kind of item a path can make from it, the filter's kind apart: a
    // message the unpacker's outcome calls inconclusive, one it does not, one it opened, an
    // attachment whose data does not decode, and one that is not a DIDComm message in encrypted
    // form.
    private static InboundMessageSnapshot EveryKind(SentRequest request)
        => RawDelivery(request, new[]
        {
            Entry("inconclusive", "inconclusive:a"),
            Entry("counted", "counted:b"),
            Entry("opened", "ok:c"),
            PoisonedAttachment("undecodable"),
            RawEntry("not-encrypted", "{\"id\":\"plain\"}"),
        });

    /// <summary>
    /// A fetch: the item of a message whose outcome is inconclusive says so, and no other item does:
    /// not the one the delivery counted against, the one that opened, the attachment that does not
    /// decode, or the one that is not in encrypted form.
    /// </summary>
    [Fact]
    public async Task A_fetch_passes_on_what_the_unpacker_said_and_its_own_failed_items_are_never_inconclusive()
    {
        var exchange = new FakePickupExchange { Unpacker = Unpack };
        exchange.On(MessagePickup.DeliveryRequestType, EveryKind);

        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 10);

        batch.Items.Select(i => i.Id).Should().Equal("inconclusive", "counted", "opened", "undecodable", "not-encrypted");
        Flags(batch.Items).Should().Equal(
            ("inconclusive", true),
            ("counted", false),
            ("opened", false),
            ("undecodable", false),
            ("not-encrypted", false));
        batch.Items.Single(i => i.Id == "opened").Unpacked.Should().NotBeNull();
        batch.Items.Where(i => i.Id != "opened").Should().OnlyContain(i => i.Error != null);
        exchange.Unpacked.Should().Equal(new[] { "inconclusive:a", "counted:b", "ok:c" }, "the two attachments with no usable payload never reach the unpacker");
        exchange.AcknowledgedIds.Should().Equal("opened");
    }

    /// <summary>
    /// A fetch with a <c>recipientDid</c> filter: a message that opened with a key of another of the
    /// host's DIDs is a failed item that is not inconclusive. It is the host's to settle, and the
    /// first of the two ways works as its failure says: the same message, fetched without the
    /// filter, is returned opened.
    /// </summary>
    [Fact]
    public async Task A_message_a_filter_turned_away_is_not_inconclusive_and_a_fetch_without_the_filter_returns_it_opened()
    {
        var exchange = new FakePickupExchange { Unpacker = Unpack };
        exchange.On(MessagePickup.DeliveryRequestType, r => Delivery(r, ("other", "to:did:example:bob-two:m1"), ("inconclusive", "inconclusive:a")));
        var client = PickupTest.Client(exchange);

        var filtered = await client.FetchAsync(M, L, limit: 10, recipientDid: L);
        var unfiltered = await client.FetchAsync(M, L, limit: 10);

        filtered.Items.Single(i => i.Id == "other").Error.Should().BeOfType<MediationProtocolException>().Which.Message.Should().Contain("for another of your DIDs");
        Flags(filtered.Items).Should().Equal(("other", false), ("inconclusive", true));
        unfiltered.Items.Single(i => i.Id == "other").Unpacked.Should().NotBeNull("a fetch without the filter takes it");
        exchange.AcknowledgedIds.Should().Equal(new[] { "other" }, "nothing was acknowledged under the filter");
    }

    /// <summary>
    /// What the failure of a message a <c>recipientDid</c> filter turned away tells the host: the
    /// two ways to settle it, a fetch without the filter and <see cref="PickupClient.AckAsync"/>,
    /// and no third: it does not say that the message may be left to age out at the mediator. The
    /// text names the request by our own id and nothing from the wire: no DID, and neither the
    /// mediator's id for the message nor the message's own. The second way then removes it:
    /// <c>AckAsync</c> with the item's id sends the acknowledgement.
    /// </summary>
    [Fact]
    public async Task The_failure_of_a_message_a_filter_turned_away_names_the_two_ways_to_settle_it_and_does_not_offer_leaving_it_to_age_out()
    {
        var exchange = new FakePickupExchange { Unpacker = Unpack };
        exchange.On(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q-other", "to:did:example:bob-two:m-other")));
        var client = PickupTest.Client(exchange);

        var filtered = await client.FetchAsync(M, L, limit: 10, recipientDid: L);

        var item = filtered.Items.Should().ContainSingle().Which;
        var text = item.Error.Should().BeOfType<MediationProtocolException>().Which.Message;
        var requestId = exchange.SentOf(MessagePickup.DeliveryRequestType)[0].Request.Id;
        text.Should().Be($"A message delivered for request '{requestId}' is for another of your DIDs: a key of another DID than the recipient_did filter's opened it. " +
            "It was not acknowledged and stays queued until you settle it: take it with a fetch without the filter, or discard it with AckAsync.");
        text.Should().Contain("take it with a fetch without the filter").And.Contain("discard it with AckAsync");
        text.Should().NotContainAny(new[] { "ages out", "age out", "aged out" }, "leaving it queued is not offered");
        text.Should().NotContainAny(new[] { "did:", "q-other", "m-other" }, "only our own request id is named");
        item.Inconclusive.Should().BeFalse();
        exchange.AcknowledgedIds.Should().BeEmpty("the fetch acknowledged nothing");

        await client.AckAsync(M, L, new[] { item.Id });

        exchange.AcknowledgedIds.Should().Equal("q-other");
    }

    /// <summary>
    /// A live stream: the same delivery, by its drain. The item of the message whose outcome is
    /// inconclusive says so, and no other item does.
    /// </summary>
    [Fact]
    public async Task A_live_stream_passes_on_what_the_unpacker_said_and_its_own_failed_items_are_never_inconclusive()
    {
        var exchange = new FakePickupExchange { Route = WebSocketRoute, Unpacker = Unpack };
        exchange.OnSequence(MessagePickup.DeliveryRequestType, EveryKind, r => Status(r));
        await using var stream = PickupTest.Client(exchange).ReceiveLiveAsync(M, L).GetAsyncEnumerator();

        var items = new List<PickupItem>();
        for (var i = 0; i < 5; i++)
            items.Add(await PickupClientLiveStreamTests.Next(stream));

        Flags(items).Should().Equal(
            ("inconclusive", true),
            ("counted", false),
            ("opened", false),
            ("undecodable", false),
            ("not-encrypted", false));
        items.Single(i => i.Id == "opened").Unpacked.Should().NotBeNull();
        items.Where(i => i.Id != "opened").Should().OnlyContain(i => i.Error != null);
    }

    /// <summary>
    /// A live stream, by a push: the item of a pushed message whose outcome is inconclusive says so,
    /// and the one the delivery counted against does not.
    /// </summary>
    [Fact]
    public async Task A_live_stream_passes_on_what_the_unpacker_said_of_a_pushed_message()
    {
        var exchange = new FakePickupExchange { Route = WebSocketRoute, Unpacker = Unpack };
        using var cts = new CancellationTokenSource();
        var stream = PickupTest.Client(exchange).ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout); // drained: the stream waits for a push
        exchange.Registration!.TryPush(Push(("inconclusive", "inconclusive:a"), ("counted", "counted:b"))).Should().BeTrue();

        (await PickupTest.Within(first)).Should().BeTrue();
        var items = new List<PickupItem> { stream.Current, await PickupClientLiveStreamTests.Next(stream) };

        Flags(items).Should().Equal(("inconclusive", true), ("counted", false));
        var waiting = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        await PickupClientLiveStreamTests.StopAsync(stream, waiting, cts);
        exchange.AcknowledgedIds.Should().BeEmpty("a failed item is never acknowledged, inconclusive or not");
    }

    /// <summary>Only a failed item can be inconclusive: an opened one cannot be made with the flag.</summary>
    [Fact]
    public void An_item_that_opened_cannot_be_inconclusive()
    {
        var opened = DefaultOutcome("ok:m1").Unpacked!;

        FluentActions.Invoking(() => new PickupItem("q1", opened, error: null, inconclusive: true)).Should().Throw<ArgumentException>();
        new PickupItem("q1", opened, error: null).Inconclusive.Should().BeFalse();
        new PickupItem("q1", unpacked: null, new TimeoutException()).Inconclusive.Should().BeFalse("a failed item is conclusive unless it is said not to be");
        new PickupItem("q1", unpacked: null, new TimeoutException(), inconclusive: true).Inconclusive.Should().BeTrue();
    }

    private static (string Id, bool Inconclusive)[] Flags(IEnumerable<PickupItem> items) => items.Select(i => (i.Id, i.Inconclusive)).ToArray();
}
