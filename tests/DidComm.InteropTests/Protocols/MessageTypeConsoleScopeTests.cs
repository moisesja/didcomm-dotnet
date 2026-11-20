using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Diagnostics;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Xunit;

namespace DidComm.InteropTests.Protocols;

/// <summary>
/// What a stranger's message type puts on the lines of a real console host that copies activity
/// tags into its log scopes (<c>ActivityTrackingOptions.Tags</c> with <c>IncludeScopes</c>, and a
/// tracing listener on the library's activity source, as an OpenTelemetry host has). The
/// dispatcher's <c>didcomm.handle</c> span carries the message type as a tag, and the lines logged
/// during dispatch (the no-handler line among them) carry the span's tags in their scope. The tag
/// holds the type only when it is at most 256 URI characters; otherwise a placeholder. Runs alone
/// (it captures the process's console output).
/// </summary>
[Collection(QaSerialTiming.Name)]
public sealed class MessageTypeConsoleScopeTests
{
    private const string OutsideAUri = "(type with characters outside a URI)";

    private static string Marker(string field) => "TYPETAG" + field + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// On a JSON console with scopes: a clean type is on the lines inside the span (documented: the
    /// tag carries it); an over-long type and a type with a line break and an escape never are,
    /// over HTTP and over WebSocket, and their placeholders are.
    /// </summary>
    [Fact]
    public async Task On_a_console_with_activity_tags_in_scope_only_a_bounded_URI_type_reaches_a_line()
    {
        var clean = "https://didcomm.org/" + Marker("CLEAN").ToLowerInvariant() + "/1.0/ping";
        var longMarker = Marker("LONG").ToLowerInvariant();
        var overLong = "https://didcomm.org/" + longMarker + new string('a', 300) + "/1.0/ping";
        var controlHttp = Marker("CTLHTTP").ToLowerInvariant();
        var controlWs = Marker("CTLWS").ToLowerInvariant();
        var longWs = Marker("LONGWS").ToLowerInvariant();
        var overLongWs = "https://didcomm.org/" + longWs + new string('a', 300) + "/1.0/ping";
        var ids = new[] { Marker("ID1"), Marker("ID2"), Marker("ID3"), Marker("ID4"), Marker("ID5") };

        var output = await RunWithRealConsoleAsync(async mediator =>
        {
            var stranger = await mediator.NewPeerAsync();
            await PostUnknownAsync(mediator, stranger, ids[0], clean);
            await PostUnknownAsync(mediator, stranger, ids[1], overLong);
            await PostUnknownAsync(mediator, stranger, ids[2], WithControlCharacters(controlHttp));

            using var socket = await mediator.ConnectAsync();
            await SendUnknownAsync(mediator, socket, stranger, ids[3], WithControlCharacters(controlWs));
            await SendUnknownAsync(mediator, socket, stranger, ids[4], overLongWs);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        });

        // Positive controls: the console wrote the no-handler lines (ids up to 128 characters are
        // logged by design) and rendered activity scopes.
        output.Should().Contain("SpanId");
        foreach (var id in ids)
            output.Should().Contain(id);

        output.Should().Contain(clean, "a type of at most 256 URI characters is the span's tag, which this host copies into its scopes");
        output.Should().NotContain(longMarker).And.NotContain(longWs, "an over-long type is never written");
        output.Should().NotContain(controlHttp).And.NotContain(controlWs, "a type with characters outside a URI is never written");
        output.Should().Contain($"(type of {overLong.Length} characters)").And.Contain($"(type of {overLongWs.Length} characters)").And.Contain(OutsideAUri);
    }

    /// <summary>
    /// The type grammar's protocol segment is <c>[^/]+</c>, so a valid type carries a line feed and
    /// an ESC. On the single-line simple console with scopes a scope value is written raw, so a type
    /// on the tag would start a forged line and clear the operator's screen: it is a placeholder.
    /// </summary>
    [Fact]
    public async Task A_strangers_type_cannot_forge_a_line_on_a_single_line_simple_console_with_scopes()
    {
        var forged = Marker("FORGED");
        var id = Marker("IDFORGE");
        var type = "https://didcomm.org/x\n" + forged + ": info: Forged.Category[0] operator, rotate keys now\u001b[2J/1.0/ping";
        var built = Record.Exception(() => Unknown("did:example:a", "did:example:b", id, type));
        built.Should().BeNull("the type grammar admits a line feed and an ESC in the protocol name");

        var output = await RunWithRealConsoleAsync(async mediator =>
        {
            var stranger = await mediator.NewPeerAsync();
            await PostUnknownAsync(mediator, stranger, id, type);
        }, simple: true);

        var lines = output.Split('\n');
        lines.Should().Contain(l => l.Contains(id, StringComparison.Ordinal), "positive control: the simple console wrote the no-handler line");
        lines.Should().Contain(l => l.Contains(id, StringComparison.Ordinal) && l.Contains(OutsideAUri, StringComparison.Ordinal),
            "the no-handler line's scope carries the tag, as a placeholder");
        lines.Where(l => l.StartsWith(forged, StringComparison.Ordinal)).Should().BeEmpty("a stranger's text never starts a console line");
        output.Should().NotContain(forged);
        output.Should().NotContain("\u001b[2J", "a stranger's terminal escape never reaches the console");
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static string WithControlCharacters(string marker)
        => "https://didcomm.org/" + marker + "\r\nforged line\u001b[2J/1.0/ping";

    private static Message Unknown(string from, string to, string id, string type)
        => new MessageBuilder().WithId(id).WithType(type).WithFrom(from).WithTo(to).WithBody(new JsonObject()).Build();

    private static async Task PostUnknownAsync(MediatorTestServer mediator, TestPeer stranger, string id, string type)
    {
        var answer = await mediator.PostAsync(await mediator.AuthcryptAsync(stranger, Unknown(stranger.Did, mediator.Did, id, type)));
        answer.Status.Should().Be(HttpStatusCode.Accepted);
        await MediatorTestServer.WaitUntilAsync(() => mediator.Logs.Entries.Any(e => e.Contains(id, StringComparison.Ordinal)), "the HTTP no-handler line was written");
    }

    private static async Task SendUnknownAsync(MediatorTestServer mediator, WebSocket socket, TestPeer stranger, string id, string type)
    {
        await ReturnRouteTestHost.SendAsync(socket, await mediator.AuthcryptAsync(stranger, Unknown(stranger.Did, mediator.Did, id, type)));
        await MediatorTestServer.WaitUntilAsync(() => mediator.Logs.Entries.Any(e => e.Contains(id, StringComparison.Ordinal)), "the WebSocket no-handler line was written");
    }

    /// <summary>
    /// Run <paramref name="scenario"/> against a TestServer mediator whose logging is a real console
    /// provider (JSON, or the single-line simple formatter) with scopes and activity tracking with
    /// tags, under a tracing listener on the library's source and ASP.NET Core's; dispose the host
    /// (flushing the console queue) and return everything the console wrote.
    /// </summary>
    private static async Task<string> RunWithRealConsoleAsync(Func<MediatorTestServer, Task> scenario, bool simple = false)
    {
        var writer = new StringWriter();
        var synced = TextWriter.Synchronized(writer);
        var original = Console.Out;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == DidCommDiagnostics.ActivitySourceName || s.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        Console.SetOut(synced);
        MediatorTestServer? mediator = null;
        try
        {
            mediator = await MediatorTestServer.StartAsync(o => o.ConfigureBuilder = b =>
            {
                if (simple)
                {
                    b.Logging.AddSimpleConsole(c =>
                    {
                        c.IncludeScopes = true;
                        c.SingleLine = true;
                        c.ColorBehavior = LoggerColorBehavior.Disabled;
                    });
                }
                else
                {
                    b.Logging.AddJsonConsole(j =>
                    {
                        j.IncludeScopes = true;
                        j.JsonWriterOptions = new JsonWriterOptions { Indented = false };
                    });
                }
                b.Logging.Configure(lo => lo.ActivityTrackingOptions =
                    ActivityTrackingOptions.SpanId | ActivityTrackingOptions.TraceId | ActivityTrackingOptions.ParentId
                    | ActivityTrackingOptions.Tags);
            });
            await scenario(mediator);
        }
        finally
        {
            if (mediator is not null)
                await mediator.DisposeAsync();
            Console.SetOut(original);
        }
        return writer.ToString();
    }
}
