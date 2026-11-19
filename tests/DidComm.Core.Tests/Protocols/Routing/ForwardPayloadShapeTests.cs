using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Tests.Envelopes;
using NetCrypto;
using Base64Url = DidComm.Jose.Base64Url;
using Xunit;
using static DidComm.Tests.Protocols.Routing.ForwardTestEnvelopes;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// #98: what a forward carries must be a DIDComm message, UTF-8 JSON with an object at the top level,
/// in every processor mode, pass-through included. The spec's forward <c>attachments</c> are "the
/// DIDComm message(s) to send" to <c>next</c> (Routing), every DIDComm encoding is <c>+json</c>
/// (Message Structure), and JSON between systems is UTF-8 (RFC 8259 §8.1). Until #98 pass-through
/// relayed any bytes unread, so a sender could make a mediator write a WebSocket text frame that is
/// not UTF-8, which makes the next hop fail the whole connection (RFC 6455 §8.1).
/// </summary>
public sealed class ForwardPayloadShapeTests
{
    private const string Next = "did:example:next";
    private const string Marker = "ZZZMARKERZZZ";

    // Invalid UTF-8 (0xFF) inside a JSON string. A lossy UTF-8 decode turns it into a valid JSON object
    // (U+FFFD in the string), which is why the check is made on the bytes: before #98 rewrap and extra
    // keys re-embedded this silently altered, and pass-through relayed it as it came.
    private static readonly byte[] JsonWithInvalidUtf8 = { (byte)'{', (byte)'"', (byte)'a', (byte)'"', (byte)':', (byte)'"', 0xFF, (byte)'"', (byte)'}' };

    // Not UTF-8 and not JSON at all.
    private static readonly byte[] NotUtf8 = { 0x7B, 0xFF, 0xFE, 0x7D };

    public static TheoryData<string> Modes => new() { "pass-through", "rewrap", "extra-routing-keys" };

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_data_base64_payload_that_is_not_UTF8_is_refused_as_malformed_in_every_mode(string mode)
    {
        var act = async () => await ProcessorFor(mode).ProcessAsync(AsDecryptedByMediator(ForwardCarrying(new AttachmentData { Base64 = Base64Url.Encode(JsonWithInvalidUtf8) })));

        var failure = (await act.Should().ThrowAsync<MalformedMessageException>()).Which;
        failure.Message.Should().Contain("not a DIDComm message");
    }

    [Fact]
    public async Task Pass_through_refuses_bytes_that_are_neither_UTF8_nor_JSON()
    {
        var act = async () => await NewProcessor().ProcessAsync(AsDecryptedByMediator(ForwardCarrying(new AttachmentData { Base64 = Base64Url.Encode(NotUtf8) })));

        await act.Should().ThrowAsync<MalformedMessageException>();
    }

    public static TheoryData<string, string> NotADidCommMessage => new()
    {
        { "base64: UTF-8 text, not JSON", "text " + Marker },
        { "base64: a JSON array", "[\"" + Marker + "\"]" },
        { "base64: a JSON string", "\"" + Marker + "\"" },
        { "base64: a JSON number", "42" },
        { "base64: an object followed by more", "{\"a\":\"" + Marker + "\"} {}" },
        { "data.json: an array", "json:[\"" + Marker + "\"]" },
        { "data.json: a string", "json:\"" + Marker + "\"" },
    };

    [Theory]
    [MemberData(nameof(NotADidCommMessage))]
    public async Task Pass_through_refuses_a_payload_that_is_UTF8_but_not_a_JSON_object_without_quoting_it(string because, string payload)
    {
        var data = payload.StartsWith("json:", StringComparison.Ordinal)
            ? new AttachmentData { Json = JsonNode.Parse(payload["json:".Length..]) }
            : new AttachmentData { Base64 = Base64Url.EncodeUtf8(payload) };

        var act = async () => await NewProcessor().ProcessAsync(AsDecryptedByMediator(ForwardCarrying(data)));

        var failure = (await act.Should().ThrowAsync<MalformedMessageException>(because)).Which;
        failure.Message.Should().NotContain(Marker);
    }

    [Fact]
    public async Task Pass_through_still_relays_a_DIDComm_message_byte_for_byte()
    {
        // The control: the check reads the payload, it does not rewrite it (white space and key order kept).
        var payload = Encoding.UTF8.GetBytes("{ \"protected\" : \"abc\",\n  \"ciphertext\":\"xyz\" }\n");

        var result = await NewProcessor().ProcessAsync(AsDecryptedByMediator(ForwardCarrying(new AttachmentData { Base64 = Base64Url.Encode(payload) })));

        result.OnwardPacked.Should().Equal(payload);
    }

    private static ForwardProcessor ProcessorFor(string mode) => mode switch
    {
        "pass-through" => NewProcessor(),
        // The key service resolves nothing: a payload that got past the check would fail as DidResolutionException.
        "rewrap" => NewProcessor(new ForwardProcessorOptions(Mode: RewrapMode.ReanoncryptToNext)),
        "extra-routing-keys" => NewProcessor(new ForwardProcessorOptions(
            ExtraRecipientRoutingKeys: new[] { TestKeyMaterial.Generate(KeyType.X25519, "did:example:extra#ka-1").PublicJwk })),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static Message ForwardCarrying(AttachmentData data) => new MessageBuilder()
        .WithType(ForwardConstants.ForwardTypeUri)
        .WithTo("did:example:m")
        .WithBody(new JsonObject { ["next"] = Next })
        .WithAttachment(new Attachment { Data = data })
        .Build();
}
