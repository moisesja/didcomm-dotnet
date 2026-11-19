using System.Text.Json;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Json;
using DidComm.Mediator.Coordination;
using DidComm.Protocols;
using Xunit;
using static DidComm.Mediator.Tests.Coordination.HandlerMessages;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>
/// QA-1 for the Coordinate Mediation 2.0 mediator role (#99), through the container's
/// <c>AddCoordinateMediation()</c> and the real dispatcher: one tenant whichever version enrolled
/// and whichever version updated first; a <c>recipient_did</c> that is not a bare DID (parameters,
/// an upper-case method or scheme, white space, an empty fragment, query or path) answered
/// <c>client_error</c>, echoed as sent and never reduced to the DID it names; and the 2048-character
/// bound on <c>recipient_did</c> (exactly 2048 registered and echoed, 2049 refused without quoting
/// it and before the store) (FR-MED-10, FR-MED-05, FR-API-09).
/// </summary>
public sealed class CoordinateMediation20Qa1Tests
{
    private const RecipientUpdateOutcome Success = RecipientUpdateOutcome.Success;
    private const RecipientUpdateOutcome NoChange = RecipientUpdateOutcome.NoChange;
    private const RecipientUpdateOutcome ClientError = RecipientUpdateOutcome.ClientError;

    // ---- one tenant, in every order ----------------------------------------------------------

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Both_versions_update_one_grant_whichever_version_enrolled_and_whichever_updated_first(bool enrolIn20, bool firstUpdateIn20)
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var first = (await fx.NewPeerAsync()).Did;
        var second = (await fx.NewPeerAsync()).Did;

        if (enrolIn20)
            (await fx.Enrol20Async(bob.Did, mediator.Did)).Should().Be(mediator.Did);
        else
            (await fx.EnrolAsync(bob.Did, mediator.Did)).Should().Equal(mediator.Did);

        // The first update through one version, the second through the other.
        (await UpdateAsync(fx, firstUpdateIn20, bob.Did, mediator.Did, Add(first))).Should().Equal(Added(first, Success));
        (await UpdateAsync(fx, !firstUpdateIn20, bob.Did, mediator.Did, Add(second))).Should().Equal(Added(second, Success));

        // Each version sees the other's registration, in registration order.
        (await UpdateAsync(fx, !firstUpdateIn20, bob.Did, mediator.Did, Add(first))).Should().Equal(Added(first, NoChange));
        (await UpdateAsync(fx, firstUpdateIn20, bob.Did, mediator.Did, Add(second))).Should().Equal(Added(second, NoChange));
        (await fx.QueryAsync(bob.Did, mediator.Did)).Dids.Should().Equal(first, second);
        (await fx.Query20Async(bob.Did, mediator.Did)).Keys.Should().Equal(first, second);
        fx.Memory.GrantCount.Should().Be(1);

        // Each is removed through the version that did not add it.
        (await UpdateAsync(fx, !firstUpdateIn20, bob.Did, mediator.Did, Remove(first))).Should().Equal(Removed(first, Success));
        (await UpdateAsync(fx, firstUpdateIn20, bob.Did, mediator.Did, Remove(second))).Should().Equal(Removed(second, Success));
        fx.Memory.RecipientCount.Should().Be(0);
        (await fx.Query20Async(bob.Did, mediator.Did)).Keys.Should().BeEmpty();
        fx.Memory.GrantCount.Should().Be(1);
    }

    // ---- recipient_did must be a bare DID (FR-MED-05) ---------------------------------------

    public static TheoryData<string, string> NotBareDids => new()
    {
        { "a parameter", "did:example:alice;service=didcomm" },
        { "an empty parameter value", "did:example:alice;p=" },
        { "an upper-case method", "did:EXAMPLE:alice" },
        { "an upper-case scheme", "DID:example:alice" },
        { "a leading space", " did:example:alice" },
        { "a trailing space", "did:example:alice " },
        { "a space inside", "did:example:al ice" },
        { "a trailing newline", "did:example:alice\n" },
        { "a trailing tab", "did:example:alice\t" },
        { "an empty fragment", "did:example:alice#" },
        { "an empty query", "did:example:alice?" },
        { "an empty path", "did:example:alice/" },
    };

    [Theory]
    [MemberData(nameof(NotBareDids))]
    public async Task A_recipient_did_that_is_not_a_bare_did_is_client_error_echoed_as_sent_and_the_did_it_names_stays_free(string what, string value)
    {
        const string bare = "did:example:alice";
        await using var fx = HandlerFixture.Create(fakeStore: true, fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);
        fx.FakeStore!.ResetCalls();

        var outcome = await fx.SendAsync(CoordinateMediation20.CreateKeylistUpdate(bob.Did, mediator.Did, new[] { Add(value), Remove(value) }), mediator.Did);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced, what);
        CoordinateMediation20.TryParseKeylistUpdateResponse(outcome.Reply!, out var updated).Should().BeTrue();
        updated.Should().Equal(new[] { Added(value, ClientError), Removed(value, ClientError) }, what);
        outcome.Reply!.Body!["updated"]!.AsArray().Select(e => e!["recipient_did"]!.GetValue<string>())
            .Should().Equal(new[] { value, value }, "echoed exactly as sent, never normalized ({0})", what);
        fx.FakeStore.UpdateCalls.Should().BeEmpty("nothing that is not a bare DID reaches the store ({0})", what);
        fx.Policy!.Registrations.Should().BeEmpty();
        (await fx.Memory.FindGrantByRecipientDidAsync(bare, default)).Should().BeNull("the value is not reduced to the DID it names ({0})", what);

        // Positive control: the bare DID was never registered, so Bob registering it is a fresh add.
        (await fx.Update20Async(bob.Did, mediator.Did, Add(bare))).Should().Equal(new[] { Added(bare, Success) }, what);
        (await fx.RegisteredAsync(bob.Did)).Should().Equal(bare);
    }

    // ---- FR-API-09 on recipient_did ----------------------------------------------------------

    [Fact]
    public async Task A_recipient_did_of_2048_characters_is_registered_and_echoed_and_one_of_2049_is_refused_unquoted_before_the_store()
    {
        await using var fx = HandlerFixture.Create(fakeStore: true, fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);
        var atLimit = "did:example:" + new string('a', 2048 - "did:example:".Length);
        var overLimit = "did:example:SENTINEL" + new string('q', 2049 - "did:example:SENTINEL".Length);
        atLimit.Should().HaveLength(2048);
        overLimit.Should().HaveLength(2049);
        fx.FakeStore!.ResetCalls();

        // 2049, alone and within the entry bound: malformed, from the parse, before any store call.
        var refused = CoordinateMediation20.CreateKeylistUpdate(bob.Did, mediator.Did, new[] { Add("did:example:ok") });
        refused.Body!["updates"]![0]!["recipient_did"] = overLimit;
        var ex = (await FluentActions.Awaiting(() => fx.SendAsync(refused, mediator.Did)).Should().ThrowAsync<MalformedMessageException>()).Which;
        ex.Message.Should().Contain("2048").And.NotContain("SENTINEL").And.NotContain("qqqq");
        ex.ToString().Should().NotContain("SENTINEL");
        fx.FakeStore.Calls.Should().BeEmpty("the bound is applied before the store is touched");
        fx.Policy!.Registrations.Should().BeEmpty();
        (await fx.RegisteredAsync(bob.Did)).Should().BeEmpty("the valid entry beside the over-long one was not applied either");

        // Exactly 2048: registered, and echoed in full on the wire.
        var accepted = await fx.SendAsync(CoordinateMediation20.CreateKeylistUpdate(bob.Did, mediator.Did, new[] { Add(atLimit) }), mediator.Did);
        CoordinateMediation20.TryParseKeylistUpdateResponse(accepted.Reply!, out var updated).Should().BeTrue();
        updated.Should().Equal(Added(atLimit, Success));
        JsonSerializer.Serialize(accepted.Reply, DidCommJson.Default).Should().Contain("\"recipient_did\":\"" + atLimit + "\"");
        (await fx.Query20Async(bob.Did, mediator.Did)).Keys.Should().Equal(atLimit);

        fx.Logs.Entries.Should().NotContain(e => e.Everything.Contains("SENTINEL", StringComparison.Ordinal) || e.Everything.Contains("aaaaaaaa", StringComparison.Ordinal),
            "no line, of any category, carries either value");
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static Task<IReadOnlyList<RecipientUpdateResult>> UpdateAsync(HandlerFixture fx, bool in20, string peer, string mediator, params RecipientUpdate[] updates)
        => in20 ? fx.Update20Async(peer, mediator, updates) : fx.UpdateAsync(peer, mediator, updates);
}
