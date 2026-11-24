using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Transports;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using DidComm.Facade;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// Every inbound message a dispatcher handled, as a protocol observer with no filter sees it (#100
/// end-to-end tests): on the mediator, the client's requests of both Message Pickup versions and its
/// Discover Features query; on the client, what arrived on its WebSocket (answers and live pushes).
/// Asynchronous like every observer (the dispatcher hands a message to its observers' queue when its
/// dispatch ends): read the records once the dispatcher's observer queue is flushed, or wait for the
/// record the assertion reads (L-065).
/// </summary>
internal sealed class PickupWireObserver : IProtocolObserver
{
    private readonly ConcurrentQueue<WireRecord> _seen = new();

    public string? ProtocolUriFilter => null;

    public IReadOnlyList<WireRecord> Seen => _seen.ToArray();

    public Task OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct)
    {
        var message = observation.Message;
        _seen.Enqueue(new WireRecord(
            message.Type,
            message.Id,
            message.Thid,
            message.CreatedTime,
            message.From,
            ReturnRouteHeader.Read(message).Mode,
            message.Attachments?.Count,
            message.Body?.DeepClone() as JsonObject));
        return Task.CompletedTask;
    }

    /// <summary>The records from <paramref name="from"/>, in order.</summary>
    public IReadOnlyList<WireRecord> From(string from) => Seen.Where(s => s.From == from).ToArray();

    /// <summary>The kinds (<see cref="WireRecord.Kind"/>) of the records from <paramref name="from"/>, in order.</summary>
    public IReadOnlyList<string> KindsFrom(string from) => From(from).Select(s => s.Kind).ToArray();

    /// <summary>The Message Pickup and Discover Features kinds from <paramref name="from"/>, in order (enrolment and forwards left out).</summary>
    public IReadOnlyList<string> PickupKindsFrom(string from)
        => KindsFrom(from).Where(k => k.StartsWith("3.0/", StringComparison.Ordinal) || k.StartsWith("4.0/", StringComparison.Ordinal) || k.StartsWith("discover-features/", StringComparison.Ordinal)).ToArray();

    /// <summary>Wait until a record from <paramref name="from"/> matches <paramref name="match"/>.</summary>
    public Task WaitForAsync(string from, Func<WireRecord, bool> match, string because)
        => MediatorTestServer.WaitUntilAsync(() => Seen.Any(s => s.From == from && match(s)), because);
}

/// <summary>One inbound message as <see cref="PickupWireObserver"/> recorded it.</summary>
internal sealed record WireRecord(string Type, string Id, string? Thid, long? CreatedTime, string? From, ReturnRouteMode ReturnRoute, int? Attachments, JsonObject? Body)
{
    /// <summary>
    /// <c>3.0/&lt;name&gt;</c> or <c>4.0/&lt;name&gt;</c> for Message Pickup (a
    /// <c>live-delivery-change</c> as <c>live-on</c> / <c>live-off</c>), <c>discover-features/&lt;name&gt;</c>,
    /// the full type otherwise.
    /// </summary>
    public string Kind
    {
        get
        {
            var name = Type[(Type.LastIndexOf('/') + 1)..];
            if (name == "live-delivery-change")
                name = Body?["live_delivery"]?.GetValue<bool>() == true ? "live-on" : "live-off";
            if (Type.StartsWith(MessagePickup40.ProtocolUri + "/", StringComparison.Ordinal))
                return "4.0/" + name;
            if (Type.StartsWith(MessagePickup.ProtocolUri + "/", StringComparison.Ordinal))
                return "3.0/" + name;
            if (Type.StartsWith(DiscoverFeatures.ProtocolUri + "/", StringComparison.Ordinal))
                return "discover-features/" + name;
            return Type;
        }
    }
}

/// <summary>Reading what a 4.0 mediator answered (the 4.0 counterparts of <see cref="PickupAnswer"/>'s readers).</summary>
internal static class Pickup40Answers
{
    /// <summary>
    /// The answer as a 4.0 <c>status</c> on <paramref name="thid"/>: the exact type, the thread, a
    /// <c>created_time</c>, no attachments and no <c>longest_waited_seconds</c> (4.0 L37).
    /// </summary>
    public static PickupStatus40 Status40(this PickupAnswer answer, string thid)
    {
        answer.Message.Type.Should().Be(MessagePickup40.StatusType);
        answer.Message.Thid.Should().Be(thid);
        answer.Message.CreatedTime.Should().NotBeNull();
        answer.Message.Attachments.Should().BeNullOrEmpty();
        answer.Message.Body!.ContainsKey("longest_waited_seconds").Should().BeFalse("4.0 removed it (L37)");
        MessagePickup40.TryParseStatus(answer.Message, out var status).Should().BeTrue();
        return status!;
    }

    /// <summary>
    /// The answer as a 4.0 <c>delivery</c>: the exact type, <paramref name="thid"/> (the request's
    /// thread, or <c>null</c> for a live push), a <c>created_time</c>, the repeated
    /// <paramref name="recipientDid"/> filter (or none), and an <c>attachments</c> array, which is
    /// written even when empty (L259).
    /// </summary>
    public static IReadOnlyList<DeliveredMessage> Delivery40(this PickupAnswer answer, string? thid, string? recipientDid = null)
    {
        answer.Message.Type.Should().Be(MessagePickup40.DeliveryType);
        answer.Message.Thid.Should().Be(thid);
        answer.Message.CreatedTime.Should().NotBeNull();
        answer.Message.Attachments.Should().NotBeNull("a 4.0 delivery always carries the attachments array (L259)");
        MessagePickup40.TryParseDelivery(answer.Message, out var messages, out var echoed).Should().BeTrue();
        echoed.Should().Be(recipientDid);
        return messages!;
    }

    /// <summary>The answer as the 4.0 problem report (L359-L369) on <paramref name="pthid"/>.</summary>
    public static (string? Code, string? Comment) Problem40(this PickupAnswer answer, string pthid)
    {
        answer.Message.Type.Should().Be(MessagePickup40.ProblemReportType);
        answer.Message.Pthid.Should().Be(pthid);
        return (answer.Message.Body?["code"]?.GetValue<string>(), answer.Message.Body?["comment"]?.GetValue<string>());
    }
}

/// <summary>Shared steps of the #100 Message Pickup 4.0 end-to-end tests.</summary>
internal static class PickupV4E2E
{
    /// <summary>The template of the Debug line <see cref="PickupClient"/> writes after Discover Features chose a version.</summary>
    public const string NegotiatedTemplate = "Message Pickup version negotiated by query {RequestId}: {Version}.";

    /// <summary>A mediator whose dispatcher reports every inbound message to <paramref name="observer"/>.</summary>
    public static Task<MediatorTestServer> StartMediatorAsync(PickupWireObserver observer, Action<MediatorTestServerOptions>? configure = null)
        => MediatorTestServer.StartAsync(o =>
        {
            configure?.Invoke(o);
            var post = o.PostConfigureServices;
            o.PostConfigureServices = services =>
            {
                post?.Invoke(services);
                services.AddSingleton<IProtocolObserver>(observer);
            };
        });

    /// <summary>A recipient container speaking <paramref name="version"/> (or negotiating), over HTTP and optionally a WebSocket.</summary>
    public static Task<MediatorClientHarness> HarnessAsync(MediatorTestServer mediator, MessagePickupVersion version, bool webSocket = false, Action<MediatorClientHarnessOptions>? configure = null)
        => MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = webSocket;
            configure?.Invoke(o);
            var client = o.Client;
            o.Client = c =>
            {
                client?.Invoke(c);
                c.MessagePickupVersion = version;
            };
        });

    /// <summary>The <c>{Version}</c> values of the negotiation lines in <paramref name="logs"/> (formatted lines carry the value itself).</summary>
    public static IReadOnlyList<string> NegotiatedVersions(CapturingLoggerProvider logs)
        => logs.Entries
            .Where(line => line.Contains("Message Pickup version negotiated by query ", StringComparison.Ordinal))
            .Select(line => line[(line.LastIndexOf(": ", StringComparison.Ordinal) + 2)..].TrimEnd('.'))
            .ToArray();

    /// <summary>
    /// A request as <c>SendAsync</c> would stamp it (<c>created_time</c> from the mediator's clock)
    /// but WITHOUT <c>return_route</c>: a 4.0 <c>messages-received</c> (L35), or a request that tests
    /// how a header-less message is treated.
    /// </summary>
    public static Message Stamped(MediatorTestServer mediator, Message message)
    {
        message.CreatedTime = mediator.Clock.Now.ToUnixTimeSeconds();
        return message;
    }

    /// <summary>A request authcrypted from <paramref name="from"/> exactly as given: no stamp, no header added.</summary>
    public static Task<string> PackAsIsAsync(MediatorTestServer mediator, TestPeer from, Message message) => mediator.AuthcryptAsync(from, message);

    /// <summary>A real encrypted DIDComm message from <paramref name="alice"/> to <paramref name="recipient"/> with a padded body, packed to at most <paramref name="targetBytes"/> and within 64 bytes of it.</summary>
    public static async Task<(byte[] Packed, Message Note)> PackSizedAsync(MediatorSender alice, TestPeer recipient, int targetBytes, string content)
    {
        const int slack = 64;
        var padding = Math.Max(0, (targetBytes - 4 * 1024) / 4 * 3);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var note = new MessageBuilder()
                .WithType("https://didcomm.org/basicmessage/2.0/message")
                .WithFrom(alice.Did)
                .WithTo(recipient.Did)
                .WithBody(new JsonObject { ["content"] = content, ["pad"] = new string('p', padding) })
                .Build();
            var packed = Encoding.UTF8.GetBytes(await alice.AuthcryptAsync(note, recipient.Did));
            var room = targetBytes - packed.Length;
            if (room >= 0 && room < slack)
                return (packed, note);
            padding += room >= 0
                ? Math.Max(16, (room - slack / 2) / 4 * 3 / 16 * 16)
                : -Math.Max(16, (-room) / 4 * 3 / 16 * 16 + 16);
            padding.Should().BeGreaterThanOrEqualTo(0);
        }
        throw new InvalidOperationException($"Could not pack a message of about {targetBytes} bytes.");
    }

    /// <summary>Hand a real encrypted message straight to the mediator's queue for <paramref name="recipient"/>, as its forward sink does.</summary>
    public static async Task<string> QueueAsync(MediatorTestServer mediator, TestPeer recipient, byte[] packed)
    {
        var result = await mediator.Queue.DeliverAsync(recipient.Did, packed, new PickupDeliveryOptions(AllowLive: false));
        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        return result.MessageId!;
    }

    /// <summary>POST <paramref name="packed"/> and expect <c>202</c> with an empty body (nothing answered).</summary>
    public static async Task PostExpectingNoAnswerAsync(MediatorTestServer mediator, string packed)
    {
        var response = await mediator.PostAsync(packed);
        response.Status.Should().Be(HttpStatusCode.Accepted);
        response.Body.Should().BeEmpty();
    }

    /// <summary>Send <paramref name="packed"/> as one text message on <paramref name="socket"/>.</summary>
    public static Task SendAsync(WebSocket socket, string packed) => ReturnRouteTestHost.SendAsync(socket, packed);

    /// <summary>A 4.0 <c>status-request</c> over HTTP (stamped, <c>return_route: all</c>): the status on its thread.</summary>
    public static async Task<PickupStatus40> Status40Async(MediatorTestServer mediator, TestPeer peer, string? recipientDid = null)
    {
        var request = MessagePickup40.CreateStatusRequest(peer.Did, mediator.Did, recipientDid);
        return (await mediator.AskAsync(peer, request)).Status40(request.Id);
    }

    /// <summary>The items of a batch that opened, as their content strings, in order.</summary>
    public static IReadOnlyList<string> Contents(PickupBatch batch)
        => batch.Items.Select(i => i.Unpacked?.Message.Body?["content"]?.GetValue<string>() ?? "<failed>").ToArray();

    /// <summary>The <see cref="UnpackResult"/> of an item that must have opened.</summary>
    public static UnpackResult Opened(PickupItem item)
    {
        item.Error.Should().BeNull();
        return item.Unpacked!;
    }

    /// <summary>
    /// An id of exactly <paramref name="length"/> characters that starts with <paramref name="prefix"/>
    /// and mixes letter case (so an echo that re-cases or cuts it shows), for the 512-character cap on
    /// message and thread ids (FR-MSG-02, FR-MSG-11; DIDComm v2.1 message_structure.md).
    /// </summary>
    public static string IdOfLength(string prefix, int length)
    {
        var text = new StringBuilder(prefix, length);
        const string fill = "AbCdEfGh";
        while (text.Length < length)
            text.Append(fill[text.Length % fill.Length]);
        return text.ToString(0, length);
    }

    /// <summary>
    /// Authcrypt (ECDH-1PU+A256KW, A256CBC-HS512) plaintext JSON written by hand, from
    /// <paramref name="from"/> to <paramref name="mediator"/>, for a message the library's packers
    /// refuse (an id longer than 512 characters): what a peer that is not this library can send.
    /// The same construction as <c>ReturnRouteTestHost.RawAuthcryptAsync</c>, to the mediator's key.
    /// </summary>
    public static async Task<string> RawAuthcryptAsync(MediatorTestServer mediator, TestPeer from, string plaintextJson)
    {
        var crypto = new DataProofsDotnet.Jose.JoseCryptoProvider();
        var senderJwk = await KeyAgreementJwkAsync(from.Secrets, from.Kids);
        var senderKey = await new DidComm.Secrets.KeyOperationResolver(from.Secrets, null, crypto).ResolveKeyAgreementAsync(senderJwk.Kid!)
            ?? throw new InvalidOperationException("The peer holds no key-agreement key.");
        var mediatorJwk = await KeyAgreementJwkAsync(mediator.App.Services.GetRequiredService<DidComm.Secrets.ISecretsResolver>(), mediator.Kids);
        var recipient = new DataProofsDotnet.Jose.Jwk { Kty = mediatorJwk.Kty, Crv = mediatorJwk.Crv, X = mediatorJwk.X, Kid = mediatorJwk.Kid };
        return await DataProofsDotnet.Jose.Encryption.JweBuilder.BuildEcdh1PuA256KwAsync(
            Encoding.UTF8.GetBytes(plaintextJson),
            new[] { recipient },
            senderKey,
            senderJwk.Kid!,
            DidComm.Jose.JoseAlgorithms.A256CbcHs512,
            crypto,
            MediaTypes.Encrypted,
            CancellationToken.None);
    }

    private static async Task<DataProofsDotnet.Jose.Jwk> KeyAgreementJwkAsync(DidComm.Secrets.ISecretsResolver secrets, IEnumerable<string> kids)
    {
        foreach (var kid in kids)
        {
            if (await secrets.FindAsync(kid) is { Crv: "X25519" } jwk)
                return jwk;
        }
        throw new InvalidOperationException("No X25519 key-agreement key is held for these kids.");
    }
}

/// <summary>
/// A host <see cref="IMessageStore"/> around the product's in-memory store that records every lease
/// it handed out (what the mediator asked for and how many records it got) and every time a lease
/// owner's leases were given back (a session closed) with how many records went back to the queue
/// (#100 end-to-end tests): the byte budget bounds a lease before anything is packed (FR-PICK-03),
/// and a live message never acknowledged returns to the queue (4.0 L308). Each record is written
/// after the store call it describes returned, so a test that waits for a record knows the effect
/// happened (L-065). Register with <c>o.ConfigureServices = s =&gt; LeaseRecordingStore.Register(s, x =&gt; store = x)</c>.
/// </summary>
internal sealed class LeaseRecordingStore(InMemoryMessageStore inner) : IMessageStore, IQa2WrappingStore
{
    private readonly ConcurrentQueue<LeaseRecord> _leases = new();
    private readonly ConcurrentQueue<int> _ownerReleases = new();

    public InMemoryMessageStore Inner { get; } = inner;

    /// <summary>Every lease, in order.</summary>
    public IReadOnlyList<LeaseRecord> Leases => _leases.ToArray();

    /// <summary>How many records each <c>ReleaseLeasesAsync</c> (an owner's leases given back) returned to the queue, in order.</summary>
    public IReadOnlyList<int> OwnerReleases => _ownerReleases.ToArray();

    public static void Register(IServiceCollection services, Action<LeaseRecordingStore> created)
        => services.AddSingleton<IMessageStore>(sp =>
        {
            var store = new LeaseRecordingStore(new InMemoryMessageStore(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MessagePickupOptions>>().Value));
            created(store);
            return store;
        });

    public async ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct)
    {
        var taken = await Inner.LeaseAsync(request, ct);
        _leases.Enqueue(new LeaseRecord(request.MaxMessages, request.MaxPayloadBytes, Live: request.AfterSequence is not null, taken.Count) { Ids = taken.Select(m => m.Id).ToArray() });
        return taken;
    }

    public async ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct)
    {
        var released = await Inner.ReleaseLeasesAsync(leaseOwner, ct);
        _ownerReleases.Enqueue(released);
        return released;
    }

    public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct) => Inner.EnqueueAsync(message, lease, now, ct);
    public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct) => Inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => Inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> ReturnUnsentAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => Inner.ReturnUnsentAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct) => Inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
    public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => Inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => Inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => Inner.PurgeGrantAsync(grantPeerDid, ct);
    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => Inner.PurgeExpiredAsync(now, ct);
}

/// <summary>One lease as <see cref="LeaseRecordingStore"/> saw it: the bounds asked for, whether it was a live push's, and how many records came back.</summary>
internal sealed record LeaseRecord(int MaxMessages, long MaxPayloadBytes, bool Live, int Taken)
{
    /// <summary>The ids of the records that came back, in order (#111: a test that drives a delivery reads how many messages it carries).</summary>
    public IReadOnlyList<string> Ids { get; init; } = Array.Empty<string>();
}
