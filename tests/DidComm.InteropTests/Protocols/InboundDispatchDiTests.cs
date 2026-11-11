using System.Collections.Concurrent;
using System.Text;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.Transports;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.WebSocket;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Protocols;

/// <summary>
/// #92 G9 — <c>UseInboundDispatch()</c> and the WebSocket transport's factory registration through the
/// real DI container (F1–F4): the graph resolves without a cycle or a hang (L-017), the consumer is
/// wired into the transport whatever else is registered (JC-19), <c>ReceiveInbound</c> without a
/// consumer fails with an actionable message, a host consumer registered first wins, and disposing
/// the container ends every session loop with nothing left unobserved.
/// </summary>
public sealed class InboundDispatchDiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly Uri Endpoint = new("ws://agents.r.us/ws");

    [Fact]
    public async Task UseInboundDispatch_with_WebSocket_and_built_in_protocols_resolves_without_a_cycle_or_hang()
    {
        var sockets = new ScriptedSocketFactory();
        await using var sp = Build(sockets, b => b.AddBuiltInProtocols());

        // Off the test thread and bounded: a DI cycle through the transport would park the resolver.
        var resolved = await Task.Run(() => (
            Client: sp.GetRequiredService<DidCommClient>(),
            Router: sp.GetRequiredService<ITransportRouter>(),
            Transport: sp.GetRequiredService<WebSocketDidCommTransport>(),
            Consumer: sp.GetRequiredService<IInboundFrameConsumer>(),
            Dispatcher: sp.GetRequiredService<ProtocolDispatcher>())).WaitAsync(Timeout);

        resolved.Consumer.GetType().Name.Should().Be("InboundDispatchConsumer");
        sp.GetServices<IDidCommTransport>().Should().ContainSingle().Which.Should().BeSameAs(resolved.Transport);
        resolved.Router.Should().NotBeNull();
        resolved.Client.Should().NotBeNull();
        resolved.Dispatcher.Should().NotBeNull();
    }

    [Fact]
    public void ReceiveInbound_without_UseInboundDispatch_fails_resolution_with_an_actionable_message()
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.UseWebSocketTransport(o => o.ReceiveInbound = true);
        });
        using var sp = services.BuildServiceProvider();

        var resolve = () => sp.GetRequiredService<WebSocketDidCommTransport>();

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*ReceiveInbound*UseInboundDispatch*");
    }

    [Fact]
    public async Task A_host_consumer_registered_first_wins()
    {
        var mine = new RecordingFrameConsumer();
        var sockets = new ScriptedSocketFactory();
        await using var sp = Build(sockets, configureServices: s => s.AddSingleton<IInboundFrameConsumer>(mine));

        sp.GetRequiredService<IInboundFrameConsumer>().Should().BeSameAs(mine, "UseInboundDispatch is a TryAdd");
        sp.GetServices<IInboundFrameConsumer>().Should().ContainSingle();

        // And the transport the container built is wired to it.
        await OpenSessionAndPushAsync(sp, sockets, "to the host consumer");
        (await WaitUntilAsync(() => mine.Calls == 1)).Should().BeTrue();
        Encoding.UTF8.GetString(mine.Frames.Single().Payload.Span).Should().Be("to the host consumer");
    }

    [Fact]
    public async Task UseWebSocketTransport_resolves_without_logging_registered()
    {
        // No AddLogging: ILogger<T> is not registered. Type activation would then have skipped the
        // four-parameter constructor; the explicit factory wires the consumer regardless (JC-19).
        var mine = new RecordingFrameConsumer();
        var sockets = new ScriptedSocketFactory();
        await using var sp = Build(sockets, configureServices: s => s.AddSingleton<IInboundFrameConsumer>(mine));
        sp.GetService<Microsoft.Extensions.Logging.ILogger<WebSocketDidCommTransport>>().Should().BeNull("the premise: no logging is registered");

        await OpenSessionAndPushAsync(sp, sockets, "wired");

        (await WaitUntilAsync(() => mine.Calls == 1)).Should().BeTrue("the consumer is wired without a logger");
    }

    [Fact]
    public async Task Disposing_the_container_ends_session_loops_without_unobserved_exceptions()
    {
        var unobserved = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var inner in e.Exception.Flatten().InnerExceptions)
                if (inner.ToString().Contains("DidComm.", StringComparison.Ordinal))
                    unobserved.Enqueue(inner);
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var sockets = new ScriptedSocketFactory();
            sockets.OnCreate = s => s.AnswerCloseOutput = false;
            WebSocketDidCommTransport transport;
            await using (var sp = Build(sockets, b => b.AddBuiltInProtocols()))
            {
                transport = sp.GetRequiredService<WebSocketDidCommTransport>();
                // Two session sockets, one of them with a frame the built-in consumer drops (not a DIDComm envelope).
                await OpenSessionAndPushAsync(sp, sockets, "not an envelope");
                await transport.SendAsync(Request(ReturnRouteMode.All, "did:example:carol"), default);
                transport.LiveSessionSocketCount.Should().Be(2);
            }

            sockets.Sockets.Should().HaveCount(2).And.OnlyContain(s => s.Disposed, "the container disposed the transport and every session socket");
            transport.LiveSessionSocketCount.Should().Be(0, "every receive loop ended within the dispose");
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            unobserved.Should().BeEmpty();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    // ---- helpers ----

    private static ServiceProvider Build(ScriptedSocketFactory sockets, Action<DidCommBuilder>? configure = null, Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        configureServices?.Invoke(services);
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.UseWebSocketTransport(o =>
            {
                o.AllowedSchemes = new[] { "ws" };
                o.MaxReconnectAttempts = 0;
                o.ReceiveInbound = true;
                o.WebSocketFactory = sockets.Create;
                o.Connect = (_, _, _) => Task.CompletedTask;
            });
            b.UseInboundDispatch();
            configure?.Invoke(b);
        });
        return services.BuildServiceProvider();
    }

    private static TransportRequest Request(ReturnRouteMode? mode, string local = "did:example:alice")
        => new(Endpoint, Encoding.UTF8.GetBytes("request"), MediaTypes.Encrypted) { ReturnRoute = mode, LocalDid = local, PeerDid = "did:example:bob" };

    /// <summary>Open a session socket through the container's transport and have the peer push one message on it.</summary>
    private static async Task OpenSessionAndPushAsync(IServiceProvider sp, ScriptedSocketFactory sockets, string text)
    {
        var transport = sp.GetRequiredService<WebSocketDidCommTransport>();
        var before = sockets.Count;
        await transport.SendAsync(Request(ReturnRouteMode.All), default);
        sockets.Count.Should().Be(before + 1);
        sockets.Sockets[^1].PushMessage(Encoding.UTF8.GetBytes(text));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
        => await DidComm.InteropTests.AspNetCore.ReturnRouteTestHost.WaitUntilAsync(condition, Timeout);
}
