using System.Reflection;
using System.Text;
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

    private static InboundMessageSnapshot RegisteredSnapshot()
    {
        var message = new Message { Id = "m1", Type = "https://didcomm.org/x/1.0/m" };
        InboundMessageSnapshot.RegisterVerified(
            message, PlaintextJson,
            encrypted: false, authenticated: false, nonRepudiation: false, anonymousSender: false,
            senderKid: null, signerKid: null, recipientKid: null);
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
}
