using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.ProblemReport;
using DidComm.Threading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

// L-014.
using ProblemReportApi = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.Tests.Protocols.ProblemReport;

/// <summary>
/// The problem-report handler's log lines (FR-PROTO-10). Any sender can cause each of them once per
/// report, and the report's <c>pthid</c> and <c>code</c> are the sender's text: so the <c>pthid</c>
/// is written bounded, the <c>code</c> only when it is short lower kebab-case, and each line is
/// rate-limited (the first and every 1000th at its own level, the rest at Debug).
/// </summary>
public sealed class ProblemReportLogLineTests
{
    private const string Marker = "ZQXMARK";

    private static ProtocolContext Ctx(Message m)
    {
        var unpacked = new UnpackResult(
            m, Array.Empty<DidComm.Jose.EnvelopeKind>(),
            false, false, false, false, null, null, null, null, null, null,
            Array.Empty<string>(), null);
        var threads = new InMemoryThreadStateStore();
        return new ProtocolContext(unpacked, threads.GetOrCreate(m.Thid ?? m.Id), Client: null, new DidCommOptions(), threads);
    }

    private static (ProblemReportHandler Handler, ListLogger Logger) Build(int threshold = 5)
    {
        var logger = new ListLogger();
        return (new ProblemReportHandler(Options.Create(new ProblemReportOptions { CascadeThreshold = threshold }), new CascadeBudgetStore(), logger), logger);
    }

    // A report as a stranger can write it: any code text, any pthid (unpack bounds the pthid at 512).
    private static Message Report(string pthid, string code, string? from = "did:peer:alice")
    {
        var report = ProblemReportApi.Create(from: "did:peer:alice", to: "did:peer:bob", code: "e.p.req.placeholder", pthid: "p");
        report.Body!["code"] = code;
        report.Pthid = pthid;
        report.From = from;
        return report;
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    [InlineData(512, false)]
    public async Task The_pthid_is_written_up_to_128_characters_and_replaced_beyond_on_every_line(int length, bool written)
    {
        var (handler, logger) = Build(threshold: 0);
        var pthid = Marker + new string('p', length - Marker.Length);

        await handler.HandleAsync(Report(pthid, "not a code"), Ctx(Report(pthid, "not a code")), CancellationToken.None);
        var trip = await handler.HandleAsync(Report(pthid, "e.p.xfer.cant-use-endpoint"), Ctx(Report(pthid, "e.p.xfer.cant-use-endpoint")), CancellationToken.None);

        trip.Should().NotBeNull("threshold 0: the first error report trips the guard");
        logger.Entries.Should().HaveCount(3, "the malformed-code line, the counted-report line and the trip line");
        var expected = written ? pthid : $"(id of {length} characters)";
        logger.Entries.Should().OnlyContain(e => Equals(e.State["Pthid"], expected));
        if (!written)
            logger.Entries.Should().OnlyContain(e => !e.Text.Contains(Marker, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("e.p.xfer.cant-use-endpoint", "e.p.xfer.cant-use-endpoint")]
    [InlineData("e.p.xfer." + Marker + "\u001b[31mRED", "(code with characters outside lower kebab-case)")]
    [InlineData("e.p.xfer." + Marker + "\r\nFAKE LOG LINE", "(code with characters outside lower kebab-case)")]
    [InlineData("e.p.xfer.zqxmark-ab", "e.p.xfer.zqxmark-ab")]
    public async Task The_code_is_written_only_when_it_is_short_lower_kebab_case(string code, string expected)
    {
        var (handler, logger) = Build();
        var report = Report("thread-1", code);

        await handler.HandleAsync(report, Ctx(report), CancellationToken.None);

        logger.Entries.Should().ContainSingle().Which.State["Code"].Should().Be(expected);
        if (expected != code)
            logger.Entries.Single().Text.Should().NotContain(Marker).And.NotContain("\u001b").And.NotContain("\n");
    }

    [Fact]
    public async Task A_long_code_is_replaced_by_its_length()
    {
        var (handler, logger) = Build();
        var code = "e.p.xfer." + new string('z', 100_000);
        var report = Report("thread-1", code);

        await handler.HandleAsync(report, Ctx(report), CancellationToken.None);

        logger.Entries.Should().ContainSingle().Which.State["Code"].Should().Be($"(code of {code.Length} characters)");
    }

    [Fact]
    public async Task Each_line_is_written_at_its_level_for_the_first_and_every_1000th_and_at_Debug_otherwise()
    {
        // Threshold 0 and a fresh pthid each time: every error report is counted and trips the guard.
        var (handler, logger) = Build(threshold: 0);
        for (var i = 0; i < 1001; i++)
        {
            var malformed = Report("m-" + i, "not a code");
            await handler.HandleAsync(malformed, Ctx(malformed), CancellationToken.None);
            var error = Report("e-" + i, "e.p.xfer.cant-use-endpoint");
            (await handler.HandleAsync(error, Ctx(error), CancellationToken.None)).Should().NotBeNull();
        }

        LogLevel[] Levels(string template) => logger.Entries.Where(e => e.Template.StartsWith(template, StringComparison.Ordinal)).Select(e => e.Level).ToArray();
        static LogLevel[] Expected(LogLevel loud) => Enumerable.Range(1, 1001).Select(n => n == 1 || n == 1000 ? loud : LogLevel.Debug).ToArray();
        Levels("Received a problem-report with an absent or malformed code").Should().Equal(Expected(LogLevel.Information));
        Levels("Inbound problem-report on thread").Should().Equal(Expected(LogLevel.Information));
        Levels("Cascade guard tripped").Should().Equal(Expected(LogLevel.Warning));
    }

    internal sealed record Entry(LogLevel Level, string Text, string Template, IReadOnlyDictionary<string, object?> State);

    internal sealed class ListLogger : ILogger<ProblemReportHandler>
    {
        private readonly ConcurrentQueue<Entry> _entries = new();

        public IReadOnlyList<Entry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object?>> ?? Array.Empty<KeyValuePair<string, object?>>();
            var map = values.ToDictionary(kv => kv.Key, kv => kv.Value);
            var template = map.TryGetValue("{OriginalFormat}", out var t) ? t?.ToString() ?? string.Empty : string.Empty;
            _entries.Enqueue(new Entry(logLevel, formatter(state, exception), template, map));
        }
    }
}
