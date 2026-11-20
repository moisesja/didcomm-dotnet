using System.Text;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.TrustPing;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using DidComm.Tests.Facade;
using DidComm.Threading;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using NetCrypto;
using Xunit;
using DpEnc = DataProofsDotnet.Jose.Encryption;
using JoseAlgorithms = DataProofsDotnet.Jose.JoseAlgorithms;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Tests.Messages;

/// <summary>
/// A message <c>id</c>, <c>thid</c> or <c>pthid</c> is at most 512 characters (FR-MSG-02, FR-MSG-11).
/// DIDComm v2.1 threading describes an id as "a short (&lt;=32 bytes) string" but recommends UUIDs
/// (36 characters as written), so the library accepts up to 512 and refuses anything longer: at
/// unpack, at pack and in <see cref="MessageBuilder.Build"/>, with a message that quotes no value.
/// </summary>
public sealed class MessageIdLengthTests
{
    private const int Max = 512;
    private const string Bob = "did:example:bob";
    private const string BobKid = Bob + "#ka-1";

    // Every long value is made of this letter. No short value in these tests holds a run of 64 of
    // them, so finding that run in an exception message means the value was quoted.
    private static readonly string Run = new('q', 64);

    public static TheoryData<string> Fields => new() { "id", "thid", "pthid" };

    private static Message Valid() => new()
    {
        Id = "msg-1",
        Type = "https://didcomm.org/empty/1.0/empty",
    };

    private static Message With(string field, string value)
    {
        var message = Valid();
        switch (field)
        {
            case "id": message.Id = value; break;
            case "thid": message.Thid = value; break;
            default: message.Pthid = value; break;
        }
        return message;
    }

    private static string ValueOf(Message message, string field)
        => field switch { "id" => message.Id, "thid" => message.Thid!, _ => message.Pthid! };

    private static string Long(int length) => new('q', length);

    [Theory]
    [MemberData(nameof(Fields))]
    public void At_512_characters_each_field_is_accepted(string field)
    {
        var message = With(field, Long(Max));

        Action act = message.Validate;

        act.Should().NotThrow();
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public void At_513_characters_each_field_is_refused_without_quoting_the_value(string field)
    {
        var message = With(field, Long(Max + 1));

        var thrown = Record.Exception(message.Validate);

        thrown.Should().BeOfType<MalformedMessageException>()
            .Which.Message.Should().Contain($"'{field}' exceeds the maximum length of 512 characters").And.NotContain(Run);
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public void The_length_is_checked_before_the_characters_so_an_over_long_invalid_value_is_never_quoted(string field)
    {
        // L-055 poison: 512 valid characters and an invalid 513th. The character check quotes the value
        // (open issue #116), so if it ran first the run would show up in the text.
        var poisoned = With(field, Long(Max) + "/");
        var control = With(field, Long(Max - 1) + "/");

        var thrown = Record.Exception(poisoned.Validate);
        var controlThrown = Record.Exception(control.Validate);

        thrown.Should().BeOfType<MalformedMessageException>()
            .Which.Message.Should().Contain("exceeds the maximum length of 512").And.NotContain(Run).And.NotContain("unreserved");
        controlThrown.Should().BeOfType<MalformedMessageException>()
            .Which.Message.Should().Contain("unreserved", "at 512 characters the length passes and the character check refuses");
    }

    [Fact]
    public void An_empty_id_is_still_refused_as_missing()
    {
        var thrown = Record.Exception(With("id", string.Empty).Validate);

        thrown.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Contain("REQUIRED");
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public void Build_refuses_an_over_long_value(string field)
    {
        var builder = new MessageBuilder().WithType("https://didcomm.org/empty/1.0/empty");
        var value = Long(Max + 1);
        builder = field switch
        {
            "id" => builder.WithId(value),
            "thid" => builder.WithThid(value),
            _ => builder.WithPthid(value),
        };

        var thrown = Record.Exception(() => builder.Build());

        thrown.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Contain("exceeds the maximum length of 512").And.NotContain(Run);
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public async Task Pack_refuses_a_hand_built_message_with_an_over_long_value(string field)
    {
        var client = Receiver(new SpySecretsResolver(), new SpyDidResolver());
        var message = With(field, Long(Max + 1));

        var thrown = await Record.ExceptionAsync(() => client.PackPlaintextAsync(message));

        thrown.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Contain("exceeds the maximum length of 512").And.NotContain(Run);
    }

    public static TheoryData<string, int, bool> UnpackCells()
    {
        var data = new TheoryData<string, int, bool>();
        foreach (var field in new[] { "id", "thid", "pthid" })
        {
            foreach (var anoncrypt in new[] { false, true })
            {
                data.Add(field, Max, anoncrypt);
                data.Add(field, Max + 1, anoncrypt);
                data.Add(field, -1, anoncrypt); // as long as the receive limit allows
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(UnpackCells))]
    public async Task Unpack_accepts_512_and_refuses_longer_up_to_the_receive_limit(string field, int length, bool anoncrypt)
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var limit = new DidCommOptions().MaxReceiveBytes;
        string Plaintext(string value)
        {
            var o = new JsonObject
            {
                ["id"] = field == "id" ? value : "len-1",
                ["typ"] = MediaTypes.Plaintext,
                ["type"] = "https://example.com/protocols/test/1.0/ping",
                ["to"] = new JsonArray(Bob),
                ["body"] = new JsonObject(),
            };
            if (field != "id")
                o[field] = value;
            return o.ToJsonString();
        }

        // -1: as long as the receive gate allows, so the length bound is what refuses it, not the size
        // gate. Anoncrypt's base64url ciphertext grows the message by about a third.
        var overhead = Plaintext(string.Empty).Length;
        var actualLength = length >= 0 ? length : anoncrypt ? (limit - 2048) / 4 * 3 - overhead : limit - 1 - overhead;
        var plaintext = Plaintext(Long(actualLength));
        var packed = anoncrypt
            ? DpEnc.JweBuilder.BuildEcdhEsA256Kw(
                Encoding.UTF8.GetBytes(plaintext), new[] { bob.PublicJwk }, JoseAlgorithms.A256CbcHs512, new JoseCryptoProvider(), MediaTypes.Encrypted)
            : plaintext;
        Encoding.UTF8.GetByteCount(packed).Should().BeLessThan(limit, "the receive size gate must not be what refuses it");
        var receiver = Receiver(new SpySecretsResolver(bob.PrivateJwk), new SpyDidResolver(LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk)));

        if (actualLength <= Max)
        {
            var result = await receiver.UnpackAsync(packed);
            ValueOf(result.Message, field).Should().HaveLength(Max);
            return;
        }

        var thrown = await Record.ExceptionAsync(() => receiver.UnpackAsync(packed));

        thrown.Should().BeOfType<MalformedMessageException>()
            .Which.Message.Should().Contain($"'{field}' exceeds the maximum length of 512 characters").And.NotContain(Run);
    }

    [Fact]
    public void The_default_generator_s_ids_pass()
    {
        var id = UuidV4MessageIdGenerator.Instance.NewId();

        id.Should().HaveLength(36);
        var message = new MessageBuilder().WithType("https://didcomm.org/empty/1.0/empty").Build();
        message.Id.Should().HaveLength(36);
    }

    [Fact]
    public void A_reply_s_thread_refuses_a_declared_thid_over_512_without_quoting_it()
    {
        var atBound = With("thid", Long(Max));
        var over = With("thid", Long(Max + 1));

        var thid = ThreadIds.ResponseThreadId(atBound);
        var thrown = Record.Exception(() => ThreadIds.ResponseThreadId(over));

        thid.Should().HaveLength(Max);
        thrown.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Contain("not a valid thread id").And.NotContain(Run);
    }

    [Fact]
    public void A_ping_response_carries_a_512_character_thid_and_refuses_a_longer_one()
    {
        Message Ping(string thid) => new()
        {
            Id = "ping-1",
            Type = TrustPing.PingType,
            From = "did:example:alice",
            To = new List<string> { Bob },
            Thid = thid,
        };

        var response = TrustPing.CreateResponse(Ping(Long(Max)));
        var thrown = Record.Exception(() => TrustPing.CreateResponse(Ping(Long(Max + 1))));

        response.Thid.Should().HaveLength(Max);
        thrown.Should().BeOfType<MalformedMessageException>().Which.Message.Should().NotContain(Run);
    }

    [Fact]
    public void The_replay_guard_s_default_id_limit_is_the_same_512()
    {
        new ReplayProtectionOptions().MaxMessageIdLength.Should().Be(Max);
    }

    private static DidCommClient Receiver(SpySecretsResolver secrets, SpyDidResolver resolver)
        => new(secrets, new NetDidKeyService(resolver), new DidCommOptions());
}
