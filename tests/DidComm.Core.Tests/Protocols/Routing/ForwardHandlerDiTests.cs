using System.Diagnostics.Metrics;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.TestSupport;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static DidComm.Tests.Protocols.Routing.ForwardTestEnvelopes;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// #95 B2: <c>AddRoutingMediator()</c> registers one shared <see cref="ForwardMetrics"/> (from the
/// container's <see cref="IMeterFactory"/> when there is one) and builds <see cref="ForwardHandler"/>
/// through a factory that hands it that counter — without losing the L-017 protection the
/// <c>AddProtocol&lt;T&gt;()</c> registration gives: a collaborator registered through a factory that
/// closes a cycle back into the handler, the registry or the dispatcher fails the first resolution
/// with <see cref="InvalidOperationException"/> instead of hanging it.
/// </summary>
public sealed class ForwardHandlerDiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task The_handler_counts_on_the_containers_ForwardMetrics_built_from_its_IMeterFactory()
    {
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        await using var sp = Build(services => services.AddSingleton<IMeterFactory>(meters));

        var handler = sp.GetRequiredService<ForwardHandler>();
        handler.Metrics.Should().BeSameAs(sp.GetRequiredService<ForwardMetrics>(), "the handler and the mediator's sink share one counter");
        handler.Metrics.Meter.Scope.Should().BeSameAs(meters, "the meter comes from the host's factory");

        var forward = ForwardHandlerTests.Forward("did:example:bob");
        await sp.GetRequiredService<ProtocolDispatcher>().DispatchAsync(AsPlaintext(forward), client: null, new DidCommOptions(), CancellationToken.None);

        outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedNotEncrypted] = 1 });
    }

    [Fact]
    public async Task A_ForwardMetrics_registered_first_wins_and_repeated_calls_add_no_second()
    {
        using var meters = new TestMeterFactory();
        var mine = new ForwardMetrics(meters);
        await using var sp = Build(
            services => services.AddSingleton(mine),
            b => b.AddRoutingMediator());

        sp.GetServices<ForwardMetrics>().Should().ContainSingle().Which.Should().BeSameAs(mine);
        sp.GetRequiredService<ForwardHandler>().Metrics.Should().BeSameAs(mine);
        sp.GetServices<ForwardHandler>().Should().ContainSingle();
        sp.GetServices<IProtocolHandler>().OfType<ForwardHandler>().Should().ContainSingle()
            .Which.Should().BeSameAs(sp.GetRequiredService<ForwardHandler>(), "the registry entry and the typed singleton are one instance");
    }

    [Fact]
    public async Task Without_an_IMeterFactory_the_counter_owns_its_meter_and_the_container_disposes_it()
    {
        var sp = Build();
        var metrics = sp.GetRequiredService<ForwardMetrics>();
        using var outcomes = ForwardOutcomeRecorder.For(metrics.Meter);
        metrics.Meter.Scope.Should().BeNull();

        await sp.DisposeAsync();

        outcomes.Completed.Should().BeTrue();
    }

    [Fact]
    public async Task Disposing_the_container_stops_the_handler_and_leaves_a_factory_meter_to_its_factory()
    {
        using var meters = new TestMeterFactory();
        using var outcomes = ForwardOutcomeRecorder.For(meters);
        var sp = Build(services => services.AddSingleton<IMeterFactory>(meters));
        var handler = sp.GetRequiredService<ForwardHandler>();

        await sp.DisposeAsync().AsTask().WaitAsync(Timeout);
        await handler.ShutdownCompletion.WaitAsync(Timeout);

        outcomes.Completed.Should().BeFalse("the host's factory owns the meter it created");
    }

    // ---- L-017: the factory-built handler keeps fail-fast cycle detection ----------------

    [Fact]
    public async Task A_factory_registered_sink_that_takes_the_dispatcher_fails_fast_from_the_dispatcher()
    {
        await using var sp = Build(services => services.AddSingleton<IForwardSink>(s => new DispatcherTakingSink(s.GetRequiredService<ProtocolDispatcher>())));

        var failure = await ResolveWithinAsync(() => sp.GetRequiredService<ProtocolDispatcher>());

        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("circular");
    }

    [Fact]
    public async Task A_factory_registered_sink_that_takes_the_dispatcher_fails_fast_from_the_handler()
    {
        await using var sp = Build(services => services.AddSingleton<IForwardSink>(s => new DispatcherTakingSink(s.GetRequiredService<ProtocolDispatcher>())));

        var failure = await ResolveWithinAsync(() => sp.GetRequiredService<ForwardHandler>());

        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("circular");
    }

    [Fact]
    public async Task A_factory_registered_sink_that_takes_the_handler_itself_fails_fast()
    {
        // Only the handler factory's own guard sees this one: the cycle never passes the registry
        // or the dispatcher.
        await using var sp = Build(services => services.AddSingleton<IForwardSink>(s => new HandlerTakingSink(s.GetRequiredService<ForwardHandler>())));

        var failure = await ResolveWithinAsync(() => sp.GetRequiredService<ForwardHandler>());

        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("circular").And.Contain(nameof(ForwardHandler));
    }

    [Fact]
    public async Task A_sink_that_resolves_the_dispatcher_lazily_still_resolves()
    {
        await using var sp = Build(services => services.AddSingleton<IForwardSink>(s => new LazySink(s)));

        var failure = await ResolveWithinAsync(() => (sp.GetRequiredService<ProtocolDispatcher>(), sp.GetRequiredService<ForwardHandler>()));

        failure.Should().BeNull();
    }

    // ---- helpers -------------------------------------------------------------------------

    private static ServiceProvider Build(Action<IServiceCollection>? before = null, Action<DidCommBuilder>? more = null)
    {
        var services = new ServiceCollection();
        before?.Invoke(services);
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
            b.AddRoutingMediator();
            more?.Invoke(b);
        });
        return services.BuildServiceProvider();
    }

    /// <summary>Resolve off-thread under a timeout, so a cycle fails the test instead of hanging the run; the exception, or <c>null</c>.</summary>
    private static async Task<Exception?> ResolveWithinAsync(Func<object> resolve)
    {
        try
        {
            await Task.Run(resolve).WaitAsync(Timeout);
            return null;
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException("The resolution hung: a dependency cycle through a factory was not detected.");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private sealed class DispatcherTakingSink(ProtocolDispatcher dispatcher) : IForwardSink
    {
        public ProtocolDispatcher Dispatcher { get; } = dispatcher;
        public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class HandlerTakingSink(ForwardHandler handler) : IForwardSink
    {
        public ForwardHandler Handler { get; } = handler;
        public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class LazySink(IServiceProvider services) : IForwardSink
    {
        public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct)
        {
            _ = services.GetRequiredService<ProtocolDispatcher>();
            return Task.CompletedTask;
        }
    }
}
