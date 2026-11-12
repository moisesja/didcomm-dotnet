using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// FR-MED-03 over the TestServer: a host with <c>AddCoordinateMediation()</c> behind the
/// registry-aware <c>MapDidCommEndpoint</c>. A recipient's request with <c>return_route: all</c>
/// gets the mediator's reply as the HTTP 200 body (FR-RR-04); without the header the handler still
/// produces the reply and the endpoint answers 202. Replay admission (FR-RR-08) and the
/// authcrypt-only rule (FR-MED-05) hold end to end.
/// </summary>
public sealed class CoordinateMediationHttpTests
{
    private static Task<ReturnRouteTestHost> StartAsync(CountingPolicy? policy = null)
        => ReturnRouteTestHost.StartAsync(o =>
        {
            if (policy is not null)
                o.ConfigureServices = services => services.AddSingleton<IMediationPolicy>(policy);
            o.ConfigureDidComm = b => b.AddCoordinateMediation();
        });

    /// <summary>What <c>DidCommClient.SendAsync</c> does for a mediation request (FR-RR-09): a fresh <c>created_time</c>, and <c>return_route: all</c> when asked.</summary>
    private static Message Prepare(ReturnRouteTestHost host, Message message, bool returnRoute)
    {
        message.CreatedTime = host.Clock.Now.ToUnixTimeSeconds();
        if (returnRoute)
            ReturnRouteHeaderWriter.Write(message, ReturnRouteMode.All, thread: null);
        return message;
    }

    private static IMediationStore StoreOf(ReturnRouteTestHost host) => host.App.Services.GetRequiredService<IMediationStore>();

    private static async Task<Message> ReplyAsync(ReturnRouteTestHost host, TestPeer peer, HttpPostResult response)
    {
        response.Status.Should().Be(HttpStatusCode.OK);
        response.ContentType.Should().Be("application/didcomm-encrypted+json");
        var unpacked = await peer.Client.UnpackAsync(response.BodyText);
        unpacked.Encrypted.Should().BeTrue();
        unpacked.Authenticated.Should().BeTrue();
        unpacked.SenderKid.Should().StartWith(host.ServerDid + "#", "the reply is authcrypted by the mediator DID the request was addressed to");
        unpacked.Message.From.Should().Be(host.ServerDid);
        unpacked.Message.To.Should().Equal(peer.Did);
        unpacked.Message.CreatedTime.Should().Be(host.Clock.Now.ToUnixTimeSeconds(), "session replies under a mediator-service PIURI are stamped (FR-RR-09)");
        return unpacked.Message;
    }

    [Fact]
    public async Task Mediate_request_with_return_route_all_returns_the_grant_as_the_200_body()
    {
        await using var host = await StartAsync();
        var request = Prepare(host, CoordinateMediation.CreateMediateRequest(host.Alice.Did, host.ServerDid), returnRoute: true);

        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, request));

        var reply = await ReplyAsync(host, host.Alice, response);
        CoordinateMediation.TryParseGrant(reply, out var routingDids).Should().BeTrue();
        routingDids.Should().Equal(host.ServerDid);
        reply.Thid.Should().Be(request.Id);
        (await StoreOf(host).GetGrantAsync(host.Alice.Did, default)).Should().NotBeNull();
    }

    [Fact]
    public async Task The_same_request_without_the_header_gets_202_and_the_grant_is_still_made()
    {
        // A missing return_route is the recipient's defect; it must not fault the mediator. The
        // handler produces the grant, the endpoint has no session to carry it, and answers 202.
        await using var host = await StartAsync();
        var request = Prepare(host, CoordinateMediation.CreateMediateRequest(host.Alice.Did, host.ServerDid), returnRoute: false);

        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, request));

        response.Status.Should().Be(HttpStatusCode.Accepted);
        response.Body.Should().BeEmpty();
        (await StoreOf(host).GetGrantAsync(host.Alice.Did, default))!.RoutingDids.Should().Equal(host.ServerDid);
    }

    [Fact]
    public async Task A_replayed_request_gets_202_and_is_not_handled_again()
    {
        var policy = new CountingPolicy();
        await using var host = await StartAsync(policy);
        var request = Prepare(host, CoordinateMediation.CreateMediateRequest(host.Alice.Did, host.ServerDid), returnRoute: true);
        var packed = await host.AuthcryptAsync(host.Alice, request);

        (await host.PostAsync(packed)).Status.Should().Be(HttpStatusCode.OK);
        var replay = await host.PostAsync(packed);

        replay.Status.Should().Be(HttpStatusCode.Accepted, "FR-RR-08: a duplicate (peer, id) is not dispatched");
        replay.Body.Should().BeEmpty();
        policy.Requests.Should().Be(1);
    }

    [Fact]
    public async Task A_request_without_a_fresh_created_time_is_not_dispatched()
    {
        var policy = new CountingPolicy();
        await using var host = await StartAsync(policy);
        var request = CoordinateMediation.CreateMediateRequest(host.Alice.Did, host.ServerDid); // not stamped
        ReturnRouteHeaderWriter.Write(request, ReturnRouteMode.All, thread: null);

        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, request));

        response.Status.Should().Be(HttpStatusCode.Accepted);
        response.Body.Should().BeEmpty();
        policy.Requests.Should().Be(0, "coordinate-mediation is under the FR-RR-08 scope by PIURI");
        (await StoreOf(host).GetGrantAsync(host.Alice.Did, default)).Should().BeNull();
    }

    [Theory]
    [InlineData(EnvelopeShape.Plaintext)]
    [InlineData(EnvelopeShape.SignedOnly)]
    [InlineData(EnvelopeShape.Anoncrypt)]
    [InlineData(EnvelopeShape.AnoncryptOfSigned)]
    public async Task A_request_without_an_authcrypt_layer_gets_202_no_body_and_no_grant(EnvelopeShape shape)
    {
        var policy = new CountingPolicy();
        await using var host = await StartAsync(policy);
        var request = Prepare(host, CoordinateMediation.CreateMediateRequest(host.Mallory.Did, host.ServerDid), returnRoute: true);
        var (packed, contentType) = await host.PackAsync(host.Mallory, request, shape);

        var response = await host.PostAsync(packed, contentType: contentType);

        response.Status.Should().Be(HttpStatusCode.Accepted);
        response.Body.Should().BeEmpty();
        policy.Requests.Should().Be(0);
        (await StoreOf(host).GetGrantAsync(host.Mallory.Did, default)).Should().BeNull();
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData(EnvelopeShape.Authcrypt)]
    [InlineData(EnvelopeShape.AnoncryptOfAuthcrypt)]
    [InlineData(EnvelopeShape.AuthcryptOfSigned)]
    public async Task Enrol_update_and_query_over_http_each_return_their_reply_in_the_200_body(EnvelopeShape shape)
    {
        await using var host = await StartAsync();
        var alice = host.Alice;

        async Task<Message> RoundTripAsync(Message request)
        {
            var (packed, contentType) = await host.PackAsync(alice, Prepare(host, request, returnRoute: true), shape);
            var reply = await ReplyAsync(host, alice, await host.PostAsync(packed, contentType: contentType));
            reply.Thid.Should().Be(request.Id);
            return reply;
        }

        CoordinateMediation.TryParseGrant(await RoundTripAsync(CoordinateMediation.CreateMediateRequest(alice.Did, host.ServerDid)), out _).Should().BeTrue();

        var update = await RoundTripAsync(CoordinateMediation.CreateRecipientUpdate(alice.Did, host.ServerDid, new[]
        {
            new RecipientUpdate(alice.Did, RecipientUpdateAction.Add),
            new RecipientUpdate(host.Carol.Did, RecipientUpdateAction.Add),
            new RecipientUpdate(alice.Did + "#key-1", RecipientUpdateAction.Add),
        }));
        CoordinateMediation.TryParseRecipientUpdateResponse(update, out var updated).Should().BeTrue();
        updated.Should().Equal(
            new RecipientUpdateResult(alice.Did, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success),
            new RecipientUpdateResult(host.Carol.Did, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success),
            new RecipientUpdateResult(alice.Did + "#key-1", RecipientUpdateAction.Add, RecipientUpdateOutcome.ClientError));

        var listed = await RoundTripAsync(CoordinateMediation.CreateRecipientQuery(alice.Did, host.ServerDid, new RecipientQueryPaging(Limit: 1, Offset: 1)));
        CoordinateMediation.TryParseRecipient(listed, out var dids, out var pagination).Should().BeTrue();
        dids.Should().Equal(host.Carol.Did);
        pagination.Should().Be(new RecipientPagination(Count: 1, Offset: 1, Remaining: 0));

        host.LeakingLogLines().Should().BeEmpty("no DID or key id of any party reaches a log line");
    }

    [Fact]
    public async Task Mallory_cannot_take_or_remove_alices_recipient_over_http()
    {
        await using var host = await StartAsync();

        async Task<Message> SendAsync(TestPeer from, Message request)
            => await ReplyAsync(host, from, await host.PostAsync(await host.AuthcryptAsync(from, Prepare(host, request, returnRoute: true))));

        await SendAsync(host.Alice, CoordinateMediation.CreateMediateRequest(host.Alice.Did, host.ServerDid));
        await SendAsync(host.Mallory, CoordinateMediation.CreateMediateRequest(host.Mallory.Did, host.ServerDid));
        await SendAsync(host.Alice, CoordinateMediation.CreateRecipientUpdate(host.Alice.Did, host.ServerDid, new[] { new RecipientUpdate(host.Alice.Did, RecipientUpdateAction.Add) }));

        var attack = await SendAsync(host.Mallory, CoordinateMediation.CreateRecipientUpdate(host.Mallory.Did, host.ServerDid, new[]
        {
            new RecipientUpdate(host.Alice.Did, RecipientUpdateAction.Remove),
            new RecipientUpdate(host.Alice.Did, RecipientUpdateAction.Add),
        }));

        CoordinateMediation.TryParseRecipientUpdateResponse(attack, out var updated).Should().BeTrue();
        updated!.Select(u => u.Result).Should().Equal(RecipientUpdateOutcome.ClientError, RecipientUpdateOutcome.ClientError);
        (await StoreOf(host).FindGrantByRecipientDidAsync(host.Alice.Did, default))!.PeerDid.Should().Be(host.Alice.Did);
    }

    [Fact]
    public async Task A_malformed_request_from_an_enrolled_peer_is_rejected_without_leaking_its_values()
    {
        await using var host = await StartAsync();
        var enrol = Prepare(host, CoordinateMediation.CreateMediateRequest(host.Alice.Did, host.ServerDid), returnRoute: true);
        (await host.PostAsync(await host.AuthcryptAsync(host.Alice, enrol))).Status.Should().Be(HttpStatusCode.OK);

        const string sentinel = "did:example:SENTINEL-recipient";
        var malformed = Prepare(host, new MessageBuilder()
            .WithType(CoordinateMediation.RecipientUpdateType)
            .WithFrom(host.Alice.Did)
            .WithTo(host.ServerDid)
            .WithBody(new JsonObject { ["updates"] = new JsonArray(new JsonObject { ["recipient_did"] = sentinel, ["action"] = "SENTINEL-upsert" }) })
            .Build(), returnRoute: true);

        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, malformed));

        response.Status.Should().Be(HttpStatusCode.BadRequest, "a malformed message is the endpoint's uniform rejection");
        response.BodyText.Should().NotContain("SENTINEL").And.NotContain(host.Alice.Did);
        host.LeakingLogLines("SENTINEL").Should().BeEmpty("the exception text the endpoint logs names the member and the rule, never a value");
        (await StoreOf(host).ListRecipientsAsync(host.Alice.Did, 0, 10, default)).Total.Should().Be(0);
    }

    private sealed class CountingPolicy : IMediationPolicy
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public ValueTask<MediationDecision> OnMediateRequestAsync(string peerDid, ProtocolContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _requests);
            return new ValueTask<MediationDecision>(MediationDecision.Grant);
        }

        public ValueTask<bool> MayRegisterAsync(string peerDid, string recipientDid, ProtocolContext context, CancellationToken ct) => new(true);
    }
}
