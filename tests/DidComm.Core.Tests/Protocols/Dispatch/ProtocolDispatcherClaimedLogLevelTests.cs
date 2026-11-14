using System.Collections.Concurrent;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Threading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Tests.Protocols.Dispatch;

/// <summary>
/// #96 R-3: when an internal correlator claims an inbound (it completed a pending request with it,
/// or handed it to a live stream) and no protocol handler is registered for its type, the
/// dispatcher writes its "No protocol handler registered" line at Debug instead of Information —
/// same template, same outcome. A correlator that declines or throws has not claimed the message,
/// every correlator still sees it, and the claim never changes the dispatch.
/// </summary>
public sealed class ProtocolDispatcherClaimedLogLevelTests
{
    private const string NoHandlerTemplate = "No protocol handler registered for the type of inbound message {MessageId} (FR-PROTO-03).";
    private const string UnhandledType = "https://didcomm.org/unhandled/1.0/answer";

    [Fact]
    public async Task Without_correlators_the_no_handler_line_is_Information()
    {
        var logger = new RecordingLogger();
        await using var dispatcher = Dispatcher(logger);

        var outcome = await dispatcher.DispatchAsync(Unpack(Msg(UnhandledType)), client: null, new DidCommOptions());

        outcome.Result.Should().Be(DispatchResult.NoHandler);
        logger.LevelsOf(NoHandlerTemplate).Should().Equal(LogLevel.Information);
    }

    [Fact]
    public async Task A_declining_correlator_keeps_the_no_handler_line_at_Information()
    {
        var logger = new RecordingLogger();
        var correlator = new FixedCorrelator(claims: false);
        await using var dispatcher = Dispatcher(logger, correlator);

        var outcome = await dispatcher.DispatchAsync(Unpack(Msg(UnhandledType)), client: null, new DidCommOptions());

        outcome.Result.Should().Be(DispatchResult.NoHandler);
        correlator.Calls.Should().Be(1);
        logger.LevelsOf(NoHandlerTemplate).Should().Equal(LogLevel.Information);
    }

    [Fact]
    public async Task A_claimed_message_without_a_handler_is_logged_at_Debug_with_the_same_template()
    {
        var logger = new RecordingLogger();
        var correlator = new FixedCorrelator(claims: true);
        await using var dispatcher = Dispatcher(logger, correlator);

        var outcome = await dispatcher.DispatchAsync(Unpack(Msg(UnhandledType, id: "answer-1")), client: null, new DidCommOptions());

        outcome.Result.Should().Be(DispatchResult.NoHandler, "claiming changes only the log level, never the outcome");
        outcome.Reply.Should().BeNull();
        logger.LevelsOf(NoHandlerTemplate).Should().Equal(LogLevel.Debug);
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Information && e.Template == NoHandlerTemplate);
        logger.Entries.Single(e => e.Template == NoHandlerTemplate).Message.Should().Contain("answer-1", "the bounded message id is still written");
    }

    [Fact]
    public async Task A_throwing_correlator_has_not_claimed_the_message()
    {
        var logger = new RecordingLogger();
        await using var dispatcher = Dispatcher(logger, new ThrowingCorrelator());

        var outcome = await dispatcher.DispatchAsync(Unpack(Msg(UnhandledType)), client: null, new DidCommOptions());

        outcome.Result.Should().Be(DispatchResult.NoHandler);
        logger.LevelsOf(NoHandlerTemplate).Should().Equal(LogLevel.Information);
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Template.StartsWith("IInboundCorrelator '", StringComparison.Ordinal),
            "the throw is still isolated and reported");
    }

    [Fact]
    public async Task Every_correlator_sees_the_message_and_one_claim_is_enough()
    {
        var logger = new RecordingLogger();
        var first = new FixedCorrelator(claims: true);
        var throwing = new ThrowingCorrelator();
        var last = new FixedCorrelator(claims: false);
        await using var dispatcher = Dispatcher(logger, first, throwing, last);

        await dispatcher.DispatchAsync(Unpack(Msg(UnhandledType)), client: null, new DidCommOptions());

        first.Calls.Should().Be(1);
        throwing.Calls.Should().Be(1, "a claim does not short-circuit the correlators after it");
        last.Calls.Should().Be(1);
        logger.LevelsOf(NoHandlerTemplate).Should().Equal(LogLevel.Debug);
    }

    [Fact]
    public async Task A_claimed_message_with_a_handler_is_still_dispatched_to_it()
    {
        var logger = new RecordingLogger();
        var registry = new ProtocolHandlerRegistry();
        var handler = new CountingHandler("https://didcomm.org/handled/1.0");
        registry.Register(handler);
        await using var dispatcher = new ProtocolDispatcher(
            registry, new InMemoryThreadStateStore(), logger, traceOptions: null, observers: null,
            correlators: new IInboundCorrelator[] { new FixedCorrelator(claims: true) });

        var outcome = await dispatcher.DispatchAsync(Unpack(Msg("https://didcomm.org/handled/1.0/answer")), client: null, new DidCommOptions());

        outcome.Result.Should().Be(DispatchResult.NoReply);
        handler.Calls.Should().Be(1);
        logger.LevelsOf(NoHandlerTemplate).Should().BeEmpty();
    }

    private static ProtocolDispatcher Dispatcher(ILogger<ProtocolDispatcher> logger, params IInboundCorrelator[] correlators)
        => new(new ProtocolHandlerRegistry(), new InMemoryThreadStateStore(), logger, traceOptions: null, observers: null,
            correlators: correlators.Length == 0 ? null : correlators);

    private static Message Msg(string type, string id = "msg-1") =>
        new() { Id = id, Type = type, From = "did:peer:alice", To = new[] { "did:peer:bob" } };

    private static UnpackResult Unpack(Message msg) => new(
        Message: msg,
        Stack: Array.Empty<DidComm.Jose.EnvelopeKind>(),
        Encrypted: false,
        Authenticated: false,
        NonRepudiation: false,
        AnonymousSender: false,
        ContentEncryption: null,
        KeyWrap: null,
        SignatureAlgorithm: null,
        SignerKid: null,
        SenderKid: null,
        RecipientKid: null,
        AllRecipientKids: Array.Empty<string>(),
        FromPrior: null);

    private sealed class FixedCorrelator(bool claims) : IInboundCorrelator
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public bool OnInbound(InboundMessageSnapshot received)
        {
            Interlocked.Increment(ref _calls);
            return claims;
        }
    }

    private sealed class ThrowingCorrelator : IInboundCorrelator
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public bool OnInbound(InboundMessageSnapshot received)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("correlator exploded");
        }
    }

    private sealed class CountingHandler(string protocolUri) : IProtocolHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string ProtocolUri => protocolUri;

        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<Message?>(null);
        }
    }

    private sealed record Entry(LogLevel Level, string Template, string Message);

    private sealed class RecordingLogger : ILogger<ProtocolDispatcher>
    {
        private readonly ConcurrentQueue<Entry> _entries = new();

        public IReadOnlyList<Entry> Entries => _entries.ToArray();

        public IReadOnlyList<LogLevel> LevelsOf(string template)
            => _entries.Where(e => e.Template == template).Select(e => e.Level).ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var template = state is IReadOnlyList<KeyValuePair<string, object?>> values
                ? values.FirstOrDefault(v => v.Key == "{OriginalFormat}").Value as string ?? string.Empty
                : string.Empty;
            _entries.Enqueue(new Entry(logLevel, template, formatter(state, exception)));
        }
    }
}
