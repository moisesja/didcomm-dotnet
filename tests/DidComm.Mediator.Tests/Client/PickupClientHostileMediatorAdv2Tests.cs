using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// ADV-2 red-team pass (#96): a hostile or compromised mediator — authenticated, so every identity
/// check passes — shaping its <c>delivery</c> answers to make the fetch do more than answer. These
/// attempts held; they stay as passing regressions.
/// </summary>
public sealed class PickupClientHostileMediatorAdv2Tests
{
    /// <summary>
    /// ADV-2 attempt (held). 10 000 attachments answer a fetch of limit 10: only the first ten are
    /// unpacked and only those are acknowledged (L-055: nothing beyond the bound is read).
    /// </summary>
    [Fact]
    public async Task ADV2_a_delivery_of_ten_thousand_attachments_is_read_only_to_the_limit()
    {
        var attachments = Enumerable.Range(0, 10_000).Select(i => (Attachment?)Entry("m" + i, "ok:inner-" + i)).ToArray();
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => RawDelivery(r, attachments));

        var batch = await PickupTest.Within(PickupTest.Client(exchange).FetchAsync(M, L, limit: 10));

        batch.Items.Should().HaveCount(10);
        exchange.Unpacked.Should().HaveCount(10);
        exchange.AcknowledgedIds.Should().Equal(Enumerable.Range(0, 10).Select(i => "m" + i));
    }

    /// <summary>
    /// ADV-2 attempt (held). Attachment ids built to inject into logs or the next request — a line
    /// break, a 2049-character id, reserved URI characters, an empty id — are never acknowledged,
    /// never handed out and never written to a log line; the one clean id still is.
    /// </summary>
    [Fact]
    public async Task ADV2_hostile_attachment_ids_are_never_acknowledged_or_logged()
    {
        var injected = "x\r\nWARN forged line";
        var oversized = new string('a', PickupBodies.MaxIdLength + 1);
        var reserved = "a/b?c#d";
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => RawDelivery(r, new Attachment?[]
        {
            Entry(injected, "ok:1"),
            Entry(oversized, "ok:2"),
            Entry(reserved, "ok:3"),
            Entry("", "ok:4"),
            Entry("clean", "ok:5"),
        }));
        var logs = new PickupTestLogs();

        var batch = await PickupTest.Within(PickupTest.Client(exchange, logs).FetchAsync(M, L, limit: 10));

        batch.Items.Select(i => i.Id).Should().Equal("clean");
        exchange.AcknowledgedIds.Should().Equal("clean");
        exchange.Unpacked.Should().Equal("ok:5");
        logs.Entries.Should().NotContain(e => e.Message.Contains("forged line") || e.Message.Contains(oversized) || e.Message.Contains(reserved));
    }

    /// <summary>
    /// ADV-2 attempt (held). One id repeated across the delivery with different contents: the first
    /// wins, the repeat is neither unpacked nor acknowledged a second time, so a mediator cannot use
    /// the repeat to have the client acknowledge a message the application never received.
    /// </summary>
    [Fact]
    public async Task ADV2_a_repeated_attachment_id_acknowledges_only_what_was_handed_out()
    {
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => RawDelivery(r, new Attachment?[]
        {
            Entry("same", "ok:first"),
            Entry("same", "ok:second"),
            Entry("fails", "bad:x"),
        }));

        var batch = await PickupTest.Within(PickupTest.Client(exchange).FetchAsync(M, L, limit: 10));

        batch.Items.Should().HaveCount(2);
        batch.Items[0].Unpacked!.Message.Id.Should().Be("first");
        batch.Items[1].Error.Should().NotBeNull();
        exchange.Unpacked.Should().Equal("ok:first", "bad:x");
        exchange.AcknowledgedIds.Should().Equal("same");
    }
}
