using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Tests.Client;

/// <summary>One request the pickup client handed to <see cref="FakePickupExchange.ExchangeAsync"/>.</summary>
internal sealed record SentRequest(MediatorRoute Route, Message Request, MediatorExchangeSpec Spec, string MediatorDid, string LocalDid, CancellationToken Ct)
{
    public string Type => Request.Type;

    public bool Is(string type) => string.Equals(Request.Type, type, StringComparison.Ordinal);

    /// <summary>The <c>message_id_list</c> of a <c>messages-received</c>.</summary>
    public IReadOnlyList<string> Ids => Request.Body![PickupBodies.MessageIdList]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    /// <summary>The <c>live_delivery</c> of a <c>live-delivery-change</c>.</summary>
    public bool Live => Request.Body![PickupBodies.LiveDelivery]!.GetValue<bool>();

    /// <summary>The <c>limit</c> of a <c>delivery-request</c>.</summary>
    public int Limit => Request.Body!["limit"]!.GetValue<int>();

    /// <summary>The <c>recipient_did</c> filter, or <c>null</c>.</summary>
    public string? RecipientDid => Request.Body?[PickupBodies.RecipientDid]?.GetValue<string>();

    // A failed assertion prints the request by its type and id only: the generated printer would read
    // every property, and Ids / Live / Limit throw on requests of the other types.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Type = ").Append(Type).Append(", Id = ").Append(Request.Id);
        return true;
    }
}

/// <summary>
/// A programmable <see cref="IMediatorExchange"/> for the pickup client's unit tests (engineer C): a
/// recording send log, answers per request type (synchronous by default, so a test can reason about
/// what has been sent when a call returns; gated with a <see cref="TaskCompletionSource"/> where a
/// test needs a rendezvous), real <see cref="LiveRegistration"/> mailboxes, programmable unpack
/// outcomes keyed by the payload text, the real <see cref="DuplicateFilter"/>, and a
/// <see cref="ManualTimeProvider"/>. The route rule of the channel (design §4.3: a live call on a
/// route that is not a WebSocket is refused before anything is sent) is reproduced so the client's
/// order of calls can be checked.
/// </summary>
/// <remarks>
/// Payload texts the default unpacker understands: <c>ok:{id}</c> (Alice's authcrypted message
/// <c>{id}</c>, decrypted for <see cref="Local"/>), <c>to:{did}:{id}</c> (decrypted for
/// <c>{did}</c>), <c>anon:{text}</c> (anonymous, keyed by its bytes), <c>bad:{text}</c> (a failure).
/// Anything else is a failure too.
/// </remarks>
internal sealed class FakePickupExchange : IMediatorExchange
{
    public const string M = "did:example:mediator";
    public const string L = "did:example:bob";
    public const string A = "did:example:alice";

    public static readonly MediatorRoute HttpRoute = new(new Uri("https://mediator.example/didcomm"), IsHttp: true, IsWebSocket: false);
    public static readonly MediatorRoute WebSocketRoute = new(new Uri("wss://mediator.example/didcomm"), IsHttp: false, IsWebSocket: true);

    private readonly object _gate = new();
    private readonly List<SentRequest> _sent = new();
    private readonly List<string> _unpacked = new();
    private readonly ConcurrentDictionary<string, Func<SentRequest, Task<InboundMessageSnapshot>>> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LiveRegistration> _live = new(StringComparer.Ordinal);
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakePickupExchange(Action<MediatorClientOptions>? configure = null, ManualTimeProvider? time = null)
    {
        var options = new MediatorClientOptions();
        configure?.Invoke(options);
        Options = options.ValidatedCopy();
        Clock = time ?? new ManualTimeProvider();
        Duplicates = new DuplicateFilter(Options.DedupeCapacity);
        Versions = new NegotiatedVersionCache(Clock);
    }

    public MediatorClientOptions Options { get; }

    public TimeProvider Time => Clock;

    public ManualTimeProvider Clock { get; }

    public DuplicateFilter Duplicates { get; }

    public NegotiatedVersionCache Versions { get; }

    /// <summary>The route every resolution returns.</summary>
    public MediatorRoute Route { get; set; } = HttpRoute;

    /// <summary>Every <see cref="ResolveRouteAsync"/> call: (mediator DID, override, requireWebSocket).</summary>
    public ConcurrentQueue<(string MediatorDid, Uri? Override, bool RequireWebSocket)> RouteCalls { get; } = new();

    /// <summary>The live mailbox bound (<see cref="LiveRegistration.PushCapacity"/> by default).</summary>
    public int PushCapacity { get; set; } = LiveRegistration.PushCapacity;

    /// <summary>The unpack step; the default reads the payload texts described on the class.</summary>
    public Func<string, CancellationToken, Task<DeliveredOutcome>> Unpacker { get; set; } = static (text, _) => Task.FromResult(DefaultOutcome(text));

    /// <summary>Runs inside <see cref="UnpackDeliveredBatchAsync"/> after the outcomes are known (to land a cancellation exactly there).</summary>
    public Action? AfterBatchUnpack { get; set; }

    /// <summary>A snapshot of every request sent so far, in order.</summary>
    public IReadOnlyList<SentRequest> Sent
    {
        get
        {
            lock (_gate)
                return _sent.ToArray();
        }
    }

    /// <summary>The requests of one type, in order.</summary>
    public IReadOnlyList<SentRequest> SentOf(string type) => Sent.Where(s => s.Is(type)).ToArray();

    /// <summary>The ids of every <c>messages-received</c> sent, flattened in order.</summary>
    public IReadOnlyList<string> AcknowledgedIds => SentOf(MessagePickup.MessagesReceivedType).SelectMany(s => s.Ids).ToArray();

    /// <summary>Every payload text handed to an unpack, in order.</summary>
    public IReadOnlyList<string> Unpacked
    {
        get
        {
            lock (_gate)
                return _unpacked.ToArray();
        }
    }

    /// <summary>The open live mailboxes.</summary>
    public IReadOnlyCollection<LiveRegistration> Registrations => _live.Values.ToArray();

    /// <summary>The mailbox of the default pair, when open.</summary>
    public LiveRegistration? Registration => _live.TryGetValue(LiveRegistration.KeyFor(M, L), out var r) ? r : null;

    /// <summary>Answer requests of <paramref name="type"/> with <paramref name="handler"/> (replacing the default).</summary>
    public FakePickupExchange On(string type, Func<SentRequest, Task<InboundMessageSnapshot>> handler)
    {
        _handlers[type] = handler;
        return this;
    }

    /// <summary>Answer requests of <paramref name="type"/> synchronously.</summary>
    public FakePickupExchange On(string type, Func<SentRequest, InboundMessageSnapshot> handler)
        => On(type, r => Task.FromResult(handler(r)));

    /// <summary>Answer requests of <paramref name="type"/> with the next of <paramref name="answers"/> (the last one again when they run out).</summary>
    public FakePickupExchange OnSequence(string type, params Func<SentRequest, InboundMessageSnapshot>[] answers)
    {
        var next = 0;
        return On(type, r =>
        {
            var i = Math.Min(Interlocked.Increment(ref next) - 1, answers.Length - 1);
            return Task.FromResult(answers[i](r));
        });
    }

    /// <summary>Wait (bounded, in real time) until at least <paramref name="count"/> requests were sent.</summary>
    public async Task WaitForSentAsync(int count, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_sent.Count >= count)
                    return;
                changed = _changed.Task;
            }
            await changed.WaitAsync(cts.Token);
        }
    }

    // ---- IMediatorExchange ----------------------------------------------------------------------

    public Task<MediatorRoute> ResolveRouteAsync(string mediatorDid, Uri? serviceEndpointOverride, bool requireWebSocket, CancellationToken ct)
    {
        RouteCalls.Enqueue((mediatorDid, serviceEndpointOverride, requireWebSocket));
        ct.ThrowIfCancellationRequested();
        // The channel's rule (design §4.3): refused before anything is sent.
        if (requireWebSocket && !Route.IsWebSocket)
            throw new InvalidOperationException("Live delivery needs a WebSocket route.");
        return Task.FromResult(Route);
    }

    public Task<InboundMessageSnapshot> ExchangeAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, CancellationToken ct)
    {
        var sent = new SentRequest(route, request, spec, mediatorDid, localDid, ct);
        TaskCompletionSource signal;
        lock (_gate)
        {
            _sent.Add(sent);
            signal = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        signal.TrySetResult();
        if (_handlers.TryGetValue(request.Type, out var handler))
            return handler(sent);
        return Task.FromResult(DefaultAnswer(sent));
    }

    public LiveRegistration RegisterLiveStream(string mediatorDid, string localDid)
    {
        var registration = new LiveRegistration(mediatorDid, localDid, r => _live.TryRemove(new KeyValuePair<string, LiveRegistration>(r.Key, r)), PushCapacity);
        if (!_live.TryAdd(registration.Key, registration))
            throw new InvalidOperationException("A live stream for this mediator and DID is already open.");
        return registration;
    }

    public async Task<DeliveredOutcome> UnpackDeliveredAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = Encoding.UTF8.GetString(payload.Span);
        lock (_gate)
            _unpacked.Add(text);
        return await Unpacker(text, ct);
    }

    public async Task<IReadOnlyList<DeliveredOutcome>> UnpackDeliveredBatchAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct)
    {
        var outcomes = new List<DeliveredOutcome>(payloads.Count);
        foreach (var payload in payloads)
            outcomes.Add(await UnpackDeliveredAsync(payload, ct));
        AfterBatchUnpack?.Invoke();
        return outcomes;
    }

    // ---- answers --------------------------------------------------------------------------------

    /// <summary>The default answer: a <c>status</c> with nothing waiting (live delivery as asked for a <c>live-delivery-change</c>).</summary>
    public static InboundMessageSnapshot DefaultAnswer(SentRequest request)
        => request.Is(MessagePickup.LiveDeliveryChangeType)
            ? Status(request, live: request.Live)
            : Status(request);

    /// <summary>A <c>status</c> answering <paramref name="request"/>.</summary>
    public static InboundMessageSnapshot Status(SentRequest request, int count = 0, bool? live = null, string? recipientDid = null)
        => Snapshot(MessagePickup.CreateStatus(M, L, new PickupStatus(count, recipientDid, LiveDelivery: live), thid: request.Request.Id));

    /// <summary>A <c>delivery</c> answering <paramref name="request"/> (or a live push, with <paramref name="request"/> <c>null</c>): one attachment per (id, payload text).</summary>
    public static InboundMessageSnapshot Delivery(SentRequest? request, params (string Id, string Payload)[] messages)
        => Snapshot(MessagePickup.CreateDelivery(M, L,
            messages.Select(m => new DeliveredMessage(m.Id, Encoding.UTF8.GetBytes(m.Payload))).ToArray(),
            thid: request?.Request.Id));

    /// <summary>A live push carrying (id, payload text) messages.</summary>
    public static InboundMessageSnapshot Push(params (string Id, string Payload)[] messages) => Delivery(null, messages);

    /// <summary>A <c>delivery</c> with hand-built attachments (ids the builder would refuse, bad data).</summary>
    public static InboundMessageSnapshot RawDelivery(SentRequest? request, IEnumerable<Attachment?> attachments, string? recipientDid = null)
    {
        var message = new Message
        {
            Id = Guid.NewGuid().ToString(),
            Type = MessagePickup.DeliveryType,
            From = M,
            To = new List<string> { L },
            Thid = request?.Request.Id,
            Body = recipientDid is null ? new JsonObject() : new JsonObject { [PickupBodies.RecipientDid] = recipientDid },
            Attachments = attachments.ToList()!,
        };
        return Snapshot(message);
    }

    /// <summary>An attachment carrying <paramref name="payload"/> as base64url.</summary>
    public static Attachment Entry(string? id, string payload)
        => new() { Id = id, Data = new AttachmentData { Base64 = Base64Url.Encode(Encoding.UTF8.GetBytes(payload)) } };

    /// <summary>An attachment whose data is not base64 at all.</summary>
    public static Attachment PoisonedAttachment(string id)
        => new() { Id = id, Data = new AttachmentData { Base64 = "%%% not base64 %%%" } };

    /// <summary>An answer of any message (a fallback snapshot: its fields are what the message says).</summary>
    public static InboundMessageSnapshot Snapshot(Message message)
        => InboundMessageSnapshot.CreateFallback(new UnpackResult(
            Message: message,
            Stack: Array.Empty<EnvelopeKind>(),
            Encrypted: true,
            Authenticated: true,
            NonRepudiation: false,
            AnonymousSender: false,
            ContentEncryption: null,
            KeyWrap: null,
            SignatureAlgorithm: null,
            SignerKid: null,
            SenderKid: M + "#key-1",
            RecipientKid: L + "#key-1",
            AllRecipientKids: new[] { L + "#key-1" },
            FromPrior: null));

    // ---- unpack outcomes ------------------------------------------------------------------------

    /// <summary>The outcome the default unpacker gives <paramref name="text"/>.</summary>
    public static DeliveredOutcome DefaultOutcome(string text)
    {
        if (text.StartsWith("ok:", StringComparison.Ordinal))
            return Success(Authcrypt(A, L, text[3..]), text);
        if (text.StartsWith("to:", StringComparison.Ordinal))
        {
            var rest = text[3..];
            var cut = rest.LastIndexOf(':');
            return Success(Authcrypt(A, rest[..cut], rest[(cut + 1)..]), text);
        }
        if (text.StartsWith("anon:", StringComparison.Ordinal))
            return Success(Result(Guid.NewGuid().ToString(), authenticated: false, senderKid: null, recipientKid: L + "#key-1"), text);
        return DeliveredOutcome.Failure(new MalformedMessageException("The delivered message is not a DIDComm message this agent can open."));
    }

    /// <summary>A success with its real duplicate-filter key.</summary>
    public static DeliveredOutcome Success(UnpackResult result, string packed)
        => DeliveredOutcome.Success(result, layersOpened: 0, DuplicateFilter.KeyOf(result, packed));

    /// <summary>An authcrypted message from <paramref name="sender"/> decrypted for <paramref name="recipient"/>.</summary>
    public static UnpackResult Authcrypt(string sender, string recipient, string id)
        => Result(id, authenticated: true, senderKid: sender + "#key-1", recipientKid: recipient + "#key-1");

    private static UnpackResult Result(string id, bool authenticated, string? senderKid, string? recipientKid)
        => new(
            Message: new Message { Id = id, Type = "https://didcomm.org/basicmessage/2.0/message", From = authenticated ? senderKid![..senderKid!.IndexOf('#')] : null },
            Stack: Array.Empty<EnvelopeKind>(),
            Encrypted: true,
            Authenticated: authenticated,
            NonRepudiation: false,
            AnonymousSender: !authenticated,
            ContentEncryption: null,
            KeyWrap: null,
            SignatureAlgorithm: null,
            SignerKid: null,
            SenderKid: senderKid,
            RecipientKid: recipientKid,
            AllRecipientKids: recipientKid is null ? Array.Empty<string>() : new[] { recipientKid },
            FromPrior: null);
}

/// <summary>A logger provider that keeps every line written through it (rendered message, level).</summary>
internal sealed class PickupTestLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Category, string Message)> Entries => _entries.ToArray();

    public ILogger<PickupClient> Logger => new Logger<PickupClient>(new Factory(this));

    public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Factory(PickupTestLogs owner) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => owner.CreateLogger(categoryName);

        public void Dispose()
        {
        }
    }

    private sealed class Capture(PickupTestLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner._entries.Enqueue((logLevel, category, formatter(state, exception)));
    }
}

/// <summary>Shorthands of the pickup client tests.</summary>
internal static class PickupTest
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>A client over <paramref name="exchange"/>, logging into <paramref name="logs"/> when given.</summary>
    public static PickupClient Client(FakePickupExchange exchange, PickupTestLogs? logs = null) => new(exchange, logs?.Logger);

    /// <summary>Await <paramref name="task"/>, failing the test instead of hanging when it does not finish in real time.</summary>
    public static async Task<T> Within<T>(ValueTask<T> task) => await task.AsTask().WaitAsync(Timeout);

    /// <summary>Await <paramref name="task"/>, failing the test instead of hanging when it does not finish in real time.</summary>
    public static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(Timeout);

    /// <summary>Await <paramref name="task"/>, failing the test instead of hanging when it does not finish in real time.</summary>
    public static async Task Within(ValueTask task) => await task.AsTask().WaitAsync(Timeout);

    /// <summary>Await <paramref name="task"/>, failing the test instead of hanging when it does not finish in real time.</summary>
    public static async Task Within(Task task) => await task.WaitAsync(Timeout);
}
