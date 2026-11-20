using System.Text;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.OutOfBand;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using DidComm.Tests.Facade;
using AwesomeAssertions;
using NetCrypto;
using Xunit;
using DpBase64Url = DataProofsDotnet.Jose.Base64Url;

namespace DidComm.Tests.Messages;

/// <summary>
/// Every way the library builds or sends a message keeps an <c>id</c>, <c>thid</c> or <c>pthid</c>
/// to at most 512 characters (FR-MSG-02, FR-MSG-11): <see cref="MessageBuilder.Build"/>, the four
/// pack forms of <see cref="DidCommClient"/> (plaintext, signed, anoncrypt, authcrypt) and Out of Band
/// (creating an invitation, writing its URL, reading one). A value of 512 works end to end, one of
/// 513 is refused with a message that quotes no value, so the library never sends what its own
/// receivers would refuse.
/// </summary>
public sealed class MessageIdLengthPackPathTests
{
    private const int Max = 512;
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";

    // Every long value is made of this letter; no short value here holds a run of 64 of them, so the
    // run in an exception message means the value was quoted.
    private static readonly string Run = new('q', 64);

    private static readonly TestKeyMaterial AliceSigning = TestKeyMaterial.Generate(KeyType.Ed25519, Alice + "#key-1");
    private static readonly TestKeyMaterial AliceAgreement = TestKeyMaterial.Generate(KeyType.X25519, Alice + "#ka-1");
    private static readonly TestKeyMaterial BobAgreement = TestKeyMaterial.Generate(KeyType.X25519, Bob + "#ka-1");

    public static TheoryData<string, string> FieldsAndPackForms()
    {
        var data = new TheoryData<string, string>();
        foreach (var field in new[] { "id", "thid", "pthid" })
        {
            foreach (var form in new[] { "plaintext", "signed", "anoncrypt", "authcrypt" })
                data.Add(field, form);
        }
        return data;
    }

    public static TheoryData<string> Fields => new() { "id", "thid", "pthid" };

    // ---- the four pack forms --------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(FieldsAndPackForms))]
    public async Task At_512_characters_each_field_packs_in_every_form_and_unpacks_unchanged(string field, string form)
    {
        var message = With(field, Long(Max));

        var packed = await PackAsync(Sender(), message, form);
        var unpacked = await Receiver().UnpackAsync(packed);

        ValueOf(unpacked.Message, field).Should().Be(Long(Max));
    }

    [Theory]
    [MemberData(nameof(FieldsAndPackForms))]
    public async Task At_513_characters_each_field_is_refused_by_every_pack_form_without_quoting_the_value(string field, string form)
    {
        var message = With(field, Long(Max + 1));
        var sender = Sender();

        var thrown = await Record.ExceptionAsync(() => PackAsync(sender, message, form));

        thrown.Should().BeOfType<MalformedMessageException>()
            .Which.Message.Should().Contain($"'{field}' exceeds the maximum length of 512 characters").And.NotContain(Run);
    }

    // ---- MessageBuilder -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Fields))]
    public void Build_accepts_each_field_at_512_characters(string field)
    {
        var builder = new MessageBuilder().WithType("https://didcomm.org/empty/1.0/empty");
        builder = field switch
        {
            "id" => builder.WithId(Long(Max)),
            "thid" => builder.WithThid(Long(Max)),
            _ => builder.WithPthid(Long(Max)),
        };

        var message = builder.Build();

        ValueOf(message, field).Should().HaveLength(Max);
    }

    // ---- Out of Band ----------------------------------------------------------------------------

    [Theory]
    [InlineData(Max, true)]
    [InlineData(Max + 1, false)]
    public void An_invitation_is_created_with_an_id_of_512_characters_and_refused_with_513(int length, bool created)
    {
        var thrown = Record.Exception(() => OutOfBand.CreateInvitation(Alice, id: Long(length)));

        if (created)
            thrown.Should().BeNull();
        else
            thrown.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Contain("'id' exceeds the maximum length of 512 characters").And.NotContain(Run);
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public void Writing_an_invitation_URL_refuses_a_field_of_513_characters_and_writes_one_of_512(string field)
    {
        var at = OutOfBand.CreateInvitation(Alice);
        Set(at.Message, field, Long(Max));
        var over = OutOfBand.CreateInvitation(Alice);
        Set(over.Message, field, Long(Max + 1));

        var url = OutOfBand.ToUrl(at, "https://example.com/invite");
        var thrown = Record.Exception(() => OutOfBand.ToUrl(over, "https://example.com/invite"));

        ValueOf(OutOfBand.FromUrl(url).Message, field).Should().Be(Long(Max));
        thrown.Should().BeOfType<MalformedMessageException>()
            .Which.Message.Should().Contain($"'{field}' exceeds the maximum length of 512 characters").And.NotContain(Run);
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public void Reading_an_invitation_URL_or_its_plaintext_refuses_a_field_of_513_characters_and_reads_one_of_512(string field)
    {
        // Written by hand, as another implementation's invitation would arrive.
        string Json(int length)
        {
            var o = new JsonObject
            {
                ["id"] = field == "id" ? Long(length) : "invitation-1",
                ["type"] = OutOfBand.InvitationType,
                ["from"] = Alice,
                ["body"] = new JsonObject(),
            };
            if (field != "id")
                o[field] = Long(length);
            return o.ToJsonString();
        }
        string Url(int length) => "https://example.com/invite?_oob=" + DpBase64Url.Encode(Encoding.UTF8.GetBytes(Json(length)));

        var read = OutOfBand.FromUrl(Url(Max));
        var fromUrl = Record.Exception(() => OutOfBand.FromUrl(Url(Max + 1)));
        var fromPlaintext = Record.Exception(() => OutOfBand.FromPlaintext(Json(Max + 1)));

        ValueOf(read.Message, field).Should().HaveLength(Max);
        foreach (var thrown in new[] { fromUrl, fromPlaintext })
        {
            thrown.Should().BeOfType<MalformedMessageException>()
                .Which.Message.Should().Contain($"'{field}' exceeds the maximum length of 512 characters").And.NotContain(Run);
        }
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static string Long(int length) => new('q', length);

    private static Message With(string field, string value)
    {
        var message = new Message
        {
            Id = "msg-1",
            Type = "https://example.com/protocols/test/1.0/ping",
            From = Alice,
            To = new List<string> { Bob },
            Body = new JsonObject(),
        };
        Set(message, field, value);
        return message;
    }

    private static void Set(Message message, string field, string value)
    {
        switch (field)
        {
            case "id": message.Id = value; break;
            case "thid": message.Thid = value; break;
            default: message.Pthid = value; break;
        }
    }

    private static string ValueOf(Message message, string field)
        => field switch { "id" => message.Id, "thid" => message.Thid!, _ => message.Pthid! };

    private static async Task<string> PackAsync(DidCommClient sender, Message message, string form) => form switch
    {
        "plaintext" => await sender.PackPlaintextAsync(message),
        "signed" => await sender.PackSignedAsync(message, Alice),
        "anoncrypt" => (await sender.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { Bob }, Forward: false))).Message,
        _ => (await sender.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice, Forward: false))).Message,
    };

    private static SpyDidResolver Resolver() => new(
        LengthCap.Doc(Alice, authentication: AliceSigning.PublicJwk, keyAgreement: AliceAgreement.PublicJwk),
        LengthCap.Doc(Bob, keyAgreement: BobAgreement.PublicJwk));

    private static DidCommClient Sender()
        => new(new SpySecretsResolver(AliceSigning.PrivateJwk, AliceAgreement.PrivateJwk), new NetDidKeyService(Resolver()), new DidCommOptions());

    private static DidCommClient Receiver()
        => new(new SpySecretsResolver(BobAgreement.PrivateJwk), new NetDidKeyService(Resolver()), new DidCommOptions());
}
