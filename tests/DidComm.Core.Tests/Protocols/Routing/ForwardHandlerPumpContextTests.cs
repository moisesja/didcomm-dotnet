using DidComm.Facade;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using AwesomeAssertions;
using Xunit;
using static DidComm.Tests.Protocols.Routing.ForwardTestEnvelopes;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// #95: the forward pump is started with the execution context suppressed. The handler is a
/// singleton that can be built lazily on the first request; its pump must not carry that request's
/// ambient state (an <see cref="AsyncLocal{T}"/>, a logging scope, an activity) into every forward
/// it ever processes.
/// </summary>
public sealed class ForwardHandlerPumpContextTests
{
    private const string Bob = "did:example:bob";
    private static readonly AsyncLocal<string?> Ambient = new();

    [Fact]
    public async Task The_pump_does_not_carry_the_ambient_state_of_the_caller_that_built_the_handler()
    {
        var sink = new AmbientRecordingSink();
        var registry = new InMemoryForwardRecipientRegistry();
        registry.Register(Bob);

        ForwardHandler handler;
        Ambient.Value = "the first request's scope";
        try
        {
            handler = new ForwardHandler(NewProcessor(), sink, registry);
        }
        finally
        {
            Ambient.Value = null;
        }

        await using (handler)
        {
            var forward = ForwardHandlerTests.Forward(Bob);
            var context = new ProtocolContext(AsDecryptedByMediator(forward), new DidComm.Threading.ThreadState(forward.Id), Client: null, new DidCommOptions(), new DidComm.Threading.InMemoryThreadStateStore());
            await handler.HandleAsync(forward, context, CancellationToken.None);
            await handler.FlushAsync(TimeSpan.FromSeconds(30));
        }

        sink.Seen.Should().ContainSingle().Which.Should().BeNull("the pump runs without the execution context of whoever built the handler");
    }

    [Fact]
    public async Task A_handler_built_while_the_flow_is_already_suppressed_still_starts()
    {
        var sink = new AmbientRecordingSink();
        var registry = new InMemoryForwardRecipientRegistry();
        registry.Register(Bob);

        ForwardHandler handler;
        using (ExecutionContext.SuppressFlow())
            handler = new ForwardHandler(NewProcessor(), sink, registry);

        await using (handler)
        {
            var forward = ForwardHandlerTests.Forward(Bob);
            var context = new ProtocolContext(AsDecryptedByMediator(forward), new DidComm.Threading.ThreadState(forward.Id), Client: null, new DidCommOptions(), new DidComm.Threading.InMemoryThreadStateStore());
            await handler.HandleAsync(forward, context, CancellationToken.None);
            await handler.FlushAsync(TimeSpan.FromSeconds(30));
        }

        sink.Seen.Should().ContainSingle();
    }

    private sealed class AmbientRecordingSink : IForwardSink
    {
        private readonly List<string?> _seen = new();

        public IReadOnlyList<string?> Seen
        {
            get
            {
                lock (_seen)
                    return _seen.ToArray();
            }
        }

        public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct)
        {
            lock (_seen)
                _seen.Add(Ambient.Value);
            return Task.CompletedTask;
        }
    }
}
