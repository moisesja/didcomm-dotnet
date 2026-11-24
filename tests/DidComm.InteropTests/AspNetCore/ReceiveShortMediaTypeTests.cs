using System.Net;
using System.Text;
using AwesomeAssertions;
using DidComm.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PlainPath = DidComm.InteropTests.AspNetCore.RewrappedReceiveTests.PlainPath;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #110: the receive paths read a declared media type the way message_structure.md asks of a
/// recipient: "IANA types for DIDComm messages MAY omit the application/ prefix; the recipient MUST
/// treat media types not containing / as having the application/ prefix present" (FR-MSG-06). The
/// value is read once (parameters dropped, trimmed, the prefix added when there is no <c>/</c>) and
/// that one base type is checked against <see cref="DidCommReceiveOptions.AcceptedMediaTypes"/>, whose
/// entries are read the same way, and against the body's envelope. A value naming no type (empty,
/// parameters only), one with more than one <c>/</c>, and an unknown type are refused as before.
/// </summary>
public sealed class ReceiveShortMediaTypeTests
{
    private const string Short = "didcomm-encrypted+json";

    public static TheoryData<PlainPath> HttpPaths => new() { PlainPath.HttpCallback, PlainPath.HttpRegistry, PlainPath.HttpSessionCallback };

    public static TheoryData<PlainPath> SocketPaths => new() { PlainPath.WebSocketCallback, PlainPath.WebSocketRegistry };

    [Theory]
    [MemberData(nameof(HttpPaths))]
    public async Task Over_HTTP_a_message_declared_with_the_short_media_type_form_is_accepted(PlainPath path)
    {
        await using var r = await PlainReceivers.StartAsync();
        var host = r.Host;
        var message = host.RecordingMessage(host.Alice).Build();

        var status = await PostRawAsync(host, PlainReceivers.PathOf(path), await host.AuthcryptAsync(host.Alice, message), Short);

        status.Should().Be(HttpStatusCode.Accepted);
        r.SeenOf(message.Id).Unpacked.Message.Id.Should().Be(message.Id);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(SocketPaths))]
    public async Task Over_STOMP_a_SEND_declared_with_the_short_media_type_form_is_accepted(PlainPath path)
    {
        await using var r = await PlainReceivers.StartAsync(o => o.UseStomp = true);
        var host = r.Host;
        var message = host.RecordingMessage(host.Alice).Build();
        using var socket = await ReceiveDeclaredMediaTypeTests.StompConnectAsync(host, PlainReceivers.PathOf(path));

        await ReceiveDeclaredMediaTypeTests.SendStompAsync(socket, ReceiveDeclaredMediaTypeTests.Send(await host.AuthcryptAsync(host.Alice, message), Short));
        await ReceiveDeclaredMediaTypeTests.StompFenceAsync(r, socket);

        r.SeenCount(message.Id).Should().Be(1);
        host.LeakingLogLines().Should().BeEmpty();
    }

    public enum Declared
    {
        Full,
        Short,
    }

    [Theory]
    [InlineData(Declared.Full)]
    [InlineData(Declared.Short)]
    public async Task An_accepted_list_that_names_the_short_form_accepts_either_form_and_nothing_else(Declared declared)
    {
        await using var r = await PlainReceivers.StartAsync(o =>
            o.PostConfigureServices = s => s.Configure<DidCommReceiveOptions>(x => x.AcceptedMediaTypes = new[] { Short }));
        var host = r.Host;
        var message = host.RecordingMessage(host.Alice).Build();
        var signed = host.RecordingMessage(host.Alice).Build();

        var accepted = await PostRawAsync(host, "/didcomm", await host.AuthcryptAsync(host.Alice, message), declared == Declared.Full ? DidCommMediaTypes.Encrypted : Short);
        var refused = await PostRawAsync(host, "/didcomm", (await host.PackAsync(host.Alice, signed, EnvelopeShape.SignedOnly)).Packed, DidCommMediaTypes.Signed);

        accepted.Should().Be(HttpStatusCode.Accepted);
        r.SeenOf(message.Id).Unpacked.Message.Id.Should().Be(message.Id);
        refused.Should().Be(HttpStatusCode.UnsupportedMediaType, "the list names the encrypted type only");
        r.SeenCount(signed.Id).Should().Be(0);
    }

    public static TheoryData<string> ValuesNamingNoAcceptedType => new()
    {
        "application/didcomm-encrypted+json/x",   // more than one '/'
        "didcomm-encrypted+json/",                // more than one '/' once the prefix is added
        ";charset=utf-8",                         // parameters only
        "application/didcomm-unknown+json",       // an unknown type
        "text/plain",
        "application/didcomm-encrypted+json\"",
        "application/didcomm-encrypted+json, application/didcomm-plain+json",
    };

    [Theory]
    [MemberData(nameof(ValuesNamingNoAcceptedType))]
    public async Task Over_HTTP_a_value_naming_no_accepted_type_is_refused_415(string value)
    {
        await using var r = await PlainReceivers.StartAsync();
        var host = r.Host;
        var message = host.RecordingMessage(host.Alice).Build();

        var status = await PostRawAsync(host, "/didcomm", await host.AuthcryptAsync(host.Alice, message), value);

        status.Should().Be(HttpStatusCode.UnsupportedMediaType);
        r.SeenCount(message.Id).Should().Be(0);
    }

    // Over HTTP the client's own header handling sends this one cleaned up ("application/didcomm-
    // encrypted+json"); a STOMP header carries it as written.
    public static TheoryData<string> StompValuesNamingNoAcceptedType()
    {
        var data = new TheoryData<string> { "application/ didcomm-encrypted+json" };
        foreach (var value in ValuesNamingNoAcceptedType)
            data.Add(value);
        return data;
    }

    [Theory]
    [MemberData(nameof(StompValuesNamingNoAcceptedType))]
    public async Task Over_STOMP_a_SEND_whose_content_type_names_no_accepted_type_is_discarded(string value)
    {
        await using var r = await PlainReceivers.StartAsync(o => o.UseStomp = true);
        var host = r.Host;
        var message = host.RecordingMessage(host.Alice).Build();
        using var socket = await ReceiveDeclaredMediaTypeTests.StompConnectAsync(host, PlainReceivers.PathOf(PlainPath.WebSocketRegistry));

        await ReceiveDeclaredMediaTypeTests.SendStompAsync(socket, ReceiveDeclaredMediaTypeTests.Send(await host.AuthcryptAsync(host.Alice, message), value));
        await ReceiveDeclaredMediaTypeTests.StompFenceAsync(r, socket);

        r.SeenCount(message.Id).Should().Be(0);
        host.Logs.Entries.Should().ContainSingle(e => e.Contains("SEND with missing/unaccepted content-type; discarding message.", StringComparison.Ordinal));
    }

    public static TheoryData<string> FullFormSpellings => new()
    {
        "application/didcomm-encrypted+json",
        "APPLICATION/DIDCOMM-ENCRYPTED+JSON",
        "application/didcomm-encrypted+json; charset=utf-8",
        "application/didcomm-encrypted+json;charset=\"utf-8\"",
        "application/didcomm-encrypted+json ; charset=utf-8",
        "didcomm-encrypted+json; charset=utf-8",
        "DIDCOMM-ENCRYPTED+JSON",
    };

    [Theory]
    [MemberData(nameof(FullFormSpellings))]
    public async Task Over_HTTP_the_encrypted_type_in_any_case_and_with_parameters_is_accepted(string value)
    {
        await using var r = await PlainReceivers.StartAsync();
        var host = r.Host;
        var message = host.RecordingMessage(host.Alice).Build();

        var status = await PostRawAsync(host, "/didcomm", await host.AuthcryptAsync(host.Alice, message), value);

        status.Should().Be(HttpStatusCode.Accepted);
        r.SeenOf(message.Id).Unpacked.Message.Id.Should().Be(message.Id);
    }

    public static TheoryData<string?, string?> Readings => new()
    {
        { "application/didcomm-encrypted+json", "application/didcomm-encrypted+json" },
        { "didcomm-encrypted+json", "application/didcomm-encrypted+json" },
        { "  didcomm-plain+json ; x=y", "application/didcomm-plain+json" },
        { "APPLICATION/DIDCOMM-SIGNED+JSON", "APPLICATION/DIDCOMM-SIGNED+JSON" },
        { "application/didcomm-encrypted+json/x", null },
        { "a/b/c", null },
        { ";charset=utf-8", null },
        { "   ", null },
        { "", null },
        { null, null },
    };

    [Theory]
    [MemberData(nameof(Readings))]
    public void A_declared_value_is_read_once_with_the_application_prefix_added_when_it_has_no_slash(string? value, string? expected)
    {
        var read = DidCommEndpointRouteBuilderExtensions.BaseMediaType(value);

        if (expected is null)
            read.Should().BeNull();
        else
            read.Should().Be(expected);
    }

    private static async Task<HttpStatusCode> PostRawAsync(ReturnRouteTestHost host, string path, string packed, string contentType)
    {
        using var client = host.Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(packed)) };
        // Without validation: the short form has no '/', which the framework's own header parser refuses.
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType).Should().BeTrue();
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }
}
