using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Demonstrates the <see cref="IProtocolHandler"/> extension point (FR-PROTO-03) by spinning
/// up a one-file <c>lets_do_lunch</c> protocol — Alice proposes a time, Bob's handler accepts
/// it. The same DI plumbing that wires Trust Ping ships custom protocols.
/// </summary>
/// <remarks>
/// <para>
/// The handler is registered post-bootstrap via direct
/// <see cref="ProtocolHandlerRegistry.Register"/> for the cookbook flow (so the section is
/// self-contained); in real apps you'd register via <c>b.AddProtocol&lt;LunchHandler&gt;()</c>
/// inside the <c>AddDidComm</c> callback.
/// </para>
/// <para>Maps to PRD §14.2 task <strong>X</strong> (FR-PROTO-03).</para>
/// </remarks>
public static class Section_X_CustomHandler
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("X", "Custom IProtocolHandler (lets_do_lunch)");

        // Wire the custom handler onto the shared registry post-bootstrap. Real apps add it
        // inside the AddDidComm callback via b.AddProtocol<LunchHandler>().
        var registry = ctx.ServiceProvider.GetRequiredService<ProtocolHandlerRegistry>();
        registry.Register(new LunchHandler());
        var dispatcher = ctx.ServiceProvider.GetRequiredService<ProtocolDispatcher>();
        var options = ctx.ServiceProvider.GetRequiredService<IOptions<DidCommOptions>>().Value;

        var proposal = new MessageBuilder()
            .WithType(LunchHandler.ProposalType)
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"when":"2026-05-27T12:30:00Z","where":"Pier 39"}""")!.AsObject())
            .Build();

        ctx.Narrator.Step("Alice proposes lunch with a custom protocol.");
        var packed = (await ctx.Client.PackEncryptedAsync(proposal, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did }, From: ctx.Alice.Did))).Message;
        var unpacked = await ctx.Client.UnpackAsync(packed);

        var outcome = await dispatcher.DispatchAsync(unpacked, ctx.Client, options);
        ctx.Narrator.Value("Dispatched to handler", outcome.Handler?.ProtocolUri);
        ctx.Narrator.Value("Reply.Type", outcome.Reply?.Type);
        ctx.Narrator.Value("Reply.Thid == proposal.Id", outcome.Reply?.Thid == proposal.Id);
        ctx.Narrator.Value("Reply.Body[\"accepted\"]", outcome.Reply?.Body?["accepted"]?.GetValue<bool>());
    }

    /// <summary>
    /// A toy custom handler for the unofficial <c>lets_do_lunch/1.0</c> protocol: accepts every
    /// proposal with <c>{"accepted": true}</c> threaded to the proposal's id.
    /// </summary>
    private sealed class LunchHandler : IProtocolHandler
    {
        public const string ProtocolUriValue = "https://didcomm.org/lets-do-lunch/1.0";
        public const string ProposalType = "https://didcomm.org/lets-do-lunch/1.0/proposal";
        public const string ResponseType = "https://didcomm.org/lets-do-lunch/1.0/response";

        public string ProtocolUri => ProtocolUriValue;

        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
        {
            if (!string.Equals(message.Type, ProposalType, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<Message?>(null);
            if (string.IsNullOrEmpty(message.From) || message.To is not { Count: > 0 })
                return Task.FromResult<Message?>(null);

            var reply = new MessageBuilder()
                .WithType(ResponseType)
                .WithFrom(message.To[0])
                .WithTo(message.From)
                .WithThid(message.Id)
                .WithBody(new JsonObject { ["accepted"] = true })
                .Build();
            return Task.FromResult<Message?>(reply);
        }
    }
}
