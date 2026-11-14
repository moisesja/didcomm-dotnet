using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Tests.Facade;
using DidComm.Transports.ReturnRoute;
using Xunit;
using static DidComm.Tests.Facade.MediatedClientTestWorld;

namespace DidComm.Tests.Transports;

/// <summary>
/// #96 design §3.1 item 2 (L-041): the snapshot overload of
/// <see cref="ReturnRouteEligibility.TryGetPeer(InboundMessageSnapshot, out string, out string, out ReturnRouteIneligibility)"/>
/// applies the same rule as the <see cref="UnpackResult"/> overload. Both run over the same real
/// unpack results (the verified snapshot unpack registered for each) and over hand-built results
/// (the fallback snapshot of each), and must agree on the answer, both DID subjects and the reason.
/// </summary>
public sealed class ReturnRouteEligibilitySnapshotParityTests
{
    private readonly MediatedClientTestWorld _world = new();

    public static TheoryData<string> Shapes => new()
    {
        "authcrypt",
        "anoncrypt",
        "signed-only",
        "anoncrypt(sign)",
        "authcrypt(sign)",
        "anoncrypt(authcrypt)",
        "plaintext",
        "forward",
        "authcrypt to Carol",
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Both_overloads_agree_on_a_real_unpack_result(string shape)
    {
        var result = await _world.BobClient().UnpackAsync(await PackAsync(shape));
        InboundMessageSnapshot.TryGetFor(result.Message, out var snapshot).Should().BeTrue("unpack registers a verified snapshot");
        snapshot.Verified.Should().BeTrue();

        AssertParity(result, snapshot, shape);
    }

    [Fact]
    public async Task The_expected_answers_hold_for_the_real_shapes()
    {
        // Pin the rule itself, so parity cannot hold by both overloads being wrong the same way.
        (await Reason("authcrypt")).Should().Be(ReturnRouteIneligibility.None);
        (await Reason("authcrypt(sign)")).Should().Be(ReturnRouteIneligibility.None);
        (await Reason("anoncrypt(authcrypt)")).Should().Be(ReturnRouteIneligibility.None);
        (await Reason("authcrypt to Carol")).Should().Be(ReturnRouteIneligibility.None);
        (await Reason("anoncrypt")).Should().Be(ReturnRouteIneligibility.NotEncryptedAndAuthenticated);
        (await Reason("signed-only")).Should().Be(ReturnRouteIneligibility.NotEncryptedAndAuthenticated);
        (await Reason("anoncrypt(sign)")).Should().Be(ReturnRouteIneligibility.NoSenderKid);
        (await Reason("plaintext")).Should().Be(ReturnRouteIneligibility.NotEncryptedAndAuthenticated);
        (await Reason("forward")).Should().Be(ReturnRouteIneligibility.Forward);
    }

    public static TheoryData<string, Func<UnpackResult, UnpackResult>> HandBuilt => new()
    {
        { "signer disagrees with sender", r => r with { SignerKid = Mallory + "#s1" } },
        { "signer kid not a DID", r => r with { SignerKid = "s1" } },
        { "sender kid not a DID", r => r with { SenderKid = "x1" } },
        { "no sender kid", r => r with { SenderKid = null } },
        { "recipient kid not a DID", r => r with { RecipientKid = "x1" } },
        { "no recipient kid", r => r with { RecipientKid = null } },
        { "claims authentication without encryption", r => r with { Encrypted = false } },
        { "unchanged", r => r },
    };

    [Theory]
    [MemberData(nameof(HandBuilt))]
    public async Task Both_overloads_agree_on_a_hand_built_result_through_its_fallback_snapshot(string name, Func<UnpackResult, UnpackResult> change)
    {
        var real = await _world.BobClient().UnpackAsync(await PackAsync("authcrypt(sign)"));
        var built = change(real);
        var snapshot = InboundMessageSnapshot.CreateFallback(built);
        snapshot.Verified.Should().BeFalse();

        AssertParity(built, snapshot, name);
    }

    [Fact]
    public async Task A_disagreeing_signer_is_refused_by_both()
    {
        var real = await _world.BobClient().UnpackAsync(await PackAsync("authcrypt(sign)"));
        var forged = real with { SignerKid = Mallory + "#s1" };

        ReturnRouteEligibility.TryGetPeer(forged, out _, out _, out var fromResult).Should().BeFalse();
        ReturnRouteEligibility.TryGetPeer(InboundMessageSnapshot.CreateFallback(forged), out _, out _, out var fromSnapshot).Should().BeFalse();

        fromResult.Should().Be(ReturnRouteIneligibility.SignerMismatch);
        fromSnapshot.Should().Be(ReturnRouteIneligibility.SignerMismatch);
    }

    private async Task<ReturnRouteIneligibility> Reason(string shape)
    {
        var result = await _world.BobClient().UnpackAsync(await PackAsync(shape));
        ReturnRouteEligibility.TryGetPeer(result, out _, out _, out var reason);
        return reason;
    }

    private static void AssertParity(UnpackResult result, InboundMessageSnapshot snapshot, string because)
    {
        var fromResult = ReturnRouteEligibility.TryGetPeer(result, out var peerA, out var localA, out var reasonA);
        var fromSnapshot = ReturnRouteEligibility.TryGetPeer(snapshot, out var peerB, out var localB, out var reasonB);

        fromSnapshot.Should().Be(fromResult, because);
        peerB.Should().Be(peerA, because);
        localB.Should().Be(localA, because);
        reasonB.Should().Be(reasonA, because);
    }

    private async Task<string> PackAsync(string shape)
    {
        var note = Note();
        var senders = _world.Senders();
        return shape switch
        {
            "authcrypt" => await _world.AuthcryptAsync(note),
            "anoncrypt" => await _world.AnoncryptAsync(note),
            "signed-only" => await senders.PackSignedAsync(note, Alice),
            "anoncrypt(sign)" => await _world.AnoncryptSignedAsync(note, signFrom: Alice),
            "authcrypt(sign)" => await _world.AuthcryptAsync(note, signFrom: Alice),
            "anoncrypt(authcrypt)" => (await senders.PackEncryptedAsync(note, new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice, ProtectSender: true))).Message,
            "plaintext" => await senders.PackPlaintextAsync(note),
            "forward" => await _world.AuthcryptAsync(ForwardMessage.Create(Bob, Bob, new[] { await _world.AnoncryptAsync(note) }) is var f ? WithFrom(f, Alice) : null!),
            "authcrypt to Carol" => await _world.AuthcryptAsync(Note(to: Carol), to: Carol),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    private static Message WithFrom(Message message, string from)
    {
        message.From = from;
        return message;
    }
}
