using System.Reflection;
using System.Text;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Protocols.Dispatch;

/// <summary>
/// #53: the unpack path registers a snapshot for every inbound message, but only
/// ObserverDelivery's byte-budget admission ever reads its UTF-8 size. These tests pin the
/// lazy contract: registration alone must not scan the plaintext, and the first read must
/// produce the exact byte count and memoize it.
/// </summary>
public sealed class InboundMessageSnapshotTests
{
    // Multibyte content so byte count != char count and the assertions below are meaningful.
    private const string PlaintextJson =
        /*lang=json,strict*/ """{"id":"m1","type":"https://didcomm.org/x/1.0/m","body":{"note":"héllo — ✓"}}""";

    private static readonly FieldInfo ByteCountField =
        typeof(InboundMessageSnapshot).GetField("_utf8ByteCount", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(
            "InboundMessageSnapshot._utf8ByteCount not found — if the lazy backing field was " +
            "renamed, update this test so it keeps proving the unpack path does not scan the plaintext.");

    private static void Register(Message message, string plaintextJson) =>
        InboundMessageSnapshot.RegisterVerified(
            message, plaintextJson,
            encrypted: false, authenticated: false, nonRepudiation: false, anonymousSender: false,
            senderKid: null, signerKid: null, recipientKid: null,
            recipientAddressing: RecipientAddressing.NotEvaluated);

    private static InboundMessageSnapshot RegisteredSnapshot()
    {
        var message = new Message { Id = "m1", Type = "https://didcomm.org/x/1.0/m" };
        Register(message, PlaintextJson);
        InboundMessageSnapshot.TryGetFor(message, out var snapshot).Should().BeTrue();
        return snapshot;
    }

    [Fact]
    public void RegisterVerified_DoesNotScanPlaintextForByteCount()
    {
        var snapshot = RegisteredSnapshot();

        ByteCountField.GetValue(snapshot).Should().Be(-1,
            "the unpack-only path must not pay an O(plaintext) byte-count scan (#53)");
    }

    [Fact]
    public void Utf8ByteCount_FirstRead_ReturnsExactUtf8Size()
    {
        var snapshot = RegisteredSnapshot();

        var expected = Encoding.UTF8.GetByteCount(PlaintextJson);
        expected.Should().BeGreaterThan(PlaintextJson.Length, "the payload must contain multibyte characters");
        snapshot.Utf8ByteCount.Should().Be(expected);
    }

    [Fact]
    public void Utf8ByteCount_IsMemoizedAfterFirstRead()
    {
        var snapshot = RegisteredSnapshot();

        var first = snapshot.Utf8ByteCount;

        ByteCountField.GetValue(snapshot).Should().Be(first, "the first read must cache the computed size");
        snapshot.Utf8ByteCount.Should().Be(first);
    }

    [Fact]
    public void RegisterVerified_TwiceForTheSameMessage_ThrowsInsteadOfOverwriting()
    {
        // #63 positive control, in two parts.
        // (1) It documents the deliberate choice of ConditionalWeakTable.Add over AddOrUpdate: the
        //     key can only ever be a fresh per-unpack instance, so overwrite semantics buy nothing —
        //     but if a second registration site ever appeared, silent overwrite would let a later,
        //     weaker registration replace verified trust metadata. Failing fast is the safe posture.
        // (2) It proves the registration-count assertions in SnapshotRegistrationTests are not
        //     vacuous: a duplicate registration really is detectable, so their silence means "one".
        var message = new Message { Id = "m1", Type = "https://didcomm.org/x/1.0/m" };
        Register(message, PlaintextJson);

        var second = () => Register(message, /*lang=json,strict*/ """{"id":"m1","type":"https://didcomm.org/x/1.0/m","body":{}}""");

        second.Should().Throw<ArgumentException>(
            "a second registration for the same message must fail loudly rather than silently " +
            "replace the verified snapshot (#63)");
        InboundMessageSnapshot.TryGetFor(message, out var snapshot).Should().BeTrue();
        snapshot.PlaintextJson.Should().Be(PlaintextJson, "the first, verified registration must survive");
    }

    [Fact]
    public void CreateFallback_ReportsNotEvaluatedEvenWhenTheResultClaimsAddressed()
    {
        // The synthetic path never evaluated 'to', so the result's claimed outcome is refused —
        // same rule as the #56 key bindings.
        var synthetic = new UnpackResult(
            Message: new Message { Id = "m1", Type = "https://didcomm.org/x/1.0/m" },
            Stack: Array.Empty<DidComm.Jose.EnvelopeKind>(),
            Encrypted: false,
            Authenticated: false,
            NonRepudiation: false,
            AnonymousSender: false,
            ContentEncryption: null,
            KeyWrap: null,
            SignatureAlgorithm: null,
            SignerKid: null,
            SenderKid: null,
            RecipientKid: null,
            AllRecipientKids: Array.Empty<string>(),
            FromPrior: null)
        {
            RecipientAddressing = RecipientAddressing.Addressed,
        };

        InboundMessageSnapshot.CreateFallback(synthetic).RecipientAddressing.Should().Be(
            RecipientAddressing.NotEvaluated);
    }
}
