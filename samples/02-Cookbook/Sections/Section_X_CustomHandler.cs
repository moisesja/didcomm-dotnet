using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Trace;
using DidComm.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

        // Routing runs on the type URI, and the URI decomposes: doc-uri / protocol-name /
        // major.minor / message-name. Parse an incoming one — or build it from parts — and match
        // at the granularity protocols actually evolve at: same name + same major = compatible.
        ctx.Narrator.Step("Take the type URI apart: MessageTypeUri / ProtocolIdentifier / ProtocolVersion.");
        var typeUri = MessageTypeUri.Parse(unpacked.Message.Type);
        ctx.Narrator.Value("DocUri", typeUri.DocUri);
        ctx.Narrator.Value("ProtocolName", typeUri.ProtocolName);
        ctx.Narrator.Value("Version", typeUri.Version.ToString());
        ctx.Narrator.Value("MessageType", typeUri.MessageType);
        ctx.Narrator.Value("PIURI", typeUri.ProtocolIdentifier);
        ctx.Narrator.Value("Full MTURI round-trips", typeUri.Value == unpacked.Message.Type);

        var fromParts = new MessageTypeUri(
            "https://didcomm.org", "lets-do-lunch", new ProtocolVersion(1, 0), "proposal");
        ctx.Narrator.Value("Built from parts, Matches(parsed)", fromParts.Matches(typeUri));
        ctx.Narrator.Value("IsValid(\"not-a-mturi\")", MessageTypeUri.IsValid("not-a-mturi"));
        ctx.Narrator.Value("TryParse(response type)",
            MessageTypeUri.TryParse(LunchHandler.ResponseType, out var responseUri) ? responseUri!.MessageType : "?");

        // Version arithmetic: a 1.1 speaker serves a 1.0 peer (same major); negotiation lands on
        // the lower minor; ordering is what you'd expect from semver's first two parts.
        var v10 = new ProtocolVersion(1, 0);
        ProtocolVersion.TryParse("1.1", out var v11);
        ctx.Narrator.Value("1.1 IsCompatibleWith 1.0", v11.IsCompatibleWith(v10));
        ctx.Narrator.Value("1.1 NegotiateWith 1.0", v11.NegotiateWith(v10).ToString());
        ctx.Narrator.Value("1.0 CompareTo 1.1 (< 0)", v10.CompareTo(v11) < 0);
        ctx.Narrator.Value("Major / Minor", $"{v11.Major}.{v11.Minor}");

        // The PIURI (protocol identifier without the message name) is what a registry keys on.
        var piuri = ProtocolIdentifier.Parse(LunchHandler.ProtocolUriValue);
        ProtocolIdentifier.TryParse("https://didcomm.org/lets-do-lunch/1.1", out var futureMinor);
        ctx.Narrator.Value("PIURI name @ doc", $"{piuri.ProtocolName} @ {piuri.DocUri} (v{piuri.Version})");
        ctx.Narrator.Value("Serves a 1.1 minor bump", piuri.MatchesProtocolAndMajor(futureMinor!));
        ctx.Narrator.Value("Composed PIURI",
            new ProtocolIdentifier("https://didcomm.org", "lets-do-lunch", new ProtocolVersion(1, 0)).Value);

        // The registry and dispatcher are plain classes too — assemble your own for embedded or
        // test scenarios, with the full seam list spelled out: handler registry, thread-state
        // store, logger, trace options, and protocol observers. Dispose releases the observer
        // channel; both teardown styles exist.
        ctx.Narrator.Step("Assemble a dispatcher by hand and inspect the registry.");
        var ownRegistry = new ProtocolHandlerRegistry();
        ownRegistry.Register(new LunchHandler());
        ctx.Narrator.Value("Registry.All", string.Join(", ", ownRegistry.All.Select(h => h.ProtocolUri)));
        ctx.Narrator.Value("Registry.TryResolve(proposal type)",
            ownRegistry.TryResolve(LunchHandler.ProposalType, out var resolved) && resolved is LunchHandler);

        await using (var ownDispatcher = new ProtocolDispatcher(
            ownRegistry,
            new InMemoryThreadStateStore(),
            NullLogger<ProtocolDispatcher>.Instance,
            new TraceOptions(),                        // Trace 2.0 stays off unless enabled
            Array.Empty<IProtocolObserver>()))         // await using ⇒ DisposeAsync at scope exit
        {
            var ownOutcome = await ownDispatcher.DispatchAsync(unpacked, ctx.Client, options);
            ctx.Narrator.Value("Hand-built dispatcher outcome", ownOutcome.Result);
        }

        var syncScoped = new ProtocolDispatcher(ownRegistry, new InMemoryThreadStateStore());
        syncScoped.Dispose();                          // the synchronous twin, for non-async hosts
        ctx.Narrator.Value("Dispatcher disposed (sync + async paths shown)", true);
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
