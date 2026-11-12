using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCrypto;
using NetDid.Core;
using NetDid.Method.Peer;
using JwkConversion = DataProofsDotnet.Jose.JwkConversion;
using ProblemReports = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// The harness of the #94 end-to-end pickup tests: a TestServer mediator (the server agent of
/// <see cref="ReturnRouteTestHost"/>) with <c>AddCoordinateMediation()</c> and
/// <c>AddMessagePickup()</c> behind the registry-aware <c>MapDidCommEndpoint("/didcomm")</c> and
/// <c>MapDidCommWebSocket("/ws")</c> (same-socket replies on), an injected clock, and a logger that
/// keeps the category of every line.
/// </summary>
/// <remarks>
/// The cast: <see cref="Recipient"/> is the enrolled peer that picks messages up; <see cref="Alice"/>
/// is a third identity whose only part is to authcrypt the messages the queue relays, so every
/// payload is a real encrypted DIDComm message the recipient can unpack; <see cref="Mallory"/> is
/// another peer. Messages enter the queue through <see cref="MessagePickupQueue.DeliverAsync"/>
/// resolved from the server's services (the forward → queue wiring is #95).
/// </remarks>
internal sealed class PickupEndpointHost : IAsyncDisposable
{
    public const int Kibibyte = 1024;
    public const int Mebibyte = 1024 * 1024;
    public const string EncryptedMediaType = "application/didcomm-encrypted+json";

    /// <summary>How far below the requested size a sized payload may be.</summary>
    public const int SizeSlack = 64;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly List<TestPeer> _ownPeers = new();
    private int _sequence;

    private PickupEndpointHost(ReturnRouteTestHost host, PickupLogCapture logs)
    {
        Host = host;
        Logs = logs;
    }

    public ReturnRouteTestHost Host { get; }

    /// <summary>Every log line of the server host, with its category, level and exception.</summary>
    public PickupLogCapture Logs { get; }

    /// <summary>The mediator's DID (the server agent).</summary>
    public string Mediator => Host.ServerDid;

    /// <summary>The enrolled peer that picks its messages up.</summary>
    public TestPeer Recipient => Host.Carol;

    /// <summary>The third identity that authcrypts the relayed messages.</summary>
    public TestPeer Alice => Host.Alice;

    /// <summary>Another peer; enrolled by the tests that need it.</summary>
    public TestPeer Mallory => Host.Mallory;

    public TestClock Clock => Host.Clock;

    public MessagePickupQueue Queue => Host.App.Services.GetRequiredService<MessagePickupQueue>();

    public InMemoryMessageStore Store => (InMemoryMessageStore)Host.App.Services.GetRequiredService<IMessageStore>();

    public IMediationStore Mediation => Host.App.Services.GetRequiredService<IMediationStore>();

    /// <summary>
    /// Start the mediator. The server's <c>MaxReceiveBytes</c> is the product default (1 MiB),
    /// which is also what every test peer's own client accepts: nothing is raised.
    /// </summary>
    /// <param name="pickup">Settings of the pickup role (<c>AddMessagePickup(configure)</c>).</param>
    /// <param name="sessions">Settings of the return-route session registry (bounds per peer, pending pushes).</param>
    public static async Task<PickupEndpointHost> StartAsync(Action<MessagePickupOptions>? pickup = null, Action<ReturnRouteSessionOptions>? sessions = null)
    {
        var logs = new PickupLogCapture();
        var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.MaxReceiveBytes = Mebibyte;
            o.ConfigureSessions = sessions;
            o.ConfigureServices = services => services.AddSingleton<ILoggerProvider>(logs);
            o.ConfigureDidComm = b =>
            {
                b.AddCoordinateMediation();
                b.AddMessagePickup(pickup);
            };
        });
        return new PickupEndpointHost(host, logs);
    }

    // ---- requests -------------------------------------------------------------------------

    /// <summary>What <c>DidCommClient.SendAsync</c> does for a mediator request (FR-RR-09): a <c>created_time</c> from the clock the server's admission reads, and <c>return_route: all</c> when asked.</summary>
    public Message Prepare(Message message, bool returnRoute = true)
    {
        message.CreatedTime = Clock.Now.ToUnixTimeSeconds();
        if (returnRoute)
            ReturnRouteHeaderWriter.Write(message, ReturnRouteMode.All, thread: null);
        return message;
    }

    /// <summary>A pickup request with a body the builders refuse to write (malformed on purpose), or with a thread of its own.</summary>
    public Message Raw(TestPeer from, string type, JsonObject body, string? thid = null)
    {
        var builder = new MessageBuilder().WithType(type).WithFrom(from.Did).WithTo(Mediator).WithBody(body);
        if (thid is not null)
            builder.WithThid(thid);
        return builder.Build();
    }

    /// <summary>Authcrypt <paramref name="request"/> (prepared here) from <paramref name="from"/> and POST it to the registry-aware endpoint.</summary>
    public async Task<HttpPostResult> PostAsync(TestPeer from, Message request, bool returnRoute = true)
        => await Host.PostAsync(await Host.AuthcryptAsync(from, Prepare(request, returnRoute)));

    /// <summary>POST <paramref name="request"/> and return the message in the <c>200</c> body, checked as the mediator's authcrypted answer to <paramref name="from"/>.</summary>
    public async Task<PickupAnswer> AskAsync(TestPeer from, Message request)
        => await AnswerAsync(from, await PostAsync(from, request));

    /// <summary>
    /// The message a <c>200</c> carries: content type <c>application/didcomm-encrypted+json</c>,
    /// authcrypted by the mediator DID to exactly <paramref name="peer"/>, unpacked with the peer's
    /// own client (whose <c>MaxReceiveBytes</c> is the product default).
    /// </summary>
    public async Task<PickupAnswer> AnswerAsync(TestPeer peer, HttpPostResult response)
    {
        response.Status.Should().Be(HttpStatusCode.OK);
        response.ContentType.Should().Be(EncryptedMediaType);
        return await UnpackAnswerAsync(peer, response.Body);
    }

    /// <summary>Unpack one packed message of the mediator as <paramref name="peer"/> and check who sent it and to whom.</summary>
    public async Task<PickupAnswer> UnpackAnswerAsync(TestPeer peer, byte[] packed)
    {
        var unpacked = await peer.Client.UnpackAsync(Encoding.UTF8.GetString(packed));
        unpacked.Encrypted.Should().BeTrue();
        unpacked.Authenticated.Should().BeTrue();
        unpacked.SenderKid.Should().StartWith(Mediator + "#", "the answer is authcrypted by the mediator DID the request was addressed to");
        unpacked.Message.From.Should().Be(Mediator);
        unpacked.Message.To.Should().Equal(peer.Did);
        return new PickupAnswer(unpacked.Message, packed);
    }

    // ---- enrolment ------------------------------------------------------------------------

    /// <summary>
    /// Enrol <paramref name="peer"/> with real Coordinate Mediation messages over the HTTP endpoint:
    /// <c>mediate-request</c> → <c>mediate-grant</c>, then <c>recipient-update</c> adding
    /// <paramref name="recipientDids"/> (the peer's own DID when none is given).
    /// </summary>
    public async Task EnrolAsync(TestPeer peer, params string[] recipientDids)
    {
        var granted = await AskAsync(peer, CoordinateMediation.CreateMediateRequest(peer.Did, Mediator));
        CoordinateMediation.TryParseGrant(granted.Message, out _).Should().BeTrue();

        var dids = recipientDids.Length == 0 ? new[] { peer.Did } : recipientDids;
        var updated = await AskAsync(peer, CoordinateMediation.CreateRecipientUpdate(peer.Did, Mediator,
            dids.Select(d => new RecipientUpdate(d, RecipientUpdateAction.Add)).ToArray()));
        CoordinateMediation.TryParseRecipientUpdateResponse(updated.Message, out var results).Should().BeTrue();
        results!.Should().OnlyContain(r => r.Result == RecipientUpdateOutcome.Success);
    }

    // ---- the queue ------------------------------------------------------------------------

    /// <summary>
    /// A real encrypted DIDComm message for <paramref name="to"/>: a small plaintext authcrypted by
    /// Alice. With <paramref name="packedBytes"/> the inner body is padded so that the packed
    /// envelope is at most that many bytes and within <see cref="SizeSlack"/> of it.
    /// </summary>
    public async Task<RelayedMessage> PackFromAliceAsync(TestPeer to, int? packedBytes = null)
    {
        var marker = "relayed-" + Interlocked.Increment(ref _sequence);

        async Task<(byte[] Packed, Message Inner)> PackAsync(int pad)
        {
            var inner = new MessageBuilder()
                .WithType("https://didcomm.org/basicmessage/2.0/message")
                .WithFrom(Alice.Did)
                .WithTo(to.Did)
                .WithBody(new JsonObject { ["content"] = marker, ["pad"] = new string('p', pad) })
                .Build();
            var packed = await Alice.Client.PackEncryptedAsync(inner, new PackEncryptedOptions(
                Recipients: new[] { to.Did },
                From: Alice.Did,
                Forward: false));
            return (Encoding.UTF8.GetBytes(packed.Message), inner);
        }

        if (packedBytes is not { } target)
        {
            var (small, smallInner) = await PackAsync(0);
            return new RelayedMessage(small, smallInner.Id, marker);
        }

        // The envelope grows by 4 characters per 3 plaintext bytes, in whole cipher blocks: walk the
        // padding to the largest one whose envelope still fits the target.
        var padding = Math.Max(0, (target - 4 * Kibibyte) / 4 * 3);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var (packed, inner) = await PackAsync(padding);
            var room = target - packed.Length;
            if (room >= 0 && room < SizeSlack)
                return new RelayedMessage(packed, inner.Id, marker);
            padding += room >= 0
                ? Math.Max(16, (room - SizeSlack / 2) / 4 * 3 / 16 * 16)
                : -Math.Max(16, (-room) / 4 * 3 / 16 * 16 + 16);
            padding.Should().BeGreaterThanOrEqualTo(0, "the target must leave room for the envelope itself");
        }
        throw new InvalidOperationException($"Could not pack a message of about {target} bytes.");
    }

    /// <summary>
    /// Pack a message from Alice and hand it to the mediator's queue for <paramref name="to"/>'s DID,
    /// expecting <paramref name="expected"/>; returns the payload with the id the queue gave it.
    /// </summary>
    public async Task<RelayedMessage> QueueAsync(TestPeer to, int? packedBytes = null, PickupDeliveryOutcome expected = PickupDeliveryOutcome.Queued)
    {
        var relayed = await PackFromAliceAsync(to, packedBytes);
        var result = await Queue.DeliverAsync(to.Did, relayed.Payload);
        result.Outcome.Should().Be(expected);
        return relayed with { Id = result.MessageId };
    }

    /// <summary>
    /// The relay is byte-exact: the attachment's decoded bytes are the queued bytes, and they unpack
    /// (as <paramref name="recipient"/>) to the message Alice authcrypted.
    /// </summary>
    public async Task ShouldBeRelayedAsync(TestPeer recipient, DeliveredMessage delivered, RelayedMessage expected)
    {
        delivered.Id.Should().Be(expected.Id);
        delivered.Payload.Span.SequenceEqual(expected.Payload).Should().BeTrue("the payload is relayed byte for byte");

        var inner = await recipient.Client.UnpackAsync(Encoding.UTF8.GetString(delivered.Payload.Span));
        inner.Encrypted.Should().BeTrue();
        inner.Authenticated.Should().BeTrue();
        inner.SenderKid.Should().StartWith(Alice.Did + "#");
        inner.Message.Id.Should().Be(expected.InnerId);
        inner.Message.Body!["content"]!.GetValue<string>().Should().Be(expected.Marker);
    }

    // ---- WebSocket ------------------------------------------------------------------------

    public Task<WebSocket> ConnectAsync() => Host.ConnectAsync();

    /// <summary>Authcrypt <paramref name="request"/> (prepared here, <c>return_route: all</c> unless told otherwise) and send it as one binary WebSocket message.</summary>
    public async Task SendAsync(WebSocket socket, TestPeer from, Message request, bool returnRoute = true)
        => await ReturnRouteTestHost.SendAsync(socket, await Host.AuthcryptAsync(from, Prepare(request, returnRoute)));

    /// <summary>Receive the next whole frame of <paramref name="socket"/> and unpack it as the mediator's message to <paramref name="peer"/>.</summary>
    public async Task<PickupAnswer> ReceiveAsync(WebSocket socket, TestPeer peer)
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var ms = new MemoryStream();
        var buffer = new byte[64 * Kibibyte];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cts.Token);
            result.MessageType.Should().Be(WebSocketMessageType.Binary, "each message of the mediator is one binary WebSocket message");
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return await UnpackAnswerAsync(peer, ms.ToArray());
    }

    /// <summary>
    /// Send <paramref name="request"/> on <paramref name="socket"/> and return the next frame. It is
    /// the mediator's answer unless something was pushed first: the receive loop handles messages in
    /// order and the session's writer is first-in first-out, which is also how these tests prove
    /// that nothing else was written.
    /// </summary>
    public async Task<PickupAnswer> AskAsync(WebSocket socket, TestPeer from, Message request)
    {
        await SendAsync(socket, from, request);
        return await ReceiveAsync(socket, from);
    }

    /// <summary>Open a socket for <paramref name="peer"/>: its first message is a <c>status-request</c> with <c>return_route: all</c>, answered on the socket.</summary>
    public async Task<(WebSocket Socket, PickupStatus Status)> OpenAsync(TestPeer peer)
    {
        var socket = await ConnectAsync();
        return (socket, await StatusAsync(socket, peer));
    }

    /// <summary>Ask for the status on <paramref name="socket"/>; the next frame must be its answer.</summary>
    public async Task<PickupStatus> StatusAsync(WebSocket socket, TestPeer peer)
    {
        var request = MessagePickup.CreateStatusRequest(peer.Did, Mediator);
        return (await AskAsync(socket, peer, request)).Status(request.Id);
    }

    /// <summary>Turn live delivery on (or off) for <paramref name="socket"/> and return the <c>status</c> that answers it.</summary>
    public async Task<PickupStatus> GoLiveAsync(WebSocket socket, TestPeer peer, bool live = true)
    {
        var request = MessagePickup.CreateLiveDeliveryChange(peer.Did, Mediator, live);
        return (await AskAsync(socket, peer, request)).Status(request.Id);
    }

    /// <summary>Acknowledge <paramref name="ids"/> on <paramref name="socket"/> and return the <c>status</c> that answers it.</summary>
    public async Task<PickupStatus> AcknowledgeAsync(WebSocket socket, TestPeer peer, params string[] ids)
    {
        var request = MessagePickup.CreateMessagesReceived(peer.Did, Mediator, ids);
        return (await AskAsync(socket, peer, request)).Status(request.Id);
    }

    /// <summary>Close <paramref name="socket"/> from the client and wait until the server's endpoint has finished with the connection.</summary>
    public async Task CloseAsync(WebSocket socket)
    {
        var ended = Host.EndedWebSocketConnections;
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", default);
        (await ReturnRouteTestHost.WaitUntilAsync(() => Host.EndedWebSocketConnections > ended, Timeout))
            .Should().BeTrue("the server observes the close");
    }

    /// <summary>
    /// Ask for a delivery on <paramref name="socket"/> until one arrives (bounded): what a recipient
    /// does after reconnecting, while the server is still releasing the leases of the socket that
    /// closed. Every attempt is a new request; the answers before the delivery are <c>status</c>.
    /// The clock is never moved, so a delivery here was not freed by the lease timeout.
    /// </summary>
    public async Task<(Message Request, PickupAnswer Delivery)> FetchUntilDeliveredAsync(WebSocket socket, TestPeer peer, int limit = 10)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var request = MessagePickup.CreateDeliveryRequest(peer.Did, Mediator, limit);
            var answer = await AskAsync(socket, peer, request);
            if (answer.Message.Type == MessagePickup.DeliveryType)
                return (request, answer);
            answer.Message.Type.Should().Be(MessagePickup.StatusType);
            (DateTime.UtcNow < deadline).Should().BeTrue("the leases of a closed socket are released without waiting for the lease timeout (FR-PICK-04)");
            await Task.Delay(10);
        }
    }

    // ---- identities -----------------------------------------------------------------------

    /// <summary>
    /// A did:peer:2 peer that publishes <paramref name="keyAgreementKeys"/> X25519 keys. An envelope
    /// for it carries one <c>recipients[]</c> entry per key, each with the full key id, so the
    /// envelope is far larger than for a one-key peer: the case in which the mediator's size
    /// estimate is short and only the session's check of the packed bytes keeps the budget.
    /// </summary>
    public async Task<TestPeer> CreateManyKeyPeerAsync(int keyAgreementKeys)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
        });
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IDidManager>();
        var keyGenerator = provider.GetRequiredService<IKeyGenerator>();
        var crypto = provider.GetRequiredService<ICryptoProvider>();

        var pairs = new List<KeyPair>();
        var purposes = new List<PeerKeyPurpose>();
        for (var i = 0; i < keyAgreementKeys; i++)
        {
            var pair = keyGenerator.Generate(KeyType.X25519);
            pairs.Add(pair);
            purposes.Add(new PeerKeyPurpose(new KeyPairSigner(pair, crypto), PeerPurpose.KeyAgreement));
        }
        var authentication = keyGenerator.Generate(KeyType.Ed25519);
        pairs.Add(authentication);
        purposes.Add(new PeerKeyPurpose(new KeyPairSigner(authentication, crypto), PeerPurpose.Authentication));

        var created = await manager.CreateAsync(new DidPeerCreateOptions { Numalgo = PeerNumalgo.Two, Keys = purposes });
        var did = created.Did.Value ?? throw new InvalidOperationException("The DID manager returned a DID with no value.");

        var privates = new List<Jwk>();
        foreach (var pair in pairs)
        {
            var method = created.DidDocument.VerificationMethod!.First(vm => string.Equals(vm.PublicKeyMultibase, pair.MultibasePublicKey, StringComparison.Ordinal));
            var kid = method.Id.StartsWith('#') ? did + method.Id : method.Id;
            privates.Add(JwkConversion.ToPrivateJwk(pair, kid));
        }

        var peer = TestPeer.Create(new PeerIdentity(did, privates));
        _ownPeers.Add(peer);
        return peer;
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        foreach (var peer in _ownPeers)
            await peer.DisposeAsync();
    }
}

/// <summary>A payload handed to the queue: the packed bytes, the id the queue gave it, and what the recipient finds inside.</summary>
/// <param name="Payload">The encrypted message exactly as queued.</param>
/// <param name="InnerId">The <c>id</c> of the plaintext Alice packed (the mediator cannot read it).</param>
/// <param name="Marker">The inner body's <c>content</c>.</param>
internal sealed record RelayedMessage(byte[] Payload, string InnerId, string Marker)
{
    /// <summary>The queue's id for the message (the attachment id); <c>null</c> when it was not retained.</summary>
    public string? Id { get; init; }
}

/// <summary>One message of the mediator as the peer received it: unpacked, with the packed bytes it travelled as.</summary>
internal sealed record PickupAnswer(Message Message, byte[] Packed)
{
    /// <summary>
    /// Read the answer as a <c>status</c> on <paramref name="thid"/> (the request's thread): the
    /// exact type, the thread, and a <c>created_time</c> — the mediator stamps what it sends back
    /// under a mediator-service protocol (FR-RR-09).
    /// </summary>
    public PickupStatus Status(string thid)
    {
        Message.Type.Should().Be(MessagePickup.StatusType);
        Message.Thid.Should().Be(thid);
        Message.CreatedTime.Should().NotBeNull();
        Message.Attachments.Should().BeNullOrEmpty();
        MessagePickup.TryParseStatus(Message, out var status).Should().BeTrue();
        return status!;
    }

    /// <summary>
    /// Read the answer as a <c>delivery</c>: the exact type, <paramref name="thid"/> (the request's
    /// thread, or <c>null</c> for a live delivery, which answers no request), a
    /// <c>created_time</c>, the repeated <paramref name="recipientDid"/> filter (or none), and
    /// attachments whose data is unpadded base64url (DIDComm v2.1 attachments).
    /// </summary>
    public IReadOnlyList<DeliveredMessage> Delivery(string? thid, string? recipientDid = null)
    {
        Message.Type.Should().Be(MessagePickup.DeliveryType);
        Message.Thid.Should().Be(thid);
        Message.CreatedTime.Should().NotBeNull();
        foreach (var attachment in Message.Attachments ?? Array.Empty<Attachment>())
        {
            attachment.Data.Base64.Should().NotBeNullOrEmpty();
            (attachment.Data.Base64!.AsSpan().IndexOfAny("=+/") < 0).Should().BeTrue("attachment data is base64url without padding");
            attachment.Data.Json.Should().BeNull();
        }
        MessagePickup.TryParseDelivery(Message, out var messages, out var echoed).Should().BeTrue();
        echoed.Should().Be(recipientDid);
        return messages!;
    }

    /// <summary>Read the answer as the Report Problem 2.0 message of FR-PICK-07 on <paramref name="pthid"/>.</summary>
    public (string? Code, string? Comment) Problem(string pthid)
    {
        Message.Type.Should().Be("https://didcomm.org/report-problem/2.0/problem-report");
        Message.Pthid.Should().Be(pthid);
        return (ProblemReports.ReadCode(Message), ProblemReports.ReadComment(Message));
    }
}

/// <summary>One captured log record.</summary>
internal sealed record PickupLogRecord(string Category, LogLevel Level, string Text, Exception? Exception)
{
    /// <summary>The line as a text sink writes it: the formatted message, then the exception's <c>ToString()</c>.</summary>
    public string Rendered => Exception is null ? Text : Text + Environment.NewLine + Exception;
}

/// <summary>A logger provider that keeps the category of every record, so a scan can tell the pickup classes' own lines from the endpoint's.</summary>
internal sealed class PickupLogCapture : ILoggerProvider
{
    /// <summary>The namespace of the pickup classes: their loggers' categories start with it.</summary>
    public const string PickupNamespace = "DidComm.Mediator.Pickup";

    private readonly ConcurrentQueue<PickupLogRecord> _records = new();

    public IReadOnlyCollection<PickupLogRecord> Records => _records;

    /// <summary>The records written by the pickup classes themselves.</summary>
    public IEnumerable<PickupLogRecord> OfPickup => _records.Where(r => r.Category.StartsWith(PickupNamespace, StringComparison.Ordinal));

    /// <summary>The records written by anything else in the host (the endpoint, the dispatcher, the client).</summary>
    public IEnumerable<PickupLogRecord> OfOthers => _records.Where(r => !r.Category.StartsWith(PickupNamespace, StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _records);

    public void Dispose()
    {
    }

    private sealed class Logger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<PickupLogRecord> _records;

        public Logger(string category, ConcurrentQueue<PickupLogRecord> records)
        {
            _category = category;
            _records = records;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _records.Enqueue(new PickupLogRecord(_category, logLevel, formatter(state, exception), exception));
    }
}
