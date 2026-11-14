using AwesomeAssertions;
using AwesomeAssertions.Execution;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Protocols.Routing;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// ADV-1 (#96 red team): who decides that a delivered message belongs to a local DID — the
/// <c>recipientDid</c> filter (D-20 as refined for Q2-1: the decrypting key, or any recipient label
/// of the innermost envelope) and the container-wide duplicate filter (D-15: keyed on the sender,
/// the decrypting DID and the id, "so the scope is per local DID", plan §4.9) — on a multi-tenant
/// host: one container holding the keys of two local DIDs, F (<see cref="ClientWorld.Local"/>) and
/// T (<see cref="ClientWorld.OtherLocal"/>), each enrolled with the mediator under its own grant.
/// Every envelope is real and goes through the real channel, unpacker and duplicate filter; only the
/// transport is scripted (<see cref="Adv1Mediator"/>).
/// </summary>
/// <remarks>
/// Failing tests (ADV1-1, ADV1-2, ADV1-3) demonstrate defects and are meant to stay red until fixed;
/// the others are attacks that held and stay as regressions.
/// </remarks>
public sealed class PickupClientAdv1Tests : IAsyncLifetime
{
    private ClientWorld _w = null!;
    private readonly FakeSend _send = new();
    private MediatorClientChannel _channel = null!;
    private PickupClient _pickup = null!;
    private Adv1Mediator _mediator = null!;

    public async Task InitializeAsync()
    {
        _w = await ClientWorld.CreateAsync();
        _channel = Create(_w, _send);
        _pickup = new PickupClient(_channel, logger: null);
        _mediator = new Adv1Mediator(_w);
        _mediator.Serve(_send);
    }

    public async Task DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _w.DisposeAsync();
    }

    private string F => _w.Local.Did;

    private string T => _w.OtherLocal.Did;

    private Task<PickupBatch> FetchAsync(string grant, string? recipientDid = null)
        => _pickup.FetchAsync(_w.Mediator.Did, grant, 10, recipientDid, serviceEndpointOverride: Https);

    // ---- ADV1-1: cross-DID suppression through the shared duplicate filter --------------------

    /// <summary>
    /// <b>ADV1-1 (defect).</b> Property broken: the duplicate filter's scope is "per local DID"
    /// (plan §4.9) and a multi-tenant host can protect itself by checking
    /// <c>Unpacked.RecipientKid</c> (§7, the stated residual "Without a filter … a multi-tenant host
    /// checks <c>Unpacked.RecipientKid</c> or uses the filter"). Attack: Alice writes one authcrypted
    /// message to Mallory and to T. Mallory, a co-recipient, holds its exact bytes and sends them in
    /// a forward whose <c>next</c> is F, so the mediator queues a copy under F's grant. F fetches
    /// without a filter: the copy opens with T's key, is returned, its key (Alice, T, id) is
    /// committed to the container's duplicate filter and the copy is acknowledged — all inside
    /// <c>FetchAsync</c>, before F's application can see <c>RecipientKid</c> and drop it. When T
    /// fetches its own genuine copy, it is a "repeat": acknowledged (deleted at the mediator) and
    /// never returned. T has lost Alice's message for good, caused by a third party.
    /// </summary>
    [Fact]
    public async Task ADV1_1_A_copy_of_another_DIDs_message_relayed_into_this_DIDs_queue_makes_that_DID_lose_it()
    {
        var note = Adv1Envelopes.Note(_w.Alice.Did, T, _w.Mallory.Did);
        var packed = (await _w.Client.PackEncryptedAsync(note, new PackEncryptedOptions(Recipients: new[] { T, _w.Mallory.Did }, From: _w.Alice.Did))).Message;
        _mediator.Enqueue(T, "t-genuine", packed);
        _mediator.Enqueue(F, "relayed-by-mallory", packed);

        // F's tenant fetches first and applies the documented check: a message not decrypted for F
        // is not F's, so its application drops it.
        var forF = await FetchAsync(F);
        var relayed = forF.Items.Should().ContainSingle().Subject;
        relayed.Unpacked!.RecipientKid.Should().StartWith(T + "#", "the copy in F's queue opened with T's key");
        _mediator.Queued(F).Should().BeEmpty("FetchAsync acknowledged it before F's application could look at it");

        // T's tenant fetches its own copy.
        var forT = await FetchAsync(T);

        using (new AssertionScope())
        {
            forT.Items.Should().ContainSingle(i => i.Unpacked != null && i.Unpacked.Message.Id == note.Id,
                "T's genuine copy of Alice's message must reach T; a copy a third party relayed into F's queue must not consume it");
            _mediator.Queued(T).Should().Contain("t-genuine", "a message T never received must not be acknowledged on T's behalf");
        }
    }

    /// <summary>
    /// Control for ADV1-1 (held): with the <c>recipientDid</c> filter on F, the relayed copy of an
    /// authcrypted message is a failure item (no recipient label of its envelope names F, and the
    /// labels are bound by <c>apv</c>, so Mallory cannot add one), nothing is committed, and T gets
    /// its message.
    /// </summary>
    [Fact]
    public async Task With_the_filter_a_relayed_copy_of_another_DIDs_authcrypt_message_is_a_failure_and_that_DID_still_gets_it()
    {
        var note = Adv1Envelopes.Note(_w.Alice.Did, T, _w.Mallory.Did);
        var packed = (await _w.Client.PackEncryptedAsync(note, new PackEncryptedOptions(Recipients: new[] { T, _w.Mallory.Did }, From: _w.Alice.Did))).Message;
        _mediator.Enqueue(T, "t-genuine", packed);
        _mediator.Enqueue(F, "relayed-by-mallory", packed);

        var forF = await FetchAsync(F, recipientDid: F);
        forF.Items.Should().ContainSingle().Which.Error.Should().BeOfType<MediationProtocolException>();
        _mediator.Queued(F).Should().Equal("relayed-by-mallory");

        var forT = await FetchAsync(T, recipientDid: T);
        forT.Items.Should().ContainSingle().Which.Unpacked!.Message.Id.Should().Be(note.Id);
    }

    // ---- ADV1-2: a co-recipient relabels a signed message --------------------------------------

    /// <summary>
    /// <b>ADV1-2 (defect).</b> Properties broken: D-20 as refined for Q2-1 — "A message whose
    /// envelope does not name the filter's DID at all (… another tenant's, under this filter) is
    /// not [this DID's]" — on the premise that a label means "its sender encrypted it to this DID
    /// too"; and the anti-surreptitious-forwarding rule behind FR-SIG-06 / FR-CONSIST-02 (a signed
    /// message is only accepted by a recipient its signed <c>to</c> names). Attack: Alice signs a
    /// message to Mallory and T (anoncrypt(sign)). Mallory takes Alice's JWS out of her copy and
    /// anoncrypts it again herself — she writes the envelope, so its <c>apv</c> commits to whatever
    /// labels she lists — to T first and F second, and relays it into F's queue. F fetches under its
    /// own filter: T's key opens it (the first held key in envelope order), FR-CONSIST-02 is checked
    /// for T only (T is in Alice's <c>to</c>), and the label F passes the filter. F's application
    /// receives a non-repudiable message "from Alice" that Alice never addressed to F; and since the
    /// duplicate key is (Alice, T, id), T's own copy is then acknowledged as a repeat and lost — the
    /// filter the residual recommends does not stop ADV1-1 for signed messages.
    /// </summary>
    [Fact]
    public async Task ADV1_2_A_co_recipient_relabels_a_signed_message_so_it_passes_another_DIDs_filter_and_its_DID_loses_it()
    {
        var note = Adv1Envelopes.Note(_w.Alice.Did, _w.Mallory.Did, T);
        var genuine = (await _w.Client.PackEncryptedAsync(note, new PackEncryptedOptions(Recipients: new[] { T, _w.Mallory.Did }, SignFrom: _w.Alice.Did))).Message;
        // What Mallory finds inside her copy: Alice's signed JWS.
        var aliceJws = await _w.Client.PackSignedAsync(note, _w.Alice.Did);
        var relabelled = Adv1Envelopes.Anoncrypt(aliceJws, Adv1Envelopes.KeyAgreement(_w.OtherLocal), Adv1Envelopes.KeyAgreement(_w.Local));
        _mediator.Enqueue(T, "t-genuine", genuine);
        _mediator.Enqueue(F, "relayed-by-mallory", relabelled);

        var forF = await FetchAsync(F, recipientDid: F);
        var forT = await FetchAsync(T, recipientDid: T);

        using (new AssertionScope())
        {
            var item = forF.Items.Should().ContainSingle().Subject;
            item.Error.Should().NotBeNull(
                "no key of F opened the message and Alice's signed 'to' does not name F, so under F's filter it is not F's message");
            forT.Items.Should().ContainSingle(i => i.Unpacked != null && i.Unpacked.Message.Id == note.Id,
                "T's genuine copy of Alice's message must reach T");
        }
    }

    /// <summary>
    /// Control for ADV1-2 (held): Mallory cannot put F first. F's key then opens the envelope, and
    /// FR-CONSIST-02 refuses a decrypting DID that Alice's signed <c>to</c> does not name.
    /// </summary>
    [Fact]
    public async Task A_relabelled_signed_message_that_Fs_own_key_opens_is_refused_by_the_addressing_rule()
    {
        var note = Adv1Envelopes.Note(_w.Alice.Did, _w.Mallory.Did, T);
        var aliceJws = await _w.Client.PackSignedAsync(note, _w.Alice.Did);
        var relabelled = Adv1Envelopes.Anoncrypt(aliceJws, Adv1Envelopes.KeyAgreement(_w.Local), Adv1Envelopes.KeyAgreement(_w.OtherLocal));
        _mediator.Enqueue(F, "relayed-by-mallory", relabelled);

        var forF = await FetchAsync(F, recipientDid: F);

        forF.Items.Should().ContainSingle().Which.Error.Should().NotBeNull();
        _mediator.Queued(F).Should().Equal("relayed-by-mallory");
    }

    // ---- ADV1-3: a recipient label nothing commits to ------------------------------------------

    /// <summary>
    /// <b>ADV1-3 (defect).</b> Property broken: D-20 — the filter "is enforced, not trusted to the
    /// mediator": "a mediator handing over another of our DIDs' messages, or another tenant's, under
    /// this filter" must not pass. The labels <c>IsFor</c> reads are evidence only while
    /// <c>apv</c> commits to them (L-040); a JWE whose protected header carries no <c>apv</c> is
    /// accepted by the unpack (DataProofs skips the re-derivation when <c>apv</c> is absent), and its
    /// recipients array is unprotected. Attack: a message for T only, from a sender that writes no
    /// <c>apv</c>; the mediator (or any relay before it) appends a recipient entry labelled with F's
    /// key id and a random <c>encrypted_key</c>, and hands it over in F's filtered fetch. T's key
    /// opens it, F's label passes the filter, and it is returned to F and acknowledged.
    /// </summary>
    [Fact]
    public async Task ADV1_3_Without_apv_a_relay_appends_the_filter_DIDs_label_to_another_DIDs_message_and_it_passes_the_filter()
    {
        var note = Adv1Envelopes.Note(_w.Alice.Did, T);
        var plaintext = await _w.Client.PackPlaintextAsync(note);
        var labelledForF = Adv1Envelopes.WithAddedRecipientLabels(plaintext, Adv1Envelopes.KeyAgreement(_w.OtherLocal), apvOverRealOnly: false,
            Adv1Envelopes.KeyAgreement(_w.Local).Kid!);
        _mediator.Enqueue(F, "t-message-labelled-for-f", labelledForF);

        var forF = await FetchAsync(F, recipientDid: F);

        var item = forF.Items.Should().ContainSingle().Subject;
        item.Error.Should().NotBeNull("only T's key opens the message; a recipient label nothing commits to is not evidence that it is F's");
    }

    /// <summary>
    /// Control for ADV1-3 (held): when the protected header carries <c>apv</c>, an appended label
    /// breaks the recipient commitment and the message fails to unpack (a failure item, not
    /// acknowledged).
    /// </summary>
    [Fact]
    public async Task With_apv_a_label_appended_by_a_relay_breaks_the_envelope()
    {
        var note = Adv1Envelopes.Note(_w.Alice.Did, T);
        var plaintext = await _w.Client.PackPlaintextAsync(note);
        var labelledForF = Adv1Envelopes.WithAddedRecipientLabels(plaintext, Adv1Envelopes.KeyAgreement(_w.OtherLocal), apvOverRealOnly: true,
            Adv1Envelopes.KeyAgreement(_w.Local).Kid!);
        _mediator.Enqueue(F, "t-message-labelled-for-f", labelledForF);

        var forF = await FetchAsync(F, recipientDid: F);

        forF.Items.Should().ContainSingle().Which.Error.Should().NotBeNull();
        _mediator.Queued(F).Should().Equal("t-message-labelled-for-f");
    }

    /// <summary>
    /// Proves the ADV1-3 envelope is otherwise sound: without the appended label it opens for T
    /// (so ADV1-3 fails only on the label, not on a broken construction).
    /// </summary>
    [Fact]
    public async Task The_apv_less_envelope_without_an_added_label_opens_for_its_real_recipient()
    {
        var note = Adv1Envelopes.Note(_w.Alice.Did, T);
        var plaintext = await _w.Client.PackPlaintextAsync(note);
        var unlabelled = Adv1Envelopes.WithAddedRecipientLabels(plaintext, Adv1Envelopes.KeyAgreement(_w.OtherLocal), apvOverRealOnly: false);

        var unpacked = await _w.Client.UnpackAsync(unlabelled);

        unpacked.Message.Id.Should().Be(note.Id);
        unpacked.RecipientKid.Should().StartWith(T + "#");
    }

    // ---- attacks on the filter that held -------------------------------------------------------

    /// <summary>
    /// Held: a relay wraps T's message in a forward layer that is self-addressed to F (anoncrypted to
    /// F, <c>next</c> = F) — the layer opens, but the filter judges the innermost envelope, which
    /// names only T: a failure item, not acknowledged.
    /// </summary>
    [Fact]
    public async Task A_layer_self_addressed_to_the_filter_DID_around_another_DIDs_message_is_a_failure()
    {
        var note = Adv1Envelopes.Note(_w.Alice.Did, T);
        var forT = (await _w.Client.PackEncryptedAsync(note, new PackEncryptedOptions(Recipients: new[] { T }, From: _w.Alice.Did))).Message;
        var layer = ForwardMessage.Create(F, F, new[] { forT });
        var wrapped = (await _w.Client.PackEncryptedAsync(layer, new PackEncryptedOptions(Recipients: new[] { F }))).Message;
        _mediator.Enqueue(F, "wrapped", wrapped);

        var forF = await FetchAsync(F, recipientDid: F);

        forF.Items.Should().ContainSingle().Which.Error.Should().BeOfType<MediationProtocolException>();
        _mediator.Queued(F).Should().Equal("wrapped");
    }

    /// <summary>
    /// Held: a sender-written label whose DID part is F but which is over the FR-API-09 key-id bound
    /// is never parsed and does not pass F's filter.
    /// </summary>
    [Fact]
    public async Task A_label_naming_the_filter_DID_over_the_key_id_bound_does_not_pass_the_filter()
    {
        var note = Adv1Envelopes.Note(_w.Alice.Did, T);
        var plaintext = await _w.Client.PackPlaintextAsync(note);
        var overLong = F + "#" + new string('k', 4100);
        var envelope = Adv1Envelopes.Anoncrypt(plaintext, Adv1Envelopes.KeyAgreement(_w.OtherLocal), Adv1Envelopes.KeyAgreement(_w.Local, kid: overLong));
        _mediator.Enqueue(F, "long-label", envelope);

        var forF = await FetchAsync(F, recipientDid: F);

        forF.Items.Should().ContainSingle().Which.Error.Should().BeOfType<MediationProtocolException>();
    }

    /// <summary>
    /// Held: a label that looks like F but is another DID subject (F with one more character in its
    /// method-specific id) does not pass F's filter: the comparison is by parsed DID subject, ordinal.
    /// </summary>
    [Fact]
    public async Task A_label_that_only_starts_like_the_filter_DID_does_not_pass_the_filter()
    {
        var note = Adv1Envelopes.Note(_w.Alice.Did, T);
        var plaintext = await _w.Client.PackPlaintextAsync(note);
        var lookalike = F + "a#key-1";
        var envelope = Adv1Envelopes.Anoncrypt(plaintext, Adv1Envelopes.KeyAgreement(_w.OtherLocal), Adv1Envelopes.KeyAgreement(_w.Local, kid: lookalike));
        _mediator.Enqueue(F, "lookalike", envelope);

        var forF = await FetchAsync(F, recipientDid: F);

        forF.Items.Should().ContainSingle().Which.Error.Should().BeOfType<MediationProtocolException>();
    }

    // ---- the duplicate filter: a stranger cannot suppress Alice's message -----------------------

    /// <summary>
    /// Held: Mallory reuses the id of Alice's message in her own authcrypt and in her own signed
    /// message to F, delivered first. Neither has Alice as its authenticated sender, so neither
    /// shares Alice's key; Alice's message is returned.
    /// </summary>
    [Fact]
    public async Task A_stranger_reusing_Alices_id_by_authcrypt_or_signature_does_not_suppress_Alices_message()
    {
        var alices = Adv1Envelopes.Note(_w.Alice.Did, F);
        var byAlice = (await _w.Client.PackEncryptedAsync(alices, new PackEncryptedOptions(Recipients: new[] { F }, From: _w.Alice.Did))).Message;

        var mallorys = Adv1Envelopes.Note(_w.Mallory.Did, F);
        mallorys.Id = alices.Id;
        var authcrypted = (await _w.Client.PackEncryptedAsync(mallorys, new PackEncryptedOptions(Recipients: new[] { F }, From: _w.Mallory.Did))).Message;
        var signed = (await _w.Client.PackEncryptedAsync(mallorys, new PackEncryptedOptions(Recipients: new[] { F }, SignFrom: _w.Mallory.Did))).Message;
        // Mallory claims Alice in an unauthenticated 'from' too.
        var claimsAlice = Adv1Envelopes.Note(_w.Alice.Did, F);
        claimsAlice.Id = alices.Id;
        var anonymous = (await _w.Client.PackEncryptedAsync(claimsAlice, new PackEncryptedOptions(Recipients: new[] { F }))).Message;

        _mediator.Enqueue(F, "m1", authcrypted);
        _mediator.Enqueue(F, "m2", signed);
        _mediator.Enqueue(F, "m3", anonymous);
        // Mallory's authcrypt and Mallory's signature name the same sender: one message, returned once.
        (await FetchAsync(F)).Items.Should().HaveCount(2);

        _mediator.Enqueue(F, "alice", byAlice);
        var batch = await FetchAsync(F);

        var item = batch.Items.Should().ContainSingle().Subject;
        item.Unpacked!.Message.Id.Should().Be(alices.Id);
        item.Unpacked.SenderKid.Should().StartWith(_w.Alice.Did + "#");
    }
}
