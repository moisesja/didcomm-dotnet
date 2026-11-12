using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Json;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Samples.Shared;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// <see cref="PickupSizing"/> (FR-PICK-03, JC-3): the arithmetic that turns <c>max_delivery_bytes</c>
/// into the byte budget of a batch and the largest admissible payload — and the proof, against real
/// packs, that its fixed allowances are honest: a payload at the admission bound and a full batch at
/// the budget both pack within <c>MaxDeliveryBytes</c> in the largest <c>delivery</c> that can
/// legally be built, with room to spare in every allowance.
/// </summary>
public sealed class MessagePickupStoreSizingTests(ITestOutputHelper output)
{
    private const int KiB = 1024;
    private const int MiB = 1024 * 1024;

    /// <summary>The longest DID the library accepts anywhere (FR-API-09): 2048 characters.</summary>
    private const int MaxDidLength = PickupBodies.MaxIdLength;

    // ---- the formula ----------------------------------------------------------------------

    /// <summary>
    /// The documented steps, written out again from the remarks of <see cref="PickupSizing"/> with
    /// the numbers of the judgement call (JC-3): 32 KiB for the envelope, 8 KiB for the delivery
    /// without its attachments, 96 bytes per attachment; every division rounds down.
    /// </summary>
    private static long Budget(long maxDeliveryBytes, int batch)
    {
        var ciphertextText = maxDeliveryBytes - 32 * KiB;                       // what is left beside the envelope
        var ciphertext = Math.DivRem(ciphertextText, 4).Quotient * 3;           // base64url: 4 characters carry 3 bytes
        var plaintext = Math.DivRem(ciphertext, 16).Quotient * 16 - 1;          // whole 16-byte blocks, at least 1 byte of padding
        var payloadText = plaintext - 8 * KiB - 96L * batch;                    // minus the skeleton and the descriptors
        return Math.DivRem(payloadText, 4).Quotient * 3;                        // base64url text back to payload bytes
    }

    [Fact]
    public void The_allowances_are_the_documented_ones()
    {
        PickupSizing.EnvelopeOverheadAllowance.Should().Be(32 * KiB);
        PickupSizing.DeliverySkeletonAllowance.Should().Be(8 * KiB);
        PickupSizing.AttachmentOverhead.Should().Be(96);
        PickupSizing.MaxMintedIdLength.Should().Be(64);
        PickupSizing.MaxThreadIdLength.Should().Be(512);
    }

    [Fact]
    public void At_the_1_MiB_default_with_a_batch_of_10_the_budget_is_564_525_bytes()
    {
        // 1 048 576 − 32 768 = 1 015 808 characters of ciphertext → 761 856 bytes → 761 855 of
        // plaintext → 752 703 characters for the payloads → 188 175 whole groups of 4 → 564 525 bytes.
        // (¾ × 752 703 = 564 527.25: rounding each division down costs the two bytes.)
        var budget = PickupSizing.ComputeBatchPayloadBudget(MiB, 10);
        output.WriteLine($"ComputeBatchPayloadBudget(1 MiB, 10) = {budget}");

        budget.Should().Be(564_525);
        budget.Should().Be(Budget(MiB, 10));

        var sizing = new PickupSizing(new MessagePickupOptions(), maxReceiveBytes: MiB);
        sizing.MaxDeliveryBytes.Should().Be(MiB);
        sizing.BatchPayloadBudget.Should().Be(564_525);
        sizing.MaxAdmissiblePayloadBytes.Should().Be(564_525);
    }

    [Theory]
    [InlineData(64 * KiB, 10, 11_565L)]
    [InlineData(64 * KiB, 1, 12_213L)]
    [InlineData(MiB, 1, 565_173L)]
    [InlineData(MiB, 100, 558_045L)]
    [InlineData(4 * MiB, 10, 2_333_997L)]
    [InlineData(44_992, 10, 9L)]             // the smallest value that carries anything for a batch of 10
    [InlineData(44_991, 10, 0L)]
    public void The_budget_follows_the_documented_steps(int maxDeliveryBytes, int batch, long expected)
    {
        PickupSizing.ComputeBatchPayloadBudget(maxDeliveryBytes, batch).Should().Be(expected);
        Budget(maxDeliveryBytes, batch).Should().Be(expected, "the test's own copy of the steps agrees with the number worked out by hand");
    }

    [Fact]
    public void The_budget_never_shrinks_when_MaxDeliveryBytes_grows_and_never_grows_with_the_batch()
    {
        // Every value across the edge where a payload starts to fit, then strides up to the top.
        var values = Enumerable.Range(40_000, 30_000).Select(v => (long)v)
            .Concat(Enumerable.Range(0, 4_000).Select(i => 70_000L + i * 997L))
            .Concat(Enumerable.Range(0, 2_000).Select(i => 4L * MiB + i * 1_071_739L))
            .Append(int.MaxValue - 1L)
            .Append(int.MaxValue)
            .ToArray();

        foreach (var batch in new[] { 1, 10, 100 })
        {
            long previous = long.MinValue;
            foreach (var value in values)
            {
                var budget = PickupSizing.ComputeBatchPayloadBudget(value, batch);
                if (budget != Budget(value, batch))
                    Assert.Fail($"({value}, {batch}): {budget}, but the documented steps give {Budget(value, batch)}.");
                if (budget < previous)
                    Assert.Fail($"({value}, {batch}): the budget fell from {previous} to {budget}.");
                if (budget > PickupSizing.ComputeBatchPayloadBudget(value, batch - 1 < 1 ? 1 : batch - 1))
                    Assert.Fail($"({value}, {batch}): a larger batch got a larger budget.");
                // Rounding errs towards a smaller batch: whatever is admitted is never above the
                // steps taken without rounding.
                var unrounded = 0.75 * (0.75 * (value - 32.0 * KiB) - 1 - 8 * KiB - 96.0 * batch);
                if (budget > 0 && budget > unrounded)
                    Assert.Fail($"({value}, {batch}): {budget} is above the unrounded {unrounded}.");
                previous = budget;
            }
        }
    }

    // ---- the admission bound --------------------------------------------------------------

    [Theory]
    [InlineData(null, MiB, MiB, 564_525)]              // no MaxDeliveryBytes: MaxReceiveBytes is the delivery bound
    [InlineData(null, 64 * KiB, 64 * KiB, 11_565)]
    [InlineData(64 * KiB, MiB, 64 * KiB, 11_565)]      // a smaller delivery bound: the budget decides
    [InlineData(4 * MiB, MiB, 4 * MiB, MiB)]           // a larger one: nothing over MaxReceiveBytes is admitted (D7)
    [InlineData(MiB, 100_000, MiB, 100_000)]
    [InlineData(MiB, 564_524, MiB, 564_524)]
    [InlineData(MiB, 564_525, MiB, 564_525)]
    [InlineData(MiB, 564_526, MiB, 564_525)]
    [InlineData(MiB, 1, MiB, 1)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, 1_207_934_241)]
    public void The_largest_admissible_payload_is_the_smaller_of_MaxReceiveBytes_and_the_budget(int? maxDeliveryBytes, int maxReceiveBytes, int effectiveDeliveryBytes, int admissible)
    {
        var sizing = new PickupSizing(new MessagePickupOptions { MaxDeliveryBytes = maxDeliveryBytes }, maxReceiveBytes);

        sizing.MaxDeliveryBytes.Should().Be(effectiveDeliveryBytes);
        sizing.BatchPayloadBudget.Should().Be(Budget(effectiveDeliveryBytes, 10), "the batch budget is not capped by MaxReceiveBytes: several small payloads may total more");
        sizing.MaxAdmissiblePayloadBytes.Should().Be(admissible);
        sizing.MaxAdmissiblePayloadBytes.Should().Be((int)Math.Min(maxReceiveBytes, sizing.BatchPayloadBudget));
    }

    [Fact]
    public void A_sizing_that_cannot_carry_any_message_is_refused_when_it_is_built()
    {
        FluentActions.Invoking(() => new PickupSizing(null!, MiB)).Should().Throw<ArgumentNullException>();

        // The delivery bound is too small — its own setting, or the MaxReceiveBytes that stands in for it.
        FluentActions.Invoking(() => new PickupSizing(new MessagePickupOptions { MaxDeliveryBytes = 44_991 }, MiB))
            .Should().Throw<ArgumentException>().Which.ParamName.Should().Be(nameof(MessagePickupOptions.MaxDeliveryBytes));
        FluentActions.Invoking(() => new PickupSizing(new MessagePickupOptions(), 44_991)).Should().Throw<ArgumentException>();
        new PickupSizing(new MessagePickupOptions(), 44_992).MaxAdmissiblePayloadBytes.Should().Be(9);

        // The delivery bound is fine, but nothing may be received at all.
        foreach (var maxReceiveBytes in new[] { 0, -1, int.MinValue })
        {
            FluentActions.Invoking(() => new PickupSizing(new MessagePickupOptions { MaxDeliveryBytes = MiB }, maxReceiveBytes))
                .Should().Throw<ArgumentException>().Which.ParamName.Should().Be("maxReceiveBytes");
        }
    }

    // ---- keeping the allowances honest: real packs ----------------------------------------

    /// <summary>Mints the longest ids the queue accepts (64 characters), so every attachment descriptor is as large as it gets.</summary>
    private sealed class LongestIds : IMessageIdGenerator
    {
        public string NewId() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    }

    /// <summary>The bytes an encrypted pack encrypts: the plaintext message, serialised as the envelope writer serialises it.</summary>
    private static byte[] Plaintext(Message message)
    {
        message.Typ ??= MediaTypes.Plaintext;
        message.Validate();
        return DeterministicJsonWriter.WriteUtf8(JsonSerializer.SerializeToNode(message, DidCommJson.Default));
    }

    /// <summary>The length of the base64url text of <paramref name="bytes"/> bytes, unpadded.</summary>
    private static int Base64Length(int bytes) => (bytes * 4 + 2) / 3;

    /// <summary>What AES-CBC makes of a plaintext: whole 16-byte blocks, at least one byte of padding.</summary>
    private static int Padded(int plaintextBytes) => (plaintextBytes / 16 + 1) * 16;

    /// <summary>
    /// A real, resolvable <c>did:peer:2</c> as long as the library accepts a DID to be (2048
    /// characters, or the few below it that the encoding allows): its service endpoint is padded, and
    /// did:peer:2 carries the service inside the DID itself.
    /// </summary>
    private static async Task<string> NewLongestPeerAsync(PickupFixture fixture)
    {
        var services = fixture.Host.Services;
        async Task<PeerIdentity> CreateAsync(int padding)
            => await PeerIdentityFactory.CreateAsync(
                services.GetRequiredService<IDidManager>(),
                services.GetRequiredService<IKeyGenerator>(),
                services.GetRequiredService<ICryptoProvider>(),
                new DidCommServiceSpec("https://mediator.example/" + new string('p', padding)));

        // The service is base64url-encoded into the DID: 3 characters of endpoint cost 4 of DID.
        var shortest = await CreateAsync(0);
        var padding = (MaxDidLength - shortest.Did.Length) * 3 / 4;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var peer = await CreateAsync(padding);
            if (peer.Did.Length > MaxDidLength)
            {
                padding -= Math.Max(1, (peer.Did.Length - MaxDidLength) * 3 / 4);
                continue;
            }
            if (peer.Did.Length < MaxDidLength - 8)
            {
                padding += Math.Max(1, (MaxDidLength - peer.Did.Length) * 3 / 4 - 1);
                continue;
            }
            foreach (var jwk in peer.Privates)
                fixture.Host.Secrets.Add(jwk);
            return peer.Did;
        }
        throw new InvalidOperationException("Could not mint a did:peer:2 close to the maximum DID length.");
    }

    /// <summary>
    /// The largest <c>delivery</c> the role can be made to build with real identities: mediator and
    /// peer DIDs of (about) 2048 characters, a registered recipient DID of exactly 2048 that the
    /// request names (so it is echoed), a 512-character thread id, and 64-character message ids.
    /// </summary>
    private sealed record WorstCase(PickupFixture Fixture, string Mediator, string Peer, string Recipient, string ThreadId)
    {
        public Message DeliveryRequest(int limit)
            => PickupMessages.Raw(
                MessagePickup.DeliveryRequestType, Peer, Mediator,
                new JsonObject { ["limit"] = limit, [PickupBodies.RecipientDid] = Recipient },
                thid: ThreadId);
    }

    private static async Task<WorstCase> WorstCaseAsync(int? maxDeliveryBytes, int batch = 10)
    {
        var fixture = PickupFixture.Create(
            pickup: o =>
            {
                o.MaxDeliveryBytes = maxDeliveryBytes;
                o.MaxDeliveryBatch = batch;
            },
            services: collection => collection.AddSingleton<IMessageIdGenerator>(new LongestIds()));
        var mediator = await NewLongestPeerAsync(fixture);
        var peer = await NewLongestPeerAsync(fixture);
        // The recipient DID only has to be registered, not resolved: a syntactically valid DID at the bound.
        var recipient = "did:example:" + new string('r', MaxDidLength - "did:example:".Length);
        mediator.Length.Should().BeInRange(MaxDidLength - 8, MaxDidLength);
        peer.Length.Should().BeInRange(MaxDidLength - 8, MaxDidLength);
        recipient.Length.Should().Be(MaxDidLength);
        await fixture.EnrolAsync(peer, mediator, recipient);
        return new WorstCase(fixture, mediator, peer, recipient, new string('t', PickupSizing.MaxThreadIdLength));
    }

    [Theory]
    [InlineData(null, 10)]          // the defaults: MaxReceiveBytes (1 MiB), batches of 10
    [InlineData(null, 1)]           // a batch of one: the largest payload a 1 MiB delivery ever admits
    [InlineData(64 * KiB, 10)]      // a small custom budget
    [InlineData(44_992, 10)]        // the smallest budget that carries anything
    public async Task A_payload_of_exactly_the_admission_bound_packs_within_MaxDeliveryBytes_in_the_largest_legal_delivery(int? maxDeliveryBytes, int batch)
    {
        var worst = await WorstCaseAsync(maxDeliveryBytes, batch);
        await using var fixture = worst.Fixture;
        var sizing = fixture.Queue.Coordinator.Sizing;
        var admissible = fixture.Queue.MaxAdmissiblePayloadBytes;
        sizing.MaxDeliveryBytes.Should().Be(maxDeliveryBytes ?? MiB);
        admissible.Should().Be((int)Budget(sizing.MaxDeliveryBytes, batch));

        // One byte over the bound is not admitted; the bound itself is.
        (await fixture.Queue.DeliverAsync(worst.Recipient, PickupFixture.Payload(admissible + 1))).Outcome.Should().Be(PickupDeliveryOutcome.PayloadTooLarge);
        var payload = PickupFixture.Payload(admissible, 7);
        var id = await fixture.QueueAsync(worst.Recipient, payload);
        id.Length.Should().Be(PickupSizing.MaxMintedIdLength);

        var session = fixture.Session(worst.Peer, worst.Mediator, pack: true);
        var outcome = await fixture.SendAsync(worst.DeliveryRequest(limit: 10), worst.Mediator, session);

        outcome.Reply.Should().BeNull("the delivery was pushed through the size-bounded send");
        var attempt = session.Attempts.Should().ContainSingle("an admitted payload fits alone at the first try: nothing is trimmed").Subject;
        attempt.Result.Should().Be(ReturnRouteSendResult.Sent);
        attempt.MaxPackedBytes.Should().Be(sizing.MaxDeliveryBytes);
        attempt.PackedBytes.Should().BeLessThanOrEqualTo(sizing.MaxDeliveryBytes);

        // And it was the worst case that was packed.
        var delivery = attempt.Message;
        delivery.From.Should().Be(worst.Mediator);
        delivery.To.Should().Equal(worst.Peer);
        delivery.Thid.Should().Be(worst.ThreadId);
        MessagePickup.TryParseDelivery(delivery, out var messages, out var echoed).Should().BeTrue();
        echoed.Should().Be(worst.Recipient);
        var delivered = messages!.Should().ContainSingle().Subject;
        delivered.Id.Should().Be(id);
        delivered.Payload.ToArray().Should().Equal(payload);

        output.WriteLine(
            $"MaxDeliveryBytes {sizing.MaxDeliveryBytes}, batch {batch}: payload {admissible} bytes packed to {attempt.PackedBytes} ({sizing.MaxDeliveryBytes - attempt.PackedBytes} to spare); " +
            $"from {worst.Mediator.Length} chars, to {worst.Peer.Length} chars, recipient_did {worst.Recipient.Length}, thid {worst.ThreadId.Length}, attachment id {id.Length}.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(64 * KiB)]
    public async Task A_full_batch_whose_payloads_total_exactly_the_budget_packs_within_MaxDeliveryBytes(int? maxDeliveryBytes)
    {
        var worst = await WorstCaseAsync(maxDeliveryBytes);
        await using var fixture = worst.Fixture;
        var sizing = fixture.Queue.Coordinator.Sizing;
        const int batch = 10;
        var budget = (int)sizing.BatchPayloadBudget;

        // Nine payloads of a length that leaves base64 the most to round up (≡ 1 mod 3), and a
        // tenth that takes the total to the budget exactly.
        var each = budget / batch;
        while (each % 3 != 1)
            each--;
        var sizes = Enumerable.Repeat(each, batch - 1).Append(budget - each * (batch - 1)).ToArray();
        sizes.Sum().Should().Be(budget);
        sizes.Should().OnlyContain(size => size > 0 && size <= sizing.MaxAdmissiblePayloadBytes);
        var ids = new List<string>();
        for (var i = 0; i < batch; i++)
            ids.Add(await fixture.QueueAsync(worst.Recipient, PickupFixture.Payload(sizes[i], (byte)i)));

        var session = fixture.Session(worst.Peer, worst.Mediator, pack: true);
        var outcome = await fixture.SendAsync(worst.DeliveryRequest(limit: batch), worst.Mediator, session);

        outcome.Reply.Should().BeNull();
        var attempt = session.Attempts.Should().ContainSingle("a batch chosen by the budget fits at the first try: nothing is trimmed").Subject;
        attempt.Result.Should().Be(ReturnRouteSendResult.Sent);
        attempt.PackedBytes.Should().BeLessThanOrEqualTo(sizing.MaxDeliveryBytes);
        MessagePickup.TryParseDelivery(attempt.Message, out var messages, out _).Should().BeTrue();
        messages!.Select(m => m.Id).Should().Equal(ids, "the whole batch travelled in one delivery");
        messages!.Sum(m => m.Payload.Length).Should().Be(budget);

        output.WriteLine($"MaxDeliveryBytes {sizing.MaxDeliveryBytes}: {batch} payloads totalling {budget} bytes packed to {attempt.PackedBytes} ({sizing.MaxDeliveryBytes - attempt.PackedBytes} to spare).");
    }

    [Fact]
    public async Task One_payload_byte_more_than_the_budget_is_not_put_into_the_batch()
    {
        // The other side of the edge above: ten payloads that total budget + 1 are not one delivery.
        var worst = await WorstCaseAsync(64 * KiB);
        await using var fixture = worst.Fixture;
        var budget = (int)fixture.Queue.Coordinator.Sizing.BatchPayloadBudget;
        var each = budget / 10;
        var sizes = Enumerable.Repeat(each, 9).Append(budget + 1 - each * 9).ToArray();
        foreach (var size in sizes)
            await fixture.QueueAsync(worst.Recipient, PickupFixture.Payload(size));

        var session = fixture.Session(worst.Peer, worst.Mediator, pack: true);
        await fixture.SendAsync(worst.DeliveryRequest(limit: 10), worst.Mediator, session);

        var attempt = session.Attempts.Should().ContainSingle().Subject;
        attempt.Result.Should().Be(ReturnRouteSendResult.Sent);
        MessagePickup.TryParseDelivery(attempt.Message, out var messages, out _).Should().BeTrue();
        messages!.Should().HaveCount(9, "the tenth payload would carry the total one byte past the budget");
    }

    // Named, so that no control character ends up in a test name (and from there in a report file).
    private static readonly Dictionary<string, string> CharactersThatCostMoreThanAByte = new()
    {
        ["a two-byte letter"] = "\u00E9",
        ["a three-byte letter"] = "\u20AC",
        ["a surrogate pair"] = "\uD83D\uDE00",
        ["a full-width digit"] = "\uFF11",
        ["a quote"] = "\"",
        ["a backslash"] = "\\",
        ["a control character"] = "\u0001",
        ["a tab"] = "\t",
        ["a line separator"] = "\u2028",
    };

    [Theory]
    [InlineData("a two-byte letter")]
    [InlineData("a three-byte letter")]
    [InlineData("a surrogate pair")]
    [InlineData("a full-width digit")]
    [InlineData("a quote")]
    [InlineData("a backslash")]
    [InlineData("a control character")]
    [InlineData("a tab")]
    [InlineData("a line separator")]
    public void A_recipient_DID_the_role_echoes_costs_one_byte_per_character(string what)
    {
        // The skeleton allowance counts a DID of 2048 characters as 2048 bytes. That holds only
        // while nothing that is written as several bytes — a multi-byte character, or one that JSON
        // escapes — can be part of a value the role accepts as a recipient DID and echoes.
        var did = "did:example:abc" + CharactersThatCostMoreThanAByte[what] + "def";
        var written = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(did, DidCommJson.Default)) - 2;
        written.Should().BeGreaterThan(did.Length, $"{what} is written as more than one byte per character");

        DidComm.Mediator.Coordination.BareDid.IsBareDid(did).Should().BeFalse($"{what} is not a DID character, so no such value is ever registered, queued for or echoed");
    }

    [Fact]
    public async Task The_envelope_of_a_did_peer_identity_takes_less_than_half_of_the_envelope_allowance()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var client = fixture.Host.Client;

        // Sixteen deliveries whose plaintext grows a byte at a time: every position within a cipher block.
        var overheads = new List<int>();
        for (var extra = 1; extra <= 16; extra++)
        {
            var delivery = MessagePickup.CreateDelivery(mediator, peer, new[] { new DeliveredMessage("m1", PickupFixture.Payload(3_000)) }, thid: new string('t', extra));
            delivery.CreatedTime = fixture.Clock.Now.ToUnixTimeSeconds();
            var packed = (await client.PackEncryptedAsync(delivery, new PackEncryptedOptions(Recipients: new[] { peer }, From: mediator, Forward: false))).Message;

            var ciphertextText = Base64Length(Padded(Plaintext(delivery).Length));
            using var envelope = JsonDocument.Parse(packed);
            envelope.RootElement.GetProperty("ciphertext").GetString()!.Length.Should().Be(
                ciphertextText, "the sizing assumes the ciphertext is the plaintext padded to whole 16-byte blocks by at least one byte, in base64url");
            overheads.Add(Encoding.UTF8.GetByteCount(packed) - ciphertextText);
        }

        output.WriteLine($"Envelope overhead for a did:peer:2 with one key-agreement key (DIDs of {mediator.Length} and {peer.Length} characters): {overheads.Min()}..{overheads.Max()} bytes; the allowance is {PickupSizing.EnvelopeOverheadAllowance}.");
        // Half, not all: growth in the JOSE layer fails here long before it can break the guarantee.
        overheads.Max().Should().BeLessThan(PickupSizing.EnvelopeOverheadAllowance / 2);
    }

    [Fact]
    public async Task The_envelope_of_the_longest_did_peer_identities_stays_within_the_envelope_allowance()
    {
        var worst = await WorstCaseAsync(null);
        await using var fixture = worst.Fixture;
        var delivery = MessagePickup.CreateDelivery(worst.Mediator, worst.Peer, new[] { new DeliveredMessage("m1", PickupFixture.Payload(3_000)) }, worst.ThreadId, worst.Recipient);
        delivery.CreatedTime = fixture.Clock.Now.ToUnixTimeSeconds();

        var packed = (await fixture.Host.Client.PackEncryptedAsync(delivery, new PackEncryptedOptions(Recipients: new[] { worst.Peer }, From: worst.Mediator, Forward: false))).Message;

        var overhead = Encoding.UTF8.GetByteCount(packed) - Base64Length(Padded(Plaintext(delivery).Length));
        output.WriteLine($"Envelope overhead with DIDs of {worst.Mediator.Length} and {worst.Peer.Length} characters: {overhead} bytes; the allowance is {PickupSizing.EnvelopeOverheadAllowance}.");
        overhead.Should().BeLessThan(PickupSizing.EnvelopeOverheadAllowance);
    }

    [Fact]
    public void The_largest_legal_delivery_without_its_attachments_serialises_below_the_skeleton_allowance()
    {
        // Three DIDs at the 2048-character bound, a thread id at its bound, a full batch of
        // attachments with the longest ids, and a created_time with every digit it can have.
        string Did(char fill) => "did:example:" + new string(fill, MaxDidLength - "did:example:".Length);
        var payloads = new[] { 1, 2, 3, 4, 5, 6, 100, 1_000, 10_000, 56_452 };
        var messages = payloads.Select((size, i) => new DeliveredMessage(new string((char)('a' + i), PickupSizing.MaxMintedIdLength), PickupFixture.Payload(size))).ToArray();
        var delivery = MessagePickup.CreateDelivery(Did('f'), Did('t'), messages, new string('h', PickupSizing.MaxThreadIdLength), Did('r'));
        delivery.CreatedTime = 9_999_999_999;

        var plaintext = Plaintext(delivery);
        using var document = JsonDocument.Parse(plaintext);
        var attachments = document.RootElement.GetProperty("attachments").EnumerateArray().Select(a => Encoding.UTF8.GetByteCount(a.GetRawText())).ToArray();
        attachments.Should().HaveCount(payloads.Length);

        // The skeleton: everything that is not an attachment or the comma before one.
        var skeleton = plaintext.Length - attachments.Sum() - (attachments.Length - 1);
        output.WriteLine($"Skeleton of the largest legal delivery: {skeleton} bytes; the allowance is {PickupSizing.DeliverySkeletonAllowance}.");
        skeleton.Should().BeLessThan(PickupSizing.DeliverySkeletonAllowance);

        // Each attachment: its descriptor, its separator and the rounding of its base64, against
        // the 4/3 characters per payload byte that the budget already counts.
        for (var i = 0; i < attachments.Length; i++)
        {
            var beyondThePayload = attachments[i] + 1 - payloads[i] * 4.0 / 3.0;
            beyondThePayload.Should().BeLessThanOrEqualTo(PickupSizing.AttachmentOverhead, $"attachment {i} carries {payloads[i]} bytes");
        }
    }
}
