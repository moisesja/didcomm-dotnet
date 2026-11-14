using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="MediationClient"/> over a fake exchange (#96 design §2, §4.5; FR-MED-09): the
/// arguments are checked before anything is resolved or sent and never quoted; every request is a
/// fresh UUID v4 sent through the exchange with the operation's one route; a grant comes back as
/// <see cref="MediationGrant"/> and a deny as <c>null</c>; a grant's <c>routing_did</c> is bounded
/// from its count before it is read (L-055) and must be bare DIDs; an update's answer must answer
/// every update in order; the exchange's exceptions pass through unchanged; no log line carries a
/// DID.
/// </summary>
public sealed class MediationClientTests
{
    private const string Mediator = FakeMediatorExchange.MediatorDid;
    private const string Me = FakeMediatorExchange.LocalDid;
    private const string Bob = "did:example:bob";
    private const string Carol = "did:example:carol";

    // ---- argument checks: before any I/O, never quoted ------------------------------------

    public static TheoryData<string> NotBareDids => new()
    {
        "",
        " ",
        "not-a-did",
        "did:example:abc#key-1",
        "did:example:abc?service=x",
        "did:example:abc/path",
        "did:example:" + new string('q', 2100),
    };

    [Theory]
    [MemberData(nameof(NotBareDids))]
    public async Task Every_operation_refuses_a_mediator_did_that_is_not_bare_before_anything_is_resolved_or_sent(string bad)
    {
        var exchange = new FakeMediatorExchange();
        var client = exchange.Client();

        foreach (var act in Operations(client, mediatorDid: bad, from: Me))
        {
            var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;
            thrown.ParamName.Should().Be("mediatorDid");
            if (bad.Length > 4)
                thrown.Message.Should().NotContain(bad.Length > 40 ? bad[..40] : bad);
        }
        exchange.SendCount.Should().Be(0);
        exchange.RouteCalls.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(NotBareDids))]
    public async Task Every_operation_refuses_a_from_did_that_is_not_bare_before_anything_is_resolved_or_sent(string bad)
    {
        var exchange = new FakeMediatorExchange();
        var client = exchange.Client();

        foreach (var act in Operations(client, mediatorDid: Mediator, from: bad))
            (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("from");
        exchange.SendCount.Should().Be(0);
        exchange.RouteCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_null_did_is_an_ArgumentNullException()
    {
        var exchange = new FakeMediatorExchange();
        var client = exchange.Client();

        foreach (var act in Operations(client, mediatorDid: null!, from: Me))
            (await act.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("mediatorDid");
        foreach (var act in Operations(client, mediatorDid: Mediator, from: null!))
            (await act.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("from");
        exchange.SendCount.Should().Be(0);
    }

    [Theory]
    [InlineData("/relative")]
    [InlineData("https://user:secret@mediator.test/")]
    [InlineData("wss://user@mediator.test/ws")]
    public async Task Every_operation_refuses_a_relative_or_credentialed_endpoint_override_before_anything_is_sent(string uri)
    {
        var exchange = new FakeMediatorExchange();
        var client = exchange.Client();
        var endpoint = new Uri(uri, UriKind.RelativeOrAbsolute);

        foreach (var act in Operations(client, Mediator, Me, endpoint))
        {
            var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;
            thrown.ParamName.Should().Be("serviceEndpointOverride");
            thrown.Message.Should().NotContain("secret").And.NotContain("user@").And.NotContain("mediator.test");
        }
        exchange.SendCount.Should().Be(0);
        exchange.RouteCalls.Should().BeEmpty();
    }

    [Fact]
    public void Arguments_are_checked_when_the_method_is_called_not_when_its_task_is_awaited()
    {
        var client = new FakeMediatorExchange().Client();

        FluentActions.Invoking(() => { _ = client.RequestMediationAsync("not-a-did", Me); }).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => { _ = client.UpdateRecipientsAsync(Mediator, Me, Array.Empty<RecipientUpdate>()); }).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => { _ = client.QueryRecipientsAsync(Mediator, Me, new RecipientQueryPaging(1001, 0)); }).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Updates_are_refused_when_null_empty_or_over_1024_before_anything_is_sent()
    {
        var exchange = new FakeMediatorExchange();
        var client = exchange.Client();

        (await FluentActions.Awaiting(() => client.UpdateRecipientsAsync(Mediator, Me, null!)).Should().ThrowAsync<ArgumentNullException>())
            .Which.ParamName.Should().Be("updates");
        (await FluentActions.Awaiting(() => client.UpdateRecipientsAsync(Mediator, Me, Array.Empty<RecipientUpdate>())).Should().ThrowAsync<ArgumentException>())
            .Which.ParamName.Should().Be("updates");

        var exactly1024 = Enumerable.Range(0, 1024).Select(i => Add($"did:example:r{i}")).ToArray();
        // 1025 entries whose 1025th is null: the refusal is the count, not that entry (L-055).
        var over = exactly1024.Append(null!).ToArray();
        (await FluentActions.Awaiting(() => client.UpdateRecipientsAsync(Mediator, Me, over)).Should().ThrowAsync<ArgumentException>())
            .Which.Message.Should().Contain("Between 1 and 1024").And.NotContain("Update 1024");
        exchange.SendCount.Should().Be(0);

        var results = await client.UpdateRecipientsAsync(Mediator, Me, exactly1024);
        results.Should().HaveCount(1024, "1024 is within the bound");
    }

    [Fact]
    public async Task An_invalid_update_entry_is_refused_by_index_without_quoting_it()
    {
        var exchange = new FakeMediatorExchange();
        var client = exchange.Client();

        async Task<ArgumentException> Refused(params RecipientUpdate[] updates)
            => (await FluentActions.Awaiting(() => client.UpdateRecipientsAsync(Mediator, Me, updates)).Should().ThrowAsync<ArgumentException>()).Which;

        (await Refused(Add(Bob), null!)).Message.Should().Contain("Update 1 is null");
        (await Refused(Add(Bob), Add(Carol), Add("did:example:carol#key-1"))).Message.Should().Contain("Update 2").And.NotContain("carol#key-1");
        (await Refused(Add("did:example:" + new string('w', 2100)))).Message.Should().Contain("Update 0").And.NotContain("wwww");
        (await Refused(new RecipientUpdate(Bob, (RecipientUpdateAction)7))).Message.Should().Contain("Update 0");
        exchange.SendCount.Should().Be(0);
        exchange.RouteCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_updates_are_read_once_so_what_is_checked_is_what_is_sent_and_compared()
    {
        var exchange = new FakeMediatorExchange();
        var client = exchange.Client();
        // A list that turns hostile after the first read of each index (a caller mutating it while
        // the call runs).
        var shifting = new ShiftingList(Add(Bob), Add("did:example:bob#not-bare"));

        var results = await client.UpdateRecipientsAsync(Mediator, Me, shifting);

        shifting.Reads.Should().Be(1, "each entry is read once, into a private copy");
        results.Should().ContainSingle().Which.RecipientDid.Should().Be(Bob);
        exchange.Operations.Single().Message.Body!.ToJsonString().Should().Contain(Bob).And.NotContain("not-bare");
    }

    // ---- requests -------------------------------------------------------------------------

    [Fact]
    public async Task Requests_are_fresh_uuid_v4_messages_from_us_to_the_mediator_with_no_timestamp_sent_on_one_route()
    {
        var exchange = new FakeMediatorExchange();
        var client = exchange.Client();
        var endpoint = new Uri("wss://mediator.test/ws");

        await client.RequestMediationAsync(Mediator, Me, endpoint);
        await client.UpdateRecipientsAsync(Mediator, Me, new[] { Add(Bob) }, endpoint);
        await client.QueryRecipientsAsync(Mediator, Me, null, endpoint);

        exchange.RouteCalls.Should().HaveCount(3, "one resolution per operation");
        exchange.RouteCalls.Should().AllSatisfy(r =>
        {
            r.MediatorDid.Should().Be(Mediator);
            r.Override.Should().Be(endpoint, "the override is handed to the route resolution as given");
            r.RequireWebSocket.Should().BeFalse();
        });
        exchange.Sent.Should().HaveCount(4, "one Discover Features query (then cached) and three operations");
        exchange.Sent.Select(s => s.Message.Id).Distinct().Should().HaveCount(4);
        exchange.Sent.Should().AllSatisfy(s =>
        {
            IsUuidV4(s.Message.Id).Should().BeTrue("request ids never come from a host's generator");
            s.Message.From.Should().Be(Me);
            s.Message.To.Should().Equal(Mediator);
            s.Message.CreatedTime.Should().BeNull("the send stamps it from the clock (FR-RR-09)");
            s.Message.Thid.Should().BeNull();
            s.MediatorDid.Should().Be(Mediator);
            s.LocalDid.Should().Be(Me);
            s.Route.Should().BeSameAs(exchange.Route);
        });
        exchange.Operations.Select(s => s.Message.Type).Should().Equal(
            CoordinateMediation.MediateRequestType, CoordinateMediation.RecipientUpdateType, CoordinateMediation.RecipientQueryType);
        exchange.Operations.Select(s => s.Spec).Should().Equal(
            CoordinateMediationWire.MediateSpec3, CoordinateMediationWire.UpdateSpec3, CoordinateMediationWire.QuerySpec3);
        exchange.Operations[0].Message.Body!.ToJsonString().Should().Be("{}");
    }

    [Fact]
    public void The_exchange_specs_expect_exactly_the_answers_of_their_request()
    {
        CoordinateMediationWire.MediateSpec3.Expected.Select(t => t.Value).Should().Equal(CoordinateMediation.MediateGrantType, CoordinateMediation.MediateDenyType);
        CoordinateMediationWire.UpdateSpec3.Expected.Select(t => t.Value).Should().Equal(CoordinateMediation.RecipientUpdateResponseType);
        CoordinateMediationWire.QuerySpec3.Expected.Select(t => t.Value).Should().Equal(CoordinateMediation.RecipientType);
        CoordinateMediationWire.MediateSpec2.Expected.Select(t => t.Value).Should().Equal(CoordinateMediation20.MediateGrantType, CoordinateMediation20.MediateDenyType);
        CoordinateMediationWire.UpdateSpec2.Expected.Select(t => t.Value).Should().Equal(CoordinateMediation20.KeylistUpdateResponseType);
        CoordinateMediationWire.QuerySpec2.Expected.Select(t => t.Value).Should().Equal(CoordinateMediation20.KeylistType);
        new[] { CoordinateMediationWire.MediateSpec3, CoordinateMediationWire.UpdateSpec3, CoordinateMediationWire.QuerySpec3,
                CoordinateMediationWire.MediateSpec2, CoordinateMediationWire.UpdateSpec2, CoordinateMediationWire.QuerySpec2,
                FeatureNegotiation.QuerySpec }
            .Should().AllSatisfy(s => s.ExpectsDelivery.Should().BeFalse());
    }

    // ---- mediate-request ------------------------------------------------------------------

    [Fact]
    public async Task A_grant_comes_back_as_MediationGrant_for_the_enrolled_did()
    {
        var exchange = new FakeMediatorExchange();
        exchange.Mediator.RoutingDids = new[] { "did:example:routing", "did:example:routing-2" };

        var grant = await exchange.Client().RequestMediationAsync(Mediator, Me);

        grant.Should().NotBeNull();
        grant!.PeerDid.Should().Be(Me);
        grant.RoutingDids.Should().Equal("did:example:routing", "did:example:routing-2");
    }

    [Fact]
    public async Task A_deny_comes_back_as_null()
    {
        var exchange = new FakeMediatorExchange();
        exchange.Mediator.Deny = true;

        (await exchange.Client().RequestMediationAsync(Mediator, Me)).Should().BeNull();
    }

    [Fact]
    public async Task A_deny_without_a_body_is_a_deny()
    {
        var exchange = new FakeMediatorExchange(Pinned(CoordinateMediationVersion.V3));
        exchange.Respond = r => ScriptedMediator.Raw(CoordinateMediation.MediateDenyType, Mediator, Me, r.Message.Id, body: null);

        (await exchange.Client().RequestMediationAsync(Mediator, Me)).Should().BeNull();
    }

    [Fact]
    public async Task A_3_0_grant_with_a_single_string_routing_did_is_read_as_one()
    {
        var exchange = new FakeMediatorExchange(Pinned(CoordinateMediationVersion.V3));
        exchange.Respond = r => Grant3(r, "did:example:routing");

        (await exchange.Client().RequestMediationAsync(Mediator, Me))!.RoutingDids.Should().Equal("did:example:routing");
    }

    [Fact]
    public async Task A_grant_naming_16_routing_dids_is_accepted()
    {
        var exchange = new FakeMediatorExchange(Pinned(CoordinateMediationVersion.V3));
        var sixteen = Enumerable.Range(0, 16).Select(i => (JsonNode)$"did:example:routing{i}").ToArray();
        exchange.Respond = r => Grant3(r, new JsonArray(sixteen));

        (await exchange.Client().RequestMediationAsync(Mediator, Me))!.RoutingDids.Should().HaveCount(16);
    }

    [Theory]
    [InlineData(CoordinateMediationVersion.V3)]
    [InlineData(CoordinateMediationVersion.V2)]
    public async Task A_grant_naming_17_routing_dids_is_refused_from_the_count_before_the_poisoned_17th_is_read(CoordinateMediationVersion version)
    {
        var exchange = new FakeMediatorExchange(Pinned(version));
        var entries = Enumerable.Range(0, 16).Select(i => (JsonNode)$"did:example:routing{i}").Append(JsonValue.Create(17)).ToArray();
        var type = version == CoordinateMediationVersion.V3 ? CoordinateMediation.MediateGrantType : CoordinateMediation20.MediateGrantType;
        exchange.Respond = r => ScriptedMediator.Raw(type, Mediator, Me, r.Message.Id, new JsonObject { ["routing_did"] = new JsonArray(entries) });

        var thrown = (await FluentActions.Awaiting(() => exchange.Client().RequestMediationAsync(Mediator, Me)).Should().ThrowAsync<MediationProtocolException>()).Which;

        thrown.GetType().Should().Be(typeof(MediationProtocolException));
        thrown.RequestId.Should().Be(exchange.Operations.Single().Message.Id);
        thrown.ProblemCode.Should().BeNull();
        thrown.InnerException.Should().BeOfType<MalformedMessageException>()
            .Which.Message.Should().Contain("more than 16 entries", "the count is refused, not the poisoned 17th entry");
    }

    [Theory]
    [InlineData("did:example:routing#key-1")]
    [InlineData("did:example:routing?service=a")]
    [InlineData("did:example:routing/path")]
    [InlineData("not-a-did")]
    public async Task A_grant_whose_routing_did_is_not_a_bare_did_is_refused_without_quoting_it(string routingDid)
    {
        var exchange = new FakeMediatorExchange(Pinned(CoordinateMediationVersion.V3));
        exchange.Respond = r => Grant3(r, new JsonArray("did:example:good", routingDid));

        var thrown = (await FluentActions.Awaiting(() => exchange.Client().RequestMediationAsync(Mediator, Me)).Should().ThrowAsync<MediationProtocolException>()).Which;

        thrown.Message.Should().Contain("not a bare DID").And.NotContain(routingDid).And.NotContain("did:example:good");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"routing_did":[]}""")]
    [InlineData("""{"routing_did":5}""")]
    [InlineData("""{"routing_did":[null]}""")]
    public async Task A_grant_with_a_missing_empty_or_mistyped_routing_did_is_malformed(string body)
    {
        var exchange = new FakeMediatorExchange(Pinned(CoordinateMediationVersion.V3));
        exchange.Respond = r => ScriptedMediator.Raw(CoordinateMediation.MediateGrantType, Mediator, Me, r.Message.Id, JsonNode.Parse(body)!.AsObject());

        var thrown = (await FluentActions.Awaiting(() => exchange.Client().RequestMediationAsync(Mediator, Me)).Should().ThrowAsync<MediationProtocolException>()).Which;

        thrown.InnerException.Should().BeOfType<MalformedMessageException>();
    }

    [Fact]
    public async Task A_grant_with_an_over_long_routing_did_is_refused_without_quoting_it()
    {
        var exchange = new FakeMediatorExchange(Pinned(CoordinateMediationVersion.V3));
        var longDid = "did:example:" + new string('v', 3000);
        exchange.Respond = r => Grant3(r, new JsonArray(longDid));

        var thrown = (await FluentActions.Awaiting(() => exchange.Client().RequestMediationAsync(Mediator, Me)).Should().ThrowAsync<MediationProtocolException>()).Which;

        thrown.ToString().Should().NotContain("vvvv");
    }

    [Fact]
    public async Task An_answer_of_another_type_is_refused()
    {
        var exchange = new FakeMediatorExchange(Pinned(CoordinateMediationVersion.V3));
        exchange.Respond = r => ScriptedMediator.Raw(CoordinateMediation.RecipientType, Mediator, Me, r.Message.Id, new JsonObject { ["dids"] = new JsonArray() });

        var thrown = (await FluentActions.Awaiting(() => exchange.Client().RequestMediationAsync(Mediator, Me)).Should().ThrowAsync<MediationProtocolException>()).Which;

        thrown.Message.Should().Contain("another type");
    }

    [Fact]
    public async Task A_2_0_answer_to_a_3_0_request_is_refused()
    {
        var exchange = new FakeMediatorExchange(Pinned(CoordinateMediationVersion.V3));
        exchange.Respond = r => ScriptedMediator.Raw(CoordinateMediation20.MediateGrantType, Mediator, Me, r.Message.Id, new JsonObject { ["routing_did"] = "did:example:routing" });

        await FluentActions.Awaiting(() => exchange.Client().RequestMediationAsync(Mediator, Me)).Should().ThrowAsync<MediationProtocolException>();
    }

    // ---- recipient-update -----------------------------------------------------------------

    [Fact]
    public async Task Update_results_come_back_in_order_with_their_outcomes_client_errors_included()
    {
        var exchange = new FakeMediatorExchange();
        exchange.Mediator.Registered.Add(Carol);

        var results = await exchange.Client().UpdateRecipientsAsync(Mediator, Me, new[] { Add(Bob), Add(Carol), Remove("did:example:nobody") });

        results.Should().Equal(
            new RecipientUpdateResult(Bob, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success),
            new RecipientUpdateResult(Carol, RecipientUpdateAction.Add, RecipientUpdateOutcome.NoChange),
            new RecipientUpdateResult("did:example:nobody", RecipientUpdateAction.Remove, RecipientUpdateOutcome.ClientError));
        exchange.Operations.Single().Message.Body!.ToJsonString().Should().Be(
            $$"""{"updates":[{"recipient_did":"{{Bob}}","action":"add"},{"recipient_did":"{{Carol}}","action":"add"},{"recipient_did":"did:example:nobody","action":"remove"}]}""");
    }

    public static TheoryData<string, string> MismatchedAnswers => new()
    {
        { "fewer", $$"""[{"recipient_did":"{{Bob}}","action":"add","result":"success"}]""" },
        { "reordered", $$"""[{"recipient_did":"{{Carol}}","action":"add","result":"success"},{"recipient_did":"{{Bob}}","action":"add","result":"success"}]""" },
        { "other DID", $$"""[{"recipient_did":"{{Bob}}","action":"add","result":"success"},{"recipient_did":"did:example:mallory","action":"add","result":"success"}]""" },
        { "case-changed DID", $$"""[{"recipient_did":"{{Bob}}","action":"add","result":"success"},{"recipient_did":"did:example:CAROL","action":"add","result":"success"}]""" },
        { "other action", $$"""[{"recipient_did":"{{Bob}}","action":"add","result":"success"},{"recipient_did":"{{Carol}}","action":"remove","result":"success"}]""" },
        { "empty", "[]" },
    };

    [Theory]
    [MemberData(nameof(MismatchedAnswers))]
    public async Task An_update_answer_that_does_not_answer_each_update_at_its_position_is_refused(string because, string updated)
    {
        var exchange = new FakeMediatorExchange(Pinned(CoordinateMediationVersion.V3));
        exchange.Respond = r => ScriptedMediator.Raw(CoordinateMediation.RecipientUpdateResponseType, Mediator, Me, r.Message.Id,
            new JsonObject { ["updated"] = JsonNode.Parse(updated) });

        var thrown = (await FluentActions.Awaiting(() => exchange.Client().UpdateRecipientsAsync(Mediator, Me, new[] { Add(Bob), Add(Carol) }))
            .Should().ThrowAsync<MediationProtocolException>(because)).Which;

        thrown.Message.Should().Contain("does not match the request").And.NotContain("mallory").And.NotContain("CAROL");
        thrown.RequestId.Should().Be(exchange.Operations.Single().Message.Id);
    }

    [Theory]
    [InlineData(CoordinateMediationVersion.V3)]
    [InlineData(CoordinateMediationVersion.V2)]
    public async Task An_update_answer_longer_than_the_request_is_refused_from_the_count_before_the_poisoned_extra_entry_is_read(CoordinateMediationVersion version)
    {
        var exchange = new FakeMediatorExchange(Pinned(version));
        var type = version == CoordinateMediationVersion.V3 ? CoordinateMediation.RecipientUpdateResponseType : CoordinateMediation20.KeylistUpdateResponseType;
        exchange.Respond = r => ScriptedMediator.Raw(type, Mediator, Me, r.Message.Id, new JsonObject
        {
            ["updated"] = JsonNode.Parse($$"""[{"recipient_did":"{{Bob}}","action":"add","result":"success"},{"recipient_did":42,"action":"explode"}]"""),
        });

        var thrown = (await FluentActions.Awaiting(() => exchange.Client().UpdateRecipientsAsync(Mediator, Me, new[] { Add(Bob) }))
            .Should().ThrowAsync<MediationProtocolException>()).Which;

        thrown.InnerException.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Contain("more than 1 entries");
    }

    [Fact]
    public async Task A_malformed_update_answer_carries_the_parse_failure_inside()
    {
        var exchange = new FakeMediatorExchange(Pinned(CoordinateMediationVersion.V3));
        exchange.Respond = r => ScriptedMediator.Raw(CoordinateMediation.RecipientUpdateResponseType, Mediator, Me, r.Message.Id,
            new JsonObject { ["updated"] = JsonNode.Parse($$"""[{"recipient_did":"{{Bob}}","action":"add","result":"perhaps"}]""") });

        var thrown = (await FluentActions.Awaiting(() => exchange.Client().UpdateRecipientsAsync(Mediator, Me, new[] { Add(Bob) }))
            .Should().ThrowAsync<MediationProtocolException>()).Which;

        thrown.InnerException.Should().BeOfType<MalformedMessageException>();
        thrown.ToString().Should().NotContain("perhaps");
    }

    // ---- what the exchange throws passes through -------------------------------------------

    public static TheoryData<Exception> ExchangeFailures => new()
    {
        new TimeoutException("Mediator request 'x' (mediate-request) was not answered."),
        new TransportException("down"),
        new InvalidOperationException("too many pending"),
        new ObjectDisposedException("channel"),
    };

    [Theory]
    [MemberData(nameof(ExchangeFailures))]
    public async Task What_the_exchange_throws_comes_out_unchanged(Exception failure)
    {
        var exchange = new FakeMediatorExchange(Pinned(CoordinateMediationVersion.V3));
        exchange.Respond = _ => throw failure;

        var thrown = await FluentActions.Awaiting(() => exchange.Client().RequestMediationAsync(Mediator, Me)).Should().ThrowAsync<Exception>();

        thrown.Which.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task A_problem_report_answer_is_the_exchanges_MediationProtocolException()
    {
        var exchange = new FakeMediatorExchange(Pinned(CoordinateMediationVersion.V3));
        exchange.Respond = r => ScriptedMediator.Raw("https://didcomm.org/report-problem/2.0/problem-report", Mediator, Me, null, new JsonObject { ["code"] = "e.p.req.no-grant" });

        await FluentActions.Awaiting(() => exchange.Client().UpdateRecipientsAsync(Mediator, Me, new[] { Add(Bob) }))
            .Should().ThrowAsync<MediationProtocolException>();
    }

    [Fact]
    public async Task A_route_refusal_comes_out_before_anything_is_sent()
    {
        var exchange = new FakeMediatorExchange();
        var refusal = new InvalidOperationException("A WebSocket route needs UseInboundDispatch().");
        exchange.RouteFailure = refusal;

        (await FluentActions.Awaiting(() => exchange.Client().RequestMediationAsync(Mediator, Me)).Should().ThrowAsync<InvalidOperationException>())
            .Which.Should().BeSameAs(refusal);
        exchange.SendCount.Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_before_the_call_sends_nothing()
    {
        var exchange = new FakeMediatorExchange();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await FluentActions.Awaiting(() => exchange.Client().RequestMediationAsync(Mediator, Me, ct: cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        exchange.SendCount.Should().Be(0);
    }

    // ---- logs -----------------------------------------------------------------------------

    [Fact]
    public async Task No_log_line_carries_a_did_and_a_throwing_logger_changes_nothing()
    {
        var sink = new HandlerLogSink();
        var exchange = new FakeMediatorExchange();
        exchange.Mediator.Registered.AddRange(Enumerable.Range(0, 3).Select(i => $"did:example:listed{i}"));
        var client = exchange.Client(new HandlerCapturingLogger<MediationClient>(sink));

        await client.RequestMediationAsync(Mediator, Me);
        await client.UpdateRecipientsAsync(Mediator, Me, new[] { Add(Bob) });
        await foreach (var _ in client.QueryAllRecipientsAsync(Mediator, Me))
        {
        }

        sink.Entries.Should().NotBeEmpty();
        sink.Entries.Should().AllSatisfy(e => e.Everything.Should().NotContain("did:", "log lines carry request ids, names and counts only"));

        var quiet = new FakeMediatorExchange().Client(new HandlerThrowingLogger<MediationClient>());
        (await quiet.RequestMediationAsync(Mediator, Me)).Should().NotBeNull();
    }

    // ---- helpers --------------------------------------------------------------------------

    internal static MediatorClientOptions Pinned(CoordinateMediationVersion version) => new() { CoordinateMediationVersion = version };

    internal static RecipientUpdate Add(string did) => new(did, RecipientUpdateAction.Add);

    internal static RecipientUpdate Remove(string did) => new(did, RecipientUpdateAction.Remove);

    internal static bool IsUuidV4(string id)
        => Guid.TryParseExact(id, "D", out _) && id.Length == 36 && id[14] == '4' && "89ab".Contains(id[19]) && id == id.ToLowerInvariant();

    private static Message Grant3(FakeExchangeRequest r, JsonNode routingDid)
        => ScriptedMediator.Raw(CoordinateMediation.MediateGrantType, Mediator, Me, r.Message.Id, new JsonObject { ["routing_did"] = routingDid });

    private static IEnumerable<Func<Task>> Operations(MediationClient client, string mediatorDid, string from, Uri? endpoint = null)
    {
        yield return () => client.RequestMediationAsync(mediatorDid, from, endpoint);
        yield return () => client.UpdateRecipientsAsync(mediatorDid, from, new[] { Add(Bob) }, endpoint);
        yield return () => client.QueryRecipientsAsync(mediatorDid, from, null, endpoint);
        yield return () => { _ = client.QueryAllRecipientsAsync(mediatorDid, from, endpoint); return Task.CompletedTask; };
    }

    /// <summary>An update list whose entries change after their first read.</summary>
    private sealed class ShiftingList(RecipientUpdate first, RecipientUpdate later) : IReadOnlyList<RecipientUpdate>
    {
        public int Reads;

        public RecipientUpdate this[int index] => Interlocked.Increment(ref Reads) == 1 ? first : later;

        public int Count => 1;

        public IEnumerator<RecipientUpdate> GetEnumerator()
        {
            yield return this[0];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
