using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.InteropTests.Transports;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.WebSocket;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #92 G7 — hostile responses to a return-route send over HTTP (FR-RR-07, JC-2/JC-6): a returned body
/// that is not authcrypt from the addressed peer to the sending DID is discarded and logged by the
/// request's id and the refusal name; garbage under a DIDComm media type is a
/// <see cref="TransportException"/> with status 200 (accepted, unreadable — never silent loss); a
/// 100 MB response is cut at the cap. The responses come from a stub primary handler, so the server
/// can say anything. Every case ends with the log scan: no DID, key id, thread value or body bytes.
/// <para>
/// The WebSocket half (FR-RR-07/08, JC-6/8/9/12): the built-in inbound consumer is resolved from a
/// client container and fed frames packed by real peers — pushes failing the identity rule, replays,
/// in-scope pushes without <c>created_time</c>, a handler that replies or throws — and a flood on one
/// session socket must neither drop nor delay another's frames.
/// </para>
/// </summary>
public sealed class ReturnRouteClientAdversarialTests
{
    private static readonly string[] IdentityFailureCases =
    {
        "anoncrypt from the server",
        "signed-only by the server",
        "plaintext",
        "authcrypt by Mallory",
        "server to Carol",
        "forward type",
    };

    private static readonly string[] GarbageCases =
    {
        "open brace",
        "invalid UTF-8",
        "JWE without ciphertext",
        "JWE with a flipped tag",
    };

    public static TheoryData<string> IdentityFailures() => new(IdentityFailureCases);

    [Theory]
    [MemberData(nameof(IdentityFailures))]
    public async Task Returned_is_null_and_logged_for_bodies_failing_the_identity_rule(string @case)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        var (body, contentType, refusal) = await HostileReplyAsync(host, @case);
        await using var client = ReturnRouteClientHarness.Create(host, StubReturning(body, contentType), holders: new[] { host.Alice, host.Carol });
        var request = host.RecordingMessage(host.Alice).Build();

        var result = await client.Client.SendAsync(request, client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        result.Transport.Accepted.Should().BeTrue();
        result.Transport.ResponsePayload.Should().NotBeNull("the transport read it; only Returned is filtered");
        result.Returned.Should().BeNull(@case);
        host.Logs.Entries.Where(l => l.Contains("was discarded", StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Should().Contain(request.Id).And.Contain(refusal);
        host.LeakingLogLines().Should().BeEmpty();
        host.Logs.Entries.Should().NotContain(l => l.Contains(Fragment(body), StringComparison.Ordinal), "no returned bytes are logged");
    }

    public static TheoryData<string> GarbageBodies() => new(GarbageCases);

    [Theory]
    [MemberData(nameof(GarbageBodies))]
    public async Task Garbage_under_a_DIDComm_type_is_TransportException_status_200(string @case)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        var body = await GarbageAsync(host, @case);
        await using var client = ReturnRouteClientHarness.Create(host, StubReturning(body, MediaTypes.Encrypted));

        var act = () => client.Client.SendAsync(host.RecordingMessage(host.Alice).Build(), client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        var thrown = (await act.Should().ThrowAsync<TransportException>(@case)).Which;
        thrown.HttpStatusCode.Should().Be(200, "the request was accepted; the returned message was unreadable");
        thrown.InnerException.Should().BeAssignableTo<DidCommException>("the FR-API-07 type is kept as the inner exception");
        thrown.Message.Should().NotContain("did:");
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_hostile_100_MB_response_is_cut_at_the_cap_and_SendAsync_throws_TransportException(bool declareLength)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        var contents = new List<GeneratedContent>();
        Func<HttpMessageHandler> stub = () => new StubHandler(_ =>
        {
            var content = GeneratedContent.Generated(100L * 1024 * 1024, declareLength);
            lock (contents)
                contents.Add(content);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        await using var client = ReturnRouteClientHarness.Create(host, stub);
        var cap = new DidCommOptions().MaxReceiveBytes;

        var act = () => client.Client.SendAsync(host.RecordingMessage(host.Alice).Build(), client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        var thrown = (await act.Should().ThrowAsync<TransportException>()).Which;
        thrown.HttpStatusCode.Should().Be(200);
        thrown.Message.Should().Contain("MaxReceiveBytes").And.NotContain("did:");

        // The transport alone reports the delivery as accepted, with the body discarded.
        var transport = client.Services.GetServices<IDidCommTransport>().Single();
        var alone = await transport.SendAsync(new TransportRequest(client.Endpoint, new byte[] { 0x7b, 0x7d }, MediaTypes.Encrypted) { MaxResponseBytes = cap }, default);
        alone.Accepted.Should().BeTrue();
        alone.ResponseDiscarded.Should().BeTrue();

        lock (contents)
        {
            contents.Should().HaveCount(2);
            contents.Should().OnlyContain(c => c.BytesPulled <= cap + 16 * 1024, "nothing past the cap plus one chunk is read");
        }
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- the inbound consumer (WebSocket pushes) ----

    public static TheoryData<string> PushIdentityFailures() => new(IdentityFailureCases);

    [Theory]
    [MemberData(nameof(PushIdentityFailures))]
    public async Task Pushes_failing_the_identity_rule_are_never_dispatched(string @case)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteWebSocketClientHarness.Create(host, holders: new[] { host.Alice, host.Carol });
        var (body, _, refusal, messageId) = await HostileMessageAsync(host, @case);

        await ConsumerOf(client).ConsumeAsync(FrameFor(client, body), default);

        client.Recorder.Invocations.Should().Be(0, @case);
        var drop = host.Logs.Entries.Where(l => l.Contains("Pushed message dropped", StringComparison.Ordinal)).Should().ContainSingle().Subject;
        drop.Should().Contain(refusal);
        drop.Should().NotContain(messageId, "nothing from the wire is logged before the identity rule passed (JC-12)");
        host.LeakingLogLines().Should().BeEmpty();
        host.Logs.Entries.Should().NotContain(l => l.Contains(Fragment(body), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_replayed_push_is_dispatched_once()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteWebSocketClientHarness.Create(host);
        var push = await ServerPushAsync(host, PickupDelivery(host).WithCreatedTime(host.Clock.Now.ToUnixTimeSeconds()).Build());
        var consumer = ConsumerOf(client);

        await consumer.ConsumeAsync(FrameFor(client, push.Bytes), default);
        await consumer.ConsumeAsync(FrameFor(client, push.Bytes), default);

        client.Recorder.Invocations.Should().Be(1, "FR-RR-08 admits an in-scope push once per (sender, id)");
        host.Logs.Entries.Where(l => l.Contains("replay admission Duplicate", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Should().Contain(push.Id);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task An_in_scope_push_without_created_time_is_dropped()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteWebSocketClientHarness.Create(host);
        var push = await ServerPushAsync(host, PickupDelivery(host).Build());

        await ConsumerOf(client).ConsumeAsync(FrameFor(client, push.Bytes), default);

        client.Recorder.Invocations.Should().Be(0, "a mediator-service push without created_time fails FR-RR-08");
        host.Logs.Entries.Where(l => l.Contains("replay admission MissingCreatedTime", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Should().Contain(push.Id);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task An_out_of_scope_app_push_without_created_time_is_dispatched()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteWebSocketClientHarness.Create(host);
        var push = await ServerPushAsync(host, AppPush(host, "plain").Build());

        await ConsumerOf(client).ConsumeAsync(FrameFor(client, push.Bytes), default);

        client.Recorder.Count.Should().Be(1, "an application push outside the FR-RR-08 scope needs no created_time");
        client.Recorder.Seen.Single().Should().Match<RecordedInbound>(r => r.Message.Id == push.Id && r.Session == null);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_handler_reply_to_a_push_is_never_sent()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteWebSocketClientHarness.Create(host);
        client.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), new MessageBuilder()
            .WithType(RecordingHandler.ProtocolUri + "/answer")
            .WithFrom(host.Alice.Did)
            .WithTo(host.ServerDid)
            .WithThid(message.Id)
            .Build());
        var push = await ServerPushAsync(host, AppPush(host, "asks").Build());

        await ConsumerOf(client).ConsumeAsync(FrameFor(client, push.Bytes), default);

        client.Recorder.Count.Should().Be(1, "the handler ran and produced a reply");
        client.Connects.Should().Be(0, "no socket was opened: the reply was not sent (no reflector, JC-9)");
        client.Events.Should().BeEmpty();
        host.Recorder.Count.Should().Be(0, "nothing reached the server");
        host.Logs.Entries.Where(l => l.Contains("handler reply not sent", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Should().Contain(push.Id);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_throwing_handler_does_not_break_the_next_frame()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteWebSocketClientHarness.Create(host);
        var first = await ServerPushAsync(host, AppPush(host, "first").Build());
        var second = await ServerPushAsync(host, AppPush(host, "second").Build());
        client.Recorder.AfterPushes = (message, _, _) =>
        {
            if (message.Id == first.Id)
                throw new InvalidOperationException("handler fault carrying " + host.Mallory.Did);
        };
        var consumer = ConsumerOf(client);

        await consumer.ConsumeAsync(FrameFor(client, first.Bytes), default);
        await consumer.ConsumeAsync(FrameFor(client, second.Bytes), default);

        client.Recorder.Invocations.Should().Be(2, "the second frame was dispatched after the first handler threw");
        client.Recorder.Seen.Select(s => s.Message.Id).Should().Equal(first.Id, second.Id);
        host.Logs.Entries.Where(l => l.Contains("the handler threw", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Should().Contain(first.Id).And.Contain(nameof(InvalidOperationException));
        host.LeakingLogLines().Should().BeEmpty("the exception's message (it names a DID) is never logged");
    }

    [Fact]
    public async Task A_flood_on_one_session_does_not_drop_or_delay_another()
    {
        const int flood = 200;
        const int other = 50;
        var timeout = TimeSpan.FromSeconds(10);
        var sockets = new ScriptedSocketFactory();
        var consumer = new RecordingFrameConsumer();
        var s1Parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseS1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var s2Done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var s1Seen = new ConcurrentQueue<string>();
        var s2Seen = new ConcurrentQueue<string>();
        consumer.Behavior = async (frame, _, _) =>
        {
            var text = Encoding.UTF8.GetString(frame.Payload.Span);
            if (frame.LocalDid == "did:example:s1")
            {
                s1Seen.Enqueue(text);
                if (text == "s1-0")
                {
                    // The first S1 frame parks S1's consumer call: S1's loop reads nothing more until released.
                    s1Parked.TrySetResult();
                    await releaseS1.Task.ConfigureAwait(false);
                }
            }
            else
            {
                s2Seen.Enqueue(text);
                if (s2Seen.Count == other)
                    s2Done.TrySetResult();
            }
        };
        await using var transport = new WebSocketDidCommTransport(
            Options.Create(new WebSocketTransportOptions
            {
                AllowedSchemes = new[] { "ws" },
                MaxReconnectAttempts = 0,
                ReceiveInbound = true,
                WebSocketFactory = sockets.Create,
                Connect = (_, _, _) => Task.CompletedTask,
            }),
            logger: null,
            coreOptions: null,
            inboundConsumer: consumer);
        var endpoint = new Uri("ws://agents.r.us/ws");
        foreach (var local in new[] { "did:example:s1", "did:example:s2" })
        {
            await transport.SendAsync(new TransportRequest(endpoint, Utf8("open"), MediaTypes.Encrypted)
            {
                ReturnRoute = ReturnRouteMode.All,
                LocalDid = local,
                PeerDid = "did:example:server",
            }, default);
        }
        var (s1, s2) = (sockets.Sockets[0], sockets.Sockets[1]);

        for (var i = 0; i < flood; i++)
            s1.PushMessage(Utf8("s1-" + i));
        await s1Parked.Task.WaitAsync(timeout);
        for (var i = 0; i < other; i++)
            s2.PushMessage(Utf8("s2-" + i));

        await s2Done.Task.WaitAsync(timeout);
        s1Seen.Should().Equal(new[] { "s1-0" }, "S1's consumer is still parked: the overlap really happened");
        s2Seen.Should().Equal(Enumerable.Range(0, other).Select(i => "s2-" + i), "S2 was served in full and in order while S1 was flooded and stuck");

        releaseS1.TrySetResult();
        (await ReturnRouteTestHost.WaitUntilAsync(() => s1Seen.Count == flood, timeout)).Should().BeTrue();
        s1Seen.Should().Equal(Enumerable.Range(0, flood).Select(i => "s1-" + i), "the flood backpressured only its own socket: nothing dropped, FIFO");
        transport.InboundMessagesDropped.Should().Be(0);
    }

    [Fact]
    public async Task Log_scan_finds_no_DID_kid_thread_or_body()
    {
        // Every HTTP case above against one host and one log, with a thread-mode request on each so the
        // thread value is in play too.
        await using var host = await ReturnRouteTestHost.StartAsync();
        var thread = "thr-" + Guid.NewGuid().ToString("N");
        var fragments = new List<string>();

        foreach (var @case in IdentityFailureCases)
        {
            var (body, contentType, _) = await HostileReplyAsync(host, @case);
            fragments.Add(Fragment(body));
            await using var client = ReturnRouteClientHarness.Create(host, StubReturning(body, contentType), holders: new[] { host.Alice, host.Carol });
            var result = await client.Client.SendAsync(host.RecordingMessage(host.Alice).Build(),
                client.ToServer() with { ReturnRoute = ReturnRouteMode.Thread, ReturnRouteThread = thread });
            result.Returned.Should().BeNull(@case);
        }
        foreach (var @case in GarbageCases)
        {
            var body = await GarbageAsync(host, @case);
            if (@case != "invalid UTF-8")
                fragments.Add(Fragment(body));
            await using var client = ReturnRouteClientHarness.Create(host, StubReturning(body, MediaTypes.Encrypted));
            var act = () => client.Client.SendAsync(host.RecordingMessage(host.Alice).Build(),
                client.ToServer() with { ReturnRoute = ReturnRouteMode.Thread, ReturnRouteThread = thread });
            await act.Should().ThrowAsync<TransportException>(@case);
        }

        // The inbound consumer's cases against the same host and log (L = Alice, P = server): pushes failing
        // the identity rule, garbage, a replay, an in-scope push without created_time, a malformed header,
        // a handler that replies and one that throws. Each in-scope push carries the thread value too.
        await using (var client = ReturnRouteWebSocketClientHarness.Create(host, holders: new[] { host.Alice, host.Carol }))
        {
            var consumer = ConsumerOf(client);
            foreach (var @case in IdentityFailureCases)
            {
                var (body, _, _, _) = await HostileMessageAsync(host, @case);
                fragments.Add(Fragment(body));
                await consumer.ConsumeAsync(FrameFor(client, body), default);
            }
            foreach (var @case in GarbageCases)
            {
                var body = await GarbageAsync(host, @case);
                await consumer.ConsumeAsync(FrameFor(client, body), default);
            }
            var replayed = await ServerPushAsync(host, PickupDelivery(host).WithThid(thread).WithCreatedTime(host.Clock.Now.ToUnixTimeSeconds()).Build());
            fragments.Add(Fragment(replayed.Bytes));
            await consumer.ConsumeAsync(FrameFor(client, replayed.Bytes), default);
            await consumer.ConsumeAsync(FrameFor(client, replayed.Bytes), default);
            var unstamped = await ServerPushAsync(host, PickupDelivery(host).WithThid(thread).Build());
            await consumer.ConsumeAsync(FrameFor(client, unstamped.Bytes), default);
            var malformedMessage = AppPush(host, "malformed").WithThid(thread).Build();
            malformedMessage.AdditionalHeaders ??= new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
            malformedMessage.AdditionalHeaders[ReturnRouteHeader.HeaderName] = System.Text.Json.JsonSerializer.SerializeToElement("sometimes");
            var malformed = await ServerPushAsync(host, malformedMessage);
            await consumer.ConsumeAsync(FrameFor(client, malformed.Bytes), default);
            client.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), new MessageBuilder()
                .WithType(RecordingHandler.ProtocolUri + "/answer").WithFrom(host.Alice.Did).WithTo(host.ServerDid).WithThid(thread).Build());
            client.Recorder.AfterPushes = (message, _, _) =>
            {
                if (message.Type.EndsWith("/throws", StringComparison.Ordinal))
                    throw new InvalidOperationException("fault naming " + host.ServerDid + " and " + thread);
            };
            var replied = await ServerPushAsync(host, AppPush(host, "replied").WithThid(thread).Build());
            await consumer.ConsumeAsync(FrameFor(client, replied.Bytes), default);
            var throws = await ServerPushAsync(host, AppPush(host, "throws").WithThid(thread).Build());
            await consumer.ConsumeAsync(FrameFor(client, throws.Bytes), default);
            client.Recorder.Invocations.Should().Be(4, "the replay's first copy, the malformed-header push, the replied one and the throwing one were dispatched");
        }
        foreach (var marker in new[] { "Pushed message dropped", "replay admission Duplicate", "replay admission MissingCreatedTime", "malformed return_route header", "handler reply not sent", "the handler threw" })
            host.Logs.Entries.Should().Contain(l => l.Contains(marker, StringComparison.Ordinal), "the scan covers the consumer's '{0}' line", marker);

        host.Logs.Entries.Should().NotBeEmpty("the scan must have something to scan");
        host.LeakingLogLines(thread).Should().BeEmpty("no DID, key id or thread value in any line");
        foreach (var fragment in fragments.Where(f => f.Length >= 8))
            host.Logs.Entries.Should().NotContain(l => l.Contains(fragment, StringComparison.Ordinal), "no returned bytes in any line");
    }

    // ---- hostile bodies ----

    private static async Task<(byte[] Body, string ContentType, string Refusal)> HostileReplyAsync(ReturnRouteTestHost host, string @case)
    {
        var (body, contentType, refusal, _) = await HostileMessageAsync(host, @case);
        return (body, contentType, refusal);
    }

    /// <summary>A message failing the identity rule for (L = Alice, P = server), packed by a real peer, with its id.</summary>
    private static async Task<(byte[] Body, string ContentType, string Refusal, string MessageId)> HostileMessageAsync(ReturnRouteTestHost host, string @case)
    {
        var server = host.App.Services.GetRequiredService<DidCommClient>();
        string? id = null;
        Message Reply(string from, string to, string type = RecordingHandler.ProtocolUri + "/reply")
        {
            var message = new MessageBuilder()
                .WithType(type)
                .WithFrom(from)
                .WithTo(to)
                .WithBody(new JsonObject())
                .Build();
            id = message.Id;
            return message;
        }

        (byte[] Body, string ContentType, string Refusal) packed = @case switch
        {
            "anoncrypt from the server" => (Utf8((await server.PackEncryptedAsync(Reply(host.ServerDid, host.Alice.Did), new PackEncryptedOptions(Recipients: new[] { host.Alice.Did }))).Message),
                MediaTypes.Encrypted, nameof(ReturnedMessageRefusal.NotAuthcrypt)),
            "signed-only by the server" => (Utf8(await server.PackSignedAsync(Reply(host.ServerDid, host.Alice.Did), host.ServerDid)), MediaTypes.Signed, nameof(ReturnedMessageRefusal.NotAuthcrypt)),
            "plaintext" => (Utf8(await server.PackPlaintextAsync(Reply(host.ServerDid, host.Alice.Did))), MediaTypes.Plaintext, nameof(ReturnedMessageRefusal.NotAuthcrypt)),
            "authcrypt by Mallory" => (Utf8((await host.Mallory.Client.PackEncryptedAsync(Reply(host.Mallory.Did, host.Alice.Did),
                new PackEncryptedOptions(Recipients: new[] { host.Alice.Did }, From: host.Mallory.Did))).Message), MediaTypes.Encrypted, nameof(ReturnedMessageRefusal.PeerMismatch)),
            "server to Carol" => (Utf8((await server.PackEncryptedAsync(Reply(host.ServerDid, host.Carol.Did),
                new PackEncryptedOptions(Recipients: new[] { host.Carol.Did }, From: host.ServerDid))).Message), MediaTypes.Encrypted, nameof(ReturnedMessageRefusal.LocalMismatch)),
            "forward type" => (Utf8((await server.PackEncryptedAsync(Reply(host.ServerDid, host.Alice.Did, "https://didcomm.org/routing/2.0/forward"),
                new PackEncryptedOptions(Recipients: new[] { host.Alice.Did }, From: host.ServerDid))).Message), MediaTypes.Encrypted, nameof(ReturnedMessageRefusal.Forward)),
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };
        return (packed.Body, packed.ContentType, packed.Refusal, id!);
    }

    private static async Task<byte[]> GarbageAsync(ReturnRouteTestHost host, string @case)
    {
        switch (@case)
        {
            case "open brace":
                return Utf8("{");
            case "invalid UTF-8":
                return new byte[] { 0x7b, 0xff, 0xfe, 0xc3, 0x28, 0x7d };
            case "JWE without ciphertext":
                return Utf8("{\"protected\":\"eyJhbGciOiJFQ0RILUVTK0EyNTZLVyJ9\",\"recipients\":[{\"header\":{\"kid\":\"x\"},\"encrypted_key\":\"AAAA\"}],\"iv\":\"AAAA\",\"tag\":\"AAAA\"}");
            case "JWE with a flipped tag":
                var server = host.App.Services.GetRequiredService<DidCommClient>();
                var packed = (await server.PackEncryptedAsync(
                    new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/reply").WithFrom(host.ServerDid).WithTo(host.Alice.Did).Build(),
                    new PackEncryptedOptions(Recipients: new[] { host.Alice.Did }, From: host.ServerDid))).Message;
                var jwe = JsonNode.Parse(packed)!.AsObject();
                var tag = jwe["tag"]!.GetValue<string>();
                jwe["tag"] = (tag[0] == 'A' ? 'B' : 'A') + tag[1..];
                return Utf8(jwe.ToJsonString());
            default:
                throw new ArgumentOutOfRangeException(nameof(@case));
        }
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    // A distinctive slice of a body (from its middle, past any shared JSON prefix) to search the logs for.
    private static string Fragment(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        return text.Length <= 24 ? text : text.Substring(text.Length / 2, 24);
    }

    private static IInboundFrameConsumer ConsumerOf(ReturnRouteWebSocketClientHarness client)
        => client.Services.GetRequiredService<IInboundFrameConsumer>();

    /// <summary>A frame as the client's session socket to the server, opened for Alice, would attribute it.</summary>
    private static InboundFrame FrameFor(ReturnRouteWebSocketClientHarness client, byte[] body)
        => new(client.Endpoint, client.Host.Alice.Did, client.Host.ServerDid, body);

    /// <summary>Authcrypt <paramref name="message"/> from the server to Alice, as a real session push would be.</summary>
    private static async Task<(byte[] Bytes, string Id)> ServerPushAsync(ReturnRouteTestHost host, Message message)
    {
        var server = host.App.Services.GetRequiredService<DidCommClient>();
        var packed = await server.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { host.Alice.Did }, From: host.ServerDid, Forward: false));
        return (Utf8(packed.Message), message.Id);
    }

    private static MessageBuilder PickupDelivery(ReturnRouteTestHost host)
        => new MessageBuilder().WithType("https://didcomm.org/message-pickup/4.0/delivery").WithFrom(host.ServerDid).WithTo(host.Alice.Did);

    private static MessageBuilder AppPush(ReturnRouteTestHost host, string name)
        => new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/" + name).WithFrom(host.ServerDid).WithTo(host.Alice.Did);

    private static Func<HttpMessageHandler> StubReturning(byte[] body, string contentType)
        => () => new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = GeneratedContent.Of(body, contentType) });

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
