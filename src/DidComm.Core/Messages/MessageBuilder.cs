using System.Text.Json.Nodes;

namespace DidComm.Messages;

/// <summary>
/// Fluent builder for <see cref="Message"/> instances per FR-MSG-13. Auto-populates
/// <see cref="Message.Id"/> (via the configured <see cref="IMessageIdGenerator"/>, default
/// <see cref="UuidV4MessageIdGenerator"/>) and <see cref="Message.Typ"/>
/// (<see cref="MediaTypes.Plaintext"/>) so the minimal call site is
/// <c>new MessageBuilder().WithType("…").Build()</c>.
/// </summary>
/// <remarks>
/// The builder is not thread-safe; callers create one per message. Builder methods return
/// <c>this</c> to allow chaining; <see cref="Build"/> runs <see cref="Message.Validate"/> so a
/// successful <c>Build()</c> guarantees the returned message satisfies the §4 structural rules.
/// </remarks>
internal sealed class MessageBuilder
{
    private readonly Message _message;
    private readonly IMessageIdGenerator _idGenerator;
    private bool _idExplicitlySet;

    /// <summary>Initialize a builder using the default UUID v4 id generator (FR-MSG-03).</summary>
    public MessageBuilder() : this(UuidV4MessageIdGenerator.Instance) { }

    /// <summary>Initialize a builder using a caller-supplied id generator.</summary>
    /// <param name="idGenerator">The id generator. Carries the FR-MSG-14 uniqueness obligation.</param>
    public MessageBuilder(IMessageIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(idGenerator);
        _idGenerator = idGenerator;
        _message = new Message
        {
            Typ = MediaTypes.Plaintext,
        };
    }

    /// <summary>Set <see cref="Message.Id"/> explicitly; otherwise the id generator runs at <see cref="Build"/> time.</summary>
    /// <param name="id">Caller-supplied id. Must satisfy FR-MSG-02.</param>
    public MessageBuilder WithId(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        _message.Id = id;
        _idExplicitlySet = true;
        return this;
    }

    /// <summary>Set <see cref="Message.Type"/> (REQUIRED, FR-MSG-05).</summary>
    /// <param name="type">A valid Message Type URI per FR-PROTO-01.</param>
    public MessageBuilder WithType(string type)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        _message.Type = type;
        return this;
    }

    /// <summary>Set <see cref="Message.From"/>.</summary>
    /// <param name="from">A DID or DID URL without a fragment.</param>
    public MessageBuilder WithFrom(string from)
    {
        _message.From = from;
        return this;
    }

    /// <summary>Set <see cref="Message.To"/>.</summary>
    /// <param name="to">Recipient DIDs / DID-URLs without fragment.</param>
    public MessageBuilder WithTo(params string[] to)
    {
        _message.To = to is { Length: > 0 } ? to.ToList() : null;
        return this;
    }

    /// <summary>Set <see cref="Message.Thid"/>.</summary>
    /// <param name="thid">Thread identifier (same constraints as <see cref="Message.Id"/>).</param>
    public MessageBuilder WithThid(string thid)
    {
        _message.Thid = thid;
        return this;
    }

    /// <summary>Set <see cref="Message.Pthid"/>.</summary>
    /// <param name="pthid">Parent-thread identifier.</param>
    public MessageBuilder WithPthid(string pthid)
    {
        _message.Pthid = pthid;
        return this;
    }

    /// <summary>Set <see cref="Message.CreatedTime"/>.</summary>
    /// <param name="epochSeconds">UTC epoch seconds.</param>
    public MessageBuilder WithCreatedTime(long epochSeconds)
    {
        _message.CreatedTime = epochSeconds;
        return this;
    }

    /// <summary>Set <see cref="Message.ExpiresTime"/>.</summary>
    /// <param name="epochSeconds">UTC epoch seconds.</param>
    public MessageBuilder WithExpiresTime(long epochSeconds)
    {
        _message.ExpiresTime = epochSeconds;
        return this;
    }

    /// <summary>Set <see cref="Message.Body"/>.</summary>
    /// <param name="body">JSON object body (the JOSE-style 'body' header).</param>
    public MessageBuilder WithBody(JsonObject body)
    {
        _message.Body = body;
        return this;
    }

    /// <summary>Append an attachment to <see cref="Message.Attachments"/>.</summary>
    /// <param name="attachment">The attachment to add.</param>
    public MessageBuilder WithAttachment(Attachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        _message.Attachments ??= new List<Attachment>();
        _message.Attachments.Add(attachment);
        return this;
    }

    /// <summary>
    /// Finalize the message: populate <c>id</c> via the generator if not explicitly set, then
    /// run <see cref="Message.Validate"/> so the returned instance is guaranteed structurally
    /// valid.
    /// </summary>
    public Message Build()
    {
        if (!_idExplicitlySet)
            _message.Id = _idGenerator.NewId();

        _message.Validate();
        return _message;
    }
}
