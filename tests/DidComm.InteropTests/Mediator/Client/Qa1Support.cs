using System.Collections.Concurrent;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.InteropTests.Transports;
using DidComm.Mediator.Coordination;
using DidComm.Protocols;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>One message a host's dispatcher saw: its type, sender, id, <c>thid</c>, <c>created_time</c> and <c>return_route</c> mode.</summary>
internal sealed record Qa1Seen(string Type, string? From, string Id, string? Thid, long? CreatedTime, ReturnRouteMode? ReturnRoute);

/// <summary>
/// QA-1 (#96): records every verified inbound a container's dispatcher sees, inline (an internal
/// <see cref="IInboundCorrelator"/> that claims nothing), with the <c>return_route</c> header the
/// existing recorder does not keep, so a test can check FR-MED-03 / FR-RR-09 on what actually
/// reached the mediator (or, registered in the recipient's container, what reached the client).
/// </summary>
internal sealed class Qa1Recorder : IInboundCorrelator
{
    private readonly ConcurrentQueue<Qa1Seen> _seen = new();

    public void Register(IServiceCollection services)
        => services.TryAddEnumerable(ServiceDescriptor.Singleton<IInboundCorrelator>(this));

    public IReadOnlyList<Qa1Seen> All => _seen.ToArray();

    public IReadOnlyList<Qa1Seen> From(string did) => _seen.Where(r => r.From == did).ToArray();

    public int Count(string did, string type) => _seen.Count(r => r.From == did && r.Type == type);

    bool IInboundCorrelator.OnInbound(InboundMessageSnapshot received)
    {
        var message = received.DeserializeMessage();
        ReturnRouteMode? mode = null;
        try
        {
            mode = ReturnRouteHeader.Read(message).Mode;
        }
        catch
        {
            // An unreadable header is recorded as absent.
        }
        _seen.Enqueue(new Qa1Seen(received.Type, received.From, received.Id, received.Thid, message.CreatedTime, mode));
        return false;
    }
}

/// <summary>
/// QA-1 (#96): a DIDComm host on the test network with nothing but what the test registers — the
/// registry-aware <c>MapDidCommEndpoint("/didcomm")</c>, did:peer resolution and its own keys. With no
/// protocols it answers nothing (a Discover Features query gets a 202), so a client's negotiation has
/// no answer; <see cref="Recorder"/> sees what arrived.
/// </summary>
internal sealed class Qa1BareHost : IAsyncDisposable
{
    public const string Path = "/didcomm";

    private Qa1BareHost(WebApplication app, PeerIdentity identity, Qa1Recorder recorder)
    {
        App = app;
        Did = identity.Did;
        Recorder = recorder;
    }

    public WebApplication App { get; }

    public string Did { get; }

    public Qa1Recorder Recorder { get; }

    public static async Task<Qa1BareHost> StartAsync(MediatorTestServer network, string host, Action<DidCommBuilder>? didComm = null)
    {
        var identity = await MediatorCast.MintAsync(new DidCommServiceSpec($"http://{host}{Path}", Accept: new[] { "didcomm/v2" }));
        var secrets = new InMemorySecretsResolver();
        foreach (var jwk in identity.Privates)
            secrets.Add(jwk);
        var recorder = new Qa1Recorder();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(o => o.BaseAddress = new Uri($"http://{host}/"));
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(network.Logs);
        recorder.Register(builder.Services);
        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            didComm?.Invoke(b);
            b.Configure(o => o.Clock = () => network.Clock.Now);
        });

        var app = builder.Build();
        app.UseRouting();
        app.MapDidCommEndpoint(Path);
        await app.StartAsync();
        network.Network.Add(app.GetTestServer());
        network.AddSensitive(identity.Did);
        foreach (var kid in DidComm.InteropTests.AspNetCore.ReturnRouteTestHost.KidsOf(identity))
            network.AddSensitive(kid);
        return new Qa1BareHost(app, identity, recorder);
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

/// <summary>
/// QA-1 (#96): a mediation policy that grants, but holds every <c>mediate-request</c> until the test
/// releases it — a rendezvous for "a request is pending upstream" (never a sleep, L-044).
/// </summary>
internal sealed class Qa1GatedPolicy : IMediationPolicy
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public void Release() => _release.TrySetResult();

    public async ValueTask<MediationDecision> OnMediateRequestAsync(string peerDid, ProtocolContext context, CancellationToken ct)
    {
        _entered.TrySetResult();
        await _release.Task.WaitAsync(ct);
        return MediationDecision.Grant;
    }

    public ValueTask<bool> MayRegisterAsync(string peerDid, string recipientDid, ProtocolContext context, CancellationToken ct)
        => new(true);
}

/// <summary>QA-1 helpers.</summary>
internal static class Qa1
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>The library's own log lines (not the framework's) written by the mediator clients.</summary>
    public static IReadOnlyList<(string Category, LogLevel Level, string Text)> ClientLines(CapturingLoggerProvider logs)
        => logs.CategorizedRecords.Where(r => r.Category.StartsWith("DidComm.Mediator.Client", StringComparison.Ordinal)).ToArray();

    /// <summary><paramref name="count"/> distinct bare DIDs nobody resolves (registration needs only the shape).</summary>
    public static string[] FakeDids(int count, string tag)
        => Enumerable.Range(0, count).Select(i => $"did:example:qa1{tag}{i:D5}").ToArray();
}
