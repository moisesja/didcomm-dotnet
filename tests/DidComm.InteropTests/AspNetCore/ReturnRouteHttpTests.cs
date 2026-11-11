using System.Net;
using System.Text.Json;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.TrustPing;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #91 F9 — the HTTP binding of the return-route extension (FR-RR-04) end to end against a
/// TestServer: an authcrypt request carrying <c>return_route: all</c> gets its reply as the
/// <c>200</c> body; without the header the endpoint keeps answering <c>202</c> with no body. Every
/// scenario ends with the F14 log scan: no DID, key id or thread value in any captured line (Trace
/// level and up, exception text included).
/// </summary>
public sealed class ReturnRouteHttpTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static TheoryData<EnvelopeShape, string> EligibilityMatrix()
    {
        var data = new TheoryData<EnvelopeShape, string>();
        foreach (var shape in Enum.GetValues<EnvelopeShape>())
            foreach (var header in new[] { "none", "all", "thread", "malformed", "absent" })
                data.Add(shape, header);
        return data;
    }

    [Theory]
    [MemberData(nameof(EligibilityMatrix))]
    public async Task Eligibility_matrix_only_an_authenticated_all_request_gets_a_session_and_a_200_body(EnvelopeShape shape, string header)
    {
        // FR-RR-03: a session (and so a 200 body, FR-RR-04) only for an encrypted envelope with an
        // authcrypt layer carrying `all`; every other combination is dispatched with Session == null
        // and answered 202. The header is always parsed and surfaced (FR-RR-01); a malformed value is
        // `none` plus a log line by id. Every message is stamped, so FR-RR-08 admits the in-scope ones.
        // Regression (QA #91): a signed-only or anoncrypt(signed) message reports Authenticated (the
        // JWS sets it) but has no sender key id; it used to land in FR-RR-08 scope and be dropped as
        // NoSenderSubject instead of being dispatched without a session.
        await using var host = await ReturnRouteTestHost.StartAsync();
        var thread = "thr-" + Guid.NewGuid().ToString("N");
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReplyTo(host, message, host.Alice.Did));

        var inbound = WithHeader(host.Stamped(host.RecordingMessage(host.Alice, "matrix")), header, thread);
        var (packed, contentType) = await host.PackAsync(host.Alice, inbound, shape);

        var response = await host.PostAsync(packed, contentType: contentType);

        var authenticated = shape is EnvelopeShape.Authcrypt or EnvelopeShape.AnoncryptOfAuthcrypt or EnvelopeShape.AuthcryptOfSigned;
        var expectSession = authenticated && header == "all";
        host.Recorder.Invocations.Should().Be(1, "every combination is dispatched (stamped, so FR-RR-08 admits the in-scope ones)");
        var seen = host.Recorder.Seen.Single();
        if (expectSession)
        {
            response.Status.Should().Be(HttpStatusCode.OK);
            response.ContentType.Should().Be("application/didcomm-encrypted+json");
            response.ContentLength.Should().Be(response.Body.Length);
            seen.Session.Should().NotBeNull();
            seen.Session!.PeerDid.Should().Be(host.Alice.Did);
            seen.Session.LocalDid.Should().Be(host.ServerDid);
            seen.Session.Transport.Should().Be(ReturnRouteTransport.Http);
            seen.Session.IsLive.Should().BeFalse();
            var reply = await host.Alice.Client.UnpackAsync(response.BodyText);
            reply.Authenticated.Should().BeTrue();
            reply.Message.Thid.Should().Be(inbound.Id);
            reply.Message.To.Should().Equal(host.Alice.Did);
        }
        else
        {
            response.Status.Should().Be(HttpStatusCode.Accepted, "{0} + {1} opens no session (FR-RR-03)", shape, header);
            response.Body.Should().BeEmpty();
            seen.Session.Should().BeNull();
        }

        var expectedMode = header switch
        {
            "all" => ReturnRouteMode.All,
            "thread" => ReturnRouteMode.Thread,
            _ => ReturnRouteMode.None,
        };
        seen.ReturnRoute.Mode.Should().Be(expectedMode, "the parsed header is surfaced whatever the envelope (FR-RR-01)");
        seen.ReturnRoute.Thread.Should().Be(header == "thread" ? thread : null);
        if (header == "malformed")
            host.Logs.Entries.Should().Contain(e => e.Contains(inbound.Id) && e.Contains("malformed return_route header treated as none"));
        host.Registry.Count.Should().Be(0, "HTTP sessions are never registered (JC-16)");
        host.LeakingLogLines(thread).Should().BeEmpty("F14: no DID, kid or thread value in the logs");
    }

    [Fact]
    public async Task A_reply_addressed_to_anyone_but_the_authenticated_sender_is_not_returned()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        Message? reply = null;
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), reply = ReplyTo(host, message, host.Carol.Did));

        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, inbound));

        response.Status.Should().Be(HttpStatusCode.Accepted, "a reply to anyone but [peer] is never sent (FR-RR-03)");
        response.Body.Should().BeEmpty();
        host.Recorder.Seen.Single().Session.Should().NotBeNull("the request was eligible; only the reply was refused");
        host.Logs.Entries.Should().Contain(e => e.Contains(reply!.Id) && e.Contains(nameof(ReturnRouteSendRefusal.ToNotExactlyPeer)),
            "the refusal is logged with the reply id and the enum name");
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_push_and_a_returned_reply_yield_exactly_one_message_in_the_body_and_the_push_wins()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        Message? push = null;
        Message? reply = null;
        host.Recorder.Behavior = (message, _) => (
            new[] { push = ReplyTo(host, message, host.Alice.Did, "pushed") },
            reply = ReplyTo(host, message, host.Alice.Did, "returned"));

        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, inbound));

        response.Status.Should().Be(HttpStatusCode.OK);
        response.ContentLength.Should().Be(response.Body.Length);
        using (var document = JsonDocument.Parse(response.Body))
            document.RootElement.GetProperty("recipients").GetArrayLength().Should().BeGreaterThan(0, "the body is one JWE (general JSON)");
        var unpacked = await host.Alice.Client.UnpackAsync(response.BodyText);
        unpacked.Message.Id.Should().Be(push!.Id, "the message pushed first owns the one HTTP slot (FR-RR-04)");
        host.Recorder.Seen.Single().PushResults.Should().Equal(true);
        host.Logs.Entries.Should().Contain(e => e.Contains(inbound.Id) && e.Contains("returned reply was dropped"));
        host.LeakingLogLines().Should().BeEmpty();
        reply.Should().NotBeNull();
    }

    [Fact]
    public async Task A_second_push_on_the_HTTP_session_returns_false()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        Message? first = null;
        host.Recorder.Behavior = (message, _) => (
            new[] { first = ReplyTo(host, message, host.Alice.Did, "first"), ReplyTo(host, message, host.Alice.Did, "second") },
            null);

        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, inbound));

        host.Recorder.Seen.Single().PushResults.Should().Equal(new[] { true, false }, "HTTP carries at most one message per request (FR-RR-04)");
        response.Status.Should().Be(HttpStatusCode.OK);
        (await host.Alice.Client.UnpackAsync(response.BodyText)).Message.Id.Should().Be(first!.Id);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_push_made_after_the_response_was_written_returns_false()
    {
        // PA-2 end to end: a handler that keeps context.Session and pushes later cannot reach a
        // response that has already been sent.
        await using var host = await ReturnRouteTestHost.StartAsync();
        IReturnRouteSession? kept = null;
        host.Recorder.Behavior = (_, context) =>
        {
            kept = context.Session;
            return (Array.Empty<Message>(), null);
        };

        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, inbound));

        response.Status.Should().Be(HttpStatusCode.Accepted, "nothing was sent while the request was open");
        kept.Should().NotBeNull();
        kept!.Closed.IsCancellationRequested.Should().BeTrue("the session ended with its request");
        kept.Mode.Should().Be(ReturnRouteMode.None);
        (await kept.TrySendAsync(ReplyTo(host, inbound, host.Alice.Did, "late"), default)).Should().BeFalse();
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_handler_that_throws_after_pushing_gets_the_uniform_empty_400_and_its_acknowledgement_is_cancelled()
    {
        // JC-13 / #90: a handler fault after dispatch keeps the pre-existing uniform 400 (empty body,
        // floored), cancels the transport acknowledgement, and — new with #91 — discards the message
        // the handler had already placed in the HTTP slot: nothing of it reaches the wire.
        await using var host = await ReturnRouteTestHost.StartAsync();
        Task? acknowledged = null;
        host.Recorder.Behavior = (message, context) =>
        {
            acknowledged = context.Acknowledged;
            return (new[] { ReplyTo(host, message, host.Alice.Did, "pushed-then-fault") }, null);
        };
        host.Recorder.AfterPushes = (_, _, _) => throw new InvalidOperationException("simulated handler fault");

        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, inbound));

        host.Recorder.Seen.Single().PushResults.Should().Equal(new[] { true }, "the push had filled the slot before the fault");
        response.Status.Should().Be(HttpStatusCode.BadRequest, "a handler fault keeps the pre-#91 uniform rejection (JC-13)");
        response.Body.Should().BeEmpty("the pushed message is discarded with the session — no body leak");
        response.ContentType.Should().NotBe("application/didcomm-encrypted+json");
        acknowledged.Should().NotBeNull();
        var waitAck = async () => await acknowledged!.WaitAsync(Timeout);
        await waitAck.Should().ThrowAsync<OperationCanceledException>("a post-handler non-2xx exit cancels the acknowledgement (#90)");
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_returned_reply_whose_pack_fails_answers_202_never_400()
    {
        // JC-13: a pack failure inside TrySendAsync is swallowed (type name logged). The reply is
        // addressed to a DID URL of the peer's own subject: it passes the FR-RR-03 addressing rule
        // but NetDidKeyService refuses DID URLs in `to` at pack time (known, outside #91).
        await using var host = await ReturnRouteTestHost.StartAsync();
        Message? reply = null;
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), reply = ReplyTo(host, message, host.Alice.Did + "?src=test"));

        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, inbound));

        response.Status.Should().Be(HttpStatusCode.Accepted, "a failed pack after dispatch is never a 400 (JC-13)");
        response.Body.Should().BeEmpty();
        host.Logs.Entries.Should().Contain(e => e.Contains(reply!.Id) && e.Contains("pack failed with"));
        host.LeakingLogLines().Should().BeEmpty("the pack failure is logged by exception type name only");
    }

    [Fact]
    public async Task Session_aware_callback_returns_its_reply_as_the_200_body()
    {
        IReturnRouteSession? seen = null;
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.Callback = (unpacked, session, _) =>
        {
            seen = session;
            return Task.FromResult<Message?>(TrustPing.CreateResponse(unpacked.Message));
        });

        var ping = host.Stamped(new MessageBuilder()
                .WithType(TrustPing.PingType)
                .WithFrom(host.Alice.Did)
                .WithTo(host.ServerDid)
                .WithReturnRoute(ReturnRouteMode.All))
            .Build();
        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, ping), path: "/didcomm-callback");

        response.Status.Should().Be(HttpStatusCode.OK);
        response.ContentType.Should().Be("application/didcomm-encrypted+json");
        var reply = await host.Alice.Client.UnpackAsync(response.BodyText);
        reply.Message.Type.Should().Be(TrustPing.ResponseType);
        reply.Message.Thid.Should().Be(ping.Id);
        seen.Should().NotBeNull();
        seen!.PeerDid.Should().Be(host.Alice.Did);
        seen.Closed.IsCancellationRequested.Should().BeTrue();
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Session_aware_callback_without_the_header_gets_no_session_and_its_reply_is_not_sent()
    {
        var calls = 0;
        IReturnRouteSession? seen = null;
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.Callback = (unpacked, session, _) =>
        {
            Interlocked.Increment(ref calls);
            seen = session;
            return Task.FromResult<Message?>(TrustPing.CreateResponse(unpacked.Message));
        });

        var ping = TrustPing.CreatePing(host.Alice.Did, host.ServerDid);
        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, ping), path: "/didcomm-callback");

        response.Status.Should().Be(HttpStatusCode.Accepted);
        response.Body.Should().BeEmpty();
        calls.Should().Be(1);
        seen.Should().BeNull("no return_route: no session (FR-RR-01)");
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Session_aware_callback_that_throws_after_pushing_answers_202_and_sends_nothing()
    {
        bool? pushed = null;
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.Callback = async (unpacked, session, ct) =>
        {
            pushed = await session!.TrySendAsync(TrustPing.CreateResponse(unpacked.Message), ct);
            throw new InvalidOperationException("simulated callback fault");
        });

        var ping = host.Stamped(new MessageBuilder()
                .WithType(TrustPing.PingType)
                .WithFrom(host.Alice.Did)
                .WithTo(host.ServerDid)
                .WithReturnRoute(ReturnRouteMode.All))
            .Build();
        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, ping), path: "/didcomm-callback");

        pushed.Should().BeTrue("the push filled the slot before the fault");
        response.Status.Should().Be(HttpStatusCode.Accepted, "a callback fault is never a 500 (no unpack-success oracle)");
        response.Body.Should().BeEmpty("what the callback pushed before throwing is discarded");
        host.Logs.Entries.Should().Contain(e => e.Contains(ping.Id) && e.Contains("callback threw InvalidOperationException"));
        host.Logs.Entries.Should().NotContain(e => e.Contains("simulated callback fault"), "exception text is never logged");
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Session_aware_callback_is_not_invoked_for_a_replayed_request()
    {
        var calls = 0;
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.Callback = (unpacked, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<Message?>(TrustPing.CreateResponse(unpacked.Message));
        });

        var ping = host.Stamped(new MessageBuilder()
                .WithType(TrustPing.PingType)
                .WithFrom(host.Alice.Did)
                .WithTo(host.ServerDid)
                .WithReturnRoute(ReturnRouteMode.All))
            .Build();
        var packed = await host.AuthcryptAsync(host.Alice, ping);

        (await host.PostAsync(packed, path: "/didcomm-callback")).Status.Should().Be(HttpStatusCode.OK);
        var replay = await host.PostAsync(packed, path: "/didcomm-callback");

        replay.Status.Should().Be(HttpStatusCode.Accepted);
        replay.Body.Should().BeEmpty();
        calls.Should().Be(1, "FR-RR-08 runs before the callback");
        host.DropLines(ping.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.Duplicate));
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>Apply one of the matrix's header variants to <paramref name="builder"/> and build.</summary>
    internal static Message WithHeader(MessageBuilder builder, string header, string thread)
    {
        switch (header)
        {
            case "none":
                builder.WithReturnRoute(ReturnRouteMode.None);
                break;
            case "all":
                builder.WithReturnRoute(ReturnRouteMode.All);
                break;
            case "thread":
                builder.WithReturnRoute(ReturnRouteMode.Thread, thread);
                break;
        }
        var message = builder.Build();
        if (header == "malformed")
        {
            message.AdditionalHeaders ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            message.AdditionalHeaders[ReturnRouteHeader.HeaderName] = JsonSerializer.SerializeToElement("All");
        }
        return message;
    }

    /// <summary>A reply from the server to <paramref name="to"/> threaded on <paramref name="inbound"/>.</summary>
    internal static Message ReplyTo(ReturnRouteTestHost host, Message inbound, string to, string name = "reply")
        => new MessageBuilder()
            .WithType(RecordingHandler.ProtocolUri + "/" + name)
            .WithFrom(host.ServerDid)
            .WithTo(to)
            .WithThid(inbound.Id)
            .Build();

    [Fact]
    public async Task Authcrypt_ping_with_return_route_all_gets_the_ping_response_as_the_200_body()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();

        var ping = host.Stamped(new MessageBuilder()
                .WithType(TrustPing.PingType)
                .WithFrom(host.Alice.Did)
                .WithTo(host.ServerDid)
                .WithReturnRoute(ReturnRouteMode.All))
            .Build();

        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, ping));

        response.Status.Should().Be(HttpStatusCode.OK, "an eligible request with return_route: all gets its reply on the response (FR-RR-04)");
        response.ContentType.Should().Be("application/didcomm-encrypted+json", "the media type is exact — no parameters");
        response.Body.Should().NotBeEmpty();

        var reply = await host.Alice.Client.UnpackAsync(response.BodyText);
        reply.Encrypted.Should().BeTrue();
        reply.Authenticated.Should().BeTrue("the reply is authcrypted from the DID that decrypted the ping");
        reply.SenderKid.Should().StartWith(host.ServerDid + "#");
        reply.RecipientKid.Should().StartWith(host.Alice.Did + "#");
        reply.Message.Type.Should().Be(TrustPing.ResponseType);
        reply.Message.From.Should().Be(host.ServerDid);
        reply.Message.To.Should().Equal(host.Alice.Did);
        reply.Message.Thid.Should().Be(ping.Id);

        host.Registry.Count.Should().Be(0, "HTTP sessions are request-scoped and never registered (JC-16)");
        host.LeakingLogLines().Should().BeEmpty("no log line may carry a DID or key id (JC-6)");
    }

    [Fact]
    public async Task The_same_ping_without_the_header_is_answered_202_with_an_empty_body()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();

        var ping = host.Stamped(new MessageBuilder()
                .WithType(TrustPing.PingType)
                .WithFrom(host.Alice.Did)
                .WithTo(host.ServerDid))
            .Build();

        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, ping));

        response.Status.Should().Be(HttpStatusCode.Accepted, "an absent header means none for this request (FR-RR-01)");
        response.Body.Should().BeEmpty();
        host.Logs.Entries.Should().Contain(e => e.Contains("returned-on-session=False"),
            "the handler still ran and produced its reply, which is logged rather than sent");
        host.LeakingLogLines().Should().BeEmpty();
    }
}
