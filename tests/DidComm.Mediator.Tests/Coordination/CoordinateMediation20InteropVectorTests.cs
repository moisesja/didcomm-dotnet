using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using DidComm.Json;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using Xunit;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>
/// The Coordinate Mediation 2.0 requests that wallets in the field send, answered by
/// <see cref="CoordinateMediation20Handler"/> through the real dispatcher (FR-MED-10; rows I-1,
/// I-4, I-5, I-7, I-12, I-13 and I-14 of the #99 interop evidence). Each reply carries the exact
/// canonical type string (those clients compare types by string), <c>routing_did</c> as a JSON
/// string, <c>from</c> = the mediator DID the request was sent to, <c>to</c> = the wallet, and
/// <c>thid</c> = the request's <c>thid</c>, else its <c>id</c>. The scala-did parser vectors are
/// read by the 2.0 codec as the mediator and the client read them.
/// </summary>
/// <remarks>
/// The templates are transcribed from these sources (read only: nothing there was built or run).
/// The sender and mediator DIDs are replaced by DIDs whose keys the test holds, so each request
/// can be authcrypted as those wallets send it; every other member is kept.
/// <list type="bullet">
/// <item><description>Identus TypeScript edge agent, <c>hyperledger/identus-edge-agent-sdk-ts@4bf86eb</c>:
/// <c>packages/lib/sdk/src/plugins/internal/didcomm/protocols/mediation/MediationRequest.ts</c> and
/// <c>MediationKeysUpdateList.ts</c> (the bodies), <c>packages/wasm/didcomm/src/Wrapper.ts</c> (the
/// headers: <c>typ</c>, <c>created_time</c> in seconds, <c>thid</c>, <c>pthid</c>, and
/// <c>return_route: all</c> on <c>mediate-request</c> only).</description></item>
/// <item><description>Identus mediator test vectors, <c>hyperledger/identus-mediator@5664f1d</c>:
/// <c>mediator/src/test/scala/org/hyperledger/identus/protocols/MessageSetup.scala</c>; its
/// <c>keylist-update</c> vector takes the recipient DID as a parameter, so the did:peer:2 used here
/// is the one its suite declares in <c>mediator/src/test/scala/org/hyperledger/identus/db/DidAccountStubSetup.scala</c>.</description></item>
/// <item><description>scala-did v0.1.2 (<c>FabioPinheiro/scala-did@dc7f1c6</c>), the codecs of the
/// Identus mediator:
/// <c>did-comm-protocols/shared/src/test/scala/fmgp/did/comm/protocol/mediatorcoordination2/MediatorCoordination2Suite.scala</c>.</description></item>
/// <item><description>Blocktrust mediator client, <c>bsandmann/blocktrust.Mediator@36b8dfd</c>:
/// <c>Blocktrust.Mediator.Client/Commands/MediatorCoordinator/RequestMediation/RequestMediationHandler.cs</c>
/// and <c>QueryKeys/QueryMediatorKeysHandler.cs</c>, with the members and order that
/// <c>bsandmann/blocktrust.DIDComm@9a7a19a</c>'s <c>Message.ToJSONObject</c> writes (nulls dropped).</description></item>
/// </list>
/// </remarks>
public sealed class CoordinateMediation20InteropVectorTests
{
    private const string GrantType = "https://didcomm.org/coordinate-mediation/2.0/mediate-grant";
    private const string KeylistUpdateResponseType = "https://didcomm.org/coordinate-mediation/2.0/keylist-update-response";
    private const string KeylistType = "https://didcomm.org/coordinate-mediation/2.0/keylist";

    // A did:peer:2 from the Identus mediator's test suite (DidAccountStubSetup.scala, `alice`).
    private const string IdentusSuiteDid = "did:peer:2.Ez6LSghwSE437wnDE1pt3X6hVDUQzSjsHzinpX3XFvMjRAm7y.Vz6Mkhh1e5CEYYq6JBUcTZ6Cp2ranCWRrv7Yax3Le4N59R6dd.SeyJ0IjoiZG0iLCJzIjp7InVyaSI6Imh0dHA6Ly9sb2NhbGhvc3Q6ODA4MCIsImEiOlsiZGlkY29tbS92MiJdfX0";

    // The recipient_did of scala-did's keylist-update vector: a did:peer:2 with an encoded service.
    private const string ScalaDidRecipientDid = "did:peer:2.Ez6LSrLYsJ7z4hUuDwLPLoKUJZPt64nv6oqQSPj4AY9TMWPMk.Vz6MkkaimikhRgvoots6ei1d7aq6xS4EZT6gd1UgRQD4uby4W.SeyJyIjpbXSwicyI6ImRpZDpwZWVyOjIuRXo2TFNnaHdTRTQzN3duREUxcHQzWDZoVkRVUXpTanNIemlucFgzWEZ2TWpSQW03eS5WejZNa2hoMWU1Q0VZWXE2SkJVY1RaNkNwMnJhbkNXUnJ2N1lheDNMZTRONTlSNmRkLlNleUowSWpvaVpHMGlMQ0p6SWpvaWFIUjBjSE02THk5aGJHbGpaUzVrYVdRdVptMW5jQzVoY0hBdklpd2ljaUk2VzEwc0ltRWlPbHNpWkdsa1kyOXRiUzkyTWlKZGZRIiwiYSI6W10sInQiOiJkbSJ9";

    // Identus edge agent (Wrapper.ts packEncrypted over MediationRequest.makeMessage()). thid and
    // pthid are set here, different from id, to pin DD-11.
    private const string IdentusSdkMediateRequest = """
        {"id":"{ID}","type":"https://didcomm.org/coordinate-mediation/2.0/mediate-request","typ":"application/didcomm-plain+json","from":"{PEER}","to":["{MEDIATOR}"],"created_time":{CREATED},"thid":"{THID}","pthid":"{PTHID}","body":{},"return_route":"all"}
        """;

    // Identus edge agent keylist-update (MediationKeysUpdateList: add only). Wrapper.ts adds
    // return_route to mediate-request only, so this one goes without it (I-4).
    private const string IdentusSdkKeylistUpdate = """
        {"id":"{ID}","type":"https://didcomm.org/coordinate-mediation/2.0/keylist-update","typ":"application/didcomm-plain+json","from":"{PEER}","to":["{MEDIATOR}"],"created_time":{CREATED},"body":{"updates":[{"recipient_did":"{RECIPIENT}","action":"add"}]}}
        """;

    // Identus mediator, MessageSetup.scala plaintextMediationRequestMessage (verbatim but the DIDs).
    private const string IdentusMediatorMediateRequest = """
        {
          "id" : "17f9f122-f762-4ba8-9011-39b9e7efb177",
          "type" : "https://didcomm.org/coordinate-mediation/2.0/mediate-request",
          "to" : [
             "{MEDIATOR}"
          ],
          "from" : "{PEER}",
          "body" : {},
          "return_route" : "all",
          "typ" : "application/didcomm-plain+json"
        }
        """;

    // Blocktrust client RequestMediationHandler: body {}, return_route, and the same header again
    // inside a top-level custom_headers array; no created_time, no thid.
    private const string BlocktrustMediateRequest = """
        {"id":"{ID}","typ":"application/didcomm-plain+json","type":"https://didcomm.org/coordinate-mediation/2.0/mediate-request","from":"{PEER}","to":["{MEDIATOR}"],"body":{},"return_route":"all","custom_headers":[{"return_route":"all"}]}
        """;

    // Blocktrust client QueryMediatorKeysHandler: the same shape, with no paginate.
    private const string BlocktrustKeylistQuery = """
        {"id":"{ID}","typ":"application/didcomm-plain+json","type":"https://didcomm.org/coordinate-mediation/2.0/keylist-query","from":"{PEER}","to":["{MEDIATOR}"],"body":{},"return_route":"all","custom_headers":[{"return_route":"all"}]}
        """;

    // scala-did MediatorCoordination2Suite.scala, msgKeylistUpdateExample (verbatim).
    private const string ScalaDidKeylistUpdate = """
        {
          "id" : "3687298f-7830-4e43-87e2-ea67c1ae7f85",
          "type" : "https://didcomm.org/coordinate-mediation/2.0/keylist-update",
          "to" : [
            "did:peer:2.Ez6LSghwSE437wnDE1pt3X6hVDUQzSjsHzinpX3XFvMjRAm7y.Vz6Mkhh1e5CEYYq6JBUcTZ6Cp2ranCWRrv7Yax3Le4N59R6dd.SeyJ0IjoiZG0iLCJzIjoiaHR0cHM6Ly9hbGljZS5kaWQuZm1ncC5hcHAvIiwiciI6W10sImEiOlsiZGlkY29tbS92MiJdfQ"
          ],
          "from" : "did:peer:2.Ez6LSqZd7Zrp9LcHBEiyMLvKVUxFYb3hNWRxq8cCXbfx4zs6u.Vz6MksTvq16LvtvSqTg8ru7XEXf65Um8WKP7Zm3Vrv4incAhb.SW10",
          "created_time" : 1683026354666,
          "body" : {
            "updates" : [
              {
                "action" : "add",
                "recipient_did" : "did:peer:2.Ez6LSrLYsJ7z4hUuDwLPLoKUJZPt64nv6oqQSPj4AY9TMWPMk.Vz6MkkaimikhRgvoots6ei1d7aq6xS4EZT6gd1UgRQD4uby4W.SeyJyIjpbXSwicyI6ImRpZDpwZWVyOjIuRXo2TFNnaHdTRTQzN3duREUxcHQzWDZoVkRVUXpTanNIemlucFgzWEZ2TWpSQW03eS5WejZNa2hoMWU1Q0VZWXE2SkJVY1RaNkNwMnJhbkNXUnJ2N1lheDNMZTRONTlSNmRkLlNleUowSWpvaVpHMGlMQ0p6SWpvaWFIUjBjSE02THk5aGJHbGpaUzVrYVdRdVptMW5jQzVoY0hBdklpd2ljaUk2VzEwc0ltRWlPbHNpWkdsa1kyOXRiUzkyTWlKZGZRIiwiYSI6W10sInQiOiJkbSJ9"
              }
            ]
          },
          "attachments" : []
        }
        """;

    // scala-did MediatorCoordination2Suite.scala, msgKeylistQueryExample (verbatim).
    private const string ScalaDidKeylistQuery = """
        {
          "id": "1234567890",
          "type": "https://didcomm.org/coordinate-mediation/2.0/keylist-query",
          "body": {"paginate": {"limit": 30,"offset": 0}},

          "to" : ["did:peer:2.Ez6LSghwSE437wnDE1pt3X6hVDUQzSjsHzinpX3XFvMjRAm7y.Vz6Mkhh1e5CEYYq6JBUcTZ6Cp2ranCWRrv7Yax3Le4N59R6dd.SeyJ0IjoiZG0iLCJzIjoiaHR0cHM6Ly9hbGljZS5kaWQuZm1ncC5hcHAvIiwiciI6W10sImEiOlsiZGlkY29tbS92MiJdfQ"],
          "from" : "did:peer:2.Ez6LSqZd7Zrp9LcHBEiyMLvKVUxFYb3hNWRxq8cCXbfx4zs6u.Vz6MksTvq16LvtvSqTg8ru7XEXf65Um8WKP7Zm3Vrv4incAhb.SW10"
        }
        """;

    // scala-did MediatorCoordination2Suite.scala, msgKeylistExample (verbatim).
    private const string ScalaDidKeylist = """
        {
          "id": "1234567891",
          "thid": "1234567890",
          "type": "https://didcomm.org/coordinate-mediation/2.0/keylist",
          "body": {
            "keys": [{"recipient_did": "did:peer:2.Ez6LSqZd7Zrp9LcHBEiyMLvKVUxFYb3hNWRxq8cCXbfx4zs6u.Vz6MksTvq16LvtvSqTg8ru7XEXf65Um8WKP7Zm3Vrv4incAhb.SW10"}],
            "pagination": {"count": 30,"offset": 30,"remaining": 100}
          },

          "to" : ["did:peer:2.Ez6LSghwSE437wnDE1pt3X6hVDUQzSjsHzinpX3XFvMjRAm7y.Vz6Mkhh1e5CEYYq6JBUcTZ6Cp2ranCWRrv7Yax3Le4N59R6dd.SeyJ0IjoiZG0iLCJzIjoiaHR0cHM6Ly9hbGljZS5kaWQuZm1ncC5hcHAvIiwiciI6W10sImEiOlsiZGlkY29tbS92MiJdfQ"],
          "from" : "did:peer:2.Ez6LSqZd7Zrp9LcHBEiyMLvKVUxFYb3hNWRxq8cCXbfx4zs6u.Vz6MksTvq16LvtvSqTg8ru7XEXf65Um8WKP7Zm3Vrv4incAhb.SW10"
        }
        """;

    private static Message Vector(string template, string peer, string mediator, params (string Name, string Value)[] values)
    {
        var json = template.Replace("{PEER}", peer, StringComparison.Ordinal).Replace("{MEDIATOR}", mediator, StringComparison.Ordinal);
        foreach (var (name, value) in values)
            json = json.Replace("{" + name + "}", value, StringComparison.Ordinal);
        return JsonSerializer.Deserialize<Message>(json, DidCommJson.Default)!;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    // What a 2.0 client reads: the reply as JSON, the way it travels inside the envelope.
    private static JsonElement Wire(Message reply) => JsonDocument.Parse(JsonSerializer.Serialize(reply, DidCommJson.Default)).RootElement;

    private static void ShouldBeGrant(DispatchOutcome outcome, string mediator, string wallet, string thid)
    {
        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        outcome.Handler.Should().BeOfType<CoordinateMediation20Handler>();
        var wire = Wire(outcome.Reply!);
        wire.GetProperty("type").GetString().Should().Be(GrantType, "Identus and Blocktrust compare the type by exact string (I-14)");
        var routingDid = wire.GetProperty("body").GetProperty("routing_did");
        routingDid.ValueKind.Should().Be(JsonValueKind.String, "both clients read routing_did as a string (I-1)");
        routingDid.GetString().Should().Be(mediator, "no routing DID is configured: the DID the request was sent to");
        wire.GetProperty("from").GetString().Should().Be(mediator, "the Identus agent takes the grant's from as the mediator's DID (I-15)");
        wire.GetProperty("to").EnumerateArray().Select(e => e.GetString()).Should().Equal(wallet);
        wire.GetProperty("thid").GetString().Should().Be(thid);
    }

    private static async Task<(HandlerFixture Fx, string Mediator, string Wallet)> EnrolledIdentusWalletAsync()
    {
        var fx = HandlerFixture.Create();
        var mediator = (await fx.NewPeerAsync()).Did;
        var wallet = (await fx.NewPeerAsync()).Did;
        var request = Vector(IdentusSdkMediateRequest, wallet, mediator, ("ID", Guid.NewGuid().ToString()), ("THID", Guid.NewGuid().ToString()), ("PTHID", Guid.NewGuid().ToString()), ("CREATED", Now()));
        (await fx.SendAsync(request, mediator)).Reply!.Type.Should().Be(GrantType);
        return (fx, mediator, wallet);
    }

    // ---- mediate-request ------------------------------------------------------------------

    [Fact]
    public async Task The_Identus_edge_agent_mediate_request_is_granted_on_its_own_thread()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = (await fx.NewPeerAsync()).Did;
        var wallet = (await fx.NewPeerAsync()).Did;
        var id = Guid.NewGuid().ToString();
        var thid = "thread-" + Guid.NewGuid();
        var request = Vector(IdentusSdkMediateRequest, wallet, mediator, ("ID", id), ("THID", thid), ("PTHID", "parent-" + Guid.NewGuid()), ("CREATED", Now()));
        request.Thid.Should().Be(thid).And.NotBe(request.Id);
        request.Pthid.Should().NotBeNull();
        request.CreatedTime.Should().NotBeNull();
        request.AdditionalHeaders!["return_route"].GetString().Should().Be("all");

        var outcome = await fx.SendAsync(request, mediator);

        ShouldBeGrant(outcome, mediator, wallet, thid: thid);
        (await fx.Memory.GetGrantAsync(wallet, default))!.RoutingDids.Should().Equal(mediator);
    }

    [Fact]
    public async Task The_Identus_mediator_test_vector_mediate_request_is_granted_on_its_id()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = (await fx.NewPeerAsync()).Did;
        var wallet = (await fx.NewPeerAsync()).Did;
        var request = Vector(IdentusMediatorMediateRequest, wallet, mediator);
        request.Typ.Should().Be("application/didcomm-plain+json");

        var outcome = await fx.SendAsync(request, mediator);

        ShouldBeGrant(outcome, mediator, wallet, thid: "17f9f122-f762-4ba8-9011-39b9e7efb177");
    }

    [Fact]
    public async Task The_Blocktrust_client_mediate_request_with_custom_headers_is_granted_on_its_id()
    {
        // Blocktrust's client sends no created_time. The handler grants it: replay admission
        // (FR-RR-08 requires a fresh created_time on mediator-service traffic) belongs to the
        // endpoint, which refuses this request by default; ReplayProtectionOptions.RequireCreatedTime
        // = false is the documented interop setting (I-12). The unknown custom_headers member is
        // kept and ignored (I-13).
        await using var fx = HandlerFixture.Create();
        var mediator = (await fx.NewPeerAsync()).Did;
        var wallet = (await fx.NewPeerAsync()).Did;
        var id = Guid.NewGuid().ToString();
        var request = Vector(BlocktrustMediateRequest, wallet, mediator, ("ID", id));
        request.CreatedTime.Should().BeNull();
        request.Thid.Should().BeNull();
        request.AdditionalHeaders!.Should().ContainKey("custom_headers");

        var outcome = await fx.SendAsync(request, mediator);

        ShouldBeGrant(outcome, mediator, wallet, thid: id);
    }

    // ---- keylist-update / keylist-query ---------------------------------------------------

    [Theory]
    [InlineData(IdentusSuiteDid)]
    [InlineData(ScalaDidRecipientDid)]
    public async Task The_Identus_edge_agent_add_only_keylist_update_without_return_route_is_applied_and_echoed_byte_for_byte(string recipientDid)
    {
        var (fx, mediator, wallet) = await EnrolledIdentusWalletAsync();
        await using var _ = fx;
        var id = Guid.NewGuid().ToString();
        var request = Vector(IdentusSdkKeylistUpdate, wallet, mediator, ("ID", id), ("CREATED", Now()), ("RECIPIENT", recipientDid));
        (request.AdditionalHeaders?.ContainsKey("return_route") ?? false).Should().BeFalse("Wrapper.ts sends return_route on mediate-request only");

        var outcome = await fx.SendAsync(request, mediator);

        // The update is applied whatever the transport later does with the answer (I-4).
        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        var wire = Wire(outcome.Reply!);
        wire.GetProperty("type").GetString().Should().Be(KeylistUpdateResponseType);
        wire.GetProperty("thid").GetString().Should().Be(id);
        var entry = wire.GetProperty("body").GetProperty("updated").EnumerateArray().Should().ContainSingle().Which;
        entry.GetProperty("result").GetString().Should().Be("success");
        entry.GetProperty("action").GetString().Should().Be("add");
        entry.GetProperty("recipient_did").GetString().Should().Be(recipientDid);
        JsonSerializer.Serialize(outcome.Reply, DidCommJson.Default).Should().Contain("\"recipient_did\":\"" + recipientDid + "\"", "the DID is echoed byte for byte");
        (await fx.Memory.FindGrantByRecipientDidAsync(recipientDid, default))!.PeerDid.Should().Be(wallet);
    }

    [Fact]
    public async Task A_Blocktrust_style_keylist_query_reads_back_keys_of_recipient_did_strings()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = (await fx.NewPeerAsync()).Did;
        var wallet = (await fx.NewPeerAsync()).Did;
        ShouldBeGrant(await fx.SendAsync(Vector(BlocktrustMediateRequest, wallet, mediator, ("ID", "enrol-1")), mediator), mediator, wallet, thid: "enrol-1");
        await fx.Update20Async(wallet, mediator, HandlerMessages.Add(IdentusSuiteDid), HandlerMessages.Add(ScalaDidRecipientDid));
        var id = Guid.NewGuid().ToString();

        var outcome = await fx.SendAsync(Vector(BlocktrustKeylistQuery, wallet, mediator, ("ID", id)), mediator);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        var wire = Wire(outcome.Reply!);
        wire.GetProperty("type").GetString().Should().Be(KeylistType, "Blocktrust compares the type with ==");
        wire.GetProperty("thid").GetString().Should().Be(id);
        // Read as QueryMediatorKeysHandler reads it: body.keys, an array, each entry's recipient_did a string.
        var keys = wire.GetProperty("body").GetProperty("keys");
        keys.ValueKind.Should().Be(JsonValueKind.Array);
        var read = new List<string>();
        foreach (var entry in keys.EnumerateArray())
        {
            entry.TryGetProperty("recipient_did", out var value).Should().BeTrue();
            value.ValueKind.Should().Be(JsonValueKind.String);
            read.Add(value.GetString()!);
        }
        read.Should().BeEquivalentTo(new[] { IdentusSuiteDid, ScalaDidRecipientDid });
    }

    // ---- scala-did parser vectors ---------------------------------------------------------

    [Fact]
    public void The_scala_did_keylist_vector_parses_with_its_keys_and_pagination()
    {
        var message = JsonSerializer.Deserialize<Message>(ScalaDidKeylist, DidCommJson.Default)!;

        CoordinateMediation20.TryParseKeylist(message, out var keys, out var pagination).Should().BeTrue();

        keys.Should().Equal("did:peer:2.Ez6LSqZd7Zrp9LcHBEiyMLvKVUxFYb3hNWRxq8cCXbfx4zs6u.Vz6MksTvq16LvtvSqTg8ru7XEXf65Um8WKP7Zm3Vrv4incAhb.SW10");
        pagination.Should().Be(new RecipientPagination(Count: 30, Offset: 30, Remaining: 100));
        message.Thid.Should().Be("1234567890");
    }

    [Fact]
    public void The_scala_did_keylist_update_and_keylist_query_vectors_parse_as_the_mediator_reads_them()
    {
        var update = JsonSerializer.Deserialize<Message>(ScalaDidKeylistUpdate, DidCommJson.Default)!;
        var query = JsonSerializer.Deserialize<Message>(ScalaDidKeylistQuery, DidCommJson.Default)!;

        CoordinateMediation20.TryParseKeylistUpdate(update, new CoordinateMediationOptions().MaxUpdatesPerMessage, out var updates).Should().BeTrue();
        updates.Should().Equal(new RecipientUpdate(ScalaDidRecipientDid, RecipientUpdateAction.Add));
        BareDid.IsBareDid(ScalaDidRecipientDid).Should().BeTrue("a did:peer:2 with an encoded service is a bare DID, not refused as a DID URL");

        CoordinateMediation20.TryParseKeylistQuery(query, out var paginate).Should().BeTrue();
        paginate.Should().Be(new RecipientQueryPaging(Limit: 30, Offset: 0));
    }
}
