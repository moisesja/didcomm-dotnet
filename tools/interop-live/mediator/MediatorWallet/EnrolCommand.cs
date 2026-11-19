using System.Text.Json.Nodes;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Resolution;
using DidComm.TestSupport;
using OutOfBandApi = DidComm.Protocols.OutOfBand.OutOfBand;

namespace MediatorWallet;

/// <summary>
/// <c>enrol --identity-dir D --invitation F --expect-mediator-did X [--expect-version 2.0|3.0]
/// [--pin V2|V3] [--endpoint-override U] --out state.json</c>: the M-1 / R-2 enrolment, through the
/// shipped <see cref="MediationClient"/> only.
/// <list type="number">
/// <item>The invitation (a saved OOB 2.0 plaintext): <c>from</c> is <c>X</c>, goal code
/// <c>request-mediate</c>.</item>
/// <item>A fresh wallet DID W (keys only) asks for mediation, with no version pinned unless
/// <c>--pin</c>: the captured Debug record "Coordinate Mediation version negotiated by query
/// {RequestId}: {Version}." names the version, checked against <c>--expect-version</c>. The grant
/// is for W and its routing DIDs are exactly [X]; a second request gets the identical grant
/// (I-6).</item>
/// <item>Two DIDs reached through the mediator (B and B2, <c>ForMediation(grant)</c>) are
/// registered in one update (each <c>Success</c>), and listing every registered DID gives exactly
/// {B, B2}. One page with limit 1 is recorded, not checked (O-7).</item>
/// </list>
/// Identity files (<c>wallet.json</c>, <c>routed.json</c>, <c>routed2.json</c>) go to D;
/// <c>state.json</c> names them.
/// </summary>
internal static class EnrolCommand
{
    /// <summary>The template of the Debug record <see cref="MediationClient"/> writes after Discover Features chose a version.</summary>
    public const string NegotiatedTemplate = "Coordinate Mediation version negotiated by query {RequestId}: {Version}.";

    public static async Task<int> RunAsync(CommandLine line, CommandResult result)
    {
        var common = CommonOptions.From(line);
        var identityDir = Path.GetFullPath(line.Require("identity-dir"));
        var invitationPath = line.Require("invitation");
        var expectedMediator = line.Require("expect-mediator-did");
        var outPath = line.Require("out");
        var endpointOverride = line.Get("endpoint-override") is { } o ? new Uri(o, UriKind.Absolute) : null;
        var expectVersion = line.Get("expect-version") switch
        {
            null => (CoordinateMediationVersion?)null,
            "2.0" => CoordinateMediationVersion.V2,
            "3.0" => CoordinateMediationVersion.V3,
            var other => throw new UsageException($"Unknown --expect-version '{other}' (2.0|3.0)."),
        };
        Directory.CreateDirectory(identityDir);
        result.Observed["options"] = common.Describe();

        using var capture = new CaptureLoggerProvider(common.LogOut);
        var secrets = new InMemorySecretsResolver();
        await using var host = WalletHost.Create(common, secrets, capture);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct = guard.Token;

        // 1. The invitation.
        var invitation = await result.StepAsync("read-invitation",
            () => Task.FromResult(OutOfBandApi.FromPlaintext(File.ReadAllText(invitationPath))),
            i => new JsonObject { ["goal_code"] = i.GoalCode, ["from_is_expected"] = i.From == expectedMediator });
        result.Check("invitation-from-is-the-mediator", invitation.From == expectedMediator);
        result.Check("invitation-goal-code-request-mediate", invitation.GoalCode == "request-mediate", invitation.GoalCode);
        var mediatorDid = expectedMediator;

        // 2. The wallet's own DID, and where the mediator is.
        var wallet = await result.StepAsync("mint-wallet-did", () => IdentityFile.MintAsync(host.Services, service: null));
        wallet.AddTo(secrets);
        var walletPath = Path.Combine(identityDir, "wallet.json");
        wallet.Save(walletPath);

        var endpoints = await result.StepAsync("resolve-mediator-endpoints", () => host.ResolveEndpointsAsync(mediatorDid, ct),
            e => new JsonArray(e.Select(x => (JsonNode?)x).ToArray()));
        var httpEndpoint = endpointOverride?.AbsoluteUri ?? WalletHost.First(endpoints, "http", "https");
        var wsEndpoint = WalletHost.First(endpoints, "ws", "wss");
        result.Observed["mediator_endpoints"] = new JsonArray(endpoints.Select(x => (JsonNode?)x).ToArray());

        if (common.ExpectEnvProxy is { } envProxy)
        {
            var destinations = new[] { httpEndpoint, wsEndpoint }.Where(e => e is not null).Select(e => new Uri(e!)).ToArray();
            result.Observed["env_proxy_precondition"] = WalletHost.AssertEnvProxy(envProxy, destinations);
        }

        // 3. Mediation, negotiated.
        var grant = await result.StepAsync("request-mediation",
            () => host.Mediation.RequestMediationAsync(mediatorDid, wallet.Did, endpointOverride, ct),
            g => g is null ? "mediate-deny" : new JsonObject { ["routing_dids"] = g.RoutingDids.Count });
        result.Check("granted", grant is not null);
        result.Check("grant-is-for-the-wallet-did", grant!.PeerDid == wallet.Did);
        result.Check("grant-routing-dids-are-exactly-the-mediator", grant.RoutingDids.SequenceEqual(new[] { mediatorDid }),
            new JsonObject { ["count"] = grant.RoutingDids.Count, ["first_is_mediator"] = grant.RoutingDids.FirstOrDefault() == mediatorDid });

        var negotiated = capture.Records.Where(r => r.Template == NegotiatedTemplate).ToArray();
        string? negotiatedVersion;
        if (common.Version == CoordinateMediationVersion.Negotiate)
        {
            result.Check("negotiation-record-captured-once", negotiated.Length == 1, negotiated.Length);
            negotiatedVersion = negotiated[0].Values.GetValueOrDefault("Version");
        }
        else
        {
            result.Check("no-negotiation-when-pinned", negotiated.Length == 0, negotiated.Length);
            negotiatedVersion = "pinned:" + common.Version;
        }
        result.Observed["negotiated_version"] = negotiatedVersion;
        if (expectVersion is { } expected)
            result.Check("negotiated-version-is-expected", negotiatedVersion == expected.ToString(), negotiatedVersion);

        var again = await result.StepAsync("request-mediation-again",
            () => host.Mediation.RequestMediationAsync(mediatorDid, wallet.Did, endpointOverride, ct));
        result.Check("second-grant-is-identical", again is not null && again.PeerDid == grant.PeerDid && again.RoutingDids.SequenceEqual(grant.RoutingDids));

        // 4. Two DIDs reached through the mediator, registered in one update.
        var service = DidCommServiceBuilder.ForMediation(grant).Build();
        var routed = await result.StepAsync("mint-routed-did", () => IdentityFile.MintAsync(host.Services, service));
        var routed2 = await result.StepAsync("mint-routed-did-2", () => IdentityFile.MintAsync(host.Services, service));
        routed.AddTo(secrets);
        routed2.AddTo(secrets);
        var routedPath = Path.Combine(identityDir, "routed.json");
        var routed2Path = Path.Combine(identityDir, "routed2.json");
        routed.Save(routedPath);
        routed2.Save(routed2Path);

        var updates = new[] { new RecipientUpdate(routed.Did, RecipientUpdateAction.Add), new RecipientUpdate(routed2.Did, RecipientUpdateAction.Add) };
        var updated = await result.StepAsync("update-recipients",
            () => host.Mediation.UpdateRecipientsAsync(mediatorDid, wallet.Did, updates, endpointOverride, ct),
            r => new JsonArray(r.Select(x => (JsonNode?)x.Result.ToString()).ToArray()));
        result.Check("each-update-answered-success",
            updated.Count == 2 && updated.Zip(updates).All(p => p.First.RecipientDid == p.Second.RecipientDid && p.First.Action == p.Second.Action && p.First.Result == RecipientUpdateOutcome.Success));

        // O-7, recorded only: one page of one.
        try
        {
            var page = await result.StepAsync("query-recipients-limit-1 (O-7)",
                () => host.Mediation.QueryRecipientsAsync(mediatorDid, wallet.Did, new RecipientQueryPaging(Limit: 1, Offset: 0), endpointOverride, ct));
            result.Observed["query_limit_1"] = new JsonObject
            {
                ["dids"] = page.Dids.Count,
                ["pagination"] = page.Pagination is { } p ? new JsonObject { ["count"] = p.Count, ["offset"] = p.Offset, ["remaining"] = p.Remaining } : null,
            };
        }
        catch (Exception ex)
        {
            result.Observed["query_limit_1"] = new JsonObject { ["error"] = CommandResult.Describe(ex) };
        }

        var all = await result.StepAsync("query-all-recipients", async () =>
        {
            var dids = new List<string>();
            await foreach (var did in host.Mediation.QueryAllRecipientsAsync(mediatorDid, wallet.Did, endpointOverride, ct).ConfigureAwait(false))
                dids.Add(did);
            return dids;
        }, d => d.Count);
        result.Check("registered-dids-are-exactly-B-and-B2",
            all.Count == 2 && all.ToHashSet(StringComparer.Ordinal).SetEquals(new[] { routed.Did, routed2.Did }), all.Count);

        var state = new WalletState(mediatorDid, wallet.Did, walletPath, routed.Did, routedPath, [routed2.Did], [routed2Path],
            httpEndpoint, wsEndpoint, endpointOverride?.AbsoluteUri, negotiatedVersion, grant.RoutingDids);
        state.Save(outPath);
        result.Observed["state"] = Path.GetFullPath(outPath);
        return 0;
    }
}
