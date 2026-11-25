using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT1-A for #111 (I-1, I-2, D-4; the residuals R-1 and R-8): what one mailbox of a container can
/// open while other mailboxes of the same container are opening deliveries whose lookups are slow
/// and honour their cancellation tokens. Every timing is driven by the injected
/// <see cref="ManualTimeProvider"/> and rendezvous on host calls; real time is only a hang guard.
/// </summary>
public sealed class ContainerSharingRt1ATests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private const int C = UnpackReservations.MaxReserved;

    // As many messages of one delivery as are opened at the same time, which is also one sender's
    // bound in a mailbox (sixteen each: #111, owner decision G).
    private const int AtOnce = DeliveredMessageUnpacker.FetchConcurrency;
    private const string M = FakePickupExchange.M;
    private const string Carol = "did:example:carol";

    /// <summary>
    /// A mailbox's message opens while other mailboxes of the same container are opening deliveries
    /// of one sender whose lookups are slow and honour their tokens: at the start of their budget
    /// and just before its end. Two cases. The one RT1-A found, fixed by raising C to 256 (owner
    /// decision D): sixteen mailboxes with four slow lookups each, 64 in all. And, with sixteen
    /// opened at once and sixteen per sender (owner decision G), the most mailboxes that can be
    /// kept busy at that sender's bound and still leave room: fifteen with sixteen slow lookups
    /// each, 240 in all (one mailbox more holds the whole container: the residual below). The
    /// premise is checked too: every lookup's token is cancelled at the budget's end and every
    /// reservation comes back with nothing else done.
    /// </summary>
    [Theory]
    [InlineData(16, 4)]
    [InlineData(C / AtOnce - 1, AtOnce)]
    public async Task A_mailbox_opens_its_messages_while_other_mailboxes_wait_on_lookups_that_honour_their_tokens(int busy, int lookupsEach)
    {
        var lookups = busy * lookupsEach;
        lookups.Should().BeLessThan(C, "the busy mailboxes leave room in the container");
        lookupsEach.Should().BeLessThanOrEqualTo(Math.Min(AtOnce, UnpackReservations.MaxPerSender), "every lookup of a busy mailbox starts at once and none is refused for its sender");
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            using var entered = calls.Enter(label, ct);
            return label.StartsWith("slow-", StringComparison.Ordinal)
                ? await UnpackFakes.StallUntilCancelled(ct).ConfigureAwait(false)
                : UnpackFakes.Opened(label);
        }, Budget, time);

        var others = Enumerable.Range(0, busy).Select(t => unpacker.UnpackAllAsync(
            new DeliveryScope(M, $"did:example:tenant-{t}"),
            Enumerable.Range(0, lookupsEach).Select(i => Input(TestJwe.Authcrypt("did:example:one-sender#key-1", $"slow-{t}-{i}"))).ToArray(),
            $"request-{t}",
            default)).ToArray();

        DeliveredOutcome atStart;
        DeliveredOutcome nearEnd;
        int reservedAtStart;
        try
        {
            await calls.WaitForAsync(lookups, RealTime);
            reservedAtStart = unpacker.Reserved;

            atStart = await OpenForCarolAsync(unpacker, "alice-1");
            time.Advance(Budget - TimeSpan.FromTicks(1));
            nearEnd = await OpenForCarolAsync(unpacker, "alice-2");
        }
        finally
        {
            time.Advance(Budget);
            await Task.WhenAll(others).WaitAsync(RealTime);
        }

        // The premise: every lookup honoured its token, so all of them came back at the budget's
        // end with nothing else done.
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue("every lookup honours its token");
        calls.Tokens.Take(lookups).Should().OnlyContain(token => token.IsCancellationRequested);
        reservedAtStart.Should().Be(lookups);
        (await OpenForCarolAsync(unpacker, "alice-3")).Succeeded.Should().BeTrue("the control: with nothing reserved Carol's message opens");

        // The property.
        new[] { atStart, nearEnd }.Should().OnlyContain(o => o.Succeeded,
            "Carol's mailbox has no slow message, and every lookup honours its token (at the start: {0}; just before the end: {1})",
            atStart.Error?.Message ?? "opened", nearEnd.Error?.Message ?? "opened");
    }

    /// <summary>
    /// R-1 (the residual, pinned): one sender that keeps sixteen mailboxes busy (C / 16), sixteen
    /// slow lookups each (its bound in a mailbox, and as many as a delivery opens at once; lookups
    /// that honour their tokens), holds all of the container's 256 reservations while they run;
    /// another mailbox's message is then refused for the container, without starting. When the
    /// budget ends them, every reservation comes back and the message opens. (With four per sender
    /// and four opened at once it took 64 mailboxes.)
    /// </summary>
    [Fact]
    public async Task Residual_one_sender_keeping_sixteen_mailboxes_busy_holds_the_whole_container()
    {
        const int mailboxes = C / AtOnce;
        mailboxes.Should().Be(16);
        UnpackReservations.MaxPerSender.Should().BeGreaterThanOrEqualTo(AtOnce, "none of a mailbox's sixteen is refused for its sender");
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            using var entered = calls.Enter(label, ct);
            return label.StartsWith("slow-", StringComparison.Ordinal)
                ? await UnpackFakes.StallUntilCancelled(ct).ConfigureAwait(false)
                : UnpackFakes.Opened(label);
        }, Budget, time);
        var others = Enumerable.Range(0, mailboxes).Select(t => unpacker.UnpackAllAsync(
            new DeliveryScope(M, $"did:example:tenant-{t}"),
            Enumerable.Range(0, AtOnce).Select(i => Input(TestJwe.Authcrypt("did:example:one-sender#key-1", $"slow-{t}-{i}"))).ToArray(),
            $"request-{t}",
            default)).ToArray();
        DeliveredOutcome refused;
        try
        {
            await calls.WaitForAsync(C, RealTime);
            unpacker.Reserved.Should().Be(C);

            refused = await OpenForCarolAsync(unpacker, "alice-1");
        }
        finally
        {
            time.Advance(Budget);
            await Task.WhenAll(others).WaitAsync(RealTime);
        }

        refused.Error.Should().BeOfType<TimeoutException>("the residual: {0} busy mailboxes hold all {1}", mailboxes, C).Which.Message.Should().Contain("all mailboxes together");
        calls.Labels.Should().NotContain("alice-1");
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue();
        (await OpenForCarolAsync(unpacker, "alice-2")).Succeeded.Should().BeTrue();
    }

    private static async Task<DeliveredOutcome> OpenForCarolAsync(DeliveredMessageUnpacker unpacker, string label)
        => (await unpacker.UnpackAllAsync(
            new DeliveryScope(M, Carol),
            new[] { Input(TestJwe.Authcrypt("did:example:alice#key-1", label)) },
            $"request-{label}",
            default).WaitAsync(RealTime)).Single();

    private static DeliveredInput Input(string packed) => new(TestJwe.LabelOf(packed), TestJwe.Bytes(packed));
}
