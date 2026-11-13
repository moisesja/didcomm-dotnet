using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// L-014: alias the static OutOfBand API class so the same-named namespace doesn't shadow it.
using OutOfBandApi = DidComm.Protocols.OutOfBand.OutOfBand;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// QA-2 (#95), start-up and endpoint composition of a real <c>WebApplication</c>: the delivery budget
/// at the exact smallest <c>MaxReceiveBytes</c> that can carry a message and one byte below it, each
/// FR-MED-07 violation (and a faulting or empty <c>IMediatorIdentity</c>) refusing the start before the
/// server answers anything; the invitation before and after start; the whole flow through every
/// pattern shape, two <c>MapDidCommMediator</c> patterns in one app sharing one replay cache, and two
/// mediators in one process that know nothing of each other.
/// </summary>
public sealed class MediatorStartupQaTests
{
    // ---- the delivery budget -----------------------------------------------------------------------

    [Fact]
    public async Task The_smallest_receive_limit_that_carries_a_message_starts_and_one_byte_less_fails_before_serving()
    {
        var smallest = Enumerable.Range(32 * 1024, 64 * 1024)
            .First(m => PickupSizing.ComputeBatchPayloadBudget(m, new MessagePickupOptions().MaxDeliveryBatch) >= 1);

        await using (var ok = await MediatorTestServer.StartAsync(o => o.MaxReceiveBytes = smallest))
        {
            ok.Queue.MaxAdmissiblePayloadBytes.Should().BeGreaterThanOrEqualTo(1);
            (await ok.GetAsync("/oob?_oobid=mediator")).Status.Should().Be(HttpStatusCode.OK);
        }

        await using var refused = await MediatorTestServer.CreateAsync(o => o.MaxReceiveBytes = smallest - 1);
        var start = () => refused.App.StartAsync();
        await start.Should().ThrowAsync<ArgumentException>();
        await ShouldServeNothingAsync(refused);
    }

    [Fact]
    public async Task An_explicit_MaxDeliveryBytes_too_small_for_one_message_is_refused_at_registration()
    {
        var act = () => MediatorTestServer.CreateAsync(o => o.Mediator = m => m.Pickup.MaxDeliveryBytes = 16 * 1024);
        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().NotContain("did:");
    }

    // ---- FR-MED-07 ---------------------------------------------------------------------------------

    public enum Violation
    {
        UnsupportedMethod,
        QueueTransportUri,
        RelativeUri,
        SecondRoutingDidBad,
        IdentityThrows,
        IdentityEmpty,
        IdentityNotBareDid,
    }

    [Theory]
    [InlineData(Violation.UnsupportedMethod)]
    [InlineData(Violation.QueueTransportUri)]
    [InlineData(Violation.RelativeUri)]
    [InlineData(Violation.SecondRoutingDidBad)]
    [InlineData(Violation.IdentityThrows)]
    [InlineData(Violation.IdentityEmpty)]
    [InlineData(Violation.IdentityNotBareDid)]
    public async Task Each_routing_DID_violation_fails_start_up_before_the_server_answers_and_names_no_DID(Violation violation)
    {
        var good = await MediatorCast.MintAsync(new DidCommServiceSpec("http://mediator.test/", Accept: new[] { "didcomm/v2" }));
        var never = new List<string> { good.Did };
        Action<MediatorTestServerOptions> configure = violation switch
        {
            Violation.UnsupportedMethod => o => o.Mediator = m => m.CoordinateMediation.RoutingDids.Add("did:example:mediator-qa"),
            Violation.QueueTransportUri => o => o.Identity = Mint(RawService("didcomm:transport/queue"), never),
            Violation.RelativeUri => o => o.Identity = Mint(RawService("/didcomm"), never),
            Violation.SecondRoutingDidBad => o => o.Mediator = m => m.CoordinateMediation.RoutingDids.Add(Mint(null, never).Did),
            Violation.IdentityThrows => o => o.ConfigureServices = s => s.AddSingleton<IMediatorIdentity>(new ThrowingIdentity()),
            Violation.IdentityEmpty => o => o.ConfigureServices = s => s.AddSingleton<IMediatorIdentity>(new FixedIdentity()),
            Violation.IdentityNotBareDid => o => o.ConfigureServices = s => s.AddSingleton<IMediatorIdentity>(new FixedIdentity(good.Did + "#key-1")),
            _ => throw new ArgumentOutOfRangeException(nameof(violation)),
        };

        await using var mediator = await MediatorTestServer.CreateAsync(o =>
        {
            o.Identity = good;
            configure(o);
        });
        var start = () => mediator.App.StartAsync();

        var refused = (await start.Should().ThrowAsync<InvalidOperationException>()).Which;
        foreach (var did in never.Append("did:example:mediator-qa"))
            refused.ToString().Should().NotContain(did);
        mediator.Invitation.PlaintextJson.Should().BeNull();
        await ShouldServeNothingAsync(mediator);
        mediator.LeakingLogLines(never.ToArray()).Should().BeEmpty();
    }

    // ---- the invitation ----------------------------------------------------------------------------

    [Fact]
    public async Task The_invitation_is_absent_before_start_and_served_from_the_primary_routing_DID_after_it()
    {
        var second = await MediatorCast.MintAsync(new DidCommServiceSpec("http://mediator.test/", Accept: new[] { "didcomm/v2" }));
        await using var mediator = await MediatorTestServer.CreateAsync(o => o.Mediator = m => m.CoordinateMediation.RoutingDids.Add(second.Did));
        mediator.Invitation.PlaintextJson.Should().BeNull("nothing is built before the host starts");

        // The secrets of the second routing DID are not held: hand them over before starting.
        var secrets = (DidComm.TestSupport.InMemorySecretsResolver)mediator.App.Services.GetRequiredService<DidComm.Secrets.ISecretsResolver>();
        foreach (var jwk in second.Privates)
            secrets.Add(jwk);
        await mediator.App.StartAsync();

        var served = await mediator.GetAsync(OutOfBandApi.ToShortUrl($"{mediator.PublicBase}/oob", DidComm.Mediator.AspNetCore.MediatorEndpointRouteBuilderExtensions.InvitationOobId));
        served.Status.Should().Be(HttpStatusCode.OK);
        var invitation = OutOfBandApi.FromPlaintext(served.Body);
        invitation.Message.From.Should().Be(mediator.Did, "the first routing DID is the primary one");
        served.Body.Should().NotContain(second.Did);
        served.Body.Should().Contain("request-mediate");

        var bob = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(bob)).Should().Equal(mediator.Did, second.Did);

        // A forward wrapped for the second routing DID is relayed like one for the first.
        await mediator.AddRecipientsAsync(bob, new[] { bob.Did });
        var alice = await mediator.NewSenderAsync();
        var (packed, _) = await alice.ForwardAsync(second.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did));
        await Qa.PostForwardAsync(mediator, packed);
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        (await mediator.FetchAsync(bob)).Should().ContainSingle();

        // Neither routing DID can be registered as a recipient (FR-MED-05).
        var refused = await mediator.AskAsync(bob, CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did,
            new[] { new RecipientUpdate(mediator.Did, RecipientUpdateAction.Add), new RecipientUpdate(second.Did, RecipientUpdateAction.Add) }));
        CoordinateMediation.TryParseRecipientUpdateResponse(refused.Message, out var results).Should().BeTrue();
        results!.Should().HaveCount(2).And.OnlyContain(r => r.Result == RecipientUpdateOutcome.ClientError);
        mediator.LeakingLogLines(second.Did).Should().BeEmpty();
    }

    // ---- patterns ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("/", "/", "/oob")]
    [InlineData("/didcomm", "/didcomm", "/didcomm/oob")]
    [InlineData("/didcomm/", "/didcomm/", "/didcomm/oob")]
    [InlineData("/t/{tenant}/didcomm", "/t/acme/didcomm", "/t/other/didcomm/oob")]
    public async Task The_whole_flow_runs_through_each_pattern_shape(string pattern, string path, string oob)
    {
        var identity = await MediatorCast.MintAsync(new DidCommServiceSpec($"http://mediator.test{path}", Accept: new[] { "didcomm/v2" }));
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Pattern = pattern;
            o.Identity = identity;
        });
        (await mediator.GetAsync($"{oob}?_oobid=mediator")).Status.Should().Be(HttpStatusCode.OK);

        var bob = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(bob, path)).Should().Equal(mediator.Did);
        var b = await mediator.NewRecipientAsync();
        await mediator.AddRecipientsAsync(bob, new[] { b.Did }, path);
        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(b.Did);
        var sent = await alice.Client.SendAsync(note, new DidComm.Transports.SendOptions(new[] { b.Did }, alice.Did));
        sent.EndpointUsed.AbsolutePath.Should().Be(path);
        await mediator.FlushAsync();

        var request = MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var delivered = (await mediator.AskAsync(bob, request, path)).Delivery(request.Id).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(b, delivered.Payload, alice)).Message.Id.Should().Be(note.Id);
        using var socket = await mediator.ConnectAsync(path);
        var status = MessagePickup.CreateStatusRequest(bob.Did, mediator.Did);
        (await mediator.AskOverSocketAsync(socket, bob, status)).Status(status.Id).MessageCount.Should().Be(1);
        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", default);
    }

    [Fact]
    public async Task Two_mediator_patterns_in_one_app_share_one_mediator_and_one_replay_cache()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Pattern = "/a";
            o.ConfigurePipeline = app => DidComm.Mediator.AspNetCore.MediatorEndpointRouteBuilderExtensions.MapDidCommMediator(app, "/b");
        });
        (await mediator.GetAsync("/a/oob?_oobid=mediator")).Status.Should().Be(HttpStatusCode.OK);
        (await mediator.GetAsync("/b/oob?_oobid=mediator")).Status.Should().Be(HttpStatusCode.OK);

        var bob = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(bob, "/a")).Should().Equal(mediator.Did);
        await mediator.AddRecipientsAsync(bob, new[] { bob.Did }, "/b");
        var alice = await mediator.NewSenderAsync();
        var (packed, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did));
        await Qa.PostForwardAsync(mediator, packed, "/b");
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);

        var request = mediator.Prepare(MessagePickup.CreateStatusRequest(bob.Did, mediator.Did));
        var stamped = await mediator.AuthcryptAsync(bob, request);
        (await mediator.PostAsync(stamped, "/a")).Status.Should().Be(HttpStatusCode.OK);
        var replay = await mediator.PostAsync(stamped, "/b");
        replay.Status.Should().Be(HttpStatusCode.Accepted, "one replay cache serves every endpoint of the host");
        replay.Body.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_mediators_in_one_process_keep_their_grants_queues_and_counters_apart()
    {
        var network = new MediatorNetwork();
        var clock = new TestClock(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        await using var m1 = await MediatorTestServer.StartAsync(o =>
        {
            o.Host = "m1.test";
            o.Network = network;
            o.Clock = clock;
        });
        await using var m2 = await MediatorTestServer.StartAsync(o =>
        {
            o.Host = "m2.test";
            o.Network = network;
            o.Clock = clock;
        });
        var bob = await m1.NewPeerAsync();
        await m1.EnrolAsync(bob);
        var alice = await m1.NewSenderAsync();

        var (toM2, _) = await alice.ForwardAsync(m2.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did));
        await Qa.PostForwardAsync(m2, toM2);
        await m2.FlushAsync();
        await Qa.SendNoteAsync(m1, alice, bob);

        m2.Outcomes.Should().Equal(new[] { ForwardOutcomes.DroppedUnknown }, "Bob is M1's recipient, not M2's");
        m1.Outcomes.Should().Equal(new[] { ForwardOutcomes.Queued }, "each mediator counts on its own meter");
        m2.Store.MessageCount.Should().Be(0);
        (await m1.FetchAsync(bob)).Should().ContainSingle();
        (await m2.StatusAsync(bob)).MessageCount.Should().Be(0, "Bob holds no grant at M2");

        // A forward for M1 posted to M2 cannot even be decrypted there: the uniform 400.
        var (toM1, _) = await alice.ForwardAsync(m1.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did));
        (await m2.PostAsync(toM1)).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static async Task ShouldServeNothingAsync(MediatorTestServer mediator)
    {
        var act = () => mediator.GetAsync("/oob?_oobid=mediator");
        await act.Should().ThrowAsync<InvalidOperationException>("the server never started, so it answers nothing");
    }

    private static PeerIdentity Mint(NetDid.Core.Model.Service? entry, List<string> never)
    {
        var identity = entry is null
            ? MediatorCast.MintAsync().GetAwaiter().GetResult()
            : MediatorCast.MintWithServiceEntryAsync(entry).GetAwaiter().GetResult();
        never.Add(identity.Did);
        return identity;
    }

    private static NetDid.Core.Model.Service RawService(string uri) => new()
    {
        Id = "#didcomm",
        Type = "DIDCommMessaging",
        ServiceEndpoint = NetDid.Core.Model.ServiceEndpointValue.FromMap(new Dictionary<string, JsonElement>
        {
            ["uri"] = JsonSerializer.SerializeToElement(uri),
            ["accept"] = JsonSerializer.SerializeToElement(new[] { "didcomm/v2" }),
        }),
    };

    private sealed class ThrowingIdentity : IMediatorIdentity
    {
        public IReadOnlyList<string> RoutingDids => throw new InvalidOperationException("did:example:mediator-qa is not available");
    }

    private sealed class FixedIdentity(params string[] dids) : IMediatorIdentity
    {
        public IReadOnlyList<string> RoutingDids => dids;
    }
}
