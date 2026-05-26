using DidComm.Facade;
using DidComm.Messages;
using DidComm.Threading;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Builds a header-only Empty 1.0 envelope, uses it to acknowledge a prior message via the
/// <c>ack</c> header, packs and unpacks it, and inspects the FR-THR-04 loop-guard predicates.
/// Demonstrates the canonical "ACK-only" wire shape — the spec text "use Empty 1.0 when only
/// an ACK is needed" — without any protocol-handler ceremony.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Message.Empty"/> is the convenience factory that pre-seeds a
/// <see cref="MessageBuilder"/> with the Empty 1.0 message type so the call site reads
/// <c>Message.Empty().WithFrom(…).WithTo(…).WithAck(…).Build()</c>.
/// </para>
/// <para>Maps to PRD §14.2 task <strong>W</strong> (FR-PROTO-06 + FR-THR-03).</para>
/// </remarks>
public static class Section_W_EmptyMessage
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("W", "Empty 1.0 (header-only ACK)");

        // Imagine Alice sent Bob a message with id "prev-message-id". Bob now wants to ACK it
        // with an empty header-only envelope rather than a substantive body.
        const string prevMessageId = "prev-message-id";

        var empty = Message.Empty()
            .WithFrom(ctx.Bob.Did)
            .WithTo(ctx.Alice.Did)
            .WithThid(prevMessageId) // continue the thread
            .WithAck(prevMessageId)  // acknowledge the prior message
            .Build();

        ctx.Narrator.Step($"Bob constructs an Empty 1.0 envelope (type={empty.Type}).");
        ctx.Narrator.Value("Body", empty.Body);
        ctx.Narrator.Value("Ack[]", string.Join(",", empty.Ack ?? new List<string>()));
        ctx.Narrator.Value("AckLoopGuard.IsPureAck", AckLoopGuard.IsPureAck(empty));
        ctx.Narrator.Value("AckLoopGuard.IsSafeToSend", AckLoopGuard.IsSafeToSend(empty));

        // Pack + unpack round-trip so the reader sees the Empty 1.0 type survive the wire.
        var packed = (await ctx.Client.PackEncryptedAsync(empty, new PackEncryptedOptions(
            Recipients: new[] { ctx.Alice.Did }, From: ctx.Bob.Did))).Message;
        var alice = await ctx.Client.UnpackAsync(packed);
        ctx.Narrator.Value("Alice unpacks ack[]", string.Join(",", alice.Message.Ack ?? new List<string>()));
        ctx.Narrator.Note("Empty 1.0 + ack[] is the canonical wire shape for an ACK-only message (FR-PROTO-06).");
    }
}
