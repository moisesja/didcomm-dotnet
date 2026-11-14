using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Json;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;

namespace DidComm.Mediator.Tests.Client;

/// <summary>One request a <see cref="FakeMediatorExchange"/> was asked to exchange.</summary>
internal sealed record FakeExchangeRequest(MediatorRoute Route, Message Message, MediatorExchangeSpec Spec, string MediatorDid, string LocalDid);

/// <summary>
/// An <see cref="IMediatorExchange"/> for engineer B's unit tests, standing in for the channel: it
/// records every route resolution and every exchange, and answers each request with what
/// <see cref="Respond"/> returns (by default a well-behaved <see cref="ScriptedMediator"/>). An
/// answer becomes a snapshot as the dispatcher would hand it over, so the facade's own
/// deserialization runs. A <c>null</c> answer is the HTTP "no answer" (<see cref="TimeoutException"/>),
/// and a problem report is turned into <see cref="MediationProtocolException"/>, as the channel does.
/// </summary>
internal sealed class FakeMediatorExchange : IMediatorExchange
{
    internal const string MediatorDid = "did:example:mediator";
    internal const string LocalDid = "did:example:alice";

    private readonly ConcurrentQueue<FakeExchangeRequest> _sent = new();
    private readonly ConcurrentQueue<(string MediatorDid, Uri? Override, bool RequireWebSocket)> _routes = new();

    public FakeMediatorExchange(MediatorClientOptions? options = null, TimeProvider? time = null)
    {
        Options = (options ?? new MediatorClientOptions()).ValidatedCopy();
        Time = time ?? TimeProvider.System;
        Duplicates = new DuplicateFilter(0);
        Versions = new NegotiatedVersionCache(Time);
        Mediator = new ScriptedMediator();
        Respond = Mediator.Answer;
    }

    public MediatorClientOptions Options { get; }

    public TimeProvider Time { get; }

    public DuplicateFilter Duplicates { get; }

    public NegotiatedVersionCache Versions { get; set; }

    /// <summary>The default answerer.</summary>
    public ScriptedMediator Mediator { get; }

    /// <summary>Answers a request; <c>null</c> = no answer. May throw (a timeout, a transport failure).</summary>
    public Func<FakeExchangeRequest, Message?> Respond { get; set; }

    /// <summary>The route every resolution returns.</summary>
    public MediatorRoute Route { get; set; } = new(new Uri("http://mediator.test/"), IsHttp: true, IsWebSocket: false);

    /// <summary>When set, route resolution throws it.</summary>
    public Exception? RouteFailure { get; set; }

    public IReadOnlyList<FakeExchangeRequest> Sent => _sent.ToArray();

    public int SendCount => _sent.Count;

    public IReadOnlyList<(string MediatorDid, Uri? Override, bool RequireWebSocket)> RouteCalls => _routes.ToArray();

    /// <summary>The requests other than Discover Features queries.</summary>
    public IReadOnlyList<FakeExchangeRequest> Operations => Sent.Where(s => !IsQuery(s.Message)).ToArray();

    /// <summary>The Discover Features queries.</summary>
    public IReadOnlyList<FakeExchangeRequest> Queries => Sent.Where(s => IsQuery(s.Message)).ToArray();

    public Task<MediatorRoute> ResolveRouteAsync(string mediatorDid, Uri? serviceEndpointOverride, bool requireWebSocket, CancellationToken ct)
    {
        _routes.Enqueue((mediatorDid, serviceEndpointOverride, requireWebSocket));
        ct.ThrowIfCancellationRequested();
        if (RouteFailure is { } failure)
            return Task.FromException<MediatorRoute>(failure);
        return Task.FromResult(Route);
    }

    public Task<InboundMessageSnapshot> ExchangeAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, CancellationToken ct)
    {
        var sent = new FakeExchangeRequest(route, request, spec, mediatorDid, localDid);
        _sent.Enqueue(sent);
        ct.ThrowIfCancellationRequested();
        Message? answer;
        try
        {
            answer = Respond(sent);
        }
        catch (Exception ex)
        {
            return Task.FromException<InboundMessageSnapshot>(ex);
        }
        if (answer is null)
            return Task.FromException<InboundMessageSnapshot>(new TimeoutException($"Mediator request '{request.Id}' ({spec.Name}) got no answer in the HTTP response."));
        if (answer.Type.StartsWith("https://didcomm.org/report-problem/", StringComparison.Ordinal))
            return Task.FromException<InboundMessageSnapshot>(new MediationProtocolException($"Mediator request '{request.Id}' was answered with a problem report.", request.Id));
        return Task.FromResult(SnapshotOf(answer, mediatorDid, localDid));
    }

    public LiveRegistration RegisterLiveStream(string mediatorDid, string localDid) => throw new NotSupportedException("Not used by MediationClient.");

    public Task<DeliveredOutcome> UnpackDeliveredAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException("Not used by MediationClient.");

    public Task<IReadOnlyList<DeliveredOutcome>> UnpackDeliveredBatchAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct) => throw new NotSupportedException("Not used by MediationClient.");

    /// <summary>A client over this exchange, as the DI constructor builds it.</summary>
    public MediationClient Client(Microsoft.Extensions.Logging.ILogger<MediationClient>? logger = null) => new(this, logger);

    internal static bool IsQuery(Message message) => message.Type == DiscoverFeatures.QueriesType;

    // As the dispatcher would hand it over: the plaintext round-tripped through JSON.
    internal static InboundMessageSnapshot SnapshotOf(Message answer, string mediatorDid, string localDid)
    {
        var copy = JsonSerializer.Deserialize<Message>(JsonSerializer.Serialize(answer, DidCommJson.Default), DidCommJson.Default)!;
        return InboundMessageSnapshot.CreateFallback(new UnpackResult(
            copy,
            Array.Empty<EnvelopeKind>(),
            Encrypted: true,
            Authenticated: true,
            NonRepudiation: false,
            AnonymousSender: false,
            ContentEncryption: null,
            KeyWrap: null,
            SignatureAlgorithm: null,
            SignerKid: null,
            SenderKid: mediatorDid + "#key-x25519-1",
            RecipientKid: localDid + "#key-x25519-1",
            AllRecipientKids: Array.Empty<string>(),
            FromPrior: null));
    }
}

/// <summary>
/// A well-behaved mediator for the fake exchange: Discover Features discloses <see cref="Disclosures"/>
/// (Coordinate Mediation 3.0 in the mediator role by default); a <c>mediate-request</c> is granted
/// (<see cref="RoutingDids"/>) or denied (<see cref="Deny"/>); an update answers <c>success</c> per entry
/// in order and changes <see cref="Registered"/>; a query answers the page asked for, with a
/// pagination, out of <see cref="Registered"/>. Both versions, in their own shapes.
/// </summary>
internal sealed class ScriptedMediator
{
    public List<FeatureDisclosure> Disclosures { get; } = new()
    {
        new FeatureDisclosure { FeatureType = "protocol", Id = CoordinateMediation.ProtocolUri, Roles = new[] { "mediator" } },
    };

    public IReadOnlyList<string> RoutingDids { get; set; } = new[] { "did:example:routing" };

    public bool Deny { get; set; }

    public List<string> Registered { get; } = new();

    /// <summary>The page size used when a query names none.</summary>
    public int DefaultPage { get; set; } = 100;

    /// <summary>Answers without a <c>thid</c> (the 2.0 page's examples carry none).</summary>
    public bool OmitThid { get; set; }

    public Message? Answer(FakeExchangeRequest sent)
    {
        var request = sent.Message;
        var from = sent.MediatorDid;
        var to = sent.LocalDid;
        var thid = request.Id;
        var type = request.Type;
        Message answer = type switch
        {
            DiscoverFeatures.QueriesType => DiscoverFeatures.CreateDisclose(from, to, thid, Disclosures.ToArray()),
            CoordinateMediation.MediateRequestType => Deny
                ? CoordinateMediation.CreateDeny(from, to, thid)
                : CoordinateMediation.CreateGrant(from, to, RoutingDids, thid),
            CoordinateMediation20.MediateRequestType => Deny
                ? Raw(CoordinateMediation20.MediateDenyType, from, to, thid, new JsonObject())
                : Raw(CoordinateMediation20.MediateGrantType, from, to, thid, new JsonObject { ["routing_did"] = RoutingDids[0] }),
            CoordinateMediation.RecipientUpdateType => CoordinateMediation.CreateRecipientUpdateResponse(from, to, Apply(request), thid),
            CoordinateMediation20.KeylistUpdateType => Raw(CoordinateMediation20.KeylistUpdateResponseType, from, to, thid, MediationBodies.WriteUpdated(Apply(request))),
            CoordinateMediation.RecipientQueryType => Page(request, "dids", CoordinateMediation.RecipientType, from, to, thid),
            CoordinateMediation20.KeylistQueryType => Page(request, CoordinateMediation20.KeysList, CoordinateMediation20.KeylistType, from, to, thid),
            _ => throw new InvalidOperationException("The scripted mediator does not know this request type."),
        };
        if (OmitThid)
            answer.Thid = null;
        return answer;
    }

    /// <summary>A message of any type and body, from the mediator to us, built as an answer.</summary>
    public static Message Raw(string type, string from, string to, string? thid, JsonObject? body)
    {
        var builder = new MessageBuilder().WithType(type).WithFrom(from).WithTo(to);
        if (thid is not null)
            builder.WithThid(thid);
        if (body is not null)
            builder.WithBody(body);
        return builder.Build();
    }

    private IReadOnlyList<RecipientUpdateResult> Apply(Message request)
    {
        var updates = MediationBodies.ReadUpdates(MediationBodies.ToElement(request.Body), "update");
        var results = new List<RecipientUpdateResult>();
        foreach (var update in updates)
        {
            if (update.Action == RecipientUpdateAction.Add)
            {
                var already = Registered.Contains(update.RecipientDid);
                if (!already)
                    Registered.Add(update.RecipientDid);
                results.Add(new RecipientUpdateResult(update.RecipientDid, update.Action, already ? RecipientUpdateOutcome.NoChange : RecipientUpdateOutcome.Success));
            }
            else
            {
                var removed = Registered.Remove(update.RecipientDid);
                results.Add(new RecipientUpdateResult(update.RecipientDid, update.Action, removed ? RecipientUpdateOutcome.Success : RecipientUpdateOutcome.ClientError));
            }
        }
        return results;
    }

    private Message Page(Message request, string listName, string type, string from, string to, string thid)
    {
        var paginate = MediationBodies.ReadPaginate(MediationBodies.ToElement(request.Body), "query");
        var offset = paginate?.Offset ?? 0;
        var limit = paginate?.Limit ?? DefaultPage;
        var page = Registered.Skip(offset).Take(Math.Max(0, limit)).ToArray();
        var pagination = new RecipientPagination(page.Length, offset, Math.Max(0, Registered.Count - offset - page.Length));
        return Raw(type, from, to, thid, MediationBodies.WriteRecipients(listName, page, pagination));
    }
}
