using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using DidComm.InteropTests.Mediator;
using DidComm.Messages;
using DidComm.Protocols.ProblemReport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Xunit;
using Xunit.Abstractions;

// L-014.
using ProblemReportApi = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// The problem-report handler's log lines, written by a REAL console provider (JSON with scopes and
/// activity tracking, and the plain-text formatter, which writes a message as it is): a report's
/// <c>pthid</c> (accepted up to 512 characters) and <c>code</c> (any text) are the sender's, so
/// neither reaches a console line as sent. A <c>pthid</c> over 128 characters is written by its
/// length, a <c>code</c> outside short lower kebab-case by a placeholder, so an escape sequence or a
/// line break in either never reaches the console. Runs in the serial collection: it redirects
/// <see cref="Console.Out"/>.
/// </summary>
[Collection(QaSerialTiming.Name)]
public sealed class ProblemReportRealConsoleTests
{
    private const char Esc = (char)27;
    private const string PthidMarker = "zqxconsolepthid";
    private const string CodeMarker = "zqxconsolecode";
    private const string InjectedLine = "FAKE LOG LINE";

    private readonly ITestOutputHelper _output;

    public ProblemReportRealConsoleTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<string> Formatters => new() { "json", "simple" };

    [Theory]
    [MemberData(nameof(Formatters))]
    public async Task A_problem_report_s_pthid_and_code_never_reach_a_real_console_line_as_sent(string formatter)
    {
        var statuses = new List<HttpStatusCode>();

        var output = await RunWithRealConsoleAsync(formatter, async host =>
        {
            foreach (var length in new[] { 129, 512 })
            {
                var pthid = PthidMarker + new string('p', length - PthidMarker.Length);
                // A malformed code: the line that writes the pthid only.
                statuses.Add((await host.PostAsync(Report(host, pthid, "not a code " + CodeMarker), contentType: MediaTypes.Plaintext)).Status);
                // An error code carrying an escape sequence and a line break: counted, and at
                // threshold 0 it trips the cascade guard, so all three lines are written.
                var code = "e.p.xfer." + CodeMarker + Esc + "[31mRED\r\n" + InjectedLine;
                statuses.Add((await host.PostAsync(Report(host, pthid, code), contentType: MediaTypes.Plaintext)).Status);
            }
        });

        _output.WriteLine($"console bytes: {output.Length}");
        statuses.Should().AllSatisfy(s => s.Should().Be(HttpStatusCode.Accepted));
        output.Should().Contain("(id of 129 characters)").And.Contain("(id of 512 characters)", "the problem-report lines were written to the console");
        output.Should().Contain("(code with characters outside lower kebab-case)");
        output.Should().Contain("Cascade guard tripped");
        var lines = output.Split('\n');
        lines.Where(l => l.Contains(PthidMarker, StringComparison.Ordinal) || l.Contains(CodeMarker, StringComparison.Ordinal) || l.Contains(InjectedLine, StringComparison.Ordinal))
            .Should().BeEmpty("neither the pthid nor the code reaches a console line as sent");
        lines.Where(l => l.Contains(Esc)).Should().BeEmpty("no escape sequence reaches the console");
    }

    /// <summary>A plaintext problem-report from Alice to the server, written by hand with any code text.</summary>
    private static string Report(ReturnRouteTestHost host, string pthid, string code)
    {
        var report = ProblemReportApi.Create(from: host.Alice.Did, to: host.ServerDid, code: "e.p.req.placeholder", pthid: "p");
        report.Body!["code"] = code;
        report.Pthid = pthid;
        return ReturnRouteTestHost.UnvalidatedJson(report);
    }

    /// <summary>
    /// Run <paramref name="scenario"/> against a host whose logging includes a real console provider
    /// (<paramref name="formatter"/>: JSON with scopes, or plain text with colours off so the only
    /// escape a line could hold is one that came from the wire), with activity tracking and a
    /// tracing listener on the library's source and ASP.NET Core's; dispose the host (flushing the
    /// console queue) and return everything the console wrote.
    /// </summary>
    private static async Task<string> RunWithRealConsoleAsync(string formatter, Func<ReturnRouteTestHost, Task> scenario)
    {
        var writer = new StringWriter();
        var synced = TextWriter.Synchronized(writer);
        var original = Console.Out;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == DidComm.Diagnostics.DidCommDiagnostics.ActivitySourceName || s.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        Console.SetOut(synced);
        ReturnRouteTestHost? host = null;
        try
        {
            host = await ReturnRouteTestHost.StartAsync(o =>
            {
                o.ConfigureServices = s => s.Configure<ProblemReportOptions>(p => p.CascadeThreshold = 0);
                o.ConfigureBuilder = b =>
                {
                    if (formatter == "json")
                    {
                        b.Logging.AddJsonConsole(j =>
                        {
                            j.IncludeScopes = true;
                            j.JsonWriterOptions = new JsonWriterOptions { Indented = false };
                        });
                    }
                    else
                    {
                        b.Logging.AddSimpleConsole(c =>
                        {
                            c.IncludeScopes = true;
                            c.ColorBehavior = LoggerColorBehavior.Disabled;
                        });
                    }
                    b.Logging.Configure(lo => lo.ActivityTrackingOptions =
                        ActivityTrackingOptions.SpanId | ActivityTrackingOptions.TraceId | ActivityTrackingOptions.ParentId
                        | ActivityTrackingOptions.Tags | ActivityTrackingOptions.Baggage);
                };
            });
            await scenario(host);
        }
        finally
        {
            if (host is not null)
                await host.DisposeAsync();
            Console.SetOut(original);
        }
        return writer.ToString();
    }
}
