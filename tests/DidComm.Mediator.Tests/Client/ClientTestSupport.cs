using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Json;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCrypto;
using NetDid.Core;

namespace DidComm.Mediator.Tests.Client;

/// <summary>The envelope an answer arrives in (the §9 identity matrix).</summary>
public enum AnswerEnvelope
{
    /// <summary>authcrypt(plaintext): the only shape a mediator answer may have.</summary>
    Authcrypt,

    /// <summary>authcrypt(sign(plaintext)) by the same DID.</summary>
    AuthcryptOfSigned,

    /// <summary>anoncrypt(authcrypt(plaintext)): the sender key protected, still an authcrypt layer.</summary>
    AnoncryptOfAuthcrypt,

    /// <summary>anoncrypt(plaintext): no sender key.</summary>
    Anoncrypt,

    /// <summary>A JWS by the sender, not encrypted.</summary>
    SignedOnly,

    /// <summary>anoncrypt(sign(plaintext)): signature-authenticated, no authcrypt layer.</summary>
    AnoncryptOfSigned,

    /// <summary>No protection at all.</summary>
    Plaintext,
}

/// <summary>
/// Real did:peer parties for the #96 client tests (design §9 "Unit, without handlers"): one
/// container that holds every party's keys, so it packs as the mediator (or Mallory) and unpacks as
/// the recipient — the unpack registers the verified snapshot the dispatcher would hand the
/// channel. <see cref="Mediator"/> is the mediator, <see cref="Local"/> the recipient DID the client
/// speaks as, <see cref="OtherLocal"/> a second DID of the same agent, <see cref="Mallory"/> a
/// stranger and <see cref="Alice"/> a sender. Engineers B and C reuse it.
/// </summary>
internal sealed class ClientWorld : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private ClientWorld(ServiceProvider provider, InMemorySecretsResolver secrets, PeerIdentity mediator, PeerIdentity local, PeerIdentity otherLocal, PeerIdentity mallory, PeerIdentity alice)
    {
        _provider = provider;
        Secrets = secrets;
        Mediator = mediator;
        Local = local;
        OtherLocal = otherLocal;
        Mallory = mallory;
        Alice = alice;
    }

    public PeerIdentity Mediator { get; }
    public PeerIdentity Local { get; }
    public PeerIdentity OtherLocal { get; }
    public PeerIdentity Mallory { get; }
    public PeerIdentity Alice { get; }
    public InMemorySecretsResolver Secrets { get; }
    public IServiceProvider Services => _provider;

    /// <summary>The facade that holds every party's keys.</summary>
    public DidCommClient Client => _provider.GetRequiredService<DidCommClient>();

    /// <summary>Every DID and key id of every party: what no client log line may contain.</summary>
    public IEnumerable<string> Identifiers
        => new[] { Mediator, Local, OtherLocal, Mallory, Alice }.SelectMany(p => p.Privates.Select(k => k.Kid!).Append(p.Did));

    public static async Task<ClientWorld> CreateAsync(Action<DidCommOptions>? didComm = null)
    {
        var secrets = new InMemorySecretsResolver();
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.Configure(o =>
            {
                // The example hosts do not resolve; the guard still classifies IP literals.
                o.OutboundEndpointPolicy.ResolveDnsNames = false;
                didComm?.Invoke(o);
            });
        });
        var provider = services.BuildServiceProvider();
        async Task<PeerIdentity> Mint()
        {
            var peer = await PeerIdentityFactory.CreateAsync(
                provider.GetRequiredService<IDidManager>(),
                provider.GetRequiredService<IKeyGenerator>(),
                provider.GetRequiredService<ICryptoProvider>());
            foreach (var jwk in peer.Privates)
                secrets.Add(jwk);
            return peer;
        }
        return new ClientWorld(provider, secrets, await Mint(), await Mint(), await Mint(), await Mint(), await Mint());
    }

    /// <summary>Pack <paramref name="message"/> from <paramref name="from"/> (default: its <c>from</c>, else the mediator) to <paramref name="to"/> (default: the local DID) in <paramref name="shape"/>.</summary>
    public async Task<string> PackAsync(Message message, AnswerEnvelope shape = AnswerEnvelope.Authcrypt, string? from = null, string? to = null)
    {
        var sender = from ?? message.From ?? Mediator.Did;
        var recipients = new[] { to ?? Local.Did };
        return shape switch
        {
            AnswerEnvelope.Authcrypt => (await Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: recipients, From: sender))).Message,
            AnswerEnvelope.AuthcryptOfSigned => (await Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: recipients, From: sender, SignFrom: sender))).Message,
            AnswerEnvelope.AnoncryptOfAuthcrypt => (await Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: recipients, From: sender, ProtectSender: true))).Message,
            AnswerEnvelope.Anoncrypt => (await Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: recipients))).Message,
            AnswerEnvelope.SignedOnly => await Client.PackSignedAsync(message, sender),
            AnswerEnvelope.AnoncryptOfSigned => (await Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: recipients, SignFrom: sender))).Message,
            AnswerEnvelope.Plaintext => await Client.PackPlaintextAsync(message),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    /// <summary>Pack and unpack <paramref name="message"/> as the recipient: what <c>SendResult.Returned</c> carries.</summary>
    public async Task<UnpackResult> ReceiveAsync(Message message, AnswerEnvelope shape = AnswerEnvelope.Authcrypt, string? from = null, string? to = null)
        => await Client.UnpackAsync(await PackAsync(message, shape, from, to));

    /// <summary>The verified snapshot unpack registered for <paramref name="message"/>: what the dispatcher hands the channel.</summary>
    public async Task<InboundMessageSnapshot> SnapshotAsync(Message message, AnswerEnvelope shape = AnswerEnvelope.Authcrypt, string? from = null, string? to = null)
        => SnapshotOf(await ReceiveAsync(message, shape, from, to));

    /// <summary>The verified snapshot of an unpack result.</summary>
    public static InboundMessageSnapshot SnapshotOf(UnpackResult result)
        => InboundMessageSnapshot.TryGetFor(result.Message, out var snapshot)
            ? snapshot
            : throw new InvalidOperationException("The result has no verified snapshot.");

    /// <summary>A message of <paramref name="type"/> from <paramref name="from"/> (default: the mediator) to <paramref name="to"/> (default: the local DID).</summary>
    public Message Answer(string type, string? thid, string? from = null, string? to = null, JsonObject? body = null, string? pthid = null)
    {
        var builder = new MessageBuilder().WithType(type).WithFrom(from ?? Mediator.Did).WithTo(to ?? Local.Did).WithBody(body ?? new JsonObject());
        if (thid is not null)
            builder.WithThid(thid);
        if (pthid is not null)
            builder.WithPthid(pthid);
        return builder.Build();
    }

    /// <summary>A <c>mediate-grant</c> answering <paramref name="thid"/>.</summary>
    public Message Grant(string? thid, string? from = null, string? to = null)
        => Answer(CoordinateMediation.MediateGrantType, thid, from, to, new JsonObject { ["routing_did"] = new JsonArray(Mediator.Did) });

    /// <summary>A <c>status</c> answering <paramref name="thid"/>.</summary>
    public Message Status(string? thid, int count = 0, string? from = null, string? to = null)
        => Answer(MessagePickup.StatusType, thid, from, to, new JsonObject { ["message_count"] = count, ["live_delivery"] = false });

    /// <summary>A <c>delivery</c> answering <paramref name="thid"/> (none: a live push), with no attachments.</summary>
    public Message Delivery(string? thid, string? from = null, string? to = null)
        => Answer(MessagePickup.DeliveryType, thid, from, to);

    /// <summary>A problem report whose <c>pthid</c> (or, with <paramref name="inThid"/>, <c>thid</c> only) names <paramref name="requestId"/>.</summary>
    public Message Problem(string requestId, string code = "e.p.req.not-enrolled", bool inThid = false, string? from = null, string? to = null)
    {
        var report = Protocols.ProblemReport.ProblemReport.Create(from ?? Mediator.Did, to ?? Local.Did, code, pthid: requestId);
        if (inThid)
        {
            report.Pthid = null;
            report.Thid = requestId;
        }
        return report;
    }

    /// <summary>A request the client sends (any request type; the channel never reads its body).</summary>
    public Message Request(string type = MessagePickup.StatusRequestType, string? from = null, string? to = null)
        => new MessageBuilder(UuidV4MessageIdGenerator.Instance).WithType(type).WithFrom(from ?? Local.Did).WithTo(to ?? Mediator.Did).WithBody(new JsonObject()).Build();

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}

/// <summary>
/// The send step of a channel under test: records every call (the message, the options and the
/// token it was handed) and answers with <see cref="Respond"/> — by default an accepted send with
/// nothing returned, as a WebSocket send or an HTTP 202 looks.
/// </summary>
internal sealed class FakeSend
{
    private readonly TaskCompletionSource _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;

    public ConcurrentQueue<(Message Message, SendOptions Options, CancellationToken Token)> Calls { get; } = new();

    /// <summary>How many sends were attempted.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Completes when the first send starts (a rendezvous; never a sleep).</summary>
    public Task FirstCall => _firstCall.Task;

    /// <summary>The answer to a send; by default accepted with nothing returned.</summary>
    public Func<Message, SendOptions, CancellationToken, Task<SendResult>> Respond { get; set; }
        = (_, options, _) => Task.FromResult(Accepted(options.ServiceEndpointOverride ?? new Uri("https://mediator.example/")));

    public Task<SendResult> SendAsync(Message message, SendOptions options, CancellationToken ct)
    {
        Calls.Enqueue((message, options, ct));
        Interlocked.Increment(ref _count);
        _firstCall.TrySetResult();
        return Respond(message, options, ct);
    }

    /// <summary>An accepted send (HTTP <paramref name="status"/>) whose response carried <paramref name="returned"/>.</summary>
    public static SendResult Accepted(Uri endpoint, UnpackResult? returned = null, int status = 202)
        => new(new PackEncryptedResult("{}", ServiceEndpoint: null, Array.Empty<string>()), new TransportResult(Accepted: true, HttpStatusCode: status), endpoint) { Returned = returned };

    /// <summary>Answer every send with <paramref name="returned"/> computed from the request (an HTTP 200 carrying it).</summary>
    public void ReturnWith(Func<Message, Task<UnpackResult?>> returned)
        => Respond = async (message, options, _) => Accepted(options.ServiceEndpointOverride!, await returned(message), status: 200);
}

/// <summary>Specs as the facades define them (one per request type and version).</summary>
internal static class ClientSpecs
{
    public static readonly MediatorExchangeSpec MediateRequest = new("mediate-request",
        new[] { MessageTypeUri.Parse(CoordinateMediation.MediateGrantType), MessageTypeUri.Parse(CoordinateMediation.MediateDenyType) }, ExpectsDelivery: false);

    public static readonly MediatorExchangeSpec StatusRequest = new("status-request",
        new[] { MessageTypeUri.Parse(MessagePickup.StatusType) }, ExpectsDelivery: false);

    public static readonly MediatorExchangeSpec DeliveryRequest = new("delivery-request",
        new[] { MessageTypeUri.Parse(MessagePickup.DeliveryType), MessageTypeUri.Parse(MessagePickup.StatusType) }, ExpectsDelivery: true);

    public static readonly MediatorExchangeSpec DiscoverFeatures = new("queries",
        new[] { MessageTypeUri.Parse(Protocols.DiscoverFeatures.DiscoverFeatures.DiscloseType) }, ExpectsDelivery: false);
}

/// <summary>Channels over a <see cref="FakeSend"/>, built the way production builds them otherwise.</summary>
internal static class ClientChannels
{
    public static readonly Uri Https = new("https://mediator.example/didcomm");
    public static readonly Uri Wss = new("wss://mediator.example/ws");
    public static readonly MediatorRoute HttpRoute = new(Https, IsHttp: true, IsWebSocket: false);
    public static readonly MediatorRoute WebSocketRoute = new(Wss, IsHttp: false, IsWebSocket: true);

    public static MediatorClientChannel Create(
        ClientWorld world,
        FakeSend send,
        Action<MediatorClientOptions>? configure = null,
        bool receivesInbound = true,
        TimeProvider? time = null,
        ILoggerFactory? logs = null,
        Func<string, CancellationToken, Task<Uri>>? resolveEndpoint = null,
        Func<string, CancellationToken, Task<DeliveredUnpack>>? unpack = null)
    {
        var options = new MediatorClientOptions();
        configure?.Invoke(options);
        return new MediatorClientChannel(world.Client, options, receivesInbound, time ?? TimeProvider.System, logs, send.SendAsync, resolveEndpoint, unpack);
    }

    /// <summary>The channel as the dispatcher sees it.</summary>
    public static IInboundCorrelator AsCorrelator(this MediatorClientChannel channel) => channel;
}

/// <summary>
/// Verified snapshots with chosen trust fields, registered the way unpack registers them — for
/// matcher rules a real envelope cannot violate (unpack refuses <c>from</c> ≠ the sender key's DID,
/// for one), so each rule can be shown to matter on its own.
/// </summary>
internal static class CraftedSnapshot
{
    public static InboundMessageSnapshot Verified(
        Message message,
        string? senderKid,
        string? recipientKid,
        string? signerKid = null,
        bool encrypted = true,
        bool authenticated = true)
    {
        var json = JsonSerializer.Serialize(message, DidCommJson.Default);
        InboundMessageSnapshot.RegisterVerified(
            message, json, encrypted, authenticated, nonRepudiation: signerKid is not null, anonymousSender: !authenticated,
            senderKid, signerKid, recipientKid, RecipientAddressing.Addressed);
        return InboundMessageSnapshot.TryGetFor(message, out var snapshot) ? snapshot : throw new InvalidOperationException();
    }
}

/// <summary>One captured log line of any category.</summary>
internal sealed record ClientLogEntry(string Category, LogLevel Level, string Formatted, IReadOnlyDictionary<string, object?> State, Exception? Exception)
{
    /// <summary>The rendered line plus every structured value, so a search covers what any provider could write.</summary>
    public string Everything => Formatted + Environment.NewLine + (Exception?.ToString() ?? string.Empty) + Environment.NewLine
        + string.Join(Environment.NewLine, State.Select(p => p.Key + "=" + (p.Value?.ToString() ?? string.Empty)));
}

/// <summary>A logger provider (and factory) that records every line at every level.</summary>
internal sealed class ClientLogCapture : ILoggerProvider, ILoggerFactory
{
    public ConcurrentQueue<ClientLogEntry> Entries { get; } = new();

    /// <summary>The lines of one category.</summary>
    public IReadOnlyList<ClientLogEntry> Of<T>() => Entries.Where(e => e.Category == typeof(T).FullName).ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private sealed class Logger(ClientLogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                    values[pair.Key] = pair.Value;
            }
            owner.Entries.Enqueue(new ClientLogEntry(category, logLevel, formatter(state, exception), values, exception));
        }
    }
}
