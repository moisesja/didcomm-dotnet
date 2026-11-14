using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// ADV-1 (#96 red team): third parties answering a pending request, steering Coordinate Mediation
/// version negotiation, writing into a live stream, and probing pending thread ids — all with real
/// envelopes through the real channel (the dispatcher's correlator call). Every attack here held;
/// these stay as regressions.
/// </summary>
public sealed class MediatorClientChannelAdv1Tests : IAsyncLifetime
{
    private const string Cm3 = CoordinateMediation.ProtocolUri;
    private const string Cm2 = CoordinateMediation20.ProtocolUri;

    private ClientWorld _w = null!;
    private readonly FakeSend _send = new();
    private readonly ManualTimeProvider _time = new();
    private readonly ClientLogCapture _logs = new();
    private MediatorClientChannel _channel = null!;

    public async Task InitializeAsync()
    {
        _w = await ClientWorld.CreateAsync();
        _channel = Create(_w, _send, time: _time, logs: _logs, resolveEndpoint: (_, _) => Task.FromResult(Wss));
    }

    public async Task DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _w.DisposeAsync();
    }

    private string M => _w.Mediator.Did;

    private string L => _w.Local.Did;

    private bool Offer(InboundMessageSnapshot snapshot) => _channel.AsCorrelator().OnInbound(snapshot);

    private static Message Disclose(string from, string to, string thid, params string[] protocols)
        => DiscoverFeatures.CreateDisclose(from, to, thid, protocols
            .Select(p => new FeatureDisclosure { FeatureType = DiscoverFeatures.FeatureTypeProtocol, Id = p, Roles = new[] { CoordinateMediation.MediatorRole } })
            .ToArray());

    // ---- version negotiation and the grant -----------------------------------------------------

    /// <summary>
    /// Held: while the Discover Features query and then the <c>mediate-request</c> wait over a
    /// WebSocket, forged answers arrive first through the dispatcher — Mallory's own authcrypt
    /// naming our pending thread (a downgrade to 2.0, a "neither", a thread-less grant, a deny, a
    /// problem report), the same claiming the mediator in anoncrypt, the mediator's genuine authcrypt
    /// to another of our DIDs, and the mediator's signature without authcrypt. None completes or
    /// fails anything; the genuine answers then do, the client speaks 3.0, and the version cache
    /// remembers 3.0 for the mediator.
    /// </summary>
    [Fact]
    public async Task Forged_answers_neither_steer_the_version_nor_answer_the_grant_nor_poison_the_version_cache()
    {
        var forgeries = new List<(string Name, bool Claimed)>();
        var sentTypes = new List<string>();
        _send.Respond = async (message, options, _) =>
        {
            sentTypes.Add(message.Type);
            var id = message.Id;
            var other = _w.OtherLocal.Did;
            var mallory = _w.Mallory.Did;
            if (message.Type == DiscoverFeatures.QueriesType)
            {
                forgeries.Add(("Mallory's 2.0-only disclose", Offer(await _w.SnapshotAsync(Disclose(mallory, L, id, Cm2), from: mallory))));
                forgeries.Add(("Mallory's empty disclose", Offer(await _w.SnapshotAsync(Disclose(mallory, L, id), from: mallory))));
                forgeries.Add(("anoncrypt disclose claiming the mediator", Offer(await _w.SnapshotAsync(Disclose(M, L, id, Cm2), AnswerEnvelope.Anoncrypt))));
                forgeries.Add(("anoncrypt(sign) disclose by Mallory", Offer(await _w.SnapshotAsync(Disclose(mallory, L, id, Cm2), AnswerEnvelope.AnoncryptOfSigned, from: mallory))));
                forgeries.Add(("the mediator's disclose to another of our DIDs", Offer(await _w.SnapshotAsync(Disclose(M, other, id, Cm2), from: M, to: other))));
                forgeries.Add(("the mediator's signed-only disclose", Offer(await _w.SnapshotAsync(Disclose(M, L, id, Cm2), AnswerEnvelope.SignedOnly))));
                forgeries.Add(("Mallory's problem report on the query", Offer(await _w.SnapshotAsync(_w.Problem(id, from: mallory), from: mallory))));
                Offer(await _w.SnapshotAsync(Disclose(M, L, id, Cm3, Cm2))).Should().BeTrue("the genuine disclose");
            }
            else if (message.Type == CoordinateMediation.MediateRequestType)
            {
                forgeries.Add(("Mallory's grant naming her own routing DID", Offer(await _w.SnapshotAsync(
                    CoordinateMediation.CreateGrant(mallory, L, new[] { mallory }, id), from: mallory))));
                forgeries.Add(("Mallory's thread-less grant", Offer(await _w.SnapshotAsync(_w.Grant(thid: null, from: mallory), from: mallory))));
                forgeries.Add(("Mallory's deny", Offer(await _w.SnapshotAsync(CoordinateMediation.CreateDeny(mallory, L, id), from: mallory))));
                forgeries.Add(("Mallory's problem report by pthid", Offer(await _w.SnapshotAsync(_w.Problem(id, from: mallory), from: mallory))));
                forgeries.Add(("Mallory's problem report by thid", Offer(await _w.SnapshotAsync(_w.Problem(id, inThid: true, from: mallory), from: mallory))));
                forgeries.Add(("the mediator's deny to another of our DIDs", Offer(await _w.SnapshotAsync(CoordinateMediation.CreateDeny(M, other, id), from: M, to: other))));
                forgeries.Add(("the mediator's problem report to another of our DIDs", Offer(await _w.SnapshotAsync(_w.Problem(id, to: other), from: M, to: other))));
                forgeries.Add(("anoncrypt deny claiming the mediator", Offer(await _w.SnapshotAsync(CoordinateMediation.CreateDeny(M, L, id), AnswerEnvelope.Anoncrypt))));
                Offer(await _w.SnapshotAsync(_w.Grant(id))).Should().BeTrue("the genuine grant");
            }
            return FakeSend.Accepted(options.ServiceEndpointOverride!);
        };
        var client = new MediationClient(_channel, logger: null);

        var grant = await client.RequestMediationAsync(M, L);

        forgeries.Should().NotBeEmpty();
        forgeries.Where(f => f.Claimed).Select(f => f.Name).Should().BeEmpty("no forgery may complete or fail a pending request");
        grant.Should().NotBeNull();
        grant!.RoutingDids.Should().Equal(M);
        sentTypes.Should().Equal(DiscoverFeatures.QueriesType, CoordinateMediation.MediateRequestType);
        _channel.Versions.TryGet(M, out var remembered).Should().BeTrue();
        remembered.Should().Be(CoordinateMediationVersion.V3);
        _channel.PendingCount.Should().Be(0);
    }

    // ---- the live stream's mailbox ----------------------------------------------------------------

    /// <summary>
    /// Held: with a live stream open for (mediator, our DID), thread-less <c>delivery</c> pushes that
    /// are not the mediator's authcrypt to that DID — Mallory's own, an anoncrypt one claiming the
    /// mediator, the mediator's to another of our DIDs, the mediator's signature alone — never reach
    /// the stream's mailbox; the mediator's own push does.
    /// </summary>
    [Fact]
    public async Task Only_the_mediators_authcrypt_push_to_the_streams_DID_reaches_the_live_mailbox()
    {
        using var stream = _channel.RegisterLiveStream(M, L);
        var mallory = _w.Mallory.Did;
        var other = _w.OtherLocal.Did;

        Offer(await _w.SnapshotAsync(_w.Delivery(thid: null, from: mallory), from: mallory)).Should().BeFalse();
        Offer(await _w.SnapshotAsync(_w.Delivery(thid: null), AnswerEnvelope.Anoncrypt)).Should().BeFalse();
        Offer(await _w.SnapshotAsync(_w.Delivery(thid: null, to: other), from: M, to: other)).Should().BeFalse();
        Offer(await _w.SnapshotAsync(_w.Delivery(thid: null), AnswerEnvelope.SignedOnly)).Should().BeFalse();
        Offer(await _w.SnapshotAsync(_w.Delivery(thid: null), AnswerEnvelope.AnoncryptOfSigned)).Should().BeFalse();
        stream.Pushes.TryRead(out _).Should().BeFalse("nothing but the mediator's authcrypt to our DID reached the mailbox");

        var genuine = await _w.SnapshotAsync(_w.Delivery(thid: null));
        Offer(genuine).Should().BeTrue();
        stream.Pushes.TryRead(out var pushed).Should().BeTrue();
        pushed.Should().BeSameAs(genuine);
    }

    // ---- probing a pending thread id ---------------------------------------------------------------

    /// <summary>
    /// Held, with a correction to the plan's §7 timing claim ("The <c>thid</c> probe only runs for
    /// messages authcrypted by a mediator to us"): the pending map is probed for ANY authcrypt sender
    /// to one of our DIDs — the identity rule binds the sender to its own <c>from</c>, not to a
    /// mediator — so Mallory's authcrypt with our pending thread id does reach the probe (L-042: the
    /// defensible claim is narrower). What she can observe from it is nothing: a hit (our pending,
    /// a 122-bit random UUID she would have to guess) and a miss return the same, write the same log
    /// line with the same values (only the shared running count advances) and leave the request
    /// waiting, which the genuine answer then completes.
    /// </summary>
    [Fact]
    public async Task A_strangers_probe_of_a_pending_thread_id_reaches_the_map_but_looks_exactly_like_a_miss()
    {
        var request = _w.Request();
        var exchange = _channel.ExchangeAsync(WebSocketRoute, request, ClientSpecs.StatusRequest, M, L, default).Observed();
        await _send.FirstCall;
        var mallory = _w.Mallory.Did;
        var hit = await _w.SnapshotAsync(_w.Status(request.Id, count: 99, from: mallory), from: mallory);
        var miss = await _w.SnapshotAsync(_w.Status(Guid.NewGuid().ToString("D"), count: 99, from: mallory), from: mallory);
        var channelLines = typeof(MediatorClientChannel).FullName;

        var lookups0 = _channel.Lookups;
        var lines0 = _logs.Entries.Count(e => e.Category == channelLines);
        var hitClaimed = Offer(hit);
        var lookups1 = _channel.Lookups;
        var hitLines = _logs.Entries.Where(e => e.Category == channelLines).Skip(lines0).ToArray();
        var missClaimed = Offer(miss);
        var lookups2 = _channel.Lookups;
        var missLines = _logs.Entries.Where(e => e.Category == channelLines).Skip(lines0 + hitLines.Length).ToArray();

        hitClaimed.Should().BeFalse();
        missClaimed.Should().BeFalse();
        (lookups1 - lookups0).Should().Be(1, "the pending map is probed for a sender that is not the request's mediator (plan §7 says otherwise)");
        (lookups2 - lookups1).Should().Be(1);
        hitLines.Should().ContainSingle();
        missLines.Should().ContainSingle();
        hitLines[0].State["{OriginalFormat}"].Should().Be(missLines[0].State["{OriginalFormat}"]);
        hitLines[0].State["Answer"].Should().Be(missLines[0].State["Answer"]);
        exchange.IsCompleted.Should().BeFalse("a forgery naming the request changes nothing");

        Offer(await _w.SnapshotAsync(_w.Status(request.Id, count: 0))).Should().BeTrue();
        (await exchange).Should().NotBeNull();
    }
}
