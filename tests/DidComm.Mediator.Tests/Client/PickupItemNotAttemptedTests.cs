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
/// <see cref="PickupItem.NotAttempted"/> on the items a fetch returns and a live stream yields
/// (#111): it is what the unpacker said of the message (<see cref="DeliveredOutcome.NotAttempted"/>,
/// whose kinds <see cref="DeliveredUnpackNotAttemptedTests"/> covers), and it is false for the items
/// the two paths make themselves: an opened message, an entry with no usable payload, and a message
/// a <c>recipientDid</c> filter turned away. The fake exchange answers synchronously, and its unpack
/// step says of each message whether it was attempted.
/// </summary>
public sealed class PickupItemNotAttemptedTests
{
    // What the stand-in unpack step says of a message, by its payload text.
    private static Task<DeliveredOutcome> Unpack(string text, CancellationToken ct)
        => Task.FromResult(text switch
        {
            _ when text.StartsWith("untried:", StringComparison.Ordinal) => DeliveredOutcome.Failure(new TimeoutException("The delivered message was not tried."), notAttempted: true),
            _ when text.StartsWith("tried:", StringComparison.Ordinal) => DeliveredOutcome.Failure(new TimeoutException("The delivered message was tried and did not open.")),
            _ => DefaultOutcome(text),
        });

    // One delivery with every kind of item a path can make from it, the filter's kind apart: a
    // message the unpacker did not attempt, one it attempted, one it opened, an attachment whose
    // data does not decode, and one that is not a DIDComm message in encrypted form.
    private static InboundMessageSnapshot EveryKind(SentRequest request)
        => RawDelivery(request, new[]
        {
            Entry("untried", "untried:a"),
            Entry("tried", "tried:b"),
            Entry("opened", "ok:c"),
            PoisonedAttachment("undecodable"),
            RawEntry("not-encrypted", "{\"id\":\"plain\"}"),
        });

    /// <summary>
    /// A fetch: the item of a message the unpacker did not attempt says so, and no other item does:
    /// not the one the unpacker attempted, the one that opened, the attachment that does not decode,
    /// or the one that is not in encrypted form.
    /// </summary>
    [Fact]
    public async Task A_fetch_passes_on_what_the_unpacker_said_and_its_own_failed_items_are_never_not_attempted()
    {
        var exchange = new FakePickupExchange { Unpacker = Unpack };
        exchange.On(MessagePickup.DeliveryRequestType, EveryKind);

        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 10);

        batch.Items.Select(i => i.Id).Should().Equal("untried", "tried", "opened", "undecodable", "not-encrypted");
        Flags(batch.Items).Should().Equal(
            ("untried", true),
            ("tried", false),
            ("opened", false),
            ("undecodable", false),
            ("not-encrypted", false));
        batch.Items.Single(i => i.Id == "opened").Unpacked.Should().NotBeNull();
        batch.Items.Where(i => i.Id != "opened").Should().OnlyContain(i => i.Error != null);
        exchange.Unpacked.Should().Equal(new[] { "untried:a", "tried:b", "ok:c" }, "the two attachments with no usable payload never reach the unpacker");
        exchange.AcknowledgedIds.Should().Equal("opened");
    }

    /// <summary>
    /// A fetch with a <c>recipientDid</c> filter: a message that opened with a key of another of the
    /// host's DIDs is a failed item, and it is not one that was not attempted: it was opened.
    /// </summary>
    [Fact]
    public async Task A_message_a_filter_turned_away_is_not_one_that_was_not_attempted()
    {
        var exchange = new FakePickupExchange { Unpacker = Unpack };
        exchange.On(MessagePickup.DeliveryRequestType, r => Delivery(r, ("other", "to:did:example:bob-two:m1"), ("untried", "untried:a")));

        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 10, recipientDid: L);

        batch.Items.Single(i => i.Id == "other").Error.Should().BeOfType<MediationProtocolException>().Which.Message.Should().Contain("for another of your DIDs");
        Flags(batch.Items).Should().Equal(("other", false), ("untried", true));
        exchange.AcknowledgedIds.Should().BeEmpty();
    }

    /// <summary>
    /// A live stream: the same delivery, by its drain. The item of the message the unpacker did not
    /// attempt says so, and no other item does.
    /// </summary>
    [Fact]
    public async Task A_live_stream_passes_on_what_the_unpacker_said_and_its_own_failed_items_are_never_not_attempted()
    {
        var exchange = new FakePickupExchange { Route = WebSocketRoute, Unpacker = Unpack };
        exchange.OnSequence(MessagePickup.DeliveryRequestType, EveryKind, r => Status(r));
        await using var stream = PickupTest.Client(exchange).ReceiveLiveAsync(M, L).GetAsyncEnumerator();

        var items = new List<PickupItem>();
        for (var i = 0; i < 5; i++)
            items.Add(await PickupClientLiveStreamTests.Next(stream));

        Flags(items).Should().Equal(
            ("untried", true),
            ("tried", false),
            ("opened", false),
            ("undecodable", false),
            ("not-encrypted", false));
        items.Single(i => i.Id == "opened").Unpacked.Should().NotBeNull();
        items.Where(i => i.Id != "opened").Should().OnlyContain(i => i.Error != null);
    }

    /// <summary>
    /// A live stream, by a push: the item of a pushed message the unpacker did not attempt says so,
    /// and the one it attempted does not.
    /// </summary>
    [Fact]
    public async Task A_live_stream_passes_on_what_the_unpacker_said_of_a_pushed_message()
    {
        var exchange = new FakePickupExchange { Route = WebSocketRoute, Unpacker = Unpack };
        using var cts = new CancellationTokenSource();
        var stream = PickupTest.Client(exchange).ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout); // drained: the stream waits for a push
        exchange.Registration!.TryPush(Push(("untried", "untried:a"), ("tried", "tried:b"))).Should().BeTrue();

        (await PickupTest.Within(first)).Should().BeTrue();
        var items = new List<PickupItem> { stream.Current, await PickupClientLiveStreamTests.Next(stream) };

        Flags(items).Should().Equal(("untried", true), ("tried", false));
        var waiting = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        await PickupClientLiveStreamTests.StopAsync(stream, waiting, cts);
        exchange.AcknowledgedIds.Should().BeEmpty("a failed item is never acknowledged, attempted or not");
    }

    /// <summary>Only a failed item can be one that was not attempted: an opened one cannot be made with the flag.</summary>
    [Fact]
    public void An_item_that_opened_cannot_be_one_that_was_not_attempted()
    {
        var opened = DefaultOutcome("ok:m1").Unpacked!;

        FluentActions.Invoking(() => new PickupItem("q1", opened, error: null, notAttempted: true)).Should().Throw<ArgumentException>();
        new PickupItem("q1", opened, error: null).NotAttempted.Should().BeFalse();
        new PickupItem("q1", unpacked: null, new TimeoutException()).NotAttempted.Should().BeFalse("a failed item is attempted unless it is said not to be");
        new PickupItem("q1", unpacked: null, new TimeoutException(), notAttempted: true).NotAttempted.Should().BeTrue();
    }

    private static (string Id, bool NotAttempted)[] Flags(IEnumerable<PickupItem> items) => items.Select(i => (i.Id, i.NotAttempted)).ToArray();
}
