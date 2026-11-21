using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using DidComm.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// #100 OD-1: the mailbox stores only DIDComm messages in encrypted form. Message Pickup's only valid
/// delivery attachment is "a DIDComm v2 Message in encrypted form" (3.0 L172; 4.0 L261, v1 or v2),
/// so <see cref="MessagePickupQueue.DeliverAsync"/> refuses anything else, whoever supplies it, with
/// <see cref="PickupDeliveryOutcome.NotEncrypted"/>, and stores nothing. "Encrypted form" is the
/// library's JWE rule (FR-API-03): strict UTF-8 JSON whose object has a <c>ciphertext</c> member,
/// judged on the outermost layer and never decrypted. The refusal comes after the size bound and
/// before the expiry, the registration lookup, the live target and any store call; it counts
/// against no quota, reaches no live session (3.0 or 4.0), and its log line quotes nothing of the
/// payload.
/// </summary>
public sealed class MessagePickupNotEncryptedTests
{
    private const string Marker = "NOTENCMARKER";

    private const string Authcrypt = "an authcrypt JWE";
    private const string Anoncrypt = "an anoncrypt JWE";
    private const string SignedThenEncrypted = "a signed message inside a JWE";
    private const string ProtectedSender = "an authcrypt JWE inside an anoncrypt JWE";
    private const string SmallestJwe = "the smallest object with a ciphertext member";
    private const string PaddedJwe = "a JWE with whitespace around it";
    private const string Plaintext = "a plaintext DIDComm message";
    private const string GeneralJws = "a signed-only message (General JWS)";
    private const string FlattenedJws = "a signed-only message (Flattened JWS)";

    public static TheoryData<string, bool> Forms => new()
    {
        { Authcrypt, true },
        { Anoncrypt, true },
        { SignedThenEncrypted, true },
        { ProtectedSender, true },
        // The library's own rule: a ciphertext member is what makes an envelope a JWE (the unpack
        // classifies it so and then fails to decrypt it). Nothing is decrypted here, so this passes.
        { SmallestJwe, true },
        { PaddedJwe, true },
        { Plaintext, false },
        { GeneralJws, false },
        { FlattenedJws, false },
        { "a JSON array holding a JWE", false },
        { "a JSON string", false },
        { "a JSON number", false },
        { "JSON null", false },
        { "not JSON", false },
        { "bytes that are not UTF-8", false },
        { "a JWE whose ciphertext member is named twice", false },
        { "a JWE followed by another value", false },
        { "a JWE followed by a stray character", false },
        { "a JWE followed by a comment", false },
        { "a member name that is an unpaired surrogate escape", false },
        { "an empty object", false },
        { "a ciphertext member only in a nested object", false },
    };

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Only_a_message_whose_outermost_layer_is_a_JWE_is_stored(string form, bool encrypted)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var payload = await FormAsync(h, form);
        h.ResetTraces();

        var result = await h.Fixture.Queue.DeliverAsync(h.Alice, payload, new PickupDeliveryOptions(AllowLive: false));

        if (encrypted)
        {
            result.Outcome.Should().Be(PickupDeliveryOutcome.Queued, form);
            var fetched = await h.Fixture.FetchAsync(h.Alice, h.Mediator, h.Http(h.Alice), 10);
            fetched!.Value.Messages.Should().ContainSingle().Which.Payload.ToArray().Should().Equal(payload, "the message is relayed byte for byte, unopened");
        }
        else
        {
            result.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.NotEncrypted, null, null), form);
            h.StoreTrace.Should().BeEmpty("nothing is stored, and the store is not even asked");
            h.MediationTrace.Should().BeEmpty("the recipient is not looked up for a message the mailbox cannot hold");
            h.Store.Inner.MessageCount.Should().Be(0);
            h.Store.Inner.TotalBytes.Should().Be(0);
        }
    }

    [Fact]
    public async Task The_size_bound_is_judged_first_so_an_oversized_message_is_too_large_and_never_parsed()
    {
        // L-055: the bound limits the parse. Poison beyond it: bytes that are not even UTF-8.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var oversized = new byte[h.Fixture.Queue.MaxAdmissiblePayloadBytes + 1];
        oversized.AsSpan().Fill(0xFF);
        h.ResetTraces();
        var logged = h.Fixture.PickupLogs.Count;

        var result = await h.Fixture.Queue.DeliverAsync(h.Alice, oversized);

        result.Outcome.Should().Be(PickupDeliveryOutcome.PayloadTooLarge);
        h.StoreTrace.Should().BeEmpty();
        h.MediationTrace.Should().BeEmpty();
        h.Fixture.PickupLogs.Skip(logged).Should().ContainSingle().Which.Formatted.Should().Contain("largest admissible message", "the size refusal is the one said");
    }

    public static TheoryData<string> BeforeEveryLookup => new()
    {
        "a DID nobody registered",
        "a DID URL",
        "an expired message",
        "a message not yet due",
        "a store that would fail",
        "a mediation store that would fail",
    };

    [Theory]
    [MemberData(nameof(BeforeEveryLookup))]
    public async Task The_form_is_judged_before_the_expiry_the_registration_and_the_store(string situation)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var recipient = situation switch
        {
            "a DID nobody registered" => "did:example:nobody",
            "a DID URL" => h.Alice + "#key-1",
            _ => h.Alice,
        };
        var options = situation switch
        {
            "an expired message" => new PickupDeliveryOptions(ExpiresTime: 1),
            "a message not yet due" => new PickupDeliveryOptions(NotBefore: h.Clock.Now.AddHours(1)),
            _ => null,
        };
        if (situation == "a store that would fail")
            h.Store.OnEnqueue = (_, _, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker);
        if (situation == "a mediation store that would fail")
            h.Mediation.OnFindGrantByRecipientDid = (_, _) => throw new HandlerBoomException(HandlerBoomException.Marker);
        h.ResetTraces();

        var result = await h.Fixture.Queue.DeliverAsync(recipient, await FormAsync(h, Plaintext), options);

        result.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.NotEncrypted, null, null), situation);
        h.MediationTrace.Should().BeEmpty();
        h.StoreTrace.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refused_message_counts_against_no_quota()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync(o =>
        {
            o.MaxQueueMessages = 1;
            o.MaxQueueBytes = 4096;
        });
        await h.EnrolAsync(h.Alice, h.Alice);
        var plaintext = await FormAsync(h, Plaintext);
        for (var i = 0; i < 5; i++)
            (await h.Fixture.Queue.DeliverAsync(h.Alice, plaintext)).Outcome.Should().Be(PickupDeliveryOutcome.NotEncrypted);

        (await h.Fixture.Queue.DeliverAsync(h.Alice, await FormAsync(h, Authcrypt))).Outcome.Should().Be(PickupDeliveryOutcome.Queued, "the one message the quota allows still has its place");
        h.Store.CountOf(nameof(IMessageStore.EnqueueAsync)).Should().Be(1);
        h.Store.Inner.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task A_message_that_is_not_encrypted_never_reaches_a_3_0_live_session_and_a_delivery_request_finds_nothing()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var socket = h.Fixture.Session(h.Alice, h.Mediator, live: true);
        (await h.SendAsync(h.LiveChange(h.Alice, true), socket)).Status().LiveDelivery.Should().BeTrue();
        h.ResetTraces();

        var refused = await h.Fixture.Queue.DeliverAsync(h.Alice, await FormAsync(h, Plaintext));

        refused.Outcome.Should().Be(PickupDeliveryOutcome.NotEncrypted);
        socket.Attempts.Should().BeEmpty("nothing is pushed live");
        h.StoreTrace.Should().BeEmpty();
        var request = h.DeliveryRequest(h.Alice);
        (await h.SendAsync(request, h.Http(h.Alice))).ShouldBeStatus(request, h.Mediator, h.Alice, 0, 0, live: false);

        // The control: an encrypted message on the same session is pushed.
        var pushed = await h.Fixture.Queue.DeliverAsync(h.Alice, await FormAsync(h, Anoncrypt));
        pushed.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        socket.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task A_message_that_is_not_encrypted_never_reaches_a_4_0_live_session_and_a_delivery_request_finds_nothing()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var socket = h.Fixture.Session(h.Alice, h.Mediator, live: true);
        (await h.SendAsync(h.LiveChange40(h.Alice, true), socket)).Status40().LiveDelivery.Should().BeTrue();
        h.ResetTraces();

        var refused = await h.Fixture.Queue.DeliverAsync(h.Alice, await FormAsync(h, GeneralJws));

        refused.Outcome.Should().Be(PickupDeliveryOutcome.NotEncrypted);
        socket.Attempts.Should().BeEmpty("nothing is pushed live");
        h.StoreTrace.Should().BeEmpty();
        var request = h.DeliveryRequest40(h.Alice);
        (await h.SendAsync(request, h.Http(h.Alice))).ShouldBeEmptyDelivery40(request, h.Mediator, h.Alice, recipientDid: null);

        var pushed = await h.Fixture.Queue.DeliverAsync(h.Alice, await FormAsync(h, Authcrypt));
        pushed.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        socket.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task The_refusal_is_said_rate_limited_and_quotes_nothing_of_the_message()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var plaintext = await FormAsync(h, Plaintext);
        Encoding.UTF8.GetString(plaintext).Should().Contain(Marker);
        var queue = h.Fixture.Queue; // built now: its start-up line is not part of what is counted
        var logged = h.Fixture.PickupLogs.Count;

        for (var i = 0; i < 1000; i++)
            (await queue.DeliverAsync(h.Alice, plaintext)).Outcome.Should().Be(PickupDeliveryOutcome.NotEncrypted);

        var lines = h.Fixture.PickupLogs.Skip(logged).ToArray();
        lines.Should().HaveCount(1000).And.OnlyContain(e => e.Formatted.Contains("not a DIDComm message in encrypted form", StringComparison.Ordinal));
        lines[0].Level.Should().Be(LogLevel.Warning, "the first is said");
        lines.Skip(1).Take(998).Should().OnlyContain(e => e.Level == LogLevel.Debug, "anyone who can reach the mailbox can cause the line");
        lines[999].Level.Should().Be(LogLevel.Warning, "and every 1000th");
        lines.Select(e => e.State["Count"]).Should().Equal(Enumerable.Range(1, 1000).Select(n => (object?)(long)n));
        lines.Should().OnlyContain(e => e.State.Count == 2 && e.State.ContainsKey("{OriginalFormat}") && e.Exception == null, "the count is the only value in the line");
        lines.Should().OnlyContain(e => !e.Everything.Contains(Marker, StringComparison.Ordinal) && !e.Everything.Contains(h.Alice, StringComparison.Ordinal));
    }

    [Fact]
    public void NotEncrypted_is_appended_after_every_outcome_that_existed_so_none_of_them_changes_its_number()
    {
        Enum.GetValues<PickupDeliveryOutcome>().Select(v => ((int)v, v.ToString())).Should().Equal(
            (0, "PushedLive"), (1, "Queued"), (2, "NotRegistered"), (3, "Expired"), (4, "PayloadTooLarge"), (5, "QueueFull"), (6, "Faulted"), (7, "NotEncrypted"));
    }

    // ---- the forms ------------------------------------------------------------------------------

    private static async Task<byte[]> FormAsync(MessagePickupHandlerHarness h, string form)
    {
        var client = h.Fixture.Host.Client;
        var note = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(h.Bob)
            .WithTo(h.Alice)
            .WithBody(new JsonObject { ["content"] = "hello " + Marker })
            .Build();
        var text = form switch
        {
            Authcrypt => (await client.PackEncryptedAsync(note, new PackEncryptedOptions(Recipients: new[] { h.Alice }, From: h.Bob))).Message,
            Anoncrypt => (await client.PackEncryptedAsync(note, new PackEncryptedOptions(Recipients: new[] { h.Alice }))).Message,
            SignedThenEncrypted => (await client.PackEncryptedAsync(note, new PackEncryptedOptions(Recipients: new[] { h.Alice }, From: h.Bob, SignFrom: h.Bob))).Message,
            ProtectedSender => (await client.PackEncryptedAsync(note, new PackEncryptedOptions(Recipients: new[] { h.Alice }, From: h.Bob, ProtectSender: true))).Message,
            SmallestJwe => """{"ciphertext":""}""",
            PaddedJwe => " \r\n\t" + (await client.PackEncryptedAsync(note, new PackEncryptedOptions(Recipients: new[] { h.Alice }))).Message + "\n ",
            Plaintext => await client.PackPlaintextAsync(note),
            GeneralJws => await client.PackSignedAsync(note, h.Bob),
            FlattenedJws => Flattened(await client.PackSignedAsync(note, h.Bob)),
            "a JSON array holding a JWE" => """[{"ciphertext":"x"}]""",
            "a JSON string" => "\"ciphertext\"",
            "a JSON number" => "1",
            "JSON null" => "null",
            "not JSON" => "ciphertext " + Marker,
            "a JWE whose ciphertext member is named twice" => """{"protected":"e30","ciphertext":"x","ciphertext":"y"}""",
            "a JWE followed by another value" => """{"ciphertext":"x"} {"ciphertext":"y"}""",
            "a JWE followed by a stray character" => """{"ciphertext":"x"}x""",
            "a JWE followed by a comment" => """{"ciphertext":"x"}/* c */""",
            "a member name that is an unpaired surrogate escape" => """{"\uD800":1,"ciphertext":"x"}""",
            "an empty object" => "{}",
            "a ciphertext member only in a nested object" => """{"protected":"e30","inner":{"ciphertext":"x"}}""",
            "bytes that are not UTF-8" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, "no such form"),
        };
        return text is null
            ? new byte[] { (byte)'{', (byte)'"', (byte)'c', 0xC3, 0x28, (byte)'"', (byte)':', (byte)'1', (byte)'}' }
            : Encoding.UTF8.GetBytes(text);
    }

    // The same signature as a Flattened JWS (RFC 7515 §7.2.2): payload, protected and signature at the top level.
    private static string Flattened(string generalJws)
    {
        var general = JsonNode.Parse(generalJws)!.AsObject();
        var signature = general["signatures"]!.AsArray().Single()!.AsObject();
        return new JsonObject
        {
            ["payload"] = general["payload"]!.GetValue<string>(),
            ["protected"] = signature["protected"]!.GetValue<string>(),
            ["signature"] = signature["signature"]!.GetValue<string>(),
        }.ToJsonString();
    }
}
