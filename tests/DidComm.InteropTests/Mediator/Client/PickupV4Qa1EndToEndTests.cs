using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ProblemReports = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// QA-1 for #100 (Message Pickup 4.0) against the real <c>AddMediator()</c> + <c>MapDidCommMediator</c>
/// host and, where a client is involved, the real <c>AddMediatorClient()</c> container: the empty
/// answer of each version on one store over HTTP and a WebSocket (4.0 L210, L259, L265; 3.0 L143,
/// L116); the version the client picks against Discover Features responders that read
/// <c>match</c> the three ways the field does and disclose 4.0; a problem report of either type
/// naming the request in an upper-cased <c>pthid</c> completing a request of either version
/// (v2.1 threading.md L9, problems.md); the 512-character cap on every 4.0 request type and every
/// thread field; one WebSocket on which a recipient switches versions between requests; and the
/// client's refusal of delivered attachments that are not in encrypted form (owner decision OD-2),
/// with real envelopes the mailbox itself would never have stored (OD-1), put in its store directly.
/// </summary>
public sealed class PickupV4Qa1EndToEndTests
{
    private const int IdCap = 512;

    public static TheoryData<bool> Transports => new() { false, true };

    // ---- 2. the empty answer of each version, on one store ----------------------------------------

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task With_nothing_for_the_DID_asked_about_4_0_answers_an_empty_delivery_and_3_0_a_status_each_on_the_thread_repeating_the_DID(bool webSocket)
    {
        // One grant, two registered DIDs; the store holds a message for the second only, so "nothing
        // for the first" is the mailbox's answer about that DID, not an empty store.
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        var first = await mediator.NewRecipientAsync();
        var second = await mediator.NewRecipientAsync();
        await mediator.EnrolAsync(bob, first.Did, second.Did);
        var alice = await mediator.NewSenderAsync();
        await PickupE2E.SendAsync(mediator, alice, second, "for the second DID");
        using var socket = webSocket ? await mediator.ConnectAsync() : null;
        Task<PickupAnswer> AskAsync(Message request) => socket is null ? mediator.AskAsync(bob, request) : mediator.AskOverSocketAsync(socket, bob, request);

        var request4 = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10, first.Did);
        var answer4 = await AskAsync(request4);
        var request3 = MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10, first.Did);
        var answer3 = await AskAsync(request3);
        var unfiltered = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var control = await AskAsync(unfiltered);

        answer4.Delivery40(thid: request4.Id, recipientDid: first.Did).Should().BeEmpty("nothing waits for the DID asked about (L210)");
        answer4.Message.Attachments.Should().NotBeNull().And.BeEmpty("\"attachments\": [] is on the wire (L259)");
        answer4.Message.Body!["recipient_did"]!.GetValue<string>().Should().Be(first.Did, "the delivery repeats the recipient_did of the request (L265)");
        var status3 = answer3.Status(request3.Id);
        status3.MessageCount.Should().Be(0, "3.0 answers with its status when nothing can be delivered (3.0 L143)");
        status3.RecipientDid.Should().Be(first.Did, "a status repeats the recipient_did asked about (3.0 L116)");
        control.Delivery40(thid: unfiltered.Id).Should().ContainSingle("precondition: the store is not empty, and nothing above leased its message");
    }

    // ---- 5. third-party Discover Features responders that disclose 4.0 ------------------------------

    public static TheoryData<string, MessagePickupVersion> Disclosers => new()
    {
        { "regex full match (Identus-like), both versions as mediator", MessagePickupVersion.V4 },
        { "prefix without .* (Blocktrust-like), both versions with roles []", MessagePickupVersion.V4 },
        { "spec wildcard, 4.0 without roles", MessagePickupVersion.V4 },
        { "spec wildcard, 4.0 in the recipient role only, 3.0 as mediator", MessagePickupVersion.V3 },
    };

    [Theory]
    [MemberData(nameof(Disclosers))]
    public async Task A_client_negotiates_with_a_responder_that_reads_match_its_own_way_and_speaks_what_counts(string kind, MessagePickupVersion expected)
    {
        var discovery = Qa1Discloser.Like(kind);
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer, o => o.PostConfigureServices = services => services.AddSingleton<IProtocolHandler>(discovery));
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.Negotiate);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var note = await PickupE2E.SendAsync(mediator, alice, recipient, kind);

        var status = await harness.Pickup.GetStatusAsync(mediator.Did, bob.Did);
        var batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);

        discovery.Matches.Should().Equal(FeatureNegotiation.PickupQueryMatch4, FeatureNegotiation.PickupQueryMatch3);
        discovery.Disclosed.Should().Contain(id => id.Contains("message-pickup/4.0", StringComparison.Ordinal), "precondition: the responder disclosed 4.0 for the 4.x query, read its way");
        harness.Channel.PickupVersions.TryGet(mediator.Did, out var version).Should().BeTrue();
        version.Should().Be(expected, kind);
        status.MessageCount.Should().Be(1);
        PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, recipient, note);
        var prefix = expected == MessagePickupVersion.V4 ? "4.0/" : "3.0/";
        await mediator.FlushObserversAsync();
        observer.PickupKindsFrom(bob.Did).Should().Equal("discover-features/queries", prefix + "status-request", prefix + "delivery-request", prefix + "messages-received");
    }

    // ---- 8 and 9. a problem report naming the request in an upper-cased pthid -----------------------

    public static TheoryData<MessagePickupVersion, string, bool> ProblemCells
    {
        get
        {
            var data = new TheoryData<MessagePickupVersion, string, bool>();
            foreach (var version in new[] { MessagePickupVersion.V4, MessagePickupVersion.V3 })
            {
                foreach (var report in new[] { "4.0 problem-report", "report-problem 2.0" })
                {
                    data.Add(version, report, false);
                    data.Add(version, report, true);
                }
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ProblemCells))]
    public async Task A_problem_report_whose_pthid_is_the_request_id_upper_cased_fails_the_request_of_either_version_with_its_code(MessagePickupVersion version, string report, bool webSocket)
    {
        // Both pickup protocols are answered by a handler that sends a problem report on the
        // request's thread, written in upper case (ids compare ignoring case, v2.1 threading.md L9).
        // A 4.0 problem report on a 3.0 request's thread is a cross-version answer; it still names
        // the request, and a problem report completes the request it names whatever it expected.
        var code = report == "4.0 problem-report" ? MessagePickup.LiveModeNotSupportedCode : "e.p.me.res.not-available";
        Message Answer(Message request)
        {
            var pthid = request.Id.ToUpperInvariant();
            return report == "4.0 problem-report"
                ? MessagePickup40.CreateLiveModeNotSupported(request.To![0], request.From!, pthid)
                : ProblemReports.Create(request.To![0], request.From!, code, pthid, "refused by the test");
        }
        var four = new Qa1AnsweringHandler(MessagePickup40.ProtocolUri, Answer);
        var three = new Qa1AnsweringHandler(MessagePickup.ProtocolUri, Answer);
        await using var mediator = await MediatorTestServer.StartAsync(o => o.PostConfigureServices = services =>
        {
            services.AddSingleton<IProtocolHandler>(four);
            services.AddSingleton<IProtocolHandler>(three);
        });
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, version, webSocket);
        var (bob, _) = await PickupE2E.EnrolAsync(mediator, harness);
        var pickup = new PickupClient(new LetterIdExchange(harness.Channel, new LetterIds("request")), logger: null);

        var thrown = await Record.ExceptionAsync(() => PickupE2E.Within(pickup.GetStatusAsync(mediator.Did, bob.Did, serviceEndpointOverride: webSocket ? harness.WebSocketEndpoint : null)));

        thrown.Should().NotBeNull();
        var answered = (version == MessagePickupVersion.V4 ? four : three).Answered.Should().ContainSingle("precondition: the request reached the pickup handler of its version").Subject;
        answered.Should().StartWith("request-abcdef-", "precondition: the request id holds letters, so upper-casing it changes it");
        var failure = thrown.Should().BeOfType<MediationProtocolException>().Subject;
        failure.ProblemCode.Should().Be(code);
        failure.RequestId.Should().Be(answered);
        failure.Message.Should().NotContain(answered.ToUpperInvariant(), "the text names the request as sent, never the thread as it arrived");
        harness.Channel.PendingCount.Should().Be(0);
    }

    // ---- 8. the 512-character cap on every 4.0 request type ----------------------------------------

    public static TheoryData<string, string, bool> OverCapCells
    {
        get
        {
            var data = new TheoryData<string, string, bool>();
            foreach (var request in new[] { "status-request", "delivery-request", "messages-received", "live-delivery-change" })
            {
                foreach (var member in new[] { "id", "thid", "pthid" })
                {
                    data.Add(request, member, false);
                    data.Add(request, member, true);
                }
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(OverCapCells))]
    public async Task A_4_0_request_of_any_type_with_a_513_character_id_thid_or_pthid_is_refused_before_dispatch_and_changes_nothing(string name, string member, bool webSocket)
    {
        // The library's packers refuse such a field, so the request is authcrypted from JSON written
        // by hand, as another implementation can send it. Over HTTP: 400 with an empty body; on a
        // WebSocket: nothing, and the socket keeps serving. The queued message is neither leased,
        // removed nor pushed.
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer);
        var (bob, recipient, alice) = await EnrolAsync(mediator);
        var queued = await PickupV4E2E.QueueAsync(mediator, recipient, Encoding.UTF8.GetBytes(await alice.AuthcryptAsync(alice.Note(recipient.Did, "kept"), recipient.Did)));
        var over = mediator.Prepare(Request40(name, bob, mediator, queued));
        var refusedId = over.Id;
        switch (member)
        {
            case "id": over.Id = refusedId = PickupV4E2E.IdOfLength("over-", IdCap + 1); break;
            case "thid": over.Thid = PickupV4E2E.IdOfLength("over-", IdCap + 1); break;
            case "pthid": over.Pthid = PickupV4E2E.IdOfLength("over-", IdCap + 1); break;
        }
        var packed = await PickupV4E2E.RawAuthcryptAsync(mediator, bob, ReturnRouteTestHost.UnvalidatedJson(over));
        using var socket = webSocket ? await mediator.ConnectAsync() : null;

        (HttpStatusCode Status, string? ContentType, string Body)? refused = null;
        if (socket is null)
            refused = await mediator.PostAsync(packed);
        else
            await PickupV4E2E.SendAsync(socket, packed);
        var fresh = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var answer = socket is null ? await mediator.AskAsync(bob, fresh) : await mediator.AskOverSocketAsync(socket, bob, fresh);

        if (refused is { } response)
        {
            response.Status.Should().Be(HttpStatusCode.BadRequest);
            response.Body.Should().BeEmpty();
        }
        answer.Delivery40(fresh.Id).Should().ContainSingle("the refused request leased, removed and pushed nothing; on a socket, the next frame answers the next request")
            .Which.Id.Should().Be(queued);
        await mediator.FlushObserversAsync();
        observer.From(bob.Did).Should().NotContain(r => r.Id == refusedId, "it never reached the dispatcher");
        observer.From(bob.Did).Should().Contain(r => r.Id == fresh.Id, "precondition: the valid one did");
    }

    public static TheoryData<string, bool> AtCapAckCells => new() { { "id", false }, { "id", true }, { "thid", false }, { "thid", true } };

    [Theory]
    [MemberData(nameof(AtCapAckCells))]
    public async Task A_4_0_messages_received_whose_id_or_thid_is_512_characters_is_applied_and_not_answered(string field, bool webSocket)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var (bob, recipient, alice) = await EnrolAsync(mediator);
        var queued = await PickupV4E2E.QueueAsync(mediator, recipient, Encoding.UTF8.GetBytes(await alice.AuthcryptAsync(alice.Note(recipient.Did, "acknowledged"), recipient.Did)));
        var ack = mediator.Prepare(MessagePickup40.CreateMessagesReceived(bob.Did, mediator.Did, new[] { queued }));
        if (field == "id")
            ack.Id = PickupV4E2E.IdOfLength("ack-", IdCap);
        else
            ack.Thid = PickupV4E2E.IdOfLength("ack-", IdCap);
        var packed = await PickupV4E2E.PackAsIsAsync(mediator, bob, ack);
        using var socket = webSocket ? await mediator.ConnectAsync() : null;

        PickupStatus40 after;
        if (socket is null)
        {
            await PickupV4E2E.PostExpectingNoAnswerAsync(mediator, packed);
            after = await PickupV4E2E.Status40Async(mediator, bob);
        }
        else
        {
            await PickupV4E2E.SendAsync(socket, packed);
            var status = MessagePickup40.CreateStatusRequest(bob.Did, mediator.Did);
            after = (await mediator.AskOverSocketAsync(socket, bob, status)).Status40(status.Id);
        }

        after.MessageCount.Should().Be(0, "the acknowledgement on a 512-character thread was applied; the next frame on the socket is the status, so nothing answered it");
        mediator.Store.MessageCount.Should().Be(0);
    }

    // ---- 9. one WebSocket, both versions ------------------------------------------------------------

    [Fact]
    public async Task On_one_WebSocket_a_recipient_switching_versions_between_requests_gets_each_answer_in_its_requests_version_from_one_mailbox()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var (bob, recipient, alice) = await EnrolAsync(mediator);
        foreach (var content in new[] { "n1", "n2", "n3" })
            await PickupE2E.SendAsync(mediator, alice, recipient, content);
        using var socket = await mediator.ConnectAsync();
        Task<PickupAnswer> AskAsync(Message request) => mediator.AskOverSocketAsync(socket, bob, request);

        var s3 = MessagePickup.CreateStatusRequest(bob.Did, mediator.Did);
        var counted3 = (await AskAsync(s3)).Status(s3.Id);
        var d4 = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 1);
        var first = (await AskAsync(d4)).Delivery40(d4.Id).Should().ContainSingle().Subject;
        var a3 = MessagePickup.CreateMessagesReceived(bob.Did, mediator.Did, new[] { first.Id });
        var afterA3 = (await AskAsync(a3)).Status(a3.Id);
        var s4 = MessagePickup40.CreateStatusRequest(bob.Did, mediator.Did);
        var counted4 = (await AskAsync(s4)).Status40(s4.Id);
        var d3 = MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 1);
        var second = (await AskAsync(d3)).Delivery(d3.Id).Should().ContainSingle().Subject;
        // A 4.0 acknowledgement as 4.0 sends it, without return_route (L35): nothing answers it, so
        // the next frame on the socket answers the request after it.
        var a4 = PickupV4E2E.Stamped(mediator, MessagePickup40.CreateMessagesReceived(bob.Did, mediator.Did, new[] { second.Id }));
        await PickupV4E2E.SendAsync(socket, await PickupV4E2E.PackAsIsAsync(mediator, bob, a4));
        var d4Again = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var third = (await AskAsync(d4Again)).Delivery40(d4Again.Id).Should().ContainSingle().Subject;
        var a3Again = MessagePickup.CreateMessagesReceived(bob.Did, mediator.Did, new[] { third.Id });
        var afterA3Again = (await AskAsync(a3Again)).Status(a3Again.Id);
        var drained3 = MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var empty3 = (await AskAsync(drained3)).Status(drained3.Id);
        var drained4 = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var empty4 = (await AskAsync(drained4)).Delivery40(drained4.Id);

        counted3.MessageCount.Should().Be(3);
        afterA3.MessageCount.Should().Be(2, "the 3.0 acknowledgement removed the message the 4.0 delivery leased");
        counted4.MessageCount.Should().Be(2);
        afterA3Again.MessageCount.Should().Be(0, "the 4.0 acknowledgement removed the message the 3.0 delivery leased, and the 3.0 one the last");
        empty3.MessageCount.Should().Be(0);
        empty4.Should().BeEmpty();
        (await ContentsAsync(recipient, first, second, third)).Should().Equal("n1", "n2", "n3");
        mediator.Store.MessageCount.Should().Be(0);
    }

    // ---- 7. OD-2 with real envelopes ------------------------------------------------------------------

    public static TheoryData<MessagePickupVersion> Versions => new() { MessagePickupVersion.V4, MessagePickupVersion.V3 };

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task A_delivered_plaintext_or_signed_only_message_is_a_failed_item_left_queued_while_anoncrypt_authcrypt_and_signed_then_encrypted_open(MessagePickupVersion version)
    {
        // The mailbox refuses what is not encrypted (OD-1), so these records are written to the store
        // directly, as a host's own store could hold them. The client judges each attachment by its
        // outermost form (3.0 L172, 4.0 L261): only a JWE is unpacked and acknowledged (OD-2).
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, version);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var forms = new (string Id, Func<Message, Task<string>> Pack)[]
        {
            ("plaintext", note => alice.Client.PackPlaintextAsync(note)),
            ("signed", note => alice.Client.PackSignedAsync(note, alice.Did)),
            ("anoncrypt", async note => (await alice.Client.PackEncryptedAsync(note, new PackEncryptedOptions(Recipients: new[] { recipient.Did }))).Message),
            ("authcrypt", note => alice.AuthcryptAsync(note, recipient.Did)),
            ("signed-then-anoncrypted", async note => (await alice.Client.PackEncryptedAsync(note, new PackEncryptedOptions(Recipients: new[] { recipient.Did }, SignFrom: alice.Did))).Message),
        };
        var notes = new Dictionary<string, Message>(StringComparer.Ordinal);
        foreach (var (id, pack) in forms)
        {
            var note = alice.Note(recipient.Did, id);
            notes[id] = note;
            var stored = await mediator.Store.EnqueueAsync(
                new QueuedMessage(id, bob.Did, recipient.Did, Encoding.UTF8.GetBytes(await pack(note)), mediator.Clock.Now), lease: null, mediator.Clock.Now, CancellationToken.None);
            stored.Should().Be(EnqueueOutcome.Enqueued);
        }

        var batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);

        batch.Items.Select(i => i.Id).Should().Equal(forms.Select(f => f.Id), "every record is delivered, in order");
        foreach (var refused in batch.Items.Take(2))
        {
            refused.Unpacked.Should().BeNull(refused.Id);
            refused.Error.Should().BeOfType<MalformedMessageException>(refused.Id).Which.Message.Should().Contain("encrypted form");
            refused.Error!.Message.Should().NotContain(alice.Did).And.NotContain(recipient.Did);
        }
        foreach (var opened in batch.Items.Skip(2))
        {
            opened.Error.Should().BeNull(opened.Id);
            opened.Unpacked!.Message.Id.Should().Be(notes[opened.Id].Id);
            opened.Unpacked.Encrypted.Should().BeTrue();
        }
        batch.Items.Single(i => i.Id == "authcrypt").Unpacked!.Authenticated.Should().BeTrue();
        batch.Items.Single(i => i.Id == "anoncrypt").Unpacked!.Authenticated.Should().BeFalse();
        mediator.Store.MessageCount.Should().Be(2, "only the three JWEs were acknowledged");

        // After the lease the two are delivered again: they were never acknowledged.
        mediator.Clock.Advance(TimeSpan.FromSeconds(61));
        var again = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);
        again.Items.Select(i => (i.Id, Failed: i.Error is MalformedMessageException)).Should().Equal(("plaintext", true), ("signed", true));
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private static async Task<(TestPeer Bob, TestPeer Recipient, MediatorSender Alice)> EnrolAsync(MediatorTestServer mediator)
    {
        var bob = await mediator.NewPeerAsync();
        var recipient = await mediator.NewRecipientAsync();
        await mediator.EnrolAsync(bob, recipient.Did);
        return (bob, recipient, await mediator.NewSenderAsync());
    }

    private static Message Request40(string name, TestPeer bob, MediatorTestServer mediator, string queued) => name switch
    {
        "status-request" => MessagePickup40.CreateStatusRequest(bob.Did, mediator.Did),
        "delivery-request" => MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10),
        "messages-received" => MessagePickup40.CreateMessagesReceived(bob.Did, mediator.Did, new[] { queued }),
        "live-delivery-change" => MessagePickup40.CreateLiveDeliveryChange(bob.Did, mediator.Did, liveDelivery: true),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static async Task<IReadOnlyList<string>> ContentsAsync(TestPeer recipient, params DeliveredMessage[] delivered)
    {
        var contents = new List<string>();
        foreach (var message in delivered)
            contents.Add((await recipient.Client.UnpackAsync(Encoding.UTF8.GetString(message.Payload.Span))).Message.Body!["content"]!.GetValue<string>());
        return contents;
    }
}

/// <summary>
/// A pickup protocol handler of the test's own that answers every message of <paramref name="protocolUri"/>
/// with what <paramref name="answer"/> builds, and records the id of each message it answered.
/// Registered after <c>AddMediator()</c>, so it is the handler the dispatcher picks for the protocol.
/// </summary>
internal sealed class Qa1AnsweringHandler(string protocolUri, Func<Message, Message> answer) : IProtocolHandler
{
    private readonly object _gate = new();
    private readonly List<string> _answered = new();

    public string ProtocolUri => protocolUri;

    public IReadOnlyList<string> Answered
    {
        get
        {
            lock (_gate)
                return _answered.ToArray();
        }
    }

    public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        lock (_gate)
            _answered.Add(message.Id);
        return Task.FromResult<Message?>(answer(message));
    }
}

/// <summary>
/// A Discover Features responder that reads <c>match</c> one of the three ways the field does and
/// discloses from its own feature list, Message Pickup 4.0 among them (the shapes of
/// <see cref="ThirdPartyDiscoverFeatures"/>, which disclose 3.0 only). It records the match strings
/// asked and the ids disclosed.
/// </summary>
internal sealed class Qa1Discloser : IProtocolHandler
{
    private readonly Func<string, string, bool> _reads;
    private readonly JsonArray _features;
    private readonly List<string> _matches = new();
    private readonly List<string> _disclosed = new();
    private readonly object _gate = new();

    private Qa1Discloser(Func<string, string, bool> reads, JsonArray features)
    {
        _reads = reads;
        _features = features;
    }

    public string ProtocolUri => DiscoverFeatures.ProtocolUri;

    public IReadOnlyList<string> Matches { get { lock (_gate) return _matches.ToArray(); } }

    public IReadOnlyList<string> Disclosed { get { lock (_gate) return _disclosed.ToArray(); } }

    private const string Pickup4 = "https://didcomm.org/message-pickup/4.0";
    private const string Pickup3 = "https://didcomm.org/messagepickup/3.0";

    public static Qa1Discloser Like(string kind) => kind switch
    {
        "regex full match (Identus-like), both versions as mediator" => new(
            (match, id) => Regex.IsMatch(id, "^(?:" + match + ")$"),
            Features((CoordinateMediation20.ProtocolUri, new JsonArray("responder")), (Pickup3, new JsonArray("mediator")), (Pickup4, new JsonArray("mediator")))),
        "prefix without .* (Blocktrust-like), both versions with roles []" => new(
            (match, id) => match == "*" || id.StartsWith(match.Replace(".*", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal),
            Features((CoordinateMediation20.ProtocolUri, new JsonArray()), (Pickup3, new JsonArray()), (Pickup4, new JsonArray()))),
        "spec wildcard, 4.0 without roles" => new(
            SpecWildcard,
            Features((Pickup4, null))),
        "spec wildcard, 4.0 in the recipient role only, 3.0 as mediator" => new(
            SpecWildcard,
            Features((Pickup4, new JsonArray("recipient")), (Pickup3, new JsonArray("mediator")))),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // feature_discovery.md L52: a trailing * matches a prefix.
    private static bool SpecWildcard(string match, string id)
        => match.EndsWith('*') ? id.StartsWith(match[..^1], StringComparison.Ordinal) : string.Equals(match, id, StringComparison.Ordinal);

    public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        if (!string.Equals(message.Type, DiscoverFeatures.QueriesType, StringComparison.Ordinal) || message.From is null || message.To is not { Count: > 0 })
            return Task.FromResult<Message?>(null);

        var disclosures = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var query in message.Body?["queries"]?.AsArray() ?? new JsonArray())
            {
                if (query?["feature-type"]?.GetValue<string>() != "protocol" || query["match"]?.GetValue<string>() is not { } match)
                    continue;
                _matches.Add(match);
                foreach (var feature in _features)
                {
                    var id = feature!["id"]!.GetValue<string>();
                    if (_reads(match, id) && seen.Add(id))
                    {
                        disclosures.Add(feature.DeepClone());
                        _disclosed.Add(id);
                    }
                }
            }
        }

        return Task.FromResult<Message?>(new MessageBuilder()
            .WithType(DiscoverFeatures.DiscloseType)
            .WithFrom(message.To[0])
            .WithTo(message.From)
            .WithThid(message.Id)
            .WithBody(new JsonObject { ["disclosures"] = disclosures })
            .Build());
    }

    private static JsonArray Features(params (string Id, JsonArray? Roles)[] entries)
    {
        var array = new JsonArray();
        foreach (var (id, roles) in entries)
        {
            var feature = new JsonObject { ["feature-type"] = "protocol", ["id"] = id };
            if (roles is not null)
                feature["roles"] = roles.DeepClone();
            array.Add(feature);
        }
        return array;
    }
}
