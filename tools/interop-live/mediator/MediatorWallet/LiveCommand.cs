using System.Diagnostics;
using System.Text.Json.Nodes;
using DidComm.Mediator.Client;

namespace MediatorWallet;

/// <summary>
/// <c>live --state S --ws-endpoint auto|U --expect-count N [--guard s]</c>: the shipped
/// <see cref="PickupClient.ReceiveLiveAsync"/> against the mediator over the WebSocket transport,
/// recorded, not judged (observation O-1b). It enumerates the stream until N items were yielded or
/// the guard (default 120 s) ends it, then leaves the loop (which acknowledges what is waiting and
/// turns live delivery off), and records what happened: each item with the time it came, how the
/// enumeration ended, how long leaving it took, and, from the client's own log records, every
/// request the stream sent (by kind), which ended without an answer, the acknowledgements that
/// failed, and the pushes it ignored (with the reason). The harness posts the forwards once the
/// mediator has logged the live-mode change, and reads the mediator's side from its log.
/// Exit 0 whenever the stream was started; the client and the stream are used as shipped.
/// </summary>
internal static class LiveCommand
{
    private const string SendingTemplate = "Mediator request {RequestId} ({Request}) sending.";
    private const string NoAnswerTemplate = "Mediator request {RequestId} ({Request}) ended without a usable answer after {ElapsedMs} ms: {ExceptionType}.";

    public static async Task<int> RunAsync(CommandLine line, CommandResult result)
    {
        var common = CommonOptions.From(line);
        var state = WalletState.Load(line.Require("state"));
        var expect = line.Int("expect-count", 1);
        var guardTime = TimeSpan.FromSeconds(line.Int("guard", 120));
        result.Observed["options"] = common.Describe();

        using var capture = new CaptureLoggerProvider(common.LogOut);
        await using var host = WalletHost.Create(common, state.LoadSecrets(), capture);
        result.Observed["require_created_time_in_effect"] = host.RequireCreatedTimeInEffect;
        var endpoint = await host.WebSocketEndpointAsync(state.MediatorDid, line.Require("ws-endpoint"), result, CancellationToken.None);
        result.Observed["ws_endpoint"] = endpoint.AbsoluteUri;

        // The hang guard of the whole observation (the stream's own pauses and the mediator's
        // silences included); cancelling it ends the enumeration.
        using var guard = new CancellationTokenSource(guardTime);
        var watch = Stopwatch.StartNew();
        var items = new JsonArray();
        var ending = "the stream ended by itself";
        long? leftLoopAtMs = null;
        try
        {
            await foreach (var item in host.Pickup.ReceiveLiveAsync(state.MediatorDid, state.WalletDid, endpoint, guard.Token).ConfigureAwait(false))
            {
                items.Add(new JsonObject
                {
                    ["at_ms"] = watch.ElapsedMilliseconds,
                    ["opened"] = item.Unpacked is not null,
                    ["message_id"] = item.Unpacked?.Message.Id,
                    ["error"] = item.Error?.GetType().Name,
                });
                if (items.Count >= expect)
                {
                    ending = $"{expect} item(s) yielded";
                    leftLoopAtMs = watch.ElapsedMilliseconds;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (guard.IsCancellationRequested)
        {
            ending = $"the {guardTime.TotalSeconds} s guard ended it";
        }
        catch (Exception ex)
        {
            ending = "the stream threw";
            result.Observed["exception"] = CommandResult.Describe(ex);
        }
        var endedAtMs = watch.ElapsedMilliseconds;

        var records = capture.Records;
        JsonObject CountBy(string template) => records
            .Where(r => r.Template == template)
            .GroupBy(r => r.Values.GetValueOrDefault("Request") ?? "?")
            .Aggregate(new JsonObject(), (o, g) => { o[g.Key] = g.Count(); return o; });

        result.Observed["live"] = new JsonObject
        {
            ["expected_items"] = expect,
            ["items"] = items,
            ["ending"] = ending,
            ["left_loop_at_ms"] = leftLoopAtMs,
            ["ended_at_ms"] = endedAtMs,
            ["leaving_took_ms"] = leftLoopAtMs is { } left ? endedAtMs - left : null,
            ["requests_sent"] = CountBy(SendingTemplate),
            ["requests_without_answer"] = CountBy(NoAnswerTemplate),
            ["acknowledgements_failed"] = records.Count(r => r.Template?.StartsWith("Acknowledgement {RequestId} of {Count} message(s) failed", StringComparison.Ordinal) == true),
            ["pushes_ignored"] = new JsonArray(records
                .Where(r => r.Template?.StartsWith("A live push was ignored", StringComparison.Ordinal) == true)
                .Select(r => (JsonNode?)(r.Values.GetValueOrDefault("Reason") ?? "?")).ToArray()),
            ["cycles_failed"] = records.Count(r => r.Template?.Contains("cycle", StringComparison.OrdinalIgnoreCase) == true && r.Template.Contains("fail", StringComparison.OrdinalIgnoreCase)),
            ["live_not_in_effect"] = records.Any(r => r.Template?.Contains("with live delivery off", StringComparison.Ordinal) == true),
            // Every record the client wrote, by template (templates carry no DIDs or content):
            // where pushes the stream did not take ended up shows here.
            ["log_templates"] = records
                .Where(r => r.Template is not null && r.Category.StartsWith("DidComm", StringComparison.Ordinal))
                .GroupBy(r => r.Template!)
                .Aggregate(new JsonObject(), (o, g) => { o[g.Key] = g.Count(); return o; }),
            // Inbound messages (answers or pushes) our client dropped, with its reason: the replay
            // admission's verdict (Admission) or the session's refusal (Refusal).
            ["inbound_dropped"] = new JsonArray(records
                .Where(r => r.Template?.Contains("dropped", StringComparison.Ordinal) == true
                            && (r.Values.ContainsKey("Admission") || r.Values.ContainsKey("Refusal")))
                .Select(r => (JsonNode?)(r.Values.GetValueOrDefault("Admission") ?? r.Values.GetValueOrDefault("Refusal"))).ToArray()),
        };
        await result.StepAsync("live-stream", () => Task.CompletedTask, new JsonObject { ["ending"] = ending, ["items"] = items.Count });
        return 0;
    }
}
