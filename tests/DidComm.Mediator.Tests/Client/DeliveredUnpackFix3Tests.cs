using System.Collections.Concurrent;
using System.Text;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// Fix round 3 (#96) regressions for opening deliveries, beside the ADV-3 reproductions they close
/// (<see cref="DeliveredUnpackAdv3Tests"/>, <see cref="MediatorClientChannelAdv3Tests"/>): every
/// unpack (host code) runs off the caller's stack, and blocked host code still keeps an abandoned
/// slot (ADV3-4); the channel's shutdown ends a delivery being opened at once and cancels the host
/// code's tokens, for a fetch and a live stream alike (ADV3-7). Manual clocks and rendezvous only
/// (L-044).
/// </summary>
public sealed class DeliveredUnpackFix3Tests : IAsyncLifetime
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout;
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);

    // Set only while this test's own thread is inside a call (or a completion) it makes.
    [ThreadStatic]
    private static bool _onTestStack;

    private ClientWorld _w = null!;

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    // ---- ADV3-4 -------------------------------------------------------------------------------

    /// <summary>
    /// Four resolvers that block their threads: the caller of <c>UnpackAllAsync</c> gets its task
    /// back at once, none of the unpacks ran on its stack, the budget decides all four outcomes while
    /// they still block, and each keeps an abandoned slot until its thread comes back.
    /// </summary>
    [Fact]
    public async Task Unpacks_run_off_the_callers_stack_and_ones_that_block_their_threads_keep_their_slots_until_they_return()
    {
        // The four unpacks below park four pool threads until this test lets them go. Without a
        // higher floor that is the whole pool of a 4-vCPU runner (the test host holds threads of its
        // own), and on a busy machine new threads come seconds apart: every test running beside
        // this one stalled for 10 s and one timed out on windows CI (#98).
        MessagePickupLive.RaiseThreadPoolFloor(DeliveredMessageUnpacker.FetchConcurrency);
        var time = new ManualTimeProvider();
        using var gate = new ManualResetEventSlim(false);
        using var entered = new SemaphoreSlim(0);
        var onCallersStack = new ConcurrentQueue<bool>();
        var unpacker = new DeliveredMessageUnpacker((text, _) =>
        {
            onCallersStack.Enqueue(_onTestStack);
            entered.Release();
            gate.Wait(); // a resolver that blocks its thread and ignores the token
            return Task.FromResult(Opened(text));
        }, Budget, time);
        // A safety for the red run: before the fix the call below blocks this thread on the gate.
        using var safety = new Timer(_ => gate.Set(), null, RealTime, Timeout.InfiniteTimeSpan);
        try
        {
            Task<IReadOnlyList<DeliveredOutcome>> batch;
            _onTestStack = true;
            try
            {
                batch = unpacker.UnpackAllAsync(Payloads(DeliveredMessageUnpacker.FetchConcurrency, "mallory-"), "request-1", default);
            }
            finally
            {
                _onTestStack = false;
            }
            gate.IsSet.Should().BeFalse("the call returned while every unpack still blocks");

            for (var i = 0; i < DeliveredMessageUnpacker.FetchConcurrency; i++)
                (await entered.WaitAsync(RealTime)).Should().BeTrue();
            time.Advance(Budget);

            (await batch.WaitAsync(RealTime)).Should().OnlyContain(o => o.Error is TimeoutException, "the budget decides the outcomes while the resolvers still block");
            unpacker.Abandoned.Should().Be(DeliveredMessageUnpacker.FetchConcurrency, "an unpack that blocks past the budget keeps an abandoned slot");
            onCallersStack.Should().HaveCount(DeliveredMessageUnpacker.FetchConcurrency).And.OnlyContain(c => !c, "no unpack ran on the caller's stack");
        }
        finally
        {
            gate.Set();
        }
        SpinWait.SpinUntil(() => unpacker.Abandoned == 0 && time.ScheduledTimers == 0, RealTime).Should().BeTrue("each slot comes back when its thread returns");
    }

    // ---- ADV3-7 -------------------------------------------------------------------------------

    /// <summary>
    /// The owner's shutdown while a delivery is being opened: the delivery ends at once, cancelled
    /// with the shutdown's token; every host lookup's token is cancelled; one that ignores its token
    /// keeps its abandoned slot until it ends; nothing else is left behind.
    /// </summary>
    [Fact]
    public async Task The_owners_shutdown_ends_a_delivery_being_opened_at_once_and_cancels_every_host_token()
    {
        var time = new ManualTimeProvider();
        using var shutdown = new CancellationTokenSource();
        using var entered = new SemaphoreSlim(0);
        var tokens = new ConcurrentQueue<CancellationToken>();
        var ignoring = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker(async (text, ct) =>
        {
            tokens.Enqueue(ct);
            entered.Release();
            if (text.StartsWith("ignores", StringComparison.Ordinal))
                return await ignoring.Task; // a lookup that ignores its token
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }, Budget, time, shutdown.Token);
        var payloads = new[] { "honours-0", "honours-1", "ignores-2" }.Select(t => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(t)).ToArray();

        var batch = unpacker.UnpackAllAsync(payloads, "request-1", default);
        for (var i = 0; i < payloads.Length; i++)
            (await entered.WaitAsync(RealTime)).Should().BeTrue();
        await shutdown.CancelAsync();

        (await FluentActions.Awaiting(() => batch.WaitAsync(RealTime)).Should().ThrowAsync<OperationCanceledException>())
            .Which.CancellationToken.Should().Be(shutdown.Token);
        tokens.Should().HaveCount(payloads.Length).And.OnlyContain(t => t.IsCancellationRequested, "the host code opening the delivery is told to stop");
        SpinWait.SpinUntil(() => unpacker.Abandoned == 1, RealTime).Should().BeTrue("the lookup that ignores its token keeps its slot");

        ignoring.SetResult(Opened("ignores-2"));
        SpinWait.SpinUntil(() => unpacker.Abandoned == 0 && unpacker.Opening == 0 && time.ScheduledTimers == 0, RealTime).Should().BeTrue();
    }

    /// <summary>A delivery handed over after the shutdown is cancelled at once and runs no host code.</summary>
    [Fact]
    public async Task A_delivery_handed_over_after_the_shutdown_runs_no_host_code()
    {
        var time = new ManualTimeProvider();
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();
        var calls = 0;
        var unpacker = new DeliveredMessageUnpacker((text, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Opened(text));
        }, Budget, time, shutdown.Token);

        await FluentActions.Awaiting(() => unpacker.UnpackAllAsync(Payloads(3, "m-"), "request-1", default).WaitAsync(RealTime))
            .Should().ThrowAsync<OperationCanceledException>();

        Volatile.Read(ref calls).Should().Be(0);
        time.ScheduledTimers.Should().Be(0);
    }

    /// <summary>
    /// Through the real channel: disposing the container while a live stream is opening a delivery
    /// of its drain round ends the stream at once with <see cref="ObjectDisposedException"/> (as
    /// <see cref="PickupClient.ReceiveLiveAsync"/> documents), and the host code opening it sees its
    /// token cancelled. Nothing of the delivery was acknowledged.
    /// </summary>
    [Fact]
    public async Task Disposing_the_container_while_a_live_stream_opens_a_delivery_ends_the_stream_at_once()
    {
        var time = new ManualTimeProvider();
        var send = new FakeSend();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = Create(_w, send, time: time, unpack: async (_, ct) =>
        {
            entered.TrySetResult(ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct); // a DID lookup that honours its token
            throw new InvalidOperationException("unreachable");
        });
        var sentTypes = new ConcurrentQueue<string>();
        send.Respond = (request, options, _) =>
        {
            sentTypes.Enqueue(request.Type);
            // Over the WebSocket the answer arrives on the connection: handed to the channel as the
            // dispatcher would, before the send returns.
            var answer = request.Type switch
            {
                MessagePickup.DeliveryRequestType => MessagePickup.CreateDelivery(_w.Mediator.Did, _w.Local.Did,
                    new[] { new DeliveredMessage("q1", Encoding.UTF8.GetBytes("packed")) }, thid: request.Id),
                _ => _w.Status(request.Id),
            };
            channel.AsCorrelator().OnInbound(CraftedSnapshot.Verified(answer, senderKid: _w.Mediator.Privates[0].Kid, recipientKid: _w.Local.Privates[0].Kid));
            return Task.FromResult(FakeSend.Accepted(options.ServiceEndpointOverride!));
        };
        var pickup = new PickupClient(channel, logger: null);
        var stream = pickup.ReceiveLiveAsync(_w.Mediator.Did, _w.Local.Did, Wss).GetAsyncEnumerator();
        var move = stream.MoveNextAsync().AsTask().Observed();
        try
        {
            var token = await entered.Task.WaitAsync(RealTime);
            await channel.DisposeAsync();

            token.IsCancellationRequested.Should().BeTrue("the container is gone: the host code opening the delivery must be told to stop");
            await FluentActions.Awaiting(() => move.WaitAsync(RealTime)).Should().ThrowAsync<ObjectDisposedException>(
                "a live stream opening a delivery when its container is disposed ends with ObjectDisposedException, not after its budget");
            sentTypes.Should().NotContain(MessagePickup.MessagesReceivedType, "nothing of the delivery was acknowledged");
        }
        finally
        {
            await stream.DisposeAsync();
            time.Advance(Budget);
        }
        channel.LiveStreamCount.Should().Be(0);
        SpinWait.SpinUntil(() => channel.Unpacker.Abandoned == 0 && channel.Unpacker.Opening == 0 && time.ScheduledTimers == 0, RealTime).Should().BeTrue();
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static ReadOnlyMemory<byte>[] Payloads(int count, string prefix)
        => Enumerable.Range(0, count).Select(i => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(prefix + i)).ToArray();

    private static DeliveredUnpack Opened(string text)
    {
        var message = new MessageBuilder().WithId(text).WithType("https://example.org/notes/1.0/note").WithBody(new System.Text.Json.Nodes.JsonObject()).Build();
        var result = new UnpackResult(message, new[] { DidComm.Jose.EnvelopeKind.Plaintext }, false, false, false, false, null, null, null, null, null, null, Array.Empty<string>(), null);
        return new DeliveredUnpack(result, text, 0);
    }
}
