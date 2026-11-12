using System.Text.Json;
using DidComm.Facade;
using DidComm.Json;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Transports.ReturnRoute;

/// <summary>
/// #92 G1 — the client-side return-route rules as pure functions over synthetic
/// <see cref="UnpackResult"/>s: the identity rule a returned or pushed message must pass
/// (<see cref="ReturnRouteIdentity"/>, JC-6), the FR-RR-08 replay gate shared with the receive side
/// (<see cref="InboundReplayGate"/>), the FR-RR-09 stamping scope (<see cref="OutgoingCreatedTime"/>)
/// and the shared header writer (<see cref="ReturnRouteHeaderWriter"/>).
/// </summary>
public sealed class ReturnRouteClientRulesTests
{
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";
    private const string Carol = "did:example:carol";
    private const string Mallory = "did:example:mallory";
    private const string AppType = "https://didcomm.org/test/1.0/m";

    public enum Shape
    {
        Plaintext,
        SignedOnly,
        Anoncrypt,
        AnoncryptOfSigned,
        AuthcryptWithEmptySenderKid,
    }

    // A returned message as the client sees it: Bob (the addressed peer) to Alice (the sender).
    private static UnpackResult Returned(
        bool encrypted = true,
        bool authenticated = true,
        string? senderKid = Bob + "#key-x25519-1",
        string? signerKid = null,
        string? recipientKid = Alice + "#key-x25519-1",
        string type = AppType,
        string id = "msg-1",
        long? createdTime = null,
        ReturnRouteMode? header = null)
    {
        var builder = new MessageBuilder().WithId(id).WithType(type).WithFrom(Bob).WithTo(Alice);
        if (createdTime is long t)
            builder = builder.WithCreatedTime(t);
        if (header is ReturnRouteMode mode)
            builder = builder.WithReturnRoute(mode, mode == ReturnRouteMode.Thread ? "thr-1" : null);
        return new UnpackResult(
            Message: builder.Build(),
            Stack: Array.Empty<DidComm.Jose.EnvelopeKind>(),
            Encrypted: encrypted,
            Authenticated: authenticated,
            NonRepudiation: signerKid is not null,
            AnonymousSender: encrypted && !authenticated,
            ContentEncryption: null,
            KeyWrap: null,
            SignatureAlgorithm: null,
            SignerKid: signerKid,
            SenderKid: senderKid,
            RecipientKid: recipientKid,
            AllRecipientKids: recipientKid is null ? Array.Empty<string>() : new[] { recipientKid },
            FromPrior: null);
    }

    private static UnpackResult OfShape(Shape shape, string type = AppType) => shape switch
    {
        Shape.Plaintext => Returned(encrypted: false, authenticated: false, senderKid: null, recipientKid: null, type: type),
        Shape.SignedOnly => Returned(encrypted: false, authenticated: true, senderKid: null, signerKid: Bob + "#key-ed25519-1", recipientKid: null, type: type),
        Shape.Anoncrypt => Returned(authenticated: false, senderKid: null, type: type),
        Shape.AnoncryptOfSigned => Returned(authenticated: true, senderKid: null, signerKid: Bob + "#key-ed25519-1", type: type),
        Shape.AuthcryptWithEmptySenderKid => Returned(senderKid: string.Empty, type: type),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    // ---- identity rule (JC-6) ----

    [Fact]
    public void Identity_rule_accepts_authcrypt_from_the_addressed_peer_to_the_sending_local_DID()
    {
        ReturnRouteIdentity.IsFromPeerToLocal(Returned(), Bob, Alice, out var refusal).Should().BeTrue();
        refusal.Should().Be(ReturnedMessageRefusal.None);

        // An inner signature by the same subject is fine (FR-CONSIST-05).
        ReturnRouteIdentity.IsFromPeerToLocal(Returned(signerKid: Bob + "#key-ed25519-1"), Bob, Alice, out refusal).Should().BeTrue();
        refusal.Should().Be(ReturnedMessageRefusal.None);
    }

    [Theory]
    [InlineData(Shape.Plaintext)]
    [InlineData(Shape.SignedOnly)]
    [InlineData(Shape.Anoncrypt)]
    [InlineData(Shape.AnoncryptOfSigned)]
    [InlineData(Shape.AuthcryptWithEmptySenderKid)]
    public void Identity_rule_refuses_non_authcrypt_shapes(Shape shape)
    {
        ReturnRouteIdentity.IsFromPeerToLocal(OfShape(shape), Bob, Alice, out var refusal).Should().BeFalse();
        refusal.Should().Be(ReturnedMessageRefusal.NotAuthcrypt);
    }

    [Fact]
    public void Identity_rule_refuses_an_inner_signer_of_another_subject()
    {
        ReturnRouteIdentity.IsFromPeerToLocal(Returned(signerKid: Mallory + "#key-ed25519-1"), Bob, Alice, out var refusal).Should().BeFalse();
        refusal.Should().Be(ReturnedMessageRefusal.NotAuthcrypt);
    }

    [Fact]
    public void Identity_rule_refuses_another_peer()
    {
        ReturnRouteIdentity.IsFromPeerToLocal(Returned(senderKid: Mallory + "#key-x25519-1"), Bob, Alice, out var refusal).Should().BeFalse();
        refusal.Should().Be(ReturnedMessageRefusal.PeerMismatch);
    }

    [Fact]
    public void Identity_rule_refuses_another_local_DID()
    {
        // The client holds Carol's keys too, but the request was sent by Alice.
        ReturnRouteIdentity.IsFromPeerToLocal(Returned(recipientKid: Carol + "#key-x25519-1"), Bob, Alice, out var refusal).Should().BeFalse();
        refusal.Should().Be(ReturnedMessageRefusal.LocalMismatch);
    }

    [Fact]
    public void Identity_rule_refuses_both()
    {
        ReturnRouteIdentity.IsFromPeerToLocal(
            Returned(senderKid: Mallory + "#key-x25519-1", recipientKid: Carol + "#key-x25519-1"), Bob, Alice, out var refusal).Should().BeFalse();
        refusal.Should().Be(ReturnedMessageRefusal.PeerAndLocalMismatch);
    }

    [Fact]
    public void Identity_rule_refuses_a_forward_type()
    {
        ReturnRouteIdentity.IsFromPeerToLocal(Returned(type: "https://didcomm.org/routing/2.0/forward"), Bob, Alice, out var refusal).Should().BeFalse();
        refusal.Should().Be(ReturnedMessageRefusal.Forward);
    }

    [Theory]
    [InlineData(Bob, Alice)]
    [InlineData(Bob + "#key-x25519-1", Alice + "#key-x25519-9")]
    [InlineData(Bob + "/path?query=1", Alice + "#frag")]
    public void Identity_rule_compares_DID_subjects_so_a_DID_URL_of_the_same_subject_matches(string expectedPeer, string expectedLocal)
    {
        // L-034: both sides are DID subjects, so a DID URL of the same subject matches.
        ReturnRouteIdentity.IsFromPeerToLocal(Returned(), expectedPeer, expectedLocal, out var refusal).Should().BeTrue();
        refusal.Should().Be(ReturnedMessageRefusal.None);
    }

    [Theory]
    [InlineData("not-a-did", Alice, nameof(ReturnedMessageRefusal.PeerMismatch))]
    [InlineData(Bob, "", nameof(ReturnedMessageRefusal.LocalMismatch))]
    [InlineData("did:example:bobby", "did:example:alicia", nameof(ReturnedMessageRefusal.PeerAndLocalMismatch))]
    public void Identity_rule_fails_closed_on_an_unparseable_or_prefix_expected_value(string expectedPeer, string expectedLocal, string expected)
    {
        ReturnRouteIdentity.IsFromPeerToLocal(Returned(), expectedPeer, expectedLocal, out var refusal).Should().BeFalse();
        refusal.ToString().Should().Be(expected);
    }

    // ---- FR-RR-08 replay gate (shared with the receive side) ----

    public static TheoryData<string, UnpackResult, bool> ReplayScope()
    {
        var data = new TheoryData<string, UnpackResult, bool>
        {
            { "all", Returned(header: ReturnRouteMode.All), true },
            { "thread", Returned(header: ReturnRouteMode.Thread), true },
            { "none + app type", Returned(header: ReturnRouteMode.None), false },
            { "absent + app type", Returned(), false },
            { "forward with all", Returned(type: "https://didcomm.org/routing/2.0/forward", header: ReturnRouteMode.All), false },
            { "signed-only with all", Returned(encrypted: false, senderKid: null, signerKid: Bob + "#k", recipientKid: null, header: ReturnRouteMode.All), false },
            { "anoncrypt with all", Returned(authenticated: false, senderKid: null, header: ReturnRouteMode.All), false },
            { "anoncrypt(sign) with all", Returned(senderKid: null, signerKid: Bob + "#k", header: ReturnRouteMode.All), false },
            { "signed-only pickup", Returned(encrypted: false, senderKid: null, signerKid: Bob + "#k", recipientKid: null, type: "https://didcomm.org/messagepickup/3.0/status"), false },
        };
        foreach (var piuri in MediatorServiceProtocols.All)
        {
            // Any minor version of the four mediator-service protocols is in scope without a header.
            data.Add(piuri + " (header-less)", Returned(type: piuri + "/any-message"), true);
            data.Add(piuri.Replace(".0", ".7", StringComparison.Ordinal) + " (other minor)", Returned(type: piuri.Replace(".0", ".7", StringComparison.Ordinal) + "/any-message"), true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ReplayScope))]
    public void Replay_scope_table_matches_the_receive_side(string label, UnpackResult unpacked, bool inScope)
    {
        InboundReplayGate.IsInScope(unpacked, ReturnRouteHeader.Read(unpacked.Message)).Should().Be(inScope, label);
    }

    [Fact]
    public void Replay_gate_outcomes()
    {
        var clockReads = 0;
        var options = new DidCommOptions { Clock = () => { clockReads++; return DateTimeOffset.FromUnixTimeSeconds(1_800_000_000); } };

        // Out of scope: the guard is not called and the clock is not read.
        var guard = new ScriptedGuard(ReplayAdmission.Admitted);
        var outOfScope = InboundReplayGate.Run(Returned(), ReturnRouteHeader.None, guard, options);
        outOfScope.Should().Be(new ReplayGateResult(ReplayGateOutcome.OutOfScope, null, null));
        outOfScope.Proceeds.Should().BeTrue();
        guard.Calls.Should().BeEmpty();
        clockReads.Should().Be(0, "the clock is read only for an in-scope message");

        // Admitted: keyed on the sender key id's subject, with the message's created_time and one clock read.
        var admitted = InboundReplayGate.Run(Returned(header: ReturnRouteMode.All, createdTime: 1_799_999_990), new ReturnRouteHeader(ReturnRouteMode.All, null), guard, options);
        admitted.Should().Be(new ReplayGateResult(ReplayGateOutcome.Admitted, ReplayAdmission.Admitted, null));
        admitted.Proceeds.Should().BeTrue();
        guard.Calls.Should().ContainSingle().Which.Should().Be((Bob, "msg-1", (long?)1_799_999_990, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)));
        clockReads.Should().Be(1);

        // Every non-Admitted answer is Refused, carrying the guard's answer.
        foreach (var answer in Enum.GetValues<ReplayAdmission>().Where(a => a != ReplayAdmission.Admitted))
        {
            var refused = InboundReplayGate.Run(Returned(header: ReturnRouteMode.All), new ReturnRouteHeader(ReturnRouteMode.All, null), new ScriptedGuard(answer), options);
            refused.Should().Be(new ReplayGateResult(ReplayGateOutcome.Refused, answer, null), answer.ToString());
            refused.Proceeds.Should().BeFalse();
        }

        // A throwing guard fails closed with the exception type name only.
        var fault = InboundReplayGate.Run(Returned(header: ReturnRouteMode.All), new ReturnRouteHeader(ReturnRouteMode.All, null), new ScriptedGuard(throws: true), options);
        fault.Should().Be(new ReplayGateResult(ReplayGateOutcome.GuardFault, null, nameof(InvalidTimeZoneException)));
        fault.Proceeds.Should().BeFalse();

        // A sender key id with no DID subject, or an empty id, never reaches the guard.
        var noSubjectGuard = new ScriptedGuard(ReplayAdmission.Admitted);
        InboundReplayGate.Run(Returned(senderKid: "urn:not-a-did#k", header: ReturnRouteMode.All), new ReturnRouteHeader(ReturnRouteMode.All, null), noSubjectGuard, options)
            .Outcome.Should().Be(ReplayGateOutcome.NoSenderSubject);
        var emptyId = Returned(header: ReturnRouteMode.All);
        emptyId.Message.Id = string.Empty;
        InboundReplayGate.Run(emptyId, new ReturnRouteHeader(ReturnRouteMode.All, null), noSubjectGuard, options)
            .Outcome.Should().Be(ReplayGateOutcome.NoSenderSubject);
        noSubjectGuard.Calls.Should().BeEmpty();
    }

    // ---- FR-RR-09 stamping scope ----

    public static TheoryData<string, Message, bool> CreatedTimeScope()
    {
        Message Of(string type, Action<Message>? edit = null)
        {
            var m = new MessageBuilder().WithType(type).WithFrom(Alice).WithTo(Bob).Build();
            edit?.Invoke(m);
            return m;
        }
        Message WithHeader(string type, JsonElement value) => Of(type, m =>
        {
            m.AdditionalHeaders ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            m.AdditionalHeaders[ReturnRouteHeader.HeaderName] = value;
        });

        return new TheoryData<string, Message, bool>
        {
            { "app + all", new MessageBuilder().WithType(AppType).WithReturnRoute(ReturnRouteMode.All).Build(), true },
            { "app + thread", new MessageBuilder().WithType(AppType).WithReturnRoute(ReturnRouteMode.Thread, "thr-1").Build(), true },
            { "app + none", new MessageBuilder().WithType(AppType).WithReturnRoute(ReturnRouteMode.None).Build(), false },
            { "app + malformed (ALL)", WithHeader(AppType, JsonSerializer.SerializeToElement("ALL")), false },
            { "app + malformed (number)", WithHeader(AppType, JsonSerializer.SerializeToElement(1)), false },
            { "app + thread without return_route_thread", WithHeader(AppType, JsonSerializer.SerializeToElement("thread")), false },
            { "app, no header", Of(AppType), false },
            { "trust ping, no header", Of("https://didcomm.org/trust-ping/2.0/ping"), false },
            { "CM 2.0 mediate-request", Of("https://didcomm.org/coordinate-mediation/2.0/mediate-request"), true },
            { "CM 3.0 recipient-update", Of("https://didcomm.org/coordinate-mediation/3.0/recipient-update"), true },
            { "pickup 3.0 delivery-request", Of("https://didcomm.org/messagepickup/3.0/delivery-request"), true },
            { "pickup 4.0 messages-received (header-less)", Of("https://didcomm.org/message-pickup/4.0/messages-received"), true },
            { "pickup 4.1 live-delivery-change", Of("https://didcomm.org/message-pickup/4.1/live-delivery-change"), true },
            { "forward, no header", Of("https://didcomm.org/routing/2.0/forward"), false },
        };
    }

    [Theory]
    [MemberData(nameof(CreatedTimeScope))]
    public void Created_time_scope_table(string label, Message message, bool inScope)
    {
        OutgoingCreatedTime.IsInScope(message).Should().Be(inScope, label);

        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_123);
        OutgoingCreatedTime.StampIfInScope(message, now);
        message.CreatedTime.Should().Be(inScope ? 1_800_000_123 : null, label);
    }

    [Theory]
    [InlineData(1_800_000_000 - 600)] // ten minutes old: still never refreshed
    [InlineData(1_800_000_000 + 3600)] // future-dated: the receiver judges it
    [InlineData(0)]
    public void StampIfInScope_never_refreshes_a_caller_value(long callerValue)
    {
        var message = new MessageBuilder()
            .WithType("https://didcomm.org/messagepickup/3.0/status-request")
            .WithReturnRoute(ReturnRouteMode.All)
            .WithCreatedTime(callerValue)
            .Build();

        OutgoingCreatedTime.StampIfInScope(message, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));

        message.CreatedTime.Should().Be(callerValue);
    }

    // ---- the shared header writer ----

    [Theory]
    [InlineData(ReturnRouteMode.None, null)]
    [InlineData(ReturnRouteMode.All, null)]
    [InlineData(ReturnRouteMode.Thread, "thr-42")]
    public void Header_writer_matches_WithReturnRoute(ReturnRouteMode mode, string? thread)
    {
        // Both start with a stale return_route_thread, which every mode but Thread removes.
        var viaBuilder = Base().WithReturnRoute(ReturnRouteMode.Thread, "stale").WithReturnRoute(mode, thread).Build();
        var viaWriter = Base().WithReturnRoute(ReturnRouteMode.Thread, "stale").Build();
        ReturnRouteHeaderWriter.Write(viaWriter, mode, thread);

        var builderJson = JsonSerializer.Serialize(viaBuilder, DidCommJson.Default);
        var writerJson = JsonSerializer.Serialize(viaWriter, DidCommJson.Default);
        writerJson.Should().Be(builderJson);
        writerJson.Should().Contain($"\"return_route\":\"{mode.ToString().ToLowerInvariant()}\"");
        if (mode == ReturnRouteMode.Thread)
            writerJson.Should().Contain("\"return_route_thread\":\"thr-42\"").And.NotContain("stale");
        else
            writerJson.Should().NotContain("return_route_thread");

        static MessageBuilder Base() => new MessageBuilder().WithId("fixed-id").WithType(AppType).WithFrom(Alice).WithTo(Bob);
    }

    [Theory]
    [InlineData(ReturnRouteMode.Thread, null)]
    [InlineData(ReturnRouteMode.Thread, " ")]
    [InlineData(ReturnRouteMode.Thread, "has space")]
    [InlineData(ReturnRouteMode.All, "thr-1")]
    [InlineData(ReturnRouteMode.None, "thr-1")]
    public void Header_writer_validates_and_writes_nothing_on_failure(ReturnRouteMode mode, string? thread)
    {
        var message = new MessageBuilder().WithType(AppType).Build();

        var validate = () => ReturnRouteHeaderWriter.Validate(mode, thread, "options", "options");
        var write = () => ReturnRouteHeaderWriter.Write(message, mode, thread, "options", "options");

        validate.Should().Throw<ArgumentException>().WithParameterName("options");
        write.Should().Throw<ArgumentException>().WithParameterName("options");
        message.AdditionalHeaders.Should().BeNull("nothing is written when validation fails");
    }

    [Fact]
    public void Header_writer_refuses_an_undefined_mode_and_a_2049_character_thread_without_quoting_it()
    {
        var undefined = () => ReturnRouteHeaderWriter.Validate((ReturnRouteMode)7, null);
        undefined.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("mode");

        var longThread = new string('t', ReturnRouteHeader.MaxThreadLength + 1);
        var tooLong = () => ReturnRouteHeaderWriter.Validate(ReturnRouteMode.Thread, longThread);
        tooLong.Should().Throw<ArgumentException>().WithParameterName("thread")
            .Which.Message.Should().NotContain(new string('t', 64));
        ReturnRouteHeaderWriter.Validate(ReturnRouteMode.Thread, new string('t', ReturnRouteHeader.MaxThreadLength));
    }

    private sealed class ScriptedGuard : IInboundReplayGuard
    {
        private readonly ReplayAdmission _answer;
        private readonly bool _throws;

        public ScriptedGuard(ReplayAdmission answer = ReplayAdmission.Admitted, bool throws = false)
        {
            _answer = answer;
            _throws = throws;
        }

        public List<(string Peer, string Id, long? Created, DateTimeOffset Now)> Calls { get; } = new();

        public ReplayAdmission TryAdmit(string peerDidSubject, string messageId, long? createdTimeEpochSeconds, DateTimeOffset now)
        {
            Calls.Add((peerDidSubject, messageId, createdTimeEpochSeconds, now));
            if (_throws)
                throw new InvalidTimeZoneException("guard fault");
            return _answer;
        }
    }
}
