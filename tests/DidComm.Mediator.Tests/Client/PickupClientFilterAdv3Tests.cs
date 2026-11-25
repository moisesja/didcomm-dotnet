using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// ADV-3 red-team pass on the strict <c>recipientDid</c> filter and the per-mailbox duplicate memory
/// of fix round 2 (#96, owner decision 1-A, ADV1-1). Two local DIDs in one container, F
/// (<see cref="ClientWorld.Local"/>) and T (<see cref="ClientWorld.OtherLocal"/>), each enrolled
/// under its own grant; every envelope is real and goes through the real channel, unpacker and
/// duplicate filter, only the mediator is scripted (<see cref="Adv1Mediator"/>). These are attacks
/// that held; they stay as regressions. Both post-encryption tampering and self-consistent
/// forgeries are tried (AGENTS.md).
/// </summary>
public sealed class PickupClientFilterAdv3Tests : IAsyncLifetime
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

    /// <summary>
    /// Held: a nested envelope (not a re-wrap layer) — Mallory anoncrypts Alice's authcrypted
    /// message for T again, to F's key, and relays it into F's queue. The outer layer is F's, but
    /// <see cref="UnpackResult.RecipientKid"/> is the innermost layer's (T's), so under F's filter it
    /// is "for another of your DIDs" and stays queued; under T's filter (the DID whose key opened the
    /// message) it is T's. T's own copy is not suppressed.
    /// </summary>
    [Fact]
    public async Task An_outer_anoncrypt_layer_to_the_filter_DID_around_another_DIDs_message_does_not_pass_the_filter()
    {
        var note = Adv1Envelopes.Note(_w.Alice.Did, T);
        var forT = (await _w.Client.PackEncryptedAsync(note, new PackEncryptedOptions(Recipients: new[] { T }, From: _w.Alice.Did))).Message;
        var wrapped = Adv1Envelopes.Anoncrypt(forT, Adv1Envelopes.KeyAgreement(_w.Local));
        _mediator.Enqueue(F, "relayed-by-mallory", wrapped);
        _mediator.Enqueue(T, "t-genuine", forT);

        var underF = await FetchAsync(F, recipientDid: F);
        var item = underF.Items.Should().ContainSingle().Subject;
        item.Error.Should().BeOfType<MediationProtocolException>().Which.Message.Should().Contain("another of your DIDs");
        _mediator.Queued(F).Should().Equal("relayed-by-mallory");

        var underT = await FetchAsync(T, recipientDid: T);
        underT.Items.Should().ContainSingle().Which.Unpacked!.Message.Id.Should().Be(note.Id);
    }

    /// <summary>
    /// Held (post-encryption tampering): a relay relabels the only recipient entry of a message for T
    /// with F's key id. F's key is selected for the label and does not open the key wrap: a uniform
    /// decryption failure, never a message under F's filter, and nothing is acknowledged.
    /// </summary>
    [Fact]
    public async Task A_recipient_entry_relabelled_with_the_filter_DIDs_key_id_is_a_decryption_failure()
    {
        var note = Adv1Envelopes.Note(_w.Alice.Did, T);
        var plaintext = await _w.Client.PackPlaintextAsync(note);
        var fKid = Adv1Envelopes.KeyAgreement(_w.Local).Kid!;
        // T's key material under F's label, with and without an apv over that label.
        foreach (var apv in new[] { false, true })
        {
            var relabelled = Adv1Envelopes.WithAddedRecipientLabels(plaintext, Adv1Envelopes.KeyAgreement(_w.OtherLocal, kid: fKid), apvOverRealOnly: apv);
            _mediator.Enqueue(F, $"relabelled-{apv}", relabelled);
        }

        var underF = await FetchAsync(F, recipientDid: F);

        underF.Items.Should().HaveCount(2).And.OnlyContain(i => i.Error is CryptoException);
        _mediator.Queued(F).Should().HaveCount(2, "failures are never acknowledged");
    }

    /// <summary>
    /// Held (self-consistent forgery): Mallory writes her own message claiming to come "for T" —
    /// <c>to</c> naming T, anoncrypted to F's key (a sender may encrypt to anyone). F's key opens it,
    /// and the addressing rule refuses it, since the plaintext's <c>to</c> does not name the DID that
    /// decrypted it. Re-addressed to F, it is F's (F's key opened it) and passes F's filter, and only
    /// F's: T's filter refuses it.
    /// </summary>
    [Fact]
    public async Task A_self_consistent_forgery_passes_only_the_filter_of_the_DID_whose_key_opened_it()
    {
        var claimsT = Adv1Envelopes.Note(_w.Mallory.Did, T);
        var toFsKey = Adv1Envelopes.Anoncrypt(await _w.Client.PackPlaintextAsync(claimsT), Adv1Envelopes.KeyAgreement(_w.Local));
        var forF = Adv1Envelopes.Note(_w.Mallory.Did, F);
        var honestlyForF = Adv1Envelopes.Anoncrypt(await _w.Client.PackPlaintextAsync(forF), Adv1Envelopes.KeyAgreement(_w.Local));
        _mediator.Enqueue(F, "claims-t", toFsKey);
        _mediator.Enqueue(F, "for-f", honestlyForF);

        var underT = await FetchAsync(F, recipientDid: T);
        underT.Items.Should().HaveCount(2).And.OnlyContain(i => i.Error != null, "neither was opened by T's key");
        underT.Items.Single(i => i.Id == "claims-t").Error.Should().BeAssignableTo<DidCommException>("the addressing rule refuses a 'to' that does not name the decrypting DID");

        var underF = await FetchAsync(F, recipientDid: F);
        // It failed when it was delivered before, so it is held back now (#111, owner decision C):
        // under F's filter it is still never returned opened.
        underF.Items.Single(i => i.Id == "claims-t").Unpacked.Should().BeNull();
        underF.Items.Single(i => i.Id == "for-f").Unpacked!.Message.Id.Should().Be(forF.Id);
        _mediator.Queued(F).Should().Equal("claims-t");
    }

    /// <summary>
    /// Held (per-mailbox memory): the same anonymous message (identical bytes) queued in F's mailbox
    /// and in T's is returned from each; a second copy in the same mailbox is acknowledged as a
    /// repeat. Fetching F's mailbox never consumes T's copy.
    /// </summary>
    [Fact]
    public async Task One_anonymous_message_in_two_mailboxes_is_returned_from_each_and_once_per_mailbox()
    {
        var note = Adv1Envelopes.Note(_w.Alice.Did, F, T);
        var packed = (await _w.Client.PackEncryptedAsync(note, new PackEncryptedOptions(Recipients: new[] { F, T }))).Message;
        _mediator.Enqueue(F, "f-1", packed);
        _mediator.Enqueue(F, "f-2", packed);
        _mediator.Enqueue(T, "t-1", packed);

        var fromF = await FetchAsync(F);
        var fromT = await FetchAsync(T);

        fromF.Items.Should().ContainSingle().Which.Unpacked!.Message.Id.Should().Be(note.Id);
        fromT.Items.Should().ContainSingle().Which.Unpacked!.Message.Id.Should().Be(note.Id);
        _mediator.Queued(F).Should().BeEmpty("the repeat in F's mailbox was acknowledged");
        _mediator.Queued(T).Should().BeEmpty();
    }
}
