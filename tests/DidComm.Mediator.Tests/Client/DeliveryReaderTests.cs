using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The recipient's per-attachment reading of a <c>delivery</c> (#96 design §3.3):
/// <see cref="PickupBodies.ReadAttachmentsEach"/> reads only the first <c>max</c> attachments (L-055),
/// turns one bad attachment into one failed reading instead of refusing the delivery (as the
/// mediator-side <see cref="PickupBodies.ReadAttachments"/> does, unchanged), and decodes nothing for
/// an attachment without a usable id; <see cref="DeliveryReader.ReadEntries"/> then keeps each id once.
/// </summary>
public sealed class DeliveryReaderTests
{
    [Fact]
    public void Each_attachment_is_its_own_reading_and_one_bad_one_does_not_refuse_the_others()
    {
        var attachments = new List<Attachment>
        {
            Base64("a", "first"),
            new() { Id = "b", Data = new AttachmentData { Base64 = "%%%" } },
            new() { Id = "c", Data = new AttachmentData { Json = new JsonObject { ["k"] = 1 } } },
            new() { Id = "d", Data = new AttachmentData { Links = new List<string> { "https://x" }, Hash = "h" } },
            Base64("e", string.Empty),
        };

        var readings = PickupBodies.ReadAttachmentsEach(attachments, 10, "delivery");

        readings.Select(r => r.Id).Should().Equal("a", "b", "c", "d", "e");
        Encoding.UTF8.GetString(readings[0].Payload!).Should().Be("first");
        readings[1].Payload.Should().BeNull();
        readings[1].Error.Should().BeOfType<MalformedMessageException>().Which.Message.Should().NotContain("%%%");
        Encoding.UTF8.GetString(readings[2].Payload!).Should().Be("{\"k\":1}");
        readings[3].Error.Should().NotBeNull("only inline data can be delivered");
        readings[4].Payload.Should().BeEmpty();
        readings[4].Error.Should().BeNull();

        // The mediator-side reader is unchanged: one bad attachment refuses the delivery.
        var strict = () => PickupBodies.ReadAttachments(attachments, "delivery");
        strict.Should().Throw<MalformedMessageException>();
    }

    [Fact]
    public void Only_the_first_max_attachments_are_read()
    {
        // Poison beyond the bound: an entry that would throw if it were looked at.
        var attachments = new PoisonAfter(2, new[] { Base64("a", "1"), Base64("b", "2") });
        var readings = PickupBodies.ReadAttachmentsEach(attachments, 2, "delivery");
        readings.Select(r => r.Id).Should().Equal("a", "b");
        PickupBodies.ReadAttachmentsEach(attachments, 0, "delivery").Should().BeEmpty();
        PickupBodies.ReadAttachmentsEach(null, 5, "delivery").Should().BeEmpty();
        var negative = () => PickupBodies.ReadAttachmentsEach(attachments, -1, "delivery");
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("q/1")]
    [InlineData("é")]
    public void An_attachment_without_a_usable_id_has_no_id_and_nothing_decoded(string? id)
    {
        var readings = PickupBodies.ReadAttachmentsEach(new List<Attachment> { new() { Id = id, Data = new AttachmentData { Base64 = "%%%" } } }, 5, "delivery");
        var reading = readings.Should().ContainSingle().Subject;
        reading.Id.Should().BeNull();
        reading.Payload.Should().BeNull();
        reading.Error.Should().BeOfType<MalformedMessageException>().Which.Message.Should().NotContain("data.base64", "the data of an unusable attachment is not decoded");
        if (!string.IsNullOrEmpty(id))
            reading.Error!.Message.Should().NotContain(id);
    }

    [Fact]
    public void An_over_long_id_is_unusable_and_not_quoted()
    {
        var id = new string('i', PickupBodies.MaxIdLength + 1);
        var reading = PickupBodies.ReadAttachmentsEach(new List<Attachment> { Base64(id, "x") }, 5, "delivery").Single();
        reading.Id.Should().BeNull();
        reading.Error!.Message.Should().NotContain(new string('i', 50));
        PickupBodies.ReadAttachmentsEach(new List<Attachment> { Base64(new string('i', PickupBodies.MaxIdLength), "x") }, 5, "delivery").Single().Id.Should().NotBeNull("2048 characters are within the bound");
    }

    [Fact]
    public void A_null_attachment_is_an_unusable_reading()
        => PickupBodies.ReadAttachmentsEach(new List<Attachment> { null! }, 5, "delivery").Single().Id.Should().BeNull();

    [Fact]
    public void ReadEntries_keeps_each_id_once_the_first_wins_and_counts_what_it_left_out()
    {
        var contents = DeliveryReader.ReadEntries(new List<Attachment>
        {
            Base64("a", "1"),
            Base64(null, "2"),
            Base64("a", "3"),
            Base64("b", "4"),
            Base64("c", "5"),
        }, 4);

        contents.Entries.Select(e => (e.Id, Text: Encoding.UTF8.GetString(e.Payload!))).Should().Equal(("a", "1"), ("b", "4"));
        contents.AttachmentCount.Should().Be(5);
        contents.Excess.Should().Be(1);
        contents.Unusable.Should().Be(1);
        contents.RepeatedIds.Should().Be(1);
    }

    private static Attachment Base64(string? id, string text)
        => new() { Id = id, Data = new AttachmentData { Base64 = Base64Url.Encode(Encoding.UTF8.GetBytes(text)) } };

    // A list whose entries at and beyond `after` throw when read.
    private sealed class PoisonAfter(int after, IReadOnlyList<Attachment> head) : List<Attachment>(head), IList<Attachment>
    {
        Attachment IList<Attachment>.this[int index]
        {
            get => index >= after ? throw new InvalidOperationException("read beyond the bound") : this[index];
            set => this[index] = value;
        }

        int ICollection<Attachment>.Count => after + 5;
    }
}
