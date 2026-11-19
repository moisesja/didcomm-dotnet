using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MediatorWallet;

/// <summary>
/// <c>ws-attempt --state S --ws-endpoint auto|U [--timeout s]</c>: one shipped-client call over the
/// WebSocket transport (<c>GetStatusAsync</c> with the ws(s) address as the endpoint override),
/// recorded, not judged (O-1, M-5, M-6). <c>auto</c> takes the first ws(s) address of the
/// mediator's resolved DID document. The command succeeds whenever the attempt was made: the
/// outcome (<c>ok</c>, or the exception and its inner types), the elapsed time and the transport's
/// log records are in <c>observed</c>. Whether the frame reached the mediator is read from the
/// mediator's and the proxy's logs by the harness.
/// </summary>
internal static class WsAttemptCommand
{
    public static async Task<int> RunAsync(CommandLine line, CommandResult result)
    {
        var common = CommonOptions.From(line);
        var state = WalletState.Load(line.Require("state"));
        var requested = line.Require("ws-endpoint");
        var timeout = TimeSpan.FromSeconds(line.Int("timeout", 10));
        result.Observed["options"] = common.Describe();

        using var capture = new CaptureLoggerProvider(common.LogOut);
        await using var host = WalletHost.Create(common, state.LoadSecrets(), capture, requestTimeout: timeout);

        var endpoint = await host.WebSocketEndpointAsync(state.MediatorDid, requested, result, CancellationToken.None);
        result.Observed["ws_endpoint"] = endpoint.AbsoluteUri;
        result.Observed["require_created_time_in_effect"] = host.RequireCreatedTimeInEffect;

        if (common.ExpectEnvProxy is { } envProxy)
            result.Observed["env_proxy_precondition"] = WalletHost.AssertEnvProxy(envProxy, new[] { endpoint });

        // The attempt: its own hang guard on top of the client's request timeout.
        using var guard = new CancellationTokenSource(timeout + TimeSpan.FromSeconds(15));
        var watch = Stopwatch.StartNew();
        JsonObject outcome;
        try
        {
            var status = await host.Pickup.GetStatusAsync(state.MediatorDid, state.WalletDid, serviceEndpointOverride: endpoint, ct: guard.Token).ConfigureAwait(false);
            outcome = new JsonObject { ["outcome"] = "ok", ["message_count"] = status.MessageCount };
        }
        catch (Exception ex)
        {
            outcome = new JsonObject
            {
                ["outcome"] = ex is TimeoutException ? "timeout" : "error",
                ["exception"] = CommandResult.Describe(ex),
            };
        }
        outcome["elapsed_ms"] = watch.ElapsedMilliseconds;
        result.Observed["attempt"] = outcome;

        // What the transports and the client logged about it (no DID or content is logged by them).
        result.Observed["log"] = new JsonArray(capture.Records
            .Where(r => r.Level >= LogLevel.Debug && r.Category.StartsWith("DidComm", StringComparison.Ordinal))
            .Select(r => (JsonNode?)new JsonObject { ["category"] = r.Category, ["level"] = r.Level.ToString(), ["template"] = r.Template })
            .ToArray());
        // Answers our client dropped, and why (the replay admission's verdict: no DIDs or content).
        result.Observed["dropped"] = new JsonArray(capture.Records
            .Where(r => r.Template?.Contains("dropped", StringComparison.Ordinal) == true && r.Values.ContainsKey("Admission"))
            .Select(r => (JsonNode?)new JsonObject { ["template"] = r.Template, ["admission"] = r.Values["Admission"] })
            .ToArray());
        await result.StepAsync("ws-call", () => Task.CompletedTask, outcome.DeepClone());
        return 0;
    }
}
