using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Json;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>
/// <see cref="CoordinateMediationHandler"/> through the real dispatcher with real envelopes
/// (FR-MED-01, -03, -04, -05): who counts as a peer, what a <c>mediate-request</c> does, how
/// replies are addressed and threaded, what is ignored, and what reaches the log.
/// </summary>
public sealed class CoordinateMediationHandlerTests
{
    private const string P = CoordinateMediation.ProtocolUri;

    public static TheoryData<HandlerEnvelope, string> UnauthenticatedRequests
    {
        get
        {
            var data = new TheoryData<HandlerEnvelope, string>();
            foreach (var shape in new[] { HandlerEnvelope.Plaintext, HandlerEnvelope.Anoncrypt, HandlerEnvelope.SignedOnly, HandlerEnvelope.AnoncryptOfSigned })
            {
                foreach (var kind in new[] { "mediate-request", "recipient-update", "recipient-query" })
                    data.Add(shape, kind);
            }
            return data;
        }
    }

    // ---- peer identity (FR-MED-05) --------------------------------------------------------

    [Theory]
    [MemberData(nameof(UnauthenticatedRequests))]
    public async Task A_request_without_an_authcrypt_layer_is_not_applied_and_not_answered(HandlerEnvelope shape, string kind)
    {
        await using var fx = HandlerFixture.Create(fakeStore: true, fakePolicy: true, fakeIdentity: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        // Bob is enrolled and holds a DID, so an update or query that got through would show.
        await fx.EnrolAsync(bob.Did, mediator.Did);
        await fx.UpdateAsync(bob.Did, mediator.Did, HandlerMessages.Add("did:example:bobs"));
        fx.FakeStore!.ResetCalls();
        var policyCalls = fx.Policy!.TotalCalls;
        var identityReads = fx.Identity!.Reads;

        var message = kind switch
        {
            "mediate-request" => CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did),
            "recipient-update" => CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, new[] { HandlerMessages.Remove("did:example:bobs"), HandlerMessages.Add("did:example:new") }),
            _ => CoordinateMediation.CreateRecipientQuery(bob.Did, mediator.Did),
        };
        var outcome = await fx.SendAsync(message, mediator.Did, shape);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        outcome.Reply.Should().BeNull();
        fx.FakeStore.Calls.Should().BeEmpty("the store is not even read for an unauthenticated message");
        fx.Policy.TotalCalls.Should().Be(policyCalls);
        fx.Identity.Reads.Should().Be(identityReads);
        (await fx.RegisteredAsync(bob.Did)).Should().Equal("did:example:bobs");

        var line = fx.Logs.Handler.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which;
        line.Formatted.Should().Contain(message.Id).And.Contain("FR-MED-05");
        line.Everything.Should().NotContain(bob.Did).And.NotContain(mediator.Did).And.NotContain("did:example:");
        line.Exception.Should().BeNull();
    }

    [Fact]
    public async Task An_unauthenticated_request_from_a_stranger_creates_no_grant()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var mallory = await fx.NewPeerAsync();

        foreach (var shape in new[] { HandlerEnvelope.Plaintext, HandlerEnvelope.Anoncrypt, HandlerEnvelope.SignedOnly, HandlerEnvelope.AnoncryptOfSigned })
        {
            var outcome = await fx.SendAsync(CoordinateMediation.CreateMediateRequest(mallory.Did, mediator.Did), mediator.Did, shape);
            outcome.Result.Should().Be(DispatchResult.NoReply, shape.ToString());
        }

        fx.Memory.GrantCount.Should().Be(0);
    }

    [Theory]
    [InlineData(HandlerEnvelope.Plaintext)]
    [InlineData(HandlerEnvelope.Anoncrypt)]
    [InlineData(HandlerEnvelope.SignedOnly)]
    [InlineData(HandlerEnvelope.AnoncryptOfSigned)]
    public async Task A_malformed_unauthenticated_request_is_dropped_like_a_well_formed_one(HandlerEnvelope shape)
    {
        // The body is never read for an unauthenticated message, so no shape of it can throw.
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var mallory = await fx.NewPeerAsync();
        var bodies = new[]
        {
            (P + "/recipient-update", new JsonObject { ["updates"] = 7 }),
            (P + "/recipient-update", new JsonObject { ["updates"] = new JsonArray(new JsonObject { ["recipient_did"] = new string('x', 5000), ["action"] = "add" }) }),
            (P + "/recipient-query", new JsonObject { ["paginate"] = new JsonObject { ["limit"] = "10" } }),
            (P + "/no-such-message", new JsonObject()),
        };

        foreach (var (type, body) in bodies)
        {
            var outcome = await fx.SendAsync(HandlerMessages.Raw(type, mallory.Did, mediator.Did, body), mediator.Did, shape);
            outcome.Result.Should().Be(DispatchResult.NoReply);
        }
    }

    [Theory]
    [InlineData(HandlerEnvelope.Authcrypt)]
    [InlineData(HandlerEnvelope.AuthcryptOfSigned)]
    [InlineData(HandlerEnvelope.AnoncryptOfAuthcrypt)]
    public async Task Every_envelope_with_an_authcrypt_layer_is_acted_on(HandlerEnvelope shape)
    {
        await using var fx = HandlerFixture.Create(fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();

        var request = CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did);
        var outcome = await fx.SendAsync(request, mediator.Did, shape);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        CoordinateMediation.TryParseGrant(outcome.Reply!, out _).Should().BeTrue();
        fx.Policy!.MediateRequests.Should().Equal(bob.Did);
        (await fx.Memory.GetGrantAsync(bob.Did, default))!.PeerDid.Should().Be(bob.Did);
    }

    [Fact]
    public async Task The_peer_is_the_authcrypt_sender_key_not_the_plaintext_from()
    {
        // A message that names no `from` at all is still attributed to the key that authcrypted it.
        await using var fx = HandlerFixture.Create(fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var anonymous = new MessageBuilder().WithType(CoordinateMediation.MediateRequestType).WithTo(mediator.Did).WithBody(new JsonObject()).Build();

        var outcome = await fx.SendAsync(anonymous, mediator.Did, HandlerEnvelope.Authcrypt, sender: bob.Did);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        outcome.Reply!.To.Should().Equal(bob.Did);
        fx.Policy!.MediateRequests.Should().Equal(bob.Did);
        (await fx.Memory.GetGrantAsync(bob.Did, default)).Should().NotBeNull();
    }

    [Fact]
    public async Task The_unauthenticated_drop_line_is_rate_limited()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var mallory = await fx.NewPeerAsync();
        var received = await fx.ReceiveAsync(CoordinateMediation.CreateMediateRequest(mallory.Did, mediator.Did), mediator.Did, HandlerEnvelope.Plaintext);

        for (var i = 0; i < 2001; i++)
            await fx.Host.Dispatcher.DispatchAsync(received, fx.Host.Client, fx.Host.Options);

        var lines = fx.Logs.Handler;
        lines.Should().HaveCount(2001);
        lines.Count(e => e.Level == LogLevel.Warning).Should().Be(3, "the 1st, the 1000th and the 2000th");
        lines.Count(e => e.Level == LogLevel.Debug).Should().Be(1998);
        lines[0].Level.Should().Be(LogLevel.Warning);
        lines[999].Level.Should().Be(LogLevel.Warning);
        lines[1999].Level.Should().Be(LogLevel.Warning);
    }

    // ---- mediate-request ------------------------------------------------------------------

    [Fact]
    public async Task Without_configured_routing_dids_the_grant_carries_the_did_that_decrypted_the_request()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var otherMediatorDid = await fx.NewPeerAsync(); // the container holds its keys too
        var bob = await fx.NewPeerAsync();
        var carol = await fx.NewPeerAsync();

        (await fx.EnrolAsync(bob.Did, mediator.Did)).Should().Equal(mediator.Did);
        (await fx.EnrolAsync(carol.Did, otherMediatorDid.Did)).Should().Equal(otherMediatorDid.Did);
    }

    [Fact]
    public async Task Configured_routing_dids_are_granted_in_order()
    {
        await using var fx = HandlerFixture.Create(o =>
        {
            o.RoutingDids.Add("did:example:routing-1");
            o.RoutingDids.Add("did:example:routing-2");
        });
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();

        var outcome = await fx.SendAsync(CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did), mediator.Did);

        CoordinateMediation.TryParseGrant(outcome.Reply!, out var routingDids).Should().BeTrue();
        routingDids.Should().Equal("did:example:routing-1", "did:example:routing-2");
        JsonSerializer.Serialize(outcome.Reply, DidCommJson.Default).Should().Contain("\"routing_did\":[\"did:example:routing-1\",\"did:example:routing-2\"]");
        (await fx.Memory.GetGrantAsync(bob.Did, default))!.RoutingDids.Should().Equal("did:example:routing-1", "did:example:routing-2");
    }

    [Fact]
    public async Task A_host_identity_supplies_the_routing_dids()
    {
        await using var fx = HandlerFixture.Create(o => o.RoutingDids.Add("did:example:from-options"), fakeIdentity: true);
        fx.Identity!.Get = () => new[] { "did:example:from-identity" };
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();

        (await fx.EnrolAsync(bob.Did, mediator.Did)).Should().Equal(new[] { "did:example:from-identity" }, "a registered IMediatorIdentity replaces the options list");
    }

    [Fact]
    public async Task A_second_request_returns_the_same_grant_keeps_registrations_and_does_not_ask_the_policy()
    {
        await using var fx = HandlerFixture.Create(fakePolicy: true, fakeIdentity: true);
        fx.Identity!.Get = () => new[] { "did:example:routing-old" };
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        (await fx.EnrolAsync(bob.Did, mediator.Did)).Should().Equal("did:example:routing-old");
        await fx.UpdateAsync(bob.Did, mediator.Did, HandlerMessages.Add("did:example:bobs"));

        // The mediator's configuration changes and the policy would now refuse: neither matters
        // for a peer that already holds a grant (FR-MED-05: idempotent).
        fx.Identity.Get = () => new[] { "did:example:routing-new" };
        fx.Policy!.OnRequest = (_, _, _) => new ValueTask<MediationDecision>(MediationDecision.Deny);

        (await fx.EnrolAsync(bob.Did, mediator.Did)).Should().Equal("did:example:routing-old");
        (await fx.EnrolAsync(bob.Did, mediator.Did)).Should().Equal("did:example:routing-old");

        fx.Policy.MediateRequests.Should().HaveCount(1, "the policy decides new grants only");
        (await fx.RegisteredAsync(bob.Did)).Should().Equal("did:example:bobs");
        fx.Memory.GrantCount.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_denying_policy_answers_mediate_deny_and_stores_nothing(bool defaultValue)
    {
        await using var fx = HandlerFixture.Create(fakePolicy: true);
        fx.Policy!.OnRequest = (_, _, _) => new ValueTask<MediationDecision>(defaultValue ? default : MediationDecision.Deny);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();

        var request = CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did);
        var outcome = await fx.SendAsync(request, mediator.Did);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        CoordinateMediation.TryParseDeny(outcome.Reply!).Should().BeTrue();
        outcome.Reply!.Type.Should().Be(CoordinateMediation.MediateDenyType);
        outcome.Reply.Thid.Should().Be(request.Id);
        outcome.Reply.From.Should().Be(mediator.Did);
        outcome.Reply.To.Should().Equal(bob.Did);
        JsonSerializer.Serialize(outcome.Reply, DidCommJson.Default).Should().Contain("\"body\":{}");
        fx.Memory.GrantCount.Should().Be(0);

        // Denied is not enrolled: an update gets client_error for every entry.
        (await fx.UpdateAsync(bob.Did, mediator.Did, HandlerMessages.Add("did:example:x")))
            .Should().Equal(HandlerMessages.Added("did:example:x", RecipientUpdateOutcome.ClientError));
    }

    [Fact]
    public async Task The_policy_sees_the_peer_did_and_the_dispatch_context()
    {
        await using var fx = HandlerFixture.Create(fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();

        await fx.EnrolAsync(bob.Did, mediator.Did);

        fx.Policy!.MediateRequests.Should().Equal(bob.Did);
        var context = fx.Policy.Contexts.Should().ContainSingle().Which;
        context.Received.SenderKid.Should().StartWith(bob.Did + "#");
        context.Received.Message.Type.Should().Be(CoordinateMediation.MediateRequestType);
    }

    [Fact]
    public async Task A_full_store_denies_new_peers_and_still_serves_existing_ones()
    {
        await using var fx = HandlerFixture.Create(o => o.MaxGrants = 1);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var carol = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);

        var denied = await fx.SendAsync(CoordinateMediation.CreateMediateRequest(carol.Did, mediator.Did), mediator.Did);
        CoordinateMediation.TryParseDeny(denied.Reply!).Should().BeTrue("the mediator is unwilling while it is full (JC-3)");
        (await fx.Memory.GetGrantAsync(carol.Did, default)).Should().BeNull();

        (await fx.EnrolAsync(bob.Did, mediator.Did)).Should().Equal(mediator.Did);
        (await fx.UpdateAsync(bob.Did, mediator.Did, HandlerMessages.Add("did:example:bobs")))
            .Should().Equal(HandlerMessages.Added("did:example:bobs", RecipientUpdateOutcome.Success));

        var line = fx.Logs.Handler.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which;
        line.Formatted.Should().Contain("maximum number of grants");
        line.Everything.Should().NotContain(carol.Did).And.NotContain(bob.Did);
    }

    public static TheoryData<string> RequestFaults => new() { "policy", "store-get", "store-grant", "identity", "identity-did-url", "identity-over-long", "identity-not-a-did", "identity-null-entry" };

    [Theory]
    [MemberData(nameof(RequestFaults))]
    public async Task A_fault_while_handling_a_mediate_request_grants_nothing_sends_nothing_and_never_escapes_dispatch(string fault)
    {
        await using var fx = HandlerFixture.Create(fakeStore: true, fakePolicy: true, fakeIdentity: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var thrown = true;
        switch (fault)
        {
            case "policy":
                fx.Policy!.OnRequest = (_, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker + bob.Did);
                break;
            case "store-get":
                fx.FakeStore!.OnGetGrant = (_, _) => throw new HandlerBoomException(HandlerBoomException.Marker + bob.Did);
                break;
            case "store-grant":
                fx.FakeStore!.OnGrant = (_, _) => throw new HandlerBoomException(HandlerBoomException.Marker + bob.Did);
                break;
            case "identity":
                fx.Identity!.Get = () => throw new HandlerBoomException(HandlerBoomException.Marker + bob.Did);
                break;
            case "identity-did-url":
                fx.Identity!.Get = () => new[] { "did:example:routing#key-1" };
                thrown = false;
                break;
            case "identity-over-long":
                fx.Identity!.Get = () => new[] { "did:example:" + new string('r', 2049) };
                thrown = false;
                break;
            case "identity-not-a-did":
                fx.Identity!.Get = () => new[] { "https://mediator.example/didcomm" };
                thrown = false;
                break;
            default:
                fx.Identity!.Get = () => new string[] { null! };
                thrown = false;
                break;
        }

        var outcome = await fx.SendAsync(CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did), mediator.Did);

        outcome.Result.Should().Be(DispatchResult.NoReply, "the protocol has no error reply for a mediate-request");
        fx.Memory.GrantCount.Should().Be(0);
        var line = fx.Logs.Handler.Should().ContainSingle(e => e.Level == LogLevel.Error).Which;
        line.Exception.Should().BeNull("an exception object is never handed to the logger");
        line.Everything.Should().NotContain(HandlerBoomException.Marker).And.NotContain(bob.Did).And.NotContain("did:example:routing").And.NotContain("mediator.example");
        if (thrown)
            line.Formatted.Should().Contain(nameof(HandlerBoomException));

        // The fault was transient: the same peer can enrol once it is gone.
        fx.Policy!.OnRequest = (_, _, _) => new ValueTask<MediationDecision>(MediationDecision.Grant);
        fx.FakeStore!.OnGetGrant = null;
        fx.FakeStore.OnGrant = null;
        fx.Identity!.Get = () => Array.Empty<string>();
        (await fx.EnrolAsync(bob.Did, mediator.Did)).Should().Equal(mediator.Did);
    }

    [Fact]
    public async Task A_stored_grant_the_reply_cannot_be_built_from_is_a_server_fault_not_an_exception()
    {
        // A host store hands back a grant whose routing DID is over the wire bound.
        await using var fx = HandlerFixture.Create(fakeStore: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        fx.FakeStore!.OnGetGrant = (peer, _) => new ValueTask<MediationGrant?>(new MediationGrant(peer, new[] { "did:example:" + new string('r', 3000) }));

        var outcome = await fx.SendAsync(CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did), mediator.Did);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        fx.Logs.Handler.Should().ContainSingle(e => e.Level == LogLevel.Error).Which.Everything.Should().NotContain("rrrr");
    }

    [Fact]
    public async Task Cancellation_propagates_and_is_not_swallowed_as_a_fault()
    {
        await using var fx = HandlerFixture.Create(fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var received = await fx.ReceiveAsync(CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did), mediator.Did);

        // Cancelled before the handler runs.
        using var before = new CancellationTokenSource();
        before.Cancel();
        await FluentActions.Awaiting(() => fx.Host.Dispatcher.DispatchAsync(received, fx.Host.Client, fx.Host.Options, before.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        // Cancelled while the policy is deciding.
        using var during = new CancellationTokenSource();
        fx.Policy!.OnRequest = (_, _, ct) =>
        {
            during.Cancel();
            ct.ThrowIfCancellationRequested();
            return new ValueTask<MediationDecision>(MediationDecision.Grant);
        };
        await FluentActions.Awaiting(() => fx.Host.Dispatcher.DispatchAsync(received, fx.Host.Client, fx.Host.Options, during.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        fx.Memory.GrantCount.Should().Be(0);
        fx.Logs.Handler.Should().NotContain(e => e.Level >= LogLevel.Warning, "a cancelled request is not a store or policy fault");
    }

    // ---- threading and addressing (FR-MED-03/04) ------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("an-existing-thread")]
    public async Task Every_reply_is_on_the_requests_thread_from_the_decrypting_did_to_the_peer(string? thid)
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();

        var requests = new[]
        {
            HandlerMessages.Raw(CoordinateMediation.MediateRequestType, bob.Did, mediator.Did, new JsonObject(), id: "req-1", thid: thid),
            HandlerMessages.Raw(CoordinateMediation.RecipientUpdateType, bob.Did, mediator.Did, HandlerMessages.UpdatesBody(("did:example:r1", "add")), id: "req-2", thid: thid),
            HandlerMessages.Raw(CoordinateMediation.RecipientQueryType, bob.Did, mediator.Did, new JsonObject(), id: "req-3", thid: thid),
        };
        var replyTypes = new[] { CoordinateMediation.MediateGrantType, CoordinateMediation.RecipientUpdateResponseType, CoordinateMediation.RecipientType };

        for (var i = 0; i < requests.Length; i++)
        {
            var outcome = await fx.SendAsync(requests[i], mediator.Did);

            outcome.Result.Should().Be(DispatchResult.ReplyProduced);
            var reply = outcome.Reply!;
            reply.Type.Should().Be(replyTypes[i]);
            reply.Thid.Should().Be(thid ?? requests[i].Id, "thid = the request's thid if present, else its id (DD-11)");
            reply.From.Should().Be(mediator.Did);
            reply.To.Should().Equal(bob.Did);
            reply.Id.Should().NotBe(requests[i].Id);
            reply.CreatedTime.Should().BeNull("the session stamps created_time when it sends (FR-RR-09); the handler does not");
        }
    }

    [Fact]
    public async Task A_reply_is_produced_with_and_without_a_return_route_session()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var carol = await fx.NewPeerAsync();

        // No session: the 3-argument dispatch (context.Session is null).
        var without = await fx.SendAsync(CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did), mediator.Did);
        without.Result.Should().Be(DispatchResult.ReplyProduced);

        // With a session: the handler returns the reply to the dispatcher and pushes nothing itself.
        var session = new HandlerFakeSession(carol.Did, mediator.Did);
        var received = await fx.ReceiveAsync(CoordinateMediation.CreateMediateRequest(carol.Did, mediator.Did), mediator.Did);
        var with = await fx.Host.Dispatcher.DispatchAsync(received, fx.Host.Client, fx.Host.Options, Task.CompletedTask, session, default);
        with.Result.Should().Be(DispatchResult.ReplyProduced);
        CoordinateMediation.TryParseGrant(with.Reply!, out _).Should().BeTrue();
        session.Sent.Should().BeEmpty();
    }

    // ---- messages the mediator role does not answer ---------------------------------------

    [Fact]
    public async Task Responses_and_unknown_names_are_ignored()
    {
        await using var fx = HandlerFixture.Create(fakeStore: true, fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var ignored = new[]
        {
            CoordinateMediation.CreateGrant(bob.Did, mediator.Did, new[] { "did:example:r" }, "t"),
            CoordinateMediation.CreateDeny(bob.Did, mediator.Did, "t"),
            CoordinateMediation.CreateRecipientUpdateResponse(bob.Did, mediator.Did, new[] { HandlerMessages.Added("did:example:r", RecipientUpdateOutcome.Success) }, "t"),
            CoordinateMediation.CreateRecipient(bob.Did, mediator.Did, new[] { "did:example:r" }, null, "t"),
            HandlerMessages.Raw(P + "/keylist-update", bob.Did, mediator.Did, HandlerMessages.UpdatesBody(("did:example:r", "add"))),
            HandlerMessages.Raw(P + "/no-such-message", bob.Did, mediator.Did, null),
            // A response with a body that would be malformed for its own parser is ignored too.
            HandlerMessages.Raw(CoordinateMediation.MediateGrantType, bob.Did, mediator.Did, new JsonObject { ["routing_did"] = 7 }),
        };

        foreach (var message in ignored)
        {
            var outcome = await fx.SendAsync(message, mediator.Did);
            outcome.Result.Should().Be(DispatchResult.NoReply, message.Type);
            outcome.Handler.Should().BeOfType<CoordinateMediationHandler>();
        }

        fx.FakeStore!.Calls.Should().BeEmpty();
        fx.Policy!.TotalCalls.Should().Be(0);
        fx.Logs.Handler.Should().OnlyContain(e => e.Level == LogLevel.Debug);
    }

    [Theory]
    [InlineData("https://didcomm.org/coordinate-mediation/3.1/mediate-request")]
    [InlineData("https://didcomm.org/coordinate-mediation/3.0/MEDIATE_REQUEST")]
    public async Task A_compatible_minor_version_or_spelling_of_the_type_is_handled(string type)
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();

        var outcome = await fx.SendAsync(HandlerMessages.Raw(type, bob.Did, mediator.Did, null), mediator.Did);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        outcome.Reply!.Type.Should().Be(CoordinateMediation.MediateGrantType, "replies always use the 3.0 type URI");
    }

    [Fact]
    public void The_handler_serves_the_3_0_piuri_in_the_mediator_role()
    {
        var handler = new CoordinateMediationHandler(new InMemoryMediationStore(), new DefaultMediationPolicy(), new HandlerFakeIdentity(), Microsoft.Extensions.Options.Options.Create(new CoordinateMediationOptions()));

        handler.ProtocolUri.Should().Be("https://didcomm.org/coordinate-mediation/3.0");
        handler.Roles.Should().Equal("mediator");
        var mutate = () => ((IList<string>)handler.Roles)[0] = "recipient";
        mutate.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void The_handler_refuses_missing_collaborators_and_bad_options()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new CoordinateMediationOptions());
        var store = new InMemoryMediationStore();
        var policy = new DefaultMediationPolicy();
        var identity = new HandlerFakeIdentity();

        FluentActions.Invoking(() => new CoordinateMediationHandler(null!, policy, identity, options)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new CoordinateMediationHandler(store, null!, identity, options)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new CoordinateMediationHandler(store, policy, null!, options)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new CoordinateMediationHandler(store, policy, identity, null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new CoordinateMediationHandler(store, policy, identity, Microsoft.Extensions.Options.Options.Create(new CoordinateMediationOptions { MaxPageSize = 0 })))
            .Should().Throw<ArgumentException>();
    }

    // ---- logs -----------------------------------------------------------------------------

    [Fact]
    public async Task No_log_line_at_any_level_carries_a_did_and_a_long_id_is_not_written()
    {
        await using var fx = HandlerFixture.Create(o => o.MaxGrants = 2, fakeStore: true, fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var carol = await fx.NewPeerAsync();
        var dave = await fx.NewPeerAsync();
        var longId = new string('i', 300);

        // One of everything that logs: unauthenticated drops, an ignored type, a capacity denial,
        // policy and store faults, and ordinary traffic in between.
        await fx.SendAsync(HandlerMessages.Raw(CoordinateMediation.MediateRequestType, bob.Did, mediator.Did, null, id: longId), mediator.Did, HandlerEnvelope.Anoncrypt);
        await fx.SendAsync(CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did), mediator.Did, HandlerEnvelope.SignedOnly);
        await fx.EnrolAsync(bob.Did, mediator.Did);
        await fx.EnrolAsync(carol.Did, mediator.Did);
        await fx.SendAsync(CoordinateMediation.CreateMediateRequest(dave.Did, mediator.Did), mediator.Did); // full → deny
        await fx.SendAsync(CoordinateMediation.CreateDeny(bob.Did, mediator.Did, "t"), mediator.Did);       // ignored
        fx.Policy!.OnRegister = (_, did, _, _) => did.EndsWith("boom", StringComparison.Ordinal)
            ? throw new HandlerBoomException(HandlerBoomException.Marker + did)
            : new ValueTask<bool>(true);
        await fx.UpdateAsync(bob.Did, mediator.Did, HandlerMessages.Add("did:example:ok"), HandlerMessages.Add("did:example:boom"), HandlerMessages.Add("did:example:url#frag"));
        fx.FakeStore!.OnUpdateRecipients = (_, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker + bob.Did);
        await fx.UpdateAsync(bob.Did, mediator.Did, HandlerMessages.Add("did:example:second"));
        fx.FakeStore.OnListRecipients = (_, _, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker + bob.Did);
        await fx.SendAsync(HandlerMessages.Raw(CoordinateMediation.RecipientQueryType, bob.Did, mediator.Did, null, id: longId), mediator.Did);

        var lines = fx.Logs.Handler;
        lines.Should().HaveCountGreaterThanOrEqualTo(7);
        lines.Should().Contain(e => e.Level == LogLevel.Error).And.Contain(e => e.Level == LogLevel.Warning).And.Contain(e => e.Level == LogLevel.Debug);
        foreach (var line in lines)
        {
            line.Exception.Should().BeNull();
            line.Everything.Should().NotContain("did:", "no DID, kid or recipient_did reaches a log line");
            line.Everything.Should().NotContain(HandlerBoomException.Marker, "exception text is never logged, only the type name");
            line.Everything.Should().NotContain(longId, "an over-long message id is replaced");
        }
        lines.Should().Contain(e => e.Formatted.Contains("(id of 300 characters)"));
        lines.Should().Contain(e => e.Formatted.Contains(nameof(HandlerBoomException)));
    }

    [Fact]
    public async Task A_throwing_logging_provider_changes_no_outcome()
    {
        await using var fx = HandlerFixture.Create(o => o.MaxGrants = 1, fakeStore: true, throwingLogger: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var carol = await fx.NewPeerAsync();

        (await fx.SendAsync(CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did), mediator.Did, HandlerEnvelope.Anoncrypt)).Result.Should().Be(DispatchResult.NoReply);
        (await fx.EnrolAsync(bob.Did, mediator.Did)).Should().Equal(mediator.Did);
        CoordinateMediation.TryParseDeny((await fx.SendAsync(CoordinateMediation.CreateMediateRequest(carol.Did, mediator.Did), mediator.Did)).Reply!).Should().BeTrue();
        (await fx.SendAsync(CoordinateMediation.CreateDeny(bob.Did, mediator.Did, "t"), mediator.Did)).Result.Should().Be(DispatchResult.NoReply);
        fx.FakeStore!.OnUpdateRecipients = (_, _, _) => throw new HandlerBoomException("x");
        (await fx.UpdateAsync(bob.Did, mediator.Did, HandlerMessages.Add("did:example:a")))
            .Should().Equal(HandlerMessages.Added("did:example:a", RecipientUpdateOutcome.ServerError));
        fx.FakeStore.OnListRecipients = (_, _, _, _) => throw new HandlerBoomException("x");
        (await fx.SendAsync(CoordinateMediation.CreateRecipientQuery(bob.Did, mediator.Did), mediator.Did)).Result.Should().Be(DispatchResult.NoReply);
    }

    [Theory]
    [InlineData(null, "(none)")]
    [InlineData("short-id", "short-id")]
    public void LogId_passes_ordinary_ids_through(string? id, string expected)
        => MediationCoordinator.LogId(id).Should().Be(expected);

    [Fact]
    public void LogId_replaces_an_id_over_128_characters()
    {
        MediationCoordinator.LogId(new string('a', 128)).Should().HaveLength(128);
        MediationCoordinator.LogId(new string('a', 129)).Should().Be("(id of 129 characters)");
    }
}
