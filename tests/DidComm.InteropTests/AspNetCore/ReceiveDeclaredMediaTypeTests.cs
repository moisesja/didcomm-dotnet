using System.Net;
using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.Messages;
using DidComm.Transports.Stomp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PlainPath = DidComm.InteropTests.AspNetCore.RewrappedReceiveTests.PlainPath;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #110: every receive path classifies the body by its structure, with the rule the unpack uses,
/// before anything is decrypted or verified. "The IANA media type for the POST request MUST be set to
/// the [corresponding media type]" (transports.md L66): over HTTP and in a STOMP SEND frame the
/// declared media type must be the one of the envelope the body is (else 415, or the SEND is
/// discarded as one with an unaccepted content-type); a bare WebSocket message declares none, so the
/// envelope's own media type must be accepted (else the message is discarded). A body that cannot be
/// classified is refused by the unpack, as before. On the three <c>MapDidCommEndpoint</c> overloads,
/// both <c>MapDidCommWebSocket</c> loops (bare and STOMP) and the mediator's endpoints.
/// </summary>
public sealed class ReceiveDeclaredMediaTypeTests
{
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(15);

    public enum Kind
    {
        Encrypted,
        Signed,
        Plain,
    }

    public enum Declared
    {
        Matching,
        Mislabelled,
    }

    public static TheoryData<PlainPath, Kind, Declared> HttpPathsKindsAndLabels()
    {
        var data = new TheoryData<PlainPath, Kind, Declared>();
        foreach (var path in new[] { PlainPath.HttpCallback, PlainPath.HttpRegistry, PlainPath.HttpSessionCallback })
        {
            foreach (var kind in Enum.GetValues<Kind>())
            {
                foreach (var declared in Enum.GetValues<Declared>())
                    data.Add(path, kind, declared);
            }
        }
        return data;
    }

    // ---- HTTP: the declared type is the envelope's -------------------------------------------------

    [Theory]
    [MemberData(nameof(HttpPathsKindsAndLabels))]
    public async Task Over_HTTP_a_body_whose_envelope_does_not_match_its_declared_media_type_is_refused_415(PlainPath path, Kind kind, Declared declared)
    {
        await using var r = await PlainReceivers.StartAsync();
        var host = r.Host;
        var message = host.RecordingMessage(host.Alice).Build();
        var (packed, contentType) = await PackAsync(host, message, kind);

        var response = await host.PostAsync(packed, PlainReceivers.PathOf(path), declared == Declared.Matching ? contentType : MislabelOf(kind));

        if (declared == Declared.Matching)
        {
            response.Status.Should().Be(HttpStatusCode.Accepted, "the default host accepts every correctly labelled kind");
            r.SeenOf(message.Id).Unpacked.Message.Id.Should().Be(message.Id);
        }
        else
        {
            response.Status.Should().Be(HttpStatusCode.UnsupportedMediaType);
            response.Body.Should().BeEmpty();
            host.Logs.Entries.Should().NotContain(e => e.StartsWith("DidComm receive: rejecting inbound envelope", StringComparison.Ordinal), "refused before any unpack");
        }
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- STOMP: a SEND's content-type is the envelope's --------------------------------------------

    public static TheoryData<PlainPath, Kind, Declared> SocketPathsKindsAndLabels()
    {
        var data = new TheoryData<PlainPath, Kind, Declared>();
        foreach (var path in new[] { PlainPath.WebSocketCallback, PlainPath.WebSocketRegistry })
        {
            foreach (var kind in Enum.GetValues<Kind>())
            {
                foreach (var declared in Enum.GetValues<Declared>())
                    data.Add(path, kind, declared);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(SocketPathsKindsAndLabels))]
    public async Task Over_STOMP_a_SEND_whose_envelope_does_not_match_its_content_type_is_discarded(PlainPath path, Kind kind, Declared declared)
    {
        await using var r = await PlainReceivers.StartAsync(o => o.UseStomp = true);
        var host = r.Host;
        var message = host.RecordingMessage(host.Alice).Build();
        var (packed, contentType) = await PackAsync(host, message, kind);
        using var socket = await StompConnectAsync(host, PlainReceivers.PathOf(path));

        await SendStompAsync(socket, Send(packed, declared == Declared.Matching ? contentType : MislabelOf(kind)));
        await StompFenceAsync(r, socket);

        if (declared == Declared.Matching)
        {
            r.SeenOf(message.Id).Unpacked.Message.Id.Should().Be(message.Id, "the default host accepts every correctly labelled kind");
        }
        else
        {
            r.SeenCount(message.Id).Should().Be(0);
            host.Logs.Entries.Should().ContainSingle(e => e.Contains("SEND with missing/unaccepted content-type; discarding message.", StringComparison.Ordinal));
        }
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- a bare WebSocket message: its envelope's type must be accepted ----------------------------

    public enum Accepting
    {
        EncryptedOnly,
        Default,
    }

    public static TheoryData<PlainPath, Kind, Accepting> SocketPathsKindsAndHosts()
    {
        var data = new TheoryData<PlainPath, Kind, Accepting>();
        foreach (var path in new[] { PlainPath.WebSocketCallback, PlainPath.WebSocketRegistry })
        {
            foreach (var kind in Enum.GetValues<Kind>())
            {
                foreach (var accepting in Enum.GetValues<Accepting>())
                    data.Add(path, kind, accepting);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(SocketPathsKindsAndHosts))]
    public async Task On_a_bare_WebSocket_a_message_whose_envelope_is_of_a_type_not_accepted_is_discarded(PlainPath path, Kind kind, Accepting accepting)
    {
        await using var r = await PlainReceivers.StartAsync(o =>
        {
            if (accepting == Accepting.EncryptedOnly)
                o.PostConfigureServices = s => s.Configure<DidCommReceiveOptions>(x => x.AcceptedMediaTypes = new[] { DidCommMediaTypes.Encrypted });
        });
        var host = r.Host;
        var message = host.RecordingMessage(host.Alice).Build();
        var (packed, _) = await PackAsync(host, message, kind);

        var delivery = await r.DeliverAsync(path, packed);

        if (accepting == Accepting.Default || kind == Kind.Encrypted)
        {
            delivery.Seen.Should().ContainSingle().Which.Unpacked.Message.Id.Should().Be(message.Id);
        }
        else
        {
            delivery.Seen.Should().BeEmpty();
            host.Logs.Entries.Should().ContainSingle(e => e == "MapDidCommWebSocket: discarding a message whose envelope's media type is not accepted.");
        }
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- a body that cannot be classified: refused by the unpack, as before -------------------------

    public static TheoryData<PlainPath> AllPaths => new()
    {
        PlainPath.HttpCallback, PlainPath.HttpRegistry, PlainPath.HttpSessionCallback, PlainPath.WebSocketCallback, PlainPath.WebSocketRegistry,
    };

    [Theory]
    [MemberData(nameof(AllPaths))]
    public async Task A_body_that_cannot_be_classified_reaches_the_unpack_refusal_as_before(PlainPath path)
    {
        await using var r = await PlainReceivers.StartAsync();
        var host = r.Host;

        var delivery = await r.DeliverAsync(path, "not a json object");

        delivery.AssertRefused(path, host, "MalformedMessageException");
        host.Logs.Entries.Should().NotContain(e => e == "MapDidCommWebSocket: discarding a message whose envelope's media type is not accepted.");
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- the mediator's endpoints -----------------------------------------------------------------

    public static TheoryData<Kind, Declared> KindsAndLabels()
    {
        var data = new TheoryData<Kind, Declared>();
        foreach (var kind in Enum.GetValues<Kind>())
        {
            foreach (var declared in Enum.GetValues<Declared>())
                data.Add(kind, declared);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(KindsAndLabels))]
    public async Task On_the_mediators_HTTP_endpoint_a_body_whose_envelope_does_not_match_its_declared_media_type_is_refused_415(Kind kind, Declared declared)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(mediator.Did);
        var (packed, contentType) = await PackForMediatorAsync(mediator, alice, note, kind);

        var response = await PostAsync(mediator, packed, declared == Declared.Matching ? contentType : MislabelOf(kind));

        if (declared == Declared.Matching)
        {
            response.Should().Be(HttpStatusCode.Accepted);
            DispatchedTo(mediator, note.Id).Should().BeTrue("the default mediator accepts every correctly labelled kind");
        }
        else
        {
            response.Should().Be(HttpStatusCode.UnsupportedMediaType);
            DispatchedTo(mediator, note.Id).Should().BeFalse();
        }
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData(Kind.Encrypted)]
    [InlineData(Kind.Signed)]
    [InlineData(Kind.Plain)]
    public async Task On_the_mediators_WebSocket_endpoint_a_message_whose_envelope_is_of_a_type_not_accepted_is_discarded(Kind kind)
    {
        await using var mediator = await MediatorTestServer.StartAsync(o =>
            o.PostConfigureServices = s => s.Configure<DidCommReceiveOptions>(x => x.AcceptedMediaTypes = new[] { DidCommMediaTypes.Encrypted }));
        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(mediator.Did);
        var (packed, _) = await PackForMediatorAsync(mediator, alice, note, kind);
        using var socket = await mediator.ConnectAsync();

        await ReturnRouteTestHost.SendAsync(socket, packed);
        // Either event ends the wait: the note reached the dispatcher, or the endpoint discarded it.
        await MediatorTestServer.WaitUntilAsync(
            () => DispatchedTo(mediator, note.Id) || mediator.Logs.Entries.Any(e => e.StartsWith("MapDidCommWebSocket: discarding", StringComparison.Ordinal)),
            "the mediator handled the message");

        DispatchedTo(mediator, note.Id).Should().Be(kind == Kind.Encrypted, "an encrypted-only mediator takes the encrypted note only");
        if (kind != Kind.Encrypted)
            mediator.Logs.Entries.Should().ContainSingle(e => e == "MapDidCommWebSocket: discarding a message whose envelope's media type is not accepted.");
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    // ---- support ------------------------------------------------------------------------------------

    private static async Task<(string Packed, string ContentType)> PackAsync(ReturnRouteTestHost host, Message message, Kind kind)
        => await host.PackAsync(host.Alice, message, kind switch
        {
            Kind.Encrypted => EnvelopeShape.Authcrypt,
            Kind.Signed => EnvelopeShape.SignedOnly,
            _ => EnvelopeShape.Plaintext,
        });

    private static async Task<(string Packed, string ContentType)> PackForMediatorAsync(MediatorTestServer mediator, MediatorSender alice, Message note, Kind kind)
        => kind switch
        {
            Kind.Encrypted => (await alice.AuthcryptAsync(note, mediator.Did), DidCommMediaTypes.Encrypted),
            Kind.Signed => (await alice.Client.PackSignedAsync(note, alice.Did), DidCommMediaTypes.Signed),
            _ => (await alice.Client.PackPlaintextAsync(note), DidCommMediaTypes.Plain),
        };

    /// <summary>Another accepted DIDComm media type than the one of <paramref name="kind"/>'s envelope.</summary>
    private static string MislabelOf(Kind kind) => kind switch
    {
        Kind.Encrypted => DidCommMediaTypes.Plain,
        Kind.Signed => DidCommMediaTypes.Encrypted,
        _ => DidCommMediaTypes.Signed,
    };

    private static bool DispatchedTo(MediatorTestServer mediator, string id)
        => mediator.Logs.Entries.Any(e => e.Contains("No protocol handler registered", StringComparison.Ordinal) && e.Contains(id, StringComparison.Ordinal));

    private static async Task<HttpStatusCode> PostAsync(MediatorTestServer mediator, string packed, string contentType)
    {
        using var client = mediator.Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, mediator.Options.Pattern)
        {
            Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(packed)),
        };
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    internal static async Task<WebSocket> StompConnectAsync(ReturnRouteTestHost host, string path)
    {
        var socket = await host.ConnectAsync(path);
        await SendStompAsync(socket, new StompFrame("CONNECT", new[]
        {
            KeyValuePair.Create("accept-version", "1.2"),
            KeyValuePair.Create("host", "localhost"),
        }, ReadOnlyMemory<byte>.Empty));
        var (_, connected) = await ReturnRouteTestHost.ReceiveAsync(socket, Hang);
        connected.Should().StartWith("CONNECTED");
        return socket;
    }

    /// <summary>An authcrypted fence in a SEND on <paramref name="socket"/>; returns once it was handled (the loop handles messages in order).</summary>
    internal static async Task StompFenceAsync(PlainReceivers r, WebSocket socket)
    {
        var fence = r.Host.RecordingMessage(r.Host.Alice, "fence").WithId("zqxfence" + Guid.NewGuid().ToString("N")).Build();
        await SendStompAsync(socket, Send(await r.Host.AuthcryptAsync(r.Host.Alice, fence), DidCommMediaTypes.Encrypted));
        (await ReturnRouteTestHost.WaitUntilAsync(() => r.SeenCount(fence.Id) > 0, Hang))
            .Should().BeTrue("the fence sent after the message was handled");
    }

    internal static StompFrame Send(string packed, string contentType) => new("SEND", new[]
    {
        KeyValuePair.Create("destination", "/didcomm"),
        KeyValuePair.Create("content-type", contentType),
    }, System.Text.Encoding.UTF8.GetBytes(packed));

    internal static async Task SendStompAsync(WebSocket socket, StompFrame frame)
    {
        using var cts = new CancellationTokenSource(Hang);
        await socket.SendAsync(StompFrameCodec.Encode(frame), WebSocketMessageType.Binary, endOfMessage: true, cts.Token);
    }
}
