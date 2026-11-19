using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.InteropTests.Adv98.D2;

/// <summary>
/// #98 D2 (E-8): the attacker-controlled ids, bodies and addresses listed on the test never reach a
/// line written by a REAL console provider, configured the way an observability-minded host configures one: JSON
/// console with <c>IncludeScopes</c>, activity tracking of span and trace ids, tags and baggage, and
/// a tracing listener on the library's activity source and ASP.NET Core's (what an OpenTelemetry
/// host has). The capturing provider most tests use does not render scopes; this test uses the real
/// formatter, so scope and activity data are part of what is checked.
/// </summary>
[Collection(QaSerialTiming.Name)]
public sealed class D2RealConsoleEchoTests
{
    private readonly ITestOutputHelper _output;

    public D2RealConsoleEchoTests(ITestOutputHelper output) => _output = output;

    private static string Marker(string field) => "D2MK" + field + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Attacker-controlled values on the receive path: thid, pthid, body values, attachment
    /// ids, an over-long id, an unknown <c>next</c> (a DID and a non-DID), a <c>recipient_did</c>
    /// nobody holds (from an enrolled peer and from a stranger), a replayed message, a plaintext
    /// with an invalid <c>thid</c> over HTTP and WebSocket, and an authcrypt whose sender key names
    /// a DID method nobody supports. None of them reaches a console line or a response body.
    /// </summary>
    [Fact]
    public async Task Attacker_ids_bodies_and_addresses_never_reach_a_real_console_line_or_a_response_body()
    {
        var thid = Marker("THID");
        var pthid = Marker("PTHID");
        var body = Marker("BODY");
        var attachment = Marker("ATTID");
        var longId = Marker("LONGID") + new string('a', 200);
        var nextDid = "did:example:" + Marker("NEXTDID");
        var nextUrl = "https://" + Marker("NEXTURL").ToLowerInvariant() + ".example/inbox";
        var recipient = "did:peer:2." + Marker("RECIP");
        var badThid = Marker("BADTHID");
        var replayId = Marker("REPLAY") + new string('r', 150);
        var unknownMethod = Marker("METHOD").ToLowerInvariant();
        var bodies = new List<string>();

        var output = await RunWithRealConsoleAsync(async mediator =>
        {
            var stranger = await mediator.NewPeerAsync();
            var bob = await mediator.NewPeerAsync();
            await mediator.EnrolAsync(bob);
            var alice = await mediator.NewSenderAsync();

            // 1. A basic message carrying thid, pthid, body and attachment markers.
            var note = new MessageBuilder()
                .WithId(longId)
                .WithType("https://didcomm.org/basicmessage/2.0/message")
                .WithFrom(stranger.Did)
                .WithTo(mediator.Did)
                .WithThid(thid)
                .WithPthid(pthid)
                .WithBody(new JsonObject { ["content"] = body })
                .WithAttachment(new Attachment { Id = attachment, Data = new AttachmentData { Json = new JsonObject { ["v"] = body } } })
                .Build();
            bodies.Add((await mediator.PostAsync(await mediator.AuthcryptAsync(stranger, note))).Body);

            // 2. Forwards to an unknown DID and to a non-DID next (anoncrypt, the stranger's path).
            var inner = await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did);
            bodies.Add((await mediator.PostAsync((await alice.ForwardAsync(mediator.Did, nextDid, inner)).Packed)).Body);
            bodies.Add((await mediator.PostAsync((await alice.ForwardAsync(mediator.Did, nextUrl, inner)).Packed)).Body);

            // 3. Pickup naming a DID nobody holds, from an enrolled peer and from a stranger.
            bodies.Add((await mediator.PostAsync(await mediator.AuthcryptAsync(bob, mediator.Prepare(MessagePickup.CreateStatusRequest(bob.Did, mediator.Did, recipient))))).Body);
            bodies.Add((await mediator.PostAsync(await mediator.AuthcryptAsync(stranger, mediator.Prepare(MessagePickup.CreateDeliveryRequest(stranger.Did, mediator.Did, 10, recipient))))).Body);

            // 4. A replayed message (the same envelope twice).
            var replayed = await mediator.AuthcryptAsync(stranger, Note(stranger.Did, mediator.Did, replayId, "https://didcomm.org/basicmessage/2.0/message"));
            bodies.Add((await mediator.PostAsync(replayed)).Body);
            bodies.Add((await mediator.PostAsync(replayed)).Body);

            // 5. A plaintext with an invalid thid, over HTTP and WebSocket.
            var plain = "{\"id\":\"p1\",\"type\":\"https://didcomm.org/basicmessage/2.0/message\",\"thid\":\"" + badThid + "!x\",\"body\":{}}";
            bodies.Add(await PostRawAsync(mediator, plain, MediaTypes.Plaintext));
            using (var socket = await mediator.ConnectAsync())
            {
                var before = mediator.Logs.Entries.Count;
                await ReturnRouteTestHost.SendAsync(socket, plain);
                await MediatorTestServer.WaitUntilAsync(() => mediator.Logs.Entries.Skip(before).Any(e => e.Contains("discarding malformed envelope", StringComparison.Ordinal)), "the socket logged the discard");
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
            }

            // 6. An authcrypt whose skid names a DID method nobody supports.
            var authcrypt = await mediator.AuthcryptAsync(stranger, Note(stranger.Did, mediator.Did, "sk1", "https://didcomm.org/basicmessage/2.0/message"));
            bodies.Add(await PostRawAsync(mediator, RewriteSkid(authcrypt, $"did:{unknownMethod}:abc#key-1"), MediaTypes.Encrypted));
        });

        var markers = new[] { thid, pthid, body, attachment, longId, nextDid, nextUrl, recipient, badThid, replayId, unknownMethod };
        _output.WriteLine("console bytes: " + output.Length);
        foreach (var leak in Leaks(output, markers))
            _output.WriteLine("LEAK: " + leak);
        output.Should().Contain("SpanId", "the console rendered activity scopes");
        Leaks(string.Join("\n", bodies), markers).Should().BeEmpty("no response body echoes a wire value");
        Leaks(output, markers).Should().BeEmpty("none of these attacker-controlled values reaches a real console line");
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static Message Note(string from, string to, string id, string type)
        => new MessageBuilder().WithId(id).WithType(type).WithFrom(from).WithTo(to).WithBody(new JsonObject()).Build();

    private static IReadOnlyList<string> Leaks(string text, params string[] markers)
        => markers.Where(m => text.Contains(m, StringComparison.Ordinal)).ToList();

    private static async Task<string> PostRawAsync(MediatorTestServer mediator, string payload, string contentType)
    {
        using var client = mediator.Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, mediator.Options.Pattern)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload)),
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        using var response = await client.SendAsync(request);
        return ((int)response.StatusCode) + " " + await response.Content.ReadAsStringAsync();
    }

    /// <summary>Replace the protected header's skid (and apu) of a general-JSON JWE.</summary>
    private static string RewriteSkid(string jwe, string skid)
    {
        var root = JsonNode.Parse(jwe)!.AsObject();
        var header = JsonNode.Parse(Base64UrlDecode(root["protected"]!.GetValue<string>()))!.AsObject();
        header["skid"] = skid;
        header["apu"] = Base64UrlEncode(Encoding.UTF8.GetBytes(skid));
        root["protected"] = Base64UrlEncode(Encoding.UTF8.GetBytes(header.ToJsonString()));
        return root.ToJsonString();
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight(t.Length + ((4 - (t.Length % 4)) % 4), '='));
    }

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Run <paramref name="scenario"/> against a TestServer mediator whose logging is a real JSON
    /// console provider with scopes and full activity tracking, under a tracing listener on the
    /// library's source and ASP.NET Core's; dispose the host (flushing the console queue) and
    /// return everything the console wrote.
    /// </summary>
    private static async Task<string> RunWithRealConsoleAsync(Func<MediatorTestServer, Task> scenario)
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
        MediatorTestServer? mediator = null;
        try
        {
            mediator = await MediatorTestServer.StartAsync(o => o.ConfigureBuilder = b =>
            {
                b.Logging.AddJsonConsole(j =>
                {
                    j.IncludeScopes = true;
                    j.JsonWriterOptions = new JsonWriterOptions { Indented = false };
                });
                b.Logging.Configure(lo => lo.ActivityTrackingOptions =
                    ActivityTrackingOptions.SpanId | ActivityTrackingOptions.TraceId | ActivityTrackingOptions.ParentId
                    | ActivityTrackingOptions.Tags | ActivityTrackingOptions.Baggage);
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
