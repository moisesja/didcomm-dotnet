using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// QA-1 (#95): the queue's record ids — every hostile shape the internal seam can return is refused
/// before it is stored (and never logged), the <c>DuplicateId</c> retry, and a container
/// <see cref="IMessageIdGenerator"/> that is sequential or throws never reaching an id.
/// </summary>
public sealed class QueueIdsQaTests
{
    private const string Peer = "did:example:qa-ids-peer";
    private const string Recipient = "did:example:qa-ids-recipient";

    public static TheoryData<string?> Unusable => new()
    {
        null,
        string.Empty,
        "qa/slash",
        "qa id",
        "qa#hash",
        "qa%41",
        "qaé",
        "qa\u0000",
        new string('a', PickupSizing.MaxMintedIdLength + 1),
    };

    [Theory]
    [MemberData(nameof(Unusable))]
    public async Task An_id_that_cannot_go_on_the_wire_fails_the_delivery_and_nothing_is_stored(string? id)
    {
        await using var fixture = Fixture(() => id!);
        await fixture.EnrolAsync(Peer, Recipient);

        var result = await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(20));

        result.Outcome.Should().Be(PickupDeliveryOutcome.Faulted);
        result.MessageId.Should().BeNull();
        fixture.Store.MessageCount.Should().Be(0);
        if (!string.IsNullOrEmpty(id))
            PipelineLogs.Leaking(fixture.Logs, id).Should().BeEmpty();
    }

    [Fact]
    public async Task An_id_of_exactly_the_minted_bound_in_unreserved_characters_is_used()
    {
        var id = "A-z_0.9~" + new string('x', PickupSizing.MaxMintedIdLength - 8);
        await using var fixture = Fixture(() => id);
        await fixture.EnrolAsync(Peer, Recipient);

        var result = await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(20));

        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        result.MessageId.Should().Be(id);
    }

    [Fact]
    public async Task A_seam_that_throws_fails_the_delivery_and_its_text_is_not_logged()
    {
        await using var fixture = Fixture(() => throw new InvalidOperationException("qa-seam-secret"));
        await fixture.EnrolAsync(Peer, Recipient);

        (await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(20))).Outcome.Should().Be(PickupDeliveryOutcome.Faulted);

        PipelineLogs.Leaking(fixture.Logs, "qa-seam-secret").Should().BeEmpty();
    }

    [Fact]
    public async Task An_id_already_held_is_retried_once_with_a_fresh_id()
    {
        var ids = new Queue<string>(new[] { "qa-first", "qa-first", "qa-second" });
        await using var fixture = Fixture(() => ids.Dequeue());
        await fixture.EnrolAsync(Peer, Recipient);

        (await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(20))).MessageId.Should().Be("qa-first");
        var second = await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(21));

        second.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        second.MessageId.Should().Be("qa-second");
        fixture.Store.MessageCount.Should().Be(2);
    }

    [Fact]
    public async Task Two_ids_already_held_in_a_row_fail_the_delivery_without_a_third_attempt()
    {
        var calls = 0;
        await using var fixture = Fixture(() => { Interlocked.Increment(ref calls); return "qa-same"; });
        await fixture.EnrolAsync(Peer, Recipient);
        await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(20));
        calls = 0;

        var second = await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(21));

        second.Outcome.Should().Be(PickupDeliveryOutcome.Faulted);
        calls.Should().Be(2);
        fixture.Store.MessageCount.Should().Be(1);
        PipelineLogs.Leaking(fixture.Logs, "qa-same").Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_id_under_another_grant_is_not_a_duplicate()
    {
        await using var fixture = Fixture(() => "qa-shared");
        await fixture.EnrolAsync(Peer, Recipient);
        await fixture.EnrolAsync("did:example:qa-ids-other", "did:example:qa-ids-other-recipient");

        (await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(20))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        (await fixture.Queue.DeliverAsync("did:example:qa-ids-other-recipient", PushFixture.Payload(20))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
    }

    [Fact]
    public async Task A_container_id_generator_that_throws_is_never_called_for_a_queued_message()
    {
        var generator = new ThrowingGenerator();
        await using var fixture = PushFixture.Create(configure: o => o.Pickup.MaxQueueMessages = 300, services: s => s.AddSingleton<IMessageIdGenerator>(generator));
        await fixture.EnrolAsync(Peer, Recipient);

        var ids = new List<string>();
        for (var i = 0; i < 200; i++)
            ids.Add((await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(5, (byte)i))).MessageId!);

        generator.Calls.Should().Be(0);
        ids.Should().OnlyHaveUniqueItems().And.AllSatisfy(id => id.Should().MatchRegex("^[0-9a-f]{32}$"));
    }

    private static PushFixture Fixture(Func<string> ids)
        => PushFixture.Create(
            configure: o => o.Pickup.MaxQueueMessages = 300,
            services: s => s.AddSingleton(new PickupIdSource(ids)));

    private sealed class ThrowingGenerator : IMessageIdGenerator
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string NewId()
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("the container's generator must not be used for queue ids");
        }
    }
}
