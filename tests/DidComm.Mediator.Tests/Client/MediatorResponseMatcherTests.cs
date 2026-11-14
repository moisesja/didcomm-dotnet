using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The matcher of #96 design §4.2 (D-6, D-7): the identity matrix of §9 — every shape that is not
/// an authcrypt answer from the request's mediator to its local DID, including a post-sign tamper
/// and a self-consistent forgery, leaves the pending request untouched and the genuine answer then
/// completes it — and every row of the table, unbound (the dispatcher's correlator call) and bound
/// (the HTTP response of a request). Real envelopes; a few rules a real unpack cannot violate are
/// shown on crafted verified snapshots.
/// </summary>
public sealed class MediatorResponseMatcherTests : IAsyncLifetime
{
    private ClientWorld _w = null!;
    private readonly FakeSend _send = new();
    private readonly ManualTimeProvider _time = new();
    private MediatorClientChannel _channel = null!;

    public async Task InitializeAsync()
    {
        _w = await ClientWorld.CreateAsync();
        _channel = Create(_w, _send, time: _time);
    }

    public async Task DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _w.DisposeAsync();
    }

    // ---- the identity matrix: nothing but the genuine answer completes the request ----

    public static TheoryData<string> Impostors => new()
    {
        "anoncrypt",
        "signed-only",
        "anoncrypt(sign)",
        "plaintext",
        "authcrypt from another DID (a self-consistent forgery with our thid)",
        "decrypted by another local DID",
        "local DID missing from to",
        "forward type",
        "fallback snapshot of the genuine answer",
        "crafted: from is not the sender key's DID",
        "crafted: signer disagrees with the sender",
        "crafted: no sender key",
        "crafted: not encrypted",
    };

    [Theory]
    [MemberData(nameof(Impostors))]
    public async Task Only_the_genuine_answer_completes_the_request_and_an_impostor_changes_nothing(string impostor)
    {
        var (request, exchange) = Start(ClientSpecs.MediateRequest);
        var lookupsBefore = _channel.Lookups;

        _channel.AsCorrelator().OnInbound(await ImpostorAsync(impostor, request.Id)).Should().BeFalse(impostor);

        exchange.IsCompleted.Should().BeFalse("an impostor neither answers nor cancels the request");
        _channel.PendingCount.Should().Be(1, "nor consumes it");
        // Two impostors pass the identity rule (a valid authcrypt from its own `from` to a DID of
        // ours) and are refused by the request's pair; every other one never reaches a lookup.
        if (!impostor.StartsWith("authcrypt from another DID", StringComparison.Ordinal) && impostor != "decrypted by another local DID")
            _channel.Lookups.Should().Be(lookupsBefore, "identity is decided before the pending map is looked at");

        var genuine = await _w.SnapshotAsync(_w.Grant(request.Id));
        _channel.AsCorrelator().OnInbound(genuine).Should().BeTrue();
        (await exchange).Id.Should().Be(genuine.Id);
    }

    [Fact]
    public async Task A_crafted_snapshot_with_every_field_right_is_accepted_so_each_crafted_impostor_fails_on_its_one_field()
    {
        var (request, exchange) = Start(ClientSpecs.MediateRequest);

        _channel.AsCorrelator().OnInbound(await ImpostorAsync("crafted: the genuine fields", request.Id)).Should().BeTrue();

        await exchange;
    }

    [Fact]
    public async Task A_post_sign_tamper_never_becomes_a_snapshot_and_the_genuine_answer_still_completes()
    {
        var (request, exchange) = Start(ClientSpecs.MediateRequest);
        var packed = await _w.PackAsync(_w.Grant(request.Id));
        var tampered = Tamper(packed);

        await FluentActions.Awaiting(() => _w.Client.UnpackAsync(tampered)).Should().ThrowAsync<DidCommException>("a tampered envelope does not unpack, so the dispatcher never sees it");
        // from = the mediator, but Mallory's sender key: the envelope layer refuses it (FR-CONSIST-01).
        var forgedFrom = await Capture(() => _w.ReceiveAsync(_w.Grant(request.Id), from: _w.Mallory.Did));
        forgedFrom.Should().BeOfType<ConsistencyException>("a message whose from is the mediator but whose sender key is Mallory's is refused at unpack (FR-CONSIST-01) and never becomes a snapshot");

        exchange.IsCompleted.Should().BeFalse();
        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Grant(request.Id))).Should().BeTrue();
        await exchange;
    }

    [Theory]
    [InlineData(AnswerEnvelope.Authcrypt)]
    [InlineData(AnswerEnvelope.AuthcryptOfSigned)]
    [InlineData(AnswerEnvelope.AnoncryptOfAuthcrypt)]
    public async Task Every_authcrypt_shape_of_the_mediators_answer_completes(AnswerEnvelope shape)
    {
        var (request, exchange) = Start(ClientSpecs.MediateRequest);

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Grant(request.Id), shape)).Should().BeTrue();

        (await exchange).Type.Should().Be(CoordinateMediation.MediateGrantType);
    }

    [Fact]
    public async Task A_forgery_bound_to_the_HTTP_response_fails_the_request_as_no_answer()
    {
        _send.ReturnWith(async m => await _w.ReceiveAsync(_w.Grant(m.Id, from: _w.Mallory.Did)));

        var act = () => _channel.ExchangeAsync(HttpRoute, _w.Request(), ClientSpecs.MediateRequest, _w.Mediator.Did, _w.Local.Did, default);

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Message.Should().Contain("no answer in the HTTP response");
    }

    // ---- row 1: the thread names a pending request ----

    [Fact]
    public async Task Row1_unbound_an_answer_threaded_to_a_pending_request_completes_it()
    {
        var (request, exchange) = Start(ClientSpecs.StatusRequest);

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(request.Id, 3))).Should().BeTrue();

        var answer = await exchange;
        answer.Type.Should().Be(MessagePickup.StatusType);
        answer.Thid.Should().Be(request.Id);
        _channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Row1_bound_the_HTTP_response_threaded_to_its_request_completes_it()
    {
        _send.ReturnWith(async m => await _w.ReceiveAsync(_w.Status(m.Id, 2)));

        var answer = await _channel.ExchangeAsync(HttpRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);

        answer.Type.Should().Be(MessagePickup.StatusType);
    }

    [Fact]
    public async Task Row1_bound_an_HTTP_response_threaded_to_another_pending_request_completes_that_one_and_fails_its_own()
    {
        var (other, otherExchange) = Start(ClientSpecs.StatusRequest);
        _send.ReturnWith(async _ => await _w.ReceiveAsync(_w.Status(other.Id, 1)));

        var act = () => _channel.ExchangeAsync(HttpRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);

        await act.Should().ThrowAsync<TimeoutException>();
        (await otherExchange).Thid.Should().Be(other.Id, "P may be another request than the one whose response carried it");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Row1_an_answer_of_a_type_the_request_does_not_expect_does_not_complete_it(bool bound)
    {
        if (bound)
        {
            _send.ReturnWith(async m => await _w.ReceiveAsync(_w.Status(m.Id)));
            var act = () => _channel.ExchangeAsync(HttpRoute, _w.Request(), ClientSpecs.MediateRequest, _w.Mediator.Did, _w.Local.Did, default);
            await act.Should().ThrowAsync<TimeoutException>();
            return;
        }

        var (request, exchange) = Start(ClientSpecs.MediateRequest);
        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(request.Id))).Should().BeFalse();
        exchange.IsCompleted.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Row1_a_problem_report_by_pthid_or_by_thid_fails_the_request_with_its_sanitized_code(bool bound, bool inThid)
    {
        Task<InboundMessageSnapshot> exchange;
        string requestId;
        if (bound)
        {
            var request = _w.Request();
            requestId = request.Id;
            _send.ReturnWith(async m => await _w.ReceiveAsync(_w.Problem(m.Id, MessagePickup.LiveModeNotSupportedCode, inThid)));
            exchange = _channel.ExchangeAsync(HttpRoute, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);
        }
        else
        {
            var started = Start(ClientSpecs.StatusRequest);
            requestId = started.Request.Id;
            exchange = started.Exchange;
            _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Problem(requestId, MessagePickup.LiveModeNotSupportedCode, inThid))).Should().BeTrue();
        }

        var thrown = (await FluentActions.Awaiting(() => exchange).Should().ThrowAsync<MediationProtocolException>()).Which;
        thrown.RequestId.Should().Be(requestId);
        thrown.ProblemCode.Should().Be(MessagePickup.LiveModeNotSupportedCode);
        thrown.Message.Should().Contain(requestId).And.NotContain(_w.Mediator.Did).And.NotContain(_w.Local.Did);
    }

    // ---- row 2: a thread that names no pending request ----

    [Fact]
    public async Task Row2_unbound_an_answer_for_no_pending_request_is_dropped()
    {
        var (_, exchange) = Start(ClientSpecs.StatusRequest);

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(Guid.NewGuid().ToString()))).Should().BeFalse();

        exchange.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Row2_bound_a_response_threaded_to_no_pending_request_fails_the_request_at_once()
    {
        _send.ReturnWith(async _ => await _w.ReceiveAsync(_w.Status(Guid.NewGuid().ToString())));
        var request = _w.Request();

        var act = () => _channel.ExchangeAsync(HttpRoute, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);

        var thrown = (await act.Should().ThrowAsync<TimeoutException>()).Which;
        thrown.Message.Should().Contain(request.Id).And.Contain("status-request").And.NotContain(_w.Mediator.Did).And.NotContain(_w.Local.Did);
        _time.ScheduledTimers.Should().Be(0, "it failed at once, without waiting out the deadline");
    }

    [Fact]
    public async Task A_thread_longer_than_64_characters_is_never_looked_up()
    {
        var (_, exchange) = Start(ClientSpecs.StatusRequest);
        var before = _channel.Lookups;

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(new string('a', 200_000)))).Should().BeFalse();
        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(new string('b', MediatorClientChannel.MaxThreadIdLength + 1)))).Should().BeFalse();

        _channel.Lookups.Should().Be(before);
        exchange.IsCompleted.Should().BeFalse();
    }

    // ---- row 3: a delivery without a thread is a live push ----

    [Fact]
    public async Task Row3_unbound_a_threadless_delivery_reaches_the_live_stream_of_its_pair()
    {
        using var stream = _channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did);
        var push = await _w.SnapshotAsync(_w.Delivery(thid: null));

        _channel.AsCorrelator().OnInbound(push).Should().BeTrue();

        stream.Pushes.TryRead(out var buffered).Should().BeTrue();
        buffered.Should().BeSameAs(push);
    }

    [Fact]
    public async Task Row3_unbound_a_threadless_delivery_never_completes_a_pending_delivery_request()
    {
        var (_, exchange) = Start(ClientSpecs.DeliveryRequest);

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Delivery(thid: null))).Should().BeFalse("no stream is open; a push is never an answer");

        exchange.IsCompleted.Should().BeFalse();
        using var stream = _channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did);
        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Delivery(thid: null))).Should().BeTrue("with a stream open the push goes there");
        exchange.IsCompleted.Should().BeFalse("and still not to the request");
    }

    [Fact]
    public async Task Row3_unbound_a_push_from_another_pair_does_not_reach_the_stream()
    {
        using var stream = _channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did);

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Delivery(null, to: _w.OtherLocal.Did), to: _w.OtherLocal.Did)).Should().BeFalse();
        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Delivery(null, from: _w.Mallory.Did))).Should().BeFalse();

        stream.Pushes.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Row3_a_threaded_delivery_never_reaches_a_stream()
    {
        using var stream = _channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did);

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Delivery(Guid.NewGuid().ToString()))).Should().BeFalse();

        stream.Pushes.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Row3_bound_a_threadless_delivery_completes_the_HTTP_delivery_request_and_never_reaches_a_stream()
    {
        using var stream = _channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did);
        _send.ReturnWith(async _ => await _w.ReceiveAsync(_w.Delivery(thid: null)));

        var answer = await _channel.ExchangeAsync(HttpRoute, _w.Request(MessagePickup.DeliveryRequestType), ClientSpecs.DeliveryRequest, _w.Mediator.Did, _w.Local.Did, default);

        answer.Type.Should().Be(MessagePickup.DeliveryType);
        stream.Pushes.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Row3_bound_a_threadless_delivery_to_a_request_that_does_not_expect_one_fails_it_and_never_reaches_a_stream()
    {
        using var stream = _channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did);
        _send.ReturnWith(async _ => await _w.ReceiveAsync(_w.Delivery(thid: null)));

        var act = () => _channel.ExchangeAsync(HttpRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);

        await act.Should().ThrowAsync<TimeoutException>();
        stream.Pushes.TryRead(out _).Should().BeFalse();
    }

    // ---- row 4: a problem report without a thread ----

    [Fact]
    public async Task Row4_unbound_a_threadless_problem_report_is_dropped()
    {
        var (request, exchange) = Start(ClientSpecs.StatusRequest);
        var report = _w.Problem(request.Id);
        report.Pthid = null;

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(report)).Should().BeFalse();

        exchange.IsCompleted.Should().BeFalse("pthid is REQUIRED");
    }

    [Fact]
    public async Task Row4_bound_a_threadless_problem_report_fails_its_HTTP_request_with_the_code()
    {
        _send.ReturnWith(async m =>
        {
            var report = _w.Problem(m.Id, "e.p.req.not-enrolled");
            report.Pthid = null;
            return await _w.ReceiveAsync(report);
        });

        var act = () => _channel.ExchangeAsync(HttpRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);

        (await act.Should().ThrowAsync<MediationProtocolException>()).Which.ProblemCode.Should().Be("e.p.req.not-enrolled");
    }

    // ---- row 5: another answer type without a thread ----

    [Fact]
    public async Task Row5_unbound_the_only_waiting_request_of_the_pair_expecting_the_type_takes_it()
    {
        var (_, statusExchange) = Start(ClientSpecs.StatusRequest);
        var (_, grantExchange) = Start(ClientSpecs.MediateRequest);
        var (_, otherLocal) = Start(ClientSpecs.MediateRequest, local: _w.OtherLocal.Did);

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Grant(thid: null))).Should().BeTrue();

        (await grantExchange).Type.Should().Be(CoordinateMediation.MediateGrantType);
        statusExchange.IsCompleted.Should().BeFalse("it does not expect a grant");
        otherLocal.IsCompleted.Should().BeFalse("another pair");
    }

    [Fact]
    public async Task Row5_unbound_with_no_candidate_the_answer_is_dropped()
    {
        var (_, statusExchange) = Start(ClientSpecs.StatusRequest);

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Grant(thid: null))).Should().BeFalse();

        statusExchange.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Row5_unbound_two_candidates_are_ambiguous_and_both_keep_waiting_until_they_time_out()
    {
        var (_, first) = Start(ClientSpecs.MediateRequest);
        var (_, second) = Start(ClientSpecs.MediateRequest);

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Grant(thid: null))).Should().BeFalse();

        first.IsCompleted.Should().BeFalse();
        second.IsCompleted.Should().BeFalse();
        _time.Advance(_channel.Options.RequestTimeout);
        await FluentActions.Awaiting(() => first).Should().ThrowAsync<TimeoutException>();
        await FluentActions.Awaiting(() => second).Should().ThrowAsync<TimeoutException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Row5_bound_a_threadless_answer_completes_its_HTTP_request_only_when_it_expects_the_type(bool expected)
    {
        _send.ReturnWith(async _ => await _w.ReceiveAsync(_w.Grant(thid: null)));
        var spec = expected ? ClientSpecs.MediateRequest : ClientSpecs.StatusRequest;

        var act = () => _channel.ExchangeAsync(HttpRoute, _w.Request(), spec, _w.Mediator.Did, _w.Local.Did, default);

        if (expected)
            (await act()).Type.Should().Be(CoordinateMediation.MediateGrantType);
        else
            await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Row5_bound_a_threadless_answer_from_another_pair_does_not_complete_the_HTTP_request()
    {
        _send.ReturnWith(async _ => await _w.ReceiveAsync(_w.Grant(thid: null, to: _w.OtherLocal.Did), to: _w.OtherLocal.Did));

        var act = () => _channel.ExchangeAsync(HttpRoute, _w.Request(), ClientSpecs.MediateRequest, _w.Mediator.Did, _w.Local.Did, default);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    // ---- the fast path ----

    [Fact]
    public async Task When_idle_nothing_is_looked_at()
    {
        var answer = await _w.SnapshotAsync(_w.Grant(Guid.NewGuid().ToString()));

        _channel.AsCorrelator().OnInbound(answer).Should().BeFalse();
        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Delivery(null))).Should().BeFalse();

        _channel.Lookups.Should().Be(0);
    }

    [Fact]
    public async Task After_the_last_request_ends_the_channel_is_idle_again()
    {
        var (request, exchange) = Start(ClientSpecs.StatusRequest);
        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(request.Id))).Should().BeTrue();
        await exchange;
        var before = _channel.Lookups;

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(request.Id))).Should().BeFalse("a late duplicate");

        _channel.Lookups.Should().Be(before, "the idle fast path");
    }

    [Fact]
    public async Task Types_that_are_not_mediator_answers_are_not_looked_at()
    {
        var (request, exchange) = Start(ClientSpecs.StatusRequest);
        var before = _channel.Lookups;

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Answer(MessagePickup.StatusRequestType, request.Id))).Should().BeFalse();
        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Answer("https://example.org/other/1.0/status", request.Id))).Should().BeFalse();

        _channel.Lookups.Should().Be(before);
        exchange.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Answer_types_match_as_the_registry_does_ignoring_case_punctuation_and_minor_version()
    {
        var (request, exchange) = Start(ClientSpecs.StatusRequest);

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Answer("https://didcomm.org/messagepickup/3.1/STATUS", request.Id))).Should().BeTrue();

        await exchange;
    }

    // ---- helpers ----

    private (Message Request, Task<InboundMessageSnapshot> Exchange) Start(MediatorExchangeSpec spec, string? local = null)
    {
        var request = _w.Request(from: local);
        var exchange = _channel.ExchangeAsync(WebSocketRoute, request, spec, _w.Mediator.Did, local ?? _w.Local.Did, default);
        exchange.IsCompleted.Should().BeFalse("the request waits for its answer");
        return (request, exchange);
    }

    private async Task<InboundMessageSnapshot> ImpostorAsync(string impostor, string thid)
    {
        var grant = _w.Grant(thid);
        switch (impostor)
        {
            case "anoncrypt": return await _w.SnapshotAsync(grant, AnswerEnvelope.Anoncrypt);
            case "signed-only": return await _w.SnapshotAsync(grant, AnswerEnvelope.SignedOnly);
            case "anoncrypt(sign)": return await _w.SnapshotAsync(grant, AnswerEnvelope.AnoncryptOfSigned);
            case "plaintext": return await _w.SnapshotAsync(grant, AnswerEnvelope.Plaintext);
            case "authcrypt from another DID (a self-consistent forgery with our thid)":
                return await _w.SnapshotAsync(_w.Grant(thid, from: _w.Mallory.Did));
            case "decrypted by another local DID":
                return await _w.SnapshotAsync(_w.Grant(thid, to: _w.OtherLocal.Did), to: _w.OtherLocal.Did);
            case "local DID missing from to":
                // Unpack refuses it (the decrypting key's DID must be in `to`), so a crafted
                // verified snapshot shows the matcher's own rule.
                return CraftedSnapshot.Verified(_w.Grant(thid, to: _w.OtherLocal.Did), senderKid: _w.Mediator.Privates[0].Kid, recipientKid: _w.Local.Privates[0].Kid);
            case "forward type":
            {
                var forward = ForwardMessage.Create(_w.Local.Did, _w.Local.Did, new[] { await _w.PackAsync(grant) });
                forward.From = _w.Mediator.Did;
                forward.Thid = thid;
                return await _w.SnapshotAsync(forward);
            }
            case "fallback snapshot of the genuine answer":
                return InboundMessageSnapshot.CreateFallback(await _w.ReceiveAsync(grant));
            case "crafted: from is not the sender key's DID":
                return CraftedSnapshot.Verified(_w.Grant(thid), senderKid: _w.Mallory.Privates[0].Kid, recipientKid: _w.Local.Privates[0].Kid);
            case "crafted: signer disagrees with the sender":
                return CraftedSnapshot.Verified(_w.Grant(thid), senderKid: _w.Mediator.Privates[0].Kid, recipientKid: _w.Local.Privates[0].Kid, signerKid: _w.Mallory.Privates[1].Kid);
            case "crafted: no sender key":
                return CraftedSnapshot.Verified(_w.Grant(thid), senderKid: null, recipientKid: _w.Local.Privates[0].Kid);
            case "crafted: the genuine fields":
                // The control: the same crafted path with every field right is accepted, so each
                // crafted impostor above is refused for its one wrong field.
                return CraftedSnapshot.Verified(_w.Grant(thid), senderKid: _w.Mediator.Privates[0].Kid, recipientKid: _w.Local.Privates[0].Kid);
            case "crafted: not encrypted":
                return CraftedSnapshot.Verified(_w.Grant(thid), senderKid: _w.Mediator.Privates[0].Kid, recipientKid: _w.Local.Privates[0].Kid, encrypted: false);
            default:
                throw new ArgumentOutOfRangeException(nameof(impostor));
        }
    }

    private static async Task<Exception?> Capture(Func<Task> act)
    {
        try
        {
            await act();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static string Tamper(string packed)
    {
        var json = JsonNode.Parse(packed)!.AsObject();
        var ciphertext = json["ciphertext"]!.GetValue<string>();
        json["ciphertext"] = (ciphertext[0] == 'A' ? "B" : "A") + ciphertext[1..];
        return json.ToJsonString();
    }
}
