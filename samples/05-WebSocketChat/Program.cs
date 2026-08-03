using System.IO;
using System.Text;
using DidComm.Exceptions;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Samples.Shared;
using DidComm.Transports;
using DidComm.Transports.Stomp;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.DependencyInjection;

// Alias the static protocol API classes so their same-named namespaces don't shadow them.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;
using TrustPingApi = DidComm.Protocols.TrustPing.TrustPing;

namespace DidComm.Samples.WebSocketChat;

/// <summary>
/// Two agents talking DIDComm over WebSocket (PRD §14.3 sample 05, tasks R/S plus T):
/// trust-ping liveness, a discover-features handshake, a short scripted bidirectional chat
/// through a custom Basic Message 2.0 handler, and a reconnect-after-drop demonstration that
/// surfaces the transport's lifecycle events and exponential-backoff recovery (FR-TRN-09..11,
/// FR-PROTO-04/05).
/// <see cref="Main"/> is the CLI; <see cref="RunAsync"/> is the testable seam invoked by the
/// InteropTests smoke test (FR-DX-02 — dynamic ports, loopback only, every wait is a concrete
/// signal with a bound, never a sleep).
/// </summary>
public static class Program
{
    /// <summary>CLI entry point — writes to <see cref="Console.Out"/> and exits 0 on success.</summary>
    public static async Task<int> Main()
    {
        try
        {
            await RunAsync(Console.Out).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WebSocketChat failed: {ex}");
            return 1;
        }
    }

    /// <summary>Run the whole conversation, writing the narration to <paramref name="output"/>.</summary>
    /// <param name="output">Destination for narrator output. <c>null</c> uses <see cref="Console.Out"/>.</param>
    public static async Task RunAsync(TextWriter? output = null)
    {
        var narrator = output is null ? new Narrator() : new Narrator(output);

        // ── 1. Two independent agents ────────────────────────────────────────────────────
        narrator.Section("1", "Start two agents over WebSocket (dynamic ports)");

        await using var alice = await ChatAgent.StartAsync("alice", narrator);
        await using var bob = await ChatAgent.StartAsync("bob", narrator);

        // Each agent knows where the other listens — in production this comes from the peer's
        // DID document or an out-of-band invitation (sample 06); here it's exchanged directly.
        alice.PeerEndpoint = bob.Endpoint;
        bob.PeerEndpoint = alice.Endpoint;

        narrator.Value("alice", $"{Trunc(alice.Identity.Did, 56)} @ {alice.Endpoint}");
        narrator.Value("bob", $"{Trunc(bob.Identity.Did, 56)} @ {bob.Endpoint}");

        // The transport raises lifecycle events for observability (FR-TRN-11) — watch Alice's.
        var aliceTransport = alice.App.Services.GetRequiredService<WebSocketDidCommTransport>();
        aliceTransport.Lifecycle += (_, args) =>
            narrator.Step($"[alice transport] {args.Kind} → {args.Endpoint}");

        // Narrate chat lines as each side's handler records them.
        alice.Chat.OnLine = line => narrator.Step($"[alice] received: \"{line}\"");
        bob.Chat.OnLine = line => narrator.Step($"[bob] received: \"{line}\"");

        try
        {
            // ── 2. Trust ping ────────────────────────────────────────────────────────────
            narrator.Section("2", "Trust ping — is anybody out there? (FR-PROTO-04)");

            // Alice probes liveness; Bob's registered TrustPingHandler auto-replies with a
            // ping-response threaded to the ping's id, delivered back over Bob's own transport.
            var ping = TrustPingApi.CreatePing(from: alice.Identity.Did, to: bob.Identity.Did);
            await alice.Client.SendAsync(ping, new SendOptions(
                Recipients: new[] { bob.Identity.Did },
                From: alice.Identity.Did,
                ServiceEndpointOverride: bob.Endpoint));
            narrator.Step($"[alice] sent ping (id = {ping.Id[..8]}…) as one binary WebSocket message (FR-TRN-09).");

            var pong = await alice.WaitForInboundAsync(
                r => string.Equals(r.Message.Type, TrustPingApi.ResponseType, StringComparison.Ordinal)
                     && string.Equals(r.Message.Thid, ping.Id, StringComparison.Ordinal),
                "Bob's ping-response");
            narrator.Value("ping-response thid == ping.id", pong.Message.Thid == ping.Id);
            narrator.Value("ping-response authenticated", pong.Authenticated);

            // ── 3. Discover features ─────────────────────────────────────────────────────
            narrator.Section("3", "Discover features — what can Bob speak? (FR-PROTO-05)");

            // The initiator client sends a `queries`, then awaits Bob's correlated `disclose`.
            // Bob answers from his protocol registry; his disclose arrives at Alice's receive
            // endpoint and completes the pending call — a real two-endpoint round trip.
            var discover = alice.App.Services.GetRequiredService<DiscoverFeaturesClient>();
            var disclosures = await discover.QueryFeaturesAsync(
                from: alice.Identity.Did,
                to: bob.Identity.Did,
                queries: new[]
                {
                    new FeatureQuery { FeatureType = DiscoverFeaturesApi.FeatureTypeProtocol, Match = "https://didcomm.org/*" },
                },
                timeout: TimeSpan.FromSeconds(30),
                serviceEndpointOverride: bob.Endpoint);

            narrator.Value("Disclosed protocols", disclosures.Count);
            foreach (var d in disclosures)
                narrator.Value($"- {d.FeatureType}", d.Id);
            narrator.Note("The chat protocol below shows up too — Bob's registry advertises every handler he registered, including custom ones.");

            // ── 4. Chat ──────────────────────────────────────────────────────────────────
            narrator.Section("4", "Chat — Basic Message 2.0 through a custom handler");

            // Bob's side of the conversation is scripted so the run is deterministic; each
            // inbound chat line dequeues one reply, threaded via thid.
            bob.Chat.EnqueueScriptedReply("Loud and clear, Alice.");
            bob.Chat.EnqueueScriptedReply("Ship it.");

            await alice.SendChatAsync(bob.Identity.Did, "Hello Bob — one envelope per WebSocket message.");
            await alice.Chat.NextLineAsync("Bob's first reply");

            await alice.SendChatAsync(bob.Identity.Did, "Envelope tour is green. Ready to ship?");
            await alice.Chat.NextLineAsync("Bob's second reply");

            // ── 5. Reconnect after drop ──────────────────────────────────────────────────
            narrator.Section("5", "Reconnect after drop — lifecycle events + backoff (FR-TRN-11)");

            narrator.Step("[bob] goes offline (host stopped, connections aborted).");
            await bob.StopAsync();

            // While Bob is down, Alice's send exhausts its (shortened) reconnect budget —
            // 100 ms base, exponential, 2 attempts; the library defaults are 1 s base / 30 s
            // cap / 0.5 jitter with 5 attempts (DD-05). The first attempt can appear to
            // succeed because the broken socket only reports the peer's death on the next
            // write, so we probe until the failure surfaces — bounded, no sleeps.
            var refused = false;
            for (var attempt = 1; attempt <= 5 && !refused; attempt++)
            {
                try
                {
                    await alice.SendChatAsync(bob.Identity.Did, "Bob, are you there?");
                }
                catch (TransportException ex)
                {
                    refused = true;
                    narrator.Step("[alice] offline send refused after exhausting the reconnect budget:");
                    narrator.Note(ex.Message);
                }
            }
            if (!refused)
                narrator.Note("UNEXPECTED: sends kept succeeding while Bob was down.");

            narrator.Step("[bob] comes back on the SAME port — same DID, same keys, fresh process.");
            bob.Chat.EnqueueScriptedReply("Back online — nothing lost but time.");
            await bob.RestartAsync();

            // The next send finds no pooled connection, dials fresh (watch the Connected
            // lifecycle event), and the conversation resumes where it left off.
            await alice.SendChatAsync(bob.Identity.Did, "Welcome back?");
            await alice.Chat.NextLineAsync("Bob's post-reconnect reply");
            narrator.Note("Same-port restart is the deterministic stand-in for any transient drop — the recovery path (backoff, redial, resume) is identical.");

            // ── 6. STOMP framing ─────────────────────────────────────────────────────────
            narrator.Section("6", "STOMP 1.2 framing (FR-TRN-12) — the wire dialect behind UseStomp");

            // Some WebSocket infrastructure (message brokers, ActiveMQ/RabbitMQ gateways) speaks
            // STOMP rather than raw binary frames. Both ends opt in through options — the
            // sending transport via UseStomp + the destination header it should address, the
            // ASP.NET Core endpoint via DidCommReceiveOptions.UseStomp — and each envelope then
            // rides as one SEND frame. The codec those paths share is public, one call each way.
            var stompOptions = new WebSocketTransportOptions
            {
                UseStomp = true,
                StompDestination = "/queue/didcomm",
            };
            narrator.Value("Client opts in via", $"UseStomp={stompOptions.UseStomp}, destination={stompOptions.StompDestination}");

            var sendFrame = new StompFrame(
                "SEND",
                new[]
                {
                    KeyValuePair.Create("destination", stompOptions.StompDestination!),
                    KeyValuePair.Create("content-type", "application/didcomm-encrypted+json"),
                },
                Encoding.UTF8.GetBytes("""{"protected":"…a packed envelope would ride here…"}"""));
            var stompWire = StompFrameCodec.Encode(sendFrame);
            narrator.Value("Encoded SEND frame", $"{stompWire.Length} bytes on the wire");

            var decodedFrame = StompFrameCodec.Decode(stompWire);
            narrator.Value("Decoded command", decodedFrame.Command);
            narrator.Value("Decoded header count", decodedFrame.Headers.Count);
            narrator.Value("destination header", decodedFrame.TryGetHeader("destination", out var stompDest) ? stompDest : "?");
            narrator.Value("Body round-trips", Encoding.UTF8.GetString(decodedFrame.Body.Span).Length > 0);
        }
        finally
        {
            await alice.StopAsync();
            // Bob may already be stopped mid-flow if the run failed between stop and restart.
            try { await bob.StopAsync(); }
            catch (ObjectDisposedException) { /* already stopped and disposed */ }
        }
    }

    private static string Trunc(string? value, int max)
        => value is null ? "<null>" : value.Length <= max ? value : value[..(max - 1)] + "…";
}
