using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Protocols;

/// <summary>
/// #91 F13 — the return-route services through the real DI container: <c>AddDidComm</c> registers
/// the session registry and the replay guard as singletons, a host registration made first wins,
/// options configured through <c>AddOptions&lt;T&gt;().Configure</c> reach them, invalid options fail
/// on first resolution, and (L-017) a handler that takes BOTH services in its constructor resolves
/// with the registry, the dispatcher and the facade without cycling the graph.
/// </summary>
public sealed class ReturnRouteDiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void AddDidComm_registers_the_in_memory_registry_and_guard_as_singletons()
    {
        using var sp = BuildProvider();

        var registry = sp.GetRequiredService<IReturnRouteSessionRegistry>();
        registry.Should().BeOfType<InMemoryReturnRouteSessionRegistry>();
        sp.GetRequiredService<IReturnRouteSessionRegistry>().Should().BeSameAs(registry, "one registry is shared by every endpoint and handler");

        var guard = sp.GetRequiredService<IInboundReplayGuard>();
        guard.Should().BeOfType<InMemoryReplayGuard>();
        sp.GetRequiredService<IInboundReplayGuard>().Should().BeSameAs(guard, "one replay cache is shared by every endpoint");

        using (var scope = sp.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IReturnRouteSessionRegistry>().Should().BeSameAs(registry, "a request scope sees the root singleton");
            scope.ServiceProvider.GetRequiredService<IInboundReplayGuard>().Should().BeSameAs(guard);
        }

        sp.GetServices<IReturnRouteSessionRegistry>().Should().HaveCount(1);
        sp.GetServices<IInboundReplayGuard>().Should().HaveCount(1);
    }

    [Fact]
    public void A_host_registration_made_before_AddDidComm_wins()
    {
        var hostRegistry = new InMemoryReturnRouteSessionRegistry(new ReturnRouteSessionOptions { MaxSessionsPerPeer = 1, MaxSessions = 2 });
        var hostGuard = new CountingGuard();
        var services = new ServiceCollection();
        services.AddSingleton<IReturnRouteSessionRegistry>(hostRegistry);
        services.AddSingleton<IInboundReplayGuard>(hostGuard);
        AddDidComm(services);
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IReturnRouteSessionRegistry>().Should().BeSameAs(hostRegistry, "AddDidComm uses TryAdd");
        sp.GetRequiredService<IInboundReplayGuard>().Should().BeSameAs(hostGuard);
        sp.GetServices<IReturnRouteSessionRegistry>().Should().HaveCount(1);
        sp.GetServices<IInboundReplayGuard>().Should().HaveCount(1);
    }

    [Fact]
    public void Configured_replay_options_reach_the_guard()
    {
        // A 60 s window: a message created 61 s ago is too old, one created 59 s ago is admitted —
        // with the default 5-minute window both would be admitted.
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var services = new ServiceCollection();
        AddDidComm(services, o => o.Clock = () => now);
        services.AddOptions<ReplayProtectionOptions>().Configure(o => o.Window = TimeSpan.FromSeconds(60));
        using var sp = services.BuildServiceProvider();

        var guard = sp.GetRequiredService<IInboundReplayGuard>();
        guard.TryAdmit("did:example:alice", "m-old", now.ToUnixTimeSeconds() - 61, now).Should().Be(ReplayAdmission.TooOld);
        guard.TryAdmit("did:example:alice", "m-fresh", now.ToUnixTimeSeconds() - 59, now).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit("did:example:alice", "m-fresh", now.ToUnixTimeSeconds() - 59, now).Should().Be(ReplayAdmission.Duplicate);
    }

    [Fact]
    public void Configured_session_options_reach_the_registry()
    {
        var services = new ServiceCollection();
        AddDidComm(services);
        services.AddOptions<ReturnRouteSessionOptions>().Configure(o =>
        {
            o.MaxSessionsPerPeer = 1;
            o.MaxSessions = 1;
        });
        using var sp = services.BuildServiceProvider();

        var registry = sp.GetRequiredService<IReturnRouteSessionRegistry>();
        var first = new StubSession("did:example:alice");
        registry.Register(first).Outcome.Should().Be(SessionRegistrationOutcome.Registered);
        registry.Register(new StubSession("did:example:bob")).Outcome.Should().Be(SessionRegistrationOutcome.RefusedTotalBound, "MaxSessions = 1 was configured");
        registry.Register(new StubSession("did:example:alice")).Evicted.Should().BeSameAs(first, "MaxSessionsPerPeer = 1 was configured");
    }

    [Fact]
    public async Task The_container_disposes_the_registry_AddDidComm_built_and_the_registry_keeps_working()
    {
        // The registry owns a notifier loop; the container that built it stops it, and an endpoint
        // that unregisters during host shutdown still finds a working registry.
        var sp = BuildProvider();
        var registry = (InMemoryReturnRouteSessionRegistry)sp.GetRequiredService<IReturnRouteSessionRegistry>();
        var opened = 0;
        registry.SessionOpened += (_, _) => Interlocked.Increment(ref opened);
        registry.Notifier.IsCompleted.Should().BeFalse();

        await sp.DisposeAsync().AsTask().WaitAsync(Timeout);

        await registry.Notifier.WaitAsync(Timeout); // only Dispose stops it
        var session = new StubSession("did:example:alice");
        registry.Register(session).Registered.Should().BeTrue();
        registry.Unregister(session).Should().BeTrue();
        registry.DroppedNotifications.Should().Be(1, "the registration's notification had no notifier to deliver it");
        Volatile.Read(ref opened).Should().Be(0);
    }

    [Fact]
    public void Invalid_replay_options_throw_on_first_resolution()
    {
        var services = new ServiceCollection();
        AddDidComm(services);
        services.AddOptions<ReplayProtectionOptions>().Configure(o => o.MaxEntriesPerPeer = o.MaxEntries + 1);
        using var sp = services.BuildServiceProvider();

        var resolve = () => sp.GetRequiredService<IInboundReplayGuard>();
        resolve.Should().Throw<InvalidOperationException>().WithMessage("*MaxEntriesPerPeer*");
    }

    [Fact]
    public void Invalid_session_options_throw_on_first_resolution()
    {
        var services = new ServiceCollection();
        AddDidComm(services);
        services.AddOptions<ReturnRouteSessionOptions>().Configure(o => o.MaxSessions = 0);
        using var sp = services.BuildServiceProvider();

        var resolve = () => sp.GetRequiredService<IReturnRouteSessionRegistry>();
        resolve.Should().Throw<InvalidOperationException>().WithMessage("*MaxSessions*");
    }

    [Fact]
    public async Task A_handler_taking_both_services_resolves_with_the_registry_dispatcher_and_facade()
    {
        // L-017: the handler registry's factory walks IEnumerable<IProtocolHandler>; a handler that
        // takes the session registry and the replay guard must not cycle back into it. Resolution runs
        // off-thread under a timeout so a cycle fails the test instead of hanging the run.
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
            b.AddProtocol<SessionAwareHandler>();
        });
        var sp = services.BuildServiceProvider();

        var (handlerRegistry, dispatcher, client, handler) = await Task.Run(() => (
            sp.GetRequiredService<ProtocolHandlerRegistry>(),
            sp.GetRequiredService<ProtocolDispatcher>(),
            sp.GetRequiredService<DidCommClient>(),
            sp.GetRequiredService<SessionAwareHandler>())).WaitAsync(Timeout);

        handlerRegistry.TryResolve(SessionAwareHandler.Type, out var resolved).Should().BeTrue();
        resolved.Should().BeSameAs(handler);
        handler.Sessions.Should().BeSameAs(sp.GetRequiredService<IReturnRouteSessionRegistry>());
        handler.Replay.Should().BeSameAs(sp.GetRequiredService<IInboundReplayGuard>());
        dispatcher.Should().NotBeNull();
        client.Should().NotBeNull();

        await sp.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        AddDidComm(services);
        return services.BuildServiceProvider();
    }

    private static void AddDidComm(IServiceCollection services, Action<DidCommOptions>? configure = null)
        => services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
            if (configure is not null)
                b.Configure(configure);
        });

    /// <summary>A handler of the kind #94 will be: it depends on both return-route services.</summary>
    private sealed class SessionAwareHandler : IProtocolHandler
    {
        public const string Type = "https://didcomm.org/test-rr-di/1.0/probe";

        public SessionAwareHandler(IReturnRouteSessionRegistry sessions, IInboundReplayGuard replay)
        {
            Sessions = sessions;
            Replay = replay;
        }

        public IReturnRouteSessionRegistry Sessions { get; }

        public IInboundReplayGuard Replay { get; }

        public string ProtocolUri => "https://didcomm.org/test-rr-di/1.0";

        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
            => Task.FromResult<Message?>(null);
    }

    private sealed class CountingGuard : IInboundReplayGuard
    {
        public int Calls { get; private set; }

        public ReplayAdmission TryAdmit(string peerDidSubject, string messageId, long? createdTimeEpochSeconds, DateTimeOffset now)
        {
            Calls++;
            return ReplayAdmission.Admitted;
        }
    }

    private sealed class StubSession : IReturnRouteSession
    {
        public StubSession(string peerDid) => PeerDid = peerDid;

        public string PeerDid { get; }

        public string LocalDid => "did:example:mediator";

        public ReturnRouteMode Mode => ReturnRouteMode.All;

        public string? Thread => null;

        public ReturnRouteTransport Transport => ReturnRouteTransport.WebSocket;

        public bool IsLive => true;

        public CancellationToken Closed => CancellationToken.None;

        public ValueTask<bool> TrySendAsync(Message message, CancellationToken ct) => ValueTask.FromResult(false);

        public ValueTask<ReturnRouteSendResult> TrySendAsync(Message message, int maxPackedBytes, CancellationToken ct)
            => ValueTask.FromResult(ReturnRouteSendResult.NotSent);
    }
}
