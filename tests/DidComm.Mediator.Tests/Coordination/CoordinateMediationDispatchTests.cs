using System.Text.Json;
using AwesomeAssertions;
using DidComm.Json;
using DidComm.Mediator.Coordination;
using DidComm.Protocols;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>
/// FR-MED-01..06 through the real container and dispatcher: the whole enrolment sequence
/// (<c>mediate-request</c> → grant → <c>recipient-update</c> → <c>recipient-query</c>) with
/// did:peer identities and real authcrypt envelopes, no transport.
/// </summary>
public sealed class CoordinateMediationDispatchTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Request_grant_update_query_sequence_through_the_dispatcher()
    {
        await using var host = MediatorTestHost.Create();
        var mediator = await host.NewPeerAsync();
        var bob = await host.NewPeerAsync();
        var bobRecipient = await host.NewPeerAsync();

        // mediate-request → mediate-grant
        var request = CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did);
        var granted = await host.SendAuthcryptAsync(request, mediator.Did);
        granted.Result.Should().Be(DispatchResult.ReplyProduced);
        output.WriteLine(JsonSerializer.Serialize(granted.Reply, DidCommJson.Default));
        CoordinateMediation.TryParseGrant(granted.Reply!, out var routingDids).Should().BeTrue();
        routingDids.Should().Equal(mediator.Did);
        granted.Reply!.From.Should().Be(mediator.Did);
        granted.Reply.To.Should().Equal(bob.Did);
        granted.Reply.Thid.Should().Be(request.Id);

        // recipient-update → recipient-update-response
        var update = CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, new[]
        {
            new RecipientUpdate(bobRecipient.Did, RecipientUpdateAction.Add),
            new RecipientUpdate(bob.Did, RecipientUpdateAction.Add),
        });
        var updated = await host.SendAuthcryptAsync(update, mediator.Did);
        CoordinateMediation.TryParseRecipientUpdateResponse(updated.Reply!, out var results).Should().BeTrue();
        results.Should().Equal(
            new RecipientUpdateResult(bobRecipient.Did, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success),
            new RecipientUpdateResult(bob.Did, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success));
        updated.Reply!.Thid.Should().Be(update.Id);

        // recipient-query → recipient
        var query = CoordinateMediation.CreateRecipientQuery(bob.Did, mediator.Did, new RecipientQueryPaging(Limit: 1, Offset: 0));
        var listed = await host.SendAuthcryptAsync(query, mediator.Did);
        CoordinateMediation.TryParseRecipient(listed.Reply!, out var dids, out var pagination).Should().BeTrue();
        dids.Should().Equal(bobRecipient.Did);
        pagination.Should().Be(new RecipientPagination(Count: 1, Offset: 0, Remaining: 1));
        listed.Reply!.Thid.Should().Be(query.Id);

        (await host.Store.FindGrantByRecipientDidAsync(bobRecipient.Did, default))!.PeerDid.Should().Be(bob.Did);
    }

    [Fact]
    public async Task Anoncrypt_mediate_request_is_not_granted_and_not_answered()
    {
        await using var host = MediatorTestHost.Create();
        var mediator = await host.NewPeerAsync();
        var bob = await host.NewPeerAsync();

        var outcome = await host.SendAnoncryptAsync(CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did), mediator.Did);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        (await host.Store.GetGrantAsync(bob.Did, default)).Should().BeNull();
    }
}
