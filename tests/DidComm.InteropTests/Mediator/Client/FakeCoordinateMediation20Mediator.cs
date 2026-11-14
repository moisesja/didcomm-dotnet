using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Internal;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// A third-party mediator that speaks only Coordinate Mediation 2.0 (FR-MED-10): a TestServer agent
/// on <c>m2.test</c> built the way any DIDComm host is — <c>AddDidComm</c> with did:peer resolution,
/// <c>AddBuiltInProtocols()</c> (so Discover Features answers from the handler registry) and the
/// registry-aware <c>MapDidCommEndpoint("/didcomm")</c> — plus, optionally, a test
/// <see cref="FakeCoordinateMediation20Handler"/> for the 2.0 PIURI in the mediator role. With the
/// handler, a <c>coordinate-mediation/*</c> query discloses 2.0 only; without it, nothing. It joins
/// the <see cref="MediatorTestServer"/>'s network and clock, so a <see cref="MediatorClientHarness"/>
/// built over that server reaches it at the address its DID document names.
/// </summary>
internal sealed class FakeCoordinateMediation20Mediator : IAsyncDisposable
{
    public const string Host = "m2.test";
    public const string Path = "/didcomm";

    private FakeCoordinateMediation20Mediator(WebApplication app, PeerIdentity identity, FakeCoordinateMediation20Handler? handler)
    {
        App = app;
        Did = identity.Did;
        Kids = DidComm.InteropTests.AspNetCore.ReturnRouteTestHost.KidsOf(identity);
        Handler = handler;
    }

    public WebApplication App { get; }

    /// <summary>The fake mediator's DID (its service is <c>http://m2.test/didcomm</c>); also the routing DID it grants.</summary>
    public string Did { get; }

    public IReadOnlyList<string> Kids { get; }

    /// <summary>The 2.0 handler, or <c>null</c> for a host that discloses no Coordinate Mediation.</summary>
    public FakeCoordinateMediation20Handler? Handler { get; }

    /// <summary>Start the host on <paramref name="mediator"/>'s network and clock.</summary>
    /// <param name="mediator">The #95 test server whose network and clock the host joins (its log capture too).</param>
    /// <param name="speaks20">Register the 2.0 handler.</param>
    /// <param name="omitThid">The handler answers without <c>thid</c>, as the 2.0 page's examples do.</param>
    public static async Task<FakeCoordinateMediation20Mediator> StartAsync(MediatorTestServer mediator, bool speaks20 = true, bool omitThid = false)
    {
        var identity = await MediatorCast.MintAsync(new DidCommServiceSpec($"http://{Host}{Path}", Accept: new[] { "didcomm/v2" }));
        var secrets = new InMemorySecretsResolver();
        foreach (var jwk in identity.Privates)
            secrets.Add(jwk);
        var handler = speaks20 ? new FakeCoordinateMediation20Handler(identity.Did, omitThid) : null;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(o => o.BaseAddress = new Uri($"http://{Host}/"));
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(mediator.Logs);
        if (handler is not null)
            builder.Services.AddSingleton(handler);
        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.AddBuiltInProtocols();
            if (handler is not null)
                b.AddProtocol<FakeCoordinateMediation20Handler>();
            b.Configure(o => o.Clock = () => mediator.Clock.Now);
        });

        var app = builder.Build();
        app.UseRouting();
        app.MapDidCommEndpoint(Path);
        await app.StartAsync();
        mediator.Network.Add(app.GetTestServer());
        mediator.AddSensitive(identity.Did);
        foreach (var kid in DidComm.InteropTests.AspNetCore.ReturnRouteTestHost.KidsOf(identity))
            mediator.AddSensitive(kid);
        return new FakeCoordinateMediation20Mediator(app, identity, handler);
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

/// <summary>
/// The mediator role of Coordinate Mediation 2.0 for the tests, answering in the shapes of the
/// didcomm.org 2.0 page (the <c>coordinate-mediation-2.0</c> fixtures): <c>mediate-grant</c> with a
/// single-string <c>routing_did</c>, <c>keylist-update-response</c> with one <c>updated</c> entry
/// per update (<c>success</c>, or <c>no_change</c> / <c>client_error</c>), and <c>keylist</c> with
/// <c>keys</c> and a <c>pagination</c>. Answers go back on the request's return route: from the DID
/// that decrypted it to its authenticated sender, with <c>thid</c> = the request id unless
/// <see cref="OmitThid"/>. It records every 2.0 message it handled.
/// </summary>
internal sealed class FakeCoordinateMediation20Handler : IProtocolHandler, IProtocolHandlerRoles
{
    private readonly string _routingDid;
    private readonly ConcurrentQueue<Message> _handled = new();
    private readonly ConcurrentDictionary<string, List<string>> _keys = new(StringComparer.Ordinal);

    public FakeCoordinateMediation20Handler(string routingDid, bool omitThid)
    {
        _routingDid = routingDid;
        OmitThid = omitThid;
    }

    public string ProtocolUri => CoordinateMediation20.ProtocolUri;

    public IReadOnlyList<string> Roles { get; } = new[] { CoordinateMediation.MediatorRole };

    public bool OmitThid { get; }

    /// <summary>The 2.0 messages handled, in order.</summary>
    public IReadOnlyList<Message> Handled => _handled.ToArray();

    /// <summary>The DIDs registered under <paramref name="peerDid"/>'s grant.</summary>
    public IReadOnlyList<string> KeysOf(string peerDid)
    {
        if (!_keys.TryGetValue(peerDid, out var keys))
            return Array.Empty<string>();
        lock (keys)
            return keys.ToArray();
    }

    public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        _handled.Enqueue(message);
        if (!MediatorPeer.TryGet(context.Received, out var peer, out var local))
            return Task.FromResult<Message?>(null);

        var name = message.Type[(CoordinateMediation20.ProtocolUri.Length + 1)..];
        JsonObject? body;
        string type;
        switch (name)
        {
            case "mediate-request":
                _keys.TryAdd(peer, new List<string>());
                type = CoordinateMediation20.MediateGrantType;
                body = new JsonObject { ["routing_did"] = _routingDid };
                break;
            case "keylist-update":
            {
                var keys = _keys.GetOrAdd(peer, _ => new List<string>());
                var results = new List<RecipientUpdateResult>();
                lock (keys)
                {
                    foreach (var update in MediationBodies.ReadUpdates(MediationBodies.ToElement(message.Body), "keylist-update"))
                    {
                        var outcome = update.Action == RecipientUpdateAction.Add
                            ? (keys.Contains(update.RecipientDid) ? RecipientUpdateOutcome.NoChange : Added(keys, update.RecipientDid))
                            : (keys.Remove(update.RecipientDid) ? RecipientUpdateOutcome.Success : RecipientUpdateOutcome.ClientError);
                        results.Add(new RecipientUpdateResult(update.RecipientDid, update.Action, outcome));
                    }
                }
                type = CoordinateMediation20.KeylistUpdateResponseType;
                body = MediationBodies.WriteUpdated(results);
                break;
            }
            case "keylist-query":
            {
                var paginate = MediationBodies.ReadPaginate(MediationBodies.ToElement(message.Body), "keylist-query");
                var all = KeysOf(peer);
                var offset = Math.Min(paginate?.Offset ?? 0, all.Count);
                var page = all.Skip(offset).Take(Math.Max(0, paginate?.Limit ?? 30)).ToArray();
                type = CoordinateMediation20.KeylistType;
                body = MediationBodies.WriteRecipients(CoordinateMediation20.KeysList, page, new RecipientPagination(page.Length, offset, all.Count - offset - page.Length));
                break;
            }
            default:
                return Task.FromResult<Message?>(null);
        }

        var reply = new MessageBuilder().WithType(type).WithFrom(local).WithTo(peer).WithBody(body);
        if (!OmitThid)
            reply.WithThid(message.Id);
        return Task.FromResult<Message?>(reply.Build());
    }

    private static RecipientUpdateOutcome Added(List<string> keys, string did)
    {
        keys.Add(did);
        return RecipientUpdateOutcome.Success;
    }
}
