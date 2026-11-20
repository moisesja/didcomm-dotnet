using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The pickup client's duplicate filter keys an authenticated message by (sender DID subject,
/// decrypting DID subject, message id). DIDComm v2.1 threading (threading.md L9) says an <c>id</c>
/// "MUST be compared case-insensitively", so two messages from one sender whose ids differ only in
/// letter case are one message: the second is acknowledged but not handed out again. The DID
/// subjects keep their exact comparison, and ids that differ in more than case stay distinct.
/// </summary>
public sealed class DuplicateFilterMessageIdCaseTests
{
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";

    [Fact]
    public void Ids_from_one_sender_that_differ_only_in_case_give_one_key()
    {
        var lower = DuplicateFilter.KeyOf(Authcrypted(Alice, Bob, "5b2e9c41-inner-abc"), "packed-1");
        var upper = DuplicateFilter.KeyOf(Authcrypted(Alice, Bob, "5B2E9C41-INNER-ABC"), "packed-2");
        var mixed = DuplicateFilter.KeyOf(Authcrypted(Alice, Bob, "5b2E9c41-Inner-aBc"), "packed-3");

        upper.Should().Be(lower);
        mixed.Should().Be(lower);
    }

    [Fact]
    public void Ids_that_differ_in_more_than_case_and_senders_in_another_case_give_other_keys()
    {
        // Controls: another id, and the same id from a DID in another case (DIDs compare exactly).
        var key = DuplicateFilter.KeyOf(Authcrypted(Alice, Bob, "5b2e9c41-inner-abc"), "p");

        var otherId = DuplicateFilter.KeyOf(Authcrypted(Alice, Bob, "5B2E9C41-INNER-ABD"), "p");
        var otherSenderCase = DuplicateFilter.KeyOf(Authcrypted("did:example:ALICE", Bob, "5b2e9c41-inner-abc"), "p");
        var otherRecipientCase = DuplicateFilter.KeyOf(Authcrypted(Alice, "did:example:BOB", "5b2e9c41-inner-abc"), "p");

        otherId.Should().NotBe(key);
        otherSenderCase.Should().NotBe(key);
        otherRecipientCase.Should().NotBe(key);
    }

    [Fact]
    public async Task A_fetch_hands_out_a_message_whose_id_differs_from_an_earlier_one_only_in_case_once_and_acknowledges_both()
    {
        // Through the real pickup client: the default fake unpacker opens "ok:<id>" as an authcrypted
        // message from Alice to Bob's local DID with that inner id.
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => RawDelivery(r, new Attachment?[]
        {
            Entry("m1", "ok:5b2e9c41-inner-abc"),
            Entry("m2", "ok:5B2E9C41-INNER-ABC"),
            Entry("m3", "ok:5b2e9c41-inner-abd"),
        }));

        var batch = await PickupTest.Within(PickupTest.Client(exchange).FetchAsync(M, L, limit: 10));

        batch.Items.Select(i => i.Id).Should().Equal(new[] { "m1", "m3" }, "m2 repeats m1's id in other letter case, so it is the same message");
        exchange.AcknowledgedIds.Should().BeEquivalentTo(new[] { "m1", "m2", "m3" }, "a repeat is still acknowledged so the mediator stops redelivering it");
    }

    private static UnpackResult Authcrypted(string sender, string recipient, string id) => new(
        Message: new Message { Id = id, Type = "https://didcomm.org/basicmessage/2.0/message" },
        Stack: Array.Empty<EnvelopeKind>(),
        Encrypted: true,
        Authenticated: true,
        NonRepudiation: false,
        AnonymousSender: false,
        ContentEncryption: null,
        KeyWrap: null,
        SignatureAlgorithm: null,
        SignerKid: null,
        SenderKid: sender + "#key-1",
        RecipientKid: recipient + "#key-1",
        AllRecipientKids: new[] { recipient + "#key-1" },
        FromPrior: null);
}
