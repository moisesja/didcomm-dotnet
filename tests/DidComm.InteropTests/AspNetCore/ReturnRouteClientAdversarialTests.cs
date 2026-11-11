using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.InteropTests.Transports;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #92 G7 — hostile responses to a return-route send over HTTP (FR-RR-07, JC-2/JC-6): a returned body
/// that is not authcrypt from the addressed peer to the sending DID is discarded and logged by the
/// request's id and the refusal name; garbage under a DIDComm media type is a
/// <see cref="TransportException"/> with status 200 (accepted, unreadable — never silent loss); a
/// 100 MB response is cut at the cap. The responses come from a stub primary handler, so the server
/// can say anything. Every case ends with the log scan: no DID, key id, thread value or body bytes.
/// </summary>
public sealed class ReturnRouteClientAdversarialTests
{
    private static readonly string[] IdentityFailureCases =
    {
        "anoncrypt from the server",
        "signed-only by the server",
        "plaintext",
        "authcrypt by Mallory",
        "server to Carol",
        "forward type",
    };

    private static readonly string[] GarbageCases =
    {
        "open brace",
        "invalid UTF-8",
        "JWE without ciphertext",
        "JWE with a flipped tag",
    };

    public static TheoryData<string> IdentityFailures() => new(IdentityFailureCases);

    [Theory]
    [MemberData(nameof(IdentityFailures))]
    public async Task Returned_is_null_and_logged_for_bodies_failing_the_identity_rule(string @case)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        var (body, contentType, refusal) = await HostileReplyAsync(host, @case);
        await using var client = ReturnRouteClientHarness.Create(host, StubReturning(body, contentType), holders: new[] { host.Alice, host.Carol });
        var request = host.RecordingMessage(host.Alice).Build();

        var result = await client.Client.SendAsync(request, client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        result.Transport.Accepted.Should().BeTrue();
        result.Transport.ResponsePayload.Should().NotBeNull("the transport read it; only Returned is filtered");
        result.Returned.Should().BeNull(@case);
        host.Logs.Entries.Where(l => l.Contains("was discarded", StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Should().Contain(request.Id).And.Contain(refusal);
        host.LeakingLogLines().Should().BeEmpty();
        host.Logs.Entries.Should().NotContain(l => l.Contains(Fragment(body), StringComparison.Ordinal), "no returned bytes are logged");
    }

    public static TheoryData<string> GarbageBodies() => new(GarbageCases);

    [Theory]
    [MemberData(nameof(GarbageBodies))]
    public async Task Garbage_under_a_DIDComm_type_is_TransportException_status_200(string @case)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        var body = await GarbageAsync(host, @case);
        await using var client = ReturnRouteClientHarness.Create(host, StubReturning(body, MediaTypes.Encrypted));

        var act = () => client.Client.SendAsync(host.RecordingMessage(host.Alice).Build(), client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        var thrown = (await act.Should().ThrowAsync<TransportException>(@case)).Which;
        thrown.HttpStatusCode.Should().Be(200, "the request was accepted; the returned message was unreadable");
        thrown.InnerException.Should().BeAssignableTo<DidCommException>("the FR-API-07 type is kept as the inner exception");
        thrown.Message.Should().NotContain("did:");
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_hostile_100_MB_response_is_cut_at_the_cap_and_SendAsync_throws_TransportException(bool declareLength)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        var contents = new List<GeneratedContent>();
        Func<HttpMessageHandler> stub = () => new StubHandler(_ =>
        {
            var content = GeneratedContent.Generated(100L * 1024 * 1024, declareLength);
            lock (contents)
                contents.Add(content);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        await using var client = ReturnRouteClientHarness.Create(host, stub);
        var cap = new DidCommOptions().MaxReceiveBytes;

        var act = () => client.Client.SendAsync(host.RecordingMessage(host.Alice).Build(), client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        var thrown = (await act.Should().ThrowAsync<TransportException>()).Which;
        thrown.HttpStatusCode.Should().Be(200);
        thrown.Message.Should().Contain("MaxReceiveBytes").And.NotContain("did:");

        // The transport alone reports the delivery as accepted, with the body discarded.
        var transport = client.Services.GetServices<IDidCommTransport>().Single();
        var alone = await transport.SendAsync(new TransportRequest(client.Endpoint, new byte[] { 0x7b, 0x7d }, MediaTypes.Encrypted) { MaxResponseBytes = cap }, default);
        alone.Accepted.Should().BeTrue();
        alone.ResponseDiscarded.Should().BeTrue();

        lock (contents)
        {
            contents.Should().HaveCount(2);
            contents.Should().OnlyContain(c => c.BytesPulled <= cap + 16 * 1024, "nothing past the cap plus one chunk is read");
        }
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Log_scan_finds_no_DID_kid_thread_or_body()
    {
        // Every HTTP case above against one host and one log, with a thread-mode request on each so the
        // thread value is in play too.
        await using var host = await ReturnRouteTestHost.StartAsync();
        var thread = "thr-" + Guid.NewGuid().ToString("N");
        var fragments = new List<string>();

        foreach (var @case in IdentityFailureCases)
        {
            var (body, contentType, _) = await HostileReplyAsync(host, @case);
            fragments.Add(Fragment(body));
            await using var client = ReturnRouteClientHarness.Create(host, StubReturning(body, contentType), holders: new[] { host.Alice, host.Carol });
            var result = await client.Client.SendAsync(host.RecordingMessage(host.Alice).Build(),
                client.ToServer() with { ReturnRoute = ReturnRouteMode.Thread, ReturnRouteThread = thread });
            result.Returned.Should().BeNull(@case);
        }
        foreach (var @case in GarbageCases)
        {
            var body = await GarbageAsync(host, @case);
            if (@case != "invalid UTF-8")
                fragments.Add(Fragment(body));
            await using var client = ReturnRouteClientHarness.Create(host, StubReturning(body, MediaTypes.Encrypted));
            var act = () => client.Client.SendAsync(host.RecordingMessage(host.Alice).Build(),
                client.ToServer() with { ReturnRoute = ReturnRouteMode.Thread, ReturnRouteThread = thread });
            await act.Should().ThrowAsync<TransportException>(@case);
        }

        host.Logs.Entries.Should().NotBeEmpty("the scan must have something to scan");
        host.LeakingLogLines(thread).Should().BeEmpty("no DID, key id or thread value in any line");
        foreach (var fragment in fragments.Where(f => f.Length >= 8))
            host.Logs.Entries.Should().NotContain(l => l.Contains(fragment, StringComparison.Ordinal), "no returned bytes in any line");
    }

    // ---- hostile bodies ----

    private static async Task<(byte[] Body, string ContentType, string Refusal)> HostileReplyAsync(ReturnRouteTestHost host, string @case)
    {
        var server = host.App.Services.GetRequiredService<DidCommClient>();
        Message Reply(string from, string to, string type = RecordingHandler.ProtocolUri + "/reply") => new MessageBuilder()
            .WithType(type)
            .WithFrom(from)
            .WithTo(to)
            .WithBody(new JsonObject())
            .Build();

        switch (@case)
        {
            case "anoncrypt from the server":
                return (Utf8((await server.PackEncryptedAsync(Reply(host.ServerDid, host.Alice.Did), new PackEncryptedOptions(Recipients: new[] { host.Alice.Did }))).Message),
                    MediaTypes.Encrypted, nameof(ReturnedMessageRefusal.NotAuthcrypt));
            case "signed-only by the server":
                return (Utf8(await server.PackSignedAsync(Reply(host.ServerDid, host.Alice.Did), host.ServerDid)), MediaTypes.Signed, nameof(ReturnedMessageRefusal.NotAuthcrypt));
            case "plaintext":
                return (Utf8(await server.PackPlaintextAsync(Reply(host.ServerDid, host.Alice.Did))), MediaTypes.Plaintext, nameof(ReturnedMessageRefusal.NotAuthcrypt));
            case "authcrypt by Mallory":
                return (Utf8((await host.Mallory.Client.PackEncryptedAsync(Reply(host.Mallory.Did, host.Alice.Did),
                    new PackEncryptedOptions(Recipients: new[] { host.Alice.Did }, From: host.Mallory.Did))).Message), MediaTypes.Encrypted, nameof(ReturnedMessageRefusal.PeerMismatch));
            case "server to Carol":
                return (Utf8((await server.PackEncryptedAsync(Reply(host.ServerDid, host.Carol.Did),
                    new PackEncryptedOptions(Recipients: new[] { host.Carol.Did }, From: host.ServerDid))).Message), MediaTypes.Encrypted, nameof(ReturnedMessageRefusal.LocalMismatch));
            case "forward type":
                return (Utf8((await server.PackEncryptedAsync(Reply(host.ServerDid, host.Alice.Did, "https://didcomm.org/routing/2.0/forward"),
                    new PackEncryptedOptions(Recipients: new[] { host.Alice.Did }, From: host.ServerDid))).Message), MediaTypes.Encrypted, nameof(ReturnedMessageRefusal.Forward));
            default:
                throw new ArgumentOutOfRangeException(nameof(@case));
        }
    }

    private static async Task<byte[]> GarbageAsync(ReturnRouteTestHost host, string @case)
    {
        switch (@case)
        {
            case "open brace":
                return Utf8("{");
            case "invalid UTF-8":
                return new byte[] { 0x7b, 0xff, 0xfe, 0xc3, 0x28, 0x7d };
            case "JWE without ciphertext":
                return Utf8("{\"protected\":\"eyJhbGciOiJFQ0RILUVTK0EyNTZLVyJ9\",\"recipients\":[{\"header\":{\"kid\":\"x\"},\"encrypted_key\":\"AAAA\"}],\"iv\":\"AAAA\",\"tag\":\"AAAA\"}");
            case "JWE with a flipped tag":
                var server = host.App.Services.GetRequiredService<DidCommClient>();
                var packed = (await server.PackEncryptedAsync(
                    new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/reply").WithFrom(host.ServerDid).WithTo(host.Alice.Did).Build(),
                    new PackEncryptedOptions(Recipients: new[] { host.Alice.Did }, From: host.ServerDid))).Message;
                var jwe = JsonNode.Parse(packed)!.AsObject();
                var tag = jwe["tag"]!.GetValue<string>();
                jwe["tag"] = (tag[0] == 'A' ? 'B' : 'A') + tag[1..];
                return Utf8(jwe.ToJsonString());
            default:
                throw new ArgumentOutOfRangeException(nameof(@case));
        }
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    // A distinctive slice of a body (from its middle, past any shared JSON prefix) to search the logs for.
    private static string Fragment(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        return text.Length <= 24 ? text : text.Substring(text.Length / 2, 24);
    }

    private static Func<HttpMessageHandler> StubReturning(byte[] body, string contentType)
        => () => new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = GeneratedContent.Of(body, contentType) });

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
