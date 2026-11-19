using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Json;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.Logging;
using Xunit;
using static DidComm.Mediator.Tests.Coordination.HandlerMessages;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>
/// The Coordinate Mediation 2.0 mediator role through the real dispatcher with real envelopes
/// (FR-MED-10, with FR-MED-04/-05/-06/-07/-08 as 3.0 applies them): a grant names one routing DID
/// as a JSON string, the first; <c>keylist-update</c> results and refusals; <c>keylist-query</c>
/// pages listed as <c>keys</c>; who counts as a peer; how replies are typed, addressed and threaded;
/// what is ignored; cancellation; and what reaches the log.
/// </summary>
public sealed class CoordinateMediation20HandlerTests
{
    private const RecipientUpdateOutcome Success = RecipientUpdateOutcome.Success;
    private const RecipientUpdateOutcome NoChange = RecipientUpdateOutcome.NoChange;
    private const RecipientUpdateOutcome ClientError = RecipientUpdateOutcome.ClientError;
    private const RecipientUpdateOutcome ServerError = RecipientUpdateOutcome.ServerError;

    // ---- mediate-request: the grant names the first routing DID, as a string ---------------

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_grant_carries_the_first_configured_routing_did_as_a_json_string(int configured)
    {
        var routing = Enumerable.Range(1, configured).Select(i => $"did:example:routing-{i}").ToArray();
        await using var fx = HandlerFixture.Create(o =>
        {
            foreach (var did in routing)
                o.RoutingDids.Add(did);
        });
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();

        var outcome = await fx.SendAsync(CoordinateMediation20.CreateMediateRequest(bob.Did, mediator.Did), mediator.Did);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        outcome.Handler.Should().BeOfType<CoordinateMediation20Handler>();
        var reply = outcome.Reply!;
        reply.Type.Should().Be(CoordinateMediation20.MediateGrantType);
        var routingDid = reply.Body!["routing_did"]!;
        routingDid.GetValueKind().Should().Be(JsonValueKind.String, "2.0 wallets read routing_did as a single string (I-1)");
        routingDid.GetValue<string>().Should().Be("did:example:routing-1", "a 2.0 grant names the first routing DID, the primary one");
        JsonSerializer.Serialize(reply, DidCommJson.Default).Should().Contain("\"body\":{\"routing_did\":\"did:example:routing-1\"}");

        // The stored grant keeps the whole list: a 3.0 re-request by the same peer lists it all.
        (await fx.Memory.GetGrantAsync(bob.Did, default))!.RoutingDids.Should().Equal(routing);
        (await fx.EnrolAsync(bob.Did, mediator.Did)).Should().Equal(routing);
    }

    [Fact]
    public async Task Without_configured_routing_dids_the_grant_carries_the_did_that_decrypted_the_request()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var otherMediatorDid = await fx.NewPeerAsync(); // the container holds its keys too
        var bob = await fx.NewPeerAsync();
        var carol = await fx.NewPeerAsync();

        (await fx.Enrol20Async(bob.Did, mediator.Did)).Should().Be(mediator.Did);
        (await fx.Enrol20Async(carol.Did, otherMediatorDid.Did)).Should().Be(otherMediatorDid.Did);
    }

    [Fact]
    public async Task A_host_identity_supplies_the_routing_did_and_the_grant_names_its_first()
    {
        await using var fx = HandlerFixture.Create(o => o.RoutingDids.Add("did:example:from-options"), fakeIdentity: true);
        fx.Identity!.Get = () => new[] { "did:example:identity-1", "did:example:identity-2" };
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();

        (await fx.Enrol20Async(bob.Did, mediator.Did)).Should().Be("did:example:identity-1", "a registered IMediatorIdentity replaces the options list");
    }

    [Fact]
    public async Task A_repeat_mediate_request_returns_the_same_grant_and_the_policy_is_asked_once()
    {
        await using var fx = HandlerFixture.Create(fakePolicy: true, fakeIdentity: true);
        fx.Identity!.Get = () => new[] { "did:example:routing-old" };
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        (await fx.Enrol20Async(bob.Did, mediator.Did)).Should().Be("did:example:routing-old");
        await fx.Update20Async(bob.Did, mediator.Did, Add("did:example:bobs"));

        // The configuration changes and the policy would now refuse: neither matters for a peer
        // that already holds a grant (FR-MED-05: idempotent).
        fx.Identity.Get = () => new[] { "did:example:routing-new" };
        fx.Policy!.OnRequest = (_, _, _) => new ValueTask<MediationDecision>(MediationDecision.Deny);

        (await fx.Enrol20Async(bob.Did, mediator.Did)).Should().Be("did:example:routing-old");
        (await fx.Enrol20Async(bob.Did, mediator.Did)).Should().Be("did:example:routing-old");

        fx.Policy.MediateRequests.Should().HaveCount(1, "the policy decides new grants only");
        (await fx.RegisteredAsync(bob.Did)).Should().Equal("did:example:bobs");
        fx.Memory.GrantCount.Should().Be(1);
    }

    [Fact]
    public async Task Reordering_the_routing_dids_after_a_grant_leaves_the_repeat_grant_on_the_stored_first_did()
    {
        // DC-3: the host migrates by listing the new DID first. A peer that already holds a grant
        // gets it unchanged; only new grants name the new primary.
        await using var fx = HandlerFixture.Create(fakeIdentity: true);
        fx.Identity!.Get = () => new[] { "did:example:routing-a", "did:example:routing-b" };
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var carol = await fx.NewPeerAsync();
        (await fx.Enrol20Async(bob.Did, mediator.Did)).Should().Be("did:example:routing-a");

        fx.Identity.Get = () => new[] { "did:example:routing-b", "did:example:routing-a" };

        (await fx.Enrol20Async(bob.Did, mediator.Did)).Should().Be("did:example:routing-a", "a grant already issued keeps the DIDs it was issued with");
        (await fx.Enrol20Async(carol.Did, mediator.Did)).Should().Be("did:example:routing-b");
        (await fx.Memory.GetGrantAsync(bob.Did, default))!.RoutingDids.Should().Equal("did:example:routing-a", "did:example:routing-b");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_denying_policy_answers_mediate_deny_with_an_empty_body_and_stores_nothing(bool defaultValue)
    {
        await using var fx = HandlerFixture.Create(fakePolicy: true);
        fx.Policy!.OnRequest = (_, _, _) => new ValueTask<MediationDecision>(defaultValue ? default : MediationDecision.Deny);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();

        var request = CoordinateMediation20.CreateMediateRequest(bob.Did, mediator.Did);
        var outcome = await fx.SendAsync(request, mediator.Did);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        var reply = outcome.Reply!;
        reply.Type.Should().Be(CoordinateMediation20.MediateDenyType);
        reply.Thid.Should().Be(request.Id);
        reply.From.Should().Be(mediator.Did);
        reply.To.Should().Equal(bob.Did);
        JsonSerializer.Serialize(reply, DidCommJson.Default).Should().Contain("\"body\":{}");
        fx.Memory.GrantCount.Should().Be(0);

        // Denied is not enrolled: an update gets client_error for every entry.
        (await fx.Update20Async(bob.Did, mediator.Did, Add("did:example:x")))
            .Should().Equal(Added("did:example:x", ClientError));
    }

    [Fact]
    public async Task A_full_store_answers_a_new_peer_with_mediate_deny_and_still_serves_existing_ones()
    {
        await using var fx = HandlerFixture.Create(o => o.MaxGrants = 1);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var carol = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);

        var denied = await fx.SendAsync(CoordinateMediation20.CreateMediateRequest(carol.Did, mediator.Did), mediator.Did);

        denied.Reply!.Type.Should().Be(CoordinateMediation20.MediateDenyType);
        JsonSerializer.Serialize(denied.Reply, DidCommJson.Default).Should().Contain("\"body\":{}");
        (await fx.Memory.GetGrantAsync(carol.Did, default)).Should().BeNull();
        (await fx.Enrol20Async(bob.Did, mediator.Did)).Should().Be(mediator.Did);

        var line = fx.Logs.Handler20.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which;
        line.Formatted.Should().Contain("maximum number of grants");
        line.Everything.Should().NotContain(carol.Did).And.NotContain(bob.Did);
    }

    public static TheoryData<string> UnbuildableStoredGrants => new() { "empty", "null-entry", "over-long" };

    [Theory]
    [MemberData(nameof(UnbuildableStoredGrants))]
    public async Task A_stored_grant_without_a_usable_first_routing_did_is_not_answered_and_nothing_is_thrown(string shape)
    {
        // A host store hands back a grant the 2.0 reply cannot be built from. That is the
        // mediator's fault, chosen inside the reply's fault boundary (DC-3): logged by type, no reply.
        await using var fx = HandlerFixture.Create(fakeStore: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        IReadOnlyList<string> stored = shape switch
        {
            "empty" => Array.Empty<string>(),
            "null-entry" => new string[] { null!, "did:example:routing-2" },
            _ => new[] { "did:example:" + new string('r', 3000) },
        };
        fx.FakeStore!.OnGetGrant = (peer, _) => new ValueTask<MediationGrant?>(new MediationGrant(peer, stored));
        var request = CoordinateMediation20.CreateMediateRequest(bob.Did, mediator.Did);

        var outcome = await fx.SendAsync(request, mediator.Did);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        outcome.Reply.Should().BeNull();
        var line = fx.Logs.Handler20.Should().ContainSingle().Which;
        line.Level.Should().Be(LogLevel.Error);
        line.Formatted.Should().Contain(nameof(ArgumentException)).And.Contain(request.Id);
        line.Exception.Should().BeNull();
        line.Everything.Should().NotContain("did:").And.NotContain("rrrr");
    }

    // ---- keylist-update -------------------------------------------------------------------

    [Fact]
    public async Task One_keylist_update_can_produce_every_result_value_in_input_order()
    {
        await using var fx = HandlerFixture.Create(o => o.MaxRecipientsPerGrant = 2, fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var mallory = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);
        await fx.Enrol20Async(mallory.Did, mediator.Did);
        await fx.Update20Async(mallory.Did, mediator.Did, Add("did:example:mallorys"));
        await fx.Update20Async(bob.Did, mediator.Did, Add("did:example:already"));
        fx.Policy!.OnRegister = (_, did, _, _) => did switch
        {
            "did:example:policy-throws" => throw new HandlerBoomException(HandlerBoomException.Marker),
            "did:example:policy-refuses" => new ValueTask<bool>(false),
            _ => new ValueTask<bool>(true),
        };

        var request = CoordinateMediation20.CreateKeylistUpdate(bob.Did, mediator.Did, new[]
        {
            Add("did:example:new"),            // success (Bob now holds 2, the bound)
            Add("did:example:already"),        // no_change
            Add("did:example:mallorys"),       // client_error: another grant holds it
            Remove("did:example:mallorys"),    // client_error: not Bob's to remove
            Remove("did:example:never"),       // client_error: nobody's
            Add("did:example:url#key-1"),      // client_error: not a bare DID
            Add("did:example:policy-refuses"), // client_error: policy
            Add("did:example:policy-throws"),  // server_error: policy fault, this entry only
            Remove("did:example:already"),     // success (1 held)
            Add("did:example:third"),          // success (2 held)
            Add("did:example:fourth"),         // client_error: per-grant bound (2)
        });
        var outcome = await fx.SendAsync(request, mediator.Did);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        outcome.Reply!.Type.Should().Be(CoordinateMediation20.KeylistUpdateResponseType);
        CoordinateMediation20.TryParseKeylistUpdateResponse(outcome.Reply, out var updated).Should().BeTrue();
        updated.Should().Equal(
            Added("did:example:new", Success),
            Added("did:example:already", NoChange),
            Added("did:example:mallorys", ClientError),
            Removed("did:example:mallorys", ClientError),
            Removed("did:example:never", ClientError),
            Added("did:example:url#key-1", ClientError),
            Added("did:example:policy-refuses", ClientError),
            Added("did:example:policy-throws", ServerError),
            Removed("did:example:already", Success),
            Added("did:example:third", Success),
            Added("did:example:fourth", ClientError));

        (await fx.RegisteredAsync(bob.Did)).Should().Equal("did:example:new", "did:example:third");
        (await fx.RegisteredAsync(mallory.Did)).Should().Equal("did:example:mallorys");

        var wire = JsonSerializer.Serialize(outcome.Reply, DidCommJson.Default);
        wire.Should().Contain("\"updated\":[").And.Contain("\"result\":\"success\"").And.Contain("\"result\":\"no_change\"")
            .And.Contain("\"result\":\"client_error\"").And.Contain("\"result\":\"server_error\"");
    }

    [Theory]
    [InlineData("did:example:alice#key-1")]
    [InlineData("did:example:alice?q")]
    [InlineData("did:example:alice/p")]
    public async Task A_recipient_did_that_is_a_did_url_is_client_error_echoed_unchanged_and_nothing_is_registered(string value)
    {
        await using var fx = HandlerFixture.Create(fakeStore: true, fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);
        fx.FakeStore!.ResetCalls();

        var outcome = await fx.SendAsync(CoordinateMediation20.CreateKeylistUpdate(bob.Did, mediator.Did, new[] { Add(value), Remove(value) }), mediator.Did);

        CoordinateMediation20.TryParseKeylistUpdateResponse(outcome.Reply!, out var updated).Should().BeTrue();
        updated.Should().Equal(Added(value, ClientError), Removed(value, ClientError));
        var echoed = outcome.Reply!.Body!["updated"]!.AsArray().Select(e => e!["recipient_did"]!.GetValue<string>());
        echoed.Should().Equal(new[] { value, value }, "the entry is echoed as sent, never normalized into its DID");
        fx.FakeStore.UpdateCalls.Should().BeEmpty("nothing that is not a bare DID reaches the store");
        fx.Policy!.Registrations.Should().BeEmpty();
        fx.Memory.RecipientCount.Should().Be(0);
        (await fx.Memory.FindGrantByRecipientDidAsync("did:example:alice", default)).Should().BeNull("a DID URL is not reduced to its DID and registered");
    }

    [Fact]
    public async Task The_mediators_own_dids_cannot_be_registered_through_keylist_update()
    {
        await using var fx = HandlerFixture.Create(
            o =>
            {
                o.RoutingDids.Add("did:example:routing-1");
                o.RoutingDids.Add("did:example:routing-2");
            },
            fakePolicy: true,
            didComm: o => o.OwnIdentifiers = new[] { "did:example:own-1#key-1", "did:example:own-2" });
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        (await fx.Enrol20Async(bob.Did, mediator.Did)).Should().Be("did:example:routing-1");

        var updated = await fx.Update20Async(bob.Did, mediator.Did,
            Add(mediator.Did),              // the DID that decrypted this request
            Add("did:example:routing-1"),   // the routing DID the 2.0 grant named
            Add("did:example:routing-2"),   // a routing DID the 2.0 grant did not name: still the mediator's
            Add("did:example:own-1"),       // OwnIdentifiers, listed as a DID URL
            Add("did:example:own-2"),
            Add("did:example:other"));

        updated.Should().Equal(
            Added(mediator.Did, ClientError),
            Added("did:example:routing-1", ClientError),
            Added("did:example:routing-2", ClientError),
            Added("did:example:own-1", ClientError),
            Added("did:example:own-2", ClientError),
            Added("did:example:other", Success));
        fx.Policy!.Registrations.Select(r => r.RecipientDid).Should().Equal(new[] { "did:example:other" }, "the policy is not asked about the mediator's own DIDs");
        (await fx.RegisteredAsync(bob.Did)).Should().Equal("did:example:other");
    }

    [Fact]
    public async Task Without_a_grant_every_entry_is_client_error_and_only_the_grant_is_read()
    {
        await using var fx = HandlerFixture.Create(fakeStore: true, fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var stranger = await fx.NewPeerAsync();

        var updated = await fx.Update20Async(stranger.Did, mediator.Did, Add("did:example:a"), Remove("did:example:b"), Add("did:example:url#x"));

        updated.Should().Equal(Added("did:example:a", ClientError), Removed("did:example:b", ClientError), Added("did:example:url#x", ClientError));
        fx.FakeStore!.Calls.Should().Equal(nameof(IMediationStore.GetGrantAsync));
        fx.Policy!.TotalCalls.Should().Be(0);
        fx.Memory.RecipientCount.Should().Be(0);
    }

    public static TheoryData<string> IdentityFaults => new() { "getter-throws", "enumerator-throws", "enumeration-throws-part-way" };

    [Theory]
    [MemberData(nameof(IdentityFaults))]
    public async Task When_the_identity_provider_fails_every_entry_is_server_error_and_nothing_is_applied(string fault)
    {
        // L-054: the routing DIDs are an input of the own-DID refusal, so they fail with it. A
        // list with a hole in it would let the mediator's current routing DID through.
        await using var fx = HandlerFixture.Create(fakeStore: true, fakePolicy: true, fakeIdentity: true);
        fx.Identity!.Get = () => new[] { "did:example:routing-a" };
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);
        await fx.Update20Async(bob.Did, mediator.Did, Add("did:example:bobs"));

        fx.Identity.Get = () => new[] { "did:example:routing-b" };
        (await fx.Update20Async(bob.Did, mediator.Did, Add("did:example:routing-b"))).Should().Equal(Added("did:example:routing-b", ClientError));

        fx.Identity.Get = fault switch
        {
            "getter-throws" => () => throw new HandlerBoomException(HandlerBoomException.Marker),
            "enumerator-throws" => () => new HandlerFaultyDids(yieldFirst: 0),
            _ => () => new HandlerFaultyDids(yieldFirst: 1, "did:example:routing-zzz"),
        };
        fx.FakeStore!.ResetCalls();
        var policyCalls = fx.Policy!.TotalCalls;

        var updated = await fx.Update20Async(bob.Did, mediator.Did,
            Add("did:example:routing-b"),
            Add("did:example:innocent"),
            Remove("did:example:bobs"),
            Add("did:example:url#frag"));

        updated.Should().Equal(
            Added("did:example:routing-b", ServerError),
            Added("did:example:innocent", ServerError),
            Removed("did:example:bobs", ServerError),
            Added("did:example:url#frag", ServerError));
        fx.FakeStore.MutatingCalls.Should().Be(0, "nothing of the message reaches the store");
        fx.Policy.TotalCalls.Should().Be(policyCalls);
        (await fx.Memory.FindGrantByRecipientDidAsync("did:example:routing-b", default)).Should().BeNull();
        (await fx.RegisteredAsync(bob.Did)).Should().Equal("did:example:bobs");

        var line = fx.Logs.Handler20.Should().ContainSingle(e => e.Level == LogLevel.Error).Which;
        line.Exception.Should().BeNull();
        line.Everything.Should().NotContain(HandlerBoomException.Marker).And.NotContain("did:");
    }

    [Fact]
    public async Task A_policy_fault_answers_server_error_for_that_entry_only()
    {
        await using var fx = HandlerFixture.Create(fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);
        fx.Policy!.OnRegister = (_, did, _, _) => did == "did:example:policy-throws"
            ? throw new HandlerBoomException(HandlerBoomException.Marker + did)
            : new ValueTask<bool>(true);

        (await fx.Update20Async(bob.Did, mediator.Did, Add("did:example:a"), Add("did:example:policy-throws"), Add("did:example:b")))
            .Should().Equal(Added("did:example:a", Success), Added("did:example:policy-throws", ServerError), Added("did:example:b", Success));

        (await fx.RegisteredAsync(bob.Did)).Should().Equal("did:example:a", "did:example:b");
        var line = fx.Logs.Handler20.Should().ContainSingle(e => e.Level == LogLevel.Error).Which;
        line.Formatted.Should().Contain(nameof(HandlerBoomException));
        line.Everything.Should().NotContain(HandlerBoomException.Marker).And.NotContain("did:");
    }

    public static TheoryData<string> UpdateStoreFaults => new() { "get-throws", "update-throws", "wrong-length" };

    [Theory]
    [MemberData(nameof(UpdateStoreFaults))]
    public async Task A_store_fault_answers_server_error_for_the_affected_entries(string fault)
    {
        await using var fx = HandlerFixture.Create(fakeStore: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);
        switch (fault)
        {
            case "get-throws":
                fx.FakeStore!.OnGetGrant = (_, _) => throw new HandlerBoomException(HandlerBoomException.Marker);
                break;
            case "update-throws":
                fx.FakeStore!.OnUpdateRecipients = (_, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker);
                break;
            default:
                fx.FakeStore!.OnUpdateRecipients = (_, _, _) => new ValueTask<IReadOnlyList<RecipientStoreOutcome>>(new[] { RecipientStoreOutcome.Added });
                break;
        }

        // The DID URL entry never reaches the store, so with the grant readable it keeps its own
        // answer; when the grant itself cannot be read, everything is server_error.
        var updated = await fx.Update20Async(bob.Did, mediator.Did, Add("did:example:a"), Add("did:example:url#x"), Remove("did:example:b"));

        var local = fault == "get-throws" ? ServerError : ClientError;
        updated.Should().Equal(Added("did:example:a", ServerError), Added("did:example:url#x", local), Removed("did:example:b", ServerError));
        fx.Memory.RecipientCount.Should().Be(0);
        var line = fx.Logs.Handler20.Should().ContainSingle(e => e.Level == LogLevel.Error).Which;
        line.Exception.Should().BeNull();
        line.Everything.Should().NotContain(HandlerBoomException.Marker).And.NotContain("did:");

        fx.FakeStore.OnGetGrant = null;
        fx.FakeStore.OnUpdateRecipients = null;
        (await fx.Update20Async(bob.Did, mediator.Did, Add("did:example:a"))).Should().Equal(Added("did:example:a", Success));
    }

    [Fact]
    public async Task An_updates_list_over_MaxUpdatesPerMessage_is_malformed_from_its_length_and_the_store_is_never_called()
    {
        // L-055: bound 1, two entries, the second poisoned. The refusal is the count: the poisoned
        // entry is never read, whatever it holds.
        await using var fx = HandlerFixture.Create(o => o.MaxUpdatesPerMessage = 1, fakeStore: true, fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);
        fx.FakeStore!.ResetCalls();
        var good = new JsonObject { ["recipient_did"] = "did:example:good", ["action"] = "add" };
        var poisons = new JsonNode?[]
        {
            new JsonObject { ["recipient_did"] = "did:example:SENTINEL", ["action"] = "SENTINEL-upsert" },
            new JsonObject { ["recipient_did"] = "did:example:SENTINEL" + new string('a', 2049), ["action"] = "add" },
            new JsonObject { ["recipient_did"] = "", ["action"] = "add" },
            new JsonObject { ["recipient_did"] = 7, ["action"] = "add" },
            JsonValue.Create("SENTINEL"),
            new JsonObject { ["recipient_did"] = "did:example:SENTINEL-well-formed", ["action"] = "add" },
        };

        foreach (var poison in poisons)
        {
            var body = new JsonObject { ["updates"] = new JsonArray(good.DeepClone(), poison) };
            var message = Raw(CoordinateMediation20.KeylistUpdateType, bob.Did, mediator.Did, body);

            var ex = (await FluentActions.Awaiting(() => fx.SendAsync(message, mediator.Did)).Should().ThrowAsync<MalformedMessageException>()).Which;

            ex.Message.Should().Be("keylist-update 'updates' has more than 1 entries.");
        }

        fx.FakeStore.Calls.Should().BeEmpty("the count is checked before the store is touched");
        fx.Policy!.Registrations.Should().BeEmpty();
        fx.Memory.RecipientCount.Should().Be(0);

        // Exactly the bound is accepted.
        (await fx.Update20Async(bob.Did, mediator.Did, Add("did:example:good"))).Should().Equal(Added("did:example:good", Success));
    }

    // ---- keylist-query (FR-MED-06) --------------------------------------------------------

    private static async Task<(HandlerFixture Fx, string Mediator, string Bob)> WithRegisteredAsync(int count, Action<CoordinateMediationOptions>? configure = null, bool fakeStore = false)
    {
        var fx = HandlerFixture.Create(configure, fakeStore: fakeStore);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);
        if (count > 0)
            await fx.Update20Async(bob.Did, mediator.Did, Enumerable.Range(0, count).Select(i => Add($"did:example:r{i:D3}")).ToArray());
        return (fx, mediator.Did, bob.Did);
    }

    [Fact]
    public async Task A_keylist_lists_keys_never_dids_as_recipient_did_objects_with_pagination()
    {
        var (fx, mediator, bob) = await WithRegisteredAsync(3);
        await using var _ = fx;

        var outcome = await fx.SendAsync(CoordinateMediation20.CreateKeylistQuery(bob, mediator), mediator);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        outcome.Reply!.Type.Should().Be(CoordinateMediation20.KeylistType);
        outcome.Reply.Body!.ToJsonString().Should().Be(
            """{"keys":[{"recipient_did":"did:example:r000"},{"recipient_did":"did:example:r001"},{"recipient_did":"did:example:r002"}],"pagination":{"count":3,"offset":0,"remaining":0}}""");
        outcome.Reply.Body.ContainsKey("dids").Should().BeFalse("dids is the 3.0 name; 2.0 lists keys");
    }

    [Theory]
    [InlineData(null, null, 5, 0, 7)]                   // no paginate → the first page at the maximum size
    [InlineData(0, 0, 0, 0, 12)]                        // limit 0 → an empty page; everything remains
    [InlineData(-3, 2, 0, 2, 10)]                       // negative limit → an empty page
    [InlineData(5, 0, 5, 0, 7)]                         // exactly the maximum
    [InlineData(6, 0, 5, 0, 7)]                         // above the maximum → clamped
    [InlineData(int.MaxValue, 2, 5, 2, 5)]
    [InlineData(3, 10, 2, 10, 0)]                       // the last, short page
    [InlineData(3, 13, 0, 13, 0)]                       // beyond the end → remaining 0, not negative
    public async Task Keylist_pages_are_clamped_to_MaxPageSize_and_always_carry_pagination(int? limit, int? offset, int count, int expectedOffset, int remaining)
    {
        var (fx, mediator, bob) = await WithRegisteredAsync(12, o => o.MaxPageSize = 5, fakeStore: true);
        await using var _ = fx;
        fx.FakeStore!.ResetCalls();
        var paginate = limit is null ? null : new RecipientQueryPaging(limit.Value, offset!.Value);

        var outcome = await fx.SendAsync(CoordinateMediation20.CreateKeylistQuery(bob, mediator, paginate), mediator);

        CoordinateMediation20.TryParseKeylist(outcome.Reply!, out var keys, out var pagination).Should().BeTrue();
        keys.Should().Equal(Enumerable.Range(expectedOffset, count).Select(i => $"did:example:r{i:D3}"));
        pagination.Should().Be(new RecipientPagination(count, expectedOffset, remaining), "count = returned, offset = applied, remaining = left after the page");
        fx.FakeStore.ListCalls.Should().ContainSingle().Which.Limit.Should().BeLessThanOrEqualTo(5, "the store is asked for the clamped page only");
    }

    public static TheoryData<string> QueryStoreFaults => new() { "throws", "too-many", "null-page" };

    [Theory]
    [MemberData(nameof(QueryStoreFaults))]
    public async Task A_store_fault_on_a_keylist_query_sends_nothing_and_never_escapes_dispatch(string fault)
    {
        var (fx, mediator, bob) = await WithRegisteredAsync(3, o => o.MaxPageSize = 2, fakeStore: true);
        await using var _ = fx;
        fx.FakeStore!.OnListRecipients = fault switch
        {
            "throws" => (_, _, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker),
            "too-many" => (_, _, _, _) => new ValueTask<RecipientPage>(new RecipientPage(new[] { "did:example:1", "did:example:2", "did:example:3" }, 3)),
            _ => (_, _, _, _) => new ValueTask<RecipientPage>((RecipientPage)null!),
        };

        var outcome = await fx.SendAsync(CoordinateMediation20.CreateKeylistQuery(bob, mediator), mediator);

        outcome.Result.Should().Be(DispatchResult.NoReply, "the protocol has no error shape for a query");
        var line = fx.Logs.Handler20.Should().ContainSingle(e => e.Level == LogLevel.Error).Which;
        line.Exception.Should().BeNull();
        line.Everything.Should().NotContain(HandlerBoomException.Marker).And.NotContain("did:");
    }

    // ---- who is a peer (FR-MED-05) --------------------------------------------------------

    public static TheoryData<HandlerEnvelope, string> UnauthenticatedRequests
    {
        get
        {
            var data = new TheoryData<HandlerEnvelope, string>();
            foreach (var shape in new[] { HandlerEnvelope.Plaintext, HandlerEnvelope.Anoncrypt, HandlerEnvelope.SignedOnly, HandlerEnvelope.AnoncryptOfSigned })
            {
                foreach (var kind in new[] { "mediate-request", "keylist-update", "keylist-query" })
                    data.Add(shape, kind);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(UnauthenticatedRequests))]
    public async Task A_2_0_request_without_an_authcrypt_layer_is_not_applied_and_not_answered(HandlerEnvelope shape, string kind)
    {
        await using var fx = HandlerFixture.Create(fakeStore: true, fakePolicy: true, fakeIdentity: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        // Bob is enrolled and holds a DID, so an update or query that got through would show.
        await fx.Enrol20Async(bob.Did, mediator.Did);
        await fx.Update20Async(bob.Did, mediator.Did, Add("did:example:bobs"));
        fx.FakeStore!.ResetCalls();
        var policyCalls = fx.Policy!.TotalCalls;
        var identityReads = fx.Identity!.Reads;

        var message = kind switch
        {
            "mediate-request" => CoordinateMediation20.CreateMediateRequest(bob.Did, mediator.Did),
            "keylist-update" => CoordinateMediation20.CreateKeylistUpdate(bob.Did, mediator.Did, new[] { Remove("did:example:bobs"), Add("did:example:new") }),
            _ => CoordinateMediation20.CreateKeylistQuery(bob.Did, mediator.Did),
        };
        var outcome = await fx.SendAsync(message, mediator.Did, shape);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        outcome.Reply.Should().BeNull();
        outcome.Handler.Should().BeOfType<CoordinateMediation20Handler>();
        fx.FakeStore.Calls.Should().BeEmpty("the store is not even read for an unauthenticated message");
        fx.Policy.TotalCalls.Should().Be(policyCalls);
        fx.Identity.Reads.Should().Be(identityReads);
        (await fx.RegisteredAsync(bob.Did)).Should().Equal("did:example:bobs");

        var line = fx.Logs.Handler20.Should().ContainSingle("one line per drop").Which;
        line.Level.Should().Be(LogLevel.Warning);
        line.Exception.Should().BeNull();
        line.State["MessageId"].Should().Be(message.Id);
        line.State.Keys.Should().BeSubsetOf(new[] { "MessageId", "Count", "{OriginalFormat}" }, "the id is the only value from the message");
        line.Everything.Should().NotContain(bob.Did).And.NotContain(mediator.Did).And.NotContain("did:example:");
    }

    [Theory]
    [InlineData(HandlerEnvelope.Authcrypt)]
    [InlineData(HandlerEnvelope.AuthcryptOfSigned)]
    [InlineData(HandlerEnvelope.AnoncryptOfAuthcrypt)]
    public async Task Every_envelope_with_an_authcrypt_layer_is_answered(HandlerEnvelope shape)
    {
        await using var fx = HandlerFixture.Create(fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();

        var grant = await fx.SendAsync(CoordinateMediation20.CreateMediateRequest(bob.Did, mediator.Did), mediator.Did, shape);
        var update = await fx.SendAsync(CoordinateMediation20.CreateKeylistUpdate(bob.Did, mediator.Did, new[] { Add("did:example:bobs") }), mediator.Did, shape);
        var query = await fx.SendAsync(CoordinateMediation20.CreateKeylistQuery(bob.Did, mediator.Did), mediator.Did, shape);

        grant.Reply!.Type.Should().Be(CoordinateMediation20.MediateGrantType);
        CoordinateMediation20.TryParseKeylistUpdateResponse(update.Reply!, out var updated).Should().BeTrue();
        updated.Should().Equal(Added("did:example:bobs", Success));
        CoordinateMediation20.TryParseKeylist(query.Reply!, out var keys, out _).Should().BeTrue();
        keys.Should().Equal("did:example:bobs");
        fx.Policy!.MediateRequests.Should().Equal(bob.Did);
    }

    [Fact]
    public async Task The_peer_is_the_authcrypt_sender_key_not_the_plaintext_from()
    {
        // A message that names no `from` at all is still attributed to the key that authcrypted it.
        await using var fx = HandlerFixture.Create(fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var anonymous = new MessageBuilder().WithType(CoordinateMediation20.MediateRequestType).WithTo(mediator.Did).WithBody(new JsonObject()).Build();

        var outcome = await fx.SendAsync(anonymous, mediator.Did, HandlerEnvelope.Authcrypt, sender: bob.Did);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        outcome.Reply!.To.Should().Equal(bob.Did);
        fx.Policy!.MediateRequests.Should().Equal(bob.Did);
        (await fx.Memory.GetGrantAsync(bob.Did, default)).Should().NotBeNull();
    }

    // ---- replies: type, thread, addressing (FR-MED-04) ------------------------------------

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
            Raw(CoordinateMediation20.MediateRequestType, bob.Did, mediator.Did, new JsonObject(), id: "req-1", thid: thid),
            Raw(CoordinateMediation20.KeylistUpdateType, bob.Did, mediator.Did, UpdatesBody(("did:example:r1", "add")), id: "req-2", thid: thid),
            Raw(CoordinateMediation20.KeylistQueryType, bob.Did, mediator.Did, new JsonObject(), id: "req-3", thid: thid),
        };
        var replyTypes = new[] { CoordinateMediation20.MediateGrantType, CoordinateMediation20.KeylistUpdateResponseType, CoordinateMediation20.KeylistType };

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

    [Theory]
    [InlineData("https://didcomm.org/coordinate-mediation/2.1/mediate-request", "mediate-grant")]
    [InlineData("https://didcomm.org/coordinate-mediation/2.0/MEDIATE_REQUEST", "mediate-grant")]
    [InlineData("https://didcomm.org/coordinate-mediation/2.0/Mediate-Request", "mediate-grant")]
    [InlineData("https://didcomm.org/coordinate-mediation/2.0/keylist_update", "keylist-update-response")]
    [InlineData("https://didcomm.org/coordinate-mediation/2.4/KEYLIST-QUERY", "keylist")]
    public async Task A_reply_uses_the_canonical_type_whatever_spelling_or_minor_the_request_used(string type, string replyName)
    {
        // I-14: a 2.0 mediator in the field matches types by exact string, so the reply is never
        // spelled the way the request was.
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);
        var body = replyName == "keylist-update-response" ? UpdatesBody(("did:example:r", "add")) : null;

        var outcome = await fx.SendAsync(Raw(type, bob.Did, mediator.Did, body), mediator.Did);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        outcome.Handler.Should().BeOfType<CoordinateMediation20Handler>();
        outcome.Reply!.Type.Should().Be("https://didcomm.org/coordinate-mediation/2.0/" + replyName);
    }

    // ---- messages the mediator role does not answer ---------------------------------------

    [Fact]
    public async Task Answers_3_0_names_and_unknown_names_under_the_2_0_piuri_are_ignored()
    {
        await using var fx = HandlerFixture.Create(fakeStore: true, fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);
        fx.FakeStore!.ResetCalls();
        var policyCalls = fx.Policy!.TotalCalls;
        var ignored = new[]
        {
            CoordinateMediation20.CreateGrant(bob.Did, mediator.Did, "did:example:r", "t"),
            CoordinateMediation20.CreateDeny(bob.Did, mediator.Did, "t"),
            CoordinateMediation20.CreateKeylistUpdateResponse(bob.Did, mediator.Did, new[] { Added("did:example:r", Success) }, "t"),
            CoordinateMediation20.CreateKeylist(bob.Did, mediator.Did, new[] { "did:example:r" }, null, "t"),
            // The 3.0 names, sent under the 2.0 PIURI.
            Raw(Piuri20 + "/recipient-update", bob.Did, mediator.Did, UpdatesBody(("did:example:r", "add"))),
            Raw(Piuri20 + "/recipient-query", bob.Did, mediator.Did, new JsonObject()),
            Raw(Piuri20 + "/no-such-message", bob.Did, mediator.Did, null),
            // An answer whose body would be malformed for its own parser is ignored too.
            Raw(CoordinateMediation20.MediateGrantType, bob.Did, mediator.Did, new JsonObject { ["routing_did"] = 7 }),
        };

        foreach (var message in ignored)
        {
            var outcome = await fx.SendAsync(message, mediator.Did);
            outcome.Result.Should().Be(DispatchResult.NoReply, message.Type);
            outcome.Handler.Should().BeOfType<CoordinateMediation20Handler>();
        }

        fx.FakeStore.Calls.Should().BeEmpty();
        fx.Policy.TotalCalls.Should().Be(policyCalls);
        (await fx.RegisteredAsync(bob.Did)).Should().BeEmpty();
        var lines = fx.Logs.Handler20;
        lines.Should().HaveCount(ignored.Length).And.OnlyContain(e => e.Level == LogLevel.Debug);
        lines.Select(e => e.State["MessageId"]).Should().Equal(ignored.Select(m => (object?)m.Id));
    }

    [Fact]
    public void The_handler_serves_the_2_0_piuri_in_the_mediator_role()
    {
        var handler = new CoordinateMediation20Handler(new InMemoryMediationStore(), new DefaultMediationPolicy(), new HandlerFakeIdentity(), Microsoft.Extensions.Options.Options.Create(new CoordinateMediationOptions()));

        handler.ProtocolUri.Should().Be("https://didcomm.org/coordinate-mediation/2.0");
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

        FluentActions.Invoking(() => new CoordinateMediation20Handler(null!, policy, identity, options)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new CoordinateMediation20Handler(store, null!, identity, options)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new CoordinateMediation20Handler(store, policy, null!, options)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new CoordinateMediation20Handler(store, policy, identity, null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new CoordinateMediation20Handler(store, policy, identity, Microsoft.Extensions.Options.Options.Create(new CoordinateMediationOptions { MaxPageSize = 0 })))
            .Should().Throw<ArgumentException>();
    }

    // ---- cancellation ---------------------------------------------------------------------

    [Fact]
    public async Task A_cancelled_token_throws_OperationCanceledException_and_changes_nothing()
    {
        await using var fx = HandlerFixture.Create(fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();

        // Cancelled before the handler runs: whatever the message, including those that would
        // reach no store (an empty update, an ignored answer, an unauthenticated request).
        using var before = new CancellationTokenSource();
        before.Cancel();
        var cancelled = new (Message Message, HandlerEnvelope Shape)[]
        {
            (CoordinateMediation20.CreateMediateRequest(bob.Did, mediator.Did), HandlerEnvelope.Authcrypt),
            (CoordinateMediation20.CreateKeylistQuery(bob.Did, mediator.Did), HandlerEnvelope.Authcrypt),
            (CoordinateMediation20.CreateKeylistUpdate(bob.Did, mediator.Did, Array.Empty<RecipientUpdate>()), HandlerEnvelope.Authcrypt),
            (CoordinateMediation20.CreateDeny(bob.Did, mediator.Did, "t"), HandlerEnvelope.Authcrypt),
            (CoordinateMediation20.CreateMediateRequest(bob.Did, mediator.Did), HandlerEnvelope.Anoncrypt),
        };
        foreach (var (message, shape) in cancelled)
        {
            await FluentActions.Awaiting(() => fx.SendAsync(message, mediator.Did, shape, ct: before.Token))
                .Should().ThrowAsync<OperationCanceledException>(message.Type + " " + shape);
        }
        fx.Policy!.TotalCalls.Should().Be(0);
        fx.Logs.Handler20.Should().BeEmpty("a request cancelled before the handler runs is not looked at");

        // Cancelled while the policy is deciding.
        using var during = new CancellationTokenSource();
        fx.Policy.OnRequest = (_, _, ct) =>
        {
            during.Cancel();
            ct.ThrowIfCancellationRequested();
            return new ValueTask<MediationDecision>(MediationDecision.Grant);
        };
        await FluentActions.Awaiting(() => fx.SendAsync(CoordinateMediation20.CreateMediateRequest(bob.Did, mediator.Did), mediator.Did, ct: during.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        fx.Memory.GrantCount.Should().Be(0);
        fx.Logs.Handler20.Should().NotContain(e => e.Level >= LogLevel.Warning, "a cancelled request is not a store or policy fault");
    }

    [Theory]
    [InlineData("mediate-request")]
    [InlineData("keylist-update")]
    [InlineData("keylist-query")]
    public async Task A_store_fault_while_the_token_is_cancelled_surfaces_a_plain_OperationCanceledException(string kind)
    {
        // A driver's "cancelled" error carries its own text; what leaves dispatch is cancellation,
        // never that exception.
        await using var fx = HandlerFixture.Create(fakeStore: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);
        using var cts = new CancellationTokenSource();
        HandlerBoomException Boom()
        {
            cts.Cancel();
            return new HandlerBoomException(HandlerBoomException.Marker + bob.Did);
        }
        Message message;
        switch (kind)
        {
            case "mediate-request":
                fx.FakeStore!.OnGetGrant = (_, _) => throw Boom();
                message = CoordinateMediation20.CreateMediateRequest(bob.Did, mediator.Did);
                break;
            case "keylist-update":
                fx.FakeStore!.OnUpdateRecipients = (_, _, _) => throw Boom();
                message = CoordinateMediation20.CreateKeylistUpdate(bob.Did, mediator.Did, new[] { Add("did:example:a") });
                break;
            default:
                fx.FakeStore!.OnListRecipients = (_, _, _, _) => throw Boom();
                message = CoordinateMediation20.CreateKeylistQuery(bob.Did, mediator.Did);
                break;
        }

        var ex = (await FluentActions.Awaiting(() => fx.SendAsync(message, mediator.Did, ct: cts.Token)).Should().ThrowAsync<OperationCanceledException>()).Which;

        ex.GetType().Should().Be(typeof(OperationCanceledException));
        ex.Message.Should().NotContain(HandlerBoomException.Marker).And.NotContain("did:");
        ex.InnerException.Should().BeNull();
        fx.Logs.Handler20.Should().NotContain(e => e.Level >= LogLevel.Warning);
        fx.Memory.RecipientCount.Should().Be(0);
    }

    // ---- logs -----------------------------------------------------------------------------

    [Fact]
    public async Task No_2_0_log_line_carries_a_did_or_a_value_planted_in_the_body_and_every_line_is_under_the_2_0_category()
    {
        const string planted = "PLANTED-MARKER";
        await using var fx = HandlerFixture.Create(o => o.MaxGrants = 2, fakeStore: true, fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var carol = await fx.NewPeerAsync();
        var dave = await fx.NewPeerAsync();
        var longId = new string('i', 300);
        var plantedBody = new JsonObject { [planted] = planted };

        // One of everything that logs: unauthenticated drops, a capacity denial, ignored messages,
        // policy and store faults, and a reply that cannot be built; ordinary traffic in between.
        await fx.SendAsync(Raw(CoordinateMediation20.MediateRequestType, bob.Did, mediator.Did, plantedBody.DeepClone().AsObject(), id: longId), mediator.Did, HandlerEnvelope.Anoncrypt);
        await fx.SendAsync(CoordinateMediation20.CreateMediateRequest(bob.Did, mediator.Did), mediator.Did, HandlerEnvelope.SignedOnly);
        await fx.SendAsync(Raw(CoordinateMediation20.MediateRequestType, bob.Did, mediator.Did, plantedBody.DeepClone().AsObject()), mediator.Did);
        await fx.Enrol20Async(carol.Did, mediator.Did);
        await fx.SendAsync(CoordinateMediation20.CreateMediateRequest(dave.Did, mediator.Did), mediator.Did);              // full → deny
        await fx.SendAsync(CoordinateMediation20.CreateDeny(bob.Did, mediator.Did, "t"), mediator.Did);                    // ignored
        await fx.SendAsync(Raw(Piuri20 + "/no-such-message", bob.Did, mediator.Did, plantedBody.DeepClone().AsObject()), mediator.Did); // ignored
        fx.Policy!.OnRegister = (_, did, _, _) => did.Contains("boom", StringComparison.Ordinal)
            ? throw new HandlerBoomException(HandlerBoomException.Marker + did)
            : new ValueTask<bool>(true);
        var updates = UpdatesBody(("did:example:ok", "add"), ("did:example:boom-" + planted, "add"), ("did:example:url#" + planted, "add"));
        updates[planted] = planted;
        await fx.SendAsync(Raw(CoordinateMediation20.KeylistUpdateType, bob.Did, mediator.Did, updates), mediator.Did);
        fx.FakeStore!.OnUpdateRecipients = (_, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker + bob.Did);
        await fx.Update20Async(bob.Did, mediator.Did, Add("did:example:second-" + planted));
        fx.FakeStore.OnListRecipients = (_, _, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker + bob.Did);
        var query = new JsonObject { ["paginate"] = new JsonObject { ["limit"] = 1, ["offset"] = 0 }, [planted] = planted };
        await fx.SendAsync(Raw(CoordinateMediation20.KeylistQueryType, bob.Did, mediator.Did, query, id: longId), mediator.Did);
        fx.FakeStore.OnGetGrant = (peer, _) => new ValueTask<MediationGrant?>(new MediationGrant(peer, Array.Empty<string>()));
        await fx.SendAsync(Raw(CoordinateMediation20.MediateRequestType, bob.Did, mediator.Did, plantedBody.DeepClone().AsObject()), mediator.Did);

        var lines = fx.Logs.Handler20;
        lines.Should().HaveCountGreaterThanOrEqualTo(9);
        lines.Should().Contain(e => e.Level == LogLevel.Error).And.Contain(e => e.Level == LogLevel.Warning).And.Contain(e => e.Level == LogLevel.Debug);
        foreach (var line in lines)
        {
            line.Exception.Should().BeNull();
            line.Everything.Should().NotContain("did:", "no DID, kid or recipient_did reaches a log line");
            line.Everything.Should().NotContain(planted, "no value from a message body reaches a log line");
            line.Everything.Should().NotContain(HandlerBoomException.Marker, "exception text is never logged, only the type name");
            line.Everything.Should().NotContain(longId, "an over-long message id is replaced");
        }
        lines.Should().Contain(e => e.Formatted.Contains("(id of 300 characters)"));
        lines.Should().Contain(e => e.Formatted.Contains(nameof(HandlerBoomException)));
        lines.Should().Contain(e => e.Formatted.Contains(nameof(ArgumentException)));
        fx.Logs.Handler.Should().BeEmpty("2.0 traffic logs under the 2.0 handler's category, not the 3.0 one");
    }

    [Fact]
    public async Task A_throwing_logging_provider_changes_no_outcome()
    {
        await using var fx = HandlerFixture.Create(o => o.MaxGrants = 1, fakeStore: true, throwingLogger: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var carol = await fx.NewPeerAsync();

        (await fx.SendAsync(CoordinateMediation20.CreateMediateRequest(bob.Did, mediator.Did), mediator.Did, HandlerEnvelope.Anoncrypt)).Result.Should().Be(DispatchResult.NoReply);
        (await fx.Enrol20Async(bob.Did, mediator.Did)).Should().Be(mediator.Did);
        (await fx.SendAsync(CoordinateMediation20.CreateMediateRequest(carol.Did, mediator.Did), mediator.Did)).Reply!.Type.Should().Be(CoordinateMediation20.MediateDenyType);
        (await fx.SendAsync(CoordinateMediation20.CreateDeny(bob.Did, mediator.Did, "t"), mediator.Did)).Result.Should().Be(DispatchResult.NoReply);
        fx.FakeStore!.OnUpdateRecipients = (_, _, _) => throw new HandlerBoomException("x");
        (await fx.Update20Async(bob.Did, mediator.Did, Add("did:example:a")))
            .Should().Equal(Added("did:example:a", ServerError));
        fx.FakeStore.OnListRecipients = (_, _, _, _) => throw new HandlerBoomException("x");
        (await fx.SendAsync(CoordinateMediation20.CreateKeylistQuery(bob.Did, mediator.Did), mediator.Did)).Result.Should().Be(DispatchResult.NoReply);
        fx.FakeStore.OnGetGrant = (peer, _) => new ValueTask<MediationGrant?>(new MediationGrant(peer, Array.Empty<string>()));
        (await fx.SendAsync(CoordinateMediation20.CreateMediateRequest(bob.Did, mediator.Did), mediator.Did)).Result.Should().Be(DispatchResult.NoReply);
    }
}
