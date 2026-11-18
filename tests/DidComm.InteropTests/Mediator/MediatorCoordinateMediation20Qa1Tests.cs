using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
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
using DidComm.Transports.ReturnRoute;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// QA-1 for the Coordinate Mediation 2.0 mediator role over the real endpoints (#99;
/// <c>AddMediator()</c> + <c>MapDidCommMediator</c> on a TestServer). A client pinned to 2.0 walks a
/// keylist over several clamped pages and reads exact pages (FR-MED-06). Discover Features answers
/// exact and wildcard Coordinate Mediation queries by whether 2.0 is on. Replay admission (FR-RR-08)
/// judges a 2.0 request by its protocol alone: stale and future-dated stamps are refused and the
/// edges admitted, a variant spelling of the type is in scope too, the same id is free for each
/// sender, and a captured <c>keylist-update</c> is applied once whether its copies come over HTTP or
/// the WebSocket. A 2.0 update or query without an authcrypt layer changes nothing and gets no
/// answer. The 2048-character bound on <c>recipient_did</c> holds at the endpoint. And no log line of a
/// whole 2.0 session, with 2.0 on or off, carries a DID or a value from a body.
/// </summary>
public sealed class MediatorCoordinateMediation20Qa1Tests
{
    private const string AnyCoordinateMediation = "https://didcomm.org/coordinate-mediation/*";
    private const string Planted = "PLANTED-QA1-MARKER";

    // ---- keylist-query pages and the client's walker (FR-MED-06) --------------------------------

    [Theory]
    [InlineData(0, 7)]
    [InlineData(1, 7)]
    [InlineData(7, 7)]
    [InlineData(8, 7)]
    [InlineData(20, 7)]
    [InlineData(250, 100)]
    public async Task A_client_pinned_to_2_0_walks_every_registered_did_once_over_the_mediators_clamped_pages(int registered, int maxPageSize)
    {
        var seen = new Qa1Recorder();
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Mediator = m => m.CoordinateMediation.MaxPageSize = maxPageSize;
            o.PostConfigureServices = seen.Register;
        });
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.Client = c => c.CoordinateMediationVersion = CoordinateMediationVersion.V2);
        var bob = await harness.NewPeerAsync();
        var mediation = harness.Mediation;
        (await mediation.RequestMediationAsync(mediator.Did, bob.Did)).Should().NotBeNull();
        var dids = Qa1.FakeDids(registered, "w");
        if (registered > 0)
        {
            (await mediation.UpdateRecipientsAsync(mediator.Did, bob.Did, dids.Select(d => new RecipientUpdate(d, RecipientUpdateAction.Add)).ToArray()))
                .Should().OnlyContain(r => r.Result == RecipientUpdateOutcome.Success);
        }

        // The walker asks for 100 a page; the mediator clamps to its own page size, and the walk
        // advances by what each page held: every DID once, in registration order.
        var walked = new List<string>();
        await foreach (var did in mediation.QueryAllRecipientsAsync(mediator.Did, bob.Did))
            walked.Add(did);
        walked.Should().Equal(dids);
        var pages = Math.Max(1, (registered + maxPageSize - 1) / maxPageSize);
        seen.Count(bob.Did, CoordinateMediation20.KeylistQueryType).Should().Be(pages, "one keylist-query per clamped page");
        seen.Count(bob.Did, CoordinateMediation.RecipientQueryType).Should().Be(0, "a client pinned to 2.0 never speaks 3.0");

        // Exact pages: limit 0, a limit over the page size, the last short page, an offset past the end.
        (await mediation.QueryRecipientsAsync(mediator.Did, bob.Did, new RecipientQueryPaging(0, 0)))
            .Should().BeEquivalentTo(new { Dids = Array.Empty<string>(), Pagination = new RecipientPagination(0, 0, registered) });
        var clamped = await mediation.QueryRecipientsAsync(mediator.Did, bob.Did, new RecipientQueryPaging(maxPageSize + 50, 0));
        clamped.Dids.Should().Equal(dids.Take(maxPageSize));
        clamped.Pagination.Should().Be(new RecipientPagination(Math.Min(maxPageSize, registered), 0, Math.Max(0, registered - maxPageSize)));
        var lastOffset = Math.Max(0, registered - 1);
        var last = await mediation.QueryRecipientsAsync(mediator.Did, bob.Did, new RecipientQueryPaging(3, lastOffset));
        last.Dids.Should().Equal(dids.Skip(lastOffset).Take(3));
        last.Pagination.Should().Be(new RecipientPagination(last.Dids.Count, lastOffset, 0));
        var past = await mediation.QueryRecipientsAsync(mediator.Did, bob.Did, new RecipientQueryPaging(5, registered + 5));
        past.Dids.Should().BeEmpty();
        past.Pagination.Should().Be(new RecipientPagination(0, registered + 5, 0), "remaining is 0 past the end, never negative");
        mediator.LeakingLogLines(dids).Should().BeEmpty();
    }

    // ---- Discover Features --------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Discover_features_answers_exact_and_wildcard_coordinate_mediation_queries_by_whether_2_0_is_on(bool enableV2)
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.CoordinateMediation.EnableV2 = enableV2);
        var bob = await mediator.NewPeerAsync();
        var both = enableV2
            ? new[] { (CoordinateMediation.ProtocolUri, CoordinateMediation.MediatorRole), (CoordinateMediation20.ProtocolUri, CoordinateMediation.MediatorRole) }
            : new[] { (CoordinateMediation.ProtocolUri, CoordinateMediation.MediatorRole) };

        (await DiscloseAsync(mediator, bob, CoordinateMediation20.ProtocolUri)).Should().BeEquivalentTo(
            enableV2 ? new[] { (CoordinateMediation20.ProtocolUri, CoordinateMediation.MediatorRole) } : Array.Empty<(string, string)>(),
            "an exact 2.0 query is answered with 2.0 in the mediator role, or with nothing when 2.0 is off");
        (await DiscloseAsync(mediator, bob, CoordinateMediation.ProtocolUri)).Should().BeEquivalentTo(
            new[] { (CoordinateMediation.ProtocolUri, CoordinateMediation.MediatorRole) }, "3.0 is disclosed either way");
        (await DiscloseAsync(mediator, bob, AnyCoordinateMediation)).Should().BeEquivalentTo(both);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    // ---- replay admission (FR-RR-08) ------------------------------------------------------------

    public static TheoryData<string, string?> Stamps => new()
    {
        { "older than Window + ClockSkew", "TooOld" },
        { "one second older than Window", "TooOld" },
        { "exactly Window old", null },
        { "one second beyond ClockSkew ahead", "FutureDated" },
        { "exactly ClockSkew ahead", null },
    };

    [Theory]
    [MemberData(nameof(Stamps))]
    public async Task A_2_0_mediate_request_without_return_route_is_judged_by_its_created_time(string stamp, string? refusal)
    {
        // Without return_route, the request is in the FR-RR-08 scope by its protocol alone.
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        var replay = new ReplayProtectionOptions();
        var now = mediator.Clock.Now.ToUnixTimeSeconds();
        var window = (long)replay.Window.TotalSeconds;
        var skew = (long)replay.ClockSkew.TotalSeconds;
        var created = stamp switch
        {
            "older than Window + ClockSkew" => now - window - skew - 1,
            "one second older than Window" => now - window - 1,
            "exactly Window old" => now - window,
            "one second beyond ClockSkew ahead" => now + skew + 1,
            _ => now + skew,
        };
        var request = Raw20(CoordinateMediation20.MediateRequestType, bob, mediator.Did, new JsonObject(), created, returnRoute: false);

        var response = await PostAsync(mediator, await AuthcryptAsync(bob, request, mediator.Did));

        response.Status.Should().Be(HttpStatusCode.Accepted, "no return route: nothing comes back over HTTP either way");
        response.Body.Should().BeEmpty();
        if (refusal is null)
        {
            await MediatorTestServer.WaitUntilAsync(() => mediator.Mediation.GetGrantAsync(bob.Did, default).AsTask().Result is not null, $"{stamp}: admitted and granted");
            mediator.Logs.Entries.Should().NotContain(e => e.Contains($"Inbound {request.Id} dropped", StringComparison.Ordinal));
        }
        else
        {
            await WaitForLineAsync(mediator, $"Inbound {request.Id} dropped: replay admission {refusal}", $"{stamp}: refused by the replay admission");
            (await mediator.Mediation.GetGrantAsync(bob.Did, default)).Should().BeNull($"{stamp}: never dispatched");
        }
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://didcomm.org/coordinate-mediation/2.3/mediate-request")]
    [InlineData("https://didcomm.org/Coordinate_Mediation/2.0/MEDIATE_REQUEST")]
    [InlineData("HTTPS://DIDCOMM.ORG/coordinate-mediation/2.0/Mediate-Request")]
    public async Task A_type_spelling_the_2_0_handler_answers_is_in_the_replay_scope_too(string type)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();

        // Unstamped and without return_route: only the protocol can bring it into scope.
        var unstamped = Raw20(type, bob, mediator.Did, new JsonObject(), createdTime: null, returnRoute: false);
        (await PostAsync(mediator, await AuthcryptAsync(bob, unstamped, mediator.Did))).Status.Should().Be(HttpStatusCode.Accepted);
        await WaitForLineAsync(mediator, $"Inbound {unstamped.Id} dropped: replay admission MissingCreatedTime", "a spelling the handler serves is judged as 2.0");
        (await mediator.Mediation.GetGrantAsync(bob.Did, default)).Should().BeNull();

        // Positive control: the same spelling, stamped and with a return route, reaches the 2.0 handler.
        var stamped = Raw20(type, bob, mediator.Did, new JsonObject(), mediator.Clock.Now.ToUnixTimeSeconds(), returnRoute: true);
        var granted = await PostAsync(mediator, await AuthcryptAsync(bob, stamped, mediator.Did));
        granted.Status.Should().Be(HttpStatusCode.OK);
        (await OpenAnswerAsync(bob, granted.Body, mediator.Did)).Type.Should().Be(CoordinateMediation20.MediateGrantType);
    }

    [Fact]
    public async Task The_same_2_0_message_id_is_admitted_once_per_sender()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        var carol = await mediator.NewPeerAsync();
        const string sharedId = "qa1-shared-message-id";

        foreach (var peer in new[] { bob, carol })
        {
            var request = Raw20(CoordinateMediation20.MediateRequestType, peer, mediator.Did, new JsonObject(), mediator.Clock.Now.ToUnixTimeSeconds(), returnRoute: true);
            request.Id = sharedId;
            var response = await PostAsync(mediator, await AuthcryptAsync(peer, request, mediator.Did));
            response.Status.Should().Be(HttpStatusCode.OK, "the admission key is (sender, id): another sender's use of the id does not count");
            (await OpenAnswerAsync(peer, response.Body, mediator.Did)).Type.Should().Be(CoordinateMediation20.MediateGrantType);
        }

        // A fresh envelope (new ciphertext) carrying Bob's id again is still his duplicate.
        var again = Raw20(CoordinateMediation20.MediateRequestType, bob, mediator.Did, new JsonObject(), mediator.Clock.Now.ToUnixTimeSeconds(), returnRoute: true);
        again.Id = sharedId;
        var dropped = await PostAsync(mediator, await AuthcryptAsync(bob, again, mediator.Did));
        dropped.Status.Should().Be(HttpStatusCode.Accepted);
        dropped.Body.Should().BeEmpty();
        await WaitForLineAsync(mediator, $"Inbound {sharedId} dropped: replay admission Duplicate", "Bob's second use of the id");
        mediator.Logs.Entries.Count(e => e.Contains($"Inbound {sharedId} dropped", StringComparison.Ordinal)).Should().Be(1, "Carol's use was admitted");
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    public enum Route
    {
        HttpThenHttp,
        SocketThenSocket,
        HttpThenSocket,
        SocketThenHttp,
    }

    [Theory]
    [InlineData(Route.HttpThenHttp)]
    [InlineData(Route.SocketThenSocket)]
    [InlineData(Route.HttpThenSocket)]
    [InlineData(Route.SocketThenHttp)]
    public async Task A_captured_2_0_keylist_update_replayed_after_a_remove_does_not_register_the_did_again(Route route)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        var x = await mediator.NewRecipientAsync();
        var enrol = Raw20(CoordinateMediation20.MediateRequestType, bob, mediator.Did, new JsonObject(), mediator.Clock.Now.ToUnixTimeSeconds(), returnRoute: true);
        (await PostAsync(mediator, await AuthcryptAsync(bob, enrol, mediator.Did))).Status.Should().Be(HttpStatusCode.OK);

        // Stamped and without return_route (as the Identus SDK sends it): in scope by its protocol.
        var add = Raw20(CoordinateMediation20.KeylistUpdateType, bob, mediator.Did, Updates((x.Did, "add")), mediator.Clock.Now.ToUnixTimeSeconds(), returnRoute: false);
        var captured = await AuthcryptAsync(bob, add, mediator.Did);
        using var socket = await mediator.ConnectAsync();

        await DeliverAsync(mediator, socket, captured, overSocket: route is Route.SocketThenSocket or Route.SocketThenHttp);
        await MediatorTestServer.WaitUntilAsync(() => mediator.Mediation.FindGrantByRecipientDidAsync(x.Did, default).AsTask().Result is not null, "the first copy is applied");

        var remove = await mediator.AskAsync(bob, CoordinateMediation20.CreateKeylistUpdate(bob.Did, mediator.Did, new[] { new RecipientUpdate(x.Did, RecipientUpdateAction.Remove) }));
        CoordinateMediation20.TryParseKeylistUpdateResponse(remove.Message, out var removed).Should().BeTrue();
        removed!.Should().ContainSingle().Which.Result.Should().Be(RecipientUpdateOutcome.Success);

        await DeliverAsync(mediator, socket, captured, overSocket: route is Route.SocketThenSocket or Route.HttpThenSocket);
        await WaitForLineAsync(mediator, $"Inbound {add.Id} dropped: replay admission Duplicate", "one replay memory serves HTTP and the WebSocket");
        (await mediator.Mediation.FindGrantByRecipientDidAsync(x.Did, default)).Should().BeNull("the replay did not register the DID again");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        mediator.LeakingLogLines(x.Did).Should().BeEmpty();
    }

    // ---- the peer gate at the endpoint ------------------------------------------------------------

    public enum Envelope
    {
        Plaintext,
        Anoncrypt,
        SignedOnly,
        AnoncryptOfSigned,
    }

    public static TheoryData<Envelope, string> UnauthenticatedRequests
    {
        get
        {
            var data = new TheoryData<Envelope, string>();
            foreach (var envelope in Enum.GetValues<Envelope>())
            {
                data.Add(envelope, "keylist-update");
                data.Add(envelope, "keylist-query");
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(UnauthenticatedRequests))]
    public async Task A_2_0_keylist_request_without_an_authcrypt_layer_changes_nothing_and_gets_no_answer(Envelope envelope, string kind)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        var held = await mediator.NewRecipientAsync();
        var other = await mediator.NewRecipientAsync();
        var enrol = Raw20(CoordinateMediation20.MediateRequestType, bob, mediator.Did, new JsonObject(), mediator.Clock.Now.ToUnixTimeSeconds(), returnRoute: true);
        (await PostAsync(mediator, await AuthcryptAsync(bob, enrol, mediator.Did))).Status.Should().Be(HttpStatusCode.OK);
        var granted = await mediator.AskAsync(bob, CoordinateMediation20.CreateKeylistUpdate(bob.Did, mediator.Did, new[] { new RecipientUpdate(held.Did, RecipientUpdateAction.Add) }));
        granted.Message.Type.Should().Be(CoordinateMediation20.KeylistUpdateResponseType);

        // Bob's own request, as he would send it but for the envelope: stamped, return_route all.
        var body = kind == "keylist-update" ? Updates((held.Did, "remove"), (other.Did, "add")) : new JsonObject();
        var type = kind == "keylist-update" ? CoordinateMediation20.KeylistUpdateType : CoordinateMediation20.KeylistQueryType;
        var request = Raw20(type, bob, mediator.Did, body, mediator.Clock.Now.ToUnixTimeSeconds(), returnRoute: true);
        var (packed, mediaType) = envelope switch
        {
            Envelope.Plaintext => (await bob.Client.PackPlaintextAsync(request), MediaTypes.Plaintext),
            Envelope.Anoncrypt => ((await bob.Client.PackEncryptedAsync(request, new PackEncryptedOptions(new[] { mediator.Did }))).Message, MediaTypes.Encrypted),
            Envelope.SignedOnly => (await bob.Client.PackSignedAsync(request, bob.Did), MediaTypes.Signed),
            _ => ((await bob.Client.PackEncryptedAsync(request, new PackEncryptedOptions(new[] { mediator.Did }, SignFrom: bob.Did))).Message, MediaTypes.Encrypted),
        };

        var response = await PostAsync(mediator, packed, mediaType);

        response.Body.Should().BeEmpty($"{envelope} {kind}: nothing is answered");
        response.Status.Should().Be(HttpStatusCode.Accepted);
        await WaitForLineAsync(mediator, $"Coordinate-mediation message {request.Id} dropped: no authcrypt sender", "the 2.0 handler's peer gate dropped it");
        (await mediator.Mediation.FindGrantByRecipientDidAsync(held.Did, default))!.PeerDid.Should().Be(bob.Did, "nothing was removed");
        (await mediator.Mediation.FindGrantByRecipientDidAsync(other.Did, default)).Should().BeNull("nothing was added");
        mediator.LeakingLogLines(held.Did, other.Did).Should().BeEmpty();
    }

    // ---- FR-API-09 at the endpoint -----------------------------------------------------------

    [Fact]
    public async Task Over_the_endpoint_a_2048_character_recipient_did_is_registered_and_echoed_and_one_of_2049_changes_nothing_and_reaches_no_log()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        var enrol = Raw20(CoordinateMediation20.MediateRequestType, bob, mediator.Did, new JsonObject(), mediator.Clock.Now.ToUnixTimeSeconds(), returnRoute: true);
        (await PostAsync(mediator, await AuthcryptAsync(bob, enrol, mediator.Did))).Status.Should().Be(HttpStatusCode.OK);
        var atLimit = "did:example:" + new string('a', 2048 - "did:example:".Length);
        var overLimit = "did:example:SENTINEL" + new string('q', 2049 - "did:example:SENTINEL".Length);

        var refused = Raw20(CoordinateMediation20.KeylistUpdateType, bob, mediator.Did, Updates((overLimit, "add")), mediator.Clock.Now.ToUnixTimeSeconds(), returnRoute: true);
        var refusedResponse = await PostAsync(mediator, await AuthcryptAsync(bob, refused, mediator.Did));
        refusedResponse.Status.Should().Be(HttpStatusCode.BadRequest, "a malformed request from an authenticated peer is refused, not answered");
        refusedResponse.Body.Should().NotContain("SENTINEL");
        (await mediator.Mediation.ListRecipientsAsync(bob.Did, 0, 10, default)).Dids.Should().BeEmpty();

        var accepted = Raw20(CoordinateMediation20.KeylistUpdateType, bob, mediator.Did, Updates((atLimit, "add")), mediator.Clock.Now.ToUnixTimeSeconds(), returnRoute: true);
        var acceptedResponse = await PostAsync(mediator, await AuthcryptAsync(bob, accepted, mediator.Did));
        acceptedResponse.Status.Should().Be(HttpStatusCode.OK);
        var answer = await OpenAnswerAsync(bob, acceptedResponse.Body, mediator.Did);
        CoordinateMediation20.TryParseKeylistUpdateResponse(answer, out var updated).Should().BeTrue();
        updated.Should().Equal(new RecipientUpdateResult(atLimit, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success));
        (await mediator.Mediation.ListRecipientsAsync(bob.Did, 0, 10, default)).Dids.Should().Equal(atLimit);

        mediator.Logs.Entries.Should().NotContain(e => e.Contains("SENTINEL", StringComparison.Ordinal) || e.Contains("aaaaaaaa", StringComparison.Ordinal));
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    // ---- logs -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_log_line_of_a_whole_2_0_session_carries_a_did_or_a_body_value(bool enableV2)
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.CoordinateMediation.EnableV2 = enableV2);
        var bob = await mediator.NewPeerAsync();
        var mallory = await mediator.NewPeerAsync();
        var x = await mediator.NewRecipientAsync();
        var start = mediator.Logs.Entries.Count;
        long Now() => mediator.Clock.Now.ToUnixTimeSeconds();
        JsonObject Plant(JsonObject body)
        {
            body[Planted] = Planted + ":did:example:planted";
            return body;
        }

        // Every 2.0 path that writes a line: grant, updates (success, DID URL, the mediator's own DID,
        // another peer's DID), a query, an ignored answer type, an unknown name, an unauthenticated
        // request, a malformed body, an update with no return route, a request without created_time,
        // and a replay.
        async Task<(HttpStatusCode Status, string Body)> SendAsync(TestPeer from, string type, JsonObject body, long? created, bool returnRoute)
            => await PostAsync(mediator, await AuthcryptAsync(from, Raw20(type, from, mediator.Did, body, created, returnRoute), mediator.Did));

        await SendAsync(bob, CoordinateMediation20.MediateRequestType, Plant(new JsonObject()), Now(), returnRoute: true);
        await SendAsync(mallory, CoordinateMediation20.MediateRequestType, new JsonObject(), Now(), returnRoute: true);
        await SendAsync(bob, CoordinateMediation20.KeylistUpdateType,
            Plant(Updates((x.Did, "add"), (x.Did + "#" + Planted, "add"), (mediator.Did, "add"), ("did:example:" + Planted, "remove"))), Now(), returnRoute: true);
        await SendAsync(mallory, CoordinateMediation20.KeylistUpdateType, Updates((x.Did, "add"), (x.Did, "remove")), Now(), returnRoute: true);
        await SendAsync(bob, CoordinateMediation20.KeylistQueryType,
            Plant(new JsonObject { ["paginate"] = new JsonObject { ["limit"] = 1, ["offset"] = 0 } }), Now(), returnRoute: true);
        await SendAsync(bob, CoordinateMediation20.MediateGrantType, Plant(new JsonObject { ["routing_did"] = "did:example:" + Planted }), Now(), returnRoute: true);
        await SendAsync(bob, CoordinateMediation20.ProtocolUri + "/" + Planted.ToLowerInvariant(), Plant(new JsonObject()), Now(), returnRoute: true);
        await SendAsync(bob, CoordinateMediation20.KeylistUpdateType, Plant(new JsonObject { ["updates"] = "did:example:" + Planted }), Now(), returnRoute: true);
        await SendAsync(bob, CoordinateMediation20.KeylistUpdateType, Updates(("did:example:no-return-route-" + Planted, "add")), Now(), returnRoute: false);
        await SendAsync(bob, CoordinateMediation20.KeylistQueryType, Plant(new JsonObject()), created: null, returnRoute: true);
        var anoncrypt = Raw20(CoordinateMediation20.KeylistQueryType, bob, mediator.Did, Plant(new JsonObject()), Now(), returnRoute: true);
        await PostAsync(mediator, (await bob.Client.PackEncryptedAsync(anoncrypt, new PackEncryptedOptions(new[] { mediator.Did }))).Message);
        var replayed = Raw20(CoordinateMediation20.KeylistQueryType, bob, mediator.Did, Plant(new JsonObject()), Now(), returnRoute: true);
        var replayedEnvelope = await AuthcryptAsync(bob, replayed, mediator.Did);
        await PostAsync(mediator, replayedEnvelope);
        await PostAsync(mediator, replayedEnvelope);
        await WaitForLineAsync(mediator, $"Inbound {replayed.Id} dropped: replay admission Duplicate", "the last request has been judged");
        if (enableV2)
        {
            await WaitForLineAsync(mediator, $"Coordinate-mediation message {anoncrypt.Id} dropped: no authcrypt sender", "the peer gate ran");
            (await mediator.Mediation.FindGrantByRecipientDidAsync(x.Did, default))!.PeerDid.Should().Be(bob.Did, "positive control: the session did its work");
        }
        else
        {
            (await mediator.Mediation.GetGrantAsync(bob.Did, default)).Should().BeNull("with 2.0 off nothing is applied");
        }

        var lines = mediator.Logs.Entries.Skip(start).ToArray();
        lines.Should().NotBeEmpty();
        lines.Should().NotContain(l => l.Contains("did:", StringComparison.OrdinalIgnoreCase), "no line, at any level, carries a DID, a key id or a recipient_did");
        lines.Should().NotContain(l => l.Contains(Planted, StringComparison.OrdinalIgnoreCase), "no value from a message, body or type, reaches a log line");
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static JsonObject Updates(params (string Did, string Action)[] entries)
    {
        var array = new JsonArray();
        foreach (var (did, action) in entries)
            array.Add(new JsonObject { ["recipient_did"] = did, ["action"] = action });
        return new JsonObject { ["updates"] = array };
    }

    /// <summary>A 2.0 request as a wallet builds it, from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static Message Raw20(string type, TestPeer from, string to, JsonObject body, long? createdTime, bool returnRoute)
    {
        var builder = new MessageBuilder().WithType(type).WithFrom(from.Did).WithTo(to).WithBody(body);
        if (createdTime is long seconds)
            builder.WithCreatedTime(seconds);
        if (returnRoute)
            builder.WithReturnRoute(ReturnRouteMode.All);
        return builder.Build();
    }

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

    /// <summary>Send <paramref name="packed"/> once, over HTTP (expecting 202: no return route) or on <paramref name="socket"/>.</summary>
    private static async Task DeliverAsync(MediatorTestServer mediator, WebSocket socket, string packed, bool overSocket)
    {
        if (overSocket)
        {
            await ReturnRouteTestHost.SendAsync(socket, packed);
            return;
        }
        var response = await PostAsync(mediator, packed);
        response.Status.Should().Be(HttpStatusCode.Accepted);
        response.Body.Should().BeEmpty();
    }

    private static async Task<Message> OpenAnswerAsync(TestPeer peer, string packed, string from)
    {
        var unpacked = await peer.Client.UnpackAsync(packed);
        unpacked.Authenticated.Should().BeTrue();
        unpacked.Message.From.Should().Be(from);
        unpacked.Message.To.Should().Equal(peer.Did);
        return unpacked.Message;
    }

    /// <summary>The (protocol, role) pairs disclosed for one Discover Features protocol query (order is not defined).</summary>
    private static async Task<IReadOnlyList<(string Id, string Role)>> DiscloseAsync(MediatorTestServer mediator, TestPeer peer, string match)
    {
        var query = DiscoverFeatures.CreateQuery(peer.Did, mediator.Did, new FeatureQuery { FeatureType = DiscoverFeatures.FeatureTypeProtocol, Match = match });
        var answer = await mediator.AskAsync(peer, query);
        answer.Message.Type.Should().Be(DiscoverFeatures.DiscloseType);
        answer.Message.Thid.Should().Be(query.Id);
        return DiscoverFeatures.ReadDisclosures(answer.Message)
            .SelectMany(d => (d.Roles ?? Array.Empty<string>()).DefaultIfEmpty("(no roles)").Select(role => (d.Id, role)))
            .ToArray();
    }

    private static Task WaitForLineAsync(MediatorTestServer mediator, string text, string because)
        => MediatorTestServer.WaitUntilAsync(() => mediator.Logs.Entries.Any(e => e.Contains(text, StringComparison.Ordinal)), because);
}
