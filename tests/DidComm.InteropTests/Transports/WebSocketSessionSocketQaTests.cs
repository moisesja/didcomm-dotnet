using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using DataProofsDotnet.Jose;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.WebSocket;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// #92 QA pass — the WebSocket transport's return-route session sockets S(E, L, P) under the
/// interleavings the G8 suite does not drive: concurrent opens for one key, a <c>none</c> queued
/// behind an <c>all</c>, a send racing the peer's close, dispose racing in-flight and queued sends, an
/// in-progress 1009 close and a retire whose close hangs, repeated open/retire cycles (no leak),
/// the inbound cap on fragmented messages, <c>ReceiveInbound</c> off with a consumer, the
/// <c>SendTimeout</c> edge, and a plain send to a peer with a long DID (regression against
/// <c>main</c>). Every wait is a rendezvous or a bounded condition (L-044); every test ends with a
/// log scan for DIDs and endpoints.
/// </summary>
public sealed class WebSocketSessionSocketQaTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly Uri Endpoint = new("ws://agents.r.us/ws");
    private const string Alice = "did:example:alice";
    private const string Carol = "did:example:carol";
    private const string Bob = "did:example:bob";

    // ---- concurrency on one key ----

    [Fact]
    public async Task Concurrent_All_sends_to_one_key_open_exactly_one_session_socket()
    {
        await using var t = QaHarness.Create();
        t.Sockets.OnCreate = s => s.HoldSends();

        var first = t.Transport.SendAsync(Request("all-0", ReturnRouteMode.All), default);
        var socket = await WaitForSocketAsync(t, 0);
        await socket.SendEntered.WaitAsync(Timeout);
        // Called synchronously: each reaches S's gate (held by the first) before SendAsync returns its task.
        var rest = Enumerable.Range(1, 7).Select(i => t.Transport.SendAsync(Request("all-" + i, ReturnRouteMode.All), default)).ToArray();
        rest.Should().OnlyContain(r => !r.IsCompleted, "every all to (E, L, P) waits for S's gate while the first holds it");

        socket.ReleaseSends();
        await first.WaitAsync(Timeout);
        await Task.WhenAll(rest).WaitAsync(Timeout);

        t.Sockets.Count.Should().Be(1, "the first all opened S; the seven queued behind it found it open and reused it");
        socket.Sent.Should().HaveCount(8);
        socket.MaxSendsInFlight.Should().Be(1);
        t.Transport.ActiveSessionSocketCount.Should().Be(1);
        t.Transport.LiveSessionSocketCount.Should().Be(1);
        t.ShouldNotLeakIdentities();
    }

    [Fact]
    public async Task A_None_queued_behind_an_All_retires_the_session_and_later_sends_use_the_pool()
    {
        await using var t = QaHarness.Create();
        t.Sockets.OnCreate = s =>
        {
            if (s.Index == 0)
                s.HoldSends();
        };

        var all = t.Transport.SendAsync(Request("all", ReturnRouteMode.All), default);
        var session = await WaitForSocketAsync(t, 0);
        await session.SendEntered.WaitAsync(Timeout);
        var none = t.Transport.SendAsync(Request("none", ReturnRouteMode.None), default);
        none.IsCompleted.Should().BeFalse("S is published before its first send, so the none routes to S and waits on its gate");

        session.ReleaseSends();
        (await all.WaitAsync(Timeout)).Accepted.Should().BeTrue();
        (await none.WaitAsync(Timeout)).Accepted.Should().BeTrue();

        session.Sent.Select(Text).Should().Equal("all", "none");
        session.CloseOutputStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        t.Transport.ActiveSessionSocketCount.Should().Be(0);
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 0, Timeout)).Should().BeTrue();

        await t.Transport.SendAsync(Request("plain"), default).WaitAsync(Timeout);
        t.Sockets.Count.Should().Be(2, "after the none returned, a header-less send uses the pooled socket");
        t.Sockets.Sockets[1].Sent.Select(Text).Should().Equal("plain");
        t.Transport.ActiveSessionSocketCount.Should().Be(0);
        t.Disconnected.Should().ContainSingle().Which.Should().Be(Endpoint);
        t.ShouldNotLeakIdentities();
    }

    [Fact]
    public async Task A_send_racing_the_peers_close_is_accepted_and_the_close_is_answered_after_it()
    {
        await using var t = QaHarness.Create();
        await t.Transport.SendAsync(Request("all", ReturnRouteMode.All), default).WaitAsync(Timeout);
        var session = t.Sockets.Sockets[0];
        t.Events.Clear();

        session.HoldSends();
        var racing = t.Transport.SendAsync(Request("racing"), default);
        await session.SendEntered.WaitAsync(Timeout);
        // The receiver evicts the session while the send is on the wire.
        session.PushClose(WebSocketCloseStatus.PolicyViolation, "evicted");
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.ActiveSessionSocketCount == 0, Timeout))
            .Should().BeTrue("the loop saw the close and stopped routing to S at once");
        racing.IsCompleted.Should().BeFalse("the send still holds S's gate");
        session.CloseOutputStatus.Should().BeNull("the loop answers the close only under S's gate, after the send");

        session.ReleaseSends();
        (await racing.WaitAsync(Timeout)).Accepted.Should().BeTrue("a frame may still be sent after the peer's close frame was received");
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 0, Timeout)).Should().BeTrue();

        session.Sent.Select(Text).Should().Equal("all", "racing");
        session.CloseOutputStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        session.CloseOutputDescription.Should().Be("return-route session closed");
        session.Disposed.Should().BeTrue();
        t.Events.Should().NotContain(WebSocketLifecycleEventKind.SendFailed);
        t.Disconnected.Should().ContainSingle().Which.Should().Be(Endpoint);

        await t.Transport.SendAsync(Request("after"), default).WaitAsync(Timeout);
        t.Sockets.Count.Should().Be(2, "with S gone, a header-less send uses the pooled socket");
        await t.Transport.SendAsync(Request("again", ReturnRouteMode.All), default).WaitAsync(Timeout);
        t.Sockets.Count.Should().Be(3, "a later all opens a fresh session socket");
        t.Sockets.Sockets[2].Sent.Select(Text).Should().Equal("again");
        t.ShouldNotLeakIdentities();
    }

    [Fact]
    public async Task An_All_whose_send_fails_on_S_reopens_a_fresh_session_on_retry()
    {
        await using var t = QaHarness.Create(o =>
        {
            o.MaxReconnectAttempts = 1;
            o.ReconnectBaseDelay = TimeSpan.FromMilliseconds(1);
            o.ReconnectMaxDelay = TimeSpan.FromMilliseconds(1);
        });
        await t.Transport.SendAsync(Request("all-1", ReturnRouteMode.All), default).WaitAsync(Timeout);
        var old = t.Sockets.Sockets[0];

        old.FailNextSend();
        (await t.Transport.SendAsync(Request("all-2", ReturnRouteMode.All), default).WaitAsync(Timeout)).Accepted.Should().BeTrue();

        t.Sockets.Count.Should().Be(2);
        old.Disposed.Should().BeTrue();
        t.Sockets.Sockets[1].Sent.Select(Text).Should().Equal("all-2");
        t.Transport.ActiveSessionSocketCount.Should().Be(1);
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 1, Timeout)).Should().BeTrue("the failed socket's loop ended; only the fresh one is live");
        t.ShouldNotLeakIdentities();
    }

    // ---- dispose races ----

    [Fact]
    public async Task Dispose_racing_in_flight_and_queued_sends_drains_them_refuses_the_rest_and_leaves_nothing_open()
    {
        var t = QaHarness.Create(o =>
        {
            o.ConnectTimeout = TimeSpan.FromSeconds(2);
            o.SendTimeout = TimeSpan.FromSeconds(5);
        });
        try
        {
            await t.Transport.SendAsync(Request("a0", ReturnRouteMode.All), default).WaitAsync(Timeout);
            await t.Transport.SendAsync(Request("c0", ReturnRouteMode.All, local: Carol), default).WaitAsync(Timeout);
            await t.Transport.SendAsync(Bare("p0"), default).WaitAsync(Timeout);
            var (sa, sc, pool) = (t.Sockets.Sockets[0], t.Sockets.Sockets[1], t.Sockets.Sockets[2]);
            foreach (var s in new[] { sa, sc, pool })
                s.HoldSends();

            // In flight: each holds its key's gate, parked inside the socket's send.
            var inFlight = new[]
            {
                t.Transport.SendAsync(Request("a1"), default),
                t.Transport.SendAsync(Request("c-none", ReturnRouteMode.None, local: Carol), default),
                t.Transport.SendAsync(Bare("p1"), default),
            };
            await Task.WhenAll(sa.SendEntered, sc.SendEntered, pool.SendEntered).WaitAsync(Timeout);
            // Queued behind them on the same gates.
            var queued = new[]
            {
                t.Transport.SendAsync(Request("a2"), default),
                t.Transport.SendAsync(Bare("p2"), default),
            };

            var watch = Stopwatch.StartNew();
            var dispose = t.Transport.DisposeAsync().AsTask();
            dispose.IsCompleted.Should().BeFalse("dispose drains the three held gates");
            await ((Func<Task>)(() => t.Transport.SendAsync(Request("late", ReturnRouteMode.All), default))).Should().ThrowAsync<ObjectDisposedException>();

            foreach (var s in new[] { sa, sc, pool })
                s.ReleaseSends();
            await dispose.WaitAsync(TimeSpan.FromSeconds(30));
            watch.Stop();

            foreach (var send in inFlight)
                (await send.WaitAsync(Timeout)).Accepted.Should().BeTrue("a send that held its gate when dispose began is drained");
            foreach (var send in queued)
                await ((Func<Task>)(() => send.WaitAsync(Timeout))).Should().ThrowAsync<ObjectDisposedException>("a send that had not won its gate never runs after dispose began");

            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2 + 5 + 5 + 5 + 3));
            t.Sockets.Sockets.Should().OnlyContain(s => s.Disposed);
            t.Sockets.Count.Should().Be(3, "nothing was opened after dispose began");
            sa.Sent.Select(Text).Should().Equal("a0", "a1");
            sc.Sent.Select(Text).Should().Equal("c0", "c-none");
            pool.Sent.Select(Text).Should().Equal("p0", "p1");
            t.Transport.ActiveSessionSocketCount.Should().Be(0);
            (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 0, Timeout)).Should().BeTrue();
            t.Disconnected.Should().HaveCount(3, "each of the three sockets reports its end once").And.OnlyContain(e => e == Endpoint);
            t.ShouldNotLeakIdentities();
        }
        finally
        {
            await t.DisposeAsync();
        }
    }

    [Fact]
    public async Task Dispose_racing_an_in_progress_1009_close_is_bounded_and_leaves_nothing_open()
    {
        const int cap = 16 * 1024;
        var sendTimeout = TimeSpan.FromSeconds(2);
        var connectTimeout = TimeSpan.FromMilliseconds(500);
        var t = QaHarness.Create(o =>
        {
            o.ConnectTimeout = connectTimeout;
            o.SendTimeout = sendTimeout;
        }, maxReceiveBytes: cap);
        try
        {
            t.Sockets.OnCreate = s =>
            {
                s.AnswerCloseOutput = false;
                s.CloseOutputHangs = true;
            };
            await t.Transport.SendAsync(Request("all", ReturnRouteMode.All), default).WaitAsync(Timeout);
            var session = t.Sockets.Sockets[0];

            session.PushFragments(new byte[cap * 2], fragmentSize: 4096);
            (await ReturnRouteTestHost.WaitUntilAsync(() => session.CloseOutputStatus is not null, Timeout))
                .Should().BeTrue("the loop is now inside its 1009 close, under S's gate, and the peer never completes it");

            var watch = Stopwatch.StartNew();
            await t.Transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            watch.Stop();

            watch.Elapsed.Should().BeLessThan(connectTimeout + sendTimeout + sendTimeout + sendTimeout + TimeSpan.FromSeconds(3), "DrainBudget + 2 x CloseBudget");
            session.CloseOutputStatus.Should().Be(WebSocketCloseStatus.MessageTooBig);
            session.Disposed.Should().BeTrue();
            t.Transport.ActiveSessionSocketCount.Should().Be(0);
            (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 0, Timeout)).Should().BeTrue();
            t.Disconnected.Should().ContainSingle().Which.Should().Be(Endpoint);
            t.Consumer.Calls.Should().Be(0);
            t.ShouldNotLeakIdentities();
        }
        finally
        {
            await t.DisposeAsync();
        }
    }

    [Fact]
    public async Task Dispose_racing_a_retire_whose_close_hangs_is_bounded_and_the_None_is_still_accepted()
    {
        var sendTimeout = TimeSpan.FromSeconds(2);
        var connectTimeout = TimeSpan.FromMilliseconds(500);
        var t = QaHarness.Create(o =>
        {
            o.ConnectTimeout = connectTimeout;
            o.SendTimeout = sendTimeout;
        });
        try
        {
            await t.Transport.SendAsync(Request("all", ReturnRouteMode.All), default).WaitAsync(Timeout);
            var session = t.Sockets.Sockets[0];
            session.AnswerCloseOutput = false;
            session.CloseOutputHangs = true;

            var none = t.Transport.SendAsync(Request("none", ReturnRouteMode.None), default);
            (await ReturnRouteTestHost.WaitUntilAsync(() => session.CloseOutputStatus is not null, Timeout))
                .Should().BeTrue("the none was sent and its retire close is hanging under S's gate");
            none.IsCompleted.Should().BeFalse();

            var watch = Stopwatch.StartNew();
            await t.Transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            watch.Stop();

            (await none.WaitAsync(Timeout)).Accepted.Should().BeTrue("the none was delivered; its close outcome does not change that");
            watch.Elapsed.Should().BeLessThan(connectTimeout + sendTimeout + sendTimeout + sendTimeout + TimeSpan.FromSeconds(3));
            session.Disposed.Should().BeTrue();
            (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 0, Timeout)).Should().BeTrue();
            t.Disconnected.Should().ContainSingle().Which.Should().Be(Endpoint);
            t.ShouldNotLeakIdentities();
        }
        finally
        {
            await t.DisposeAsync();
        }
    }

    // ---- resource bounds ----

    [Fact]
    public async Task Repeated_open_and_retire_cycles_leak_no_socket_loop_or_session()
    {
        const int cycles = 20;
        await using var t = QaHarness.Create();

        for (var i = 0; i < cycles; i++)
        {
            await t.Transport.SendAsync(Request("all-" + i, ReturnRouteMode.All, local: i % 2 == 0 ? Alice : Carol), default).WaitAsync(Timeout);
            t.Sockets.Sockets[^1].PushMessage(Utf8("push-" + i));
            await t.Transport.SendAsync(Request("none-" + i, ReturnRouteMode.None, local: i % 2 == 0 ? Alice : Carol), default).WaitAsync(Timeout);
            (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 0, Timeout))
                .Should().BeTrue("cycle {0}: the retired socket's loop ended once the peer answered the close", i);
        }

        t.Sockets.Count.Should().Be(cycles, "one session socket per cycle, and no pooled socket");
        t.Sockets.Sockets.Should().OnlyContain(s => s.Disposed && s.CloseOutputStatus == WebSocketCloseStatus.NormalClosure);
        t.Transport.ActiveSessionSocketCount.Should().Be(0);
        t.Disconnected.Should().HaveCount(cycles);
        t.Consumer.Calls.Should().Be(cycles, "each cycle's push reached the consumer before its socket retired");
        t.Transport.InboundMessagesDropped.Should().Be(0);
        t.ShouldNotLeakIdentities();
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(8 * 1024)]
    [InlineData(16 * 1024)]
    public async Task A_fragmented_push_of_exactly_the_cap_is_surfaced_and_one_more_byte_closes_1009(int fragmentSize)
    {
        const int cap = 16 * 1024;
        await using var t = QaHarness.Create(maxReceiveBytes: cap);
        t.Sockets.OnCreate = s => s.AnswerCloseOutput = false;
        await t.Transport.SendAsync(Request("all", ReturnRouteMode.All), default).WaitAsync(Timeout);
        var session = t.Sockets.Sockets[0];
        var exact = Enumerable.Range(0, cap).Select(i => (byte)(i % 251)).ToArray();

        session.PushFragments(exact, fragmentSize);
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Consumer.Frames.Count == 1, Timeout)).Should().BeTrue();
        t.Consumer.Frames.Single().Payload.ToArray().Should().Equal(exact, "the fragments were reassembled into one message of exactly the cap");
        session.CloseOutputStatus.Should().BeNull();

        session.PushFragments(new byte[cap + 1], fragmentSize);
        (await ReturnRouteTestHost.WaitUntilAsync(() => session.CloseOutputStatus is not null, Timeout)).Should().BeTrue();
        session.CloseOutputStatus.Should().Be(WebSocketCloseStatus.MessageTooBig);
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.InboundMessagesDropped == 1, Timeout)).Should().BeTrue();
        t.Consumer.Calls.Should().Be(1, "the oversized message never reached the consumer");
        t.Transport.ActiveSessionSocketCount.Should().Be(0);
        t.ShouldNotLeakIdentities();
    }

    [Fact]
    public async Task With_ReceiveInbound_off_a_supplied_consumer_is_never_called_and_the_peer_close_is_still_answered()
    {
        await using var t = QaHarness.Create(o => o.ReceiveInbound = false);
        t.Logs.Records.Should().ContainSingle(r => r.Level == LogLevel.Information && r.Text.Contains("ReceiveInbound is off", StringComparison.Ordinal));

        await t.Transport.SendAsync(Request("all", ReturnRouteMode.All), default).WaitAsync(Timeout);
        await t.Transport.SendAsync(Request("all", ReturnRouteMode.All, local: Carol), default).WaitAsync(Timeout);
        var session = t.Sockets.Sockets[0];
        session.PushMessage(Utf8("one"));
        session.PushFragments(new byte[20_000], 4096);
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.InboundMessagesDropped == 2, Timeout)).Should().BeTrue();
        session.PushClose();
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 1, Timeout)).Should().BeTrue();

        t.Consumer.Calls.Should().Be(0);
        session.CloseOutputStatus.Should().Be(WebSocketCloseStatus.NormalClosure, "the loop still completes the close handshake");
        t.Logs.Records.Count(r => r.Text.Contains("are discarded (WebSocketTransportOptions.ReceiveInbound is off)", StringComparison.Ordinal))
            .Should().Be(1, "the discard notice is logged once, not once per socket");
        t.ShouldNotLeakIdentities();
    }

    private static readonly AsyncLocal<string?> Ambient = new();

    [Fact]
    public async Task The_receive_loop_does_not_carry_the_opening_callers_ambient_context()
    {
        // E5: the loop lives as long as the socket, so it must not hold the sending caller's
        // ExecutionContext (async locals, activity) for that long.
        await using var t = QaHarness.Create();
        var seen = new ConcurrentQueue<string>();
        t.Consumer.Behavior = (_, _, _) =>
        {
            seen.Enqueue(Ambient.Value ?? "<none>");
            return ValueTask.CompletedTask;
        };

        Ambient.Value = "opening caller";
        await t.Transport.SendAsync(Request("all", ReturnRouteMode.All), default).WaitAsync(Timeout);
        Ambient.Value = null;
        t.Sockets.Sockets[0].PushMessage(Utf8("push"));

        (await ReturnRouteTestHost.WaitUntilAsync(() => seen.Count == 1, Timeout)).Should().BeTrue();
        seen.Should().Equal("<none>");
    }

    // ---- defects ----

    [Fact]
    public async Task Retire_with_a_finite_SendTimeout_surfaces_messages_already_on_their_way()
    {
        // The passing counterpart of QA-F01: the retire backstop is one SendTimeout away.
        await AssertRetireSurfacesInFlightPushesAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task QA_F01_Retire_with_an_infinite_SendTimeout_still_surfaces_messages_already_on_their_way()
    {
        // Timeout.InfiniteTimeSpan is a legal SendTimeout (sends use CancelAfter, which accepts it). It
        // used to map the retire's close grace to zero, as dispose's budget does: the close frame went
        // out with an already-cancelled token and the backstop fired at once, so the loop stopped
        // before the messages the peer pushed ahead of the none were read. The non-dispose paths now
        // get a finite grace (30 s when SendTimeout is infinite or not positive).
        await AssertRetireSurfacesInFlightPushesAsync(System.Threading.Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public async Task QA_F02_A_header_less_send_to_a_peer_with_a_long_DID_is_still_delivered_over_the_pool()
    {
        // On main, SendAsync built a TransportRequest without identities, so a direct authcrypt send to a
        // recipient whose DID is longer than 2048 characters (accepted by the facade up to 4096, and
        // packable: FR-API-09 keeps an over-long recipient kid in the envelope) was delivered over the
        // pooled socket. #92 first set PeerDid on every direct authcrypt send, and the WebSocket
        // transport refused any PeerDid over 2048 characters, even for a send that asks for no return
        // route. Now the facade sets no identities for such a peer, and the transport refuses an
        // invalid identity only on a send that would open a session socket.
        var longBob = "did:example:" + new string('b', 2100 - "did:example:".Length);
        var aliceKey = Key(Alice + "#x1");
        var bobKey = Key(longBob + "#x1");
        var resolver = new DocResolver(Doc(Alice, aliceKey.Public), Doc(longBob, bobKey.Public));
        await using var t = QaHarness.Create();
        var client = new DidCommClient(
            new InMemorySecretsResolver(new[] { aliceKey.Private }),
            new NetDidKeyService(resolver),
            new NoServices(),
            new TransportRouter(new IDidCommTransport[] { t.Transport }),
            new DidCommOptions());
        var message = new MessageBuilder().WithType("https://example.com/protocols/qa/1.0/note").WithFrom(Alice).WithBody(new JsonObject()).Build();

        var result = await client.SendAsync(message, new SendOptions(Recipients: new[] { longBob }, From: Alice, ServiceEndpointOverride: Endpoint));

        result.Transport.Accepted.Should().BeTrue();
        t.Sockets.Count.Should().Be(1);
        t.Transport.ActiveSessionSocketCount.Should().Be(0, "a header-less send never opens a session socket");
    }

    [Theory]
    [InlineData(2048, true)]
    [InlineData(2049, false)]
    public async Task QA_F02_The_facade_sets_identities_only_when_both_DIDs_are_within_2048_characters(int peerLength, bool carriesIdentities)
    {
        // A peer whose DID is longer than FR-API-09's 2048 characters can never pass the identity rule:
        // its key ids fail the receive path's DID-part bound. So the send carries no identities, uses
        // no session socket and reads no in-band response, for every return-route mode.
        var peer = "did:example:" + new string('b', peerLength - "did:example:".Length);
        var aliceKey = Key(Alice + "#x1");
        var peerKey = Key(peer + "#x1");
        var transport = new CapturingTransport();
        var client = new DidCommClient(
            new InMemorySecretsResolver(new[] { aliceKey.Private }),
            new NetDidKeyService(new DocResolver(Doc(Alice, aliceKey.Public), Doc(peer, peerKey.Public))),
            new NoServices(),
            new TransportRouter(new IDidCommTransport[] { transport }),
            new DidCommOptions());
        var message = new MessageBuilder().WithType("https://example.com/protocols/qa/1.0/note").WithFrom(Alice).WithBody(new JsonObject()).Build();

        foreach (var mode in new ReturnRouteMode?[] { null, ReturnRouteMode.None, ReturnRouteMode.All })
        {
            var result = await client.SendAsync(message, new SendOptions(Recipients: new[] { peer }, From: Alice, ServiceEndpointOverride: Endpoint) { ReturnRoute = mode });
            result.Transport.Accepted.Should().BeTrue();
            result.Returned.Should().BeNull();
        }

        transport.Requests.Should().HaveCount(3);
        foreach (var request in transport.Requests)
        {
            if (carriesIdentities)
            {
                request.LocalDid.Should().Be(Alice);
                request.PeerDid.Should().Be(peer);
            }
            else
            {
                request.LocalDid.Should().BeNull("a peer DID over 2048 characters can never be verified, so no identity is sent");
                request.PeerDid.Should().BeNull();
                request.MaxResponseBytes.Should().BeNull("no in-band response is read for a reply that could never pass the identity rule");
            }
        }
    }

    public static TheoryData<string> InvalidIdentities() => new() { "2049 characters", "space", "not a DID", "line feed" };

    [Theory]
    [MemberData(nameof(InvalidIdentities))]
    public async Task QA_F02_A_send_that_opens_no_session_treats_invalid_identities_as_absent_and_uses_the_pool(string @case)
    {
        var invalid = @case switch
        {
            "2049 characters" => "did:example:" + new string('b', 2049 - "did:example:".Length),
            "space" => "did:example:b ob",
            "not a DID" => "mailto:bob@example.com",
            _ => "did:example:bob\ndid:example:mallory",
        };
        await using var t = QaHarness.Create();

        var sends = new[]
        {
            Request("header-less peer", peer: invalid),
            Request("none peer", ReturnRouteMode.None, peer: invalid),
            Request("header-less local", local: invalid),
            Request("none local", ReturnRouteMode.None, local: invalid),
        };
        foreach (var request in sends)
            (await t.Transport.SendAsync(request, default)).Accepted.Should().BeTrue(@case);

        t.Sockets.Count.Should().Be(1, "every send used the one pooled socket: no session socket can exist under an invalid key");
        t.Sockets.Sockets[0].Sent.Select(Text).Should().Equal("header-less peer", "none peer", "header-less local", "none local");
        t.Transport.ActiveSessionSocketCount.Should().Be(0);
        t.Transport.LiveSessionSocketCount.Should().Be(0);
        var open = () => t.Transport.SendAsync(Request("all", ReturnRouteMode.All, peer: invalid), default);
        (await open.Should().ThrowAsync<ArgumentException>("a send that would open a session socket still refuses it")).Which.Message.Should().NotContain("mallory");
        t.Sockets.Count.Should().Be(1);
        t.ShouldNotLeakIdentities();
    }

    [Fact]
    public async Task QA_F02_Under_STOMP_invalid_identities_never_throw()
    {
        // No session socket exists under STOMP, so the identities are never used as a key.
        var connected = DidComm.Transports.Stomp.StompFrameCodec.Encode(new DidComm.Transports.Stomp.StompFrame(
            "CONNECTED", new[] { KeyValuePair.Create("version", "1.2") }, ReadOnlyMemory<byte>.Empty));
        await using var t = QaHarness.Create(o => o.UseStomp = true);
        t.Sockets.OnCreate = s => s.PushMessage(connected);
        var invalid = "did:example:" + new string('b', 2049 - "did:example:".Length);

        foreach (var mode in new ReturnRouteMode?[] { null, ReturnRouteMode.None, ReturnRouteMode.All, ReturnRouteMode.Thread })
        {
            (await t.Transport.SendAsync(Request("stomp", mode, peer: invalid), default)).Accepted.Should().BeTrue(mode?.ToString() ?? "header-less");
            (await t.Transport.SendAsync(Request("stomp", mode, local: "did:example:al ice"), default)).Accepted.Should().BeTrue(mode?.ToString() ?? "header-less");
        }

        t.Sockets.Count.Should().Be(1, "every send used the pooled STOMP socket");
        t.Transport.ActiveSessionSocketCount.Should().Be(0);
        t.ShouldNotLeakIdentities();
    }

    // ---- helpers ----

    private static async Task AssertRetireSurfacesInFlightPushesAsync(TimeSpan sendTimeout)
    {
        await using var t = QaHarness.Create(o => o.SendTimeout = sendTimeout);
        t.Sockets.OnCreate = s => s.AnswerCloseOutput = false; // the peer's close frame is still on its way
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.Consumer.Behavior = async (_, n, _) =>
        {
            if (n == 1)
            {
                parked.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }
        };
        await t.Transport.SendAsync(Request("all", ReturnRouteMode.All), default).WaitAsync(Timeout);
        var session = t.Sockets.Sockets[0];

        // Two pushes the peer sent before it read our none: the loop holds the first in the consumer.
        session.PushMessage(Utf8("first"));
        session.PushMessage(Utf8("second"));
        await parked.Task.WaitAsync(Timeout);
        (await t.Transport.SendAsync(Request("none", ReturnRouteMode.None), default).WaitAsync(Timeout)).Accepted.Should().BeTrue();
        session.CloseOutputStatus.Should().Be(WebSocketCloseStatus.NormalClosure);

        release.TrySetResult();
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Consumer.Frames.Count == 2, Timeout))
            .Should().BeTrue("a retiring socket keeps surfacing the messages already on their way until the peer's close frame");
        t.Consumer.Frames.Select(f => Text(f.Payload)).Should().Equal("first", "second");
        session.PushClose();
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 0, Timeout)).Should().BeTrue();
    }

    private static TransportRequest Request(string text, ReturnRouteMode? mode = null, string local = Alice, string peer = Bob, Uri? endpoint = null)
        => new(endpoint ?? Endpoint, Utf8(text), MediaTypes.Encrypted) { ReturnRoute = mode, LocalDid = local, PeerDid = peer };

    private static TransportRequest Bare(string text) => new(Endpoint, Utf8(text), MediaTypes.Encrypted);

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

    private static async Task<ScriptedSessionWebSocket> WaitForSocketAsync(QaHarness t, int index)
    {
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Sockets.Count > index, Timeout)).Should().BeTrue();
        return t.Sockets.Sockets[index];
    }

    private static (Jwk Private, Jwk Public) Key(string kid)
    {
        var pair = new DefaultKeyGenerator().Generate(KeyType.X25519);
        return (JwkConversion.ToPrivateJwk(pair, kid), JwkConversion.ToPublicJwk(pair.KeyType, pair.PublicKey, kid));
    }

    private static DidDocument Doc(string did, Jwk keyAgreement)
    {
        var method = new VerificationMethod
        {
            Id = keyAgreement.Kid!,
            Type = "JsonWebKey2020",
            Controller = new Did(did),
            PublicKeyJwk = new Microsoft.IdentityModel.Tokens.JsonWebKey { Kty = keyAgreement.Kty, Crv = keyAgreement.Crv, X = keyAgreement.X },
        };
        return new DidDocument
        {
            Id = new Did(did),
            VerificationMethod = new[] { method },
            KeyAgreement = new[] { VerificationRelationshipEntry.FromEmbedded(method) },
        };
    }

    private sealed class DocResolver : IDidResolver
    {
        private readonly Dictionary<string, DidDocument> _docs;

        public DocResolver(params DidDocument[] docs) => _docs = docs.ToDictionary(d => d.Id.Value!, StringComparer.Ordinal);

        public bool CanResolve(string did) => true;

        public Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(_docs.TryGetValue(did, out var doc)
                ? new DidResolutionResult { DidDocument = doc, ResolutionMetadata = new DidResolutionMetadata() }
                : DidResolutionResult.NotFound(did));
    }

    private sealed class NoServices : IServiceEndpointResolver
    {
        public Task<IReadOnlyList<DidCommServiceInfo>> ResolveAsync(string did, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DidCommServiceInfo>>(Array.Empty<DidCommServiceInfo>());
    }

    /// <summary>Records every request and accepts it; never reads anything back.</summary>
    private sealed class CapturingTransport : IDidCommTransport
    {
        private readonly ConcurrentQueue<TransportRequest> _requests = new();

        public IReadOnlyList<TransportRequest> Requests => _requests.ToArray();

        public string Scheme => "ws";

        public bool CanHandle(Uri endpoint) => true;

        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
        {
            _requests.Enqueue(request);
            return Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: 200));
        }
    }

    /// <summary>A transport over scripted sockets, with a recording consumer, its lifecycle events and its logs.</summary>
    private sealed class QaHarness : IAsyncDisposable
    {
        private readonly ConcurrentQueue<(WebSocketLifecycleEventKind Kind, Uri Endpoint)> _events = new();

        private QaHarness(Action<WebSocketTransportOptions>? configure, int maxReceiveBytes)
        {
            Sockets = new ScriptedSocketFactory();
            Consumer = new RecordingFrameConsumer();
            Logs = new CapturingLoggerProvider();
            var options = new WebSocketTransportOptions
            {
                AllowedSchemes = new[] { "ws" },
                MaxReconnectAttempts = 0,
                ReceiveInbound = true,
                SendTimeout = TimeSpan.FromSeconds(10),
                WebSocketFactory = Sockets.Create,
                Connect = (_, _, _) => Task.CompletedTask,
            };
            configure?.Invoke(options);
            var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
            Transport = new WebSocketDidCommTransport(
                Options.Create(options),
                factory.CreateLogger<WebSocketDidCommTransport>(),
                Options.Create(new DidCommOptions { MaxReceiveBytes = maxReceiveBytes }),
                Consumer);
            Transport.Lifecycle += (_, e) => _events.Enqueue((e.Kind, e.Endpoint));
        }

        public WebSocketDidCommTransport Transport { get; }

        public ScriptedSocketFactory Sockets { get; }

        public RecordingFrameConsumer Consumer { get; }

        public CapturingLoggerProvider Logs { get; }

        public EventLog Events => new(_events);

        public IReadOnlyList<Uri> Disconnected => _events.Where(e => e.Kind == WebSocketLifecycleEventKind.Disconnected).Select(e => e.Endpoint).ToList();

        public static QaHarness Create(Action<WebSocketTransportOptions>? configure = null, int maxReceiveBytes = 256 * 1024)
            => new(configure, maxReceiveBytes);

        /// <summary>No transport log line names a DID or the endpoint (JC-12, E12).</summary>
        public void ShouldNotLeakIdentities()
        {
            Logs.Entries.Should().NotContain(l => l.Contains("did:", StringComparison.Ordinal), "no DID in any transport log line");
            Logs.Entries.Should().NotContain(l => l.Contains("agents.r.us", StringComparison.Ordinal), "no endpoint in any transport log line");
        }

        public ValueTask DisposeAsync() => Transport.DisposeAsync();

        public sealed class EventLog : IEnumerable<WebSocketLifecycleEventKind>
        {
            private readonly ConcurrentQueue<(WebSocketLifecycleEventKind Kind, Uri Endpoint)> _events;

            public EventLog(ConcurrentQueue<(WebSocketLifecycleEventKind Kind, Uri Endpoint)> events) => _events = events;

            public void Clear() => _events.Clear();

            public IEnumerator<WebSocketLifecycleEventKind> GetEnumerator() => _events.Select(e => e.Kind).ToList().GetEnumerator();

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
