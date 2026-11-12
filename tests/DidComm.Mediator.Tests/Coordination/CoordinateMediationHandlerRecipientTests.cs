using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Json;
using DidComm.Mediator.Coordination;
using DidComm.Protocols;
using Microsoft.Extensions.Logging;
using Xunit;
using static DidComm.Mediator.Tests.Coordination.HandlerMessages;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>
/// <c>recipient-update</c> and <c>recipient-query</c> through the real dispatcher (FR-MED-05, -06,
/// -08): every <c>result</c> value, input order, the ownership rule against a second peer, bounds,
/// faults contained, and the pagination edges.
/// </summary>
public sealed class CoordinateMediationHandlerRecipientTests
{
    private const string P = CoordinateMediation.ProtocolUri;
    private const RecipientUpdateOutcome Success = RecipientUpdateOutcome.Success;
    private const RecipientUpdateOutcome NoChange = RecipientUpdateOutcome.NoChange;
    private const RecipientUpdateOutcome ClientError = RecipientUpdateOutcome.ClientError;
    private const RecipientUpdateOutcome ServerError = RecipientUpdateOutcome.ServerError;

    // ---- recipient-update -----------------------------------------------------------------

    [Fact]
    public async Task One_message_can_produce_every_result_value_in_input_order()
    {
        await using var fx = HandlerFixture.Create(o => o.MaxRecipientsPerGrant = 2, fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var mallory = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);
        await fx.EnrolAsync(mallory.Did, mediator.Did);
        await fx.UpdateAsync(mallory.Did, mediator.Did, Add("did:example:mallorys"));
        await fx.UpdateAsync(bob.Did, mediator.Did, Add("did:example:already"));
        fx.Policy!.OnRegister = (_, did, _, _) => did switch
        {
            "did:example:policy-throws" => throw new HandlerBoomException(HandlerBoomException.Marker),
            "did:example:policy-refuses" => new ValueTask<bool>(false),
            _ => new ValueTask<bool>(true),
        };

        var request = CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, new[]
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
        outcome.Reply!.Type.Should().Be(CoordinateMediation.RecipientUpdateResponseType);
        CoordinateMediation.TryParseRecipientUpdateResponse(outcome.Reply, out var updated).Should().BeTrue();
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

        // On the wire: the four values the protocol defines, spelled as it spells them.
        var wire = JsonSerializer.Serialize(outcome.Reply, DidCommJson.Default);
        wire.Should().Contain("\"result\":\"success\"").And.Contain("\"result\":\"no_change\"")
            .And.Contain("\"result\":\"client_error\"").And.Contain("\"result\":\"server_error\"");
    }

    [Fact]
    public async Task Mallory_can_neither_add_nor_remove_a_did_bob_registered()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var mallory = await fx.NewPeerAsync();
        var bobsDid = (await fx.NewPeerAsync()).Did;
        await fx.EnrolAsync(bob.Did, mediator.Did);
        await fx.EnrolAsync(mallory.Did, mediator.Did);
        (await fx.UpdateAsync(bob.Did, mediator.Did, Add(bobsDid), Add(bob.Did))).Should().Equal(Added(bobsDid, Success), Added(bob.Did, Success));

        (await fx.UpdateAsync(mallory.Did, mediator.Did, Add(bobsDid))).Should().Equal(Added(bobsDid, ClientError));
        (await fx.UpdateAsync(mallory.Did, mediator.Did, Remove(bobsDid))).Should().Equal(Removed(bobsDid, ClientError));
        (await fx.UpdateAsync(mallory.Did, mediator.Did, Remove(bob.Did), Add(bob.Did))).Should().Equal(Removed(bob.Did, ClientError), Added(bob.Did, ClientError));

        (await fx.RegisteredAsync(bob.Did)).Should().Equal(bobsDid, bob.Did);
        (await fx.RegisteredAsync(mallory.Did)).Should().BeEmpty();
        (await fx.Memory.FindGrantByRecipientDidAsync(bobsDid, default))!.PeerDid.Should().Be(bob.Did);

        // Bob re-adding his own DID changes nothing; Mallory's own DID is hers to register.
        (await fx.UpdateAsync(bob.Did, mediator.Did, Add(bobsDid))).Should().Equal(Added(bobsDid, NoChange));
        (await fx.UpdateAsync(mallory.Did, mediator.Did, Add(mallory.Did))).Should().Equal(Added(mallory.Did, Success));
    }

    [Fact]
    public async Task Squatting_is_first_come_first_served_and_a_policy_can_close_it()
    {
        // The protocol asks for no proof of ownership (its endnote): Mallory registering Bob's DID
        // first wins by default. A policy that demands the DID be the peer's own closes it.
        await using var open = HandlerFixture.Create();
        var mediator = await open.NewPeerAsync();
        var bob = await open.NewPeerAsync();
        var mallory = await open.NewPeerAsync();
        await open.EnrolAsync(bob.Did, mediator.Did);
        await open.EnrolAsync(mallory.Did, mediator.Did);
        (await open.UpdateAsync(mallory.Did, mediator.Did, Add(bob.Did))).Should().Equal(Added(bob.Did, Success));
        (await open.UpdateAsync(bob.Did, mediator.Did, Add(bob.Did))).Should().Equal(Added(bob.Did, ClientError));

        await using var strict = HandlerFixture.Create(fakePolicy: true);
        strict.Policy!.OnRegister = (peer, did, _, _) => new ValueTask<bool>(peer == did);
        var mediator2 = await strict.NewPeerAsync();
        var bob2 = await strict.NewPeerAsync();
        var mallory2 = await strict.NewPeerAsync();
        await strict.EnrolAsync(bob2.Did, mediator2.Did);
        await strict.EnrolAsync(mallory2.Did, mediator2.Did);
        (await strict.UpdateAsync(mallory2.Did, mediator2.Did, Add(bob2.Did))).Should().Equal(Added(bob2.Did, ClientError));
        (await strict.UpdateAsync(bob2.Did, mediator2.Did, Add(bob2.Did))).Should().Equal(Added(bob2.Did, Success));
    }

    [Theory]
    [InlineData("did:example:alice#key-1")]
    [InlineData("did:example:alice?service=didcomm")]
    [InlineData("did:example:alice/path")]
    [InlineData("did:example:alice;param=1")]
    [InlineData("not a did")]
    [InlineData("https://example.com/")]
    [InlineData(" did:example:alice")]
    [InlineData("did:example:alice ")]
    [InlineData("DID:example:alice")]
    public async Task A_recipient_did_that_is_not_a_bare_did_is_client_error_echoed_and_never_normalized(string value)
    {
        await using var fx = HandlerFixture.Create(fakeStore: true, fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);
        fx.FakeStore!.ResetCalls();

        var updated = await fx.UpdateAsync(bob.Did, mediator.Did, Add(value), Remove(value));

        updated.Should().Equal(Added(value, ClientError), Removed(value, ClientError));
        fx.FakeStore.UpdateCalls.Should().BeEmpty("nothing that is not a bare DID reaches the store");
        fx.Policy!.Registrations.Should().BeEmpty();
        fx.Memory.RecipientCount.Should().Be(0);
        (await fx.Memory.FindGrantByRecipientDidAsync("did:example:alice", default)).Should().BeNull("a DID URL is not reduced to its DID and registered");
    }

    [Fact]
    public async Task The_mediators_own_dids_cannot_be_registered_as_recipients()
    {
        await using var fx = HandlerFixture.Create(
            o => o.RoutingDids.Add("did:example:routing"),
            fakePolicy: true,
            didComm: o => o.OwnIdentifiers = new[] { "did:example:own-1#key-1", "did:example:own-2" });
        var mediator = await fx.NewPeerAsync();
        var secondMediatorDid = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);

        var updated = await fx.UpdateAsync(bob.Did, mediator.Did,
            Add(mediator.Did),            // the DID that decrypted this request
            Add("did:example:routing"),   // a configured routing DID (also in Bob's grant)
            Add("did:example:own-1"),     // OwnIdentifiers, listed as a DID URL
            Add("did:example:own-2"),
            Add("did:example:other"));

        updated.Should().Equal(
            Added(mediator.Did, ClientError),
            Added("did:example:routing", ClientError),
            Added("did:example:own-1", ClientError),
            Added("did:example:own-2", ClientError),
            Added("did:example:other", Success));
        fx.Policy!.Registrations.Select(r => r.RecipientDid).Should().Equal(new[] { "did:example:other" }, "the policy is not asked about the mediator's own DIDs");

        // A mediator DID the host did not declare is only known as "own" when it is the one addressed.
        (await fx.UpdateAsync(bob.Did, secondMediatorDid.Did, Add(secondMediatorDid.Did))).Should().Equal(Added(secondMediatorDid.Did, ClientError));
        (await fx.RegisteredAsync(bob.Did)).Should().Equal("did:example:other");
    }

    [Fact]
    public async Task The_policy_is_asked_once_per_add_and_never_for_removals_or_peers_without_a_grant()
    {
        await using var fx = HandlerFixture.Create(fakeStore: true, fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var stranger = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);

        await fx.UpdateAsync(bob.Did, mediator.Did, Add("did:example:a"), Remove("did:example:a"), Add("did:example:b"), Remove("did:example:zzz"));
        fx.Policy!.Registrations.Should().Equal((bob.Did, "did:example:a"), (bob.Did, "did:example:b"));
        fx.FakeStore!.UpdateCalls.Should().ContainSingle("one store call applies the whole batch").Which.Updates.Should().HaveCount(4);

        // No grant (JC-4): every entry client_error, and it costs one store read — no policy call,
        // no update call.
        fx.FakeStore.ResetCalls();
        var updated = await fx.UpdateAsync(stranger.Did, mediator.Did, Add("did:example:c"), Remove("did:example:a"), Add("did:example:url#x"));
        updated.Should().Equal(Added("did:example:c", ClientError), Removed("did:example:a", ClientError), Added("did:example:url#x", ClientError));
        fx.Policy.Registrations.Should().HaveCount(2);
        fx.FakeStore.Calls.Should().Equal(nameof(IMediationStore.GetGrantAsync));
        (await fx.RegisteredAsync(bob.Did)).Should().Equal("did:example:b");
    }

    [Fact]
    public async Task A_grant_revoked_between_the_read_and_the_write_is_client_error()
    {
        // The handler's first read sees a grant; the store then reports NoGrant (revoked meanwhile).
        await using var fx = HandlerFixture.Create(fakeStore: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);
        fx.FakeStore!.OnUpdateRecipients = async (peer, updates, ct) =>
        {
            await fx.FakeStore.Inner.RevokeAsync(peer, ct);
            return await fx.FakeStore.Inner.UpdateRecipientsAsync(peer, updates, ct);
        };

        (await fx.UpdateAsync(bob.Did, mediator.Did, Add("did:example:a"))).Should().Equal(Added("did:example:a", ClientError));
        fx.Memory.RecipientCount.Should().Be(0);
    }

    public static TheoryData<string> UpdateStoreFaults => new() { "get-throws", "update-throws", "wrong-length", "null-list", "unknown-outcome" };

    [Theory]
    [MemberData(nameof(UpdateStoreFaults))]
    public async Task A_store_fault_answers_server_error_and_never_escapes_dispatch(string fault)
    {
        await using var fx = HandlerFixture.Create(fakeStore: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);
        switch (fault)
        {
            case "get-throws":
                fx.FakeStore!.OnGetGrant = (_, _) => throw new HandlerBoomException(HandlerBoomException.Marker);
                break;
            case "update-throws":
                fx.FakeStore!.OnUpdateRecipients = (_, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker);
                break;
            case "wrong-length":
                fx.FakeStore!.OnUpdateRecipients = (_, _, _) => new ValueTask<IReadOnlyList<RecipientStoreOutcome>>(new[] { RecipientStoreOutcome.Added });
                break;
            case "null-list":
                fx.FakeStore!.OnUpdateRecipients = (_, _, _) => new ValueTask<IReadOnlyList<RecipientStoreOutcome>>((IReadOnlyList<RecipientStoreOutcome>)null!);
                break;
            default:
                fx.FakeStore!.OnUpdateRecipients = (_, updates, _) => new ValueTask<IReadOnlyList<RecipientStoreOutcome>>(updates.Select(_ => (RecipientStoreOutcome)99).ToArray());
                break;
        }

        // The DID URL entry never reaches the store, so with the grant readable it keeps its own
        // answer; when the grant itself cannot be read, everything is server_error.
        var updated = await fx.UpdateAsync(bob.Did, mediator.Did, Add("did:example:a"), Add("did:example:url#x"), Remove("did:example:b"));

        var local = fault == "get-throws" ? ServerError : ClientError;
        updated.Should().Equal(Added("did:example:a", ServerError), Added("did:example:url#x", local), Removed("did:example:b", ServerError));
        fx.Memory.RecipientCount.Should().Be(0);
        if (fault != "unknown-outcome")
        {
            var line = fx.Logs.Handler.Should().ContainSingle(e => e.Level == LogLevel.Error).Which;
            line.Exception.Should().BeNull();
            line.Everything.Should().NotContain(HandlerBoomException.Marker).And.NotContain("did:");
        }

        // The dispatcher and the handler keep working afterwards.
        fx.FakeStore.OnGetGrant = null;
        fx.FakeStore.OnUpdateRecipients = null;
        (await fx.UpdateAsync(bob.Did, mediator.Did, Add("did:example:a"))).Should().Equal(Added("did:example:a", Success));
    }

    [Fact]
    public async Task The_total_bound_is_server_error_and_the_per_grant_bound_is_client_error()
    {
        await using var fx = HandlerFixture.Create(o =>
        {
            o.MaxRecipientsPerGrant = 2;
            o.MaxRecipients = 3;
        });
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var carol = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);
        await fx.EnrolAsync(carol.Did, mediator.Did);

        (await fx.UpdateAsync(bob.Did, mediator.Did, Add("did:example:b1"), Add("did:example:b2"), Add("did:example:b3")))
            .Should().Equal(Added("did:example:b1", Success), Added("did:example:b2", Success), Added("did:example:b3", ClientError));
        (await fx.UpdateAsync(carol.Did, mediator.Did, Add("did:example:c1"), Add("did:example:c2")))
            .Should().Equal(Added("did:example:c1", Success), Added("did:example:c2", ServerError));
        fx.Memory.RecipientCount.Should().Be(3);
    }

    [Fact]
    public async Task Ten_thousand_adds_stop_at_the_bound()
    {
        // #93 acceptance: 10k adds → client_error once MaxRecipientsPerGrant is hit; memory bounded.
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);
        var bound = new CoordinateMediationOptions().MaxRecipientsPerGrant;

        var results = new List<RecipientUpdateResult>();
        for (var batch = 0; batch < 10; batch++)
            results.AddRange(await fx.UpdateAsync(bob.Did, mediator.Did, Enumerable.Range(batch * 1000, 1000).Select(i => Add($"did:example:r{i}")).ToArray()));

        results.Should().HaveCount(10_000);
        results.Take(bound).Should().OnlyContain(r => r.Result == Success);
        results.Skip(bound).Should().OnlyContain(r => r.Result == ClientError);
        fx.Memory.RecipientCount.Should().Be(bound);
    }

    [Fact]
    public async Task More_entries_than_MaxUpdatesPerMessage_is_malformed_and_applies_nothing()
    {
        await using var fx = HandlerFixture.Create(o => o.MaxUpdatesPerMessage = 3, fakeStore: true, fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);
        fx.FakeStore!.ResetCalls();

        (await fx.UpdateAsync(bob.Did, mediator.Did, Add("did:example:1"), Add("did:example:2"), Add("did:example:3")))
            .Should().OnlyContain(r => r.Result == Success, "exactly the maximum is accepted");
        fx.FakeStore.ResetCalls();
        var registrations = fx.Policy!.Registrations.Count;

        var tooMany = CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, new[] { Add("did:example:SENTINEL4"), Add("did:example:5"), Add("did:example:6"), Add("did:example:7") });
        var ex = (await FluentActions.Awaiting(() => fx.SendAsync(tooMany, mediator.Did)).Should().ThrowAsync<MalformedMessageException>()).Which;

        ex.Message.Should().Contain("3").And.NotContain("SENTINEL").And.NotContain("did:");
        fx.FakeStore.Calls.Should().BeEmpty("the count is checked before the store or the policy is touched");
        fx.Policy.Registrations.Should().HaveCount(registrations);
        (await fx.RegisteredAsync(bob.Did)).Should().HaveCount(3);
    }

    [Fact]
    public async Task An_over_long_recipient_did_is_malformed_without_echo_and_nothing_in_the_message_is_applied()
    {
        await using var fx = HandlerFixture.Create(fakeStore: true, fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);
        fx.FakeStore!.ResetCalls();
        var overLong = "did:example:SENTINEL" + new string('a', 2049);
        var message = Raw(CoordinateMediation.RecipientUpdateType, bob.Did, mediator.Did, UpdatesBody(("did:example:good", "add"), (overLong, "add")));

        var ex = (await FluentActions.Awaiting(() => fx.SendAsync(message, mediator.Did)).Should().ThrowAsync<MalformedMessageException>()).Which;

        ex.Message.Should().Contain("2048").And.NotContain("SENTINEL");
        fx.FakeStore.Calls.Should().BeEmpty();
        fx.Policy!.Registrations.Should().BeEmpty();
        fx.Memory.RecipientCount.Should().Be(0, "the well-formed entry before it is not applied either");
    }

    public static TheoryData<string, JsonObject?> MalformedAuthenticated => new()
    {
        { "recipient-update", null },
        { "recipient-update", new JsonObject { ["updates"] = "did:example:SENTINEL" } },
        { "recipient-update", new JsonObject { ["updates"] = new JsonArray(new JsonObject { ["recipient_did"] = "did:example:SENTINEL", ["action"] = "upsert-SENTINEL" }) } },
        { "recipient-update", new JsonObject { ["updates"] = new JsonArray(new JsonObject { ["recipient_did"] = 7, ["action"] = "add" }) } },
        { "recipient-query", new JsonObject { ["paginate"] = new JsonObject { ["limit"] = "10", ["offset"] = 0 } } },
        { "recipient-query", new JsonObject { ["paginate"] = new JsonObject { ["limit"] = 10 } } },
        { "recipient-query", new JsonObject { ["paginate"] = new JsonObject { ["limit"] = 10, ["offset"] = -1 } } },
        { "recipient-query", new JsonObject { ["paginate"] = "SENTINEL" } },
    };

    [Theory]
    [MemberData(nameof(MalformedAuthenticated))]
    public async Task A_malformed_body_from_an_enrolled_peer_is_a_MalformedMessageException_that_quotes_nothing(string name, JsonObject? body)
    {
        await using var fx = HandlerFixture.Create(fakeStore: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);
        fx.FakeStore!.ResetCalls();

        var message = Raw(P + "/" + name, bob.Did, mediator.Did, body?.DeepClone().AsObject());
        var ex = (await FluentActions.Awaiting(() => fx.SendAsync(message, mediator.Did)).Should().ThrowAsync<MalformedMessageException>()).Which;

        ex.Message.Should().NotContain("SENTINEL").And.NotContain(bob.Did);
        ex.InnerException.Should().BeNull();
        fx.FakeStore.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_update_list_gets_an_empty_response()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);

        var outcome = await fx.SendAsync(CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, Array.Empty<RecipientUpdate>()), mediator.Did);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        JsonSerializer.Serialize(outcome.Reply, DidCommJson.Default).Should().Contain("\"body\":{\"updated\":[]}");
    }

    [Fact]
    public async Task A_registered_did_at_the_length_limit_round_trips()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);
        var atLimit = "did:example:" + new string('a', 2048 - "did:example:".Length);

        (await fx.UpdateAsync(bob.Did, mediator.Did, Add(atLimit))).Should().Equal(Added(atLimit, Success));
        (await fx.QueryAsync(bob.Did, mediator.Did)).Dids.Should().Equal(atLimit);
    }

    // ---- recipient-query (FR-MED-06) ------------------------------------------------------

    private static async Task<(HandlerFixture Fx, string Mediator, string Bob)> WithRegisteredAsync(int count, Action<CoordinateMediationOptions>? configure = null, bool fakeStore = false)
    {
        var fx = HandlerFixture.Create(configure, fakeStore: fakeStore);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);
        if (count > 0)
            await fx.UpdateAsync(bob.Did, mediator.Did, Enumerable.Range(0, count).Select(i => Add($"did:example:r{i:D3}")).ToArray());
        return (fx, mediator.Did, bob.Did);
    }

    [Fact]
    public async Task Without_paginate_the_first_page_at_the_maximum_size_is_returned_with_pagination()
    {
        var (fx, mediator, bob) = await WithRegisteredAsync(12, o => o.MaxPageSize = 5);
        await using var _ = fx;

        var outcome = await fx.SendAsync(CoordinateMediation.CreateRecipientQuery(bob, mediator), mediator);

        CoordinateMediation.TryParseRecipient(outcome.Reply!, out var dids, out var pagination).Should().BeTrue();
        dids.Should().Equal(Enumerable.Range(0, 5).Select(i => $"did:example:r{i:D3}"));
        pagination.Should().Be(new RecipientPagination(Count: 5, Offset: 0, Remaining: 7));
        JsonSerializer.Serialize(outcome.Reply, DidCommJson.Default).Should().Contain("\"pagination\":{\"count\":5,\"offset\":0,\"remaining\":7}");

        // An absent body is the same query.
        var bodiless = await fx.SendAsync(Raw(CoordinateMediation.RecipientQueryType, bob, mediator, null), mediator);
        CoordinateMediation.TryParseRecipient(bodiless.Reply!, out dids, out pagination).Should().BeTrue();
        dids.Should().HaveCount(5);
        pagination.Should().Be(new RecipientPagination(5, 0, 7));
    }

    [Theory]
    [InlineData(0, 0, 0, 12)]                      // limit 0 → empty page; everything remains
    [InlineData(-1, 0, 0, 12)]                     // negative limit → empty page
    [InlineData(int.MinValue, 3, 0, 9)]
    [InlineData(0, 12, 0, 0)]
    [InlineData(3, 0, 3, 9)]
    [InlineData(3, 10, 2, 0)]                      // the last, short page
    [InlineData(3, 12, 0, 0)]                      // offset at the end
    [InlineData(3, 13, 0, 0)]                      // offset beyond the end → remaining 0, not negative
    [InlineData(3, int.MaxValue, 0, 0)]
    [InlineData(5, 0, 5, 7)]                       // exactly the maximum
    [InlineData(6, 0, 5, 7)]                       // above the maximum → clamped
    [InlineData(int.MaxValue, 2, 5, 5)]
    [InlineData(int.MaxValue, int.MaxValue, 0, 0)]
    public async Task Pagination_edges(int limit, int offset, int expectedCount, int expectedRemaining)
    {
        var (fx, mediator, bob) = await WithRegisteredAsync(12, o => o.MaxPageSize = 5);
        await using var _ = fx;

        var (dids, pagination) = await fx.QueryAsync(bob, mediator, new RecipientQueryPaging(limit, offset));

        dids.Should().HaveCount(expectedCount);
        pagination.Should().Be(new RecipientPagination(expectedCount, offset, expectedRemaining), "count = returned, offset = applied, remaining = left after the page");
        if (expectedCount > 0)
            dids[0].Should().Be($"did:example:r{offset:D3}");
    }

    [Fact]
    public async Task Walking_the_pages_visits_every_did_once_in_registration_order()
    {
        var (fx, mediator, bob) = await WithRegisteredAsync(23, o => o.MaxPageSize = 4);
        await using var _ = fx;

        var seen = new List<string>();
        var offset = 0;
        while (true)
        {
            var (dids, pagination) = await fx.QueryAsync(bob, mediator, new RecipientQueryPaging(100, offset));
            seen.AddRange(dids);
            pagination!.Count.Should().Be(dids.Count);
            pagination.Offset.Should().Be(offset);
            pagination.Remaining.Should().Be(23 - offset - dids.Count);
            if (pagination.Remaining == 0)
                break;
            offset += pagination.Count;
        }

        seen.Should().Equal(Enumerable.Range(0, 23).Select(i => $"did:example:r{i:D3}"));
    }

    [Fact]
    public async Task A_peer_sees_only_its_own_dids_and_a_peer_without_a_grant_sees_an_empty_page()
    {
        var (fx, mediator, bob) = await WithRegisteredAsync(3);
        await using var _ = fx;
        var carol = await fx.NewPeerAsync();
        var stranger = await fx.NewPeerAsync();
        await fx.EnrolAsync(carol.Did, mediator);
        await fx.UpdateAsync(carol.Did, mediator, Add("did:example:carols"));

        (await fx.QueryAsync(carol.Did, mediator)).Should().BeEquivalentTo((new[] { "did:example:carols" }, new RecipientPagination(1, 0, 0)));
        (await fx.QueryAsync(bob, mediator)).Dids.Should().HaveCount(3).And.NotContain("did:example:carols");

        var (dids, pagination) = await fx.QueryAsync(stranger.Did, mediator, new RecipientQueryPaging(10, 0));
        dids.Should().BeEmpty();
        pagination.Should().Be(new RecipientPagination(0, 0, 0));
    }

    [Fact]
    public async Task The_store_is_asked_for_the_clamped_page_only()
    {
        var (fx, mediator, bob) = await WithRegisteredAsync(3, o => o.MaxPageSize = 5, fakeStore: true);
        await using var _ = fx;
        fx.FakeStore!.ResetCalls();

        await fx.QueryAsync(bob, mediator, new RecipientQueryPaging(int.MaxValue, 2));
        await fx.QueryAsync(bob, mediator, new RecipientQueryPaging(-7, 1));
        await fx.QueryAsync(bob, mediator);

        fx.FakeStore.ListCalls.Should().Equal((bob, 2, 5), (bob, 1, 0), (bob, 0, 5));
    }

    public static TheoryData<string> QueryStoreFaults => new() { "throws", "too-many", "null-page", "null-dids", "negative-total" };

    [Theory]
    [MemberData(nameof(QueryStoreFaults))]
    public async Task A_store_fault_on_a_query_sends_nothing_and_never_escapes_dispatch(string fault)
    {
        var (fx, mediator, bob) = await WithRegisteredAsync(3, o => o.MaxPageSize = 2, fakeStore: true);
        await using var _ = fx;
        fx.FakeStore!.OnListRecipients = fault switch
        {
            "throws" => (_, _, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker),
            "too-many" => (_, _, _, _) => new ValueTask<RecipientPage>(new RecipientPage(new[] { "did:example:1", "did:example:2", "did:example:3" }, 3)),
            "null-page" => (_, _, _, _) => new ValueTask<RecipientPage>((RecipientPage)null!),
            "null-dids" => (_, _, _, _) => new ValueTask<RecipientPage>(new RecipientPage(null!, 3)),
            _ => (_, _, _, _) => new ValueTask<RecipientPage>(new RecipientPage(Array.Empty<string>(), -1)),
        };

        var outcome = await fx.SendAsync(CoordinateMediation.CreateRecipientQuery(bob, mediator), mediator);

        outcome.Result.Should().Be(DispatchResult.NoReply, "the protocol has no error shape for a query");
        var line = fx.Logs.Handler.Should().ContainSingle(e => e.Level == LogLevel.Error).Which;
        line.Exception.Should().BeNull();
        line.Everything.Should().NotContain(HandlerBoomException.Marker).And.NotContain("did:");
    }

    [Fact]
    public async Task A_total_that_shrinks_below_the_page_never_yields_a_negative_remaining()
    {
        // A store whose total and page disagree (a concurrent removal): remaining floors at zero.
        var (fx, mediator, bob) = await WithRegisteredAsync(0, fakeStore: true);
        await using var _ = fx;
        fx.FakeStore!.OnListRecipients = (_, _, _, _) => new ValueTask<RecipientPage>(new RecipientPage(new[] { "did:example:1", "did:example:2" }, 1));

        (await fx.QueryAsync(bob, mediator, new RecipientQueryPaging(10, 0))).Pagination.Should().Be(new RecipientPagination(2, 0, 0));
    }
}
