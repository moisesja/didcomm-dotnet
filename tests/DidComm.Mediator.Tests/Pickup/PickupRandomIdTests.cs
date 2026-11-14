using System.Text.RegularExpressions;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// #95 JC-3 (design §5): the ids of queued messages come from the queue's own CSPRNG — 16 random
/// bytes as 32 lowercase hex characters — and never from the container's
/// <see cref="IMessageIdGenerator"/>, so a host's sequential generator cannot make one recipient's
/// attachment ids predictable from another's. Only the internal <see cref="PickupIdSource"/> seam
/// replaces them.
/// </summary>
public sealed partial class PickupRandomIdTests
{
    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex LowerHex32();

    [Fact]
    public async Task A_sequential_IMessageIdGenerator_in_the_container_never_reaches_an_attachment_id()
    {
        var sequential = new SequentialIds();
        await using var fixture = PickupFixture.Create(
            pickup: o => o.MaxQueueMessages = 1000,
            services: s => s.AddSingleton<IMessageIdGenerator>(sequential));
        var (mediator, peer) = await fixture.EnrolledAsync();
        var callsBefore = sequential.Calls;

        var ids = new List<string>(1000);
        for (var i = 0; i < 1000; i++)
        {
            var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(16, (byte)i));
            result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
            ids.Add(result.MessageId!);
        }

        ids.Should().AllSatisfy(id => LowerHex32().IsMatch(id).Should().BeTrue("a queued id is 32 lowercase hex characters"));
        ids.Should().OnlyHaveUniqueItems();
        sequential.Calls.Should().Be(callsBefore, "the queue never asks the container's generator for an id");
        sequential.Minted.Should().NotIntersectWith(ids);

        // What goes on the wire is the same random id.
        var delivered = await MessagePickupLive.FetchNowAsync(fixture, peer, mediator);
        delivered.Should().HaveCount(10);
        delivered.Select(m => m.Id).Should().Equal(ids.Take(10));
    }

    [Fact]
    public void NewRandomId_is_32_lowercase_hex_characters_unreserved_within_the_minted_bound_and_distinct()
    {
        var ids = Enumerable.Range(0, 10_000).Select(_ => PickupIds.NewRandomId()).ToList();

        ids.Should().AllSatisfy(id =>
        {
            LowerHex32().IsMatch(id).Should().BeTrue();
            id.Length.Should().BeLessThanOrEqualTo(PickupSizing.MaxMintedIdLength);
            UnreservedUriChars.IsUnreserved(id).Should().BeTrue();
        });
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_queue_built_with_the_public_constructor_mints_random_ids_too()
    {
        await using var fixture = PickupFixture.Create();
        var (_, peer) = await fixture.EnrolledAsync();
        var queue = new MessagePickupQueue(
            fixture.Store,
            fixture.Host.Store,
            fixture.Sessions,
            Options.Create(new MessagePickupOptions()),
            Options.Create(new DidCommOptions()));

        var first = await queue.DeliverAsync(peer, PickupFixture.Payload(16, 1));
        var second = await queue.DeliverAsync(peer, PickupFixture.Payload(16, 2));

        LowerHex32().IsMatch(first.MessageId!).Should().BeTrue();
        LowerHex32().IsMatch(second.MessageId!).Should().BeTrue();
        first.MessageId.Should().NotBe(second.MessageId);
    }

    [Fact]
    public async Task The_internal_seam_is_the_only_way_to_choose_the_ids()
    {
        var calls = 0;
        await using var fixture = PickupFixture.Create(services: s => s.AddSingleton(new PickupIdSource(() => "seam-" + Interlocked.Increment(ref calls))));
        var (_, peer) = await fixture.EnrolledAsync();

        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(16))).MessageId.Should().Be("seam-1");
        FluentActions.Invoking(() => new PickupIdSource(null!)).Should().Throw<ArgumentNullException>();
    }

    /// <summary>The kind of generator a host might register for its own message ids: guessable, one after the other.</summary>
    private sealed class SequentialIds : IMessageIdGenerator
    {
        private int _next;

        public List<string> Minted { get; } = new();

        public int Calls => Volatile.Read(ref _next);

        public string NewId()
        {
            var id = "seq-" + Interlocked.Increment(ref _next);
            lock (Minted)
                Minted.Add(id);
            return id;
        }
    }
}
