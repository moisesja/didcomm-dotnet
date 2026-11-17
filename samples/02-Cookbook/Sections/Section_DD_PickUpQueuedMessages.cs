using System.Text;
using System.Text.Json.Nodes;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;

// L-014: alias the static ProblemReport API class so the namespace doesn't shadow it.
using ProblemReportApi = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// The mailbox: messages for a wallet wait at its mediator until the wallet asks for them. Bob asks
/// how many are waiting, fetches them (each one is opened for him), and discards the one he cannot
/// open. All of it over HTTP, against a real mediator on 127.0.0.1.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What the wallet can rely on.</strong> Delivery is at-least-once: the same message can
/// arrive twice (a lost acknowledgement, a dropped connection), and the client filters repeats per
/// mailbox, so each one reaches you once while the client remembers it. <c>FetchAsync</c>
/// acknowledges what it opened before it returns. A message that keeps failing to open is never
/// discarded for you: give up on it with <c>AckAsync</c>. And the mediator keeps a message for a
/// limited time only (30 days by default): a wallet that stays offline longer loses those messages,
/// and nobody is told.
/// </para>
/// <para>
/// The section then shows the mediator's side of the same mailbox: dropping a message straight into
/// a recipient's queue (<c>MessagePickupQueue.DeliverAsync</c>), every Message Pickup message as it
/// travels, and — for anyone writing a persistent store — what each <c>IMessageStore</c> call must do.
/// </para>
/// <para>Maps to PRD §14.2 task <strong>DD</strong> (FR-PICK-01..11).</para>
/// </remarks>
public static class Section_DD_PickUpQueuedMessages
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("DD", "Pick up queued messages");

        await using var mediator = await CookbookMediatorHost.StartAsync(ctx);
        ctx.Narrator.Step($"[mediator] listening on {mediator.Endpoint}");

        // ── Bob's wallet: the mediator client plus an HTTP transport (as in section CC) ──
        var walletSecrets = new InMemorySecretsResolver();
        foreach (var key in ctx.Bob.Privates) walletSecrets.Add(key);
        var services = new ServiceCollection();
        services.AddDidComm(b => b
            .UseNetDidResolver()
            .UseSecretsResolver(walletSecrets)
            .UseHttpTransport(o => o.AllowedSchemes = ["http", "https"]) // loopback demo; production stays https
            .Configure(o => o.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1")) // this host (on every port), nothing else
            .AddMediatorClient());
        await using var wallet = services.BuildServiceProvider();
        var mediation = wallet.GetRequiredService<MediationClient>();
        var pickup = wallet.GetRequiredService<PickupClient>();

        // Enroll (section CC): a grant, a public DID that points at the mediator, registered.
        var grant = await mediation.RequestMediationAsync(mediator.Did, ctx.Bob.Did)
            ?? throw new InvalidOperationException("The mediator refused to mediate (mediate-deny).");
        var bobPublic = await PeerIdentityFactory.CreateWithServiceEntryAsync(
            wallet.GetRequiredService<IDidManager>(),
            wallet.GetRequiredService<IKeyGenerator>(),
            wallet.GetRequiredService<ICryptoProvider>(),
            DidCommServiceBuilder.ForMediation(grant).Build());
        foreach (var key in bobPublic.Privates) walletSecrets.Add(key);
        var registered = await mediation.UpdateRecipientsAsync(mediator.Did, ctx.Bob.Did,
            [new RecipientUpdate(bobPublic.Did, RecipientUpdateAction.Add)]);
        ctx.Narrator.Value("[bob] enrolled; public DID registered", registered[0].Result);

        await FillTheMailboxAsync(ctx, mediator, bobPublic.Did);

        // ── Bob picks up: status → fetch (opened and acknowledged) → discard what he cannot open ──
        ctx.Narrator.Step("Bob asks how many messages wait for him (GetStatusAsync).");
        var status = await pickup.GetStatusAsync(mediator.Did, ctx.Bob.Did);
        ctx.Narrator.Value("[bob] status", $"{status.MessageCount} message(s), {status.TotalBytes} bytes, live delivery {status.LiveDelivery}");

        ctx.Narrator.Step("Bob fetches up to 10 (FetchAsync opens each one and acknowledges what it opened).");
        var batch = await pickup.FetchAsync(mediator.Did, ctx.Bob.Did, limit: 10);
        ctx.Narrator.Value("[bob] fetched", $"{batch.Items.Count} item(s), drained {batch.Drained}");
        var unopened = new List<string>();
        foreach (var item in batch.Items)
        {
            if (item.Unpacked is { } opened)
                ctx.Narrator.Value("[bob] opened", $"\"{opened.Message.Body?["content"]?.GetValue<string>()}\" (authenticated sender: {opened.Authenticated})");
            else
            {
                ctx.Narrator.Value("[bob] could not open", item.Error?.GetType().Name);
                unopened.Add(item.Id);
            }
        }
        ctx.Narrator.Note("Delivery is at-least-once: a message can come twice, and the client filters the repeats per mailbox. FetchAsync has already acknowledged the message it opened.");

        // A failed item stays queued (it comes back after its lease, 60 s at this mediator). When you
        // have given up on it, discard it yourself: nothing does it for you.
        var afterAck = await pickup.AckAsync(mediator.Did, ctx.Bob.Did, unopened);
        ctx.Narrator.Value("[bob] discarded the unopenable one; status", $"{afterAck.MessageCount} message(s)");

        var empty = await pickup.FetchAsync(mediator.Did, ctx.Bob.Did, limit: 10);
        ctx.Narrator.Value("[bob] fetch again", $"drained {empty.Drained}, status {empty.Status?.MessageCount} message(s)");
        ctx.Narrator.Note("The delayed message is not counted or delivered until it is due. When it becomes due it is not pushed to a live connection either, so a wallet in live mode still checks its mailbox now and then (MediatorClientOptions.LivePollInterval).");
        ctx.Narrator.Note("The mediator keeps a message for a limited time (MessagePickupOptions.MaxMessageAge, 30 days by default): a recipient offline longer than that loses those messages without notice.");

        await MessagesOnTheWireAsync(ctx);
        await StoreContractAsync(ctx);
    }

    /// <summary>
    /// The mediator's side: put messages straight into Bob's queue with
    /// <see cref="MessagePickupQueue.DeliverAsync"/>. This is the call the mediator makes for every
    /// forward it accepts; a mediator app can make it too, for a message of its own.
    /// </summary>
    private static async Task FillTheMailboxAsync(CookbookContext ctx, CookbookMediatorHost mediator, string bobPublicDid)
    {
        ctx.Narrator.Step("[mediator] the app drops messages into Bob's queue (MessagePickupQueue.DeliverAsync).");
        var queue = mediator.Services.GetRequiredService<MessagePickupQueue>();

        // Alice encrypts a note to Bob's public DID (authcrypt). The queue stores the bytes as they
        // are: only Bob can open them.
        var note = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(bobPublicDid)
            .WithBody(new JsonObject { ["content"] = "Hello Bob, from your mailbox." })
            .Build();
        var packed = await ctx.Client.PackEncryptedAsync(note, new PackEncryptedOptions(Recipients: [bobPublicDid], From: ctx.Alice.Did));

        // The queue keeps the buffer it is given (no copy), so each call gets its own.
        byte[] Payload() => Encoding.UTF8.GetBytes(packed.Message);

        // Due now, expires in a day (the message's own expires_time, in epoch seconds), and stored
        // without trying a live push (Bob has no live connection anyway).
        var options = new PickupDeliveryOptions(NotBefore: null, ExpiresTime: DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(), AllowLive: false);
        ctx.Narrator.Value("Options", $"not before {options.NotBefore?.ToString("u") ?? "now"}, expires_time set {options.ExpiresTime is not null}, try live {options.AllowLive}");
        var stored = await queue.DeliverAsync(bobPublicDid, Payload(), options);
        ctx.Narrator.Value("[mediator] Alice's note", $"{stored.Outcome}, held under Bob's grant: {stored.GrantPeerDid == ctx.Bob.Did}, id {stored.MessageId}");

        // A delayed one: queued now, but not counted or delivered for an hour.
        var delayed = await queue.DeliverAsync(bobPublicDid, Payload(), new PickupDeliveryOptions(NotBefore: DateTimeOffset.UtcNow.AddHours(1)));
        ctx.Narrator.Value("[mediator] the same note, not before an hour from now", delayed.Outcome);

        // Bytes Bob cannot open: queued like anything else (the mediator never looks inside).
        var junk = await queue.DeliverAsync(bobPublicDid, "this is not a DIDComm message"u8.ToArray(), options);
        ctx.Narrator.Value("[mediator] something Bob cannot open", junk.Outcome);

        // A DID nobody registered: refused, nothing stored.
        var stranger = await queue.DeliverAsync(ctx.Alice.Did, Payload(), options);
        ctx.Narrator.Value("[mediator] a DID nobody registered", $"{stranger.Outcome}, id {stranger.MessageId ?? "none"}");
    }

    /// <summary>
    /// Every Message Pickup message, built with <see cref="MessagePickup"/>, sent as plaintext and
    /// read back with the matching parser. <see cref="PickupClient"/> and the mediator do this for
    /// you; this is for a handler of your own or a fake mediator in tests.
    /// </summary>
    private static async Task MessagesOnTheWireAsync(CookbookContext ctx)
    {
        ctx.Narrator.Step("The Message Pickup messages on the wire (build → send → parse).");
        const string wallet = "did:example:bob";
        const string mediatorDid = "did:example:mediator";
        const string bobPublic = "did:example:bob-public";
        const string queuedId = "6f1c2a7e9b0d4e3fa1b2c3d4e5f60718"; // the mediator's id for a queued message

        // status-request → status
        var statusRequest = await OverTheWireAsync(ctx, MessagePickup.CreateStatusRequest(wallet, mediatorDid, recipientDid: bobPublic));
        if (MessagePickup.TryParseStatusRequest(statusRequest, out var about))
            ctx.Narrator.Value("status-request → TryParseStatusRequest", $"recipient_did {about ?? "(all)"}");

        var statusMessage = await OverTheWireAsync(ctx, MessagePickup.CreateStatus(mediatorDid, wallet,
            new PickupStatus(MessageCount: 2, RecipientDid: bobPublic, LongestWaitedSeconds: 90,
                NewestReceivedTime: 1_900_000_000, OldestReceivedTime: 1_899_999_910, TotalBytes: 4096, LiveDelivery: false),
            MessagePickup.ResponseThreadId(statusRequest)));
        if (MessagePickup.TryParseStatus(statusMessage, out var status))
        {
            ctx.Narrator.Value("status → TryParseStatus", $"message_count={status.MessageCount}, recipient_did={status.RecipientDid}, longest_waited_seconds={status.LongestWaitedSeconds}, " +
                $"newest/oldest_received_time={status.NewestReceivedTime}/{status.OldestReceivedTime}, total_bytes={status.TotalBytes}, live_delivery={status.LiveDelivery}");
        }

        // delivery-request → delivery (one attachment per message)
        var deliveryRequest = await OverTheWireAsync(ctx, MessagePickup.CreateDeliveryRequest(wallet, mediatorDid, limit: 10));
        if (MessagePickup.TryParseDeliveryRequest(deliveryRequest, out var limit, out var filter))
            ctx.Narrator.Value("delivery-request → TryParseDeliveryRequest", $"limit={limit}, recipient_did={filter ?? "(any)"}");

        var delivery = await OverTheWireAsync(ctx, MessagePickup.CreateDelivery(mediatorDid, wallet,
            [new DeliveredMessage(queuedId, Encoding.UTF8.GetBytes("<an encrypted message>"))],
            MessagePickup.ResponseThreadId(deliveryRequest)));
        if (MessagePickup.TryParseDelivery(delivery, out var delivered, out _))
            ctx.Narrator.Value("delivery → TryParseDelivery", string.Join("; ", delivered.Select(d => $"id={d.Id}, {d.Payload.Length} bytes")));

        // messages-received → status
        var received = await OverTheWireAsync(ctx, MessagePickup.CreateMessagesReceived(wallet, mediatorDid, [queuedId]));
        if (MessagePickup.TryParseMessagesReceived(received, out var ids))
            ctx.Narrator.Value("messages-received → TryParseMessagesReceived", $"message_id_list [{string.Join(", ", ids)}]");

        // live-delivery-change → status, or a problem report on a connection that cannot push
        var change = await OverTheWireAsync(ctx, MessagePickup.CreateLiveDeliveryChange(wallet, mediatorDid, liveDelivery: true));
        if (MessagePickup.TryParseLiveDeliveryChange(change, out var live))
            ctx.Narrator.Value("live-delivery-change → TryParseLiveDeliveryChange", $"live_delivery {live}");

        var problem = await OverTheWireAsync(ctx, MessagePickup.CreateLiveModeNotSupported(mediatorDid, wallet, MessagePickup.ResponseThreadId(change)));
        ctx.Narrator.Value("its problem report (CreateLiveModeNotSupported)", $"code {ProblemReportApi.ReadCode(problem)}, pthid is the change's id: {problem.Pthid == change.Id}");
    }

    /// <summary>
    /// For store authors: what each <see cref="IMessageStore"/> call does, walked on a standalone
    /// <see cref="InMemoryMessageStore"/>. A persistent store (a database, a cloud queue) keeps the
    /// same contract; register it before <c>AddMediator()</c> / <c>AddMessagePickup()</c>.
    /// </summary>
    private static async Task StoreContractAsync(CookbookContext ctx)
    {
        ctx.Narrator.Step("DD.3 For store authors: what each IMessageStore call does.");
        var memory = new InMemoryMessageStore(); // the default bounds of MessagePickupOptions
        IMessageStore store = memory;            // every call below goes through the contract
        var ct = CancellationToken.None;

        // The store reads no clock: every call that depends on time is told the instant.
        var now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var ageLimit = new MessagePickupOptions().MaxMessageAge ?? TimeSpan.FromDays(30);
        const string bob = "did:example:bob";               // a grant: the DID that enrolled
        const string bobPublic = "did:example:bob-public";  // DIDs registered under it
        const string bobWork = "did:example:bob-work";
        const string carol = "did:example:carol";           // another grant

        // The queue sets ExpiresAt to the earlier of the message's own expiry and the age limit.
        QueuedMessage Record(string id, string grant, string recipient) => new(
            Id: id, GrantPeerDid: grant, RecipientDid: recipient,
            Payload: Encoding.UTF8.GetBytes($"<encrypted message {id}>"),
            ReceivedAt: now, NotBefore: null, ExpiresAt: now + ageLimit, PassOvers: 0);

        // EnqueueAsync: one atomic check-and-insert. Refusals are outcomes, never exceptions.
        var outcomes = new List<EnqueueOutcome>();
        foreach (var record in new[] { Record("m1", bob, bobPublic), Record("m2", bob, bobPublic), Record("m3", bob, bobWork), Record("c1", carol, carol) })
            outcomes.Add(await store.EnqueueAsync(record, lease: null, now, ct));
        ctx.Narrator.Value("EnqueueAsync m1, m2, m3 (Bob), c1 (Carol)", string.Join(", ", outcomes));
        ctx.Narrator.Value("EnqueueAsync m1 again", await store.EnqueueAsync(Record("m1", bob, bobPublic), lease: null, now, ct));
        ctx.Narrator.Value("Whole store: MessageCount / TotalBytes", $"{memory.MessageCount} / {memory.TotalBytes}");

        // GetStatusAsync: what the recipient can see now. HighWaterSequence is the store's arrival
        // counter: every record stored later gets a higher number.
        var status = await store.GetStatusAsync(bob, recipientDid: null, now, ct);
        ctx.Narrator.Value("GetStatusAsync(Bob)", $"{status.MessageCount} message(s), {status.TotalBytes} bytes, " +
            $"received {status.OldestReceivedAt:u} … {status.NewestReceivedAt:u}, high-water sequence {status.HighWaterSequence}");

        // LeaseAsync: select and mark in one step, oldest first. The owner token names the
        // connection the messages go out on, so closing it can end all its leases at once.
        var socketLease = new MessageLease(Owner: "connection-1", ExpiresAt: now.AddSeconds(60));
        var request = new MessageLeaseRequest(GrantPeerDid: bob, RecipientDid: null, MaxMessages: 2, MaxPayloadBytes: 64 * 1024, Now: now, Lease: socketLease);
        ctx.Narrator.Value("Lease request", $"grant {request.GrantPeerDid}, recipient {request.RecipientDid ?? "any"}, up to {request.MaxMessages} message(s) / {request.MaxPayloadBytes} bytes, " +
            $"at {request.Now:u}, after sequence {request.AfterSequence?.ToString() ?? "any"}, owner {request.Lease.Owner}");
        var taken = await store.LeaseAsync(request, ct);
        ctx.Narrator.Value("LeaseAsync", Ids(taken));
        var first = taken[0];
        ctx.Narrator.Value("First record", $"id {first.Id}, grant {first.GrantPeerDid}, recipient {first.RecipientDid}, {first.Payload.Length} bytes, received {first.ReceivedAt:u}, " +
            $"not before {first.NotBefore?.ToString("u") ?? "-"}, expires {first.ExpiresAt:u}, passed over {first.PassOvers}x, sequence {first.Sequence}");
        ctx.Narrator.Value("The lease", $"id of {socketLease.Id.Length} random hex characters, owner {socketLease.Owner}, ends {socketLease.ExpiresAt:u}");

        // ReleaseAsync: the taker gives back what it did not send; deliverable again, in place.
        ctx.Narrator.Value("ReleaseAsync(m2, that lease)", await store.ReleaseAsync(bob, ["m2"], socketLease.Id, ct));

        // A second taker over HTTP (no owner: its lease ends by time only), for what arrived after #1.
        var httpLease = new MessageLease(Owner: null, ExpiresAt: now.AddSeconds(60));
        var after = await store.LeaseAsync(new MessageLeaseRequest(bob, null, 10, 64 * 1024, now, httpLease) { AfterSequence = 1 }, ct);
        ctx.Narrator.Value("LeaseAsync, after sequence 1", Ids(after));

        // PassOverAsync: m3 cannot go out now (say, too big for this connection). It stays stored,
        // counted and in place, and is not handed out before the given instant.
        ctx.Narrator.Value("PassOverAsync(m3, for 5 minutes)", await store.PassOverAsync(bob, ["m3"], httpLease.Id, now.AddMinutes(5), ct));
        var nothing = await store.LeaseAsync(new MessageLeaseRequest(bob, null, 10, 64 * 1024, now, new MessageLease(null, now.AddSeconds(60))), ct);
        ctx.Narrator.Value("LeaseAsync now", $"{nothing.Count} (two leased, one set aside); GetStatusAsync still counts {(await store.GetStatusAsync(bob, null, now, ct)).MessageCount}");

        // ReleaseLeasesAsync: connection-1 closed; what went out over it is deliverable again.
        ctx.Narrator.Value("ReleaseLeasesAsync(connection-1)", await store.ReleaseLeasesAsync("connection-1", ct));

        // Ten minutes on, every lease and the pass-over mark have run out; the mark's count stays.
        var later = now.AddMinutes(10);
        var again = await store.LeaseAsync(new MessageLeaseRequest(bob, null, 10, 64 * 1024, later, new MessageLease(null, later.AddSeconds(60))), ct);
        ctx.Narrator.Value("LeaseAsync 10 minutes later", string.Join(", ", again.Select(m => $"{m.Id} (passed over {m.PassOvers}x)")));

        // The ways out: acknowledged, purged by the host, or expired.
        ctx.Narrator.Value("AcknowledgeAsync(m1)", await store.AcknowledgeAsync(bob, ["m1"], ct));
        ctx.Narrator.Value("PurgeRecipientAsync(Bob, bob-work), after the host removed that DID", await store.PurgeRecipientAsync(bob, bobWork, ct));
        ctx.Narrator.Value("PurgeGrantAsync(Carol), after the host revoked her grant", await store.PurgeGrantAsync(carol, ct));
        ctx.Narrator.Value("PurgeExpiredAsync(30 days later)", await store.PurgeExpiredAsync(now + ageLimit, ct));
        var end = await store.GetStatusAsync(bob, null, now + ageLimit, ct);
        ctx.Narrator.Value("Bob's queue is empty", end.MessageCount == QueueStatus.Empty.MessageCount && end.TotalBytes == QueueStatus.Empty.TotalBytes);
        ctx.Narrator.Value("Whole store: MessageCount / TotalBytes", $"{memory.MessageCount} / {memory.TotalBytes}");

        // Bounds: a store with room for two messages per grant refuses the third, without throwing.
        IMessageStore small = new InMemoryMessageStore(new MessagePickupOptions { MaxQueueMessages = 2 });
        var bounded = new List<EnqueueOutcome>();
        foreach (var id in new[] { "b1", "b2", "b3" })
            bounded.Add(await small.EnqueueAsync(Record(id, bob, bobPublic), lease: null, now, ct));
        ctx.Narrator.Value("Store bounded at 2 per grant: EnqueueAsync ×3", string.Join(", ", bounded));
    }

    private static string Ids(IReadOnlyList<QueuedMessage> records)
        => records.Count == 0 ? "none" : string.Join(", ", records.Select(m => $"{m.Id} (#{m.Sequence})"));

    // Plaintext is enough to show the shapes; real traffic is authcrypted (section F).
    private static async Task<Message> OverTheWireAsync(CookbookContext ctx, Message message)
        => (await ctx.Client.UnpackAsync(await ctx.Client.PackPlaintextAsync(message))).Message;
}
