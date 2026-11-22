using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// Message Pickup 4.0 on the wire against the real <c>AddMediator()</c> + <c>MapDidCommMediator</c>
/// host (#100), with hand-rolled requests from an enrolled peer (stamped and authcrypted as
/// <c>SendAsync</c> sends them, unless a test leaves something out on purpose) and every stored
/// message a real encrypted DIDComm message: <c>message_count_limit</c> (L206, L255; R-5), the
/// byte budget (<c>max_delivery_bytes</c>), <c>messages-received</c> without <c>return_route</c>
/// over HTTP and on a WebSocket session (L35, L294; R-4), live mode asked for over HTTP (L342-L369),
/// replay admission of 4.0 requests (FR-RR-08: every mediator-service request is in scope), and the
/// 512-character cap on ids and threads (FR-MSG-02, FR-MSG-11).
/// </summary>
public sealed class PickupV4WireEndToEndTests
{
    private const int Kibibyte = 1024;
    private const int Mebibyte = 1024 * 1024;

    // The cap on message and thread ids (FR-MSG-02, FR-MSG-11), written out rather than read from
    // the product's constant, so a change to that constant shows here.
    private const int IdCap = 512;

    public static TheoryData<bool> Transports => new() { false, true };

    // ---- message_count_limit ---------------------------------------------------------------

    [Fact]
    public async Task A_message_count_limit_of_zero_gets_an_empty_delivery_and_leases_nothing()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var (bob, recipient, alice) = await EnrolAsync(mediator);
        for (var i = 0; i < 3; i++)
            await PickupE2E.SendAsync(mediator, alice, recipient, $"m{i}");

        var zero = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, messageCountLimit: 0);
        var none = (await mediator.AskAsync(bob, zero)).Delivery40(zero.Id);
        var all = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, messageCountLimit: 10);
        var delivered = (await mediator.AskAsync(bob, all)).Delivery40(all.Id);

        none.Should().BeEmpty("a limit of 0 is valid and delivers nothing (R-5, L210)");
        delivered.Should().HaveCount(3, "the request with limit 0 leased nothing: the clock never moved, yet every message is delivered");
    }

    [Fact]
    public async Task A_message_count_limit_of_two_with_five_queued_delivers_the_two_oldest()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var (bob, recipient, alice) = await EnrolAsync(mediator);
        var notes = new List<string>();
        for (var i = 0; i < 5; i++)
            notes.Add((await PickupE2E.SendAsync(mediator, alice, recipient, $"m{i}")).Id);

        var two = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, messageCountLimit: 2);
        var first = (await mediator.AskAsync(bob, two)).Delivery40(two.Id);
        var rest = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, messageCountLimit: 10);
        var second = (await mediator.AskAsync(bob, rest)).Delivery40(rest.Id);

        first.Should().HaveCount(2, "the number of attached messages MUST NOT exceed message_count_limit (L255)");
        (await InnerIdsAsync(recipient, first)).Should().Equal(notes.Take(2));
        (await InnerIdsAsync(recipient, second)).Should().Equal(notes.Skip(2), "the first two are leased; the other three come next");
    }

    [Fact]
    public async Task A_message_count_limit_above_the_batch_bound_is_clamped_to_it_however_large()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.Pickup.MaxDeliveryBatch = 3);
        var (bob, recipient, alice) = await EnrolAsync(mediator);
        for (var i = 0; i < 5; i++)
            await PickupE2E.SendAsync(mediator, alice, recipient, $"m{i}");

        var fifty = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, messageCountLimit: 50);
        var first = (await mediator.AskAsync(bob, fifty)).Delivery40(fifty.Id);
        // Beyond 32 bits: written by hand (the builder takes an int). Clamped, then capped.
        var huge = new MessageBuilder()
            .WithType(MessagePickup40.DeliveryRequestType)
            .WithFrom(bob.Did)
            .WithTo(mediator.Did)
            .WithBody(new JsonObject { ["message_count_limit"] = 99_999_999_999L })
            .Build();
        var second = (await mediator.AskAsync(bob, huge)).Delivery40(huge.Id);

        first.Should().HaveCount(3, "MaxDeliveryBatch caps a larger limit");
        second.Should().HaveCount(2, "a limit beyond 32 bits is clamped, then capped: what is left is delivered");
    }

    // ---- the byte budget -------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Five_300_KiB_messages_under_the_default_budget_arrive_one_per_4_0_delivery_and_every_answer_fits_one_mebibyte(bool webSocket)
    {
        // Slow for this suite on purpose: 1.5 MiB of real ciphertext is packed, relayed and opened.
        LeaseRecordingStore store = null!;
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s => LeaseRecordingStore.Register(s, x => store = x)); // max_delivery_bytes = MaxReceiveBytes = 1 MiB
        var budget = mediator.Queue.Coordinator.Sizing.BatchPayloadBudget;
        budget.Should().BeInRange(300 * Kibibyte, 2 * 300 * Kibibyte - 1, "precondition: one of these messages fits the byte budget of a lease, two do not");
        var (bob, recipient, alice) = await EnrolAsync(mediator);
        var queued = new List<(string Id, byte[] Packed, string NoteId)>();
        for (var i = 0; i < 5; i++)
        {
            var (packed, note) = await PickupV4E2E.PackSizedAsync(alice, recipient, 300 * Kibibyte, $"big-{i}");
            queued.Add((await PickupV4E2E.QueueAsync(mediator, recipient, packed), packed, note.Id));
        }
        queued.Should().OnlyContain(q => q.Packed.Length > 300 * Kibibyte - 64 && q.Packed.Length <= 300 * Kibibyte);

        using var socket = webSocket ? await mediator.ConnectAsync() : null;
        Task<PickupAnswer> AskAsync(Message request)
            => socket is null ? mediator.AskAsync(bob, request) : mediator.AskOverSocketAsync(socket, bob, request);

        for (var i = 0; i < 5; i++)
        {
            var request = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
            var answer = await AskAsync(request);
            answer.Packed.Length.Should().BeLessThanOrEqualTo(Mebibyte, "every packed answer fits max_delivery_bytes");
            var delivered = answer.Delivery40(request.Id).Should().ContainSingle("two 300 KiB messages would not fit one mebibyte once packed").Subject;
            delivered.Id.Should().Be(queued[i].Id);
            delivered.Payload.Span.SequenceEqual(queued[i].Packed).Should().BeTrue("the message is relayed byte for byte");
            (await recipient.Client.UnpackAsync(System.Text.Encoding.UTF8.GetString(delivered.Payload.Span))).Message.Id.Should().Be(queued[i].NoteId);

            // 4.0: the acknowledgement goes without return_route and gets no answer (L35, L294).
            var ack = PickupV4E2E.Stamped(mediator, MessagePickup40.CreateMessagesReceived(bob.Did, mediator.Did, new[] { delivered.Id }));
            if (socket is null)
                await PickupV4E2E.PostExpectingNoAnswerAsync(mediator, await PickupV4E2E.PackAsIsAsync(mediator, bob, ack));
            else
                await PickupV4E2E.SendAsync(socket, await PickupV4E2E.PackAsIsAsync(mediator, bob, ack));
            // The next frame on the socket is this status's answer: nothing answered the acknowledgement.
            var status = MessagePickup40.CreateStatusRequest(bob.Did, mediator.Did);
            (await AskAsync(status)).Status40(status.Id).MessageCount.Should().Be(4 - i);
        }

        var last = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        (await AskAsync(last)).Delivery40(last.Id).Should().BeEmpty();
        store.Inner.MessageCount.Should().Be(0);

        // The byte budget bounded every lease before anything was packed (FR-PICK-03): each 4.0
        // delivery-request leased one message, never all five to give four back after packing.
        var leases = store.Leases.Where(l => !l.Live).ToArray();
        leases.Should().HaveCount(6, "five deliveries and the empty one");
        leases.Should().OnlyContain(l => l.MaxMessages == 10 && l.MaxPayloadBytes == budget, "the request's limit and the budget derived from max_delivery_bytes");
        leases.Select(l => l.Taken).Should().Equal(1, 1, 1, 1, 1, 0);
    }

    // ---- messages-received without return_route -------------------------------------------

    [Fact]
    public async Task A_4_0_messages_received_over_HTTP_without_return_route_is_answered_202_and_applied()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var (bob, recipient, alice) = await EnrolAsync(mediator);
        await PickupE2E.SendAsync(mediator, alice, recipient, "one");
        await PickupE2E.SendAsync(mediator, alice, recipient, "two");
        var request = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var delivered = (await mediator.AskAsync(bob, request)).Delivery40(request.Id);

        var ack = PickupV4E2E.Stamped(mediator, MessagePickup40.CreateMessagesReceived(bob.Did, mediator.Did, new[] { delivered[0].Id }));
        ReturnRouteHeader.Read(ack).Mode.Should().Be(ReturnRouteMode.None, "precondition: as 4.0 sends it (L35)");
        await PickupV4E2E.PostExpectingNoAnswerAsync(mediator, await PickupV4E2E.PackAsIsAsync(mediator, bob, ack));
        var afterFirst = await PickupV4E2E.Status40Async(mediator, bob);

        // With return_route: all it is still applied and still not answered (R-4: nothing answers it).
        var withHeader = MessagePickup40.CreateMessagesReceived(bob.Did, mediator.Did, new[] { delivered[1].Id });
        await PickupV4E2E.PostExpectingNoAnswerAsync(mediator, await mediator.AuthcryptAsync(bob, mediator.Prepare(withHeader)));
        var afterSecond = await PickupV4E2E.Status40Async(mediator, bob);

        afterFirst.MessageCount.Should().Be(1, "the header-less acknowledgement was applied");
        afterSecond.MessageCount.Should().Be(0);
        mediator.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_header_less_4_0_messages_received_on_the_session_socket_is_applied_and_the_session_stays_in_mode_all()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var (bob, recipient, alice) = await EnrolAsync(mediator);
        await PickupE2E.SendAsync(mediator, alice, recipient, "on the socket");
        using var socket = await mediator.ConnectAsync();
        var request = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var delivered = (await mediator.AskOverSocketAsync(socket, bob, request)).Delivery40(request.Id).Should().ContainSingle().Subject;

        // The acknowledgement and then a status-request, both without return_route. The session the
        // delivery-request opened (mode all) is unchanged by a message without the header (FR-RR-05),
        // so the status-request is still answered on the socket — and the first frame back is that
        // answer, so nothing answered the acknowledgement.
        var ack = PickupV4E2E.Stamped(mediator, MessagePickup40.CreateMessagesReceived(bob.Did, mediator.Did, new[] { delivered.Id }));
        await PickupV4E2E.SendAsync(socket, await PickupV4E2E.PackAsIsAsync(mediator, bob, ack));
        var status = PickupV4E2E.Stamped(mediator, MessagePickup40.CreateStatusRequest(bob.Did, mediator.Did));
        await PickupV4E2E.SendAsync(socket, await PickupV4E2E.PackAsIsAsync(mediator, bob, status));
        var answer = await mediator.ReceiveOverSocketAsync(socket, bob);

        answer.Status40(status.Id).MessageCount.Should().Be(0, "the acknowledgement was applied");
        mediator.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task The_same_header_less_acknowledgement_without_created_time_is_dropped_by_replay_admission_and_removes_nothing()
    {
        // The control of the test above: a 4.0 messages-received is a mediator-service message, so it
        // is in replay scope without return_route (FR-RR-08), and without created_time it is refused
        // before dispatch. The client stamps it by protocol (FR-RR-09, PickupV4LiveEndToEndTests).
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer);
        var (bob, recipient, alice) = await EnrolAsync(mediator);
        await PickupE2E.SendAsync(mediator, alice, recipient, "on the socket");
        using var socket = await mediator.ConnectAsync();
        var request = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var delivered = (await mediator.AskOverSocketAsync(socket, bob, request)).Delivery40(request.Id).Should().ContainSingle().Subject;

        var ack = MessagePickup40.CreateMessagesReceived(bob.Did, mediator.Did, new[] { delivered.Id });
        ack.CreatedTime.Should().BeNull("precondition: not stamped");
        await PickupV4E2E.SendAsync(socket, await PickupV4E2E.PackAsIsAsync(mediator, bob, ack));
        var status = PickupV4E2E.Stamped(mediator, MessagePickup40.CreateStatusRequest(bob.Did, mediator.Did));
        await PickupV4E2E.SendAsync(socket, await PickupV4E2E.PackAsIsAsync(mediator, bob, status));
        var answer = await mediator.ReceiveOverSocketAsync(socket, bob);

        answer.Status40(status.Id).MessageCount.Should().Be(1, "nothing was removed");
        mediator.Store.MessageCount.Should().Be(1);
        await mediator.FlushObserversAsync();
        observer.From(bob.Did).Should().NotContain(r => r.Id == ack.Id, "the unstamped acknowledgement never reached the dispatcher");
        observer.From(bob.Did).Should().Contain(r => r.Id == status.Id, "precondition: the status-request after it did");
    }

    // ---- live mode over HTTP ---------------------------------------------------------------

    [Fact]
    public async Task Live_delivery_asked_for_over_HTTP_is_answered_with_the_4_0_problem_report_in_the_200_body()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var (bob, _, _) = await EnrolAsync(mediator);

        var request = MessagePickup40.CreateLiveDeliveryChange(bob.Did, mediator.Did, liveDelivery: true);
        var answer = await mediator.AskAsync(bob, request);
        var (code, comment) = answer.Problem40(pthid: request.Id);

        code.Should().Be("e.m.live-mode-not-supported");
        comment.Should().Be("Connection does not support Live Delivery");
        answer.Message.Thid.Should().BeNull("the report names the request's thread as pthid (L359-L369)");
    }

    // ---- replay scope ----------------------------------------------------------------------

    [Fact]
    public async Task A_4_0_request_without_created_time_is_answered_202_and_has_no_effect()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var (bob, recipient, alice) = await EnrolAsync(mediator);
        await PickupE2E.SendAsync(mediator, alice, recipient, "kept");

        var unstamped = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        ReturnRouteHeaderWriter.Write(unstamped, ReturnRouteMode.All, thread: null);
        await PickupV4E2E.PostExpectingNoAnswerAsync(mediator, await PickupV4E2E.PackAsIsAsync(mediator, bob, unstamped));
        var stamped = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var delivered = (await mediator.AskAsync(bob, stamped)).Delivery40(stamped.Id);

        delivered.Should().ContainSingle("the unstamped request was dropped before dispatch: it leased nothing");
    }

    [Fact]
    public async Task A_4_0_request_played_again_is_dropped()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var (bob, recipient, alice) = await EnrolAsync(mediator);
        await PickupE2E.SendAsync(mediator, alice, recipient, "first");
        await PickupE2E.SendAsync(mediator, alice, recipient, "second");
        var request = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, messageCountLimit: 1);
        var packed = await mediator.AuthcryptAsync(bob, mediator.Prepare(request));

        var first = await mediator.PostAsync(packed);
        var again = await mediator.PostAsync(packed);
        var fresh = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, messageCountLimit: 10);
        var rest = (await mediator.AskAsync(bob, fresh)).Delivery40(fresh.Id);

        first.Status.Should().Be(HttpStatusCode.OK);
        again.Status.Should().Be(HttpStatusCode.Accepted, "the same sender and message id is admitted once (FR-RR-08)");
        again.Body.Should().BeEmpty();
        rest.Should().ContainSingle("the replay leased nothing: had it been dispatched it would have taken the second message");
    }

    // ---- the 512-character id cap ----------------------------------------------------------

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task A_4_0_request_whose_id_or_thid_is_512_characters_is_answered_on_exactly_that_thread(bool webSocket)
    {
        // FR-MSG-02/11: an id or thid of up to 512 characters is valid. The mediator puts its answer
        // on the request's thread (thid, else id; DD-11), as written: not cut, not re-cased.
        await using var mediator = await MediatorTestServer.StartAsync();
        var (bob, recipient, alice) = await EnrolAsync(mediator);
        await PickupE2E.SendAsync(mediator, alice, recipient, "on a long thread");
        using var socket = webSocket ? await mediator.ConnectAsync() : null;
        Task<PickupAnswer> AskAsync(Message request)
            => socket is null ? mediator.AskAsync(bob, request) : mediator.AskOverSocketAsync(socket, bob, request);

        var status = MessagePickup40.CreateStatusRequest(bob.Did, mediator.Did);
        status.Id = PickupV4E2E.IdOfLength("status-", IdCap);
        var counted = (await AskAsync(status)).Status40(status.Id);
        var delivery = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        delivery.Thid = PickupV4E2E.IdOfLength("thread-", IdCap);
        var delivered = (await AskAsync(delivery)).Delivery40(delivery.Thid);
        var live = MessagePickup40.CreateLiveDeliveryChange(bob.Did, mediator.Did, liveDelivery: true);
        live.Id = PickupV4E2E.IdOfLength("live-", IdCap);
        var liveAnswer = await AskAsync(live);

        counted.MessageCount.Should().Be(1);
        delivered.Should().ContainSingle();
        if (webSocket)
            liveAnswer.Status40(live.Id).LiveDelivery.Should().BeTrue("a WebSocket session can go live (L340)");
        else
            liveAnswer.Problem40(pthid: live.Id).Code.Should().Be("e.m.live-mode-not-supported", "the problem report names the 512-character thread as pthid");
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task A_4_0_request_whose_id_is_513_characters_is_refused_before_dispatch_and_leases_nothing(bool webSocket)
    {
        // The library's packers refuse such an id, so the request is authcrypted from JSON written by
        // hand, as a peer that is not this library can send it. Refused at unpack (FR-MSG-02): over
        // HTTP a 400 with an empty body, on a WebSocket nothing at all, and the socket keeps serving.
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer);
        var (bob, recipient, alice) = await EnrolAsync(mediator);
        await PickupE2E.SendAsync(mediator, alice, recipient, "kept");
        var over = mediator.Prepare(MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10));
        over.Id = PickupV4E2E.IdOfLength("over-", IdCap + 1);
        var packed = await PickupV4E2E.RawAuthcryptAsync(mediator, bob, ReturnRouteTestHost.UnvalidatedJson(over));
        using var socket = webSocket ? await mediator.ConnectAsync() : null;

        (HttpStatusCode Status, string? ContentType, string Body)? refused = null;
        if (socket is null)
            refused = await mediator.PostAsync(packed);
        else
            await PickupV4E2E.SendAsync(socket, packed);
        // Then a valid request (on the same socket: its answer is the next frame, so nothing answered the first).
        var fresh = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var answer = socket is null ? await mediator.AskAsync(bob, fresh) : await mediator.AskOverSocketAsync(socket, bob, fresh);

        if (refused is { } response)
        {
            response.Status.Should().Be(HttpStatusCode.BadRequest);
            response.Body.Should().BeEmpty();
        }
        answer.Delivery40(fresh.Id).Should().ContainSingle("the refused request leased nothing");
        await mediator.FlushObserversAsync();
        observer.From(bob.Did).Should().NotContain(r => r.Id == over.Id, "it never reached the dispatcher");
        observer.From(bob.Did).Should().Contain(r => r.Id == fresh.Id, "precondition: the valid one did");
    }

    private static async Task<(TestPeer Bob, TestPeer Recipient, MediatorSender Alice)> EnrolAsync(MediatorTestServer mediator)
    {
        var bob = await mediator.NewPeerAsync();
        var recipient = await mediator.NewRecipientAsync();
        await mediator.EnrolAsync(bob, recipient.Did);
        return (bob, recipient, await mediator.NewSenderAsync());
    }

    private static async Task<IReadOnlyList<string>> InnerIdsAsync(TestPeer recipient, IReadOnlyList<DeliveredMessage> delivered)
    {
        var ids = new List<string>();
        foreach (var message in delivered)
            ids.Add((await recipient.Client.UnpackAsync(System.Text.Encoding.UTF8.GetString(message.Payload.Span))).Message.Id);
        return ids;
    }
}
