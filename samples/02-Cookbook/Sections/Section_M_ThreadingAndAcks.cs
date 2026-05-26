using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Threading;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Threads two messages together and shows the ACK opt-in flow Alice and Bob would use to
/// confirm receipt. Demonstrates the four spec-defined building blocks: the thread id
/// (<c>thid</c>) that links a reply to its parent, the <c>please_ack</c> request, the
/// <c>ack</c> reply, and the safety predicates that prevent ACK loops.
/// </summary>
/// <remarks>
/// <para>
/// The protocol layer that actually emits ACK replies on Bob's behalf arrives in Phase 6.2.
/// This section therefore acts as a tour of the message-layer API: it builds the messages,
/// packs and unpacks them so you can confirm the headers survive the wire, and inspects the
/// <see cref="AckLoopGuard"/> predicates that the future handler will use to refuse loops.
/// </para>
/// <para>Maps to PRD §14.2 task <strong>M</strong> (FR-THR-01..04).</para>
/// </remarks>
public static class Section_M_ThreadingAndAcks
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("M", "Threading & ACKs");

        // Alice sends the opening message and asks Bob to acknowledge it.
        var opening = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"content":"Bob, did you get this?"}""")!.AsObject())
            .WithPleaseAck() // [""] — "ack the current message"
            .Build();

        ctx.Narrator.Step("Alice packs an authcrypt message and asks for an ACK.");
        var packedOpening = (await ctx.Client.PackEncryptedAsync(opening, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice.Did))).Message;

        var bobReceives = await ctx.Client.UnpackAsync(packedOpening);
        ctx.Narrator.Value("Bob sees please_ack", string.Join(",", bobReceives.Message.PleaseAck ?? new List<string>()));
        ctx.Narrator.Value("Bob sees RequestsAck", AckLoopGuard.RequestsAck(bobReceives.Message));

        // Bob replies with a pure ACK (Empty 1.0 envelope carrying just the ack header).
        // The reply continues the thread by carrying the opener's id as thid.
        var bobReply = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom(ctx.Bob.Did)
            .WithTo(ctx.Alice.Did)
            .WithThid(bobReceives.Message.Id)
            .WithAck(bobReceives.Message.Id)
            .Build();

        ctx.Narrator.Step("Bob builds a pure-ACK reply and threads it to Alice's message.");
        ctx.Narrator.Value("IsPureAck", AckLoopGuard.IsPureAck(bobReply));
        ctx.Narrator.Value("IsSafeToSend", AckLoopGuard.IsSafeToSend(bobReply));
        ctx.Narrator.Value("Reply thid", bobReply.Thid);

        var packedReply = (await ctx.Client.PackEncryptedAsync(bobReply, new PackEncryptedOptions(
            Recipients: new[] { ctx.Alice.Did },
            From: ctx.Bob.Did))).Message;
        var aliceReceives = await ctx.Client.UnpackAsync(packedReply);
        ctx.Narrator.Value("Alice sees ack[]", string.Join(",", aliceReceives.Message.Ack ?? new List<string>()));

        // FR-THR-04 rule 2: a pure ACK that ALSO asks for an ACK would spawn an infinite loop.
        // The guard rejects it before transmission.
        var brokenLoop = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom(ctx.Bob.Did)
            .WithTo(ctx.Alice.Did)
            .WithAck("some-id")
            .WithPleaseAck()
            .Build();
        ctx.Narrator.Step("Demonstrate the FR-THR-04 loop guard: pure-ACK that also asks for an ACK.");
        ctx.Narrator.Value("IsSafeToSend (loop-trap)", AckLoopGuard.IsSafeToSend(brokenLoop));
        ctx.Narrator.Note("The handler in Phase 6.2 refuses to send when IsSafeToSend is false.");
    }
}
