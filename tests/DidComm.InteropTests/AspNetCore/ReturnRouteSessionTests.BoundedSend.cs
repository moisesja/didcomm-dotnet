using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using DidComm.AspNetCore;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #94 (FR-PICK-03) — the size-bounded send of the two session types,
/// <c>TrySendAsync(message, maxPackedBytes, ct)</c>: the bound is checked on the packed envelope
/// (exactly, inclusive), a message over it is neither held nor queued and gives its HTTP slot or
/// WebSocket push slot back, a bound below 1 is refused before the session is touched, and every
/// cause the two-argument send answers <c>false</c> for is <c>NotSent</c> — never <c>TooLarge</c>.
/// The other half of this class pins the two-argument send, which now runs through this overload.
/// </summary>
public sealed partial class ReturnRouteSessionTests
{
    /// <summary>A bound no packed envelope fits in (a JWE is hundreds of bytes), but a valid one.</summary>
    private const int TinyBound = 16;

    // ---- HTTP session -------------------------------------------------------------------------

    [Fact]
    public async Task Http_bounded_send_within_the_bound_is_Sent_and_becomes_the_response()
    {
        var session = NewHttpSession();
        var message = Fixed("fits");
        const int Bound = 64 * 1024;

        (await session.TrySendAsync(message, Bound, default)).Should().Be(ReturnRouteSendResult.Sent);

        session.HasResponse.Should().BeTrue();
        var body = session.TakeResponseAndClose();
        body.Should().NotBeNull();
        body!.Length.Should().BeLessThanOrEqualTo(Bound);
        var unpacked = await _alice.Client.UnpackAsync(Encoding.UTF8.GetString(body));
        unpacked.Message.Id.Should().Be(message.Id);
        unpacked.Authenticated.Should().BeTrue();
        unpacked.SenderKid.Should().StartWith(_serverDid + "#");
        _logs.Entries.Should().NotContain(e => e.Contains("over the caller's bound"));
    }

    [Fact]
    public async Task Http_bounded_send_over_the_bound_is_TooLarge_holds_nothing_and_frees_the_slot()
    {
        var session = NewHttpSession();
        var message = Fixed("too-large");
        var packedBytes = await HttpPackedSizeAsync(message);
        var callsBefore = _keys.Calls;

        (await session.TrySendAsync(message, TinyBound, default)).Should().Be(ReturnRouteSendResult.TooLarge);

        _keys.Calls.Should().BeGreaterThan(callsBefore, "the size is measured on the packed envelope, so the message was packed");
        session.HasResponse.Should().BeFalse("a message over the bound is not held");
        session.Mode.Should().Be(ReturnRouteMode.All, "the session is as the send found it");
        session.Closed.IsCancellationRequested.Should().BeFalse();
        _logs.Records.Should().ContainSingle(r => r.Text.Contains("over the caller's bound"))
            .Which.Should().Match<(LogLevel Level, string Text)>(r =>
                r.Level == LogLevel.Debug
                && r.Text.Contains(message.Id, StringComparison.Ordinal)
                && r.Text.Contains($"is {packedBytes} bytes", StringComparison.Ordinal)
                && r.Text.Contains($"bound of {TinyBound}", StringComparison.Ordinal));

        // The claim was released: the same message, unbounded, takes the slot and is the response.
        (await session.TrySendAsync(message, int.MaxValue, default)).Should().Be(ReturnRouteSendResult.Sent);
        var unpacked = await _alice.Client.UnpackAsync(Encoding.UTF8.GetString(session.TakeResponseAndClose()!));
        unpacked.Message.Id.Should().Be(message.Id);
        NoLeaks();
    }

    [Fact]
    public async Task Http_after_TooLarge_a_smaller_message_is_Sent_under_the_same_bound()
    {
        // What a message-pickup delivery does (FR-PICK-03): the batch is over the budget, so it is
        // trimmed and sent again on the same session under the same budget.
        var small = Fixed("trimmed");
        var large = Fixed("batch", padding: 2048);
        var bound = await HttpPackedSizeAsync(small);
        var session = NewHttpSession();

        (await session.TrySendAsync(large, bound, default)).Should().Be(ReturnRouteSendResult.TooLarge);
        (await session.TrySendAsync(small, bound, default)).Should().Be(ReturnRouteSendResult.Sent);

        var body = session.TakeResponseAndClose()!;
        body.Length.Should().Be(bound);
        (await _alice.Client.UnpackAsync(Encoding.UTF8.GetString(body))).Message.Id.Should().Be(small.Id);
    }

    [Fact]
    public async Task Http_bounded_send_boundary_is_exact_and_inclusive()
    {
        // The packed size of Fixed(..) does not vary between packs (see Fixed), so it can be
        // measured in one session and used as the bound in another.
        var message = Fixed("boundary");
        var packedBytes = await HttpPackedSizeAsync(message);
        (await HttpPackedSizeAsync(message)).Should().Be(packedBytes, "the packed size of a fixed plaintext is the same on every pack");

        var atTheBound = NewHttpSession();
        (await atTheBound.TrySendAsync(message, packedBytes, default))
            .Should().Be(ReturnRouteSendResult.Sent, "the bound is the largest envelope accepted: an envelope of exactly that size fits");
        atTheBound.TakeResponseAndClose()!.Length.Should().Be(packedBytes, "the size compared is the size of the bytes the response carries");

        var oneByteUnder = NewHttpSession();
        (await oneByteUnder.TrySendAsync(message, packedBytes - 1, default))
            .Should().Be(ReturnRouteSendResult.TooLarge, "one byte over the bound is too large");
        oneByteUnder.TakeResponseAndClose().Should().BeNull("nothing is held, so the endpoint answers 202");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task Http_bounded_send_refuses_a_bound_below_one_before_touching_the_session(int maxPackedBytes)
    {
        var session = NewHttpSession();
        var message = ToAlice("bad-bound");
        var send = async () => await session.TrySendAsync(message, maxPackedBytes, default);

        (await send.Should().ThrowAsync<ArgumentOutOfRangeException>()).Which.ParamName.Should().Be("maxPackedBytes");

        _keys.Calls.Should().Be(0, "an invalid bound is refused before any snapshot or crypto");
        session.HasResponse.Should().BeFalse();
        session.Mode.Should().Be(ReturnRouteMode.All);
        session.Closed.IsCancellationRequested.Should().BeFalse();
        (await session.TrySendAsync(message, int.MaxValue, default)).Should().Be(ReturnRouteSendResult.Sent, "the slot was never claimed");

        session.TakeResponseAndClose().Should().NotBeNull();
        await send.Should().ThrowAsync<ArgumentOutOfRangeException>("the bound is validated first, whatever the session's state");
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("slot-filled")]
    [InlineData("wrong-to")]
    [InlineData("two-recipients")]
    [InlineData("wrong-from")]
    [InlineData("missing-from")]
    [InlineData("pack-failed")]
    public async Task Http_bounded_send_answers_NotSent_wherever_the_unbounded_send_answers_false(string cause)
    {
        var session = NewHttpSession();
        var message = ToAlice(cause);
        switch (cause)
        {
            case "closed":
                session.TakeResponseAndClose();
                break;
            case "slot-filled":
                (await session.TrySendAsync(ToAlice("first"), default)).Should().BeTrue();
                break;
            case "wrong-to":
                message = Message(_serverDid, _carol.Did);
                break;
            case "two-recipients":
                message = Message(_serverDid, _alice.Did, _carol.Did);
                break;
            case "wrong-from":
                message = Message(_carol.Did, _alice.Did);
                break;
            case "missing-from":
                message = Message(null, _alice.Did);
                break;
            case "pack-failed":
                _keys.FailNext = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cause), cause, "unknown cause");
        }

        // A one-byte bound: had the message been measured, the answer would be TooLarge.
        (await session.TrySendAsync(message, 1, default))
            .Should().Be(ReturnRouteSendResult.NotSent, "'{0}' is refused as the two-argument send refuses it, and a message that was not packed is never TooLarge", cause);
        _logs.Entries.Should().NotContain(e => e.Contains("over the caller's bound"));
    }

    [Fact]
    public async Task Http_bounded_send_still_packing_when_the_endpoint_takes_the_response_is_NotSent()
    {
        // PA-2 through the size-bounded overload: the pack completes after the endpoint closed the
        // session and took its (empty) response; within its bound or not, it is never Sent.
        var session = NewHttpSession();
        _keys.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var background = session.TrySendAsync(ToAlice("late-push"), int.MaxValue, default).AsTask();
        await _keys.Entered.Task.WaitAsync(Timeout);
        session.TakeResponseAndClose().Should().BeNull("the push had not completed when the response was taken");

        _keys.Gate.SetResult();
        (await background.WaitAsync(Timeout)).Should().Be(ReturnRouteSendResult.NotSent, "a pack that completes after the close is never reported as sent");
        session.HasResponse.Should().BeFalse();
    }

    // ---- WebSocket session --------------------------------------------------------------------

    [Fact]
    public async Task Ws_bounded_send_within_the_bound_is_Sent_and_the_frame_is_written()
    {
        var socket = new FakeWebSocket();
        using var connection = new CancellationTokenSource();
        var session = NewWebSocketSession(socket, connection);
        session.Start();
        var message = Fixed("fits");
        const int Bound = 64 * 1024;

        (await session.TrySendAsync(message, Bound, default)).Should().Be(ReturnRouteSendResult.Sent);

        (await ReturnRouteTestHost.WaitUntilAsync(() => socket.Frames.Count == 1, Timeout)).Should().BeTrue();
        (await session.StopAsync()).Should().BeTrue();
        var frame = socket.Frames.Should().ContainSingle().Which;
        frame.Length.Should().BeLessThanOrEqualTo(Bound);
        var unpacked = await _alice.Client.UnpackAsync(Encoding.UTF8.GetString(frame));
        unpacked.Message.Id.Should().Be(message.Id);
        unpacked.SenderKid.Should().StartWith(_serverDid + "#");
        socket.MessageTypes.Should().Equal(WebSocketMessageType.Binary);
        _logs.Entries.Should().NotContain(e => e.Contains("over the caller's bound"));
    }

    [Fact]
    public async Task Ws_bounded_send_over_the_bound_is_TooLarge_queues_nothing_and_neither_leaks_nor_adds_a_push_slot()
    {
        // The writer is parked in a send, so every slot is observable: MaxPendingPushes + 1 pushes
        // over the bound each take a slot and must give it back, once. Were one kept, a later
        // TooLarge would be NotSent (no free slot). Were one given back twice, the slot count would
        // let an extra push through to the writer queue, and it would be the queue's own depth —
        // the second limit — that refuses it, with a different log line.
        const int MaxPendingPushes = 2;
        var small = Fixed("trimmed");
        var large = Fixed("batch", padding: 2048);
        var bound = await WsPackedSizeAsync(small);
        var largeBytes = await WsPackedSizeAsync(large);

        var socket = new FakeWebSocket { SendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var connection = new CancellationTokenSource();
        var session = NewWebSocketSession(socket, connection, MaxPendingPushes);
        session.Start();
        (await session.TrySendAsync(ToAlice("in-flight"), int.MaxValue, default)).Should().Be(ReturnRouteSendResult.Sent);
        await socket.SendEntered.Task.WaitAsync(Timeout); // the writer took it and is parked inside SendAsync

        for (var i = 0; i <= MaxPendingPushes; i++)
            (await session.TrySendAsync(large, bound, default)).Should().Be(ReturnRouteSendResult.TooLarge, "push {0} over the bound found a free slot, so the ones before it gave theirs back", i);

        session.Mode.Should().Be(ReturnRouteMode.All, "the session is as the sends found it");
        session.IsClosed.Should().BeFalse();
        _logs.Records.Where(r => r.Text.Contains("over the caller's bound")).Should().HaveCount(MaxPendingPushes + 1)
            .And.OnlyContain(r =>
                r.Level == LogLevel.Debug
                && r.Text.Contains(large.Id, StringComparison.Ordinal)
                && r.Text.Contains($"is {largeBytes} bytes", StringComparison.Ordinal)
                && r.Text.Contains($"bound of {bound}", StringComparison.Ordinal));

        // Trim and retry under the same bound (FR-PICK-03): exactly MaxPendingPushes are queued.
        for (var i = 0; i < MaxPendingPushes; i++)
            (await session.TrySendAsync(small, bound, default)).Should().Be(ReturnRouteSendResult.Sent, "slot {0} is free: no push over the bound kept one", i);
        (await session.TrySendAsync(small, bound, default)).Should().Be(ReturnRouteSendResult.NotSent, "the bound on pending pushes still holds");
        _logs.Entries.Should().ContainSingle(e => e.Contains("no free push slot"), "the one extra push was refused by the slot count, before any packing");
        _logs.Entries.Should().NotContain(e => e.Contains("writer queue is full"), "no slot was given back twice, so no push reached a full writer queue");

        // FIFO writer: a frame queued by a TooLarge push would be written before the trimmed ones.
        socket.SendGate.SetResult();
        (await ReturnRouteTestHost.WaitUntilAsync(() => socket.Frames.Count == 1 + MaxPendingPushes, Timeout)).Should().BeTrue();
        (await session.StopAsync()).Should().BeTrue();
        (await FrameTypesAsync(socket)).Should().Equal(TestType("in-flight"), TestType("trimmed"), TestType("trimmed"));
        socket.Frames.Skip(1).Should().OnlyContain(f => f.Length == bound);
        NoLeaks();
    }

    [Fact]
    public async Task Ws_bounded_send_boundary_is_exact_and_inclusive()
    {
        // The packed size of Fixed(..) does not vary between packs (see Fixed), so it can be
        // measured in one session and used as the bound in another.
        var message = Fixed("boundary");
        var packedBytes = await WsPackedSizeAsync(message);
        (await WsPackedSizeAsync(message)).Should().Be(packedBytes, "the packed size of a fixed plaintext is the same on every pack");

        var atTheBound = new FakeWebSocket();
        using (var connection = new CancellationTokenSource())
        {
            var session = NewWebSocketSession(atTheBound, connection);
            session.Start();
            (await session.TrySendAsync(message, packedBytes, default))
                .Should().Be(ReturnRouteSendResult.Sent, "the bound is the largest envelope accepted: an envelope of exactly that size fits");
            (await ReturnRouteTestHost.WaitUntilAsync(() => atTheBound.Frames.Count == 1, Timeout)).Should().BeTrue();
            (await session.StopAsync()).Should().BeTrue();
        }
        atTheBound.Frames.Should().ContainSingle().Which.Length.Should().Be(packedBytes, "the size compared is the size of the frame the writer sends");

        var oneByteUnder = new FakeWebSocket();
        using (var connection = new CancellationTokenSource())
        {
            var session = NewWebSocketSession(oneByteUnder, connection);
            session.Start();
            (await session.TrySendAsync(message, packedBytes - 1, default))
                .Should().Be(ReturnRouteSendResult.TooLarge, "one byte over the bound is too large");

            // FIFO writer: had the push been queued, its frame would be written before the sentinel's.
            (await session.TrySendAsync(ToAlice("sentinel"), default)).Should().BeTrue();
            (await ReturnRouteTestHost.WaitUntilAsync(() => !oneByteUnder.Frames.IsEmpty, Timeout)).Should().BeTrue();
            (await session.StopAsync()).Should().BeTrue();
        }
        (await FrameTypesAsync(oneByteUnder)).Should().Equal(TestType("sentinel"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task Ws_bounded_send_refuses_a_bound_below_one_before_touching_the_session(int maxPackedBytes)
    {
        // One push slot: an invalid call that took it and kept it would make the valid send NotSent.
        var socket = new FakeWebSocket();
        using var connection = new CancellationTokenSource();
        var session = NewWebSocketSession(socket, connection, maxPendingPushes: 1);
        session.Start();
        var message = ToAlice("bad-bound");
        var send = async () => await session.TrySendAsync(message, maxPackedBytes, default);

        (await send.Should().ThrowAsync<ArgumentOutOfRangeException>()).Which.ParamName.Should().Be("maxPackedBytes");
        await send.Should().ThrowAsync<ArgumentOutOfRangeException>();

        _keys.Calls.Should().Be(0, "an invalid bound is refused before any snapshot or crypto");
        session.Mode.Should().Be(ReturnRouteMode.All);
        session.IsClosed.Should().BeFalse();
        (await session.TrySendAsync(message, int.MaxValue, default)).Should().Be(ReturnRouteSendResult.Sent, "no push slot was taken");
        (await ReturnRouteTestHost.WaitUntilAsync(() => socket.Frames.Count == 1, Timeout)).Should().BeTrue();

        (await session.StopAsync()).Should().BeTrue();
        await send.Should().ThrowAsync<ArgumentOutOfRangeException>("the bound is validated first, whatever the session's state");
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("downgraded-none")]
    [InlineData("downgraded-thread")]
    [InlineData("no-free-slot")]
    [InlineData("wrong-to")]
    [InlineData("two-recipients")]
    [InlineData("wrong-from")]
    [InlineData("missing-from")]
    [InlineData("pack-failed")]
    public async Task Ws_bounded_send_answers_NotSent_wherever_the_unbounded_send_answers_false(string cause)
    {
        var socket = new FakeWebSocket { SendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var connection = new CancellationTokenSource();
        var session = NewWebSocketSession(socket, connection, maxPendingPushes: 1);
        session.Start();
        var message = ToAlice(cause);
        switch (cause)
        {
            case "closed":
                session.RequestClose(SessionCloseReason.Unregistered);
                break;
            case "downgraded-none":
                session.ApplyHeader(ReturnRouteHeader.None);
                break;
            case "downgraded-thread":
                session.ApplyHeader(new ReturnRouteHeader(ReturnRouteMode.Thread, "thread-1"));
                break;
            case "no-free-slot":
                // The writer is parked in its send of the first push; the second holds the one slot.
                (await session.TrySendAsync(ToAlice("in-flight"), default)).Should().BeTrue();
                await socket.SendEntered.Task.WaitAsync(Timeout);
                (await session.TrySendAsync(ToAlice("queued"), default)).Should().BeTrue();
                break;
            case "wrong-to":
                message = Message(_serverDid, _carol.Did);
                break;
            case "two-recipients":
                message = Message(_serverDid, _alice.Did, _carol.Did);
                break;
            case "wrong-from":
                message = Message(_carol.Did, _alice.Did);
                break;
            case "missing-from":
                message = Message(null, _alice.Did);
                break;
            case "pack-failed":
                _keys.FailNext = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cause), cause, "unknown cause");
        }

        // A one-byte bound: had the message been measured, the answer would be TooLarge.
        (await session.TrySendAsync(message, 1, default))
            .Should().Be(ReturnRouteSendResult.NotSent, "'{0}' is refused as the two-argument send refuses it, and a message that was not packed is never TooLarge", cause);
        _logs.Entries.Should().NotContain(e => e.Contains("over the caller's bound"));

        socket.SendGate.SetResult();
        (await session.StopAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Ws_bounded_send_downgraded_during_the_pack_is_NotSent_and_gives_its_slot_back()
    {
        // The existing "refused after the pack" cause through the size-bounded overload: the frame
        // is within its bound, but `none` was applied while it was being packed.
        var socket = new FakeWebSocket();
        using var connection = new CancellationTokenSource();
        var session = NewWebSocketSession(socket, connection, maxPendingPushes: 1);
        session.Start();
        var resolverGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _keys.Gate = resolverGate;

        var downgraded = session.TrySendAsync(ToAlice("downgraded-during-pack"), int.MaxValue, default).AsTask();
        await _keys.Entered.Task.WaitAsync(Timeout);
        session.ApplyHeader(ReturnRouteHeader.None);
        _keys.Gate = null;
        resolverGate.SetResult();
        (await downgraded.WaitAsync(Timeout)).Should().Be(ReturnRouteSendResult.NotSent, "the session was downgraded before the frame could be queued");

        session.ApplyHeader(new ReturnRouteHeader(ReturnRouteMode.All, null));
        (await session.TrySendAsync(ToAlice("after"), int.MaxValue, default)).Should().Be(ReturnRouteSendResult.Sent, "the refused push gave its slot back");
        (await ReturnRouteTestHost.WaitUntilAsync(() => socket.Frames.Count == 1, Timeout)).Should().BeTrue();
        (await session.StopAsync()).Should().BeTrue();
        (await FrameTypesAsync(socket)).Should().Equal(TestType("after"));
    }

    // ---- the two-argument send ----------------------------------------------------------------

    [Fact]
    public async Task The_two_argument_send_has_no_size_bound_on_either_session()
    {
        // It is the size-bounded send with int.MaxValue: a large message is still accepted whole.
        const int Padding = 256 * 1024;
        var large = Fixed("large", Padding);

        var http = NewHttpSession();
        (await http.TrySendAsync(large, default)).Should().BeTrue();
        http.TakeResponseAndClose()!.Length.Should().BeGreaterThan(Padding);

        var socket = new FakeWebSocket();
        using var connection = new CancellationTokenSource();
        var ws = NewWebSocketSession(socket, connection);
        ws.Start();
        (await ws.TrySendAsync(large, default)).Should().BeTrue();
        (await ReturnRouteTestHost.WaitUntilAsync(() => socket.Frames.Count == 1, Timeout)).Should().BeTrue();
        (await ws.StopAsync()).Should().BeTrue();
        socket.Frames.Should().ContainSingle().Which.Length.Should().BeGreaterThan(Padding);
        _logs.Entries.Should().NotContain(e => e.Contains("over the caller's bound"));
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>
    /// A message from the server to Alice whose plaintext is the same bytes on every pack: its id
    /// and <c>created_time</c> are explicit, so neither a generated id nor FR-RR-09 stamping (which
    /// keeps a value already set) can change its length. The identities here are did:peer:2 with
    /// X25519 key agreement, so every other part of the authcrypt envelope — ephemeral key, wrapped
    /// key, IV, tag, key ids — has a fixed length too: within one test the packed size of this
    /// message is the same for every pack, which is what lets a test measure it and then use it as
    /// a bound.
    /// </summary>
    private Message Fixed(string name, int padding = 0)
        => new MessageBuilder()
            .WithId("bounded-send-" + name)
            .WithType(TestType(name))
            .WithFrom(_serverDid)
            .WithTo(_alice.Did)
            .WithCreatedTime(1_700_000_000)
            .WithBody(new JsonObject { ["padding"] = new string('x', padding) })
            .Build();

    /// <summary>The packed size of <paramref name="message"/> on an HTTP session: the length of the body an unbounded send holds.</summary>
    private async Task<int> HttpPackedSizeAsync(Message message)
    {
        var session = NewHttpSession();
        (await session.TrySendAsync(message, int.MaxValue, default)).Should().Be(ReturnRouteSendResult.Sent);
        return session.TakeResponseAndClose()!.Length;
    }

    /// <summary>The packed size of <paramref name="message"/> on a WebSocket session: the length of the frame an unbounded send writes.</summary>
    private async Task<int> WsPackedSizeAsync(Message message)
    {
        var socket = new FakeWebSocket();
        using var connection = new CancellationTokenSource();
        var session = NewWebSocketSession(socket, connection);
        session.Start();
        (await session.TrySendAsync(message, int.MaxValue, default)).Should().Be(ReturnRouteSendResult.Sent);
        (await ReturnRouteTestHost.WaitUntilAsync(() => socket.Frames.Count == 1, Timeout)).Should().BeTrue();
        (await session.StopAsync()).Should().BeTrue();
        return socket.Frames.Should().ContainSingle().Which.Length;
    }
}
