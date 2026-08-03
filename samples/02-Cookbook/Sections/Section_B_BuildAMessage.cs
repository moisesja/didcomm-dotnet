using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DidComm.Messages;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Builds a plaintext DIDComm message with the fluent <c>MessageBuilder</c> and shows what the
/// builder does for you: <c>id</c> and <c>typ</c> are auto-populated so the minimal call site
/// is just a <c>type</c> and a <c>Build()</c>, while everything else — sender, recipients,
/// thread id, creation time, application-defined extension headers — is opt-in.
/// </summary>
/// <remarks>
/// <para>
/// The section builds two messages. The first shows the auto-populated members and a custom
/// extension header (unknown headers ride in <c>AdditionalHeaders</c> and survive an
/// unpack→repack round-trip untouched). The second is a reply that continues the first one's
/// thread by setting <c>thid</c> to the original message id — the pattern every
/// request/response protocol uses. The full JSON of the first message is printed so you can
/// see exactly what goes on the wire before any envelope is applied.
/// </para>
/// <para>
/// Maps to PRD §14.2 task <strong>B</strong> (FR-MSG-13 — builder auto-population; FR-MSG-12/15 — extension headers).
/// </para>
/// </remarks>
public static class Section_B_BuildAMessage
{
    // Display-only serializer options: compact like the wire form, nulls omitted so unset
    // headers vanish (the library's own serializer behaves the same way).
    private static readonly JsonSerializerOptions DisplayOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("B", "Build a message");

        ctx.Narrator.Step("Build: only 'type' is required — 'id' (UUID v4) and 'typ' are filled in for you.");
        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithCreatedTime(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .WithBody(JsonNode.Parse("""{"content":"Hello, Bob."}""")!.AsObject())
            .Build();

        // Application-defined headers the spec doesn't know about go in AdditionalHeaders.
        // Receivers that don't understand them ignore them; the library preserves them
        // verbatim across a round-trip (FR-MSG-12/15).
        message.AdditionalHeaders = new Dictionary<string, JsonElement>
        {
            ["x-cookbook-trace"] = JsonSerializer.SerializeToElement("section-b"),
        };

        ctx.Narrator.Value("Id (auto-populated UUID v4)", message.Id);
        ctx.Narrator.Value("Typ (auto-populated)", message.Typ);
        ctx.Narrator.Value("CreatedTime (epoch seconds)", message.CreatedTime);
        ctx.Narrator.Value("Custom header x-cookbook-trace", message.AdditionalHeaders["x-cookbook-trace"]);
        ctx.Narrator.Value("JSON", JsonSerializer.Serialize(message, DisplayOptions));

        // A reply continues the conversation by pointing 'thid' at the first message's id —
        // that is all DIDComm threading needs (section M goes deeper).
        ctx.Narrator.Step("Build a reply on the same thread: thid = the first message's id.");
        var reply = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Bob.Did)
            .WithTo(ctx.Alice.Did)
            .WithThid(message.Id)
            .WithBody(JsonNode.Parse("""{"content":"Hello back, Alice."}""")!.AsObject())
            .Build();
        ctx.Narrator.Value("Reply.Thid == first.Id", string.Equals(reply.Thid, message.Id, StringComparison.Ordinal));

        // The id comes from a pluggable generator. The default is UUID v4 (the spec's
        // recommendation); pass your own IMessageIdGenerator when ids must come from elsewhere —
        // e.g. a store-and-forward queue that assigns them — or pin a single id with WithId
        // (the deterministic-replay / idempotency-key case). (FR-MSG-13)
        ctx.Narrator.Step("Control the message id: the builder's generator seam, or an explicit WithId.");
        IMessageIdGenerator generator = UuidV4MessageIdGenerator.Instance;   // the shared default instance
        ctx.Narrator.Value("Generator.NewId() sample", generator.NewId());
        var generated = new MessageBuilder(new UuidV4MessageIdGenerator())   // or your own generator
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .Build();
        ctx.Narrator.Value("Id from injected generator", generated.Id);
        var pinned = new MessageBuilder()
            .WithId("00000000-0000-4000-8000-00000000cafe")
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .Build();
        ctx.Narrator.Value("Id pinned via WithId", pinned.Id);

        // Message is also a plain mutable model — what JSON deserialization hands you. A message
        // assembled by hand (or off the wire) hasn't been through Build(), so run Validate()
        // yourself before trusting it; it throws MalformedMessageException on a §4 violation.
        ctx.Narrator.Step("Validate a hand-assembled message: new Message() + Validate().");
        var handAssembled = new Message
        {
            Id = generator.NewId(),
            Type = "https://didcomm.org/basicmessage/2.0/message",
            Body = new JsonObject { ["content"] = "assembled without the builder" },
        };
        handAssembled.Validate();                                            // throws if the §4 rules don't hold
        ctx.Narrator.Value("Hand-assembled message validates", true);

        ctx.Narrator.Note("Build() validates the message structurally — a successful Build() guarantees the §4 rules hold.");
        return Task.CompletedTask;
    }
}
