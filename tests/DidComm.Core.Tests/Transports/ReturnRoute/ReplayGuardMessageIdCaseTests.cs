using DidComm.Facade;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Transports.ReturnRoute;

/// <summary>
/// FR-RR-08 keys admission on (sender DID subject, message id). DIDComm v2.1 threading (threading.md
/// L9) says an <c>id</c> "MUST be compared case-insensitively", so an id that differs from an admitted
/// one only in letter case is the same id: refused as a duplicate. The sender subject keeps its exact
/// comparison, and ids that differ in more than case stay distinct.
/// </summary>
public sealed class ReplayGuardMessageIdCaseTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";
    private const string Lower = "6d0f8a3c-replay-abc";
    private const string Upper = "6D0F8A3C-REPLAY-ABC";

    private static InMemoryReplayGuard Guard()
        => new(new ReplayProtectionOptions { ClockSkew = TimeSpan.Zero });

    private static long S(DateTimeOffset t) => t.ToUnixTimeSeconds();

    [Fact]
    public void An_id_that_differs_from_an_admitted_one_only_in_case_is_a_duplicate()
    {
        var g = Guard();

        var first = g.TryAdmit(Alice, Lower, S(T), T);
        var upper = g.TryAdmit(Alice, Upper, S(T), T);
        var mixed = g.TryAdmit(Alice, "6d0F8a3C-Replay-aBC", S(T), T);

        first.Should().Be(ReplayAdmission.Admitted);
        upper.Should().Be(ReplayAdmission.Duplicate);
        mixed.Should().Be(ReplayAdmission.Duplicate);
        g.Count.Should().Be(1, "a duplicate is neither inserted nor refreshed");
    }

    [Fact]
    public void Ids_that_differ_in_more_than_case_and_the_sender_subject_keep_their_exact_comparison()
    {
        // Controls: another id from the same sender, and the same id from a sender whose subject
        // differs only in case (a DID subject is compared exactly), are both admitted.
        var g = Guard();
        g.TryAdmit(Alice, Lower, S(T), T).Should().Be(ReplayAdmission.Admitted);

        var otherId = g.TryAdmit(Alice, Lower + "0", S(T), T);
        var otherSender = g.TryAdmit(Bob, Upper, S(T), T);
        var senderInOtherCase = g.TryAdmit("did:example:ALICE", Lower, S(T), T);

        otherId.Should().Be(ReplayAdmission.Admitted);
        otherSender.Should().Be(ReplayAdmission.Admitted);
        senderInOtherCase.Should().Be(ReplayAdmission.Admitted);
        g.Count.Should().Be(4);
        g.PeerCount.Should().Be(3);
    }

    [Fact]
    public void Once_the_entry_expires_the_case_variant_is_admitted_and_the_heap_and_buckets_stay_in_step()
    {
        // The purge removes the bucket entry by the id it stored; the case-insensitive bucket then
        // admits the other casing as a fresh entry, and the count follows (no orphan either way).
        var g = Guard();
        g.TryAdmit(Alice, Lower, S(T), T).Should().Be(ReplayAdmission.Admitted);
        var later = T.AddSeconds(301);

        var again = g.TryAdmit(Alice, Upper, S(later), later);
        var replay = g.TryAdmit(Alice, Lower, S(later), later);

        again.Should().Be(ReplayAdmission.Admitted);
        replay.Should().Be(ReplayAdmission.Duplicate);
        g.Count.Should().Be(1);
        g.PeerCount.Should().Be(1);
    }

    // ---- through the gate the endpoints and the client's inbound consumer run -----------------

    private static UnpackResult Authcrypted(string id) => new(
        Message: new MessageBuilder().WithId(id).WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(Bob).WithTo(Alice).WithCreatedTime(S(T)).WithReturnRoute(ReturnRouteMode.All).Build(),
        Stack: Array.Empty<DidComm.Jose.EnvelopeKind>(),
        Encrypted: true,
        Authenticated: true,
        NonRepudiation: false,
        AnonymousSender: false,
        ContentEncryption: null,
        KeyWrap: null,
        SignatureAlgorithm: null,
        SignerKid: null,
        SenderKid: Bob + "#key-x25519-1",
        RecipientKid: Alice + "#key-x25519-1",
        AllRecipientKids: new[] { Alice + "#key-x25519-1" },
        FromPrior: null);

    [Fact]
    public void The_replay_gate_refuses_a_message_whose_id_differs_from_an_admitted_one_only_in_case()
    {
        var guard = Guard();
        var options = new DidCommOptions { Clock = () => T };
        var header = new ReturnRouteHeader(ReturnRouteMode.All, null);

        var first = InboundReplayGate.Run(Authcrypted(Lower), header, guard, options);
        var caseVariant = InboundReplayGate.Run(Authcrypted(Upper), header, guard, options);
        var other = InboundReplayGate.Run(Authcrypted(Lower + "0"), header, guard, options);

        first.Should().Be(new ReplayGateResult(ReplayGateOutcome.Admitted, ReplayAdmission.Admitted, null));
        caseVariant.Should().Be(new ReplayGateResult(ReplayGateOutcome.Refused, ReplayAdmission.Duplicate, null));
        other.Should().Be(new ReplayGateResult(ReplayGateOutcome.Admitted, ReplayAdmission.Admitted, null), "an id that differs in more than case is another message");
    }
}
