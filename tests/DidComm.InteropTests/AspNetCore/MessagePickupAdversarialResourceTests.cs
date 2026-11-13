using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Resolution;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// ADV-2 (#94), the attacks that need the real thing: the real endpoints with a recipient whose
/// own DID document makes its envelopes larger than the mediator allows for (JC-19), the cost of
/// one small request measured on the bytes the real session packs, and the size-bounded send of
/// the real HTTP and WebSocket sessions hammered from many threads while the session closes or is
/// downgraded under it.
/// </summary>
public sealed class MessagePickupAdversarialResourceTests(ITestOutputHelper output)
{
    private const int Kibibyte = 1024;
    private static readonly Regex OverTheBound = new(@"its packed envelope is (\d+) bytes, over the caller's bound of (\d+)", RegexOptions.Compiled);

    // ---- JC-19 over the real endpoint: the message behind the one no envelope can carry -------------

    /// <summary>
    /// The real thing behind <c>MessagePickupAdversarialResourceBoundsTests</c>: a recipient with 38
    /// key-agreement keys (the most a did:peer:2 of 2048 characters holds), a payload at the
    /// admission bound that any sender may send it, and one ordinary message behind. The recipient
    /// polls once a minute, the default <c>LeaseTimeout</c>. Every poll finds the lease of the big
    /// message run out, leases it again, cannot send it, and answers <c>status</c>: the message
    /// behind it is never delivered.
    /// </summary>
    [Fact]
    public async Task Over_http_a_message_the_recipients_envelope_cannot_carry_does_not_keep_the_one_behind_it_from_a_recipient_that_polls_once_a_minute()
    {
        const int budget = 352 * Kibibyte;
        await using var mediator = await PickupEndpointHost.StartAsync(o => o.MaxDeliveryBytes = budget);
        var wide = await mediator.CreateManyKeyPeerAsync(38);
        await mediator.EnrolAsync(wide);
        var big = await mediator.QueueAsync(wide, packedBytes: mediator.Queue.MaxAdmissiblePayloadBytes);
        var behind = await mediator.QueueAsync(wide, packedBytes: 86 * Kibibyte);

        var delivered = new List<string>();
        var answers = new List<string>();
        for (var poll = 0; poll < 4 && delivered.Count == 0; poll++)
        {
            var request = MessagePickup.CreateDeliveryRequest(wide.Did, mediator.Mediator, 10);
            var response = await mediator.PostAsync(wide, request);
            response.Body.Length.Should().BeLessThanOrEqualTo(budget);
            var answer = await mediator.AnswerAsync(wide, response);
            if (MessagePickup.TryParseDelivery(answer.Message, out var messages, out _))
                delivered.AddRange(messages!.Select(m => m.Id));
            else
                answers.Add("status(" + answer.Status(request.Id).MessageCount + ")");
            mediator.Clock.Advance(TimeSpan.FromSeconds(60));
        }

        mediator.Store.MessageCount.Should().Be(2, "nothing is removed on a size measurement");
        delivered.Should().Contain(behind.Id!, "a message that cannot be delivered must not block the ones behind it for a recipient that polls once per lease period; the polls were answered {0}", string.Join(", ", answers));
        delivered.Should().NotContain(big.Id!);
    }

    // ---- what one small request costs, measured on the real session ---------------------------------

    /// <summary>
    /// The most one <c>delivery-request</c> can make the mediator pack at the default settings: a
    /// 38-key recipient, one payload just under the admission bound and nine small ones behind it,
    /// so that the batch of ten and every trimmed batch down to the big one alone are over the
    /// budget. The session packs each of them in full before it can say "too large". The count is
    /// bounded (2 × <c>MaxDeliveryBatch</c> packs per request); the bytes and the time are measured
    /// and written to the output (the report quotes them). The big one is then passed over under
    /// the request's lease and the same request delivers the nine behind it with one more pack.
    /// </summary>
    [Fact]
    public async Task At_the_defaults_one_delivery_request_costs_a_bounded_number_of_full_packs_and_delivers_what_is_behind_the_message_that_does_not_fit()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var wide = await mediator.CreateManyKeyPeerAsync(38);
        await mediator.EnrolAsync(wide);
        var max = mediator.Queue.MaxAdmissiblePayloadBytes;
        const int small = 200;

        // The mediator relays bytes: these payloads stand for one large message and nine short ones.
        var payloads = new List<byte[]> { Filled(max - 9 * small - 64, 1) };
        for (var i = 0; i < 9; i++)
            payloads.Add(Filled(small, (byte)(2 + i)));
        var ids = new List<string>();
        foreach (var payload in payloads)
        {
            var queued = await mediator.Queue.DeliverAsync(wide.Did, payload);
            queued.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
            ids.Add(queued.MessageId!);
        }

        var request = MessagePickup.CreateDeliveryRequest(wide.Did, mediator.Mediator, int.MaxValue);
        var packedRequest = await mediator.Host.AuthcryptAsync(wide, mediator.Prepare(request));
        var before = TooLargeLines(mediator).Count;
        var clock = Stopwatch.StartNew();
        var response = await mediator.Host.PostAsync(packedRequest);
        clock.Stop();

        var packs = TooLargeLines(mediator).Skip(before).ToArray();
        var packedBytes = packs.Sum(p => (long)p.Packed);
        output.WriteLine($"One delivery-request of {packedRequest.Length} bytes: {packs.Length} full packs refused as too large, {packedBytes} bytes packed in all ({(double)packedBytes / packedRequest.Length:F0} bytes packed per request byte), {clock.ElapsedMilliseconds} ms.");

        packs.Length.Should().BeLessThanOrEqualTo(10, "the trim loop packs once per leased message at most, and a lease is at most MaxDeliveryBatch");
        packs.Should().OnlyContain(p => p.Bound == 1024 * 1024);

        // The big one keeps the request's lease, and the same request goes on: the nine behind it
        // are its answer, with one more pack, within the budget. Nothing is removed.
        (await mediator.AnswerAsync(wide, response)).Delivery(request.Id).Select(m => m.Id).Should().Equal(ids.Skip(1));
        response.Body.Length.Should().BeLessThanOrEqualTo(1024 * 1024);
        mediator.Store.MessageCount.Should().Be(10, "nothing is removed");

        // A second request finds the big one passed over and the rest delivered: a status, no pack.
        var second = MessagePickup.CreateDeliveryRequest(wide.Did, mediator.Mediator, int.MaxValue);
        var secondResponse = await mediator.PostAsync(wide, second);
        TooLargeLines(mediator).Count.Should().Be(before + packs.Length, "the second request packs no delivery");
        (await mediator.AnswerAsync(wide, secondResponse)).Status(second.Id).MessageCount.Should().Be(10);
    }

    private static IReadOnlyList<(int Packed, int Bound)> TooLargeLines(PickupEndpointHost mediator)
        => mediator.Logs.OfOthers.Select(r => OverTheBound.Match(r.Text)).Where(m => m.Success)
            .Select(m => (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))).ToArray();

    private static byte[] Filled(int size, byte seed)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++)
            bytes[i] = (byte)(seed + i * 31);
        return bytes;
    }

    // ---- what a recipient that stops reading makes the mediator hold ---------------------------------

    // ---- the size-bounded send of the real sessions, hammered ---------------------------------------

    /// <summary>
    /// The HTTP session has one slot. Many senders at once — some within their bound, some over it —
    /// while the endpoint takes the response and closes at a moment of its own choosing. Whatever
    /// the interleaving: at most one send is <c>Sent</c>; the response is that message's envelope
    /// and no other's (never one that was reported <c>TooLarge</c> or <c>NotSent</c>, never two);
    /// a <c>Sent</c> message is never lost to the close; and the response is within its sender's bound.
    /// </summary>
    [Fact]
    public async Task Http_session_many_bounded_sends_racing_each_other_and_the_close_yield_at_most_one_Sent_and_the_response_is_exactly_that_message()
    {
        await using var rig = await SessionRig.CreateAsync();
        var smallSize = await rig.PackedSizeAsync(rig.ToAlice("probe", 0));
        var random = new Random(94);

        for (var round = 0; round < 150; round++)
        {
            var session = rig.NewHttpSession();
            var sends = new List<(Message Message, int Bound)>();
            for (var i = 0; i < 6; i++)
            {
                // Small ones fit a bound a little over their size; padded ones never do; some have no bound.
                var padded = random.Next(3) == 0;
                var bound = random.Next(4) == 0 ? int.MaxValue : smallSize + 64;
                sends.Add((rig.ToAlice($"r{round}-m{i}", padded ? 2048 : 0), bound));
            }

            using var start = new ManualResetEventSlim();
            var results = sends.Select(s => Task.Run(async () =>
            {
                start.Wait();
                return await session.TrySendAsync(s.Message, s.Bound, CancellationToken.None);
            })).ToArray();
            // From "before any pack began" to "after the first pack ended" (a pack is a fraction of a millisecond).
            var closeAfter = SpinFor(random);
            var closer = Task.Run(() =>
            {
                start.Wait();
                Thread.SpinWait(closeAfter);
                return session.TakeResponseAndClose();
            });
            start.Set();
            var answers = await Task.WhenAll(results);
            var body = await closer;

            var sent = Enumerable.Range(0, sends.Count).Where(i => answers[i] == ReturnRouteSendResult.Sent).ToArray();
            sent.Length.Should().BeLessThanOrEqualTo(1, $"round {round}: one slot, one message");
            for (var i = 0; i < sends.Count; i++)
            {
                if (answers[i] == ReturnRouteSendResult.TooLarge)
                    sends[i].Bound.Should().BeLessThan(int.MaxValue, $"round {round}: only a message with a bound can be too large");
            }
            if (sent.Length == 1)
            {
                body.Should().NotBeNull($"round {round}: a message reported Sent is the response; the close cannot lose it");
                body!.Length.Should().BeLessThanOrEqualTo(sends[sent[0]].Bound, $"round {round}");
                (await rig.Alice.Client.UnpackAsync(Encoding.UTF8.GetString(body))).Message.Id.Should().Be(sends[sent[0]].Message.Id, $"round {round}: the response is the message that was reported Sent");
            }
            else
            {
                body.Should().BeNull($"round {round}: nothing was reported Sent, so nothing is the response");
            }

            // Closed for good: nothing is sent, nothing is "too large" without a pack, and the response was taken once.
            (await session.TrySendAsync(rig.ToAlice("late", 2048), 16, CancellationToken.None)).Should().Be(ReturnRouteSendResult.NotSent);
            session.TakeResponseAndClose().Should().BeNull();
        }
    }

    /// <summary>
    /// The WebSocket session has <c>MaxPendingPushes</c> slots. Many bounded sends at once — within
    /// and over their bound — while the peer downgrades and resumes the session, or it closes.
    /// Whatever the interleaving: only messages reported <c>Sent</c> are ever written, each at most
    /// once and within its bound; nothing is written after the close; and when everything has
    /// settled the session has exactly its slots back — none leaked by a refused or oversized send,
    /// none given back twice.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebSocket_session_many_bounded_sends_racing_downgrades_or_the_close_write_only_what_was_Sent_and_end_with_every_push_slot_back(bool closes)
    {
        const int Slots = 4;
        await using var rig = await SessionRig.CreateAsync();
        var smallSize = await rig.PackedSizeAsync(rig.ToAlice("probe", 0));
        var random = new Random(closes ? 95 : 96);

        for (var round = 0; round < 60; round++)
        {
            var socket = new RecordingSocket();
            using var connection = new CancellationTokenSource();
            var session = rig.NewWebSocketSession(socket, connection, Slots);
            session.Start();

            var sends = new List<(Message Message, int Bound)>();
            for (var i = 0; i < 16; i++)
                sends.Add((rig.ToAlice($"r{round}-m{i}", random.Next(3) == 0 ? 2048 : 0), random.Next(4) == 0 ? int.MaxValue : smallSize + 64));

            using var start = new ManualResetEventSlim();
            var results = sends.Select(s => Task.Run(async () =>
            {
                start.Wait();
                return await session.TrySendAsync(s.Message, s.Bound, CancellationToken.None);
            })).ToArray();
            var spin = SpinFor(random);
            var disturbance = Task.Run(() =>
            {
                start.Wait();
                Thread.SpinWait(spin);
                if (closes)
                {
                    session.RequestClose(SessionCloseReason.Unregistered);
                    return;
                }
                for (var i = 0; i < 3; i++)
                {
                    session.ApplyHeader(ReturnRouteHeader.None);
                    Thread.SpinWait(spin / 4);
                    session.ApplyHeader(new ReturnRouteHeader(ReturnRouteMode.All, null));
                }
            });
            start.Set();
            var answers = await Task.WhenAll(results);
            await disturbance;

            // Settle: the writer takes every queued frame off the queue (sending or discarding it).
            await rig.UntilAsync(() => FreePushSlots(session) == Slots, $"round {round}: every push slot is given back (free: {FreePushSlots(session)} of {Slots})");
            if (closes)
                (await session.StopAsync()).Should().BeTrue();
            FreePushSlots(session).Should().Be(Slots, $"round {round}: no slot leaked, none given back twice");

            var sentIds = Enumerable.Range(0, sends.Count).Where(i => answers[i] == ReturnRouteSendResult.Sent).Select(i => sends[i].Message.Id).ToHashSet();
            var written = new List<string>();
            foreach (var frame in socket.Frames)
            {
                var id = (await rig.Alice.Client.UnpackAsync(Encoding.UTF8.GetString(frame))).Message.Id;
                written.Add(id);
                frame.Length.Should().BeLessThanOrEqualTo(sends.Single(s => s.Message.Id == id).Bound, $"round {round}: a frame is within its sender's bound");
            }
            written.Should().OnlyHaveUniqueItems($"round {round}");
            written.Should().BeSubsetOf(sentIds, $"round {round}: only a message reported Sent is ever written");
            socket.MaxConcurrentSends.Should().BeLessThanOrEqualTo(1, $"round {round}: one writer");

            if (closes)
            {
                var framesAtClose = socket.Frames.Count;
                (await session.TrySendAsync(rig.ToAlice("late", 0), int.MaxValue, CancellationToken.None)).Should().Be(ReturnRouteSendResult.NotSent);
                (await session.TrySendAsync(rig.ToAlice("late-large", 2048), 16, CancellationToken.None)).Should().Be(ReturnRouteSendResult.NotSent, "a closed session costs no pack, so it cannot say too large");
                socket.Frames.Count.Should().Be(framesAtClose, $"round {round}: nothing is written after the close");
                FreePushSlots(session).Should().Be(Slots);
            }
            else
            {
                // Still open and resumed: all the slots are usable again.
                (await session.TrySendAsync(rig.ToAlice("after", 0), int.MaxValue, CancellationToken.None)).Should().Be(ReturnRouteSendResult.Sent);
                await session.StopAsync();
            }
        }
    }

    private static int SpinFor(Random random) => random.Next(5) switch
    {
        0 => 0,
        1 => random.Next(1, 2_000),
        2 => random.Next(2_000, 20_000),
        3 => random.Next(20_000, 200_000),
        _ => random.Next(200_000, 600_000),
    };

    private static int FreePushSlots(WebSocketReturnRouteSession session)
    {
        var field = typeof(WebSocketReturnRouteSession).GetField("_freePushSlots", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WebSocketReturnRouteSession no longer has a field '_freePushSlots': update this test with the session.");
        return (int)field.GetValue(session)!;
    }

    /// <summary>The server side of a session without an endpoint: real identities, the real packing client, key resolution that yields the thread (so a close can land inside a pack).</summary>
    private sealed class SessionRig : IAsyncDisposable
    {
        private readonly TestPeer _carol;
        private readonly TestPeer _mallory;
        private readonly DidCommClient _client;
        private readonly string _serverDid;

        private SessionRig(string serverDid, TestPeer alice, TestPeer carol, TestPeer mallory, DidCommClient client)
        {
            _serverDid = serverDid;
            Alice = alice;
            _carol = carol;
            _mallory = mallory;
            _client = client;
        }

        public TestPeer Alice { get; }

        public static async Task<SessionRig> CreateAsync()
        {
            ThreadPool.GetMinThreads(out var worker, out var io);
            if (worker < 48)
                ThreadPool.SetMinThreads(48, io);
            var secrets = new InMemorySecretsResolver();
            var (server, alice, carol, mallory) = await ReturnRouteTestHost.CreateIdentitiesAsync(secrets);
            var keys = new YieldingKeyService(alice.Services.GetRequiredService<IDidKeyService>());
            return new SessionRig(server.Did, alice, carol, mallory, new DidCommClient(secrets, keys, new DidCommOptions()));
        }

        public HttpReturnRouteSession NewHttpSession() => new(_client, Alice.Did, _serverDid, null);

        public WebSocketReturnRouteSession NewWebSocketSession(WebSocket socket, CancellationTokenSource connection, int slots)
            => new(socket, _client, Alice.Did, _serverDid, slots, connection, null, TimeSpan.FromSeconds(5));

        /// <summary>A message from the server to Alice whose packed size depends only on <paramref name="padding"/> (ids have one length).</summary>
        public Message ToAlice(string name, int padding)
            => new MessageBuilder()
                .WithId(Guid.NewGuid().ToString("N"))
                .WithType("https://didcomm.org/test-adv2/1.0/" + name.Split('-')[0])
                .WithFrom(_serverDid)
                .WithTo(Alice.Did)
                .WithBody(new JsonObject { ["pad"] = new string('p', padding) })
                .Build();

        public async Task<int> PackedSizeAsync(Message message)
        {
            var session = NewHttpSession();
            (await session.TrySendAsync(message, int.MaxValue, CancellationToken.None)).Should().Be(ReturnRouteSendResult.Sent);
            return session.TakeResponseAndClose()!.Length;
        }

        public async Task UntilAsync(Func<bool> condition, string because)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(15))
                    throw new TimeoutException("Not within the deadline: " + because);
                await Task.Delay(1);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Alice.DisposeAsync();
            await _carol.DisposeAsync();
            await _mallory.DisposeAsync();
        }
    }

    /// <summary>Key resolution that gives the thread away now and then, so that other senders, a downgrade or the close run while a pack is under way.</summary>
    private sealed class YieldingKeyService(IDidKeyService inner) : IDidKeyService
    {
        public async Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
        {
            switch (Random.Shared.Next(4))
            {
                case 0:
                    await Task.Yield();
                    break;
                case 1:
                    Thread.SpinWait(Random.Shared.Next(2000));
                    break;
            }
            return await inner.GetVerificationMethodsAsync(did, relationship, ct);
        }

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => inner.IsKeyAuthorizedAsync(did, kid, relationship, ct);

        public void RejectUnsupportedMethod(string did) => inner.RejectUnsupportedMethod(did);
    }

    /// <summary>A socket whose peer has stopped reading: a send never completes until the connection is cancelled.</summary>
    private sealed class StalledSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        private int _entered;

        public bool Entered => Volatile.Read(ref _entered) != 0;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException("the session never receives");

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Volatile.Write(ref _entered, 1);
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    /// <summary>A socket that records what the session's writer sends.</summary>
    private sealed class RecordingSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        private int _concurrentSends;
        private int _maxConcurrentSends;

        public ConcurrentQueue<byte[]> Frames { get; } = new();
        public int MaxConcurrentSends => Volatile.Read(ref _maxConcurrentSends);

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException("the session never receives");

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            var concurrent = Interlocked.Increment(ref _concurrentSends);
            int seen;
            while ((seen = Volatile.Read(ref _maxConcurrentSends)) < concurrent
                   && Interlocked.CompareExchange(ref _maxConcurrentSends, concurrent, seen) != seen)
            {
            }
            try
            {
                // Give the thread away mid-send: a downgrade or the close can land while a frame is being written.
                await Task.Yield();
                Frames.Enqueue(buffer.ToArray());
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentSends);
            }
        }
    }
}
