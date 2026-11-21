using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.TestSupport;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// #100 red team ADV-1, recipient role: cross-version confusion between Message Pickup 3.0 and 4.0
/// in the client's matcher, its negotiation and its version cache. Every <c>[Fact]</c> without a
/// skip is an attack that held; a skipped one reproduces a finding (ADV1-n).
/// </summary>
public sealed class PickupClientCrossVersionAdv1Tests : IAsyncLifetime
{
    private ClientWorld _w = null!;
    private readonly FakeSend _send = new();
    private readonly ManualTimeProvider _time = new();

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    private string M => _w.Mediator.Did;

    private string L => _w.Local.Did;

    // ---- another version's answer on our thread ---------------------------------------------

    /// <summary>(request, answer on its thread, whether it completes the request).</summary>
    public static TheoryData<string, string, bool> Answers => new()
    {
        // Controls: each request's own answers complete it.
        { "status-request 3.0", "status 3.0", true },
        { "status-request 4.0", "status 4.0", true },
        { "delivery-request 3.0", "status 3.0", true },
        { "delivery-request 3.0", "delivery 3.0", true },
        { "delivery-request 4.0", "delivery 4.0", true },
        { "live 3.0", "status 3.0", true },
        { "live 4.0", "status 4.0", true },
        // The other version's answers never do.
        { "status-request 3.0", "status 4.0", false },
        { "status-request 4.0", "status 3.0", false },
        { "delivery-request 3.0", "status 4.0", false },
        { "delivery-request 3.0", "delivery 4.0", false },
        { "delivery-request 4.0", "status 3.0", false },
        { "delivery-request 4.0", "delivery 3.0", false },
        { "delivery-request 4.0", "status 4.0", false },   // 4.0 L29: an empty delivery, never a status
        { "live 3.0", "status 4.0", false },
        { "live 4.0", "status 3.0", false },
        // Nor does any pickup answer complete the version query.
        { "query", "status 3.0", false },
        { "query", "status 4.0", false },
        { "query", "delivery 4.0", false },
    };

    [Theory]
    [MemberData(nameof(Answers))]
    public async Task An_answer_completes_only_a_request_of_its_own_version_that_expects_it(string requestKind, string answerKind, bool completes)
    {
        await using var channel = Create(_w, _send, time: _time);
        var (request, spec) = Request(requestKind);
        var exchange = channel.ExchangeAsync(WebSocketRoute, request, spec, M, L, default).Observed();
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);

        var answer = Answer(answerKind, request.Id);
        var claimed = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(answer));

        claimed.Should().Be(completes);
        if (completes)
            (await PickupTest.Within(exchange)).Type.Should().Be(answer.Type);
        else
            exchange.IsCompleted.Should().BeFalse("an answer of the other version or of the wrong kind keeps the request waiting until its timeout");
    }

    [Theory]
    [InlineData("status-request 3.0", "4.0")]
    [InlineData("delivery-request 3.0", "4.0")]
    [InlineData("status-request 4.0", "2.0")]
    [InlineData("delivery-request 4.0", "2.0")]
    public async Task A_problem_report_of_either_kind_on_our_thread_fails_the_request_and_never_completes_it_as_an_answer(string requestKind, string reportKind)
    {
        await using var channel = Create(_w, _send, time: _time);
        var (request, spec) = Request(requestKind);
        var exchange = channel.ExchangeAsync(WebSocketRoute, request, spec, M, L, default).Observed();
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);
        var report = reportKind == "4.0"
            ? MessagePickup40.CreateLiveModeNotSupported(M, L, request.Id)
            : _w.Problem(request.Id, "e.p.xfer.cant-use-endpoint");

        var claimed = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(report));
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(exchange));

        claimed.Should().BeTrue();
        thrown.Should().BeOfType<MediationProtocolException>().Which.RequestId.Should().Be(request.Id);
    }

    // ---- a forged disclose ---------------------------------------------------------------------

    public static TheoryData<string> ForgedDiscloses => new()
    {
        "authcrypted by a third party that writes its own from",
        "authcrypted by a third party that writes the mediator as from",
        "from the mediator but anoncrypted",
        "from the mediator, authcrypted to another of our DIDs",
        "from the mediator, authcrypted, on another thread",
    };

    [Theory]
    [MemberData(nameof(ForgedDiscloses))]
    public async Task A_disclose_that_downgrades_to_3_0_completes_the_version_query_only_from_the_mediator_authcrypted_to_us_on_its_thread(string forgery)
    {
        await using var channel = Create(_w, _send, time: _time);
        var query = FeatureNegotiation.CreatePickupQuery(L, M);
        var exchange = channel.ExchangeAsync(WebSocketRoute, query, FeatureNegotiation.QuerySpec, M, L, default).Observed();
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);
        var only3 = new JsonObject { ["disclosures"] = new JsonArray(new JsonObject { ["feature-type"] = "protocol", ["id"] = MessagePickup.ProtocolUri, ["roles"] = new JsonArray("mediator") }) };

        if (forgery == "authcrypted by a third party that writes the mediator as from")
        {
            // Refused by unpack itself (FR-CONSIST-01: the authcrypt sender must be the plaintext
            // from), so it never reaches the matcher on the receive path.
            var refused = await Record.ExceptionAsync(() => _w.SnapshotAsync(_w.Answer(DiscoverFeatures.DiscloseType, query.Id, body: only3), from: _w.Mallory.Did));
            refused.Should().BeOfType<DidComm.Exceptions.ConsistencyException>();
            exchange.IsCompleted.Should().BeFalse();
            return;
        }

        var snapshot = forgery switch
        {
            "authcrypted by a third party that writes its own from" => await _w.SnapshotAsync(_w.Answer(DiscoverFeatures.DiscloseType, query.Id, from: _w.Mallory.Did, body: only3)),
            "from the mediator but anoncrypted" => await _w.SnapshotAsync(_w.Answer(DiscoverFeatures.DiscloseType, query.Id, body: only3), AnswerEnvelope.Anoncrypt),
            "from the mediator, authcrypted to another of our DIDs" => await _w.SnapshotAsync(_w.Answer(DiscoverFeatures.DiscloseType, query.Id, to: _w.OtherLocal.Did, body: only3), to: _w.OtherLocal.Did),
            _ => await _w.SnapshotAsync(_w.Answer(DiscoverFeatures.DiscloseType, "another-thread", body: only3)),
        };

        channel.AsCorrelator().OnInbound(snapshot).Should().BeFalse(forgery);
        exchange.IsCompleted.Should().BeFalse();

        // Control: the mediator's own answer on the thread does complete it.
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Answer(DiscoverFeatures.DiscloseType, query.Id, body: only3))).Should().BeTrue();
        FeatureNegotiation.ChoosePickup((await PickupTest.Within(exchange)).DeserializeMessage(), query.Id).Should().Be(MessagePickupVersion.V3);
    }

    // ---- the reading of the disclose ---------------------------------------------------------

    [Fact]
    public void A_4_0_disclosure_past_the_inspected_bound_is_never_read_so_burying_it_only_downgrades_the_mediator_itself()
    {
        var disclosures = new JsonArray(Disclosure(MessagePickup.ProtocolUri, "mediator"));
        for (var i = 1; i < FeatureNegotiation.MaxInspected; i++)
            disclosures.Add(Disclosure($"https://didcomm.org/noise{i}/1.0", "mediator"));
        disclosures.Add(Disclosure(MessagePickup40.ProtocolUri, "mediator"));   // entry MaxInspected + 1

        FeatureNegotiation.ChoosePickup(Disclose(disclosures), "q").Should().Be(MessagePickupVersion.V3);
    }

    [Fact]
    public void A_4_0_disclosure_in_the_recipient_role_only_is_not_a_4_0_mediator_even_with_no_3_0_entry()
    {
        var act = () => FeatureNegotiation.ChoosePickup(Disclose(new JsonArray(Disclosure(MessagePickup40.ProtocolUri, "recipient"))), "q");

        act.Should().Throw<MediationNotSupportedException>();
    }

    public static TheoryData<string, JsonObject> NotA40Disclosure => new()
    {
        { "feature-type in another case", new JsonObject { ["feature-type"] = "Protocol", ["id"] = MessagePickup40.ProtocolUri } },
        { "a goal-code, not a protocol", new JsonObject { ["feature-type"] = "goal-code", ["id"] = MessagePickup40.ProtocolUri } },
        { "roles not an array", new JsonObject { ["feature-type"] = "protocol", ["id"] = MessagePickup40.ProtocolUri, ["roles"] = "mediator" } },
        { "a message type, not a protocol id", new JsonObject { ["feature-type"] = "protocol", ["id"] = MessagePickup40.DeliveryRequestType } },
        { "a look-alike letter", new JsonObject { ["feature-type"] = "protocol", ["id"] = "https://didcomm.org/messаge-pickup/4.0" } },
        { "another host", new JsonObject { ["feature-type"] = "protocol", ["id"] = "https://didcomm.org.evil.example/message-pickup/4.0" } },
    };

    [Theory]
    [MemberData(nameof(NotA40Disclosure))]
    public void An_entry_that_is_not_a_clean_4_0_protocol_disclosure_does_not_upgrade_to_4_0(string because, JsonObject entry)
    {
        var disclosures = new JsonArray(entry, Disclosure(MessagePickup.ProtocolUri, "mediator"));

        FeatureNegotiation.ChoosePickup(Disclose(disclosures), "q").Should().Be(MessagePickupVersion.V3, because);
    }

    // ---- the version cache -------------------------------------------------------------------

    [Fact]
    public async Task A_version_learnt_from_one_mediator_is_never_used_for_another()
    {
        var exchange = PickupClientNegotiationTests.Negotiating(PickupClientNegotiationTests.Protocol(MessagePickup40.ProtocolUri, "mediator"));
        await PickupTest.Client(exchange).GetStatusAsync(FakePickupExchange.M, FakePickupExchange.L);

        exchange.PickupVersions.TryGet(FakePickupExchange.M, out var remembered).Should().BeTrue();
        remembered.Should().Be(MessagePickupVersion.V4);
        exchange.PickupVersions.TryGet("did:example:other-mediator", out _).Should().BeFalse();
    }

    /// <summary>
    /// Info (ours): the cache is keyed by the mediator alone, so what one of our DIDs negotiated is
    /// spoken by our other DIDs at that mediator for the hour. Only the mediator can author the
    /// disclose (the matcher's pair check), so no third party can choose it; and Coordinate
    /// Mediation's cache on main is keyed the same way.
    /// </summary>
    [Fact]
    public async Task A_version_negotiated_by_one_of_our_DIDs_is_spoken_by_our_others_at_that_mediator()
    {
        var exchange = PickupClientNegotiationTests.Negotiating(PickupClientNegotiationTests.Protocol(MessagePickup40.ProtocolUri, "mediator"));
        var client = PickupTest.Client(exchange);

        await client.GetStatusAsync(FakePickupExchange.M, FakePickupExchange.L);
        await client.GetStatusAsync(FakePickupExchange.M, FakePickupExchange.A);

        exchange.Sent.Select(s => s.Type).Should().Equal(DiscoverFeatures.QueriesType, MessagePickup40.StatusRequestType, MessagePickup40.StatusRequestType);
    }

    /// <summary>
    /// ADV1-1 (Low): a remembered version that the mediator stopped speaking (a restart with
    /// <c>EnableV4 = false</c>, or a mediator rolled back to a 3.0-only release) is not forgotten when
    /// a request under it goes unanswered. Every pickup call to that mediator then times out, and a
    /// live stream ends at once, until the entry ages out after an hour; nothing re-negotiates.
    /// </summary>
    [Fact(Skip = "ADV1-1, declined in #100 (a documented limit, the same as Coordinate Mediation's cache on main): a remembered pickup version the mediator no longer answers is kept for the hour; every call times out until it expires or the process restarts")]
    public async Task A_remembered_version_whose_requests_go_unanswered_is_forgotten_so_the_next_call_negotiates_again()
    {
        var exchange = PickupClientNegotiationTests.Negotiating(PickupClientNegotiationTests.Protocol(MessagePickup40.ProtocolUri, "mediator"));
        var client = PickupTest.Client(exchange);
        await client.GetStatusAsync(FakePickupExchange.M, FakePickupExchange.L);

        // The mediator now serves 3.0 only: a 4.0 request gets no answer.
        exchange.On(MessagePickup40.StatusRequestType, r => Task.FromException<InboundMessageSnapshot>(new TimeoutException($"No answer to request '{r.Request.Id}'.")));
        var thrown = await Record.ExceptionAsync(() => client.GetStatusAsync(FakePickupExchange.M, FakePickupExchange.L));

        thrown.Should().BeOfType<TimeoutException>();
        exchange.PickupVersions.TryGet(FakePickupExchange.M, out _).Should().BeFalse("a version the mediator no longer answers should not be spoken for the rest of the hour");
    }

    // ---- helpers -----------------------------------------------------------------------------

    private (Message Request, MediatorExchangeSpec Spec) Request(string kind) => kind switch
    {
        "status-request 3.0" => PickupWire.StatusRequest(MessagePickupVersion.V3, L, M, null),
        "status-request 4.0" => PickupWire.StatusRequest(MessagePickupVersion.V4, L, M, null),
        "delivery-request 3.0" => PickupWire.DeliveryRequest(MessagePickupVersion.V3, L, M, 10, null),
        "delivery-request 4.0" => PickupWire.DeliveryRequest(MessagePickupVersion.V4, L, M, 10, null),
        "live 3.0" => PickupWire.LiveDeliveryChange(MessagePickupVersion.V3, L, M, true),
        "live 4.0" => PickupWire.LiveDeliveryChange(MessagePickupVersion.V4, L, M, true),
        "query" => (FeatureNegotiation.CreatePickupQuery(L, M), FeatureNegotiation.QuerySpec),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private Message Answer(string kind, string thid)
    {
        var one = new[] { new DeliveredMessage("q1", Encoding.UTF8.GetBytes(FakePickupExchange.Jwe("x"))) };
        return kind switch
        {
            "status 3.0" => MessagePickup.CreateStatus(M, L, new PickupStatus(0, LiveDelivery: false), thid),
            "status 4.0" => MessagePickup40.CreateStatus(M, L, new PickupStatus40(0, LiveDelivery: false), thid),
            "delivery 3.0" => MessagePickup.CreateDelivery(M, L, one, thid),
            "delivery 4.0" => MessagePickup40.CreateDelivery(M, L, one, thid),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static JsonObject Disclosure(string id, string role)
        => new() { ["feature-type"] = "protocol", ["id"] = id, ["roles"] = new JsonArray(role) };

    private static Message Disclose(JsonArray disclosures)
        => new MessageBuilder()
            .WithType(DiscoverFeatures.DiscloseType)
            .WithFrom("did:example:mediator")
            .WithTo("did:example:bob")
            .WithThid("q")
            .WithBody(new JsonObject { ["disclosures"] = disclosures })
            .Build();
}
