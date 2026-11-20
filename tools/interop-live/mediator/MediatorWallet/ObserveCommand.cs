using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Messages;
using DidComm.TestSupport;

namespace MediatorWallet;

/// <summary>
/// <c>observe --state S [--triggers D] --out observations.json</c>: the raw probe of the mediator
/// the wallet enrolled with (harness §4), run after the gating cells. It writes a JSON array of
/// observation records <c>{"id","e":[],"i":[],"title","value","method","gateable"}</c>; none is
/// gated. Its own wallet (W) and routed DID (B) come from <c>state.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Records: O-0 crypto in each direction, O-15 the Discover Features disclosure, O-4 / O-5 / O-6
/// <c>thid</c>, <c>created_time</c> and <c>to</c> on every kind of answer (plus I-11 / I-13 / I-15
/// facts), O-3 leasing and head-of-line,
/// O-8 <c>limit</c> with 12 queued, O-9 <c>messages-received</c> and <c>AckAsync</c>, O-11
/// <c>please_ack</c> / <c>return_route</c> and the delivered attachment against the sender's inner
/// envelope, O-12 a repeated envelope, O-13 a malformed <c>keylist-update</c> and an unparseable
/// body; and, last because they change the mediator's live-mode state for the wallet, O-2 the
/// Text-frame WebSocket probe and its live push, and O-2c forwards after a live-mode socket closed.
/// O-5 includes the wallet side of the clock offset (each answer's <c>Date</c> header against our
/// clock).
/// </para>
/// <para>
/// Every step has its own hang guard; a step that fails records its error in its record and the
/// probe goes on. The queue is drained (raw <c>delivery-request</c>, one <c>messages-received</c>)
/// before each step that counts messages. Forwards are built here (an inner authcrypt envelope from
/// a fresh sender DID to B, wrapped by hand in a routing 2.0 <c>forward</c> anoncrypted to the
/// mediator), except the ones in <c>--triggers</c>: each <c>NAME.forward.json</c> there (a forward
/// to the mediator for B, as the python counterpart builds it) is posted once in O-11, and its
/// delivered attachment is compared with <c>NAME.inner.json</c> when that file exists.
/// </para>
/// <para>
/// The probe uses <see cref="DidCommClient"/> pack/unpack, a BCL <see cref="HttpClient"/> without a
/// proxy and a <see cref="ClientWebSocket"/>; the shipped clients are called only where the record
/// is about them (O-3's <c>FetchAsync</c>, O-9's <c>AckAsync</c>). Exit 0 when the probe ran,
/// whatever it found; 1 when it could not start.
/// </para>
/// </remarks>
internal static class ObserveCommand
{
    private static readonly TimeSpan StepGuard = TimeSpan.FromSeconds(30);

    public static async Task<int> RunAsync(CommandLine line, CommandResult result)
    {
        var common = CommonOptions.From(line);
        var state = WalletState.Load(line.Require("state"));
        var triggers = line.Get("triggers");
        var outPath = line.Require("out");
        var endpoint = new Uri(state.EndpointOverride ?? state.HttpEndpoint ?? throw new UsageException("The state names no HTTP endpoint."));

        using var capture = new CaptureLoggerProvider(common.LogOut);
        var secrets = state.LoadSecrets();
        await using var host = WalletHost.Create(common, secrets, capture);
        if (common.ExpectEnvProxy is { } envProxy)
            result.Observed["env_proxy_precondition"] = WalletHost.AssertEnvProxy(envProxy, FetchCommand.MediatorAddresses(state));

        // A sender for the probe's own forwards, and a stranger (enrolled nowhere) for the problem report.
        var sender = await IdentityFile.MintAsync(host.Services, service: null).ConfigureAwait(false);
        var stranger = await IdentityFile.MintAsync(host.Services, service: null).ConfigureAwait(false);
        // An outsider whose keys the wallet does not hold: a message for it cannot be opened here.
        var outsider = await IdentityFile.MintAsync(host.Services, service: null).ConfigureAwait(false);
        sender.AddTo(secrets);
        stranger.AddTo(secrets);

        using var probe = new RawProbe(host.DidComm, state.MediatorDid, state.WalletDid, endpoint);
        probe.Role = did => did == state.MediatorDid ? "mediator" : did == state.WalletDid ? "wallet" : did == state.RoutedDid ? "routed"
            : did == stranger.Did ? "stranger" : did == sender.Did ? "sender" : did == outsider.Did ? "outsider" : "other";
        var run = new Run(probe, host, state, sender.Did, stranger.Did, outsider.Did, result);

        var records = new List<JsonObject>();
        var answers = new JsonArray();
        await run.Record(records, "O-0", ["I-8"], [], "Crypto in each direction: our authcrypt and anoncrypt requests, the mediator's answers opened by us",
            "raw probe: status-request authcrypted and anoncrypted, answers unpacked", ct => run.CryptoAsync(answers, ct));
        await run.Record(records, "O-15", ["I-10"], ["E-35"], "The Discover Features disclosure as sent",
            "raw probe: the two queries our client sends (coordinate-mediation/3.*, /2.*), then match '*'", ct => run.DisclosureAsync(answers, ct));
        await run.Record(records, "O-4-answers", [], [], "Every kind of answer, as received (feeds O-4, O-5, O-6)",
            "raw probe: CM 2.0 mediate-request, keylist-update add/remove, keylist-query; pickup status-request; a stranger's delivery-request (problem report)",
            ct => run.AnswersAsync(answers, ct));
        await run.Record(records, "O-3", ["E-32"], [], "Leasing and head-of-line: two delivery-requests without an ack; a junk forward ahead of a good one with limit 1",
            "raw probe, then the shipped FetchAsync(limit 1) twice", ct => run.LeaseAsync(answers, ct), TimeSpan.FromSeconds(90));
        await run.Record(records, "O-8", ["E-35"], [], "limit 5 with 12 queued; 12 ids in one messages-received, then the status",
            "raw probe: 12 forwards posted at once, delivery-request limit 5, one messages-received, status-request", ct => run.LimitAsync(answers, ct), TimeSpan.FromSeconds(90));
        await run.Record(records, "O-9", ["E-54"], [], "messages-received gets no answer; what AckAsync does",
            "raw probe (messages-received timing) and the shipped PickupClient.AckAsync, then a status", ct => run.AckAsync(ct), TimeSpan.FromSeconds(90));
        await run.Record(records, "O-11", ["E-31"], [], "please_ack / return_route on deliveries; the delivered attachment against the sender's inner envelope",
            "raw probe: one forward of ours and each --triggers forward, delivered, compared", ct => run.RewrapAsync(triggers, answers, ct), TimeSpan.FromSeconds(120));
        await run.Record(records, "O-12", ["E-55"], ["I-9"], "The same envelope posted twice",
            "raw probe: one packed status-request posted twice", ct => run.DejavuAsync(ct));
        await run.Record(records, "O-13", ["E-54"], [], "A malformed keylist-update (updates: 5, return_route all); an unparseable body",
            "raw probe", ct => run.MalformedAsync(ct), TimeSpan.FromSeconds(60));
        // Last: the WebSocket steps change the mediator's live-mode state for this wallet.
        await run.Record(records, "O-2", ["E-5", "E-14", "E-22", "E-35"], [], "WebSocket with Text frames: answers, live push framing",
            "raw ClientWebSocket, Text frames: status-request, live-delivery-change true, an HTTP forward for B and the push it causes, live-delivery-change false, close",
            ct => run.WebSocketAsync(answers, ct), TimeSpan.FromSeconds(60));
        await run.Record(records, "O-2c", ["E-5", "E-37"], [], "Forwards after a live-mode socket closed without turning live mode off",
            "raw ClientWebSocket: live-delivery-change true, one forward pushed, close with live mode on; then up to six HTTP forwards for B one after another, each with its own 15 s guard, stopping at the first without a response; then six at once",
            ct => run.ClosedLiveSocketAsync(ct), TimeSpan.FromSeconds(120));

        // Derived from every answer collected above (and the push in O-2).
        records.Add(Derived("O-4", ["E-35"], ["I-7"], "thid against the request id, per answer", answers,
            a => new JsonObject { ["thid_is_request_id"] = a["thid_is_request_id"]?.DeepClone(), ["thid"] = a["thid"]?.DeepClone(), ["pthid_is_request_id"] = a["pthid_is_request_id"]?.DeepClone() }));
        records.Add(Derived("O-5", ["E-14", "E-23"], ["I-7", "I-12"], "created_time on each answer and push; the mediator's clock against ours (wallet side)", answers,
            a => new JsonObject { ["created_time_present"] = a["created_time_present"]?.DeepClone(), ["created_time_minus_our_clock_s"] = a["created_time_minus_our_clock_s"]?.DeepClone(), ["date_header"] = a["date_header"]?.DeepClone() }));
        records.Add(Derived("O-6", ["E-35"], [], "to on answers and pushes", answers,
            a => new JsonObject { ["to"] = a["to"]?.DeepClone() }));
        records.Add(Derived("I-11-13-15", [], ["I-11", "I-13", "I-15"], "from_prior, top-level members and from on every answer", answers,
            a => new JsonObject { ["from_prior_present"] = a["from_prior_present"]?.DeepClone(), ["from_is_mediator"] = a["from_is_mediator"]?.DeepClone(), ["top_level_members"] = a["top_level_members"]?.DeepClone() }));

        var array = new JsonArray(records.Select(r => (JsonNode?)r).ToArray());
        File.WriteAllText(outPath, array.ToJsonString(CommandResult.Json) + "\n");
        result.Observed["records"] = new JsonArray(records.Select(r => (JsonNode?)new JsonObject
        {
            ["id"] = r["id"]?.DeepClone(),
            ["probe_ok"] = r["value"]?["error"] is null,
        }).ToArray());
        result.Observed["out"] = Path.GetFullPath(outPath);
        return 0;
    }

    private static JsonObject Derived(string id, string[] e, string[] i, string title, JsonArray answers, Func<JsonObject, JsonObject> pick)
    {
        var value = new JsonArray();
        foreach (var node in answers)
        {
            if (node is not JsonObject entry || entry["answer"] is not JsonObject answer)
                continue;
            var row = pick(answer);
            row["step"] = entry["step"]?.DeepClone();
            row["request_type"] = entry["request_type"]?.DeepClone();
            row["answer_type"] = answer["type"]?.DeepClone();
            row["channel"] = entry["channel"]?.DeepClone() ?? "http";
            if (id == "O-5")
                row["date_minus_our_clock_s"] = entry["wire"]?["date_minus_our_clock_s"]?.DeepClone();
            value.Add(row);
        }
        return Record(id, e, i, title, new JsonObject { ["answers"] = value }, "derived from every answer the probe opened (raw probe)");
    }

    public static JsonObject Record(string id, string[] e, string[] i, string title, JsonNode value, string method) => new()
    {
        ["id"] = id,
        ["e"] = new JsonArray(e.Select(x => (JsonNode?)x).ToArray()),
        ["i"] = new JsonArray(i.Select(x => (JsonNode?)x).ToArray()),
        ["title"] = title,
        ["value"] = value,
        ["method"] = method,
        ["gateable"] = false,
    };

    /// <summary>The probe steps, sharing the probe, the wallet's state and the answers list.</summary>
    private sealed class Run(RawProbe probe, WalletHost host, WalletState state, string senderDid, string strangerDid, string outsiderDid, CommandResult result)
    {
        public async Task Record(List<JsonObject> records, string id, string[] e, string[] i, string title, string method, Func<CancellationToken, Task<JsonObject>> step, TimeSpan? guard = null)
        {
            var limit = guard ?? StepGuard;
            using var cts = new CancellationTokenSource(limit);
            var watch = Stopwatch.StartNew();
            JsonObject value;
            try
            {
                value = await step(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                value = new JsonObject { ["error"] = CommandResult.Describe(ex), ["hang_guard_s"] = limit.TotalSeconds, ["partial"] = Partial };
            }
            Partial = null;
            value["probe_ms"] = watch.ElapsedMilliseconds;
            records.Add(ObserveCommand.Record(id, e, i, title, value, method));
            result.Observed[id] = value["error"] is null ? "recorded" : "error: " + value["error"]!["type"];
        }

        /// <summary>What a step had found before it failed.</summary>
        private JsonObject? Partial { get; set; }

        private JsonObject Begin()
        {
            Partial = new JsonObject();
            return Partial;
        }

        private static void Keep(JsonArray answers, string step, JsonObject record, string channel = "http")
        {
            record["step"] = step;
            record["channel"] = channel;
            answers.Add(record.DeepClone());
        }

        private async Task<string> ForwardForRoutedAsync(string text, CancellationToken ct)
        {
            var (_, _, outer) = await probe.BuildForwardAsync(senderDid, state.RoutedDid, state.RoutedDid, text, ct).ConfigureAwait(false);
            return outer;
        }

        private async Task<int> PostForwardAsync(string outer, CancellationToken ct)
            => (await probe.PostAsync(outer, ct).ConfigureAwait(false)).Status;

        /// <summary>
        /// Every undelivered message: one raw delivery-request, then one messages-received per id, all
        /// at once (Identus applies only the first id a messages-received names that it still holds, O-8).
        /// </summary>
        private async Task<JsonObject> DrainAsync(CancellationToken ct)
        {
            var (_, answer, _) = await probe.AskAsync(probe.Request(RawProbe.Pickup3 + "/delivery-request", new JsonObject { ["limit"] = 100 }), ct).ConfigureAwait(false);
            var ids = answer?.Message.Type == RawProbe.Pickup3 + "/delivery"
                ? (answer.Message.Attachments ?? []).Select(a => a.Id).Where(x => x is not null).Select(x => x!).ToArray()
                : [];
            long? ackMs = null;
            if (ids.Length > 0)
            {
                var watch = Stopwatch.StartNew();
                await Task.WhenAll(ids.Select(async id =>
                {
                    var packed = await probe.AuthcryptAsync(probe.Request(RawProbe.Pickup3 + "/messages-received",
                        new JsonObject { ["message_id_list"] = new JsonArray((JsonNode?)id) }), ct: ct).ConfigureAwait(false);
                    await probe.PostAsync(packed, ct).ConfigureAwait(false);
                })).ConfigureAwait(false);
                ackMs = watch.ElapsedMilliseconds;
            }
            var status = await StatusCountAsync(ct).ConfigureAwait(false);
            return new JsonObject { ["acknowledged_one_per_message"] = ids.Length, ["ack_ms"] = ackMs, ["status_after"] = status };
        }

        private async Task<int?> StatusCountAsync(CancellationToken ct)
        {
            var (_, answer, _) = await probe.AskAsync(probe.Request(RawProbe.Pickup3 + "/status-request", new JsonObject()), ct).ConfigureAwait(false);
            return answer?.Message.Body?["message_count"]?.GetValue<int>();
        }

        private async Task<int?> WaitForCountAsync(int atLeast, CancellationToken ct)
        {
            int? count = null;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                count = await StatusCountAsync(ct).ConfigureAwait(false);
                if (count >= atLeast)
                    return count;
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            return count;
        }

        // O-0 -------------------------------------------------------------------------------
        public async Task<JsonObject> CryptoAsync(JsonArray answers, CancellationToken ct)
        {
            var value = Begin();
            var (authWire, authAnswer, authRecord) = await probe.AskAsync(probe.Request(RawProbe.Pickup3 + "/status-request", new JsonObject()), ct).ConfigureAwait(false);
            Keep(answers, "O-0 authcrypt status-request", authRecord);
            value["authcrypt_request"] = authRecord.DeepClone();
            value["mediator_opened_our_authcrypt"] = authWire.Status == 200 && authAnswer is not null;
            value["we_opened_the_mediator_answer"] = authAnswer is not null;
            value["mediator_answer_authenticated"] = authAnswer?.Authenticated;

            try
            {
                var (anonWire, anonAnswer, anonRecord) = await probe.AskAsync(probe.Request(RawProbe.Pickup3 + "/status-request", new JsonObject()), ct, anoncrypt: true).ConfigureAwait(false);
                Keep(answers, "O-0 anoncrypt status-request", anonRecord);
                value["anoncrypt_request"] = anonRecord.DeepClone();
                value["mediator_opened_our_anoncrypt"] = anonWire.Status is 200 or 202;
                value["anoncrypt_answer_type"] = anonAnswer?.Message.Type;
            }
            catch (Exception ex)
            {
                value["anoncrypt_request"] = new JsonObject { ["error"] = CommandResult.Describe(ex) };
            }
            return value;
        }

        // O-15 ------------------------------------------------------------------------------
        public async Task<JsonObject> DisclosureAsync(JsonArray answers, CancellationToken ct)
        {
            var value = Begin();
            async Task<JsonObject> AskAsync(string step, params string[] matches)
            {
                var queries = new JsonArray(matches.Select(m => (JsonNode?)new JsonObject { ["feature-type"] = "protocol", ["match"] = m }).ToArray());
                var (_, answer, record) = await probe.AskAsync(probe.Request("https://didcomm.org/discover-features/2.0/queries", new JsonObject { ["queries"] = queries }), ct).ConfigureAwait(false);
                Keep(answers, step, record);
                return new JsonObject
                {
                    ["queries"] = queries.DeepClone(),
                    ["answer_type"] = answer?.Message.Type,
                    ["disclosures"] = answer?.Message.Body?["disclosures"]?.DeepClone(),
                    ["wire"] = record["wire"]?.DeepClone(),
                };
            }
            value["client_queries"] = await AskAsync("O-15 discover-features (client's queries)", "https://didcomm.org/coordinate-mediation/3.*", "https://didcomm.org/coordinate-mediation/2.*").ConfigureAwait(false);
            value["match_all"] = await AskAsync("O-15 discover-features (*)", "*").ConfigureAwait(false);
            var disclosed = value["client_queries"]!["disclosures"] as JsonArray;
            value["routing_did_kind_in_grant"] = "see O-4-answers (mediate-grant body)";
            value["cm2_disclosed"] = disclosed?.Any(d => d?["id"]?.GetValue<string>()?.StartsWith("https://didcomm.org/coordinate-mediation/2.", StringComparison.Ordinal) == true);
            value["cm3_disclosed"] = disclosed?.Any(d => d?["id"]?.GetValue<string>()?.StartsWith("https://didcomm.org/coordinate-mediation/3.", StringComparison.Ordinal) == true);
            return value;
        }

        // O-4 / O-5 / O-6 sources ----------------------------------------------------------
        public async Task<JsonObject> AnswersAsync(JsonArray answers, CancellationToken ct)
        {
            var value = Begin();
            var steps = new JsonArray();
            async Task AskAsync(string step, Message request, string? from = null)
            {
                try
                {
                    var (_, answer, record) = await probe.AskAsync(request, ct, from: from).ConfigureAwait(false);
                    Keep(answers, step, record);
                    steps.Add(new JsonObject { ["step"] = step, ["record"] = record.DeepClone() });
                    if (answer?.Message.Type is { } t && t.EndsWith("/mediate-grant", StringComparison.Ordinal))
                        value["routing_did_json_kind"] = answer.Message.Body?["routing_did"]?.GetValueKind().ToString();
                }
                catch (Exception ex)
                {
                    steps.Add(new JsonObject { ["step"] = step, ["error"] = CommandResult.Describe(ex) });
                }
            }

            await AskAsync("mediate-request (CM 2.0, again)", probe.Request(RawProbe.Cm2 + "/mediate-request", new JsonObject())).ConfigureAwait(false);
            var throwaway = (await IdentityFile.MintAsync(host.Services, service: null).ConfigureAwait(false)).Did;
            await AskAsync("keylist-update add (CM 2.0)", probe.Request(RawProbe.Cm2 + "/keylist-update", new JsonObject
            {
                ["updates"] = new JsonArray(new JsonObject { ["recipient_did"] = throwaway, ["action"] = "add" }),
            })).ConfigureAwait(false);
            await AskAsync("keylist-update remove (CM 2.0)", probe.Request(RawProbe.Cm2 + "/keylist-update", new JsonObject
            {
                ["updates"] = new JsonArray(new JsonObject { ["recipient_did"] = throwaway, ["action"] = "remove" }),
            })).ConfigureAwait(false);
            await AskAsync("keylist-query (CM 2.0, no paginate)", probe.Request(RawProbe.Cm2 + "/keylist-query", new JsonObject())).ConfigureAwait(false);
            await AskAsync("keylist-query (CM 2.0, paginate limit 1)", probe.Request(RawProbe.Cm2 + "/keylist-query", new JsonObject
            {
                ["paginate"] = new JsonObject { ["limit"] = 1, ["offset"] = 0 },
            })).ConfigureAwait(false);
            await AskAsync("status-request", probe.Request(RawProbe.Pickup3 + "/status-request", new JsonObject())).ConfigureAwait(false);
            await AskAsync("delivery-request from a DID enrolled nowhere (problem report)",
                probe.Request(RawProbe.Pickup3 + "/delivery-request", new JsonObject { ["limit"] = 1 }, from: strangerDid), strangerDid).ConfigureAwait(false);
            value["exchanges"] = steps;
            return value;
        }

        // O-2 ------------------------------------------------------------------------------
        public async Task<JsonObject> WebSocketAsync(JsonArray answers, CancellationToken ct)
        {
            var value = Begin();
            var ws = state.WsEndpoint is { } w ? new Uri(w) : throw new InvalidOperationException("The state names no WebSocket endpoint.");
            value["drain_before"] = await DrainAsync(ct).ConfigureAwait(false);
            using var socket = await RawProbe.ConnectAsync(ws, ct).ConfigureAwait(false);
            value["connected"] = true;

            async Task<JsonObject> ExchangeAsync(string step, Message request)
            {
                await RawProbe.SendTextAsync(socket, await probe.AuthcryptAsync(request, ct: ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                using var frameGuard = CancellationTokenSource.CreateLinkedTokenSource(ct);
                frameGuard.CancelAfter(TimeSpan.FromSeconds(10));
                var frame = await RawProbe.ReceiveAsync(socket, frameGuard.Token).ConfigureAwait(false);
                return await DescribeFrameAsync(step, frame, request, answers, ct).ConfigureAwait(false);
            }

            value["status_request_text_frame"] = await ExchangeAsync("O-2 ws status-request", probe.Request(RawProbe.Pickup3 + "/status-request", new JsonObject())).ConfigureAwait(false);
            value["live_delivery_change_true"] = await ExchangeAsync("O-2 ws live-delivery-change",
                probe.Request(RawProbe.Pickup3 + "/live-delivery-change", new JsonObject { ["live_delivery"] = true })).ConfigureAwait(false);

            // The push: a forward for B over HTTP while the socket is live.
            var (forwardId, _, outer) = await probe.BuildForwardAsync(senderDid, state.RoutedDid, state.RoutedDid, "O-2 live push", ct).ConfigureAwait(false);
            var receive = Task.Run(async () =>
            {
                using var pushGuard = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pushGuard.CancelAfter(TimeSpan.FromSeconds(20));
                return await RawProbe.ReceiveAsync(socket, pushGuard.Token).ConfigureAwait(false);
            }, ct);
            var post = await probe.PostAsync(outer, ct).ConfigureAwait(false);
            value["forward_post"] = post.Describe();
            try
            {
                var push = await receive.ConfigureAwait(false);
                var described = await DescribeFrameAsync("O-2 ws live push", push, request: null, answers, ct).ConfigureAwait(false);
                if (described["answer"] is JsonObject pushed)
                    pushed["thid_is_forward_id"] = string.Equals(pushed["thid"]?.GetValue<string>(), forwardId, StringComparison.OrdinalIgnoreCase);
                value["push"] = described;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                value["push"] = "no push within 20 s";
            }
            value["taken_as_push_by_our_client"] = "no: this mediator seals the push for the DID the forward named and sends it on the connection the main DID opened; our client keeps one DID per connection (FR-RR-07, LocalMismatch), so it fetches the message at its next poll instead (O-1b; issue #134)";
            value["live_delivery_change_false"] = await ExchangeAsync("O-2 ws live-delivery-change false",
                probe.Request(RawProbe.Pickup3 + "/live-delivery-change", new JsonObject { ["live_delivery"] = false })).ConfigureAwait(false);
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "probe done", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or TimeoutException)
            {
                value["close_error"] = ex.GetType().Name;
            }
            value["drain_after"] = await DrainAsync(ct).ConfigureAwait(false);
            return value;
        }

        // O-2c -----------------------------------------------------------------------------
        public async Task<JsonObject> ClosedLiveSocketAsync(CancellationToken ct)
        {
            var value = Begin();
            var ws = state.WsEndpoint is { } w ? new Uri(w) : throw new InvalidOperationException("The state names no WebSocket endpoint.");
            value["drain_before"] = await DrainAsync(ct).ConfigureAwait(false);
            using (var socket = await RawProbe.ConnectAsync(ws, ct).ConfigureAwait(false))
            {
                await RawProbe.SendTextAsync(socket, await probe.AuthcryptAsync(probe.Request(RawProbe.Pickup3 + "/live-delivery-change",
                    new JsonObject { ["live_delivery"] = true }), ct: ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                using var frameGuard = CancellationTokenSource.CreateLinkedTokenSource(ct);
                frameGuard.CancelAfter(TimeSpan.FromSeconds(10));
                var answer = await RawProbe.ReceiveAsync(socket, frameGuard.Token).ConfigureAwait(false);
                value["live_on_answer_frame"] = answer.Type.ToString();

                // One push received while the socket is open, as in O-2.
                var receive = Task.Run(async () =>
                {
                    using var pushGuard = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    pushGuard.CancelAfter(TimeSpan.FromSeconds(20));
                    return await RawProbe.ReceiveAsync(socket, pushGuard.Token).ConfigureAwait(false);
                }, ct);
                value["forward_while_live"] = (await probe.PostAsync(await ForwardForRoutedAsync("O-2c while live", ct).ConfigureAwait(false), ct).ConfigureAwait(false)).Describe();
                try
                {
                    value["push_while_live"] = (await receive.ConfigureAwait(false)).Type.ToString();
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    value["push_while_live"] = "no push within 20 s";
                }
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing with live mode on", ct).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }

            // Forwards after the close, one after another; the first one without an HTTP response ends the series.
            var posts = new JsonArray();
            for (var n = 1; n <= 6; n++)
            {
                try
                {
                    var wire = await probe.PostAsync(await ForwardForRoutedAsync($"O-2c after close {n}", ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                    posts.Add(new JsonObject { ["n"] = n, ["wire"] = wire.Describe() });
                }
                catch (TimeoutException ex)
                {
                    posts.Add(new JsonObject { ["n"] = n, ["no_response"] = ex.Message });
                    break;
                }
            }
            value["answered_after_close"] = posts.Count(p => p?["wire"] is not null);

            // Then six at once (a burst after the close), each with its own guard.
            var burst = await Task.WhenAll(Enumerable.Range(1, 6).Select(async n =>
            {
                try
                {
                    return (JsonNode?)(await probe.PostAsync(await ForwardForRoutedAsync($"O-2c burst {n}", ct).ConfigureAwait(false), ct).ConfigureAwait(false)).Status;
                }
                catch (TimeoutException)
                {
                    return "no response";
                }
            })).ConfigureAwait(false);
            value["burst_after_close"] = new JsonArray(burst);
            value["forwards_after_close"] = posts;
            try
            {
                value["status_after"] = await StatusCountAsync(ct).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                value["status_after"] = "no response: " + ex.Message;
            }
            return value;
        }

        private async Task<JsonObject> DescribeFrameAsync(string step, (WebSocketMessageType Type, int Frames, string Text) frame, Message? request, JsonArray answers, CancellationToken ct)
        {
            var record = new JsonObject
            {
                ["frame_type"] = frame.Type.ToString(),
                ["frames"] = frame.Frames,
                ["bytes"] = Encoding.UTF8.GetByteCount(frame.Text),
            };
            if (frame.Type == WebSocketMessageType.Close)
                return record;
            try
            {
                var parsed = JsonNode.Parse(frame.Text);
                record["one_json_object"] = parsed is JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                record["one_json_object"] = false;
            }
            try
            {
                var unpacked = await probe.Client.UnpackAsync(frame.Text, ct).ConfigureAwait(false);
                record["answer"] = probe.DescribeAnswer(unpacked, request, date: null);
                if (unpacked.Message.Type.EndsWith("/delivery", StringComparison.Ordinal))
                    record["attachments"] = new JsonArray(RawProbe.ReadAttachments(unpacked.Message).Select(a => (JsonNode?)a.Facts).ToArray());
            }
            catch (Exception ex)
            {
                record["unpack_error"] = CommandResult.Describe(ex);
            }
            var kept = (JsonObject)record.DeepClone();
            kept["request_type"] = request?.Type ?? "(push)";
            Keep(answers, step, kept, "websocket");
            return record;
        }

        // O-3 ------------------------------------------------------------------------------
        public async Task<JsonObject> LeaseAsync(JsonArray answers, CancellationToken ct)
        {
            var value = Begin();
            value["drain_before"] = await DrainAsync(ct).ConfigureAwait(false);

            // A junk forward (its inner envelope is for a DID whose keys the wallet does not hold) ahead of a good one.
            var (_, _, junk) = await probe.BuildForwardAsync(senderDid, state.RoutedDid, outsiderDid, "O-3 junk: not for B", ct).ConfigureAwait(false);
            var good = await ForwardForRoutedAsync("O-3 good", ct).ConfigureAwait(false);
            value["junk_post"] = await PostForwardAsync(junk, ct).ConfigureAwait(false);
            value["good_post"] = await PostForwardAsync(good, ct).ConfigureAwait(false);
            value["queued"] = await WaitForCountAsync(2, ct).ConfigureAwait(false);

            async Task<JsonObject> RawDeliveryAsync(string step)
            {
                var (_, answer, record) = await probe.AskAsync(probe.Request(RawProbe.Pickup3 + "/delivery-request", new JsonObject { ["limit"] = 1 }), ct).ConfigureAwait(false);
                Keep(answers, step, record);
                var ids = (answer?.Message.Attachments ?? []).Select(a => a.Id).ToArray();
                return new JsonObject
                {
                    ["answer_type"] = answer?.Message.Type,
                    ["attachments"] = ids.Length,
                    ["ids"] = new JsonArray(ids.Select(x => (JsonNode?)x).ToArray()),
                };
            }
            var first = await RawDeliveryAsync("O-3 delivery-request limit 1 (first)").ConfigureAwait(false);
            var second = await RawDeliveryAsync("O-3 delivery-request limit 1 (again, no ack)").ConfigureAwait(false);
            value["raw_first"] = first;
            value["raw_second"] = second;
            value["same_ids_twice_no_lease"] = JsonNode.DeepEquals(first["ids"], second["ids"]);

            // The shipped client, limit 1, twice: what a head-of-line junk message does to it.
            var fetches = new JsonArray();
            for (var round = 1; round <= 2; round++)
            {
                try
                {
                    var batch = await host.Pickup.FetchAsync(state.MediatorDid, state.WalletDid, limit: 1, serviceEndpointOverride: state.Override, ct: ct).ConfigureAwait(false);
                    fetches.Add(new JsonObject
                    {
                        ["round"] = round,
                        ["items"] = batch.Items.Count,
                        ["opened"] = batch.Items.Count(x => x.Unpacked is not null),
                        ["failed"] = new JsonArray(batch.Items.Where(x => x.Error is not null).Select(x => (JsonNode?)x.Error!.GetType().Name).ToArray()),
                        ["drained"] = batch.Drained,
                    });
                }
                catch (Exception ex)
                {
                    fetches.Add(new JsonObject { ["round"] = round, ["error"] = CommandResult.Describe(ex) });
                }
            }
            value["fetch_limit_1"] = fetches;
            value["drain_after"] = await DrainAsync(ct).ConfigureAwait(false);
            return value;
        }

        // O-8 ------------------------------------------------------------------------------
        public async Task<JsonObject> LimitAsync(JsonArray answers, CancellationToken ct)
        {
            var value = Begin();
            value["drain_before"] = await DrainAsync(ct).ConfigureAwait(false);
            var forwards = new List<string>();
            for (var n = 1; n <= 12; n++)
                forwards.Add(await ForwardForRoutedAsync($"O-8 burst {n}", ct).ConfigureAwait(false));
            var watch = Stopwatch.StartNew();
            var statuses = await Task.WhenAll(forwards.Select(f => PostForwardAsync(f, ct))).ConfigureAwait(false);
            value["posts"] = new JsonObject { ["statuses"] = new JsonArray(statuses.Select(s => (JsonNode?)s).ToArray()), ["elapsed_ms"] = watch.ElapsedMilliseconds };
            value["queued"] = await WaitForCountAsync(12, ct).ConfigureAwait(false);

            var (_, answer, record) = await probe.AskAsync(probe.Request(RawProbe.Pickup3 + "/delivery-request", new JsonObject { ["limit"] = 5 }), ct).ConfigureAwait(false);
            Keep(answers, "O-8 delivery-request limit 5", record);
            var ids = (answer?.Message.Attachments ?? []).Select(a => a.Id).Where(x => x is not null).Select(x => x!).ToArray();
            value["limit_5_returned"] = ids.Length;
            value["attachments"] = new JsonArray(RawProbe.ReadAttachments(answer?.Message ?? new Message()).Take(3).Select(a => (JsonNode?)a.Facts).ToArray());

            var ack = await probe.PostAsync(await probe.AuthcryptAsync(probe.Request(RawProbe.Pickup3 + "/messages-received",
                new JsonObject { ["message_id_list"] = new JsonArray(ids.Select(x => (JsonNode?)x).ToArray()) }), ct: ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            value["messages_received_all_ids"] = new JsonObject { ["ids"] = ids.Length, ["wire"] = ack.Describe() };
            var after = await StatusCountAsync(ct).ConfigureAwait(false);
            value["status_after"] = after;
            value["removed_by_that_one_messages_received"] = after is { } left ? ids.Length - left : null;
            value["drain_after"] = await DrainAsync(ct).ConfigureAwait(false);
            return value;
        }

        // O-9 ------------------------------------------------------------------------------
        public async Task<JsonObject> AckAsync(CancellationToken ct)
        {
            var value = Begin();
            value["drain_before"] = await DrainAsync(ct).ConfigureAwait(false);
            value["post"] = await PostForwardAsync(await ForwardForRoutedAsync("O-9 ack me", ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            value["queued"] = await WaitForCountAsync(1, ct).ConfigureAwait(false);
            var (_, answer, _) = await probe.AskAsync(probe.Request(RawProbe.Pickup3 + "/delivery-request", new JsonObject { ["limit"] = 10 }), ct).ConfigureAwait(false);
            var ids = (answer?.Message.Attachments ?? []).Select(a => a.Id).Where(x => x is not null).Select(x => x!).ToArray();
            value["delivered_ids"] = ids.Length;

            // The shipped client's acknowledgement, whatever it does today.
            var watch = Stopwatch.StartNew();
            try
            {
                var status = await host.Pickup.AckAsync(state.MediatorDid, state.WalletDid, ids, state.Override, ct).ConfigureAwait(false);
                value["ack_async"] = new JsonObject { ["outcome"] = "status", ["message_count"] = status.MessageCount, ["elapsed_ms"] = watch.ElapsedMilliseconds };
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                value["ack_async"] = new JsonObject { ["outcome"] = "exception", ["exception"] = CommandResult.Describe(ex), ["elapsed_ms"] = watch.ElapsedMilliseconds };
            }
            value["status_after_ack_async"] = await StatusCountAsync(ct).ConfigureAwait(false);

            // The raw form: one messages-received for an id that no longer exists, timed.
            var raw = await probe.PostAsync(await probe.AuthcryptAsync(probe.Request(RawProbe.Pickup3 + "/messages-received",
                new JsonObject { ["message_id_list"] = new JsonArray((JsonNode?)(ids.FirstOrDefault() ?? "nothing")) }), ct: ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            value["raw_messages_received"] = raw.Describe();
            return value;
        }

        // O-11 -----------------------------------------------------------------------------
        public async Task<JsonObject> RewrapAsync(string? triggers, JsonArray answers, CancellationToken ct)
        {
            var value = Begin();
            var cases = new List<(string Name, string Outer, string? Inner)>();
            var (_, ourInner, ourOuter) = await probe.BuildForwardAsync(senderDid, state.RoutedDid, state.RoutedDid, "O-11 ours", ct).ConfigureAwait(false);
            cases.Add(("didcomm-dotnet (this probe)", ourOuter, ourInner));
            if (triggers is not null && Directory.Exists(triggers))
            {
                foreach (var file in Directory.GetFiles(triggers, "*.forward.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(file)[..^".forward.json".Length];
                    var innerPath = Path.Combine(triggers, name + ".inner.json");
                    cases.Add(("trigger " + name, File.ReadAllText(file), File.Exists(innerPath) ? File.ReadAllText(innerPath) : null));
                }
            }
            // The trigger names only: the directory is a runner temp path, not data about the mediator.
            value["triggers"] = new JsonArray(cases.Skip(1).Select(c => (JsonNode?)c.Name).ToArray());

            var results = new JsonArray();
            foreach (var (name, outer, inner) in cases)
            {
                var entry = new JsonObject { ["case"] = name };
                try
                {
                    entry["drain_before"] = await DrainAsync(ct).ConfigureAwait(false);
                    entry["post"] = (await probe.PostAsync(outer, ct).ConfigureAwait(false)).Describe();
                    entry["queued"] = await WaitForCountAsync(1, ct).ConfigureAwait(false);
                    var (_, answer, record) = await probe.AskAsync(probe.Request(RawProbe.Pickup3 + "/delivery-request", new JsonObject { ["limit"] = 10 }), ct).ConfigureAwait(false);
                    Keep(answers, "O-11 delivery-request (" + name + ")", record);
                    if (answer is not null)
                    {
                        entry["delivery_please_ack"] = record["answer"]?["please_ack"]?.DeepClone();
                        entry["delivery_return_route"] = record["answer"]?["return_route"]?.DeepClone();
                        var attachments = RawProbe.ReadAttachments(answer.Message);
                        entry["attachments"] = new JsonArray(attachments.Select(a => (JsonNode?)a.Facts.DeepClone()).ToArray());
                        // The queue was drained first, but pick ours by content anyway: the best match wins.
                        var compared = attachments.Where(a => a.Bytes is not null).Select(a => (a.Bytes, Verdict: inner is null ? "no inner file" : RawProbe.Compare(a.Bytes!, inner))).ToArray();
                        var best = compared.FirstOrDefault(c => c.Verdict == "byte-equal");
                        if (best.Bytes is null)
                            best = compared.FirstOrDefault(c => c.Verdict == "json-equal");
                        if (best.Bytes is null && compared.Length == 1)
                            best = compared[0];
                        entry["against_sender_inner"] = best.Bytes is not null ? best.Verdict : $"different (none of {compared.Length} attachment(s) matched)";
                        if (best.Bytes is { } bytes)
                        {
                            try
                            {
                                var opened = await probe.Client.UnpackAsync(Encoding.UTF8.GetString(bytes), ct).ConfigureAwait(false);
                                entry["delivered_opened_by_routed_did"] = opened.RecipientKid?.StartsWith(state.RoutedDid + "#", StringComparison.Ordinal);
                                entry["delivered_please_ack"] = opened.Message.PleaseAck is { } pa ? new JsonArray(pa.Select(x => (JsonNode?)x).ToArray()) : null;
                            }
                            catch (Exception ex)
                            {
                                entry["delivered_unpack_error"] = CommandResult.Describe(ex);
                            }
                        }
                    }
                    entry["drain_after"] = await DrainAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    entry["error"] = CommandResult.Describe(ex);
                }
                results.Add(entry);
            }
            value["cases"] = results;
            return value;
        }

        // O-12 -----------------------------------------------------------------------------
        public async Task<JsonObject> DejavuAsync(CancellationToken ct)
        {
            var value = Begin();
            var packed = await probe.AuthcryptAsync(probe.Request(RawProbe.Pickup3 + "/status-request", new JsonObject()), ct: ct).ConfigureAwait(false);
            var first = await probe.PostAsync(packed, ct).ConfigureAwait(false);
            var second = await probe.PostAsync(packed, ct).ConfigureAwait(false);
            value["first"] = first.Describe();
            value["second"] = second.Describe();
            foreach (var (name, wire) in new[] { ("first", first), ("second", second) })
            {
                if (wire.Body.Length == 0)
                    continue;
                try
                {
                    var answer = await probe.Client.UnpackAsync(wire.Body, ct).ConfigureAwait(false);
                    value[name + "_answer_type"] = answer.Message.Type;
                    value[name + "_problem_code"] = answer.Message.Body?["code"]?.ToString();
                }
                catch (Exception ex)
                {
                    value[name + "_unpack_error"] = CommandResult.Describe(ex);
                }
            }
            return value;
        }

        // O-13 -----------------------------------------------------------------------------
        public async Task<JsonObject> MalformedAsync(CancellationToken ct)
        {
            var value = Begin();
            var malformed = probe.Request(RawProbe.Cm2 + "/keylist-update", new JsonObject { ["updates"] = 5 });
            var (wire, answer, _) = await probe.AskAsync(malformed, ct).ConfigureAwait(false);
            value["keylist_update_updates_5"] = new JsonObject
            {
                ["wire"] = wire.Describe(),
                ["answer_type"] = answer?.Message.Type,
                ["problem_code"] = answer?.Message.Body?["code"]?.ToString(),
            };
            var junk = await probe.PostAsync("this is not a DIDComm envelope", ct).ConfigureAwait(false);
            value["unparseable_body"] = junk.Describe();
            value["unparseable_body_head"] = junk.Body.Length > 200 ? junk.Body[..200] : junk.Body;
            return value;
        }
    }
}
