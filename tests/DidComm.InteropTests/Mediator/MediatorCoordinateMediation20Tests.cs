using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator.Client;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using DidComm.Secrets;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// The Coordinate Mediation 2.0 mediator role over the real endpoints (FR-MED-10, FR-MED-07,
/// FR-RR-08; #99): this library's mediator (<c>AddMediator()</c> + <c>MapDidCommMediator</c> on a
/// TestServer) answering 2.0 and 3.0 over one set of grants. A <see cref="MediationClient"/> pinned to
/// 2.0 enrols and registers a DID that a 3.0 client lists (and the other way round), and Alice's
/// forward to it reaches Bob's pickup. With two routing DIDs a 2.0 grant names the first only, a 3.0
/// grant names both in order, and forwards to either are relayed. A client that negotiates asks
/// first and then speaks 3.0. 2.0 requests are under replay admission by their protocol alone: one
/// without <c>created_time</c> is dropped, a stamped one is applied once. Wallet shapes seen in the
/// wild: an Identus-style <c>keylist-update</c> without <c>return_route</c> is applied (its answer
/// has nowhere to go over HTTP), a Blocktrust-style <c>mediate-request</c> without
/// <c>created_time</c> is refused unless the host turns that requirement off. With
/// <c>EnableV2 = false</c> 2.0 is neither answered nor disclosed, and a request that is not
/// authcrypted is never granted or answered.
/// </summary>
public sealed class MediatorCoordinateMediation20Tests
{
    private const string AnyCoordinateMediation = "https://didcomm.org/coordinate-mediation/*";

    // ---- a 2.0 client and a 3.0 client on one tenant -----------------------------------------------

    [Fact]
    public async Task A_client_pinned_to_2_0_and_a_3_0_client_share_one_grant_and_Alices_forward_reaches_Bobs_pickup()
    {
        var seen = new Qa1Recorder();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.PostConfigureServices = seen.Register);
        await using var v2 = await MediatorClientHarness.CreateAsync(mediator, o => o.Client = c => c.CoordinateMediationVersion = CoordinateMediationVersion.V2);
        await using var v3 = await MediatorClientHarness.CreateAsync(mediator); // negotiates (3.0 with this mediator)
        var bob = await v2.NewPeerAsync();
        v3.Hold(bob);

        // Enrolled through 2.0: one routing DID, the first configured one.
        var grant = await v2.Mediation.RequestMediationAsync(mediator.Did, bob.Did);
        grant.Should().NotBeNull();
        grant!.PeerDid.Should().Be(bob.Did);
        grant.RoutingDids.Should().Equal(mediator.Did);
        (await mediator.Mediation.GetGrantAsync(bob.Did, default))!.RoutingDids.Should().Equal(mediator.Did);

        // Added through 2.0, listed through 3.0.
        var viaV2 = await MediatorClientEnrolmentTests.PublishAsync(v2, grant);
        (await v2.Mediation.UpdateRecipientsAsync(mediator.Did, bob.Did, new[] { new RecipientUpdate(viaV2.Did, RecipientUpdateAction.Add) }))
            .Should().Equal(new RecipientUpdateResult(viaV2.Did, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success));
        (await v3.Mediation.QueryRecipientsAsync(mediator.Did, bob.Did)).Dids.Should().Equal(viaV2.Did);

        // The same peer asking through 3.0 gets the grant it already holds.
        (await v3.Mediation.RequestMediationAsync(mediator.Did, bob.Did))!.RoutingDids.Should().Equal(mediator.Did);

        // Added through 3.0, listed through 2.0, in registration order.
        var viaV3 = await MediatorClientEnrolmentTests.PublishAsync(v3, grant);
        (await v3.Mediation.UpdateRecipientsAsync(mediator.Did, bob.Did, new[] { new RecipientUpdate(viaV3.Did, RecipientUpdateAction.Add) }))
            .Should().Equal(new RecipientUpdateResult(viaV3.Did, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success));
        (await v2.Mediation.QueryRecipientsAsync(mediator.Did, bob.Did)).Dids.Should().Equal(viaV2.Did, viaV3.Did);

        // Each client spoke its own version on the wire.
        seen.From(bob.Did).Select(r => r.Type).Should().Equal(
            CoordinateMediation20.MediateRequestType,
            CoordinateMediation20.KeylistUpdateType,
            DiscoverFeatures.QueriesType,
            CoordinateMediation.RecipientQueryType,
            CoordinateMediation.MediateRequestType,
            CoordinateMediation.RecipientUpdateType,
            CoordinateMediation20.KeylistQueryType);

        // Alice writes to the DID registered through 2.0 (SendAsync wraps it in a forward for the
        // mediator its DID document names), and Bob's pickup fetches it.
        var alice = await v2.NewAliceAsync();
        var note = alice.Note(viaV2.Did, "through 2.0");
        var sent = await alice.Client.SendAsync(note, new SendOptions(new[] { viaV2.Did }, alice.Did));
        sent.Transport.Accepted.Should().BeTrue();
        sent.EndpointUsed.Should().Be(mediator.Endpoint);
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);

        var batch = await v2.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);
        batch.Items.Should().ContainSingle();
        PickupE2E.ShouldBeAlicesNote(batch.Items[0], alice, viaV2, note);
        mediator.LeakingLogLines(viaV2.Did, viaV3.Did).Should().BeEmpty();
    }

    // ---- the first routing DID ---------------------------------------------------------------------

    [Fact]
    public async Task With_two_routing_DIDs_a_2_0_grant_names_the_first_a_3_0_grant_names_both_and_forwards_to_either_are_relayed()
    {
        var second = await MediatorCast.MintAsync(new DidCommServiceSpec("http://mediator.test/", Accept: new[] { "didcomm/v2" }));
        await using var mediator = await StartWithSecondRoutingDidAsync(second);
        await using var v2 = await MediatorClientHarness.CreateAsync(mediator, o => o.Client = c => c.CoordinateMediationVersion = CoordinateMediationVersion.V2);
        await using var v3 = await MediatorClientHarness.CreateAsync(mediator, o => o.Client = c => c.CoordinateMediationVersion = CoordinateMediationVersion.V3);
        var bob = await v2.NewPeerAsync();
        v3.Hold(bob);
        var carol = await v3.NewPeerAsync();

        var bobsGrant = await v2.Mediation.RequestMediationAsync(mediator.Did, bob.Did);
        bobsGrant!.RoutingDids.Should().Equal(new[] { mediator.Did }, "a 2.0 grant carries one routing DID: the first");
        (await v3.Mediation.RequestMediationAsync(mediator.Did, carol.Did))!.RoutingDids.Should().Equal(mediator.Did, second.Did);
        (await v3.Mediation.RequestMediationAsync(mediator.Did, bob.Did))!.RoutingDids
            .Should().Equal(new[] { mediator.Did, second.Did }, "Bob's grant holds both; a 2.0 grant can name only the first");
        (await mediator.Mediation.GetGrantAsync(bob.Did, default))!.RoutingDids.Should().Equal(mediator.Did, second.Did);

        // On the wire: a JSON string naming the first, whichever routing DID the request was sent to.
        var dave = await mediator.NewPeerAsync();
        foreach (var addressedTo in new[] { mediator.Did, second.Did })
        {
            var request = Raw20(CoordinateMediation20.MediateRequestType, dave, addressedTo, new JsonObject(), mediator.Clock.Now.ToUnixTimeSeconds(), returnRoute: true);
            var response = await PostAsync(mediator, await AuthcryptAsync(dave, request, addressedTo));
            response.Status.Should().Be(HttpStatusCode.OK);
            ShouldBe20Grant(await OpenAnswerAsync(dave, response.Body, addressedTo), request, mediator.Did);
        }

        // A DID registered through 2.0 is reached through either routing DID.
        var recipient = await MediatorClientEnrolmentTests.PublishAsync(v2, bobsGrant);
        (await v2.Mediation.UpdateRecipientsAsync(mediator.Did, bob.Did, new[] { new RecipientUpdate(recipient.Did, RecipientUpdateAction.Add) }))
            .Should().ContainSingle().Which.Result.Should().Be(RecipientUpdateOutcome.Success);
        var alice = await v2.NewAliceAsync();

        var viaFirst = alice.Note(recipient.Did, "via the first");
        (await alice.Client.SendAsync(viaFirst, new SendOptions(new[] { recipient.Did }, alice.Did))).Transport.Accepted.Should().BeTrue();
        await mediator.FlushAsync();

        var viaSecond = alice.Note(recipient.Did, "via the second");
        var (packed, _) = await alice.ForwardAsync(second.Did, recipient.Did, await alice.AuthcryptAsync(viaSecond, recipient.Did));
        await Qa.PostForwardAsync(mediator, packed);
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.Queued);
        var batch = await v2.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);
        batch.Items.Should().HaveCount(2);
        PickupE2E.ShouldBeAlicesNote(batch.Items[0], alice, recipient, viaFirst);
        PickupE2E.ShouldBeAlicesNote(batch.Items[1], alice, recipient, viaSecond);
        mediator.LeakingLogLines(second.Did, recipient.Did).Should().BeEmpty();
    }

    // ---- negotiation -------------------------------------------------------------------------------

    [Fact]
    public async Task A_negotiating_client_asks_once_and_then_speaks_3_0_to_a_mediator_that_discloses_both()
    {
        var seen = new Qa1Recorder();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.PostConfigureServices = seen.Register);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();

        // What the client is told: both versions, in the mediator role.
        var asker = await mediator.NewPeerAsync();
        DisclosedSet(await DiscloseAsync(mediator, asker)).Should().BeEquivalentTo(new[]
        {
            (CoordinateMediation.ProtocolUri, CoordinateMediation.MediatorRole),
            (CoordinateMediation20.ProtocolUri, CoordinateMediation.MediatorRole),
        });

        (await harness.Mediation.RequestMediationAsync(mediator.Did, bob.Did))!.RoutingDids.Should().Equal(mediator.Did);
        (await harness.Mediation.QueryRecipientsAsync(mediator.Did, bob.Did)).Dids.Should().BeEmpty();

        var fromBob = seen.From(bob.Did);
        fromBob.Select(r => r.Type).Should().Equal(
            new[] { DiscoverFeatures.QueriesType, CoordinateMediation.MediateRequestType, CoordinateMediation.RecipientQueryType },
            "3.0 is preferred when both are disclosed, and the answer is remembered for this mediator");
        fromBob.Should().AllSatisfy(r =>
        {
            r.ReturnRoute.Should().Be(ReturnRouteMode.All);
            r.CreatedTime.Should().Be(mediator.Clock.Now.ToUnixTimeSeconds());
        });
        harness.Channel.Versions.TryGet(mediator.Did, out var version).Should().BeTrue();
        version.Should().Be(CoordinateMediationVersion.V3);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    // ---- replay admission (FR-RR-08) ---------------------------------------------------------------

    [Theory]
    [InlineData(true)] // the 2.0 page's shape: return_route "all" on every request
    [InlineData(false)] // no return_route: in the replay scope by its protocol alone
    public async Task A_2_0_mediate_request_needs_a_created_time_and_is_applied_once(bool returnRoute)
    {
        var seen = new Qa1Recorder();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.PostConfigureServices = seen.Register);
        var bob = await mediator.NewPeerAsync();

        // Without created_time: dropped before dispatch, answered like any accepted message.
        var unstamped = Raw20(CoordinateMediation20.MediateRequestType, bob, mediator.Did, new JsonObject(), createdTime: null, returnRoute);
        var refused = await PostAsync(mediator, await AuthcryptAsync(bob, unstamped, mediator.Did));
        refused.Status.Should().Be(HttpStatusCode.Accepted);
        refused.Body.Should().BeEmpty();
        await WaitForLineAsync(mediator, $"Inbound {unstamped.Id} dropped: replay admission MissingCreatedTime", "the refusal is the replay admission's");
        seen.All.Should().NotContain(r => r.Id == unstamped.Id, "a refused message is never dispatched");
        (await mediator.Mediation.GetGrantAsync(bob.Did, default)).Should().BeNull();

        // Stamped: granted (positive control).
        var stamped = Raw20(CoordinateMediation20.MediateRequestType, bob, mediator.Did, new JsonObject(), mediator.Clock.Now.ToUnixTimeSeconds(), returnRoute);
        var envelope = await AuthcryptAsync(bob, stamped, mediator.Did);
        var granted = await PostAsync(mediator, envelope);
        if (returnRoute)
        {
            granted.Status.Should().Be(HttpStatusCode.OK);
            ShouldBe20Grant(await OpenAnswerAsync(bob, granted.Body, mediator.Did), stamped, mediator.Did);
        }
        else
        {
            granted.Status.Should().Be(HttpStatusCode.Accepted, "without a return route the grant has nowhere to go over HTTP");
            granted.Body.Should().BeEmpty();
        }
        (await mediator.Mediation.GetGrantAsync(bob.Did, default))!.RoutingDids.Should().Equal(mediator.Did);

        // The identical envelope again: dropped as a duplicate, not dispatched a second time.
        var replayed = await PostAsync(mediator, envelope);
        replayed.Status.Should().Be(HttpStatusCode.Accepted);
        replayed.Body.Should().BeEmpty();
        await WaitForLineAsync(mediator, $"Inbound {stamped.Id} dropped: replay admission Duplicate", "the replay is refused by the replay admission");
        seen.All.Count(r => r.Id == stamped.Id).Should().Be(1);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    // ---- wallet shapes -----------------------------------------------------------------------------

    [Fact]
    public async Task An_Identus_style_keylist_update_without_return_route_is_applied_and_its_answer_has_nowhere_to_go()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        var registered = await mediator.NewRecipientAsync();

        // The Identus SDK sends return_route on mediate-request only, on a thread of its own (thid ≠ id)
        // under a parent thread.
        var request = Raw20(CoordinateMediation20.MediateRequestType, bob, mediator.Did, new JsonObject(), mediator.Clock.Now.ToUnixTimeSeconds(), returnRoute: true);
        request.Thid = Guid.NewGuid().ToString();
        request.Pthid = Guid.NewGuid().ToString();
        var granted = await PostAsync(mediator, await AuthcryptAsync(bob, request, mediator.Did));
        granted.Status.Should().Be(HttpStatusCode.OK);
        ShouldBe20Grant(await OpenAnswerAsync(bob, granted.Body, mediator.Did), request, mediator.Did);

        // keylist-update, authcrypted and stamped, without return_route.
        var updates = new JsonObject
        {
            ["updates"] = new JsonArray(new JsonObject { ["recipient_did"] = registered.Did, ["action"] = "add" }),
        };
        var update = Raw20(CoordinateMediation20.KeylistUpdateType, bob, mediator.Did, updates, mediator.Clock.Now.ToUnixTimeSeconds(), returnRoute: false);
        var response = await PostAsync(mediator, await AuthcryptAsync(bob, update, mediator.Did));

        response.Status.Should().Be(HttpStatusCode.Accepted, "no return route: the HTTP response carries no reply");
        response.Body.Should().BeEmpty();
        await WaitForLineAsync(mediator,
            $"Protocol dispatch produced a reply (handler={CoordinateMediation20.ProtocolUri}, returned-on-session=False)",
            "the handler answered; the endpoint had no channel for it");
        var listed = await mediator.AskAsync(bob, CoordinateMediation.CreateRecipientQuery(bob.Did, mediator.Did));
        CoordinateMediation.TryParseRecipient(listed.Message, out var dids, out _).Should().BeTrue();
        dids.Should().Equal(new[] { registered.Did }, "the update was applied");
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Blocktrust_style_mediate_request_without_created_time_is_refused_unless_the_host_turns_that_requirement_off(bool requireCreatedTime)
    {
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            if (!requireCreatedTime)
                o.Mediator = m => m.Replay.RequireCreatedTime = false;
        });
        var bob = await mediator.NewPeerAsync();

        // As Blocktrust's client builds it: a body, return_route "all", no created_time, and an extra
        // top-level custom_headers array.
        var request = Raw20(CoordinateMediation20.MediateRequestType, bob, mediator.Did, new JsonObject(), createdTime: null, returnRoute: true);
        request.AdditionalHeaders!["custom_headers"] = JsonSerializer.SerializeToElement(new[] { new Dictionary<string, string> { ["return_route"] = "all" } });
        var response = await PostAsync(mediator, await AuthcryptAsync(bob, request, mediator.Did));

        if (requireCreatedTime)
        {
            response.Status.Should().Be(HttpStatusCode.Accepted);
            response.Body.Should().BeEmpty();
            await WaitForLineAsync(mediator, $"Inbound {request.Id} dropped: replay admission MissingCreatedTime", "refused by the replay admission (FR-RR-08)");
            (await mediator.Mediation.GetGrantAsync(bob.Did, default)).Should().BeNull();
            mediator.LeakingLogLines().Should().BeEmpty();
            return;
        }

        response.Status.Should().Be(HttpStatusCode.OK);
        var grant = await OpenAnswerAsync(bob, response.Body, mediator.Did);
        ShouldBe20Grant(grant, request, mediator.Did);
        grant.From.Should().Be(mediator.Did, "Blocktrust's client takes the grant's from as the mediator DID");
        (await mediator.Mediation.GetGrantAsync(bob.Did, default))!.RoutingDids.Should().Equal(mediator.Did);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    // ---- EnableV2 = false --------------------------------------------------------------------------

    [Fact]
    public async Task With_EnableV2_off_a_2_0_request_is_not_answered_or_applied_and_only_3_0_is_disclosed()
    {
        var seen = new Qa1Recorder();
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Mediator = m => m.CoordinateMediation.EnableV2 = false;
            o.PostConfigureServices = seen.Register;
        });
        var bob = await mediator.NewPeerAsync();

        var request = Raw20(CoordinateMediation20.MediateRequestType, bob, mediator.Did, new JsonObject(), mediator.Clock.Now.ToUnixTimeSeconds(), returnRoute: true);
        var response = await PostAsync(mediator, await AuthcryptAsync(bob, request, mediator.Did));

        response.Status.Should().Be(HttpStatusCode.Accepted);
        response.Body.Should().BeEmpty();
        await WaitForLineAsync(mediator, $"No protocol handler registered for the type of inbound message {request.Id}", "admitted, then found no handler");
        seen.All.Should().ContainSingle(r => r.Id == request.Id, "admitted and dispatched");
        (await mediator.Mediation.GetGrantAsync(bob.Did, default)).Should().BeNull();

        DisclosedSet(await DiscloseAsync(mediator, bob)).Should().BeEquivalentTo(new[]
        {
            (CoordinateMediation.ProtocolUri, CoordinateMediation.MediatorRole),
        });
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task With_EnableV2_off_a_client_pinned_to_2_0_gets_an_immediate_TimeoutException_naming_its_request()
    {
        var seen = new Qa1Recorder();
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Mediator = m => m.CoordinateMediation.EnableV2 = false;
            o.PostConfigureServices = seen.Register;
        });
        // A clock the test never moves and a 10-minute request timeout: only the HTTP "no answer"
        // rule can end the call.
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.Time = new QaManualTimeProvider();
            o.Client = c =>
            {
                c.CoordinateMediationVersion = CoordinateMediationVersion.V2;
                c.RequestTimeout = TimeSpan.FromMinutes(10);
            };
        });
        var bob = await harness.NewPeerAsync();

        var thrown = (await FluentActions.Awaiting(() => harness.Mediation.RequestMediationAsync(mediator.Did, bob.Did).WaitAsync(Qa1.Wait))
            .Should().ThrowAsync<TimeoutException>()).Which;

        var sent = seen.From(bob.Did).Should().ContainSingle("a pinned version is not negotiated").Subject;
        sent.Type.Should().Be(CoordinateMediation20.MediateRequestType);
        thrown.Message.Should().Contain(sent.Id, "the client's own no-answer rule, not the test's hang guard").And.NotContain(bob.Did).And.NotContain(mediator.Did);
        (await mediator.Mediation.GetGrantAsync(bob.Did, default)).Should().BeNull();
        harness.Channel.PendingCount.Should().Be(0);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    // ---- the peer gate -----------------------------------------------------------------------------

    public enum Envelope
    {
        Anoncrypt,
        SignedOnly,
        AnoncryptOfSigned,
    }

    [Theory]
    [InlineData(Envelope.Anoncrypt)]
    [InlineData(Envelope.SignedOnly)]
    [InlineData(Envelope.AnoncryptOfSigned)]
    public async Task A_2_0_mediate_request_without_an_authcrypt_layer_is_neither_granted_nor_answered(Envelope envelope)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        var request = Raw20(CoordinateMediation20.MediateRequestType, bob, mediator.Did, new JsonObject(), mediator.Clock.Now.ToUnixTimeSeconds(), returnRoute: true);
        var (packed, mediaType) = envelope switch
        {
            Envelope.Anoncrypt => ((await bob.Client.PackEncryptedAsync(request, new PackEncryptedOptions(new[] { mediator.Did }))).Message, MediaTypes.Encrypted),
            Envelope.SignedOnly => (await bob.Client.PackSignedAsync(request, bob.Did), MediaTypes.Signed),
            _ => ((await bob.Client.PackEncryptedAsync(request, new PackEncryptedOptions(new[] { mediator.Did }, SignFrom: bob.Did))).Message, MediaTypes.Encrypted),
        };

        var response = await PostAsync(mediator, packed, mediaType);

        response.Status.Should().Be(HttpStatusCode.Accepted);
        response.Body.Should().BeEmpty();
        await WaitForLineAsync(mediator, $"Coordinate-mediation message {request.Id} dropped: no authcrypt sender", "the 2.0 handler's peer gate dropped it");
        mediator.Logs.CategorizedRecords.Should().Contain(r =>
            r.Category == typeof(CoordinateMediation20Handler).FullName && r.Text.Contains(request.Id, StringComparison.Ordinal));
        (await mediator.Mediation.GetGrantAsync(bob.Did, default)).Should().BeNull();
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    // ---- helpers -----------------------------------------------------------------------------------

    /// <summary>A 2.0 request as a wallet builds it, from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static Message Raw20(string type, TestPeer from, string to, JsonObject body, long? createdTime, bool returnRoute)
    {
        var builder = new MessageBuilder().WithType(type).WithFrom(from.Did).WithTo(to).WithBody(body);
        if (createdTime is long seconds)
            builder.WithCreatedTime(seconds);
        if (returnRoute)
            builder.WithReturnRoute(ReturnRouteMode.All);
        var message = builder.Build();
        message.AdditionalHeaders ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        return message;
    }

    /// <summary>Authcrypt <paramref name="message"/> from <paramref name="from"/> to <paramref name="to"/> (one of the mediator's routing DIDs), unchanged (no stamp, no header).</summary>
    private static async Task<string> AuthcryptAsync(TestPeer from, Message message, string to)
        => (await from.Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { to }, From: from.Did, Forward: false))).Message;

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(MediatorTestServer mediator, string packed, string mediaType = MediaTypes.Encrypted)
    {
        using var client = mediator.Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, mediator.Options.Pattern)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(packed)),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Open a <c>200</c> answer as <paramref name="peer"/>: authcrypted by <paramref name="from"/>, to the peer.</summary>
    private static async Task<Message> OpenAnswerAsync(TestPeer peer, string packed, string from)
    {
        var unpacked = await peer.Client.UnpackAsync(packed);
        unpacked.Authenticated.Should().BeTrue();
        unpacked.SenderKid.Should().StartWith(from + "#", "the answer comes from the DID the request was sent to");
        unpacked.Message.From.Should().Be(from);
        unpacked.Message.To.Should().Equal(peer.Did);
        return unpacked.Message;
    }

    /// <summary>A 2.0 <c>mediate-grant</c> on <paramref name="request"/>'s thread (its <c>thid</c>, else its <c>id</c>) whose <c>routing_did</c> is the JSON string <paramref name="routingDid"/>.</summary>
    private static void ShouldBe20Grant(Message grant, Message request, string routingDid)
    {
        grant.Type.Should().Be(CoordinateMediation20.MediateGrantType, "the canonical type string");
        grant.Thid.Should().Be(request.Thid ?? request.Id);
        var value = grant.Body!["routing_did"];
        value.Should().NotBeNull();
        value!.GetValueKind().Should().Be(JsonValueKind.String, "2.0 wallets read routing_did as a string");
        value.GetValue<string>().Should().Be(routingDid);
    }

    /// <summary>A Discover Features query for every Coordinate Mediation version, asked by <paramref name="peer"/>.</summary>
    private static async Task<IReadOnlyList<FeatureDisclosure>> DiscloseAsync(MediatorTestServer mediator, TestPeer peer)
    {
        var query = DiscoverFeatures.CreateQuery(peer.Did, mediator.Did, new FeatureQuery
        {
            FeatureType = DiscoverFeatures.FeatureTypeProtocol,
            Match = AnyCoordinateMediation,
        });
        var answer = await mediator.AskAsync(peer, query);
        answer.Message.Type.Should().Be(DiscoverFeatures.DiscloseType);
        return DiscoverFeatures.ReadDisclosures(answer.Message);
    }

    /// <summary>Each disclosed protocol with each of its roles (the order of disclosures is not defined).</summary>
    private static IEnumerable<(string Id, string Role)> DisclosedSet(IReadOnlyList<FeatureDisclosure> disclosures)
        => disclosures.SelectMany(d => (d.Roles ?? Array.Empty<string>()).Select(role => (d.Id, role)));

    private static Task WaitForLineAsync(MediatorTestServer mediator, string text, string because)
        => MediatorTestServer.WaitUntilAsync(() => mediator.Logs.Entries.Any(e => e.Contains(text, StringComparison.Ordinal)), because);

    /// <summary>
    /// A mediator with two routing DIDs: the test server's own first, then <paramref name="second"/>
    /// (a service at the same endpoint, its keys held), as the FR-MED-07 start-up checks require.
    /// </summary>
    private static async Task<MediatorTestServer> StartWithSecondRoutingDidAsync(PeerIdentity second)
    {
        var mediator = await MediatorTestServer.CreateAsync(o => o.Mediator = m => m.CoordinateMediation.RoutingDids.Add(second.Did));
        try
        {
            var secrets = (InMemorySecretsResolver)mediator.App.Services.GetRequiredService<ISecretsResolver>();
            foreach (var jwk in second.Privates)
                secrets.Add(jwk);
            mediator.AddSensitive(second.Did);
            foreach (var kid in ReturnRouteTestHost.KidsOf(second))
                mediator.AddSensitive(kid);
            await mediator.App.StartAsync();
            return mediator;
        }
        catch
        {
            await mediator.DisposeAsync();
            throw;
        }
    }
}
