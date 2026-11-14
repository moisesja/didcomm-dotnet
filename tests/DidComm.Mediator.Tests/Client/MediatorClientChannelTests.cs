using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Samples.Shared;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.Logging;
using NetCrypto;
using NetDid.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="MediatorClientChannel"/> as an exchange (#96 design §4.1, §4.3, D-18, §6.1): what is
/// sent and how, the pending bound taken before sending, HTTP no-answer, problem reports and their
/// codes, routes and their refusals before anything is sent, the live registry, disposal, and the
/// log lines (no DID, key id or attachment id; rate-limited where a peer can repeat them).
/// </summary>
public sealed class MediatorClientChannelTests : IAsyncLifetime
{
    private ClientWorld _w = null!;
    private readonly FakeSend _send = new();
    private readonly ManualTimeProvider _time = new();

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    // ---- what is sent ----

    [Fact]
    public async Task A_request_is_sent_once_with_return_route_all_the_route_endpoint_as_the_override_and_authcrypt_from_the_local_DID()
    {
        using var channel = Create(_w, _send, time: _time);
        _send.ReturnWith(async m => await _w.ReceiveAsync(_w.Status(m.Id)));
        var request = _w.Request();

        await channel.ExchangeAsync(HttpRoute, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);

        var call = _send.Calls.Should().ContainSingle().Which;
        call.Message.Should().BeSameAs(request, "the channel never stamps or rewrites the request (SendAsync stamps a copy, FR-RR-09)");
        call.Message.CreatedTime.Should().BeNull();
        call.Options.Recipients.Should().Equal(_w.Mediator.Did);
        call.Options.From.Should().Be(_w.Local.Did);
        call.Options.SignFrom.Should().BeNull();
        call.Options.ServiceEndpointOverride.Should().Be(Https);
        call.Options.ReturnRoute.Should().Be(ReturnRouteMode.All);
        call.Token.CanBeCanceled.Should().BeTrue("the send runs under the request's deadline");
    }

    [Fact]
    public async Task The_real_send_stamps_created_time_from_the_facade_clock_on_what_goes_out()
    {
        // FR-RR-09 is SendAsync's: the request carries no created_time and the transport receives a
        // copy stamped from DidCommOptions.Clock.
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_123);
        await using var world = await ClientWorld.CreateAsync(o => o.Clock = () => now);
        var router = new RecordingRouter();
        var client = new DidCommClient(world.Secrets, world.Services.GetRequiredService<Resolution.IDidKeyService>(), world.Services.GetRequiredService<Resolution.IServiceEndpointResolver>(), router,
            new DidCommOptions { Clock = () => now });
        using var channel = new MediatorClientChannel(client, new MediatorClientOptions(), receivesInbound: false, _time, loggerFactory: null);

        var act = () => channel.ExchangeAsync(HttpRoute, world.Request(), ClientSpecs.StatusRequest, world.Mediator.Did, world.Local.Did, default);

        await act.Should().ThrowAsync<TimeoutException>("the router answers 202 with nothing");
        var sent = await world.Client.UnpackAsync(System.Text.Encoding.UTF8.GetString(router.Requests.Single().Payload.Span));
        sent.Message.CreatedTime.Should().Be(now.ToUnixTimeSeconds());
        ReturnRouteHeader.Read(sent.Message).Mode.Should().Be(ReturnRouteMode.All);
        router.Requests.Single().ReturnRoute.Should().Be(ReturnRouteMode.All);
        router.Requests.Single().Endpoint.Should().Be(Https);
    }

    // ---- the pending bound ----

    [Fact]
    public async Task The_pending_bound_is_refused_before_anything_is_sent()
    {
        using var channel = Create(_w, _send, o => o.MaxPendingRequests = 2, time: _time);
        var first = channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);
        var second = channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);
        var third = _w.Request();

        var act = () => channel.ExchangeAsync(WebSocketRoute, third, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);

        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Message.Should().Contain("MaxPendingRequests").And.Contain(third.Id).And.NotContain(_w.Mediator.Did);
        _send.Count.Should().Be(2, "the third was never sent");
        _send.Calls.Select(c => c.Message.Id).Should().NotContain(third.Id);
        channel.PendingCount.Should().Be(2);
        first.IsCompleted.Should().BeFalse();
        second.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task A_slot_frees_when_a_request_ends()
    {
        using var channel = Create(_w, _send, o => o.MaxPendingRequests = 1, time: _time);
        var request = _w.Request();
        var first = channel.ExchangeAsync(WebSocketRoute, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(request.Id))).Should().BeTrue();
        await first;

        var next = channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);

        next.IsCompleted.Should().BeFalse("it was admitted and sent");
        _send.Count.Should().Be(2);
    }

    [Fact]
    public async Task A_request_id_that_is_already_waiting_is_refused_and_the_waiting_one_is_untouched()
    {
        using var channel = Create(_w, _send, time: _time);
        var request = _w.Request();
        var first = channel.ExchangeAsync(WebSocketRoute, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);

        var act = () => channel.ExchangeAsync(WebSocketRoute, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _send.Count.Should().Be(1);
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(request.Id))).Should().BeTrue();
        await first;
    }

    [Theory]
    [InlineData("")]
    [InlineData("an-id-that-is-much-longer-than-sixty-four-characters-and-so-could-never-be-matched-back")]
    public async Task A_request_id_that_could_never_be_matched_is_refused_before_sending(string id)
    {
        using var channel = Create(_w, _send, time: _time);
        var request = _w.Request();
        request.Id = id;

        var act = () => channel.ExchangeAsync(WebSocketRoute, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);

        await act.Should().ThrowAsync<ArgumentException>();
        _send.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_spec_the_matcher_could_never_complete_is_refused_before_sending()
    {
        using var channel = Create(_w, _send, time: _time);
        var unknown = new MediatorExchangeSpec("ping", new[] { MessageTypeUri.Parse("https://didcomm.org/trust-ping/2.0/ping-response") }, ExpectsDelivery: false);
        var inconsistent = new MediatorExchangeSpec("delivery-request", new[] { MessageTypeUri.Parse(MessagePickup.DeliveryType) }, ExpectsDelivery: false);
        var none = new MediatorExchangeSpec("nothing", Array.Empty<MessageTypeUri>(), ExpectsDelivery: false);

        foreach (var spec in new[] { unknown, inconsistent, none })
        {
            var act = () => channel.ExchangeAsync(WebSocketRoute, _w.Request(), spec, _w.Mediator.Did, _w.Local.Did, default);
            await act.Should().ThrowAsync<ArgumentException>(spec.Name);
        }
        _send.Count.Should().Be(0);
    }

    // ---- HTTP: no answer is an immediate timeout ----

    [Theory]
    [InlineData(202)]
    [InlineData(200)]
    public async Task Over_HTTP_a_response_without_an_answer_fails_at_once_naming_the_request_and_never_a_DID(int status)
    {
        using var channel = Create(_w, _send, time: _time);
        _send.Respond = (_, o, _) => Task.FromResult(FakeSend.Accepted(o.ServiceEndpointOverride!, returned: null, status));
        var request = _w.Request();

        var act = () => channel.ExchangeAsync(HttpRoute, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);

        var thrown = (await act.Should().ThrowAsync<TimeoutException>()).Which;
        thrown.Message.Should().Contain(request.Id).And.Contain("status-request").And.Contain("retry");
        foreach (var identifier in _w.Identifiers)
            thrown.Message.Should().NotContain(identifier);
        _time.ScheduledTimers.Should().Be(0, "nothing waits for the deadline");
        channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Over_HTTP_a_send_failure_propagates_as_it_is()
    {
        using var channel = Create(_w, _send, time: _time);
        var failure = new Exceptions.TransportException("accepted, reply unreadable", httpStatusCode: 200, scheme: "https");
        _send.Respond = (_, _, _) => Task.FromException<SendResult>(failure);

        var act = () => channel.ExchangeAsync(HttpRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);

        (await act.Should().ThrowAsync<Exceptions.TransportException>()).Which.Should().BeSameAs(failure);
        channel.PendingCount.Should().Be(0);
    }

    // ---- problem codes ----

    public static TheoryData<string?, string?> Codes => new()
    {
        { "e.m.live-mode-not-supported", "e.m.live-mode-not-supported" },
        { "w.p.xfer.retry_later-2", "w.p.xfer.retry_later-2" },
        { "e.p." + new string('a', 124), "e.p." + new string('a', 124) },
        { "e.p." + new string('a', 125), null },
        { "E.P.REQ", null },
        { "e.p.req\nINJECTED", null },
        { "e.p.req did:example:x", null },
        { "x.p.req", null },
        { "e.p..req", null },
        { "e.p", null },
        { "", null },
        { null, null },
    };

    [Theory]
    [MemberData(nameof(Codes))]
    public void A_problem_code_is_kept_only_when_well_formed(string? code, string? kept)
        => MediatorProblemCode.Sanitize(code).Should().Be(kept);

    [Fact]
    public async Task A_problem_report_with_a_hostile_code_fails_the_request_without_the_code()
    {
        using var channel = Create(_w, _send, time: _time);
        _send.ReturnWith(async m =>
        {
            var report = _w.Problem(m.Id);
            report.Body!["code"] = "e.p.req\r\nINJECTED " + _w.Mallory.Did;
            return await _w.ReceiveAsync(report);
        });

        var act = () => channel.ExchangeAsync(HttpRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);

        var thrown = (await act.Should().ThrowAsync<MediationProtocolException>()).Which;
        thrown.ProblemCode.Should().BeNull();
        thrown.Message.Should().NotContain("INJECTED").And.NotContain(_w.Mallory.Did);
    }

    [Fact]
    public async Task A_problem_report_whose_code_is_not_a_string_fails_the_request_with_no_code()
    {
        using var channel = Create(_w, _send, time: _time);
        _send.ReturnWith(async m =>
        {
            var report = _w.Problem(m.Id);
            report.Body!["code"] = new JsonObject { ["nested"] = 1 };
            return await _w.ReceiveAsync(report);
        });

        var act = () => channel.ExchangeAsync(HttpRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);

        (await act.Should().ThrowAsync<MediationProtocolException>()).Which.ProblemCode.Should().BeNull();
    }

    // ---- routes ----

    [Fact]
    public async Task An_override_is_used_as_it_is_and_nothing_is_resolved()
    {
        var resolved = 0;
        using var channel = Create(_w, _send, time: _time, resolveEndpoint: (_, _) => { resolved++; return Task.FromResult(Https); });

        var route = await channel.ResolveRouteAsync(_w.Mediator.Did, new Uri("http://10.0.0.1/didcomm"), requireWebSocket: false, default);

        route.Should().Be(new MediatorRoute(new Uri("http://10.0.0.1/didcomm"), IsHttp: true, IsWebSocket: false));
        resolved.Should().Be(0);
    }

    public static TheoryData<string, bool, bool> Schemes => new()
    {
        { "https://m.example/", true, false },
        { "http://m.example/", true, false },
        { "wss://m.example/", false, true },
        { "ws://m.example/", false, true },
        { "WSS://M.example/", false, true },
        { "tcp://m.example:1/", false, false },
    };

    [Theory]
    [MemberData(nameof(Schemes))]
    public async Task The_scheme_of_the_resolved_endpoint_decides_how_answers_come_back(string uri, bool isHttp, bool isWebSocket)
    {
        using var channel = Create(_w, _send, time: _time, resolveEndpoint: (_, _) => Task.FromResult(new Uri(uri)));

        var route = await channel.ResolveRouteAsync(_w.Mediator.Did, null, requireWebSocket: false, default);

        route.IsHttp.Should().Be(isHttp);
        route.IsWebSocket.Should().Be(isWebSocket);
        route.Endpoint.Should().Be(new Uri(uri));
    }

    [Theory]
    [InlineData("wss://m.example/")]
    [InlineData("ws://m.example/")]
    [InlineData("tcp://m.example:1/")]
    public async Task A_route_that_is_not_HTTP_is_refused_when_the_channel_cannot_receive_naming_the_fix(string uri)
    {
        using var channel = Create(_w, _send, receivesInbound: false, time: _time);

        var act = () => channel.ResolveRouteAsync(_w.Mediator.Did, new Uri(uri), requireWebSocket: false, default);

        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Message.Should().Contain("UseWebSocketTransport(o => o.ReceiveInbound = true)").And.Contain("UseInboundDispatch()");
        _send.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_resolved_WebSocket_route_is_refused_too_when_the_channel_cannot_receive()
    {
        using var channel = Create(_w, _send, receivesInbound: false, time: _time, resolveEndpoint: (_, _) => Task.FromResult(Wss));

        var act = () => channel.ResolveRouteAsync(_w.Mediator.Did, null, requireWebSocket: false, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("https://m.example/", true)]
    [InlineData("tcp://m.example:1/", true)]
    [InlineData("wss://m.example/", false)]
    public async Task Live_delivery_refuses_a_route_that_is_not_a_WebSocket_before_anything_is_sent(string uri, bool refused)
    {
        using var channel = Create(_w, _send, time: _time);

        var act = () => channel.ResolveRouteAsync(_w.Mediator.Did, new Uri(uri), requireWebSocket: true, default);

        if (refused)
            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("WebSocket");
        else
            (await act()).IsWebSocket.Should().BeTrue();
        _send.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_relative_override_is_refused()
    {
        using var channel = Create(_w, _send, time: _time);

        var act = () => channel.ResolveRouteAsync(_w.Mediator.Did, new Uri("/inbox", UriKind.Relative), requireWebSocket: false, default);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Without_an_override_the_route_is_the_first_entry_of_the_mediators_DID()
    {
        var mediator = await PeerIdentityFactory.CreateAsync(
            _w.Services.GetRequiredService<IDidManager>(), _w.Services.GetRequiredService<IKeyGenerator>(), _w.Services.GetRequiredService<ICryptoProvider>(),
            new DidCommServiceSpec("wss://mediator.example/ws"));
        using var channel = new MediatorClientChannel(_w.Client, new MediatorClientOptions(), receivesInbound: true, _time, loggerFactory: null);

        var route = await channel.ResolveRouteAsync(mediator.Did, null, requireWebSocket: true, default);

        route.Should().Be(new MediatorRoute(new Uri("wss://mediator.example/ws"), IsHttp: false, IsWebSocket: true));
    }

    [Fact]
    public async Task A_routed_mediator_is_refused_by_the_real_resolution()
    {
        var mediator = await PeerIdentityFactory.CreateAsync(
            _w.Services.GetRequiredService<IDidManager>(), _w.Services.GetRequiredService<IKeyGenerator>(), _w.Services.GetRequiredService<ICryptoProvider>(),
            new DidCommServiceSpec(_w.Alice.Did));
        using var channel = new MediatorClientChannel(_w.Client, new MediatorClientOptions(), receivesInbound: true, _time, loggerFactory: null);

        var act = () => channel.ResolveRouteAsync(mediator.Did, null, requireWebSocket: false, default);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(DidCommClient.RoutedReturnRouteRefusal);
    }

    [Fact]
    public async Task Resolving_the_endpoint_is_bounded_by_the_request_timeout_and_names_no_DID()
    {
        var token = default(CancellationToken);
        using var channel = Create(_w, _send, time: _time, resolveEndpoint: (_, ct) =>
        {
            token = ct;
            return new TaskCompletionSource<Uri>().Task; // never answers, ignores cancellation
        });

        var resolving = channel.ResolveRouteAsync(_w.Mediator.Did, null, requireWebSocket: false, default);
        _time.Advance(channel.Options.RequestTimeout);

        var thrown = (await FluentActions.Awaiting(() => resolving).Should().ThrowAsync<TimeoutException>()).Which;
        thrown.Message.Should().NotContain(_w.Mediator.Did);
        token.IsCancellationRequested.Should().BeTrue("the resolver was told");
        _send.Count.Should().Be(0);
    }

    [Fact]
    public async Task Cancelling_the_resolution_is_a_cancellation_not_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        using var channel = Create(_w, _send, time: _time, resolveEndpoint: (_, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => Https, TaskScheduler.Default));

        var resolving = channel.ResolveRouteAsync(_w.Mediator.Did, null, requireWebSocket: false, cts.Token);
        await cts.CancelAsync();

        (await FluentActions.Awaiting(() => resolving).Should().ThrowAsync<OperationCanceledException>()).Which.CancellationToken.Should().Be(cts.Token);
    }

    // ---- the live registry ----

    [Fact]
    public void One_live_stream_per_pair()
    {
        using var channel = Create(_w, _send, time: _time);
        using var first = channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did);

        var act = () => channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().NotContain(_w.Mediator.Did).And.NotContain(_w.Local.Did);
        using var other = channel.RegisterLiveStream(_w.Mediator.Did, _w.OtherLocal.Did);
        using var otherMediator = channel.RegisterLiveStream(_w.Alice.Did, _w.Local.Did);
        channel.LiveStreamCount.Should().Be(3);
    }

    [Fact]
    public void The_live_stream_bound_holds()
    {
        using var channel = Create(_w, _send, time: _time);
        var streams = Enumerable.Range(0, MediatorClientChannel.MaxLiveStreams)
            .Select(i => channel.RegisterLiveStream(_w.Mediator.Did, $"did:example:local-{i}"))
            .ToList();

        var act = () => channel.RegisterLiveStream(_w.Mediator.Did, "did:example:one-more");

        act.Should().Throw<InvalidOperationException>();
        streams[0].Dispose();
        using var admitted = channel.RegisterLiveStream(_w.Mediator.Did, "did:example:one-more");
        channel.LiveStreamCount.Should().Be(MediatorClientChannel.MaxLiveStreams);
        streams.ForEach(s => s.Dispose());
    }

    [Fact]
    public void Disposing_a_registration_removes_exactly_that_entry()
    {
        using var channel = Create(_w, _send, time: _time);
        var old = channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did);
        old.Dispose();
        using var newer = channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did);

        old.Dispose();

        channel.LiveStreamCount.Should().Be(1, "the newer registration for the same pair stays");
        var act = () => channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task A_full_push_buffer_drops_the_push_without_blocking()
    {
        using var channel = Create(_w, _send, time: _time);
        using var stream = channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did);
        for (var i = 0; i < LiveRegistration.PushCapacity; i++)
            channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Delivery(null))).Should().BeTrue();

        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Delivery(null))).Should().BeFalse("dropped, not acknowledged");

        var buffered = 0;
        while (stream.Pushes.TryRead(out _))
            buffered++;
        buffered.Should().Be(LiveRegistration.PushCapacity);
    }

    [Fact]
    public async Task A_push_after_the_stream_ended_is_dropped()
    {
        using var channel = Create(_w, _send, time: _time);
        var stream = channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did);
        stream.Dispose();

        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Delivery(null))).Should().BeFalse();
        channel.LiveStreamCount.Should().Be(0);
    }

    // ---- disposal ----

    [Fact]
    public async Task Disposal_fails_waiting_requests_ends_live_streams_and_refuses_new_work()
    {
        var channel = Create(_w, _send, time: _time);
        var waiting = channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);
        var sendGate = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _send.Respond = (_, _, _) => sendGate.Task;
        var sending = channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);
        var stream = channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did);

        channel.Dispose();
        channel.Dispose();
        await channel.DisposeAsync();

        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<ObjectDisposedException>();
        await FluentActions.Awaiting(() => sending).Should().ThrowAsync<ObjectDisposedException>("a send in flight is abandoned too");
        await FluentActions.Awaiting(() => stream.Pushes.Completion).Should().ThrowAsync<ObjectDisposedException>();
        channel.PendingCount.Should().Be(0);
        channel.LiveStreamCount.Should().Be(0);
        await FluentActions.Awaiting(() => channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default)).Should().ThrowAsync<ObjectDisposedException>();
        await FluentActions.Awaiting(() => channel.ResolveRouteAsync(_w.Mediator.Did, Https, false, default)).Should().ThrowAsync<ObjectDisposedException>();
        FluentActions.Invoking(() => channel.RegisterLiveStream(_w.Mediator.Did, _w.OtherLocal.Did)).Should().Throw<ObjectDisposedException>();
        await FluentActions.Awaiting(() => channel.UnpackDeliveredAsync(new byte[] { 1 }, default)).Should().ThrowAsync<ObjectDisposedException>();
        stream.Dispose();
        sendGate.TrySetResult(FakeSend.Accepted(Wss));
    }

    // ---- log lines ----

    [Fact]
    public async Task No_log_line_carries_a_DID_a_key_id_or_an_attachment_id_and_peer_repeatable_lines_are_rate_limited()
    {
        var logs = new ClientLogCapture();
        using var channel = Create(_w, _send, o => o.MaxPendingRequests = 8, time: _time, logs: logs);
        const string attachmentId = "attachment-id-that-must-not-be-logged";

        // Answers for nobody (twice), an ambiguous answer, a dropped push, a problem report, an HTTP
        // no-answer, a timeout, an identity failure and a completed request.
        var a = channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.MediateRequest, _w.Mediator.Did, _w.Local.Did, default);
        var b = channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.MediateRequest, _w.Mediator.Did, _w.Local.Did, default);
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(Guid.NewGuid().ToString()))).Should().BeFalse();
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Grant(null))).Should().BeFalse();
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(Guid.NewGuid().ToString()))).Should().BeFalse();
        var push = _w.Delivery(null);
        push.Attachments = new List<Attachment> { new() { Id = attachmentId, Data = new AttachmentData { Base64 = "eyJ9" } } };
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(push)).Should().BeFalse();
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Grant(Guid.NewGuid().ToString(), from: _w.Mallory.Did))).Should().BeFalse();
        var identityLines = logs.Entries.Count;
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Grant(Guid.NewGuid().ToString()), AnswerEnvelope.Anoncrypt)).Should().BeFalse();
        logs.Entries.Count.Should().Be(identityLines, "an identity failure is other traffic: not logged");
        _send.ReturnWith(async m => await _w.ReceiveAsync(_w.Problem(m.Id)));
        await FluentActions.Awaiting(() => channel.ExchangeAsync(HttpRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default)).Should().ThrowAsync<MediationProtocolException>();
        _send.ReturnWith(_ => Task.FromResult<UnpackResult?>(null));
        await FluentActions.Awaiting(() => channel.ExchangeAsync(HttpRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default)).Should().ThrowAsync<TimeoutException>();
        _time.Advance(channel.Options.RequestTimeout);
        await FluentActions.Awaiting(() => a).Should().ThrowAsync<TimeoutException>();
        await FluentActions.Awaiting(() => b).Should().ThrowAsync<TimeoutException>();

        var lines = logs.Of<MediatorClientChannel>();
        lines.Should().NotBeEmpty();
        foreach (var line in lines)
        {
            foreach (var identifier in _w.Identifiers.Append(attachmentId).Append(push.Id))
                line.Everything.Should().NotContain(identifier);
        }
        var unclaimed = lines.Where(l => l.Formatted.Contains("no waiting request takes", StringComparison.Ordinal)).ToArray();
        unclaimed.Should().HaveCountGreaterThanOrEqualTo(3);
        unclaimed[0].Level.Should().Be(LogLevel.Warning, "the first of a peer-repeatable line");
        unclaimed.Skip(1).Should().OnlyContain(l => l.Level == LogLevel.Debug, "the rest until the 1000th");
        lines.Should().Contain(l => l.Formatted.Contains("more than one waiting request", StringComparison.Ordinal));
        lines.Should().Contain(l => l.Formatted.Contains("live delivery push", StringComparison.Ordinal));
        lines.Should().Contain(l => l.Formatted.Contains("problem report", StringComparison.Ordinal));
        lines.Should().Contain(l => l.Level == LogLevel.Debug && l.Formatted.Contains("ms", StringComparison.Ordinal) && l.Formatted.Contains("status-request", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_throwing_logging_provider_changes_nothing()
    {
        using var channel = Create(_w, _send, time: _time, logs: new ThrowingLoggerFactory());
        var request = _w.Request();
        var exchange = channel.ExchangeAsync(WebSocketRoute, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);

        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(Guid.NewGuid().ToString()))).Should().BeFalse();
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(request.Id))).Should().BeTrue();

        (await exchange).Thid.Should().Be(request.Id);
    }

    private sealed class RecordingRouter : ITransportRouter
    {
        public System.Collections.Concurrent.ConcurrentQueue<TransportRequest> Requests { get; } = new();

        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
        {
            Requests.Enqueue(request);
            return Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: 202));
        }
    }

    private sealed class ThrowingLoggerFactory : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new Throwing();

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class Throwing : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => throw new InvalidOperationException("down");

            public bool IsEnabled(LogLevel logLevel) => throw new InvalidOperationException("down");

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => throw new InvalidOperationException("down");
        }
    }
}
