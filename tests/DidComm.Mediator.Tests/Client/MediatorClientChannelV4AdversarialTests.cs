using System.Text;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.TestSupport;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// Red-team pass ADV-3 over the recipient role of Message Pickup 4.0 (#100): threading and identity
/// in the matcher. Can a 4.0 <c>status</c>, <c>delivery</c> or <c>problem-report</c> complete a
/// request it does not answer — sent by a DID other than the mediator, not authcrypted to the
/// requesting DID, of the other version, naming the request in the wrong header — or reach a live
/// stream it does not belong to? Every test that is not skipped is an attack that was defeated.
/// </summary>
public sealed class MediatorClientChannelV4AdversarialTests : IAsyncLifetime
{
    private ClientWorld _w = null!;
    private readonly FakeSend _send = new();
    private readonly ManualTimeProvider _time = new();

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    private string M => _w.Mediator.Did;

    private string L => _w.Local.Did;

    public static TheoryData<string, string> Forgeries
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var answer in new[] { "status", "delivery", "problem-report" })
                foreach (var forgery in new[] { "authcrypted by a stranger", "authcrypted by the mediator to another DID of ours", "anoncrypt", "signed only", "anoncrypt(sign)", "plaintext", "authcrypt(sign) by a stranger" })
                    data.Add(answer, forgery);
            return data;
        }
    }

    // ---- 1. only the mediator, to the requesting DID, completes a 4.0 request ----------------

    [Theory]
    [MemberData(nameof(Forgeries))]
    public async Task A_4_0_answer_naming_a_waiting_request_completes_it_only_from_its_mediator_to_its_DID(string answer, string forgery)
    {
        // Defeated. The forgery names the waiting request exactly as the real answer does (the id
        // re-cased in thid, or pthid for a problem report). Identity comes first: a shape without an
        // authcrypt layer is not even looked up; a stranger's authcrypt, or the mediator's to another
        // DID of this agent, is looked up and refused by the pair check, exactly as a miss. The real
        // answer then still completes the request.
        await using var channel = Create(_w, _send, time: _time);
        var (request, spec) = answer switch
        {
            "status" => (MessagePickup40.CreateStatusRequest(L, M, idGenerator: new LetterIds("status")), PickupWire.StatusRequestSpec4),
            "delivery" => (MessagePickup40.CreateDeliveryRequest(L, M, 5, idGenerator: new LetterIds("fetch")), PickupWire.DeliveryRequestSpec4),
            _ => (MessagePickup40.CreateLiveDeliveryChange(L, M, true, new LetterIds("live")), PickupWire.LiveDeliveryChangeSpec4),
        };
        var exchange = channel.ExchangeAsync(WebSocketRoute, request, spec, M, L, default).Observed();
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);
        var thread = request.Id.ToUpperInvariant();
        var mallory = _w.Mallory.Did;
        var otherLocal = _w.OtherLocal.Did;

        var (from, to, shape) = forgery switch
        {
            "authcrypted by a stranger" => (mallory, L, AnswerEnvelope.Authcrypt),
            "authcrypted by the mediator to another DID of ours" => (M, otherLocal, AnswerEnvelope.Authcrypt),
            "anoncrypt" => (M, L, AnswerEnvelope.Anoncrypt),
            "signed only" => (M, L, AnswerEnvelope.SignedOnly),
            "anoncrypt(sign)" => (M, L, AnswerEnvelope.AnoncryptOfSigned),
            "plaintext" => (M, L, AnswerEnvelope.Plaintext),
            _ => (mallory, L, AnswerEnvelope.AuthcryptOfSigned),
        };
        var identityPasses = shape is AnswerEnvelope.Authcrypt or AnswerEnvelope.AuthcryptOfSigned;
        var lookupsBefore = channel.Lookups;

        var forged = await _w.SnapshotAsync(Answer(answer, thread, from, to), shape, from: from, to: to);
        channel.AsCorrelator().OnInbound(forged).Should().BeFalse(forgery);

        exchange.IsCompleted.Should().BeFalse("a forgery neither answers nor cancels the request ({0})", forgery);
        (channel.Lookups - lookupsBefore).Should().Be(identityPasses ? 1 : 0, "identity is checked before any lookup (D-7)");
        channel.PendingCount.Should().Be(1);

        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(Answer(answer, thread, M, L))).Should().BeTrue("the real answer still completes it");
        if (answer == "problem-report")
        {
            var thrown = await Record.ExceptionAsync(() => PickupTest.Within(exchange));
            thrown.Should().BeOfType<MediationProtocolException>().Which.ProblemCode.Should().Be(MessagePickup.LiveModeNotSupportedCode);
        }
        else
        {
            (await PickupTest.Within(exchange)).Type.Should().Be(answer == "status" ? MessagePickup40.StatusType : MessagePickup40.DeliveryType);
        }
    }

    // ---- 2. a problem report names its request in pthid, not thid -----------------------------

    [Fact]
    public async Task A_4_0_problem_report_with_both_pthid_and_thid_fails_only_the_request_its_pthid_names()
    {
        // Defeated. Two 4.0 requests wait. A problem report whose pthid names the first and whose
        // thid names the second fails the first only; one whose pthid names nothing waiting and
        // whose thid names the second completes nothing. The second is then answered normally.
        await using var channel = Create(_w, _send, time: _time);
        var live = MessagePickup40.CreateLiveDeliveryChange(L, M, true, new LetterIds("live"));
        var status = MessagePickup40.CreateStatusRequest(L, M, idGenerator: new LetterIds("status"));
        var failing = channel.ExchangeAsync(WebSocketRoute, live, PickupWire.LiveDeliveryChangeSpec4, M, L, default).Observed();
        var waiting = channel.ExchangeAsync(WebSocketRoute, status, PickupWire.StatusRequestSpec4, M, L, default).Observed();
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);

        var both = MessagePickup40.CreateLiveModeNotSupported(M, L, live.Id.ToUpperInvariant());
        both.Thid = status.Id;
        var astray = MessagePickup40.CreateLiveModeNotSupported(M, L, "not-a-waiting-request");
        astray.Thid = status.Id.ToUpperInvariant();

        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(astray)).Should().BeFalse("pthid names the request; a thid beside it is the report's own thread");
        waiting.IsCompleted.Should().BeFalse();
        failing.IsCompleted.Should().BeFalse();
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(both)).Should().BeTrue();
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(failing));
        waiting.IsCompleted.Should().BeFalse("the report's thid does not make it an answer to the request it names");

        thrown.Should().BeOfType<MediationProtocolException>().Which.RequestId.Should().Be(live.Id);
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(StatusFor(status.Id))).Should().BeTrue();
        (await PickupTest.Within(waiting)).Type.Should().Be(MessagePickup40.StatusType);
    }

    [Fact(Skip = "ADV3-1: a problem report without pthid fails the request its thid names; pthid is REQUIRED (problems.md L83)")]
    public async Task A_4_0_problem_report_without_pthid_completes_no_request_even_when_its_thid_names_one()
    {
        // FINDING (Low, pre-existing on main 3c797eb for report-problem/2.0; DidComm.Mediator.Client).
        // DIDComm v2.1 problems.md L83: "pthid - REQUIRED. The value is the thid of the thread in
        // which the problem occurred." A report without it is not a valid problem report, and
        // FR-MED-04 says the client correlates a problem report on pthid. The matcher falls back to
        // thid ("pthid, else thid"), so a pthid-less report naming the request in thid fails it
        // with a MediationProtocolException carrying the report's code, instead of being dropped.
        await using var channel = Create(_w, _send, time: _time);
        var live = MessagePickup40.CreateLiveDeliveryChange(L, M, true, new LetterIds("live"));
        var exchange = channel.ExchangeAsync(WebSocketRoute, live, PickupWire.LiveDeliveryChangeSpec4, M, L, default).Observed();
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);
        var report = MessagePickup40.CreateLiveModeNotSupported(M, L, live.Id);
        report.Pthid = null;
        report.Thid = live.Id;

        var claimed = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(report));

        claimed.Should().BeFalse("a problem report names the request it is about in pthid, which is REQUIRED");
        exchange.IsCompleted.Should().BeFalse();
    }

    [Theory(Skip = "ADV3-2: a recipient_did echo that is a DID URL of the DID asked about is accepted; 4.0 L168 asks for the matching value")]
    [InlineData("#key-1")]
    [InlineData("?versionId=1")]
    [InlineData("/path")]
    public void A_status_or_delivery_repeating_a_DID_URL_of_the_DID_asked_about_is_not_the_matching_value(string suffix)
    {
        // FINDING (Info, pre-existing on main 3c797eb for 3.0 deliveries; DidComm.Mediator.Client).
        // 4.0 L168: "If a recipient_did was specified in the status-request message, the matching
        // value MUST be specified in the recipient_did attribute of the status message." The echo
        // check compares DID subjects (DidSubject.SameDidSubject), so "did:example:a#key-1" passes
        // for a request about "did:example:a". Only the authenticated mediator can send it.
        const string Asked = "did:example:Bob-1";
        var echoed = Asked + suffix;
        var spec = PickupWire.StatusRequestSpec4;

        var status = Record.Exception(() => DeliveryReader.RequireStatusFilterEcho(echoed, Asked, "request-1", spec));
        var delivery = Record.Exception(() => DeliveryReader.RequireDeliveryFilterEcho(
            MessagePickup40.CreateDelivery(M, L, Array.Empty<DeliveredMessage>(), "request-1", echoed), "request-1", PickupWire.DeliveryRequestSpec4, Asked));

        status.Should().BeOfType<MediationProtocolException>("the echo is not the value asked about");
        delivery.Should().BeOfType<MediationProtocolException>();
    }

    // ---- 3. an answer of the other version, or of the wrong name, completes nothing ----------

    [Fact]
    public async Task An_answer_on_the_right_thread_but_of_the_other_version_or_the_wrong_name_completes_nothing()
    {
        // Defeated. Each answer is from the mediator to the requesting DID and names the request,
        // but is not a type its spec expects: a 3.0 status for a 4.0 status-request, a 4.0 status
        // for a 3.0 one, a 4.0 delivery for a 4.0 status-request, and a 4.0 status for a 4.0
        // delivery-request (4.0 answers it with a delivery only, L29, L210). None completes.
        await using var channel = Create(_w, _send, time: _time);
        var status4 = MessagePickup40.CreateStatusRequest(L, M);
        var status3 = MessagePickup.CreateStatusRequest(L, M);
        var fetch4 = MessagePickup40.CreateDeliveryRequest(L, M, 5);
        var waits = new[]
        {
            channel.ExchangeAsync(WebSocketRoute, status4, PickupWire.StatusRequestSpec4, M, L, default).Observed(),
            channel.ExchangeAsync(WebSocketRoute, status3, DeliveryReader.StatusRequestSpec, M, L, default).Observed(),
            channel.ExchangeAsync(WebSocketRoute, fetch4, PickupWire.DeliveryRequestSpec4, M, L, default).Observed(),
        };
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);

        var wrong = new (string Because, Message Answer)[]
        {
            ("a 3.0 status for a 4.0 status-request", _w.Status(status4.Id)),
            ("a 4.0 status for a 3.0 status-request", StatusFor(status3.Id)),
            ("a 4.0 delivery for a 4.0 status-request", DeliveryFor(status4.Id)),
            ("a 4.0 status for a 4.0 delivery-request", StatusFor(fetch4.Id)),
            ("a 3.0 delivery for a 4.0 delivery-request", _w.Delivery(fetch4.Id)),
        };
        foreach (var (because, message) in wrong)
            channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(message)).Should().BeFalse(because);

        waits.Should().OnlyContain(t => !t.IsCompleted);
        channel.PendingCount.Should().Be(3);
    }

    // ---- 4. a thread-less 4.0 delivery reaches only its own pair's live stream ---------------

    [Fact]
    public async Task A_thread_less_4_0_delivery_reaches_the_live_stream_of_its_mediator_and_DID_only()
    {
        // Defeated. A stream is open for (mediator, local DID). A thread-less 4.0 delivery from a
        // stranger to that DID, from the mediator to another DID of ours, or in a shape without an
        // authcrypt layer reaches no stream; a delivery that carries a thid is never taken as a
        // push. The real push then reaches it.
        await using var channel = Create(_w, _send, time: _time);
        using var stream = channel.RegisterLiveStream(M, L);
        var mallory = _w.Mallory.Did;
        var otherLocal = _w.OtherLocal.Did;
        var forged = new (string Because, InboundMessageSnapshot Snapshot)[]
        {
            ("a stranger's", await _w.SnapshotAsync(Push(mallory, L), from: mallory)),
            ("to another DID of ours", await _w.SnapshotAsync(Push(M, otherLocal), to: otherLocal)),
            ("anoncrypt", await _w.SnapshotAsync(Push(M, L), AnswerEnvelope.Anoncrypt)),
            ("signed only", await _w.SnapshotAsync(Push(M, L), AnswerEnvelope.SignedOnly)),
            ("anoncrypt(sign)", await _w.SnapshotAsync(Push(M, L), AnswerEnvelope.AnoncryptOfSigned)),
            ("with a thid", await _w.SnapshotAsync(DeliveryFor("not-a-waiting-request"))),
        };

        foreach (var (because, snapshot) in forged)
            channel.AsCorrelator().OnInbound(snapshot).Should().BeFalse(because);
        stream.Pushes.TryRead(out _).Should().BeFalse("no forgery reached the stream");

        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(Push(M, L))).Should().BeTrue();
        stream.Pushes.TryRead(out var pushed).Should().BeTrue();
        pushed!.Type.Should().Be(MessagePickup40.DeliveryType);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private Message Answer(string answer, string thread, string from, string to) => answer switch
    {
        "status" => MessagePickup40.CreateStatus(from, to, new PickupStatus40(0, LiveDelivery: false), thread),
        "delivery" => MessagePickup40.CreateDelivery(from, to, Array.Empty<DeliveredMessage>(), thread),
        _ => MessagePickup40.CreateLiveModeNotSupported(from, to, thread),
    };

    private Message StatusFor(string thid) => MessagePickup40.CreateStatus(M, L, new PickupStatus40(0, LiveDelivery: true), thid);

    private Message DeliveryFor(string thid) => MessagePickup40.CreateDelivery(M, L, Array.Empty<DeliveredMessage>(), thid);

    private static Message Push(string from, string to)
        => MessagePickup40.CreateDelivery(from, to, new[] { new DeliveredMessage("q1", Encoding.UTF8.GetBytes(FakePickupExchange.Jwe("x"))) });
}
