using AwesomeAssertions;
using DidComm.Mediator.Client.Internal;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="UnpackReservations"/> (#111, I-1, I-2): 16 host calls per named sender (owner
/// decision G) and 16 for the anonymous partition, per (mediator, mailbox); 256 per container (owner
/// decision D); a refusal says which bound refused; each reservation is given back exactly once,
/// whoever releases it; and the table keeps no partition whose count is zero.
/// </summary>
public sealed class UnpackReservationsTests
{
    private static readonly DeliveryScope Bob = new("did:example:mediator", "did:example:bob");
    private static readonly DeliveryScope Carol = new("did:example:mediator", "did:example:carol");

    [Fact]
    public void A_reservation_is_given_back_exactly_once_whoever_releases_it()
    {
        var reservations = new UnpackReservations();
        reservations.TryTake(Bob, "did:example:alice", out var first, out _).Should().BeTrue();
        reservations.TryTake(Bob, "did:example:alice", out var second, out _).Should().BeTrue();

        first!.Release();
        first.Release();
        first.ReleaseWhen(Task.CompletedTask);

        reservations.Reserved.Should().Be(1, "the second reservation is still held");
        reservations.Partitions.Should().Be(1);
        second!.Release();
        reservations.Reserved.Should().Be(0);
        reservations.Partitions.Should().Be(0, "a partition whose count reaches zero is removed");
    }

    [Fact]
    public void A_reservation_is_given_back_when_its_call_ends_however_it_ends()
    {
        var reservations = new UnpackReservations();
        var ends = new[] { new TaskCompletionSource(), new TaskCompletionSource(), new TaskCompletionSource() };
        foreach (var end in ends)
        {
            reservations.TryTake(Bob, null, out var reservation, out _).Should().BeTrue();
            reservation!.ReleaseWhen(end.Task);
        }
        reservations.Reserved.Should().Be(3);

        ends[0].SetResult();
        ends[1].SetException(new InvalidOperationException("late fault"));
        ends[2].SetCanceled();

        reservations.Reserved.Should().Be(0, "the release runs on the thread that ends the call");
    }

    [Fact]
    public void A_named_sender_gets_sixteen_per_mailbox_and_the_anonymous_partition_sixteen_of_its_own()
    {
        UnpackReservations.MaxPerSender.Should().Be(16, "owner decision G");
        UnpackReservations.MaxPerAnonymous.Should().Be(16);
        var reservations = new UnpackReservations();
        for (var i = 0; i < UnpackReservations.MaxPerSender; i++)
            reservations.TryTake(Bob, "did:example:alice", out _, out _).Should().BeTrue();
        for (var i = 0; i < UnpackReservations.MaxPerAnonymous; i++)
            reservations.TryTake(Bob, null, out _, out _).Should().BeTrue();

        reservations.TryTake(Bob, "did:example:alice", out var refused, out var refusal).Should().BeFalse();
        refused.Should().BeNull();
        refusal.Should().Be(UnpackReservations.Refusal.Partition);
        reservations.TryTake(Bob, null, out _, out refusal).Should().BeFalse();
        refusal.Should().Be(UnpackReservations.Refusal.Partition);

        reservations.TryTake(Bob, "did:example:mallory", out _, out _).Should().BeTrue("another sender has a partition of its own");
        reservations.TryTake(Carol, "did:example:alice", out _, out _).Should().BeTrue("another mailbox has partitions of its own");
        reservations.TryTake(Carol, null, out _, out _).Should().BeTrue();
        reservations.TryTake(new DeliveryScope("did:example:other-mediator", Bob.LocalDid), null, out _, out _).Should().BeTrue("so does another mediator");
        reservations.Reserved.Should().Be(UnpackReservations.MaxPerSender + UnpackReservations.MaxPerAnonymous + 4);
    }

    [Fact]
    public void The_container_holds_two_hundred_and_fifty_six_and_refuses_the_next_for_the_container()
    {
        UnpackReservations.MaxReserved.Should().Be(256, "owner decision D");
        var reservations = new UnpackReservations();
        var taken = new List<UnpackReservations.Reservation>();
        for (var sender = 0; sender < UnpackReservations.MaxReserved / UnpackReservations.MaxPerSender; sender++)
        {
            for (var i = 0; i < UnpackReservations.MaxPerSender; i++)
            {
                reservations.TryTake(Bob, $"did:example:sender-{sender}", out var reservation, out _).Should().BeTrue();
                taken.Add(reservation!);
            }
        }

        reservations.TryTake(Carol, "did:example:alice", out _, out var refusal).Should().BeFalse();
        refusal.Should().Be(UnpackReservations.Refusal.Container);
        reservations.Reserved.Should().Be(UnpackReservations.MaxReserved);

        taken[0].Release();
        reservations.TryTake(Carol, "did:example:alice", out _, out refusal).Should().BeTrue();
        refusal.Should().Be(UnpackReservations.Refusal.None);
    }

    [Fact]
    public async Task Concurrent_takes_never_hold_more_than_the_bounds()
    {
        var reservations = new UnpackReservations();
        // Dedicated threads, not pool threads: the barrier parks them until all eight have arrived.
        using var start = new Barrier(8);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            var won = 0;
            for (var i = 0; i < 100; i++)
            {
                if (reservations.TryTake(Bob, i % 2 == 0 ? null : "did:example:alice", out _, out _))
                    won++;
            }
            return won;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)));

        results.Sum().Should().Be(UnpackReservations.MaxPerSender + UnpackReservations.MaxPerAnonymous);
        reservations.Reserved.Should().Be(UnpackReservations.MaxPerSender + UnpackReservations.MaxPerAnonymous);
    }
}
