using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.WebSocket;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// #92 red-team regressions, ported from the adversarial probes and kept as ordinary tests:
/// <list type="bullet">
/// <item>ADV-01: a retire or 1009 backstop that fires on a timer thread runs the inbound consumer's
/// cancellation callbacks there; a callback that throws must not become an unhandled exception that
/// terminates the process (it used to: <c>CancellationTokenSource.CancelAfter</c> rethrows on the
/// timer thread).</item>
/// <item>ADV-02: a host <see cref="IInboundFrameConsumer"/> or <see cref="IDidCommTransport"/> that
/// takes <see cref="DidCommClient"/> in its constructor fails the first resolution with an
/// <see cref="InvalidOperationException"/>, instead of deadlocking the application's startup.</item>
/// <item>ADV-04: an HTTP response that declares a large <c>Content-Length</c> costs memory as its
/// bytes arrive, not the declared length up front.</item>
/// </list>
/// Every wait is a rendezvous or a bounded condition (L-044).
/// </summary>
public sealed class ReturnRouteClientRedTeamRegressionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly Uri Endpoint = new("ws://agents.r.us/ws");

    // ---- ADV-01: a throwing cancellation callback on the consumer's token ----

    /// <summary>
    /// The consumer returned normally but leaked a registration on its token (<c>ct.Register</c>
    /// without disposing it), whose callback throws once its work is done. The peer then sends one
    /// message over the cap and stays silent after the 1009 close, so the backstop ends the loop on
    /// a timer thread and runs that callback there. No application send is involved.
    /// </summary>
    [Fact]
    public async Task ADV_01_A_peer_triggered_1009_backstop_runs_a_leaked_throwing_registration_without_terminating_the_process()
    {
        using var unhandled = new UnhandledExceptionRecorder();
        const int cap = 4096;
        var sockets = new ScriptedSocketFactory { OnCreate = s => s.AnswerCloseOutput = false };
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRan = 0;
        var consumer = new RecordingFrameConsumer
        {
            Behavior = (_, _, ct) =>
            {
                var work = new TaskCompletionSource();
                // Leaked (never disposed); SetCanceled throws once the work has completed.
                ct.Register(() =>
                {
                    Interlocked.Exchange(ref callbackRan, 1);
                    work.SetCanceled();
                });
                work.SetResult();
                completed.TrySetResult();
                return ValueTask.CompletedTask;
            },
        };
        await using var transport = NewTransport(sockets, consumer, sendTimeout: TimeSpan.FromMilliseconds(200), cap: cap);
        await transport.SendAsync(Request(ReturnRouteMode.All), default);
        var session = sockets.Sockets[0];

        session.PushMessage(Encoding.UTF8.GetBytes("ordinary push"));
        await completed.Task.WaitAsync(Timeout);
        session.PushMessage(new byte[cap + 1]); // one oversized message, then silence

        (await ReturnRouteTestHost.WaitUntilAsync(() => Volatile.Read(ref callbackRan) == 1 && transport.LiveSessionSocketCount == 0, Timeout))
            .Should().BeTrue("the backstop ended the loop and ran the leaked callback");
        session.CloseOutputStatus.Should().Be(WebSocketCloseStatus.MessageTooBig);
        unhandled.Seen.Should().BeEmpty("a host cancellation callback must never become an unhandled exception");
    }

    /// <summary>
    /// The application-triggered variant: a consumer parked on a message registers a throwing
    /// callback, the application retires the session with <c>return_route: none</c>, and the peer
    /// never answers the close. The backstop cancels the token on a timer thread.
    /// </summary>
    [Fact]
    public async Task ADV_01_A_retire_backstop_runs_a_throwing_callback_of_a_parked_consumer_without_terminating_the_process()
    {
        using var unhandled = new UnhandledExceptionRecorder();
        var sockets = new ScriptedSocketFactory { OnCreate = s => s.AnswerCloseOutput = false };
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new RecordingFrameConsumer
        {
            Behavior = async (_, _, ct) =>
            {
                ct.Register(() => throw new InvalidOperationException("host callback fault (test)"));
                ct.Register(() => cancelled.TrySetResult());
                parked.TrySetResult();
                // Ignores the token: the loop stays in the consumer until the test releases it.
                await release.Task.ConfigureAwait(false);
            },
        };
        try
        {
            await using var transport = NewTransport(sockets, consumer, sendTimeout: TimeSpan.FromMilliseconds(200));
            await transport.SendAsync(Request(ReturnRouteMode.All), default);
            sockets.Sockets[0].PushMessage(Encoding.UTF8.GetBytes("push"));
            await parked.Task.WaitAsync(Timeout);

            // Retire: the close frame is sent, the peer never answers, and the backstop is armed.
            (await transport.SendAsync(Request(ReturnRouteMode.None), default)).Accepted.Should().BeTrue();
            await cancelled.Task.WaitAsync(Timeout);

            release.TrySetResult();
            (await ReturnRouteTestHost.WaitUntilAsync(() => transport.LiveSessionSocketCount == 0, Timeout))
                .Should().BeTrue("the loop ends once the consumer returns");
            unhandled.Seen.Should().BeEmpty("a host cancellation callback must never become an unhandled exception");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>
    /// The other two ways the loop's token is cancelled — a send that fails on the session socket,
    /// and dispose — run the same throwing callback; neither lets its fault escape.
    /// </summary>
    [Theory]
    [InlineData("send failure")]
    [InlineData("dispose")]
    public async Task ADV_01_Every_other_cancellation_of_the_loop_contains_a_throwing_consumer_callback(string path)
    {
        using var unhandled = new UnhandledExceptionRecorder();
        var sockets = new ScriptedSocketFactory();
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new RecordingFrameConsumer
        {
            Behavior = async (_, _, ct) =>
            {
                ct.Register(() => throw new InvalidOperationException("host callback fault (test)"));
                ct.Register(() => cancelled.TrySetResult());
                parked.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, ct).ConfigureAwait(false);
            },
        };
        var transport = NewTransport(sockets, consumer);
        await using (transport)
        {
            await transport.SendAsync(Request(ReturnRouteMode.All), default);
            sockets.Sockets[0].PushMessage(Encoding.UTF8.GetBytes("push"));
            await parked.Task.WaitAsync(Timeout);

            if (path == "send failure")
            {
                sockets.Sockets[0].FailNextSend();
                var send = () => transport.SendAsync(Request(null), default);
                await send.Should().ThrowAsync<DidComm.Exceptions.TransportException>();
            }
            else
            {
                var dispose = () => transport.DisposeAsync().AsTask();
                await dispose.Should().NotThrowAsync();
            }
            await cancelled.Task.WaitAsync(Timeout);
            (await ReturnRouteTestHost.WaitUntilAsync(() => transport.LiveSessionSocketCount == 0, Timeout)).Should().BeTrue();
        }
        unhandled.Seen.Should().BeEmpty();
    }

    // ---- ADV-02: a host consumer or transport that takes the facade ----

    [Fact]
    public async Task ADV_02_A_host_consumer_taking_DidCommClient_fails_first_resolution_fast_instead_of_hanging()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInboundFrameConsumer, FacadeTakingConsumer>();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.UseWebSocketTransport(o => o.ReceiveInbound = true);
            b.UseInboundDispatch();
        });
        var sp = services.BuildServiceProvider();

        var failure = await ResolveWithinAsync(() => sp.GetRequiredService<DidCommClient>());

        var message = failure.Should().BeOfType<InvalidOperationException>().Which.Message;
        message.Should().Contain("circular").And.Contain(typeof(DidCommClient).FullName!).And.Contain("IInboundFrameConsumer").And.Contain("IServiceProvider");
        (await ResolveWithinAsync(() => sp.GetRequiredService<WebSocketDidCommTransport>()))
            .Should().BeOfType<InvalidOperationException>("the same cycle reached from the transport's end");
        await sp.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    [Fact]
    public async Task ADV_02_A_custom_transport_taking_DidCommClient_fails_first_resolution_fast_instead_of_hanging()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDidCommTransport, FacadeTakingTransport>();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
        });
        var sp = services.BuildServiceProvider();

        var failure = await ResolveWithinAsync(() => sp.GetRequiredService<DidCommClient>());

        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("circular").And.Contain("IDidCommTransport");
        (await ResolveWithinAsync(() => sp.GetRequiredService<IDidCommTransport>()))
            .Should().BeOfType<InvalidOperationException>("the same cycle reached from the transport's end");
        await sp.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    [Fact]
    public async Task ADV_02_The_guarded_facade_factory_still_resolves_one_instance_for_an_acyclic_graph()
    {
        // The guard only throws on re-entry: the ordinary graph, with a handler taking the facade, a
        // consumer and a transport, resolves once and caches the singleton.
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.UseWebSocketTransport(o => o.ReceiveInbound = true);
            b.UseInboundDispatch();
            b.AddBuiltInProtocols();
            b.AddProtocol<FacadeTakingHandler>();
        });
        var sp = services.BuildServiceProvider();

        var failure = await ResolveWithinAsync(() => (sp.GetRequiredService<DidCommClient>(), sp.GetRequiredService<ProtocolDispatcher>()));

        failure.Should().BeNull();
        sp.GetRequiredService<FacadeTakingHandler>().Client.Should().BeSameAs(sp.GetRequiredService<DidCommClient>());
        sp.GetServices<DidCommClient>().Should().ContainSingle("the descriptor's service type is still DidCommClient");
        await sp.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    // ---- ADV-02 (follow-up): a host consumer that takes the transport or the router ----

    public static TheoryData<Type> TransportTakingConsumers() => new()
    {
        typeof(WebSocketTransportTakingConsumer),
        typeof(SingleTransportTakingConsumer),
        typeof(AllTransportsTakingConsumer),
        typeof(RouterTakingConsumer),
    };

    /// <summary>
    /// The consumer is a dependency of the WebSocket transport, so a consumer that takes the
    /// transport (directly, as <see cref="IDidCommTransport"/>, or through the router) cycles
    /// without passing through <see cref="DidCommClient"/>. Whichever end the application resolves
    /// first, the resolution fails fast with an actionable <see cref="InvalidOperationException"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(TransportTakingConsumers))]
    public async Task ADV_02_A_host_consumer_taking_the_transport_or_the_router_fails_first_resolution_fast_instead_of_hanging(Type consumer)
    {
        var entries = new (string Name, Func<IServiceProvider, object> Resolve)[]
        {
            ("DidCommClient", sp => sp.GetRequiredService<DidCommClient>()),
            ("WebSocketDidCommTransport", sp => sp.GetRequiredService<WebSocketDidCommTransport>()),
            ("IInboundFrameConsumer", sp => sp.GetRequiredService<IInboundFrameConsumer>()),
            ("ITransportRouter", sp => sp.GetRequiredService<ITransportRouter>()),
        };
        foreach (var (name, resolve) in entries)
        {
            // A fresh container per entry point: each one is a first resolution.
            var services = new ServiceCollection();
            services.AddSingleton(typeof(IInboundFrameConsumer), consumer);
            services.AddDidComm(b =>
            {
                b.UseNetDidResolver();
                b.UseSecretsResolver(new InMemorySecretsResolver());
                b.UseWebSocketTransport(o => o.ReceiveInbound = true);
                b.UseInboundDispatch();
            });
            var sp = services.BuildServiceProvider();

            var failure = await ResolveWithinAsync(() => resolve(sp));

            var message = failure.Should().BeOfType<InvalidOperationException>(name).Which.Message;
            message.Should().Contain("circular", name)
                .And.Contain(typeof(WebSocketDidCommTransport).FullName!, name)
                .And.Contain("IInboundFrameConsumer", name)
                .And.Contain("ITransportRouter", name)
                .And.Contain("IServiceProvider", name);
            message.Should().NotContain("did:", "no wire value or identity in the message");
            await sp.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task ADV_02_The_guarded_transport_factory_still_resolves_one_instance_that_IDidCommTransport_maps_to()
    {
        // The guard only throws on re-entry. An acyclic graph, including a host consumer that resolves
        // the transport lazily through IServiceProvider (the documented remedy), resolves once.
        var services = new ServiceCollection();
        services.AddSingleton<IInboundFrameConsumer, LazyTransportConsumer>();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.UseWebSocketTransport(o => o.ReceiveInbound = true);
            b.AddBuiltInProtocols();
        });
        var sp = services.BuildServiceProvider();
        WebSocketDidCommTransport? transport = null;
        IDidCommTransport? single = null;
        IDidCommTransport[]? all = null;
        IInboundFrameConsumer? resolvedConsumer = null;

        var failure = await ResolveWithinAsync(() =>
        {
            sp.GetRequiredService<DidCommClient>();
            transport = sp.GetRequiredService<WebSocketDidCommTransport>();
            single = sp.GetRequiredService<IDidCommTransport>();
            all = sp.GetServices<IDidCommTransport>().ToArray();
            resolvedConsumer = sp.GetRequiredService<IInboundFrameConsumer>();
            return transport;
        });

        failure.Should().BeNull();
        single.Should().BeSameAs(transport, "IDidCommTransport maps to the one WebSocket transport");
        all.Should().ContainSingle().Which.Should().BeSameAs(transport);
        sp.GetServices<WebSocketDidCommTransport>().Should().ContainSingle().Which.Should().BeSameAs(transport);
        (await Task.Run(() => sp.GetRequiredService<WebSocketDidCommTransport>()).WaitAsync(Timeout)).Should().BeSameAs(transport, "a singleton, on any thread");
        resolvedConsumer.Should().BeOfType<LazyTransportConsumer>().Which.Transport.Should().BeSameAs(transport, "resolved lazily, after construction");
        await sp.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    // ---- ADV-04: a declared Content-Length is not allocated up front ----

    [Fact]
    public async Task ADV_04_A_large_declared_length_with_no_body_allocates_far_less_than_the_cap_and_is_discarded_at_the_timeout()
    {
        const int cap = 1024 * 1024;
        var content = GeneratedContent.Stalled(declareLength: true, length: cap);
        var (transport, logs) = BuildHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }, TimeSpan.FromMilliseconds(300));
        var request = new TransportRequest(new Uri("https://agents.r.us/inbox"), new byte[] { 0x7b, 0x7d }, MediaTypes.Encrypted) { MaxResponseBytes = cap };

        var before = GC.GetAllocatedBytesForCurrentThread();
        var send = transport.SendAsync(request, default);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Precondition: everything up to the first body read ran on this thread (the stub answers
        // synchronously), so the measured window covers the body buffer's allocation.
        content.ReadStarted.Task.IsCompleted.Should().BeTrue("the first body read started before SendAsync returned");
        send.IsCompleted.Should().BeFalse("the body is stalled");
        allocated.Should().BeLessThan(cap / 4, "memory is committed as body bytes arrive, not by the declared Content-Length");

        var result = await send.WaitAsync(Timeout);
        result.Accepted.Should().BeTrue();
        result.ResponseDiscarded.Should().BeTrue();
        result.ResponsePayload.Should().BeNull();
        logs.Warnings.Should().ContainSingle().Which.Should().Contain("ReadTimeout");
    }

    public static TheoryData<string> LargeDeclaredBodies() => new() { "exact", "short", "longer than declared" };

    [Theory]
    [MemberData(nameof(LargeDeclaredBodies))]
    public async Task ADV_04_A_large_declared_length_is_read_exactly_and_a_short_body_is_ReadFailed(string @case)
    {
        const int cap = 1024 * 1024;
        const int declared = 200 * 1024;
        var content = GeneratedContent.Generated(declared, declareLength: true, provide: @case switch
        {
            "short" => declared - 1,
            "longer than declared" => declared + 50_000,
            _ => declared,
        });
        var (transport, logs) = BuildHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }, TimeSpan.FromSeconds(30));

        var result = await transport.SendAsync(
            new TransportRequest(new Uri("https://agents.r.us/inbox"), new byte[] { 0x7b, 0x7d }, MediaTypes.Encrypted) { MaxResponseBytes = cap }, default);

        result.Accepted.Should().BeTrue();
        if (@case == "short")
        {
            result.ResponseDiscarded.Should().BeTrue("fewer bytes than the declared Content-Length is a truncated body");
            result.ResponsePayload.Should().BeNull();
            logs.Warnings.Should().ContainSingle().Which.Should().Contain("ReadFailed");
            return;
        }
        result.ResponseDiscarded.Should().BeFalse();
        result.ResponsePayload!.Value.Length.Should().Be(declared, "exactly the declared bytes, never grown by what follows");
        result.ResponsePayload.Value.ToArray().Should().OnlyContain(b => b == GeneratedContent.Fill);
        content.BytesPulled.Should().Be(declared);
        logs.Warnings.Should().BeEmpty();
    }

    // ---- helpers ----

    private static WebSocketDidCommTransport NewTransport(ScriptedSocketFactory sockets, IInboundFrameConsumer consumer, TimeSpan? sendTimeout = null, int? cap = null)
        => new(
            Options.Create(new WebSocketTransportOptions
            {
                AllowedSchemes = new[] { "ws" },
                MaxReconnectAttempts = 0,
                ReceiveInbound = true,
                SendTimeout = sendTimeout ?? TimeSpan.FromSeconds(5),
                WebSocketFactory = sockets.Create,
                Connect = (_, _, _) => Task.CompletedTask,
            }),
            logger: null,
            coreOptions: cap is int c ? Options.Create(new DidCommOptions { MaxReceiveBytes = c }) : null,
            inboundConsumer: consumer);

    private static TransportRequest Request(ReturnRouteMode? mode)
        => new(Endpoint, Encoding.UTF8.GetBytes("request"), MediaTypes.Encrypted) { ReturnRoute = mode, LocalDid = "did:example:alice", PeerDid = "did:example:bob" };

    private static async Task<Exception?> ResolveWithinAsync(Func<object> resolve)
    {
        // Off the test thread and bounded: a cycle the guard misses parks the resolver forever.
        var task = Task.Factory.StartNew(() =>
        {
            try
            {
                resolve();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var completed = await Task.WhenAny(task, Task.Delay(Timeout));
        completed.Should().BeSameAs(task, "DI resolution must complete or fail within the bound; a hang is a deadlock");
        return await task;
    }

    private static (HttpDidCommTransport Transport, HttpTransportResponseTests.ListLogger Logs) BuildHttp(
        Func<HttpRequestMessage, HttpResponseMessage> responder, TimeSpan requestTimeout)
    {
        var services = new ServiceCollection();
        services.AddOptions<HttpTransportOptions>().Configure(o =>
        {
            o.AllowedSchemes = new[] { "https" };
            o.MaxRetryAttempts = 0;
            o.CircuitBreakerFailureThreshold = 100;
            o.RequestTimeout = requestTimeout;
        });
        services.AddHttpClient(HttpDidCommTransport.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(_ => new StubHandler(responder));
        var sp = services.BuildServiceProvider();
        var logs = new HttpTransportResponseTests.ListLogger();
        var transport = new HttpDidCommTransport(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<HttpTransportOptions>>(),
            logs);
        return (transport, logs);
    }

    /// <summary>Records the unhandled exceptions raised while it is alive (one would terminate the test host).</summary>
    private sealed class UnhandledExceptionRecorder : IDisposable
    {
        private readonly ConcurrentQueue<string> _seen = new();

        public UnhandledExceptionRecorder() => AppDomain.CurrentDomain.UnhandledException += OnUnhandled;

        public IReadOnlyCollection<string> Seen => _seen.ToArray();

        public void Dispose() => AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;

        private void OnUnhandled(object? sender, UnhandledExceptionEventArgs e)
        {
            var type = e.ExceptionObject.GetType().FullName ?? "?";
            _seen.Enqueue(type);
            Console.Error.WriteLine("ADV-01 regression: unhandled " + type + ", IsTerminating=" + e.IsTerminating);
        }
    }

    /// <summary>Breaks the documented dependency rule: takes the facade in its constructor.</summary>
    private sealed class FacadeTakingConsumer : IInboundFrameConsumer
    {
        public FacadeTakingConsumer(DidCommClient client) => _ = client;

        public ValueTask ConsumeAsync(InboundFrame frame, CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>Breaks the dependency rule: takes the WebSocket transport, which depends on the consumer.</summary>
    private sealed class WebSocketTransportTakingConsumer : IInboundFrameConsumer
    {
        public WebSocketTransportTakingConsumer(WebSocketDidCommTransport transport) => _ = transport;

        public ValueTask ConsumeAsync(InboundFrame frame, CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>Breaks the dependency rule: takes the last registered transport (the WebSocket one).</summary>
    private sealed class SingleTransportTakingConsumer : IInboundFrameConsumer
    {
        public SingleTransportTakingConsumer(IDidCommTransport transport) => _ = transport;

        public ValueTask ConsumeAsync(InboundFrame frame, CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>Breaks the dependency rule: takes every registered transport.</summary>
    private sealed class AllTransportsTakingConsumer : IInboundFrameConsumer
    {
        public AllTransportsTakingConsumer(IEnumerable<IDidCommTransport> transports) => _ = transports;

        public ValueTask ConsumeAsync(InboundFrame frame, CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>Breaks the dependency rule: takes the router, which takes every transport.</summary>
    private sealed class RouterTakingConsumer : IInboundFrameConsumer
    {
        public RouterTakingConsumer(ITransportRouter router) => _ = router;

        public ValueTask ConsumeAsync(InboundFrame frame, CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>Follows the dependency rule: resolves the transport through the provider when first needed.</summary>
    private sealed class LazyTransportConsumer : IInboundFrameConsumer
    {
        private readonly IServiceProvider _services;

        public LazyTransportConsumer(IServiceProvider services) => _services = services;

        public WebSocketDidCommTransport Transport => _services.GetRequiredService<WebSocketDidCommTransport>();

        public ValueTask ConsumeAsync(InboundFrame frame, CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>A custom transport that takes the facade in its constructor (the facade depends on every transport).</summary>
    private sealed class FacadeTakingTransport : IDidCommTransport
    {
        public FacadeTakingTransport(DidCommClient client) => _ = client;

        public string Scheme => "test";

        public bool CanHandle(Uri endpoint) => false;

        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>A protocol handler may take the facade: the facade does not depend on the handlers.</summary>
    private sealed class FacadeTakingHandler : IProtocolHandler
    {
        public FacadeTakingHandler(DidCommClient client) => Client = client;

        public DidCommClient Client { get; }

        public string ProtocolUri => "https://didcomm.org/test-adv-02/1.0";

        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct) => Task.FromResult<Message?>(null);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
