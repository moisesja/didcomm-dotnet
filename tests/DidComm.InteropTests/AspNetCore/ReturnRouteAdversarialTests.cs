using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #91 adversarial pass (plan F15 / H5): every attack runs against the real registry-aware endpoints
/// (TestServer, did:peer identities, the real admission pipeline). Each test is the proof of concept
/// for one attack; where the property held it stays as a regression test, where it did not the
/// root cause was fixed and the test pins the fix (plan section K, ADV-*). Every scenario ends with
/// the log scan: no DID, key id or thread value in any captured line (JC-6).
/// </summary>
public sealed class ReturnRouteAdversarialTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // ---- F15(a) / session hijack (ii): another authenticated DID ----------------------------------

    [Fact]
    public async Task F15a_Mallorys_session_never_carries_a_push_meant_for_Alice_and_cannot_push_to_Alice()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        // Mallory's handler invocation tries to push a message addressed to Alice through Mallory's session.
        host.Recorder.Behavior = (message, _) => message.From == host.Mallory.Did
            ? (new[] { PushTo(host, host.Alice.Did, "to-alice-via-mallory", message.Id) }, null)
            : (Array.Empty<Message>(), null);

        var (aliceSocket, aliceSession) = await OpenSessionAsync(host, host.Alice);
        using var aliceOwned = aliceSocket;
        var (mallorySocket, mallorySession) = await OpenSessionAsync(host, host.Mallory);
        using var malloryOwned = mallorySocket;
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 2, Timeout)).Should().BeTrue();

        host.Recorder.Seen.Single(r => r.Message.From == host.Mallory.Did).PushResults
            .Should().Equal(new[] { false }, "a session only carries messages addressed to exactly its own peer (FR-RR-03)");
        mallorySession.PeerDid.Should().Be(host.Mallory.Did);
        host.Registry.Find(host.Mallory.Did).Should().ContainSingle().Which.Should().BeSameAs(mallorySession);
        host.Registry.Find(host.Alice.Did).Should().ContainSingle().Which.Should().BeSameAs(aliceSession);
        (await mallorySession.TrySendAsync(PushTo(host, host.Alice.Did, "direct"), default)).Should().BeFalse();
        var both = new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/both").WithFrom(host.ServerDid).WithTo(host.Mallory.Did, host.Alice.Did).Build();
        (await mallorySession.TrySendAsync(both, default)).Should().BeFalse("two recipients are refused even when one is the peer");

        // A handler that wants to reach Alice finds only Alice's session, and the push is sealed for her.
        var forAlice = PushTo(host, host.Alice.Did, "for-alice");
        foreach (var session in host.Registry.Find(host.Alice.Did))
            (await session.TrySendAsync(forAlice, default)).Should().BeTrue();
        var frame = await ReturnRouteTestHost.ReceiveAsync(aliceSocket, Timeout);
        (await host.Alice.Client.UnpackAsync(frame.Text)).Message.Id.Should().Be(forAlice.Id);
        await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(mallorySocket);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Mallory_on_Alices_session_socket_can_neither_downgrade_nor_upgrade_it_nor_read_its_pushes()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        var (socket, alice) = await OpenSessionAsync(host, host.Alice);
        using var owned = socket;

        // Mallory, authenticated as herself, sends `none` on Alice's socket: dispatched, no session, no downgrade.
        var malloryNone = host.RecordingMessage(host.Mallory, "mallory-none").WithReturnRoute(ReturnRouteMode.None).Build();
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Mallory, malloryNone));
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 2, Timeout)).Should().BeTrue();
        Seen(host, malloryNone).Session.Should().BeNull();
        alice.Mode.Should().Be(ReturnRouteMode.All, "only the session's own (peer, local) pair changes its mode (FR-RR-05)");

        var push = PushTo(host, host.Alice.Did, "still-flowing");
        (await alice.TrySendAsync(push, default)).Should().BeTrue();
        var frame = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
        var readByMallory = async () => await host.Mallory.Client.UnpackAsync(frame.Text);
        await readByMallory.Should().ThrowAsync<Exception>("a push is sealed for the session's peer; the socket holder learns nothing");
        (await host.Alice.Client.UnpackAsync(frame.Text)).Message.Id.Should().Be(push.Id);

        // Alice downgrades; Mallory's `all` on the same socket cannot upgrade it back or open her own.
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, host.RecordingMessage(host.Alice, "alice-none").WithReturnRoute(ReturnRouteMode.None).Build()));
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 3, Timeout)).Should().BeTrue();
        alice.Mode.Should().Be(ReturnRouteMode.None);
        var malloryAll = host.Stamped(host.RecordingMessage(host.Mallory, "mallory-all").WithReturnRoute(ReturnRouteMode.All)).Build();
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Mallory, malloryAll));
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 4, Timeout)).Should().BeTrue();
        Seen(host, malloryAll).Session.Should().BeNull();
        alice.Mode.Should().Be(ReturnRouteMode.None);
        host.Registry.Count.Should().Be(1);
        host.Registry.Find(host.Mallory.Did).Should().BeEmpty("the socket's peer is fixed at creation");
        (await alice.TrySendAsync(PushTo(host, host.Alice.Did, "after-none"), default)).Should().BeFalse();
        await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(socket);
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- session hijack (i): unauthcrypted envelopes ---------------------------------------------

    [Theory]
    [InlineData(EnvelopeShape.Plaintext)]
    [InlineData(EnvelopeShape.Anoncrypt)]
    [InlineData(EnvelopeShape.SignedOnly)]
    [InlineData(EnvelopeShape.AnoncryptOfSigned)]
    public async Task An_unauthcrypted_envelope_with_return_route_never_opens_rebinds_or_downgrades_a_session(EnvelopeShape shape)
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        // Plaintext and anoncrypt can claim any `from`; a signed envelope must name its signer (Mallory).
        var claimed = shape is EnvelopeShape.Plaintext or EnvelopeShape.Anoncrypt ? host.Alice.Did : host.Mallory.Did;
        Message Claiming(string name, ReturnRouteMode mode)
            => host.Stamped(new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/" + name).WithFrom(claimed).WithTo(host.ServerDid).WithReturnRoute(mode)).Build();

        using (var fresh = await host.ConnectAsync())
        {
            var (packed, _) = await host.PackAsync(host.Mallory, Claiming("fresh-all", ReturnRouteMode.All), shape);
            await ReturnRouteTestHost.SendAsync(fresh, packed);
            (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 1, Timeout)).Should().BeTrue();
            host.Recorder.Seen.Single().Session.Should().BeNull();
            host.Registry.Count.Should().Be(0);
            await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(fresh);
        }

        var (socket, alice) = await OpenSessionAsync(host, host.Alice);
        using var owned = socket;
        var none = Claiming("unauth-none", ReturnRouteMode.None);
        await ReturnRouteTestHost.SendAsync(socket, (await host.PackAsync(host.Mallory, none, shape)).Packed);
        var all = Claiming("unauth-all", ReturnRouteMode.All);
        await ReturnRouteTestHost.SendAsync(socket, (await host.PackAsync(host.Mallory, all, shape)).Packed);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 4, Timeout)).Should().BeTrue();

        Seen(host, none).Session.Should().BeNull();
        Seen(host, all).Session.Should().BeNull();
        alice.Mode.Should().Be(ReturnRouteMode.All, "trust comes from the authcrypt layer, not the socket");
        host.Registry.Count.Should().Be(1);
        host.Registry.Find(host.Alice.Did).Should().ContainSingle().Which.Should().BeSameAs(alice);
        var push = PushTo(host, host.Alice.Did, "after-unauth");
        (await alice.TrySendAsync(push, default)).Should().BeTrue();
        var frame = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
        (await host.Alice.Client.UnpackAsync(frame.Text)).Message.Id.Should().Be(push.Id, "it is the first frame: nothing was written for the unauthcrypted messages");
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- F15(b) / session hijack (iii): captured-envelope replay ----------------------------------

    [Fact]
    public async Task F15b_Alices_captured_envelope_replayed_after_her_delivery_is_dropped_on_any_socket_and_over_HTTP()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        var inbound = host.Stamped(host.RecordingMessage(host.Alice, "captured").WithReturnRoute(ReturnRouteMode.All)).Build();
        var captured = await host.AuthcryptAsync(host.Alice, inbound);

        using var aliceSocket = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(aliceSocket, captured);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 1, Timeout)).Should().BeTrue();
        var aliceSession = host.Registry.Find(host.Alice.Did).Single();

        using var mallorySocket = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(mallorySocket, captured);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.DropLines(inbound.Id).Count == 1, Timeout)).Should().BeTrue();
        var http = await host.PostAsync(captured);

        http.Status.Should().Be(HttpStatusCode.Accepted);
        http.Body.Should().BeEmpty();
        host.DropLines(inbound.Id).Should().HaveCount(2).And.AllSatisfy(l => l.Should().Contain(nameof(ReplayAdmission.Duplicate)));
        host.Recorder.Invocations.Should().Be(1);
        host.Registry.Count.Should().Be(1);
        host.Registry.Find(host.Alice.Did).Should().ContainSingle().Which.Should().BeSameAs(aliceSession);
        await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(mallorySocket);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task F15b_Alices_captured_envelope_replayed_before_her_delivery_binds_Alice_and_its_socket_receives_only_ciphertext_for_her()
    {
        // The documented residual (threat model): whoever delivers an authcrypted `all` first owns the
        // socket, but the session is Alice's — everything on it is sealed for Alice. Alice's own late
        // delivery is then the duplicate.
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        var inbound = host.Stamped(host.RecordingMessage(host.Alice, "captured").WithReturnRoute(ReturnRouteMode.All)).Build();
        var captured = await host.AuthcryptAsync(host.Alice, inbound);

        using var mallorySocket = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(mallorySocket, captured);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 1, Timeout)).Should().BeTrue();
        var session = host.Registry.Find(host.Alice.Did).Should().ContainSingle().Subject;
        session.PeerDid.Should().Be(host.Alice.Did, "the peer comes from the verified skid, never from the connection");
        host.Registry.Find(host.Mallory.Did).Should().BeEmpty();

        var push = PushTo(host, host.Alice.Did, "for-alice");
        (await session.TrySendAsync(push, default)).Should().BeTrue();
        var frame = await ReturnRouteTestHost.ReceiveAsync(mallorySocket, Timeout);
        var readByMallory = async () => await host.Mallory.Client.UnpackAsync(frame.Text);
        await readByMallory.Should().ThrowAsync<Exception>();
        (await host.Alice.Client.UnpackAsync(frame.Text)).Message.Id.Should().Be(push.Id);

        using var aliceSocket = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(aliceSocket, captured);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.DropLines(inbound.Id).Count == 1, Timeout)).Should().BeTrue();
        host.DropLines(inbound.Id).Single().Should().Contain(nameof(ReplayAdmission.Duplicate));
        host.Recorder.Invocations.Should().Be(1);
        host.Registry.Count.Should().Be(1);
        await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(aliceSocket);
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- session hijack (iv): skid / from mismatch -----------------------------------------------

    [Fact]
    public async Task An_authcrypt_from_Mallory_whose_plaintext_from_names_Alice_never_opens_a_session_for_Alice()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        var opened = new ConcurrentQueue<ReturnRouteSessionEventArgs>();
        host.Registry.SessionOpened += (_, e) => opened.Enqueue(e);
        var forged = host.Stamped(new MessageBuilder()
            .WithType(RecordingHandler.ProtocolUri + "/forged")
            .WithFrom(host.Alice.Did)
            .WithTo(host.ServerDid)
            .WithReturnRoute(ReturnRouteMode.All)).Build();
        var packed = (await host.Mallory.Client.PackEncryptedAsync(forged, new PackEncryptedOptions(
            Recipients: new[] { host.ServerDid }, From: host.Mallory.Did, Forward: false))).Message;

        var http = await host.PostAsync(packed);
        http.Status.Should().Be(HttpStatusCode.BadRequest, "FR-CONSIST-01 rejects skid/from disagreement at unpack, before admission");
        http.Body.Should().BeEmpty();

        using var socket = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, packed);
        var sentinel = host.RecordingMessage(host.Carol, "sentinel").Build();
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Carol, sentinel));
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 1, Timeout)).Should().BeTrue();

        host.Recorder.Seen.Single().Message.Id.Should().Be(sentinel.Id, "the forged message was never dispatched");
        opened.Should().BeEmpty();
        host.Registry.Count.Should().Be(0);
        host.Registry.Find(host.Alice.Did).Should().BeEmpty();
        await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(socket);
        // The two unpack-rejection lines log the ConsistencyException with its text (FR-API-07: the
        // reason is logged server-side only) — pre-existing, outside #91; every other line is clean.
        host.LeakingLogLines()
            .Where(l => !l.StartsWith("DidComm receive: rejecting inbound envelope", StringComparison.Ordinal)
                        && !l.StartsWith("MapDidCommWebSocket: discarding rejected envelope", StringComparison.Ordinal))
            .Should().BeEmpty();
    }

    // ---- F15(c): return_route_thread --------------------------------------------------------------

    public static TheoryData<string, string, string?> MalformedThreads => new()
    {
        { "over the cap", "\"rrthreadovercap" + new string('t', ReturnRouteHeader.MaxThreadLength + 1 - 15) + "\"", "rrthreadovercap" },
        { "whitespace", "\"   \"", null },
        { "a number", "4242424242", null },
        { "an object", "{\"rr\":\"rrthreadobject\"}", "rrthreadobject" },
        { "a space", "\"rrthread space\"", "rrthread space" },
        { "a slash", "\"rrthread/slash\"", "rrthread/slash" },
        { "a newline", "\"rrthread\\ninjectedline\"", "injectedline" },
        { "a DID-shaped value", "\"did:rrthread:colon\"", "did:rrthread:colon" },
    };

    [Theory]
    [MemberData(nameof(MalformedThreads))]
    public async Task F15c_A_malformed_return_route_thread_is_treated_as_none_never_stored_and_never_logged(string why, string threadJson, string? marker)
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);

        // HTTP: the request is dispatched as `none` — no session, no thread surfaced.
        var overHttp = WithRawThread(host.RecordingMessage(host.Alice, "thread-http").Build(), threadJson);
        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, overHttp));
        response.Status.Should().Be(HttpStatusCode.Accepted, why);
        response.Body.Should().BeEmpty();
        var seen = Seen(host, overHttp);
        seen.Session.Should().BeNull();
        seen.ReturnRoute.Should().Be(ReturnRouteHeader.None, "a thread no valid thid can equal is a malformed header (FR-RR-01)");
        host.Logs.Entries.Should().Contain(e => e.Contains(overHttp.Id) && e.Contains("malformed return_route header"));

        // WebSocket: on Alice's session the malformed header is a `none` (it carries return_route) and
        // its value is not stored on the session.
        var (socket, alice) = await OpenSessionAsync(host, host.Alice);
        using var owned = socket;
        var overSocket = WithRawThread(host.RecordingMessage(host.Alice, "thread-ws").Build(), threadJson);
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, overSocket));
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 3, Timeout)).Should().BeTrue();
        Seen(host, overSocket).ReturnRoute.Should().Be(ReturnRouteHeader.None);
        alice.Mode.Should().Be(ReturnRouteMode.None);
        alice.Thread.Should().BeNull();

        host.LeakingLogLines(marker is null ? Array.Empty<string>() : new[] { marker }).Should().BeEmpty();
    }

    [Fact]
    public async Task F15c_A_megabyte_return_route_thread_under_MaxReceiveBytes_is_treated_as_none()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.MaxReceiveBytes = 4 * 1024 * 1024);
        const string marker = "rrhugethreadmarker";
        var inbound = WithRawThread(host.RecordingMessage(host.Alice, "huge-thread").Build(), "\"" + marker + new string('a', 1 << 20) + "\"");
        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, inbound));

        response.Status.Should().Be(HttpStatusCode.Accepted);
        Seen(host, inbound).ReturnRoute.Should().Be(ReturnRouteHeader.None);
        Seen(host, inbound).Session.Should().BeNull();
        host.LeakingLogLines(marker).Should().BeEmpty();
    }

    // ---- F15(d) / response-body smuggling ---------------------------------------------------------

    [Fact]
    public async Task F15d_Two_pushes_and_a_returned_reply_produce_exactly_one_JWE_and_nothing_after_it()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        Message? first = null;
        host.Recorder.Behavior = (message, _) => (
            new[] { first = PushTo(host, host.Alice.Did, "first", message.Id), PushTo(host, host.Alice.Did, "second", message.Id) },
            PushTo(host, host.Alice.Did, "returned", message.Id));

        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, inbound));

        response.Status.Should().Be(HttpStatusCode.OK);
        response.ContentType.Should().Be("application/didcomm-encrypted+json");
        response.ContentLength.Should().Be(response.Body.Length);
        AssertSingleJwe(response.Body);
        (await host.Alice.Client.UnpackAsync(response.BodyText)).Message.Id.Should().Be(first!.Id);
        host.Recorder.Seen.Single().PushResults.Should().Equal(true, false);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_background_pushes_from_one_handler_fill_the_one_HTTP_slot_exactly_once()
    {
        const int N = 8;
        var state = new AsyncBehaviorState();
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.ConfigureServices = s => s.AddSingleton(state);
            o.ConfigureDidComm = b => b.AddProtocol<AsyncBehaviorHandler>();
        });
        var barrier = new AsyncBarrier(N, Timeout);
        var results = new ConcurrentDictionary<string, bool>();
        state.Behavior = async (message, context, ct) =>
        {
            var session = context.Session!;
            var pushes = Enumerable.Range(0, N).Select(i => PushTo(host, host.Alice.Did, "bg-" + i, message.Id)).ToArray();
            await Task.WhenAll(pushes.Select(p => Task.Run(async () =>
            {
                await barrier.ArriveAndWaitAsync();
                results[p.Id] = await session.TrySendAsync(p, ct);
            })));
            return null;
        };

        var inbound = host.Stamped(new MessageBuilder().WithType(AsyncBehaviorHandler.Uri + "/probe").WithFrom(host.Alice.Did).WithTo(host.ServerDid).WithReturnRoute(ReturnRouteMode.All)).Build();
        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, inbound));

        barrier.Arrived.Should().Be(N, "all pushes were released together, after every one had arrived (L-044)");
        results.Should().HaveCount(N);
        results.Values.Count(r => r).Should().Be(1, "HTTP carries at most one message per request, whoever wins (FR-RR-04)");
        response.Status.Should().Be(HttpStatusCode.OK);
        AssertSingleJwe(response.Body);
        (await host.Alice.Client.UnpackAsync(response.BodyText)).Message.Id.Should().Be(results.Single(r => r.Value).Key);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_logging_provider_that_throws_after_a_successful_push_changes_neither_the_200_nor_the_socket()
    {
        // A host logging provider is host code: it must not change the response (the rule SafeLog
        // states). The last log line after a session push used to be written raw, so a throwing
        // provider turned the 200 carrying the push into the uniform 400 — and tore down the socket.
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.ConfigureServices = s => s.AddSingleton<ILoggerProvider>(new ThrowingLoggerProvider("Protocol dispatch produced a reply"));
        });
        Message? pushed = null;
        host.Recorder.Behavior = (message, _) => (
            new[] { pushed = PushTo(host, host.Alice.Did, "pushed", message.Id) },
            PushTo(host, host.Alice.Did, "returned", message.Id));

        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, inbound));
        response.Status.Should().Be(HttpStatusCode.OK);
        (await host.Alice.Client.UnpackAsync(response.BodyText)).Message.Id.Should().Be(pushed!.Id);

        using var socket = await host.ConnectAsync();
        for (var i = 0; i < 2; i++)
        {
            var message = host.Stamped(host.RecordingMessage(host.Alice, "ws-" + i).WithReturnRoute(ReturnRouteMode.All)).Build();
            await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, message));
            var first = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
            var second = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
            first.Type.Should().Be(WebSocketMessageType.Binary, "message {0}: the connection survives the logging fault", i);
            second.Type.Should().Be(WebSocketMessageType.Binary);
            (await host.Alice.Client.UnpackAsync(first.Text)).Message.Thid.Should().Be(message.Id);
            (await host.Alice.Client.UnpackAsync(second.Text)).Message.Thid.Should().Be(message.Id);
        }
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- F15(e): fresh-DID flood against both bounded stores ---------------------------------------

    [Fact]
    public async Task F15e_A_fresh_DID_flood_keeps_the_first_sessions_and_entries_and_never_exceeds_either_bound()
    {
        const int MaxSessions = 8;
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.ConfigureSessions = s => { s.MaxSessions = MaxSessions; s.MaxSessionsPerPeer = 1; };
            o.ConfigureReplay = r => { r.MaxEntries = 32; r.MaxEntriesPerPeer = 32; };
        });
        var maxCount = 0;
        host.Registry.SessionOpened += (_, _) => InterlockedMax(ref maxCount, host.Registry.Count);
        var peers = await host.CreatePeersAsync(2 * MaxSessions);

        var sockets = new List<WebSocket>();
        var openers = new List<Message>();
        try
        {
            for (var i = 0; i < peers.Count; i++)
            {
                var opener = host.Stamped(host.RecordingMessage(peers[i], "flood").WithReturnRoute(ReturnRouteMode.All)).Build();
                openers.Add(opener);
                var socket = await host.ConnectAsync();
                sockets.Add(socket);
                await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(peers[i], opener));
                (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == i + 1, Timeout)).Should().BeTrue();
                host.Registry.Count.Should().Be(Math.Min(i + 1, MaxSessions));
            }

            maxCount.Should().BeLessThanOrEqualTo(MaxSessions);
            for (var i = 0; i < peers.Count; i++)
            {
                host.Registry.Find(peers[i].Did).Should().HaveCount(i < MaxSessions ? 1 : 0, "peer {0}: a flood of fresh DIDs never evicts an existing DID's session", i);
                var dispatchedWith = Seen(host, openers[i]).Session;
                (dispatchedWith is not null).Should().Be(i < MaxSessions, "peer {0} is dispatched either way, with a session only within the bound", i);
            }

            // The replay guard under the same flood (16 entries so far): three more requests per peer
            // over HTTP saturate MaxEntries = 32; the first peer's opener is still remembered.
            var guard = (InMemoryReplayGuard)host.ReplayGuard;
            foreach (var peer in peers)
                for (var k = 0; k < 3; k++)
                    (await host.PostAsync(await host.AuthcryptAsync(peer, host.Stamped(host.RecordingMessage(peer, "more").WithReturnRoute(ReturnRouteMode.All)).Build())))
                        .Status.Should().Be(HttpStatusCode.Accepted);
            guard.Count.Should().Be(32);
            guard.SaturationCount.Should().BeGreaterThan(0);
            (await host.PostAsync(await host.AuthcryptAsync(peers[0], openers[0]))).Status.Should().Be(HttpStatusCode.Accepted);
            host.DropLines(openers[0].Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.Duplicate));
            host.Registry.Count.Should().Be(MaxSessions, "HTTP sessions are request-scoped and never registered (JC-16)");
        }
        finally
        {
            foreach (var socket in sockets)
                socket.Abort();
        }
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 0, Timeout)).Should().BeTrue();
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- F15(f) / FR-RR-02: forward ----------------------------------------------------------------

    [Fact]
    public async Task F15f_An_authcrypt_forward_carrying_return_route_is_relayed_exactly_like_the_header_less_control()
    {
        var sink = new CapturingForwardSink();
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.ConfigureServices = s => s.AddSingleton<IForwardSink>(sink);
            o.ConfigureDidComm = b => b.AddRoutingMediator();
        });
        host.App.Services.GetRequiredService<InMemoryForwardRecipientRegistry>().Register(host.Carol.Did);
        var opened = new ConcurrentQueue<ReturnRouteSessionEventArgs>();
        host.Registry.SessionOpened += (_, e) => opened.Enqueue(e);

        // The payload for Carol itself asks for return_route: all — the relay cannot read it and must
        // never act on it (FR-RR-02).
        var inner = host.Stamped(new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/inner")
            .WithFrom(host.Alice.Did).WithTo(host.Carol.Did).WithReturnRoute(ReturnRouteMode.All)).Build();
        var payload = (await host.Alice.Client.PackEncryptedAsync(inner, new PackEncryptedOptions(
            Recipients: new[] { host.Carol.Did }, From: host.Alice.Did, Forward: false))).Message;
        Message Forward(bool withHeader)
        {
            var forward = ForwardMessage.Create(host.ServerDid, host.Carol.Did, new[] { payload });
            forward.From = host.Alice.Did;
            if (withHeader)
            {
                forward.AdditionalHeaders ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                forward.AdditionalHeaders[ReturnRouteHeader.HeaderName] = JsonSerializer.SerializeToElement(ReturnRouteHeader.AllValue);
            }
            return forward;
        }

        // The header-less control first, alone, so its relay is the reference.
        var control = await host.PostAsync(await host.AuthcryptAsync(host.Alice, Forward(withHeader: false)));
        control.Status.Should().Be(HttpStatusCode.Accepted);
        var reference = (await sink.WaitForCountAsync(1, Timeout)).Single();

        var withHeader = await host.AuthcryptAsync(host.Alice, Forward(withHeader: true));
        foreach (var packed in new[] { withHeader, withHeader })
        {
            var response = await host.PostAsync(packed);
            response.Status.Should().Be(HttpStatusCode.Accepted);
            response.Body.Should().BeEmpty("a forward never opens a session, so it never gets a 200 body");
        }
        using var socket = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, Forward(withHeader: true)));

        var results = await sink.WaitForCountAsync(4, Timeout);
        await host.App.Services.GetRequiredService<ForwardHandler>().FlushAsync(Timeout);
        results.Should().HaveCount(4, "forward is replay-exempt: the same forward twice is relayed twice (FR-RR-08)");
        reference.NextHop.Should().Be(host.Carol.Did);
        System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(reference.OnwardPacked),
            System.Text.Json.Nodes.JsonNode.Parse(payload)).Should().BeTrue("the control relays the attachment it carried");
        results.Should().AllSatisfy(r =>
        {
            r.NextHop.Should().Be(reference.NextHop);
            r.OnwardPacked.Should().Equal(reference.OnwardPacked, "byte for byte what the header-less control relayed (FR-RR-02)");
        });
        opened.Should().BeEmpty();
        host.Registry.Count.Should().Be(0);
        host.Recorder.Invocations.Should().Be(0, "the relay never dispatches the payload it carries");
        var atCarol = await host.Carol.Client.UnpackAsync(Encoding.UTF8.GetString(results.First().OnwardPacked));
        atCarol.Message.Id.Should().Be(inner.Id);
        ReturnRouteHeader.Read(atCarol.Message).Mode.Should().Be(ReturnRouteMode.All, "the payload's own header is carried untouched, neither honoured nor stripped");
        await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(socket);
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- F15(g): many header-less messages on a session socket -------------------------------------

    [Fact]
    public async Task F15g_A_thousand_header_less_messages_on_a_session_socket_keep_one_session_and_queue_nothing()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        var opened = new ConcurrentQueue<ReturnRouteSessionEventArgs>();
        var closed = new ConcurrentQueue<ReturnRouteSessionEventArgs>();
        host.Registry.SessionOpened += (_, e) => opened.Enqueue(e);
        host.Registry.SessionClosed += (_, e) => closed.Enqueue(e);
        var (socket, session) = await OpenSessionAsync(host, host.Alice);
        using var owned = socket;

        const int N = 1000;
        for (var i = 0; i < N; i++)
            await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, host.RecordingMessage(host.Alice, "bulk").Build()));
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == N + 1, TimeSpan.FromSeconds(90))).Should().BeTrue();

        host.Recorder.Seen.Where(r => r.Message.Type.EndsWith("/bulk", StringComparison.Ordinal))
            .Should().HaveCount(N).And.AllSatisfy(r =>
            {
                r.Session.Should().BeSameAs(session, "a header-less message keeps the session (FR-RR-05)");
                r.ReturnRoute.Should().Be(ReturnRouteHeader.None);
            });
        // Registry events come from the registry's notifier, possibly after the call, so the one expected is awaited.
        (await ReturnRouteTestHost.WaitUntilAsync(() => !opened.IsEmpty, Timeout)).Should().BeTrue();
        opened.Should().ContainSingle();
        closed.Should().BeEmpty();
        host.Registry.Count.Should().Be(1);
        session.Mode.Should().Be(ReturnRouteMode.All);
        var push = PushTo(host, host.Alice.Did, "after-bulk");
        (await session.TrySendAsync(push, default)).Should().BeTrue();
        var frame = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
        (await host.Alice.Client.UnpackAsync(frame.Text)).Message.Id.Should().Be(push.Id, "the writer wrote nothing before it and is still healthy");
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- F15(h) / keep a socket open past eviction -------------------------------------------------

    [Fact]
    public async Task F15h_An_evicted_peer_that_never_answers_the_close_and_keeps_sending_is_disconnected_within_the_grace()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.ConfigureSessions = s => s.MaxSessionsPerPeer = 1;
        });
        long evictedAt = 0;
        host.Registry.SessionClosed += (_, e) =>
        {
            if (e.Reason == SessionCloseReason.Evicted)
                Interlocked.CompareExchange(ref evictedAt, Stopwatch.GetTimestamp(), 0);
        };
        var (first, firstSession) = await OpenSessionAsync(host, host.Alice);
        using var firstOwned = first;
        var endedBefore = host.EndedWebSocketConnections;

        // The first client never reads (so it never sees or answers the close frame) and keeps sending.
        using var stop = new CancellationTokenSource();
        var keepSending = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await ReturnRouteTestHost.SendAsync(first, await host.AuthcryptAsync(host.Alice, host.RecordingMessage(host.Alice, "after-eviction").Build()));
                    await Task.Delay(50, stop.Token);
                }
            }
            catch (Exception)
            {
                // The server ended the connection (or the test stopped the loop).
            }
        });

        var (second, secondSession) = await OpenSessionAsync(host, host.Alice);
        using var secondOwned = second;
        (await ReturnRouteTestHost.WaitUntilAsync(() => Interlocked.Read(ref evictedAt) != 0, Timeout)).Should().BeTrue();
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.EndedWebSocketConnections > endedBefore, TimeSpan.FromSeconds(15))).Should().BeTrue();
        var elapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref evictedAt));
        await stop.CancelAsync();
        await keepSending;

        elapsed.Should().BeLessThan(WebSocketReturnRouteSession.DefaultCloseGrace + TimeSpan.FromSeconds(1), "the eviction backstop ends a connection whose peer never answers (JC-21)");
        firstSession.Closed.IsCancellationRequested.Should().BeTrue();
        ((WebSocketReturnRouteSession)firstSession).WriterStopped.Should().BeTrue();
        host.Registry.Find(host.Alice.Did).Should().ContainSingle().Which.Should().BeSameAs(secondSession);
        host.Registry.Count.Should().Be(1);
        host.Recorder.Seen.Where(r => r.Message.Type.EndsWith("/after-eviction", StringComparison.Ordinal) && r.Session is not null)
            .Should().AllSatisfy(r => r.Session.Should().BeSameAs(firstSession, "only messages handled before the eviction saw the session"));
        (await firstSession.TrySendAsync(PushTo(host, host.Alice.Did, "late"), default)).Should().BeFalse();

        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 0, Timeout)).Should().BeTrue();
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- eviction races: a peer reconnecting in a tight loop ----------------------------------------

    [Fact]
    public async Task A_peer_reconnecting_in_a_tight_loop_never_exceeds_its_bound_and_every_evicted_connection_ends()
    {
        const int N = 10;
        RendezvousSessionRegistry? registry = null;
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.ConfigureServices = s => s.AddSingleton<IReturnRouteSessionRegistry>(_ => registry = new RendezvousSessionRegistry(
                new InMemoryReturnRouteSessionRegistry(new ReturnRouteSessionOptions { MaxSessionsPerPeer = 1 }),
                // The first three registrations park until all three arrived, then race (forced
                // overlap); the rest follow back to back. Few parties: each one parks a pool thread.
                new Rendezvous(3, Timeout)));
        });
        var opened = new ConcurrentQueue<IReturnRouteSession>();
        var maxCount = 0;
        host.Registry.SessionOpened += (_, e) =>
        {
            opened.Enqueue(e.Session);
            InterlockedMax(ref maxCount, host.Registry.Count);
        };
        var unobserved = new ConcurrentQueue<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) =>
        {
            if (e.Exception.ToString().Contains("DidComm.", StringComparison.Ordinal))
                unobserved.Enqueue(e.Exception);
        };
        TaskScheduler.UnobservedTaskException += onUnobserved;
        var sockets = new List<WebSocket>();
        try
        {
            var envelopes = await Task.WhenAll(Enumerable.Range(0, N).Select(i =>
                host.AuthcryptAsync(host.Alice, host.Stamped(host.RecordingMessage(host.Alice, "reconnect-" + i).WithReturnRoute(ReturnRouteMode.All)).Build())));
            foreach (var envelope in envelopes)
            {
                var socket = await host.ConnectAsync();
                sockets.Add(socket);
                await ReturnRouteTestHost.SendAsync(socket, envelope);
            }
            // Half the clients answer the eviction close like a well-behaved peer; the other half never read.
            var answering = sockets.Where((_, i) => i % 2 == 0).Select(AnswerCloseAsync).ToList();

            (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == N, TimeSpan.FromSeconds(20))).Should().BeTrue();
            registry!.Rendezvous.MaxConcurrent.Should().BeGreaterThan(1, "the registrations overlapped (L-044)");
            registry.Registrations.Should().HaveCount(N).And.AllSatisfy(r => r.Result.Outcome.Should().Be(SessionRegistrationOutcome.Registered));
            registry.Registrations.Count(r => r.Result.Evicted is not null).Should().Be(N - 1, "every registration past the first evicted exactly one");
            maxCount.Should().BeLessThanOrEqualTo(1, "the per-peer bound holds at every registration");
            var survivor = host.Registry.Find(host.Alice.Did).Should().ContainSingle().Subject;

            (await ReturnRouteTestHost.WaitUntilAsync(() => host.EndedWebSocketConnections == N - 1,
                WebSocketReturnRouteSession.DefaultCloseGrace + TimeSpan.FromSeconds(5))).Should().BeTrue("every evicted connection ends, answered or not");
            (await ReturnRouteTestHost.WaitUntilAsync(() => opened.Count >= N, Timeout)).Should().BeTrue("every registration raises SessionOpened (from the registry's notifier)");
            opened.Should().HaveCount(N);
            foreach (var session in opened.Where(s => !ReferenceEquals(s, survivor)).Cast<WebSocketReturnRouteSession>())
            {
                session.Closed.IsCancellationRequested.Should().BeTrue();
                session.CloseReason.Should().Be(SessionCloseReason.Evicted);
                session.WriterStopped.Should().BeTrue("no writer task outlives its connection");
            }
            host.ActiveWebSocketConnections.Should().Be(1);
            foreach (var socket in sockets)
                socket.Abort();
            await Task.WhenAll(answering);
            (await ReturnRouteTestHost.WaitUntilAsync(() => host.ActiveWebSocketConnections == 0 && host.Registry.Count == 0, Timeout)).Should().BeTrue();
            host.EndedWebSocketConnections.Should().Be(N);
            ((WebSocketReturnRouteSession)survivor).WriterStopped.Should().BeTrue();
        }
        finally
        {
            foreach (var socket in sockets)
                socket.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            TaskScheduler.UnobservedTaskException -= onUnobserved;
        }
        unobserved.Should().BeEmpty();
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- replay and clock edges at the endpoint ------------------------------------------------------

    [Fact]
    public async Task The_same_message_id_from_two_authenticated_senders_is_admitted_for_each_and_replayed_for_neither()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, message.From!));
        var id = "shared-" + Guid.NewGuid().ToString("N");
        var fromAlice = await host.AuthcryptAsync(host.Alice, host.Stamped(host.RecordingMessage(host.Alice).WithId(id).WithReturnRoute(ReturnRouteMode.All)).Build());
        var fromCarol = await host.AuthcryptAsync(host.Carol, host.Stamped(host.RecordingMessage(host.Carol).WithId(id).WithReturnRoute(ReturnRouteMode.All)).Build());

        (await host.PostAsync(fromAlice)).Status.Should().Be(HttpStatusCode.OK);
        (await host.PostAsync(fromCarol)).Status.Should().Be(HttpStatusCode.OK, "the key is (sender DID subject, id)");
        (await host.PostAsync(fromAlice)).Status.Should().Be(HttpStatusCode.Accepted);
        (await host.PostAsync(fromCarol)).Status.Should().Be(HttpStatusCode.Accepted);

        host.Recorder.Invocations.Should().Be(2);
        host.DropLines(id).Should().HaveCount(2).And.AllSatisfy(l => l.Should().Contain(nameof(ReplayAdmission.Duplicate)));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_server_clock_that_moves_backwards_between_two_deliveries_still_refuses_the_second()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
            o.ConfigureDidComm = b => b.Configure(c => c.ExpiresClockSkew = TimeSpan.FromSeconds(120)));
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));
        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var envelope = await host.AuthcryptAsync(host.Alice, inbound);

        (await host.PostAsync(envelope)).Status.Should().Be(HttpStatusCode.OK);
        host.Clock.Advance(TimeSpan.FromSeconds(-60));
        (await host.PostAsync(envelope)).Status.Should().Be(HttpStatusCode.Accepted);
        host.Clock.Advance(TimeSpan.FromSeconds(-61));
        (await host.PostAsync(envelope)).Status.Should().Be(HttpStatusCode.Accepted);
        host.Clock.Advance(TimeSpan.FromSeconds(121 + 300));
        (await host.PostAsync(envelope)).Status.Should().Be(HttpStatusCode.Accepted);
        host.Clock.Advance(TimeSpan.FromSeconds(121));
        (await host.PostAsync(envelope)).Status.Should().Be(HttpStatusCode.Accepted);

        host.Recorder.Invocations.Should().Be(1);
        host.DropLines(inbound.Id).Select(l => Outcome(l)).Should().Equal(
            nameof(ReplayAdmission.Duplicate),     // back 60 s, inside the skew: still remembered
            nameof(ReplayAdmission.FutureDated),   // back 121 s: refused on its timestamp
            nameof(ReplayAdmission.Duplicate),     // created + Window + skew exactly: still remembered
            nameof(ReplayAdmission.TooOld));       // past it: refused on age
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_registered_guard_both_endpoints_of_one_application_share_one_replay_cache()
    {
        // JC-17 fallback (a host that wires the endpoints without AddDidComm's registrations): a
        // captured request admitted over HTTP must not be admitted again over the WebSocket endpoint
        // of the same application (FR-RR-08: a replayed request re-executes nothing).
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.PostConfigureServices = s =>
            {
                s.RemoveAll<IInboundReplayGuard>();
                s.RemoveAll<IReturnRouteSessionRegistry>();
            };
        });
        host.App.Services.GetService<IInboundReplayGuard>().Should().BeNull("the scenario is a host without the registrations");
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));
        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var captured = await host.AuthcryptAsync(host.Alice, inbound);

        (await host.PostAsync(captured)).Status.Should().Be(HttpStatusCode.OK);
        using var socket = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, captured);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.DropLines(inbound.Id).Count == 1 || host.Recorder.Invocations > 1, Timeout)).Should().BeTrue();

        host.Recorder.Invocations.Should().Be(1, "the replay on the other endpoint is the duplicate");
        host.DropLines(inbound.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.Duplicate));
        await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(socket);
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- oracles an unauthenticated party can observe -------------------------------------------------

    [Theory]
    [InlineData(EnvelopeShape.Plaintext)]
    [InlineData(EnvelopeShape.Anoncrypt)]
    [InlineData(EnvelopeShape.SignedOnly)]
    [InlineData(EnvelopeShape.AnoncryptOfSigned)]
    public async Task An_unauthenticated_sender_gets_the_same_answer_with_or_without_return_route_and_cannot_cause_a_replay_drop(EnvelopeShape shape)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, message.From!));
        var claimed = shape is EnvelopeShape.Plaintext or EnvelopeShape.Anoncrypt ? host.Alice.Did : host.Mallory.Did;
        var with = new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/with").WithFrom(claimed).WithTo(host.ServerDid).WithReturnRoute(ReturnRouteMode.All).Build();
        var without = new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/without").WithFrom(claimed).WithTo(host.ServerDid).Build();
        var (packedWith, contentType) = await host.PackAsync(host.Mallory, with, shape);
        var (packedWithout, _) = await host.PackAsync(host.Mallory, without, shape);

        var responses = new[]
        {
            await host.PostAsync(packedWithout, contentType: contentType),
            await host.PostAsync(packedWith, contentType: contentType),
            await host.PostAsync(packedWith, contentType: contentType), // the same envelope again, timestamp-free
        };

        responses.Should().AllSatisfy(r =>
        {
            r.Status.Should().Be(HttpStatusCode.Accepted);
            r.Body.Should().BeEmpty();
            r.ContentType.Should().BeNull();
        });
        host.Recorder.Invocations.Should().Be(3, "outside FR-RR-08 scope: never dropped, so no drop-vs-admit signal");
        host.Recorder.Seen.Should().AllSatisfy(r => r.Session.Should().BeNull());
        host.DropLines(with.Id).Should().BeEmpty();
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Malformed_return_route_lines_from_an_unauthenticated_flood_are_rate_limited_and_carry_only_the_id()
    {
        // FR-RR-01 logs a malformed header, and anyone can send one (plaintext is enough): the line is
        // rate-limited like the replay drops (Warning for the first and every 1000th, Debug otherwise).
        await using var host = await ReturnRouteTestHost.StartAsync();
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var message = new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/garbage").WithFrom(host.Alice.Did).WithTo(host.ServerDid).Build();
            message.AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [ReturnRouteHeader.HeaderName] = JsonSerializer.SerializeToElement("rrgarbagevalue" + i),
            };
            ids.Add(message.Id);
            var (packed, contentType) = await host.PackAsync(host.Mallory, message, EnvelopeShape.Plaintext);
            (await host.PostAsync(packed, contentType: contentType)).Status.Should().Be(HttpStatusCode.Accepted);
        }

        host.Recorder.Invocations.Should().Be(3, "a malformed header never fails dispatch");
        string[] Lines(string id) => host.Logs.Entries.Where(e => e.Contains(id, StringComparison.Ordinal) && e.Contains("malformed return_route header", StringComparison.Ordinal)).ToArray();
        Lines(ids[0]).Should().ContainSingle().Which.Should().Contain("1 such headers", "the first is the Warning with the running count");
        Lines(ids[1]).Should().ContainSingle().Which.Should().NotContain("such headers", "later ones are Debug lines");
        Lines(ids[2]).Should().ContainSingle().Which.Should().NotContain("such headers");
        host.LeakingLogLines("rrgarbagevalue").Should().BeEmpty("the value is never logged");
    }

    // ---- DI graph (L-017) ------------------------------------------------------------------------------

    [Fact]
    public async Task A_handler_taking_the_registry_the_guard_and_the_facade_resolves_within_the_bound()
    {
        var sp = BuildContainer(b => b.AddProtocol<RegistryGuardClientHandler>());
        var failure = await ResolveWithinAsync(() =>
        {
            sp.GetRequiredService<ProtocolDispatcher>();
            return sp.GetRequiredService<RegistryGuardClientHandler>();
        });
        failure.Should().BeNull();
        sp.GetRequiredService<RegistryGuardClientHandler>().Client.Should().BeSameAs(sp.GetRequiredService<DidCommClient>());
        await sp.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    [Fact]
    public async Task A_host_registry_or_guard_that_takes_the_dispatcher_or_the_handlers_resolves_when_no_handler_depends_on_it()
    {
        var sp = BuildContainer(_ => { }, s =>
        {
            s.AddSingleton<IReturnRouteSessionRegistry, DispatcherTakingRegistry>();
            s.AddSingleton<IInboundReplayGuard, HandlerTakingGuard>();
        });
        var failure = await ResolveWithinAsync(() => (sp.GetRequiredService<IReturnRouteSessionRegistry>(), sp.GetRequiredService<IInboundReplayGuard>()));
        failure.Should().BeNull();
        await sp.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    [Fact]
    public async Task A_cycle_through_a_host_registry_that_takes_the_dispatcher_and_a_handler_that_takes_the_registry_fails_fast()
    {
        var sp = BuildContainer(b => b.AddProtocol<RegistryTakingHandler>(), s => s.AddSingleton<IReturnRouteSessionRegistry, DispatcherTakingRegistry>());
        var failure = await ResolveWithinAsync(() => sp.GetRequiredService<ProtocolDispatcher>());
        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("circular", AtLeast.Once());
        var again = await ResolveWithinAsync(() => sp.GetRequiredService<IReturnRouteSessionRegistry>());
        again.Should().BeOfType<InvalidOperationException>("the same cycle reached from its other end");
    }

    [Fact]
    public async Task A_cycle_through_a_host_guard_that_takes_the_handlers_and_a_handler_that_takes_the_guard_fails_fast()
    {
        var sp = BuildContainer(b => b.AddProtocol<GuardTakingHandler>(), s => s.AddSingleton<IInboundReplayGuard, HandlerTakingGuard>());
        var failure = await ResolveWithinAsync(() => sp.GetRequiredService<IInboundReplayGuard>());
        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("circular", AtLeast.Once());
        (await ResolveWithinAsync(() => sp.GetRequiredService<ProtocolHandlerRegistry>())).Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task A_handler_that_takes_the_dispatcher_fails_fast_instead_of_hanging()
    {
        var sp = BuildContainer(b => b.AddProtocol<DispatcherTakingHandler>());
        var failure = await ResolveWithinAsync(() => sp.GetRequiredService<ProtocolDispatcher>());
        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("circular", AtLeast.Once());
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static RecordedInbound Seen(ReturnRouteTestHost host, Message message)
        => host.Recorder.Seen.Single(r => r.Message.Id == message.Id);

    private static Message PushTo(ReturnRouteTestHost host, string to, string name, string? thid = null)
    {
        var builder = new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/" + name).WithFrom(host.ServerDid).WithTo(to);
        if (thid is not null)
            builder.WithThid(thid);
        return builder.Build();
    }

    /// <summary>Connect and send an authcrypted, stamped <c>return_route: all</c> from <paramref name="peer"/>; returns the socket and its new session.</summary>
    private static async Task<(WebSocket Socket, IReturnRouteSession Session)> OpenSessionAsync(ReturnRouteTestHost host, TestPeer peer)
    {
        var before = host.Registry.Find(peer.Did);
        var socket = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(peer,
            host.Stamped(host.RecordingMessage(peer, "open").WithReturnRoute(ReturnRouteMode.All)).Build()));
        IReturnRouteSession? session = null;
        (await ReturnRouteTestHost.WaitUntilAsync(
            () => (session = host.Registry.Find(peer.Did).FirstOrDefault(s => !before.Contains(s))) is not null, Timeout)).Should().BeTrue();
        return (socket, session!);
    }

    private static Message WithRawThread(Message message, string threadJson)
    {
        message.AdditionalHeaders ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        message.AdditionalHeaders[ReturnRouteHeader.HeaderName] = JsonSerializer.SerializeToElement(ReturnRouteHeader.ThreadValue);
        message.AdditionalHeaders[ReturnRouteHeader.ThreadHeaderName] = JsonDocument.Parse(threadJson).RootElement.Clone();
        return message;
    }

    private static void AssertSingleJwe(byte[] body)
    {
        // JsonDocument.Parse refuses anything after the first value, so two concatenated JWEs (or any
        // trailing bytes) fail here.
        using var document = JsonDocument.Parse(body);
        document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        document.RootElement.TryGetProperty("ciphertext", out _).Should().BeTrue();
        document.RootElement.GetProperty("recipients").GetArrayLength().Should().BeGreaterThan(0);
        Encoding.UTF8.GetString(body).Split("\"ciphertext\"").Length.Should().Be(2, "exactly one ciphertext member in the body");
    }

    private static string Outcome(string dropLine)
        => new[] { nameof(ReplayAdmission.Duplicate), nameof(ReplayAdmission.FutureDated), nameof(ReplayAdmission.TooOld), nameof(ReplayAdmission.Saturated), nameof(ReplayAdmission.MissingCreatedTime) }
            .Single(o => dropLine.Contains("admission " + o, StringComparison.Ordinal));

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    /// <summary>Read until the server's close frame and answer it, as a well-behaved peer does; returns when the socket ends.</summary>
    private static async Task AnswerCloseAsync(WebSocket socket)
    {
        try
        {
            var buffer = new byte[16 * 1024];
            while (socket.State is WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, default);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", default);
                    return;
                }
            }
        }
        catch (Exception)
        {
            // Aborted by the test once the scenario is over.
        }
    }

    private static ServiceProvider BuildContainer(Action<DidCommBuilder> configure, Action<IServiceCollection>? before = null)
    {
        var services = new ServiceCollection();
        before?.Invoke(services);
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
            configure(b);
        });
        return services.BuildServiceProvider();
    }

    /// <summary>Resolve on a dedicated thread; a resolution that neither returns nor throws within the bound is a deadlock and fails the test.</summary>
    private static async Task<Exception?> ResolveWithinAsync(Func<object> resolve)
    {
        var task = Task.Factory.StartNew(() =>
        {
            try
            {
                resolve();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var completed = await Task.WhenAny(task, Task.Delay(Timeout));
        completed.Should().BeSameAs(task, "DI resolution must complete or fail within the bound; a hang is a deadlock");
        return await task;
    }

    private sealed class RegistryGuardClientHandler : IProtocolHandler
    {
        public RegistryGuardClientHandler(IReturnRouteSessionRegistry sessions, IInboundReplayGuard replay, DidCommClient client) => Client = client;

        public DidCommClient Client { get; }

        public string ProtocolUri => "https://didcomm.org/test-rr-adv-di-a/1.0";

        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct) => Task.FromResult<Message?>(null);
    }

    private sealed class RegistryTakingHandler : IProtocolHandler
    {
        public RegistryTakingHandler(IReturnRouteSessionRegistry sessions) { }

        public string ProtocolUri => "https://didcomm.org/test-rr-adv-di-b/1.0";

        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct) => Task.FromResult<Message?>(null);
    }

    private sealed class GuardTakingHandler : IProtocolHandler
    {
        public GuardTakingHandler(IInboundReplayGuard replay) { }

        public string ProtocolUri => "https://didcomm.org/test-rr-adv-di-c/1.0";

        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct) => Task.FromResult<Message?>(null);
    }

    private sealed class DispatcherTakingHandler : IProtocolHandler
    {
        public DispatcherTakingHandler(ProtocolDispatcher dispatcher) { }

        public string ProtocolUri => "https://didcomm.org/test-rr-adv-di-d/1.0";

        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct) => Task.FromResult<Message?>(null);
    }

    private sealed class DispatcherTakingRegistry : IReturnRouteSessionRegistry
    {
        private readonly InMemoryReturnRouteSessionRegistry _inner = new();

        public DispatcherTakingRegistry(ProtocolDispatcher dispatcher) { }

        public int Count => _inner.Count;

        public event EventHandler<ReturnRouteSessionEventArgs>? SessionOpened
        {
            add => _inner.SessionOpened += value;
            remove => _inner.SessionOpened -= value;
        }

        public event EventHandler<ReturnRouteSessionEventArgs>? SessionClosed
        {
            add => _inner.SessionClosed += value;
            remove => _inner.SessionClosed -= value;
        }

        public SessionRegistration Register(IReturnRouteSession session) => _inner.Register(session);

        public bool Unregister(IReturnRouteSession session) => _inner.Unregister(session);

        public IReadOnlyList<IReturnRouteSession> Find(string did) => _inner.Find(did);
    }

    private sealed class HandlerTakingGuard : IInboundReplayGuard
    {
        public HandlerTakingGuard(IEnumerable<IProtocolHandler> handlers) => _ = handlers.Count();

        public ReplayAdmission TryAdmit(string peerDidSubject, string messageId, long? createdTimeEpochSeconds, DateTimeOffset now) => ReplayAdmission.Admitted;
    }
}

/// <summary>
/// An async rendezvous: every caller awaits until <c>parties</c> have arrived (bounded), without
/// blocking a thread, so the callers then proceed together (L-044).
/// </summary>
internal sealed class AsyncBarrier
{
    private readonly int _parties;
    private readonly TimeSpan _timeout;
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public AsyncBarrier(int parties, TimeSpan timeout)
    {
        _parties = parties;
        _timeout = timeout;
    }

    public int Arrived => Volatile.Read(ref _arrived);

    public Task ArriveAndWaitAsync()
    {
        if (Interlocked.Increment(ref _arrived) >= _parties)
            _released.TrySetResult();
        return _released.Task.WaitAsync(_timeout);
    }
}

/// <summary>Test-controlled async behaviour for <see cref="AsyncBehaviorHandler"/>.</summary>
internal sealed class AsyncBehaviorState
{
    public Func<Message, ProtocolContext, CancellationToken, Task<Message?>> Behavior { get; set; } = (_, _, _) => Task.FromResult<Message?>(null);
}

/// <summary>A protocol handler whose behaviour is an async delegate (background pushes, rendezvous).</summary>
internal sealed class AsyncBehaviorHandler : IProtocolHandler
{
    public const string Uri = "https://didcomm.org/test-rr-adversarial/1.0";

    private readonly AsyncBehaviorState _state;

    public AsyncBehaviorHandler(AsyncBehaviorState state) => _state = state;

    public string ProtocolUri => Uri;

    public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct) => _state.Behavior(message, context, ct);
}

/// <summary>A forward sink that records every relay result.</summary>
internal sealed class CapturingForwardSink : IForwardSink
{
    private readonly ConcurrentQueue<ForwardProcessingResult> _results = new();

    public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct)
    {
        _results.Enqueue(result);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<ForwardProcessingResult>> WaitForCountAsync(int count, TimeSpan timeout)
    {
        (await ReturnRouteTestHost.WaitUntilAsync(() => _results.Count >= count, timeout)).Should().BeTrue($"the sink should receive {count} relays");
        return _results.ToList();
    }
}

/// <summary>A logging provider that throws for every line whose text contains a marker (a faulty host sink).</summary>
internal sealed class ThrowingLoggerProvider : ILoggerProvider
{
    private readonly string _marker;

    public ThrowingLoggerProvider(string marker) => _marker = marker;

    public ILogger CreateLogger(string categoryName) => new Logger(_marker);

    public void Dispose()
    {
    }

    private sealed class Logger : ILogger
    {
        private readonly string _marker;

        public Logger(string marker) => _marker = marker;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains(_marker, StringComparison.Ordinal))
                throw new InvalidOperationException("simulated logging provider fault");
        }
    }
}
