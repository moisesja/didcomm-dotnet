using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// L-014: alias the static TrustPing API class so the namespace import doesn't shadow it.
using TrustPingApi = DidComm.Protocols.TrustPing.TrustPing;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Sends a Trust Ping 2.0 liveness probe from Alice to Bob and lets the registered Trust
/// Ping handler craft the response. Walks the four pieces a handler-driven protocol needs:
/// a built message, a packed envelope, the dispatcher resolving the right handler, and the
/// response that comes back threaded to the ping's id.
/// </summary>
/// <remarks>
/// <para>
/// Trust Ping is the simplest spec protocol — its body carries only <c>response_requested</c>
/// (default <c>true</c>) and the recipient's handler auto-replies with <c>ping-response</c>
/// whose <c>thid</c> equals the ping's <c>id</c>. Showing it as the first dispatcher section
/// makes the FR-PROTO-03 wiring concrete: register handlers → call the dispatcher → observe
/// the outcome.
/// </para>
/// <para>Maps to PRD §14.2 task <strong>S</strong> (FR-PROTO-04).</para>
/// </remarks>
public static class Section_S_TrustPing
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("S", "Trust Ping (liveness)");

        // The dispatcher + registry are wired by CookbookContext via AddBuiltInProtocols(),
        // which registers TrustPingHandler + EmptyHandler. We just resolve and use.
        var dispatcher = ctx.ServiceProvider.GetRequiredService<ProtocolDispatcher>();
        var options = ctx.ServiceProvider.GetRequiredService<IOptions<DidCommOptions>>().Value;

        // Alice builds a ping and packs it authcrypt for Bob.
        var ping = TrustPingApi.CreatePing(from: ctx.Alice.Did, to: ctx.Bob.Did);
        ctx.Narrator.Step($"Alice builds a ping (id={ping.Id[..8]}…, response_requested={TrustPingApi.IsResponseRequested(ping)}).");
        var packedPing = (await ctx.Client.PackEncryptedAsync(ping, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did }, From: ctx.Alice.Did))).Message;

        // Bob's side: unpack, then run the dispatcher to let the registered handler reply.
        var unpacked = await ctx.Client.UnpackAsync(packedPing);
        ctx.Narrator.Step("Bob unpacks the ping and dispatches it through ProtocolDispatcher.");
        var outcome = await dispatcher.DispatchAsync(unpacked, ctx.Client, options);

        ctx.Narrator.Value("DispatchResult", outcome.Result);
        ctx.Narrator.Value("Handler", outcome.Handler?.ProtocolUri);
        ctx.Narrator.Value("Reply.Type", outcome.Reply?.Type);
        ctx.Narrator.Value("Reply.Thid == ping.Id", outcome.Reply?.Thid == ping.Id);
        ctx.Narrator.Value("Reply.From / To", $"{outcome.Reply?.From} → {string.Join(",", outcome.Reply?.To ?? new List<string>())}");

        // Round-trip the reply back to Alice so the section ends with a fully closed loop.
        var packedReply = (await ctx.Client.PackEncryptedAsync(outcome.Reply!, new PackEncryptedOptions(
            Recipients: new[] { ctx.Alice.Did }, From: ctx.Bob.Did))).Message;
        var aliceReceives = await ctx.Client.UnpackAsync(packedReply);
        ctx.Narrator.Value("Alice receives ping-response thid", aliceReceives.Message.Thid);
        ctx.Narrator.Note("response_requested = false would suppress the auto-reply (FR-PROTO-04).");
    }
}
