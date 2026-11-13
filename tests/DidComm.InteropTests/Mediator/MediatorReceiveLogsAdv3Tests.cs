using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// ADV-3 (#95): what a stranger can write into the mediator's logs through <c>MapDidCommMediator</c>
/// with no key at all — a plaintext message posted to the mediator's HTTP endpoint is unpacked,
/// admitted and dispatched like any other. The #95 CHANGELOG says the mediator logs "message ids
/// (bounded), counts, setting values and exception type names only … and every line a stranger can
/// trigger is rate-limited per reason". Each line is also rendered through the real
/// <c>Microsoft.Extensions.Logging.Console</c> simple formatter, i.e. what a console host prints.
/// </summary>
public sealed class MediatorReceiveLogsAdv3Tests
{
    private const string Marker = "ADV3-FORGED";
    private const string Escape = "\u001b";

    // A valid Message Type URI (FR-PROTO-01): the protocol-name segment may hold anything but '/',
    // so a CR LF, a forged log line and a terminal escape sequence fit inside it.
    private static readonly string HostileType =
        "https://didcomm.org/p\r\n2026-11-13 12:00:00 crit: " + Marker + " admin token rotated\r\n" + Escape + "[2J" + Escape + "[1;31m/1.0/m";

    // ADV-3 DEFECT 3 (P3; on main too; fixed): ProtocolDispatcher wrote "No protocol handler registered
    // for inbound message type '{Type}'" at Information with the type exactly as it arrived — any
    // length, CR, LF and escape characters included. Through MapDidCommMediator any stranger reaches it
    // with a plaintext POST. The line now carries the bounded message id only, never the type.
    [Fact]
    public async Task ADV3_D3_A_strangers_message_type_never_reaches_a_log_line()
    {
        await using var mediator = await MediatorTestServer.StartAsync();

        (await PostPlaintextAsync(mediator, Plaintext("adv3-type-1", HostileType))).Should().Be(HttpStatusCode.Accepted);

        mediator.Logs.CategorizedRecords.Should().NotContain(r => r.Text.Contains(Marker, StringComparison.Ordinal));
    }

    // ADV-3 DEFECT 3 (same class; fixed): the dispatcher's pure-ACK lines and the admission's
    // malformed return_route line carried the message id unbounded (ids are unreserved characters, so
    // no line break, but of any length the receive limit allows), and the pure-ACK Warning was written
    // for every such message. Ids are now written through LogValues.Id; the pure-ACK drop line is a
    // Warning the first time and every 1000th, Debug otherwise.
    [Fact]
    public async Task ADV3_D3_Every_line_a_stranger_triggers_carries_a_bounded_id_and_is_rate_limited()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var longId = new string('a', 200_000);

        for (var i = 0; i < 3; i++)
            await PostPlaintextAsync(mediator, PureAckAskingForAck(longId + i));
        await PostPlaintextAsync(mediator, Plaintext(longId + "rr", "https://didcomm.org/basicmessage/2.0/message", returnRoute: 42));

        mediator.Logs.CategorizedRecords.Should().NotContain(r => r.Text.Length > 4_096, "an id is written bounded (LogValues.Id keeps 128 characters)");
        mediator.Logs.CategorizedRecords.Count(r => r.Level >= LogLevel.Warning && r.Category == "DidComm.Protocols.ProtocolDispatcher")
            .Should().BeLessThanOrEqualTo(1, "a line any stranger can trigger is a Warning the first time and every 1000th, Debug otherwise");
    }

    [Theory]
    [InlineData(MediaTypes.Encrypted)]
    [InlineData(MediaTypes.Signed)]
    [InlineData(MediaTypes.Plaintext)]
    public async Task AD5_holds_a_rejected_envelope_quoting_a_forged_line_prints_type_names_only_on_a_console(string mediaType)
    {
        // Each body is refused by the unpack path, and each refusal's exception text would quote the
        // hostile value (a kid, an id): the endpoint's line must carry type names only.
        await using var mediator = await MediatorTestServer.StartAsync();
        var hostile = "did:example:" + Marker + "\r\n" + Escape + "[2J#k";
        var body = mediaType switch
        {
            MediaTypes.Encrypted => new JsonObject
            {
                ["protected"] = "eyJlbmMiOiJBMjU2Q0JDLUhTNTEyIn0",
                ["recipients"] = new JsonArray(new JsonObject { ["header"] = new JsonObject { ["kid"] = hostile }, ["encrypted_key"] = "AA" }),
                ["iv"] = "AA",
                ["ciphertext"] = "AA",
                ["tag"] = "AA",
            }.ToJsonString(),
            MediaTypes.Signed => new JsonObject
            {
                ["payload"] = "e30",
                ["signatures"] = new JsonArray(new JsonObject { ["protected"] = "eyJhbGciOiJFZERTQSJ9", ["header"] = new JsonObject { ["kid"] = hostile }, ["signature"] = "AA" }),
            }.ToJsonString(),
            _ => new JsonObject { ["id"] = "x y " + Marker + "\r\n", ["typ"] = MediaTypes.Plaintext, ["type"] = "https://didcomm.org/basicmessage/2.0/message", ["body"] = new JsonObject() }.ToJsonString(),
        };

        using var client = mediator.Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, mediator.Options.Pattern) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        foreach (var record in mediator.Logs.CategorizedRecords)
            RenderSimpleConsole(record.Level, record.Category, record.Text).Should().NotContain(Marker).And.NotContain(Escape);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────

    private static string Plaintext(string id, string type, object? returnRoute = null)
    {
        var json = new JsonObject
        {
            ["id"] = id,
            ["typ"] = MediaTypes.Plaintext,
            ["type"] = type,
            ["body"] = new JsonObject(),
        };
        if (returnRoute is int number)
            json["return_route"] = number;
        return json.ToJsonString();
    }

    private static string PureAckAskingForAck(string id) => new JsonObject
    {
        ["id"] = id,
        ["typ"] = MediaTypes.Plaintext,
        ["type"] = "https://didcomm.org/basicmessage/2.0/message",
        ["ack"] = new JsonArray("some-earlier-message"),
        ["please_ack"] = new JsonArray(""),
        ["body"] = new JsonObject(),
    }.ToJsonString();

    private static async Task<HttpStatusCode> PostPlaintextAsync(MediatorTestServer mediator, string json)
    {
        using var client = mediator.Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, mediator.Options.Pattern)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json)),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.Plaintext);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>What <c>AddSimpleConsole()</c> (colors off) prints for one line.</summary>
    private static string RenderSimpleConsole(LogLevel level, string category, string text)
    {
        using var services = new ServiceCollection()
            .AddLogging(b => b.AddSimpleConsole(o => o.ColorBehavior = LoggerColorBehavior.Disabled))
            .BuildServiceProvider();
        var formatter = services.GetServices<ConsoleFormatter>().Single(f => f.Name == ConsoleFormatterNames.Simple);
        using var writer = new StringWriter();
        formatter.Write(new LogEntry<string>(level, category, new EventId(0), text, null, static (state, _) => state), null, writer);
        return writer.ToString();
    }
}
