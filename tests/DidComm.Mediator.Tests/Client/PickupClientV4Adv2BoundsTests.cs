using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.TestSupport;
using DidComm.Transports;
using Xunit;
using Xunit.Abstractions;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// Red team ADV-2 for #100 (bounds and resources), recipient role: a 4.0 <c>status</c> whose numbers
/// are beyond 64 bits, the one-way send's <see cref="MediatorClientOptions.MaxPendingRequests"/> slot
/// on every way a send can end (a transport that throws at once, one that never completes and
/// ignores its token, the caller's cancellation, a disposal), and the client's encrypted-form check
/// (#100 F-2) on a delivery of hostile attachments that fill the receive bound (an opt-in probe,
/// <c>DIDCOMM_ADV100_PROBES=1</c>).
/// </summary>
public sealed class PickupClientV4Adv2BoundsTests(ITestOutputHelper output) : IAsyncLifetime
{
    private ClientWorld _w = null!;
    private readonly FakeSend _send = new();
    private readonly ManualTimeProvider _time = new();

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    private string M => _w.Mediator.Did;

    private string L => _w.Local.Did;

    private Message Ack() => MessagePickup40.CreateMessagesReceived(L, M, new[] { "q1" });

    // ---- a status beyond 64 bits ------------------------------------------------------------------

    [Fact]
    public async Task Adv2_a_4_0_status_whose_numbers_are_beyond_64_bits_is_read_clamped_and_costs_nothing_that_depends_on_them()
    {
        var digits = new string('9', 400);
        var exchange = new FakePickupExchange(o => o.MessagePickupVersion = MessagePickupVersion.V4).On(MessagePickup40.StatusRequestType, r =>
        {
            var status = MessagePickup40.CreateStatus(FakePickupExchange.M, FakePickupExchange.L, new PickupStatus40(1), r.Request.Id);
            status.Body!["message_count"] = JsonNode.Parse("1e400");
            status.Body!["total_bytes"] = JsonNode.Parse(digits);
            status.Body!["newest_received_time"] = JsonNode.Parse(digits + "e99999999999");
            status.Body!["oldest_received_time"] = JsonNode.Parse("123456789012345678901234567890.000");
            return FakePickupExchange.Snapshot(status);
        });

        var status = await PickupTest.Client(exchange).GetStatusAsync(FakePickupExchange.M, FakePickupExchange.L);

        status.MessageCount.Should().Be(int.MaxValue);
        status.TotalBytes.Should().Be(long.MaxValue);
        status.NewestReceivedTime.Should().Be(long.MaxValue / 1000, "milliseconds clamped to 64 bits, then rounded down to seconds");
        status.OldestReceivedTime.Should().Be(long.MaxValue / 1000);
    }

    // ---- the one-way send's slot -----------------------------------------------------------------

    [Fact]
    public async Task Adv2_a_transport_that_throws_before_returning_a_task_gives_the_slot_back()
    {
        await using var channel = Create(_w, _send, o => o.MaxPendingRequests = 1, time: _time);
        _send.Respond = (_, _, _) => throw new InvalidOperationException("thrown before any task");

        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(channel.SendOneWayAsync(WebSocketRoute, Ack(), "messages-received", M, L, default)));

        thrown.Should().BeOfType<InvalidOperationException>();
        channel.PendingCount.Should().Be(0);
        _time.ScheduledTimers.Should().Be(0, "the deadline was released");
    }

    [Fact]
    public async Task Adv2_a_transport_that_never_completes_and_ignores_its_token_holds_the_slot_for_one_RequestTimeout_only()
    {
        await using var channel = Create(_w, _send, o => o.MaxPendingRequests = 1, time: _time);
        var never = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _send.Respond = (_, _, _) => never.Task;
        var mark = _time.Mark();

        var first = channel.SendOneWayAsync(WebSocketRoute, Ack(), "messages-received", M, L, default);
        await _time.WaitForTimerAsync(channel.Options.RequestTimeout, mark, PickupTest.Timeout);
        channel.PendingCount.Should().Be(1, "the send in flight holds the one slot");
        var busy = await Record.ExceptionAsync(() => PickupTest.Within(channel.SendOneWayAsync(WebSocketRoute, Ack(), "messages-received", M, L, default)));
        _time.Advance(channel.Options.RequestTimeout).Should().Be(1);
        var timedOut = await Record.ExceptionAsync(() => PickupTest.Within(first));

        busy.Should().BeOfType<MediatorBusyException>();
        timedOut.Should().BeOfType<TimeoutException>();
        channel.PendingCount.Should().Be(0, "the slot ends with the deadline, whatever the transport does");
        _time.ScheduledTimers.Should().Be(0);
        _send.Count.Should().Be(1);
    }

    [Fact]
    public async Task Adv2_the_callers_cancellation_and_a_disposal_each_give_the_slot_back()
    {
        var channel = Create(_w, _send, o => o.MaxPendingRequests = 2, time: _time);
        _send.Respond = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        };
        using var cts = new CancellationTokenSource();
        var cancelled = channel.SendOneWayAsync(WebSocketRoute, Ack(), "messages-received", M, L, cts.Token);
        var disposed = channel.SendOneWayAsync(WebSocketRoute, Ack(), "messages-received", M, L, default);
        SpinWait.SpinUntil(() => _send.Count == 2, PickupTest.Timeout).Should().BeTrue();
        channel.PendingCount.Should().Be(2);

        cts.Cancel();
        var first = await Record.ExceptionAsync(() => PickupTest.Within(cancelled));
        var afterCancel = channel.PendingCount;
        await channel.DisposeAsync();
        var second = await Record.ExceptionAsync(() => PickupTest.Within(disposed));

        first.Should().BeAssignableTo<OperationCanceledException>();
        afterCancel.Should().Be(1);
        second.Should().BeOfType<ObjectDisposedException>();
        channel.PendingCount.Should().Be(0);
        _time.ScheduledTimers.Should().Be(0);
    }

    // ---- F-2 on a delivery of hostile attachments (probe) ------------------------------------------

    [AdvProbeFact]
    public void Adv2_probe_the_clients_encrypted_form_check_on_ten_attachments_that_fill_the_receive_bound()
    {
        // 1 MiB is the default MaxReceiveBytes; base64 makes each attachment 4/3 of its payload.
        var each = (1024 * 1024 / 10) / 4 * 3 - 200;
        var shapes = MessagePickup40Adv2BoundsTests.HostileShapes(each).Where(s => s.Encrypted && s.Bytes.Length >= 16 * 1024).ToArray();
        foreach (var (name, bytes, _) in shapes)
        {
            var attachments = Enumerable.Range(0, 10)
                .Select(i => PickupBodies.ToAttachment(new DeliveredMessage("m" + i, bytes)))
                .ToList();
            _ = DeliveryReader.ReadEntries(attachments, 10); // warm
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            var contents = DeliveryReader.ReadEntries(attachments, 10);
            sw.Stop();
            var alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            output.WriteLine($"{name,-64} 10 × {bytes.Length,7} B: {sw.Elapsed.TotalMilliseconds,7:F2} ms, alloc {alloc,11:N0} B ({(double)alloc / (10L * bytes.Length):F2}× the payloads)");

            contents.Entries.Should().HaveCount(10, name);
            contents.Entries.Should().OnlyContain(e => e.Error == null, name);
            alloc.Should().BeLessThan(10L * bytes.Length * 6, name);
        }
    }
}
