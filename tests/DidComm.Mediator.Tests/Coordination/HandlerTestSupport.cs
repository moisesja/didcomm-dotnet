using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Samples.Shared;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>The envelope an inbound request arrives in (the FR-MED-05 peer-identity matrix).</summary>
public enum HandlerEnvelope
{
    /// <summary>No protection at all.</summary>
    Plaintext,

    /// <summary>Encrypted to the mediator, no sender key.</summary>
    Anoncrypt,

    /// <summary>A JWS by the sender, not encrypted.</summary>
    SignedOnly,

    /// <summary>anoncrypt(sign(plaintext)): encrypted and signature-authenticated, but no authcrypt layer.</summary>
    AnoncryptOfSigned,

    /// <summary>authcrypt(plaintext).</summary>
    Authcrypt,

    /// <summary>authcrypt(sign(plaintext)).</summary>
    AuthcryptOfSigned,

    /// <summary>anoncrypt(authcrypt(plaintext)): the sender key is hidden from observers, not from the recipient.</summary>
    AnoncryptOfAuthcrypt,
}

/// <summary>One captured log line. <see cref="Rendered"/> is what a console provider prints.</summary>
internal sealed record HandlerLogEntry(string Category, LogLevel Level, string Formatted, IReadOnlyDictionary<string, object?> State, Exception? Exception)
{
    public string Rendered => Exception is null ? Formatted : Formatted + Environment.NewLine + Exception;

    /// <summary>The rendered line plus every structured value, so a search covers what any provider could write.</summary>
    public string Everything => Rendered + Environment.NewLine + string.Join(Environment.NewLine, State.Select(p => p.Key + "=" + (p.Value?.ToString() ?? string.Empty)));
}

/// <summary>Collects every log line of every category written through <see cref="HandlerCapturingLogger{T}"/>.</summary>
internal sealed class HandlerLogSink
{
    public static readonly string HandlerCategory = typeof(CoordinateMediationHandler).FullName!;

    public ConcurrentQueue<HandlerLogEntry> Entries { get; } = new();

    /// <summary>The lines the Coordinate Mediation handler itself wrote, in order.</summary>
    public IReadOnlyList<HandlerLogEntry> Handler => Entries.Where(e => e.Category == HandlerCategory).ToArray();
}

/// <summary>An <c>ILogger&lt;T&gt;</c> that records everything, at every level, under the category of <typeparamref name="T"/>.</summary>
internal sealed class HandlerCapturingLogger<T>(HandlerLogSink sink) : ILogger<T>
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
        sink.Entries.Enqueue(new HandlerLogEntry(typeof(T).FullName!, logLevel, formatter(state, exception), values, exception));
    }
}

/// <summary>A logging provider that is down: every call throws.</summary>
internal sealed class HandlerThrowingLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => throw new InvalidOperationException("logging provider down (scope)");

    public bool IsEnabled(LogLevel logLevel)
        => throw new InvalidOperationException("logging provider down (enabled)");

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => throw new InvalidOperationException("logging provider down (log)");
}

/// <summary>A fault a fake throws. Its message carries text that must never reach a log or a reply.</summary>
internal sealed class HandlerBoomException(string message) : Exception(message)
{
    public const string Marker = "boom-secret-text";
}

/// <summary>
/// An <see cref="IMediationStore"/> over the real <see cref="InMemoryMediationStore"/> that counts
/// every call and lets a test replace any method's behaviour.
/// </summary>
internal sealed class HandlerFakeStore : IMediationStore
{
    public HandlerFakeStore(CoordinateMediationOptions? options = null)
        => Inner = new InMemoryMediationStore(options ?? new CoordinateMediationOptions());

    public InMemoryMediationStore Inner { get; }

    public ConcurrentQueue<string> Calls { get; } = new();
    public ConcurrentQueue<(string PeerDid, IReadOnlyList<RecipientUpdate> Updates)> UpdateCalls { get; } = new();
    public ConcurrentQueue<(string PeerDid, int Offset, int Limit)> ListCalls { get; } = new();
    public ConcurrentQueue<MediationGrant> GrantCalls { get; } = new();

    public Func<string, CancellationToken, ValueTask<MediationGrant?>>? OnGetGrant { get; set; }
    public Func<MediationGrant, CancellationToken, ValueTask<MediationGrant?>>? OnGrant { get; set; }
    public Func<string, IReadOnlyList<RecipientUpdate>, CancellationToken, ValueTask<IReadOnlyList<RecipientStoreOutcome>>>? OnUpdateRecipients { get; set; }
    public Func<string, int, int, CancellationToken, ValueTask<RecipientPage>>? OnListRecipients { get; set; }

    /// <summary>Calls that can change the store.</summary>
    public int MutatingCalls => Calls.Count(c => c is nameof(GrantAsync) or nameof(RevokeAsync) or nameof(UpdateRecipientsAsync));

    public void ResetCalls()
    {
        Calls.Clear();
        UpdateCalls.Clear();
        ListCalls.Clear();
        GrantCalls.Clear();
    }

    public ValueTask<MediationGrant?> GetGrantAsync(string peerDid, CancellationToken ct)
    {
        Calls.Enqueue(nameof(GetGrantAsync));
        return OnGetGrant is { } hook ? hook(peerDid, ct) : Inner.GetGrantAsync(peerDid, ct);
    }

    public ValueTask<MediationGrant?> GrantAsync(MediationGrant grant, CancellationToken ct)
    {
        Calls.Enqueue(nameof(GrantAsync));
        GrantCalls.Enqueue(grant);
        return OnGrant is { } hook ? hook(grant, ct) : Inner.GrantAsync(grant, ct);
    }

    public ValueTask<bool> RevokeAsync(string peerDid, CancellationToken ct)
    {
        Calls.Enqueue(nameof(RevokeAsync));
        return Inner.RevokeAsync(peerDid, ct);
    }

    public ValueTask<IReadOnlyList<RecipientStoreOutcome>> UpdateRecipientsAsync(string peerDid, IReadOnlyList<RecipientUpdate> updates, CancellationToken ct)
    {
        Calls.Enqueue(nameof(UpdateRecipientsAsync));
        UpdateCalls.Enqueue((peerDid, updates.ToArray()));
        return OnUpdateRecipients is { } hook ? hook(peerDid, updates, ct) : Inner.UpdateRecipientsAsync(peerDid, updates, ct);
    }

    public ValueTask<RecipientPage> ListRecipientsAsync(string peerDid, int offset, int limit, CancellationToken ct)
    {
        Calls.Enqueue(nameof(ListRecipientsAsync));
        ListCalls.Enqueue((peerDid, offset, limit));
        return OnListRecipients is { } hook ? hook(peerDid, offset, limit, ct) : Inner.ListRecipientsAsync(peerDid, offset, limit, ct);
    }

    public ValueTask<MediationGrant?> FindGrantByRecipientDidAsync(string recipientDid, CancellationToken ct)
    {
        Calls.Enqueue(nameof(FindGrantByRecipientDidAsync));
        return Inner.FindGrantByRecipientDidAsync(recipientDid, ct);
    }
}

/// <summary>An <see cref="IMediationPolicy"/> that records what it was asked and answers as the test says.</summary>
internal sealed class HandlerFakePolicy : IMediationPolicy
{
    public ConcurrentQueue<string> MediateRequests { get; } = new();
    public ConcurrentQueue<(string PeerDid, string RecipientDid)> Registrations { get; } = new();
    public ConcurrentQueue<ProtocolContext> Contexts { get; } = new();

    public Func<string, ProtocolContext, CancellationToken, ValueTask<MediationDecision>> OnRequest { get; set; }
        = (_, _, _) => new ValueTask<MediationDecision>(MediationDecision.Grant);

    public Func<string, string, ProtocolContext, CancellationToken, ValueTask<bool>> OnRegister { get; set; }
        = (_, _, _, _) => new ValueTask<bool>(true);

    public int TotalCalls => MediateRequests.Count + Registrations.Count;

    public ValueTask<MediationDecision> OnMediateRequestAsync(string peerDid, ProtocolContext context, CancellationToken ct)
    {
        MediateRequests.Enqueue(peerDid);
        Contexts.Enqueue(context);
        return OnRequest(peerDid, context, ct);
    }

    public ValueTask<bool> MayRegisterAsync(string peerDid, string recipientDid, ProtocolContext context, CancellationToken ct)
    {
        Registrations.Enqueue((peerDid, recipientDid));
        Contexts.Enqueue(context);
        return OnRegister(peerDid, recipientDid, context, ct);
    }
}

/// <summary>An <see cref="IMediatorIdentity"/> whose answer a test can change, or make throw.</summary>
internal sealed class HandlerFakeIdentity : IMediatorIdentity
{
    public Func<IReadOnlyList<string>> Get { get; set; } = () => Array.Empty<string>();

    public int Reads { get; private set; }

    public IReadOnlyList<string> RoutingDids
    {
        get
        {
            Reads++;
            return Get();
        }
    }
}

/// <summary>
/// A routing-DID list whose enumeration fails: at once, or after yielding its first entries (a
/// cursor that died, a collection changed underneath the reader).
/// </summary>
internal sealed class HandlerFaultyDids(int yieldFirst, params string[] dids) : IReadOnlyList<string>
{
    public int Count => dids.Length + 1;

    public string this[int index] => index < yieldFirst ? dids[index] : throw new HandlerBoomException(HandlerBoomException.Marker);

    public IEnumerator<string> GetEnumerator()
    {
        if (yieldFirst == 0)
            throw new HandlerBoomException(HandlerBoomException.Marker);
        return Enumerate();
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private IEnumerator<string> Enumerate()
    {
        for (var i = 0; i < yieldFirst; i++)
            yield return dids[i];
        throw new HandlerBoomException(HandlerBoomException.Marker);
    }
}

/// <summary>A return-route session that records what a handler pushed onto it (nothing is expected).</summary>
internal sealed class HandlerFakeSession(string peerDid, string localDid) : IReturnRouteSession
{
    public ConcurrentQueue<Message> Sent { get; } = new();
    public string PeerDid { get; } = peerDid;
    public string LocalDid { get; } = localDid;
    public ReturnRouteMode Mode => ReturnRouteMode.All;
    public string? Thread => null;
    public ReturnRouteTransport Transport => ReturnRouteTransport.Http;
    public bool IsLive => true;
    public CancellationToken Closed => CancellationToken.None;

    public ValueTask<bool> TrySendAsync(Message message, CancellationToken ct)
    {
        Sent.Enqueue(message);
        return new ValueTask<bool>(true);
    }

    /// <summary>Records the message like the unbounded send; the fake packs nothing, so no bound is ever exceeded.</summary>
    public ValueTask<ReturnRouteSendResult> TrySendAsync(Message message, int maxPackedBytes, CancellationToken ct)
    {
        Sent.Enqueue(message);
        return new ValueTask<ReturnRouteSendResult>(ReturnRouteSendResult.Sent);
    }
}

/// <summary>
/// A <see cref="MediatorTestHost"/> with every log line captured and, where a test asks, fakes
/// registered before <c>AddCoordinateMediation()</c>.
/// </summary>
internal sealed class HandlerFixture : IAsyncDisposable
{
    private HandlerFixture(MediatorTestHost host, HandlerLogSink logs, HandlerFakeStore? store, HandlerFakePolicy? policy, HandlerFakeIdentity? identity)
    {
        Host = host;
        Logs = logs;
        FakeStore = store;
        Policy = policy;
        Identity = identity;
    }

    public MediatorTestHost Host { get; }
    public HandlerLogSink Logs { get; }
    public HandlerFakeStore? FakeStore { get; }
    public HandlerFakePolicy? Policy { get; }
    public HandlerFakeIdentity? Identity { get; }

    /// <summary>The real in-memory store: the container's, or the one inside the fake.</summary>
    public InMemoryMediationStore Memory => FakeStore?.Inner ?? (InMemoryMediationStore)Host.Store;

    public static HandlerFixture Create(
        Action<CoordinateMediationOptions>? configure = null,
        bool fakeStore = false,
        bool fakePolicy = false,
        bool fakeIdentity = false,
        bool throwingLogger = false,
        Action<DidCommOptions>? didComm = null)
    {
        var logs = new HandlerLogSink();
        HandlerFakeStore? store = null;
        if (fakeStore)
        {
            var bounds = new CoordinateMediationOptions();
            configure?.Invoke(bounds);
            store = new HandlerFakeStore(bounds);
        }
        var policy = fakePolicy ? new HandlerFakePolicy() : null;
        var identity = fakeIdentity ? new HandlerFakeIdentity() : null;

        var host = MediatorTestHost.Create(
            configure,
            before: services =>
            {
                services.AddSingleton(logs);
                services.AddSingleton(typeof(ILogger<>), throwingLogger ? typeof(HandlerThrowingLogger<>) : typeof(HandlerCapturingLogger<>));
                if (store is not null)
                    services.AddSingleton<IMediationStore>(store);
                if (policy is not null)
                    services.AddSingleton<IMediationPolicy>(policy);
                if (identity is not null)
                    services.AddSingleton<IMediatorIdentity>(identity);
            },
            after: builder =>
            {
                if (didComm is not null)
                    builder.Configure(didComm);
            });
        return new HandlerFixture(host, logs, store, policy, identity);
    }

    public Task<PeerIdentity> NewPeerAsync() => Host.NewPeerAsync();

    /// <summary>Pack <paramref name="message"/> in <paramref name="shape"/> and unpack it as the mediator.</summary>
    public async Task<UnpackResult> ReceiveAsync(Message message, string mediatorDid, HandlerEnvelope shape = HandlerEnvelope.Authcrypt, string? sender = null)
    {
        var client = Host.Client;
        var from = sender ?? message.From ?? throw new InvalidOperationException("The test message needs a sender.");
        var to = new[] { mediatorDid };
        var packed = shape switch
        {
            HandlerEnvelope.Plaintext => await client.PackPlaintextAsync(message),
            HandlerEnvelope.SignedOnly => await client.PackSignedAsync(message, from),
            HandlerEnvelope.Anoncrypt => (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: to))).Message,
            HandlerEnvelope.AnoncryptOfSigned => (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: to, From: null, SignFrom: from))).Message,
            HandlerEnvelope.Authcrypt => (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: to, From: from))).Message,
            HandlerEnvelope.AuthcryptOfSigned => (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: to, From: from, SignFrom: from))).Message,
            HandlerEnvelope.AnoncryptOfAuthcrypt => (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: to, From: from, ProtectSender: true))).Message,
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        return await client.UnpackAsync(packed);
    }

    /// <summary>Pack, unpack and dispatch through the real dispatcher with the overload that passes no session.</summary>
    public async Task<DispatchOutcome> SendAsync(Message message, string mediatorDid, HandlerEnvelope shape = HandlerEnvelope.Authcrypt, string? sender = null, CancellationToken ct = default)
    {
        var received = await ReceiveAsync(message, mediatorDid, shape, sender);
        return await Host.Dispatcher.DispatchAsync(received, Host.Client, Host.Options, ct);
    }

    /// <summary>Enrol <paramref name="peerDid"/> and return the grant's routing DIDs.</summary>
    public async Task<IReadOnlyList<string>> EnrolAsync(string peerDid, string mediatorDid)
    {
        var outcome = await SendAsync(CoordinateMediation.CreateMediateRequest(peerDid, mediatorDid), mediatorDid);
        if (outcome.Reply is null || !CoordinateMediation.TryParseGrant(outcome.Reply, out var routingDids))
            throw new InvalidOperationException($"Enrolment did not produce a grant ({outcome.Result}).");
        return routingDids;
    }

    /// <summary>Send a <c>recipient-update</c> as <paramref name="peerDid"/> and return the parsed results.</summary>
    public async Task<IReadOnlyList<RecipientUpdateResult>> UpdateAsync(string peerDid, string mediatorDid, params RecipientUpdate[] updates)
    {
        var outcome = await SendAsync(CoordinateMediation.CreateRecipientUpdate(peerDid, mediatorDid, updates), mediatorDid);
        if (outcome.Reply is null || !CoordinateMediation.TryParseRecipientUpdateResponse(outcome.Reply, out var updated))
            throw new InvalidOperationException($"The update did not produce a recipient-update-response ({outcome.Result}).");
        return updated;
    }

    /// <summary>Send a <c>recipient-query</c> as <paramref name="peerDid"/> and return the parsed page.</summary>
    public async Task<(IReadOnlyList<string> Dids, RecipientPagination? Pagination)> QueryAsync(string peerDid, string mediatorDid, RecipientQueryPaging? paginate = null)
    {
        var outcome = await SendAsync(CoordinateMediation.CreateRecipientQuery(peerDid, mediatorDid, paginate), mediatorDid);
        if (outcome.Reply is null || !CoordinateMediation.TryParseRecipient(outcome.Reply, out var dids, out var pagination))
            throw new InvalidOperationException($"The query did not produce a recipient message ({outcome.Result}).");
        return (dids, pagination);
    }

    /// <summary>Every DID the peer has registered, straight from the store.</summary>
    public async Task<IReadOnlyList<string>> RegisteredAsync(string peerDid)
        => (await Memory.ListRecipientsAsync(peerDid, 0, int.MaxValue, CancellationToken.None)).Dids;

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}

/// <summary>Message builders for shapes the typed <c>Create…</c> helpers refuse to build.</summary>
internal static class HandlerMessages
{
    public const string Piuri = CoordinateMediation.ProtocolUri;

    /// <summary>A message of any type with any body (or none), as a hostile or sloppy peer would send it.</summary>
    public static Message Raw(string type, string from, string mediator, JsonObject? body, string? id = null, string? thid = null)
    {
        var builder = new MessageBuilder().WithType(type).WithFrom(from).WithTo(mediator);
        if (id is not null)
            builder.WithId(id);
        if (thid is not null)
            builder.WithThid(thid);
        if (body is not null)
            builder.WithBody(body);
        return builder.Build();
    }

    public static JsonObject UpdatesBody(params (string RecipientDid, string Action)[] entries)
    {
        var array = new JsonArray();
        foreach (var (did, action) in entries)
            array.Add(new JsonObject { ["recipient_did"] = did, ["action"] = action });
        return new JsonObject { ["updates"] = array };
    }

    public static RecipientUpdate Add(string did) => new(did, RecipientUpdateAction.Add);

    public static RecipientUpdate Remove(string did) => new(did, RecipientUpdateAction.Remove);

    public static RecipientUpdateResult Added(string did, RecipientUpdateOutcome result) => new(did, RecipientUpdateAction.Add, result);

    public static RecipientUpdateResult Removed(string did, RecipientUpdateOutcome result) => new(did, RecipientUpdateAction.Remove, result);
}
