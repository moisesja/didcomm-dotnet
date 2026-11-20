using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.ProblemReport;
using DidComm.Protocols.TrustPing;
using DidComm.Threading;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using ThreadState = DidComm.Threading.ThreadState;

// L-014.
using ProblemReportApi = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// End to end through the ASP.NET Core endpoints (HTTP <c>/didcomm</c>, WebSocket <c>/ws</c>): a
/// message whose <c>id</c>, <c>thid</c> or <c>pthid</c> is longer than 512 characters (FR-MSG-02,
/// FR-MSG-11) is refused at unpack in every envelope, before the replay guard, the dispatcher's
/// thread store, the problem-report cascade budget or a handler sees it; one of exactly 512 is
/// accepted and dispatched. The library's packers refuse such ids, so these envelopes are built from
/// plaintext written by hand, as a peer that is not this library can send them.
/// </summary>
public sealed class MessageIdLengthEndpointTests
{
    private const int Max = 512;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const string HttpRejection = "DidComm receive: rejecting inbound envelope (MalformedMessageException, innermost: MalformedMessageException).";
    private const string SocketRejection = "MapDidCommWebSocket: discarding malformed envelope (MalformedMessageException).";

    private readonly ITestOutputHelper _out;

    public MessageIdLengthEndpointTests(ITestOutputHelper output) => _out = output;

    public enum Transport
    {
        Http,
        WebSocket,
    }

    public static TheoryData<string, EnvelopeShape> FieldsAndEnvelopes()
    {
        var data = new TheoryData<string, EnvelopeShape>();
        foreach (var field in new[] { "id", "thid", "pthid" })
        {
            foreach (var shape in new[] { EnvelopeShape.Plaintext, EnvelopeShape.SignedOnly, EnvelopeShape.Anoncrypt, EnvelopeShape.Authcrypt })
                data.Add(field, shape);
        }
        return data;
    }

    public static TheoryData<Transport> Transports => new() { Transport.Http, Transport.WebSocket };

    // ---- 1. every field, every envelope, both transports ----------------------------------------

    [Theory]
    [MemberData(nameof(FieldsAndEnvelopes))]
    public async Task Over_HTTP_each_field_at_512_characters_is_dispatched_and_at_513_answered_400_with_an_empty_body(string field, EnvelopeShape shape)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        var over = Probe(host, field, Value("zqxover" + field, Max + 1), "over");
        var at = Probe(host, field, Value("zqxat" + field, Max), "at");
        var (overPacked, overType) = await host.RawPackAsync(host.Alice, ReturnRouteTestHost.UnvalidatedJson(over), shape);
        var (atPacked, atType) = await host.RawPackAsync(host.Alice, ReturnRouteTestHost.UnvalidatedJson(at), shape);

        var refused = await host.PostAsync(overPacked, contentType: overType);
        var accepted = await host.PostAsync(atPacked, contentType: atType);

        refused.Status.Should().Be(HttpStatusCode.BadRequest);
        refused.Body.Should().BeEmpty();
        accepted.Status.Should().Be(HttpStatusCode.Accepted);
        accepted.Body.Should().BeEmpty();
        host.Recorder.Seen.Should().ContainSingle("only the 512-character message reaches the handler")
            .Which.Message.Should().Match<Message>(m => ValueOf(m, field) == ValueOf(at, field));
        host.Logs.Entries.Where(l => l.StartsWith("DidComm receive: rejecting", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Should().Be(HttpRejection, "the rejection is logged by exception type only");
        LinesCarrying(host, "zqxover" + field, "zqxat" + field).Should().BeEmpty("no log line carries either value");
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(FieldsAndEnvelopes))]
    public async Task Over_WebSocket_each_field_at_512_characters_is_dispatched_and_at_513_dropped_with_nothing_written_and_the_socket_still_serving(string field, EnvelopeShape shape)
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));
        var over = Probe(host, field, Value("zqxover" + field, Max + 1), "over");
        var at = Probe(host, field, Value("zqxat" + field, Max), "at");
        // Authcrypt with return_route all: it opens a session, so its reply is the first frame written
        // unless something was written for an earlier message.
        var sentinel = host.Stamped(host.RecordingMessage(host.Alice, "sentinel").WithReturnRoute(ReturnRouteMode.All)).Build();
        var (overPacked, _) = await host.RawPackAsync(host.Alice, ReturnRouteTestHost.UnvalidatedJson(over), shape);
        var (atPacked, _) = await host.RawPackAsync(host.Alice, ReturnRouteTestHost.UnvalidatedJson(at), shape);
        var socket = await host.ConnectAsync();

        await ReturnRouteTestHost.SendAsync(socket, overPacked);
        await ReturnRouteTestHost.SendAsync(socket, atPacked);
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, sentinel));
        var first = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);

        first.Type.Should().Be(WebSocketMessageType.Text);
        (await host.Alice.Client.UnpackAsync(first.Text)).Message.Thid.Should().Be(sentinel.Id, "nothing was written for the refused message, and the socket still serves the next one");
        host.Recorder.Seen.Select(r => r.Message.Id).Should().Equal(new[] { at.Id, sentinel.Id }, "the 512-character message was dispatched, in order, and the 513-character one was not");
        ValueOf(host.Recorder.Seen.First().Message, field).Should().Be(ValueOf(at, field));
        host.Logs.Entries.Where(l => l.StartsWith("MapDidCommWebSocket: discarding", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Should().Be(SocketRejection, "the drop is logged by exception type only");
        await CloseAndAwaitEndAsync(host, socket);
        LinesCarrying(host, "zqxover" + field, "zqxat" + field).Should().BeEmpty("no log line carries either value");
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- 2. refused before any store, budget, replay guard or handler ---------------------------

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task A_pure_ack_whose_thid_is_over_512_characters_is_refused_before_the_thread_store_sees_it(Transport transport)
    {
        // A pure ACK (an ack header, an empty body) is filed in the thread store before any handler
        // is looked up, so the store is the first thing that would see it.
        var store = new SpyThreadStateStore();
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IThreadStateStore>(store));
        var over = PureAck(Value("zqxackover", Max + 1));
        var at = PureAck(Value("zqxackat", Max));

        var statuses = await DeliverAsync(host, transport, over, at, until: () => store.Keys.Contains(at.Thid!));

        if (transport == Transport.Http)
            statuses.Should().Equal(HttpStatusCode.BadRequest, HttpStatusCode.Accepted);
        store.Keys.Should().Equal(new[] { at.Thid }, "only the 512-character thread id reached the store");
        host.Recorder.Invocations.Should().Be(0);
        LinesCarrying(host, "zqxackover", "zqxackat").Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task An_error_problem_report_whose_pthid_is_over_512_characters_is_refused_before_the_cascade_budget_counts_it(Transport transport)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        var budget = host.App.Services.GetRequiredService<CascadeBudgetStore>();
        var over = ErrorReport(host, Value("zqxbudgetover", Max + 1));
        var at = ErrorReport(host, Value("zqxbudgetat", Max));

        var statuses = await DeliverAsync(host, transport, over, at, until: () => budget.Peek(at.Pthid!).ErrorCount > 0);

        if (transport == Transport.Http)
            statuses.Should().Equal(HttpStatusCode.BadRequest, HttpStatusCode.Accepted);
        budget.Count.Should().Be(1, "only the 512-character pthid was counted");
        budget.Peek(at.Pthid!).ErrorCount.Should().Be(1);
        LinesCarrying(host, "zqxbudgetover", "zqxbudgetat").Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task An_authcrypt_message_in_replay_scope_whose_id_is_600_characters_is_refused_before_the_replay_guard_sees_it(Transport transport)
    {
        // Authcrypt, return_route all and created_time: in replay scope. The guard refuses an id over
        // its own 512 limit too, but as an admission (answered 202, logged as MessageIdTooLong). Unpack
        // refuses it first, so the guard is never asked.
        CountingReplayGuard? guard = null;
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.ConfigureServices = s => s.AddSingleton<IInboundReplayGuard>(sp => guard = new CountingReplayGuard(new InMemoryReplayGuard(new ReplayProtectionOptions())));
        });
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));
        var over = InScope(host, Value("zqxreplayover", 600));
        var at = InScope(host, Value("zqxreplayat", Max));

        var statuses = await DeliverAsync(host, transport, over, at, until: () => host.Recorder.Seen.Any(r => r.Message.Id == at.Id), shape: EnvelopeShape.Authcrypt);

        if (transport == Transport.Http)
            statuses.Should().Equal(HttpStatusCode.BadRequest, HttpStatusCode.OK);
        guard!.Admitted.Select(a => a.MessageId).Should().Equal(new[] { at.Id }, "the guard saw only the 512-character id");
        host.Recorder.Seen.Should().ContainSingle().Which.Message.Id.Should().Be(at.Id);
        host.Logs.Entries.Should().NotContain(l => l.Contains(nameof(ReplayAdmission.MessageIdTooLong), StringComparison.Ordinal));
        LinesCarrying(host, "zqxreplayover", "zqxreplayat").Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(FieldsAndEnvelopes))]
    public async Task A_value_of_512_valid_characters_and_an_invalid_513th_is_refused_for_its_length_in_every_envelope(string field, EnvelopeShape shape)
    {
        // The character check quotes the value (open issue #116), so it must never be the one that
        // refuses a value over the limit.
        await using var host = await ReturnRouteTestHost.StartAsync();
        var poisoned = Probe(host, field, Value("zqxpoison" + field, Max) + "/", "poison");
        var (packed, _) = await host.RawPackAsync(host.Alice, ReturnRouteTestHost.UnvalidatedJson(poisoned), shape);
        var server = host.App.Services.GetRequiredService<DidCommClient>();

        var thrown = await Record.ExceptionAsync(() => server.UnpackAsync(packed));

        thrown.Should().BeOfType<MalformedMessageException>().Which.Message.Should()
            .Contain($"'{field}' exceeds the maximum length of 512 characters")
            .And.NotContain("unreserved")
            .And.NotContain("zqxpoison");
    }

    // ---- 5. the ping-response's size follows the request's thid ---------------------------------

    [Fact]
    public async Task On_a_session_socket_a_ping_with_a_512_character_thid_gets_a_response_within_a_kilobyte_of_a_UUID_thread_and_one_of_513_gets_nothing()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        var uuid = Ping(host, Guid.NewGuid().ToString());
        var over = Ping(host, Value("zqxpingover", Max + 1));
        var at = Ping(host, Value("zqxpingat", Max));
        var socket = await host.ConnectAsync();

        await ReturnRouteTestHost.SendAsync(socket, await host.RawAuthcryptAsync(host.Alice, ReturnRouteTestHost.UnvalidatedJson(uuid)));
        var uuidResponse = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
        await ReturnRouteTestHost.SendAsync(socket, await host.RawAuthcryptAsync(host.Alice, ReturnRouteTestHost.UnvalidatedJson(over)));
        await ReturnRouteTestHost.SendAsync(socket, await host.RawAuthcryptAsync(host.Alice, ReturnRouteTestHost.UnvalidatedJson(at)));
        var atResponse = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);

        (await host.Alice.Client.UnpackAsync(uuidResponse.Text)).Message.Thid.Should().Be(uuid.Thid);
        var answered = (await host.Alice.Client.UnpackAsync(atResponse.Text)).Message;
        answered.Type.Should().Be(TrustPing.ResponseType);
        answered.Thid.Should().Be(at.Thid, "the frame after the uuid ping's answers the 512-character ping: nothing was pushed for the 513-character one");
        AssertWithinAKilobyte(Encoding.UTF8.GetByteCount(uuidResponse.Text), Encoding.UTF8.GetByteCount(atResponse.Text));
        host.Logs.Entries.Where(l => l.StartsWith("MapDidCommWebSocket: discarding", StringComparison.Ordinal)).Should().ContainSingle().Which.Should().Be(SocketRejection);
        await CloseAndAwaitEndAsync(host, socket);
        LinesCarrying(host, "zqxpingover", "zqxpingat").Should().BeEmpty();
    }

    [Fact]
    public async Task Over_HTTP_return_route_a_ping_with_a_512_character_thid_gets_a_response_body_within_a_kilobyte_of_a_UUID_thread_and_one_of_513_gets_400()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        var uuid = Ping(host, Guid.NewGuid().ToString());
        var over = Ping(host, Value("zqxpingover", Max + 1));
        var at = Ping(host, Value("zqxpingat", Max));

        var uuidResult = await host.PostAsync(await host.RawAuthcryptAsync(host.Alice, ReturnRouteTestHost.UnvalidatedJson(uuid)));
        var overResult = await host.PostAsync(await host.RawAuthcryptAsync(host.Alice, ReturnRouteTestHost.UnvalidatedJson(over)));
        var atResult = await host.PostAsync(await host.RawAuthcryptAsync(host.Alice, ReturnRouteTestHost.UnvalidatedJson(at)));

        uuidResult.Status.Should().Be(HttpStatusCode.OK);
        overResult.Status.Should().Be(HttpStatusCode.BadRequest);
        overResult.Body.Should().BeEmpty();
        atResult.Status.Should().Be(HttpStatusCode.OK);
        (await host.Alice.Client.UnpackAsync(uuidResult.BodyText)).Message.Thid.Should().Be(uuid.Thid);
        (await host.Alice.Client.UnpackAsync(atResult.BodyText)).Message.Thid.Should().Be(at.Thid);
        AssertWithinAKilobyte(uuidResult.Body.Length, atResult.Body.Length);
        LinesCarrying(host, "zqxpingover", "zqxpingat").Should().BeEmpty();
    }

    // ---- log values outside the core: the push consumer and STOMP -------------------------------

    [Fact]
    public async Task A_pushed_message_s_id_of_300_characters_is_written_by_its_length_in_the_client_s_log_lines()
    {
        // The client's inbound consumer writes a pushed message's id once its sender is proven; an id
        // up to 512 characters passes unpack, so the line writes it bounded (at most 128 characters).
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteWebSocketClientHarness.Create(host);
        var consumer = client.Services.GetRequiredService<IInboundFrameConsumer>();
        var push = new MessageBuilder()
            .WithId(Value("zqxpushid", 300))
            .WithType(RecordingHandler.ProtocolUri + "/push")
            .WithFrom(host.ServerDid)
            .WithTo(host.Alice.Did)
            .Build();
        // A malformed return_route header: the consumer logs that, by id.
        push.AdditionalHeaders = new Dictionary<string, JsonElement> { [ReturnRouteHeader.HeaderName] = JsonSerializer.SerializeToElement("All") };
        var server = host.App.Services.GetRequiredService<DidCommClient>();
        var packed = await server.PackEncryptedAsync(push, new PackEncryptedOptions(Recipients: new[] { host.Alice.Did }, From: host.ServerDid, Forward: false));

        await consumer.ConsumeAsync(new InboundFrame(client.Endpoint, host.Alice.Did, host.ServerDid, Encoding.UTF8.GetBytes(packed.Message)), default);

        client.Recorder.Invocations.Should().Be(1, "the push was dispatched");
        host.Logs.Entries.Should().Contain("Pushed message (id of 300 characters): malformed return_route header treated as none (FR-RR-01). 1 such drops.");
        LinesCarrying(host, "zqxpushid").Should().BeEmpty();
    }

    [Fact]
    public async Task An_unsupported_STOMP_command_is_not_written_into_the_log()
    {
        // A STOMP command is at most 32 bytes of the peer's text, which can hold a terminal escape.
        const char Esc = (char)27;
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.UseStomp = true);
        var socket = await host.ConnectAsync();

        await socket.SendAsync(Encoding.UTF8.GetBytes("ZQXSTOMP" + Esc + "[31mRED\n\n\0"), WebSocketMessageType.Binary, endOfMessage: true, default);
        var close = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);

        close.Type.Should().Be(WebSocketMessageType.Close);
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.ProtocolError);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.EndedWebSocketConnections == 1, Timeout)).Should().BeTrue("the connection ended");
        host.Logs.Entries.Should().Contain("MapDidCommWebSocket(STOMP): unsupported STOMP command; closing with 1002.");
        host.Logs.Entries.Should().NotContain(l => l.Contains("ZQXSTOMP", StringComparison.Ordinal) || l.Contains(Esc));
        socket.Dispose();
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>Exactly <paramref name="length"/> unreserved characters, starting with <paramref name="marker"/>.</summary>
    private static string Value(string marker, int length) => marker + new string('q', length - marker.Length);

    private static string ValueOf(Message message, string field)
        => field switch { "id" => message.Id, "thid" => message.Thid!, _ => message.Pthid! };

    private static void Set(Message message, string field, string value)
    {
        switch (field)
        {
            case "id": message.Id = value; break;
            case "thid": message.Thid = value; break;
            default: message.Pthid = value; break;
        }
    }

    /// <summary>A recording-protocol message from Alice to the server whose <paramref name="field"/> is <paramref name="value"/> (set after the builder's validation).</summary>
    private static Message Probe(ReturnRouteTestHost host, string field, string value, string name)
    {
        var message = host.RecordingMessage(host.Alice, name).Build();
        Set(message, field, value);
        return message;
    }

    /// <summary>A plaintext pure ACK of a type no handler is registered for, on thread <paramref name="thid"/>.</summary>
    private static Message PureAck(string thid)
    {
        var message = new MessageBuilder().WithType("https://didcomm.org/test-unhandled/1.0/ack").WithAck("acked-1").Build();
        message.Thid = thid;
        return message;
    }

    /// <summary>A plaintext error problem-report from Alice about thread <paramref name="pthid"/>.</summary>
    private static Message ErrorReport(ReturnRouteTestHost host, string pthid)
    {
        var report = ProblemReportApi.Create(from: host.Alice.Did, to: host.ServerDid, code: "e.p.xfer.cant-use-endpoint", pthid: "p");
        report.Pthid = pthid;
        return report;
    }

    /// <summary>An authcrypt-ready recording message with return_route all and created_time (in replay scope) whose id is <paramref name="id"/>.</summary>
    private static Message InScope(ReturnRouteTestHost host, string id)
    {
        var message = host.Stamped(host.RecordingMessage(host.Alice, "replay").WithReturnRoute(ReturnRouteMode.All)).Build();
        message.Id = id;
        return message;
    }

    /// <summary>An authcrypt-ready Trust Ping that asks for a response on the session (return_route all), on thread <paramref name="thid"/>.</summary>
    private static Message Ping(ReturnRouteTestHost host, string thid)
    {
        var ping = host.Stamped(new MessageBuilder()
                .WithType(TrustPing.PingType)
                .WithFrom(host.Alice.Did)
                .WithTo(host.ServerDid)
                .WithBody(new JsonObject { [TrustPing.ResponseRequestedField] = true })
                .WithReturnRoute(ReturnRouteMode.All))
            .Build();
        ping.Thid = thid;
        return ping;
    }

    /// <summary>
    /// Deliver <paramref name="over"/> then <paramref name="at"/> in <paramref name="shape"/>: as two
    /// HTTP posts (their statuses returned), or on one socket, waiting until <paramref name="until"/>
    /// holds (the second message was processed, so the first, on the same socket, was too), then closing it.
    /// </summary>
    private static async Task<IReadOnlyList<HttpStatusCode>> DeliverAsync(
        ReturnRouteTestHost host, Transport transport, Message over, Message at, Func<bool> until, EnvelopeShape shape = EnvelopeShape.Plaintext)
    {
        var (overPacked, overType) = await host.RawPackAsync(host.Alice, ReturnRouteTestHost.UnvalidatedJson(over), shape);
        var (atPacked, atType) = await host.RawPackAsync(host.Alice, ReturnRouteTestHost.UnvalidatedJson(at), shape);
        if (transport == Transport.Http)
        {
            var refused = await host.PostAsync(overPacked, contentType: overType);
            var accepted = await host.PostAsync(atPacked, contentType: atType);
            refused.Body.Should().BeEmpty();
            host.Logs.Entries.Should().ContainSingle(l => l.StartsWith("DidComm receive: rejecting", StringComparison.Ordinal)).Which.Should().Be(HttpRejection);
            return new[] { refused.Status, accepted.Status };
        }

        var socket = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, overPacked);
        await ReturnRouteTestHost.SendAsync(socket, atPacked);
        (await ReturnRouteTestHost.WaitUntilAsync(until, Timeout)).Should().BeTrue("the 512-character message was processed");
        host.Logs.Entries.Should().ContainSingle(l => l.StartsWith("MapDidCommWebSocket: discarding", StringComparison.Ordinal)).Which.Should().Be(SocketRejection);
        await CloseAndAwaitEndAsync(host, socket);
        return Array.Empty<HttpStatusCode>();
    }

    /// <summary>Close the socket and wait for the server's receive loop to end; the hang guard fails the test.</summary>
    private static async Task CloseAndAwaitEndAsync(ReturnRouteTestHost host, WebSocket socket)
    {
        var ended = host.EndedWebSocketConnections;
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.EndedWebSocketConnections > ended, Timeout)).Should().BeTrue("the server's receive loop ended");
        socket.Dispose();
    }

    private void AssertWithinAKilobyte(int uuidBytes, int atBytes)
    {
        var growth = atBytes - uuidBytes;
        _out.WriteLine($"ping-response packed: {uuidBytes:N0} B on a UUID thread, {atBytes:N0} B on a 512-character thread (+{growth:N0} B)");
        growth.Should().BeInRange(Max - 36, 1024,
            "the response carries the 512-character thid ({0} bytes against {1} for a UUID thread) and nothing more", atBytes, uuidBytes);
    }

    private static IReadOnlyList<string> LinesCarrying(ReturnRouteTestHost host, params string[] markers)
        => host.Logs.Entries.Where(l => markers.Any(m => l.Contains(m, StringComparison.Ordinal))).ToList();

    /// <summary>The default thread store, recording every thread id it is asked about.</summary>
    private sealed class SpyThreadStateStore : IThreadStateStore
    {
        private readonly InMemoryThreadStateStore _inner = new();
        private readonly ConcurrentQueue<string> _keys = new();

        public IReadOnlyList<string> Keys => _keys.ToArray();

        public ThreadState GetOrCreate(string thid)
        {
            _keys.Enqueue(thid);
            return _inner.GetOrCreate(thid);
        }

        public ThreadState? Get(string thid)
        {
            _keys.Enqueue(thid);
            return _inner.Get(thid);
        }

        public bool Remove(string thid)
        {
            _keys.Enqueue(thid);
            return _inner.Remove(thid);
        }
    }

    /// <summary>A replay guard that records every admission it is asked for.</summary>
    private sealed class CountingReplayGuard : IInboundReplayGuard
    {
        private readonly IInboundReplayGuard _inner;
        private readonly ConcurrentQueue<(string Peer, string MessageId)> _admitted = new();

        public CountingReplayGuard(IInboundReplayGuard inner) => _inner = inner;

        public IReadOnlyList<(string Peer, string MessageId)> Admitted => _admitted.ToArray();

        public ReplayAdmission TryAdmit(string peerDidSubject, string messageId, long? createdTimeEpochSeconds, DateTimeOffset now)
        {
            _admitted.Enqueue((peerDidSubject, messageId));
            return _inner.TryAdmit(peerDidSubject, messageId, createdTimeEpochSeconds, now);
        }
    }
}
