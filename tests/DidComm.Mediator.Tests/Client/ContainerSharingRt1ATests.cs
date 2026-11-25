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
    private const int Four = DeliveredMessageUnpacker.FetchConcurrency;
    private const int Busy = 16; // the case RT1-A found: 16 mailboxes, 64 lookups
    private const int Lookups = Busy * Four;
    private const string M = FakePickupExchange.M;
    private const string Carol = "did:example:carol";

    /// <summary>
    /// A mailbox's message opens while sixteen other mailboxes of the same container (one sender's
    /// four slow lookups each, 64 in all) are opening deliveries whose lookups are slow and honour
    /// their tokens: at the start of their budget and just before its end. Fixed by raising C to 256
    /// (owner decision D). The premise is checked too: every lookup's token is cancelled at the
    /// budget's end and every reservation comes back with nothing else done.
    /// </summary>
    [Fact]
    public async Task A_mailbox_opens_its_messages_while_other_mailboxes_wait_on_lookups_that_honour_their_tokens()
    {
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

        var others = Enumerable.Range(0, Busy).Select(t => unpacker.UnpackAllAsync(
            new DeliveryScope(M, $"did:example:tenant-{t}"),
            Enumerable.Range(0, Four).Select(i => Input(TestJwe.Authcrypt("did:example:one-sender#key-1", $"slow-{t}-{i}"))).ToArray(),
            $"request-{t}",
            default)).ToArray();

        DeliveredOutcome atStart;
        DeliveredOutcome nearEnd;
        int reservedAtStart;
        try
        {
            await calls.WaitForAsync(Lookups, RealTime);
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

        // The premise: every lookup honoured its token, so all 64 came back at the budget's end
        // with nothing else done.
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue("every lookup honours its token");
        calls.Tokens.Take(Lookups).Should().OnlyContain(token => token.IsCancellationRequested);
        reservedAtStart.Should().Be(Lookups);
        (await OpenForCarolAsync(unpacker, "alice-3")).Succeeded.Should().BeTrue("the control: with nothing reserved Carol's message opens");

        // The property.
        new[] { atStart, nearEnd }.Should().OnlyContain(o => o.Succeeded,
            "Carol's mailbox has no slow message, and every lookup honours its token (at the start: {0}; just before the end: {1})",
            atStart.Error?.Message ?? "opened", nearEnd.Error?.Message ?? "opened");
    }

    /// <summary>
    /// R-1 (the residual, pinned): one sender that keeps 64 mailboxes busy, four slow lookups each
    /// (lookups that honour their tokens), holds all of the container's 256 reservations while they
    /// run; another mailbox's message is then refused for the container, without starting. When the
    /// budget ends them, every reservation comes back and the message opens.
    /// </summary>
    [Fact]
    public async Task Residual_one_sender_keeping_sixty_four_mailboxes_busy_holds_the_whole_container()
    {
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
        var others = Enumerable.Range(0, C / Four).Select(t => unpacker.UnpackAllAsync(
            new DeliveryScope(M, $"did:example:tenant-{t}"),
            Enumerable.Range(0, Four).Select(i => Input(TestJwe.Authcrypt("did:example:one-sender#key-1", $"slow-{t}-{i}"))).ToArray(),
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

        refused.Error.Should().BeOfType<TimeoutException>("the residual: 64 busy mailboxes hold all 256").Which.Message.Should().Contain("all mailboxes together");
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
