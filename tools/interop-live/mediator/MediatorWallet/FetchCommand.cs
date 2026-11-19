using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pickup;

namespace MediatorWallet;

/// <summary>
/// <c>fetch --state S --expect-count N [--limit L] [--explicit-ack] [--wait-timeout s] --out-dir D</c>:
/// pick up through the shipped <see cref="PickupClient"/> only.
/// <list type="number">
/// <item>Wait until <c>GetStatusAsync</c> counts at least N (100 ms pacing; hang guard
/// <c>--wait-timeout</c>, default 30 s).</item>
/// <item><c>FetchAsync(limit L)</c> (default 10) until a fetch reports the queue drained. Each
/// fetch acknowledges what it opened; against a mediator that does not answer
/// <c>messages-received</c> that acknowledgement fails quietly (logged) and the mediator applies it
/// anyway, so the loop still ends.</item>
/// <item>Exactly N items, every one opened, no message id twice; the drained fetch's status counts
/// 0, and so does a separate <c>GetStatusAsync</c>.</item>
/// <item>With <c>--explicit-ack</c>, <c>AckAsync</c> with the fetched ids must answer a status of 0
/// (works against our mediator; against Identus see O-9).</item>
/// </list>
/// Each item goes to <c>D/item-N.unpacked.json</c> in <c>InteropCli unpack</c>'s
/// <c>{message, metadata}</c> schema; <c>D/fetch.json</c> lists them.
/// </summary>
internal static class FetchCommand
{
    private const int MaxRounds = 60;

    public static async Task<int> RunAsync(CommandLine line, CommandResult result)
    {
        var common = CommonOptions.From(line);
        var state = WalletState.Load(line.Require("state"));
        var expectCount = line.Int("expect-count", -1);
        if (expectCount < 0)
            throw new UsageException("Missing required option '--expect-count'.");
        var limit = line.Int("limit", 10);
        var explicitAck = line.Flag("explicit-ack");
        var waitTimeout = TimeSpan.FromSeconds(line.Int("wait-timeout", 30));
        var outDir = Path.GetFullPath(line.Require("out-dir"));
        Directory.CreateDirectory(outDir);
        result.Observed["options"] = common.Describe();

        using var capture = new CaptureLoggerProvider(common.LogOut);
        await using var host = WalletHost.Create(common, state.LoadSecrets(), capture);
        var endpoint = state.Override;
        if (common.ExpectEnvProxy is { } envProxy)
            result.Observed["env_proxy_precondition"] = WalletHost.AssertEnvProxy(envProxy, MediatorAddresses(state));

        // 1. Wait for the count (the forward may still be on its way into the mediator's store).
        var waited = await result.StepAsync("wait-for-status", async () =>
        {
            var watch = Stopwatch.StartNew();
            var polls = 0;
            while (true)
            {
                using var stepGuard = new CancellationTokenSource(waitTimeout - watch.Elapsed > TimeSpan.Zero ? waitTimeout - watch.Elapsed : TimeSpan.FromMilliseconds(1));
                polls++;
                var status = await host.Pickup.GetStatusAsync(state.MediatorDid, state.WalletDid, serviceEndpointOverride: endpoint, ct: stepGuard.Token).ConfigureAwait(false);
                if (status.MessageCount >= expectCount)
                    return (status, polls);
                if (watch.Elapsed >= waitTimeout)
                    throw new TimeoutException($"The mediator counted {status.MessageCount} message(s), not {expectCount}, within {waitTimeout.TotalSeconds} s ({polls} polls).");
                await Task.Delay(100).ConfigureAwait(false);
            }
        }, w => new JsonObject { ["message_count"] = w.status.MessageCount, ["polls"] = w.polls });

        // 2. Fetch until drained.
        var items = new List<PickupItem>();
        var rounds = new JsonArray();
        PickupStatus? finalStatus = null;
        using (var fetchGuard = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(60, expectCount * 10))))
        {
            await result.StepAsync("fetch-until-drained", async () =>
            {
                for (var round = 1; ; round++)
                {
                    if (round > MaxRounds)
                        throw new ExpectationException($"The queue was not drained after {MaxRounds} fetches.");
                    var watch = Stopwatch.StartNew();
                    var batch = await host.Pickup.FetchAsync(state.MediatorDid, state.WalletDid, limit, serviceEndpointOverride: endpoint, ct: fetchGuard.Token).ConfigureAwait(false);
                    rounds.Add(new JsonObject
                    {
                        ["round"] = round,
                        ["ms"] = watch.ElapsedMilliseconds,
                        ["items"] = batch.Items.Count,
                        ["failed"] = batch.Items.Count(i => i.Error is not null),
                        ["drained"] = batch.Drained,
                        ["status_count"] = batch.Status?.MessageCount,
                    });
                    items.AddRange(batch.Items);
                    if (batch.Drained)
                    {
                        finalStatus = batch.Status;
                        return;
                    }
                    // A round that only returns messages that do not open would repeat forever.
                    if (batch.Items.Count > 0 && batch.Items.All(i => i.Error is not null))
                        throw new ExpectationException($"Fetch {round} returned only messages that did not open; they stay queued.");
                }
            }, new JsonObject { ["limit"] = limit });
        }
        result.Observed["rounds"] = rounds;

        // 3. What was fetched.
        var opened = items.Where(i => i.Unpacked is not null).ToList();
        var failed = items.Where(i => i.Error is not null).ToList();
        var listing = new JsonArray();
        for (var n = 0; n < opened.Count; n++)
        {
            var file = Path.Combine(outDir, $"item-{n + 1}.unpacked.json");
            File.WriteAllText(file, UnpackJson(opened[n].Unpacked!).ToJsonString(CommandResult.Json) + "\n");
            var message = opened[n].Unpacked!.Message;
            listing.Add(new JsonObject
            {
                ["index"] = n + 1,
                ["pickup_id"] = opened[n].Id,
                ["message_id"] = message.Id,
                ["type"] = message.Type,
                ["authenticated"] = opened[n].Unpacked!.Authenticated,
                ["file"] = file,
            });
        }
        var failures = new JsonArray(failed.Select(f => (JsonNode?)new JsonObject { ["pickup_id"] = f.Id, ["error"] = CommandResult.Describe(f.Error!) }).ToArray());

        var separate = await result.StepAsync("status-after-fetch",
            () => host.Pickup.GetStatusAsync(state.MediatorDid, state.WalletDid, serviceEndpointOverride: endpoint),
            s => s.MessageCount);

        JsonNode? explicitAckOutcome = null;
        if (explicitAck)
        {
            var ids = items.Select(i => i.Id).ToArray();
            if (ids.Length == 0)
                throw new ExpectationException("--explicit-ack: nothing was fetched to acknowledge.");
            var acked = await result.StepAsync("explicit-ack",
                () => host.Pickup.AckAsync(state.MediatorDid, state.WalletDid, ids, endpoint),
                s => s.MessageCount);
            explicitAckOutcome = new JsonObject { ["ids"] = ids.Length, ["status_count"] = acked.MessageCount };
        }

        var summary = new JsonObject
        {
            ["expected"] = expectCount,
            ["waited_count"] = waited.status.MessageCount,
            ["items"] = listing,
            ["failures"] = failures,
            ["rounds"] = rounds.DeepClone(),
            ["final_status_count"] = finalStatus?.MessageCount,
            ["status_after_fetch"] = separate.MessageCount,
            ["explicit_ack"] = explicitAckOutcome,
            ["ack_failed_logged"] = capture.Records.Count(r => r.Template?.StartsWith("Acknowledgement {RequestId} of {Count} message(s) failed", StringComparison.Ordinal) == true),
            ["beyond_limit_logged"] = capture.Records.Count(r => r.Template?.StartsWith("Delivery for request {RequestId} carried {Count} message(s) beyond the limit", StringComparison.Ordinal) == true),
        };
        File.WriteAllText(Path.Combine(outDir, "fetch.json"), summary.ToJsonString(CommandResult.Json) + "\n");
        result.Observed["fetch"] = summary.DeepClone();

        result.Check("no-item-failed-to-open", failed.Count == 0, failed.Count);
        result.Check("exactly-the-expected-count", opened.Count == expectCount, opened.Count);
        result.Check("no-message-id-twice", opened.Select(i => i.Unpacked!.Message.Id).Distinct(StringComparer.Ordinal).Count() == opened.Count);
        result.Check("drained-status-counts-zero", finalStatus?.MessageCount == 0, finalStatus?.MessageCount);
        result.Check("separate-status-counts-zero", separate.MessageCount == 0, separate.MessageCount);
        return 0;
    }

    /// <summary>The mediator addresses a command may use (for the env-proxy precondition).</summary>
    public static IEnumerable<Uri> MediatorAddresses(WalletState state)
        => new[] { state.EndpointOverride, state.HttpEndpoint, state.WsEndpoint }.Where(e => e is not null).Select(e => new Uri(e!)).Distinct();

    /// <summary>The <c>{message, metadata}</c> JSON of an unpack, exactly as <c>InteropCli unpack</c> prints it.</summary>
    public static JsonObject UnpackJson(UnpackResult unpacked) => new()
    {
        ["message"] = JsonSerializer.SerializeToNode(unpacked.Message, InteropJson),
        ["metadata"] = new JsonObject
        {
            ["encrypted"] = unpacked.Encrypted,
            ["authenticated"] = unpacked.Authenticated,
            ["non_repudiation"] = unpacked.NonRepudiation,
            ["anonymous_sender"] = unpacked.AnonymousSender,
            ["enc"] = unpacked.ContentEncryption,
            ["kw"] = unpacked.KeyWrap,
            ["sig_alg"] = unpacked.SignatureAlgorithm,
            ["signer_kid"] = unpacked.SignerKid,
            ["sender_kid"] = unpacked.SenderKid,
            ["recipient_kid"] = unpacked.RecipientKid,
        },
    };

    /// <summary>InteropCli's output options (null members left out), so the two tools' files compare.</summary>
    private static readonly JsonSerializerOptions InteropJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
