using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// The client's version question against Discover Features answered the way the two 2.0 mediators
/// in the field answer it (owner decision O-1 of 2026-11-18, #99; FR-MED-09, FR-MED-10). The
/// mediator behind it is this library's real <see cref="CoordinateMediation20Handler"/>; only the
/// Discover Features responder is a stand-in, built from the source of the mediators:
/// <list type="bullet">
/// <item><description>Identus: <c>match</c> is a regular expression that must match the whole id,
/// and Coordinate Mediation 2.0 is disclosed in the role <c>responder</c>
/// (<c>hyperledger/identus-mediator@5664f1d</c>,
/// <c>mediator/src/main/scala/org/hyperledger/identus/mediator/protocols/DiscoverFeaturesExecuter.scala</c>
/// lines 30-62).</description></item>
/// <item><description>Blocktrust: <c>match</c> is <c>*</c> or a prefix once <c>.*</c> is removed, and
/// every protocol is disclosed with an empty <c>roles</c> list
/// (<c>bsandmann/blocktrust.Mediator@36b8dfd</c>,
/// <c>Blocktrust.Mediator.Server/Commands/DiscoverFeatures/ProcessDiscoverFeaturesHandler.cs</c> and
/// <c>Blocktrust.Mediator.Common/Models/DiscoverFeatures/DiscoverFeature.cs</c>).</description></item>
/// </list>
/// The client asked one <c>coordinate-mediation/*</c> query before, which both readings answer
/// with nothing; it now asks <c>…/3.*</c> and <c>…/2.*</c>, and a role list that names no role of
/// the protocol says nothing. Only the question is tested here: our client still cannot enroll
/// with a real Blocktrust mediator, whose answers it refuses on purpose (owner decision O-2).
/// </summary>
public sealed class MediatorClientThirdPartyDiscoveryTests
{
    public static TheoryData<string> Mediators => new() { "Identus", "Blocktrust" };

    [Theory]
    [MemberData(nameof(Mediators))]
    public async Task A_negotiating_client_understands_the_answer_and_enrols_registers_and_lists_in_2_0(string kind)
    {
        var discovery = ThirdPartyDiscoverFeatures.Like(kind);
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var host = await CoordinateMediation20OnlyHost.StartAsync(mediator, discovery: discovery);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();

        var grant = await harness.Mediation.RequestMediationAsync(host.Did, bob.Did);

        // The question: the two queries, exactly; the answer: the 2.0 entry only, in the mediator's
        // own role shape.
        discovery.Matches.Should().Equal("https://didcomm.org/coordinate-mediation/3.*", "https://didcomm.org/coordinate-mediation/2.*");
        discovery.Disclosed.Should().Equal(CoordinateMediation20.ProtocolUri);
        harness.Channel.Versions.TryGet(host.Did, out var version).Should().BeTrue();
        version.Should().Be(CoordinateMediationVersion.V2);

        grant.Should().NotBeNull();
        grant!.RoutingDids.Should().Equal(new[] { host.Did }, "a 2.0 grant carries one routing DID: the first");
        var published = await MediatorClientEnrolmentTests.PublishAsync(harness, grant);
        (await harness.Mediation.UpdateRecipientsAsync(host.Did, bob.Did, new[] { new RecipientUpdate(published.Did, RecipientUpdateAction.Add) }))
            .Should().Equal(new RecipientUpdateResult(published.Did, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success));
        (await harness.Mediation.QueryRecipientsAsync(host.Did, bob.Did)).Dids.Should().Equal(published.Did);

        host.Seen.From(bob.Did).Select(r => r.Type).Should().Equal(
            new[]
            {
                DiscoverFeatures.QueriesType,
                CoordinateMediation20.MediateRequestType,
                CoordinateMediation20.KeylistUpdateType,
                CoordinateMediation20.KeylistQueryType,
            },
            "one question, then 2.0 for every operation");
        mediator.LeakingLogLines(published.Did).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Mediators))]
    public async Task A_third_party_answer_that_names_the_recipient_role_only_still_does_not_count(string kind)
    {
        // The other half of the rule: a peer that says it is the recipient is not taken for a
        // mediator, whichever way it reads the question.
        var discovery = ThirdPartyDiscoverFeatures.Like(kind, roles: new JsonArray("recipient"));
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var host = await CoordinateMediation20OnlyHost.StartAsync(mediator, discovery: discovery);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();

        var thrown = (await FluentActions.Awaiting(() => harness.Mediation.RequestMediationAsync(host.Did, bob.Did))
            .Should().ThrowAsync<MediationNotSupportedException>()).Which;

        thrown.DisclosedProtocols.Should().Equal(CoordinateMediation20.ProtocolUri);
        host.Seen.From(bob.Did).Select(r => r.Type).Should().Equal(new[] { DiscoverFeatures.QueriesType }, "nothing is sent after the answer");
    }
}

/// <summary>
/// A Discover Features responder that reads <c>match</c> and shapes <c>roles</c> as a third-party
/// mediator does (see <see cref="MediatorClientThirdPartyDiscoveryTests"/>). It discloses from a
/// fixed feature list and records the match strings it was asked and the ids it disclosed.
/// </summary>
internal sealed class ThirdPartyDiscoverFeatures : IProtocolHandler
{
    private readonly Func<string, string, bool> _reads;
    private readonly JsonArray _features;
    private readonly List<string> _matches = new();
    private readonly List<string> _disclosed = new();
    private readonly object _gate = new();

    private ThirdPartyDiscoverFeatures(Func<string, string, bool> reads, JsonArray features)
    {
        _reads = reads;
        _features = features;
    }

    public string ProtocolUri => DiscoverFeatures.ProtocolUri;

    public IReadOnlyList<string> Matches { get { lock (_gate) return _matches.ToArray(); } }

    public IReadOnlyList<string> Disclosed { get { lock (_gate) return _disclosed.ToArray(); } }

    /// <summary>
    /// The Identus or the Blocktrust responder. <paramref name="roles"/> replaces the Coordinate
    /// Mediation 2.0 entry's roles (default: the mediator's own shape).
    /// </summary>
    public static ThirdPartyDiscoverFeatures Like(string kind, JsonArray? roles = null) => kind switch
    {
        // identus-mediator@5664f1d DiscoverFeaturesExecuter.scala: Regex(match).matches(id).
        "Identus" => new(
            (match, id) => Regex.IsMatch(id, "^(?:" + match + ")$"),
            Features(roles ?? new JsonArray("responder"),
                ("https://didcomm.org/routing/2.0", new JsonArray("mediator")),
                (CoordinateMediation20.ProtocolUri, null),
                ("https://didcomm.org/messagepickup/3.0", new JsonArray("mediator")),
                ("https://didcomm.org/trust-ping/2.0", new JsonArray("receiver")),
                ("https://didcomm.org/discover-features/2.0", new JsonArray("responder")))),
        // blocktrust.Mediator@36b8dfd ProcessDiscoverFeaturesHandler.cs: "*", else
        // Id.StartsWith(Match.Replace(".*", "")); every entry with roles [].
        "Blocktrust" => new(
            (match, id) => match == "*" || id.StartsWith(match.Replace(".*", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal),
            Features(roles ?? new JsonArray(),
                ("https://didcomm.org/out-of-band/2.0", new JsonArray()),
                (CoordinateMediation20.ProtocolUri, null),
                ("https://didcomm.org/messagepickup/3.0", new JsonArray()),
                ("https://didcomm.org/trust-ping/2.0", new JsonArray()),
                ("https://didcomm.org/discover-features/2.0", new JsonArray()),
                ("https://didcomm.org/shorten-url/1.0/shortened-url", new JsonArray()),
                ("https://didcomm.org/report-problem/2.0/problem-report", new JsonArray()))),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        if (!string.Equals(message.Type, DiscoverFeatures.QueriesType, StringComparison.Ordinal)
            || message.From is null || message.To is not { Count: > 0 })
        {
            return Task.FromResult<Message?>(null);
        }

        var disclosures = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var query in message.Body?["queries"]?.AsArray() ?? new JsonArray())
            {
                if (query?["feature-type"]?.GetValue<string>() != "protocol" || query["match"]?.GetValue<string>() is not { } match)
                    continue;
                _matches.Add(match);
                foreach (var feature in _features)
                {
                    var id = feature!["id"]!.GetValue<string>();
                    if (_reads(match, id) && seen.Add(id))
                    {
                        disclosures.Add(feature.DeepClone());
                        _disclosed.Add(id);
                    }
                }
            }
        }

        var reply = new MessageBuilder()
            .WithType(DiscoverFeatures.DiscloseType)
            .WithFrom(message.To[0])
            .WithTo(message.From)
            .WithThid(message.Id)
            .WithBody(new JsonObject { ["disclosures"] = disclosures })
            .Build();
        return Task.FromResult<Message?>(reply);
    }

    // The Coordinate Mediation 2.0 entry is the one whose roles are null in the list: it takes
    // cmRoles.
    private static JsonArray Features(JsonArray cmRoles, params (string Id, JsonArray? Roles)[] entries)
    {
        var array = new JsonArray();
        foreach (var (id, roles) in entries)
            array.Add(new JsonObject { ["feature-type"] = "protocol", ["id"] = id, ["roles"] = (roles ?? cmRoles).DeepClone() });
        return array;
    }
}
