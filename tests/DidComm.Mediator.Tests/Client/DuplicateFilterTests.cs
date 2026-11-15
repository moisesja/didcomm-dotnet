using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Mediator.Client.Internal;
using DidComm.Messages;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The pickup client's duplicate filter (#96 design D-15, §4.9): keys bind a message id to its
/// authenticated sender (a stranger reusing an id cannot suppress someone else's message), an
/// anonymous message is keyed by its bytes, a message is remembered per mailbox it was fetched from
/// (ADV1-1), eviction is FIFO at the capacity, capacity 0 is off, and retained memory does not grow
/// with the wire values (L-062).
/// </summary>
public sealed class DuplicateFilterTests
{
    private const string Alice = "did:example:alice";
    private const string Mallory = "did:example:mallory";
    private const string Bob = "did:example:bob";

    [Fact]
    public void An_authenticated_message_is_keyed_by_sender_recipient_and_id()
    {
        var a = Authcrypt(Alice, Bob, "id-1");
        DuplicateFilter.KeyOf(a, "packed-1").Should().Be(DuplicateFilter.KeyOf(Authcrypt(Alice, Bob, "id-1"), "other bytes"),
            "a redelivery of the same message from the same sender matches whatever its envelope bytes");
        DuplicateFilter.KeyOf(a, "packed-1").Should().NotBe(DuplicateFilter.KeyOf(Authcrypt(Mallory, Bob, "id-1"), "packed-1"),
            "message ids are unique per sender only: Mallory reusing Alice's id must not suppress Alice's message");
        DuplicateFilter.KeyOf(a, "packed-1").Should().NotBe(DuplicateFilter.KeyOf(Authcrypt(Alice, "did:example:carol", "id-1"), "packed-1"));
        DuplicateFilter.KeyOf(a, "packed-1").Should().NotBe(DuplicateFilter.KeyOf(Authcrypt(Alice, Bob, "id-2"), "packed-1"));
    }

    [Fact]
    public void Sender_key_ids_of_one_DID_give_one_key()
    {
        DuplicateFilter.KeyOf(Authcrypt(Alice + "#key-1", Bob + "#key-x", "id-1"), "p")
            .Should().Be(DuplicateFilter.KeyOf(Authcrypt(Alice + "#key-2", Bob + "#key-y", "id-1"), "q"),
                "the key is over DID subjects, not key ids (PRD §4.3)");
    }

    [Fact]
    public void A_signed_message_is_keyed_by_its_signer()
    {
        var signed = Result("id-1", authenticated: false, nonRepudiation: true, senderKid: null, signerKid: Alice + "#sig", recipientKid: Bob + "#k");
        DuplicateFilter.KeyOf(signed, "p").Should().Be(DuplicateFilter.KeyOf(signed with { }, "different bytes"));
        DuplicateFilter.KeyOf(signed, "p").Should().NotBe(
            DuplicateFilter.KeyOf(Result("id-1", false, true, null, Mallory + "#sig", Bob + "#k"), "p"));
    }

    [Fact]
    public void An_anonymous_message_is_keyed_by_its_bytes_not_its_id()
    {
        var anon = Result("id-1", authenticated: false, nonRepudiation: false, senderKid: null, signerKid: null, recipientKid: Bob + "#k");
        DuplicateFilter.KeyOf(anon, "bytes-1").Should().Be(DuplicateFilter.KeyOf(anon, "bytes-1"));
        DuplicateFilter.KeyOf(anon, "bytes-1").Should().NotBe(DuplicateFilter.KeyOf(anon, "bytes-2"),
            "without an authenticated sender anyone could send the same id; only the same bytes are the same message");
        DuplicateFilter.KeyOf(anon, "bytes-1").Should().NotBe(DuplicateFilter.KeyOf(Authcrypt(Alice, Bob, "id-1"), "bytes-1"));
    }

    [Fact]
    public void Fields_are_length_prefixed()
    {
        DuplicateFilter.KeyOf(Authcrypt("did:example:ab", Bob, "c"), "p")
            .Should().NotBe(DuplicateFilter.KeyOf(Authcrypt("did:example:a", Bob, "bc"), "p"));
    }

    // ---- the mailbox scope (ADV1-1) ----

    [Fact]
    public void A_message_is_remembered_per_mailbox()
    {
        // A copy of T's message that a third party relayed into F's mailbox, once returned from F's
        // mailbox, must not make T's own copy a repeat: the same message key, another mailbox.
        const string f = "did:example:f";
        const string t = "did:example:t";
        var filter = new DuplicateFilter(8);
        var message = DuplicateFilter.KeyOf(Authcrypt(Alice, t, "id-1"), "p");

        filter.Add(f, message);

        filter.Contains(f, message).Should().BeTrue("returned from F's mailbox once: a repeat there");
        filter.Contains(t, message).Should().BeFalse("T never received it from its own mailbox");
        DuplicateFilter.InMailbox(f, message).Should().NotBe(DuplicateFilter.InMailbox(t, message));
    }

    [Fact]
    public void The_mailbox_scope_is_the_DID_subject_and_the_message_keys_stay_distinct()
    {
        var one = DuplicateFilter.KeyOf(Authcrypt(Alice, Bob, "id-1"), "p");
        var two = DuplicateFilter.KeyOf(Authcrypt(Alice, Bob, "id-2"), "p");

        DuplicateFilter.InMailbox(Bob, one).Should().Be(DuplicateFilter.InMailbox(Bob, one));
        DuplicateFilter.InMailbox(Bob, one).Should().NotBe(DuplicateFilter.InMailbox(Bob, two));
        DuplicateFilter.InMailbox(Bob, one).Should().NotBe(one, "the remembered key is not the bare message key");
        DuplicateFilter.InMailbox("did:example:bo", one).Should().NotBe(DuplicateFilter.InMailbox(Bob, one),
            "a DID that is a prefix of another is another mailbox");
    }

    [Fact]
    public void Remembers_up_to_capacity_and_evicts_the_oldest_first()
    {
        var filter = new DuplicateFilter(3);
        filter.Add(Bob, 1);
        filter.Add(Bob, 2);
        filter.Add(Bob, 2);
        filter.Add(Bob, 3);
        filter.Count.Should().Be(3, "a key already remembered is not added twice");
        filter.Add(Bob, 4);

        filter.Contains(Bob, 1).Should().BeFalse("the oldest key is forgotten first");
        filter.Contains(Bob, 2).Should().BeTrue();
        filter.Contains(Bob, 3).Should().BeTrue();
        filter.Contains(Bob, 4).Should().BeTrue();
        filter.Count.Should().Be(3);
    }

    [Fact]
    public void Capacity_zero_is_off()
    {
        var filter = new DuplicateFilter(0);
        filter.Add(Bob, 7);
        filter.Contains(Bob, 7).Should().BeFalse();
        filter.Count.Should().Be(0);
    }

    [Fact]
    public void Retained_memory_does_not_depend_on_the_wire_values()
    {
        // L-062: only fixed-size hashes are kept, so 200 000-character ids cost what short ones do.
        var hugeId = new string('i', 200_000);
        var hugeSender = "did:example:" + new string('s', 2_000);
        var hugeMailbox = "did:example:" + new string('m', 2_000);
        var filter = new DuplicateFilter(64);
        for (var n = 0; n < 1_000; n++)
            filter.Add(hugeMailbox, DuplicateFilter.KeyOf(Authcrypt(hugeSender, Bob, hugeId + n), "p"));

        filter.Count.Should().Be(64);
        typeof(DuplicateFilter).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Select(f => f.FieldType)
            .Should().NotContain(t => t == typeof(string) || t.GenericTypeArguments.Contains(typeof(string)),
                "the filter must never retain a wire value");
    }

    [Fact]
    public async Task Concurrent_adds_keep_the_bound()
    {
        var filter = new DuplicateFilter(100);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var n = 0; n < 5_000; n++)
                filter.Add(Bob, (UInt128)(t * 1_000_000 + n));
        })));
        filter.Count.Should().Be(100);
    }

    private static UnpackResult Authcrypt(string senderKid, string recipientKid, string id)
        => Result(id, authenticated: true, nonRepudiation: false, senderKid: senderKid.Contains('#') ? senderKid : senderKid + "#key-1",
            signerKid: null, recipientKid: recipientKid.Contains('#') ? recipientKid : recipientKid + "#key-1");

    private static UnpackResult Result(string id, bool authenticated, bool nonRepudiation, string? senderKid, string? signerKid, string? recipientKid)
        => new(
            Message: new Message { Id = id, Type = "https://didcomm.org/basicmessage/2.0/message" },
            Stack: Array.Empty<EnvelopeKind>(),
            Encrypted: true,
            Authenticated: authenticated,
            NonRepudiation: nonRepudiation,
            AnonymousSender: !authenticated,
            ContentEncryption: null,
            KeyWrap: null,
            SignatureAlgorithm: null,
            SignerKid: signerKid,
            SenderKid: senderKid,
            RecipientKid: recipientKid,
            AllRecipientKids: recipientKid is null ? Array.Empty<string>() : new[] { recipientKid },
            FromPrior: null);
}
