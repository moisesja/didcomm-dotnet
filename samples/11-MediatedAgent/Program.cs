using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Mediator;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Samples.MediatedAgent.Wallet;
using DidComm.Samples.Shared;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;

namespace DidComm.Samples.MediatedAgent;

/// <summary>
/// A complete mediator, end to end (PRD §14.3 sample 11). The mediator is an ASP.NET Core app built
/// with <c>AddMediator()</c> and <c>MapDidCommMediator()</c>. Bob's wallet has no endpoint of its
/// own: it reads the mediator's invitation, enrolls, publishes a DID reached through the mediator,
/// and picks his messages up over HTTP and live over a WebSocket. Alice reaches Bob with a plain
/// <c>SendAsync</c>. Carol, who is not on the operator's allow-list, is refused. The operator
/// inspects and changes what the mediator holds, then shuts it down.
/// <see cref="Main"/> is the command line; <see cref="RunAsync"/> is the seam the smoke test calls
/// (loopback only, a free port, no fixed waits: FR-DX-02).
/// </summary>
public static class Program
{
    // Every wait below is for a specific event; this only turns a hang into a clear failure.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>Command-line entry point: writes to the console and exits 0 on success.</summary>
    public static async Task<int> Main()
    {
        try
        {
            await RunAsync(Console.Out).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MediatedAgent failed: {ex}");
            return 1;
        }
    }

    /// <summary>Run the whole story, writing the narration to <paramref name="output"/>.</summary>
    /// <param name="output">Where the narration goes; <c>null</c> for the console.</param>
    public static async Task RunAsync(TextWriter? output = null)
    {
        var narrator = output is null ? new Narrator() : new Narrator(output);

        // ── 1. The mediator ─────────────────────────────────────────────────────────────────
        narrator.Section("1", "Start the mediator (AddMediator + MapDidCommMediator)");
        var settings = CheckSettings(narrator);
        await using var mediator = await MediatorHost.StartAsync(settings).ConfigureAwait(false);
        DescribeMediator(narrator, mediator);

        // ── 2. Bob enrolls ──────────────────────────────────────────────────────────────────
        narrator.Section("2", "Bob enrolls: the invitation, a grant, a DID that routes through the mediator");
        await using var bob = await RecipientWallet.CreateAsync("bob", narrator).ConfigureAwait(false);
        // Enrolment is open by default. This operator only grants mediation to peers that signed up
        // with it first: Bob did, so his DID goes on the allow-list.
        mediator.Policy.Allow(bob.Did);
        narrator.Step($"[mediator] Bob signed up: his DID is on the allow-list ({mediator.Policy.Count} peer)");
        await bob.ReadInvitationAsync(mediator.InvitationUrl).ConfigureAwait(false);
        if (!await bob.RequestMediationAsync().ConfigureAwait(false))
            throw new InvalidOperationException("The mediator refused Bob.");
        var bobPublic = await bob.CreateRoutedDidAsync("his public DID").ConfigureAwait(false);
        var bobShop = await bob.CreateRoutedDidAsync("a second DID, for a shop").ConfigureAwait(false);
        await bob.ListRoutedDidsAsync().ConfigureAwait(false);

        // ── 3. Alice sends ──────────────────────────────────────────────────────────────────
        narrator.Section("3", "Alice sends to Bob's public DID (the route goes through the mediator)");
        await using var alice = await AliceAgent.CreateAsync().ConfigureAwait(false);
        // What Alice's resolver reads from Bob's DID before anything is sent: his service names the
        // mediator's DID, not an address. A sender expands it into the mediator's own address and
        // wraps one forward for the mediator's keys (FR-ROUTE-04).
        var route = await alice.ResolveServiceAsync(bobPublic).ConfigureAwait(false);
        narrator.Value("Bob's service uri (the mediator's DID)", Text.Trunc(route.Uri, 48));
        narrator.Value("It is the mediator's DID", string.Equals(route.Uri, mediator.Did, StringComparison.Ordinal));
        await SendAndWaitUntilStoredAsync(narrator, mediator, alice, bobPublic, "Hello Bob", show: true).ConfigureAwait(false);

        // ── 4. Bob picks up over HTTP ───────────────────────────────────────────────────────
        narrator.Section("4", "Bob picks up over HTTP: status, fetch, status again");
        var fetched = await bob.PickUpOverHttpAsync(bobPublic).ConfigureAwait(false);
        if (!fetched.Contains("Hello Bob"))
            throw new InvalidOperationException("Bob did not receive Alice's message.");

        // ── 5. Bob goes live over a WebSocket ───────────────────────────────────────────────
        narrator.Section("5", "Bob goes live over a WebSocket: Alice sends again, Bob receives it");
        await ReceiveLiveAsync(narrator, mediator, bob, alice, bobPublic).ConfigureAwait(false);

        // ── 6. Carol is refused ─────────────────────────────────────────────────────────────
        narrator.Section("6", "Carol is refused: mediate-deny from the allow-list policy");
        await using var carol = await RecipientWallet.CreateAsync("carol", narrator).ConfigureAwait(false);
        await carol.ReadInvitationAsync(mediator.InvitationUrl).ConfigureAwait(false);
        if (await carol.RequestMediationAsync().ConfigureAwait(false))
            throw new InvalidOperationException("The allow-list let Carol in.");
        narrator.Step("[carol] mediate-deny: not on the allow-list");

        // ── 7. The operator ─────────────────────────────────────────────────────────────────
        narrator.Section("7", "The operator's console, then shutdown");
        await OperateAsync(narrator, mediator, alice, bob.Did, bobPublic, bobShop, carol).ConfigureAwait(false);
    }

    /// <summary>Check every setting before deploying, and show two settings the checks refuse.</summary>
    private static MediatorOptions CheckSettings(Narrator narrator)
    {
        var settings = new MediatorOptions();
        MediatorHost.ConfigureMediator(settings);
        settings.Validate(); // AddMediator() checks them too, and again on the values in effect at start-up
        narrator.Step("Settings checked before deploying: OK");

        // A mailbox age limit shorter than the longest delay a sender may ask for would let a delayed
        // message age out before it is due.
        var tooShort = new MediatorOptions();
        MediatorHost.ConfigureMediator(tooShort);
        tooShort.Pickup.MaxMessageAge = TimeSpan.FromMinutes(10); // shorter than MaxForwardDelay (30 minutes)
        narrator.Step($"Invalid settings refused: {RefusalOf(tooShort.Validate)}");

        // Each part's settings check themselves too: a peer may not hold more sessions than the
        // registry holds in all. The messages name the setting, never its value.
        var sessions = new ReturnRouteSessionOptions { MaxSessionsPerPeer = 8, MaxSessions = 4, MaxPendingPushes = 16, MaxPendingNotifications = 256 };
        narrator.Step($"Invalid settings refused: {RefusalOf(sessions.Validate)}");
        return settings;
    }

    // The message of the exception a settings check throws. Session and replay settings report with
    // InvalidOperationException, the others with ArgumentException.
    private static string RefusalOf(Action validate)
    {
        try
        {
            validate();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ex.Message;
        }
        throw new InvalidOperationException("UNEXPECTED: the settings were accepted.");
    }

    private static void DescribeMediator(Narrator narrator, MediatorHost mediator)
    {
        narrator.Step($"[mediator] listening on {mediator.HttpEndpoint}");
        // The DID was minted with DidCommServiceBuilder.ForEndpoint(its own URL): a known port is
        // needed because the DID names the address and is checked before the server listens.
        narrator.Step($"[mediator] routing DID = {Text.Trunc(mediator.Did, 48)}");
        var identity = mediator.App.Services.GetRequiredService<IMediatorIdentity>();
        narrator.Value("[mediator] routing DIDs", mediator.Identity.RoutingDids.Count);
        narrator.Value("[mediator] primary routing DID is the one minted", string.Equals(identity.RoutingDids[0], mediator.Did, StringComparison.Ordinal));
        narrator.Value("[mediator] largest message it queues (bytes)", mediator.Queue.MaxAdmissiblePayloadBytes);

        // The replay guard checks every Coordinate Mediation and Message Pickup message, with or
        // without return_route; a forward is exempt (it carries no sender of its own).
        narrator.Value("[mediator] protocols the replay guard always checks", MediatorServiceProtocols.All.Count);
        narrator.Value("[mediator] mediate-request is one of them", MediatorServiceProtocols.IsMediatorServiceType(CoordinateMediation.MediateRequestType));
        narrator.Value("[mediator] forward is exempt", MediatorServiceProtocols.IsReplayExempt(ForwardConstants.ForwardTypeUri));

        // What the operator publishes, as a link or a QR code. It reveals only the routing DID.
        narrator.Step($"[mediator] invitation URL = {mediator.InvitationUrl}");
    }

    /// <summary>Alice sends one message to <paramref name="to"/> and the sample waits until the mediator has stored it.</summary>
    private static async Task SendAndWaitUntilStoredAsync(Narrator narrator, MediatorHost mediator, AliceAgent alice, string to, string content, bool show)
    {
        var storedBefore = mediator.StoredForwards.Stored; // the count before the send
        var sent = await alice.SendAsync(to, content).ConfigureAwait(false);
        if (show)
        {
            narrator.Value("Endpoint used (from the mediator's own DID)", sent.EndpointUsed);
            narrator.Value("Transport HTTP status", sent.Transport.HttpStatusCode);
        }
        // The 202 means the mediator received the forward; it is queued just after. Wait for that.
        await AwaitOrFailAsync(mediator.StoredForwards.WhenStoredAsync(storedBefore + 1), "the mediator to store Alice's message").ConfigureAwait(false);
        narrator.Step($"[mediator] stored Alice's message \"{content}\" for pickup");
    }

    private static async Task ReceiveLiveAsync(Narrator narrator, MediatorHost mediator, RecipientWallet bob, AliceAgent alice, string bobPublic)
    {
        // The operator watches sessions open and close. These notifications are best effort (they
        // can arrive late, or be dropped under load); a session's own Closed token is the signal
        // to rely on.
        var opened = 0;
        var closed = 0;
        EventHandler<ReturnRouteSessionEventArgs> onOpened = (_, e) =>
        {
            if (e.Reason == SessionCloseReason.None && e.Session.Transport == ReturnRouteTransport.WebSocket)
                Interlocked.Increment(ref opened);
        };
        EventHandler<ReturnRouteSessionEventArgs> onClosed = (_, e) =>
        {
            if (e.Reason is SessionCloseReason.Unregistered or SessionCloseReason.Evicted)
                Interlocked.Increment(ref closed);
        };
        mediator.Sessions.SessionOpened += onOpened;
        mediator.Sessions.SessionClosed += onClosed;
        try
        {
            var sessionClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var live = bob.ConnectLive(mediator.WebSocketEndpoint);
            try
            {
                await live.ShowLiveSwitchAsync().ConfigureAwait(false);

                // Start the stream (it turns live delivery on and takes what is queued), then Alice
                // sends. Bob gets the message by push, or by the stream's own collection if it was
                // stored first: either way it arrives, and that is what the sample checks.
                var next = live.ReceiveNextAsync();
                await SendAndWaitUntilStoredAsync(narrator, mediator, alice, bobPublic, "Hello again, Bob", show: false).ConfigureAwait(false);
                var item = await AwaitOrFailAsync(next, "Bob's live delivery").ConfigureAwait(false);
                var text = item.Unpacked?.Message.Body?["content"]?.GetValue<string>();
                if (text != "Hello again, Bob")
                    throw new InvalidOperationException("Bob's live stream returned something else.");
                narrator.Step($"[bob] live: {text}");
                await live.StopAndAcknowledgeAsync(item).ConfigureAwait(false);

                // The operator looks Bob up among the live connections. Bob's DID opened one
                // WebSocket session (sessions are keyed by the DID that authenticated them).
                var sessions = mediator.Sessions.Find(bob.Did);
                narrator.Step($"[mediator] sessions for Bob: {sessions.Count}");
                narrator.Value("[mediator] session transport", sessions[0].Transport);
                sessions[0].Closed.Register(() => sessionClosed.TrySetResult());
            }
            finally
            {
                await live.DisposeAsync().ConfigureAwait(false); // Bob goes offline: his connection closes
            }

            await AwaitOrFailAsync(sessionClosed.Task, "Bob's session to close").ConfigureAwait(false);
            narrator.Step("[mediator] Bob's session closed (its Closed token fired)");
            narrator.Value("[mediator] sessions open now", mediator.Sessions.Count);
            narrator.Note($"Session notifications seen so far: {Volatile.Read(ref opened)} opened, {Volatile.Read(ref closed)} closed (best effort: they may still be on their way).");
        }
        finally
        {
            mediator.Sessions.SessionOpened -= onOpened;
            mediator.Sessions.SessionClosed -= onClosed;
        }
    }

    private static async Task OperateAsync(Narrator narrator, MediatorHost mediator, AliceAgent alice, string bob, string bobPublic, string bobShop, RecipientWallet carol)
    {
        // The mediation store, read and changed by the operator.
        IMediationStore store = mediator.MediationStore;
        var grant = await store.GetGrantAsync(bob, CancellationToken.None).ConfigureAwait(false);
        narrator.Step($"[operator] Bob's grant: routing DIDs = {grant?.RoutingDids.Count}");
        var owner = await store.FindGrantByRecipientDidAsync(bobPublic, CancellationToken.None).ConfigureAwait(false);
        narrator.Value("[operator] Bob's public DID belongs to Bob's grant", string.Equals(owner?.PeerDid, bob, StringComparison.Ordinal));
        var page = await store.ListRecipientsAsync(bob, offset: 0, limit: 10, CancellationToken.None).ConfigureAwait(false);
        narrator.Value("[operator] Bob's registered DIDs", $"{page.Dids.Count} of {page.Total}");
        narrator.Value("[operator] grants / registered DIDs in the store", $"{mediator.MediationStore.GrantCount} / {mediator.MediationStore.RecipientCount}");

        // Bob closes his shop. Alice wrote to it while he was offline; the operator unregisters the
        // DID and drops what was waiting for it.
        await SendAndWaitUntilStoredAsync(narrator, mediator, alice, bobShop, "Is the shop open?", show: false).ConfigureAwait(false);
        var removal = new RecipientUpdate(bobShop, RecipientUpdateAction.Remove);
        var outcomes = await store.UpdateRecipientsAsync(bob, new[] { removal }, CancellationToken.None).ConfigureAwait(false);
        narrator.Step($"[operator] {removal.Action} Bob's shop DID: {outcomes[0]}");
        var purged = await mediator.MessageStore.PurgeRecipientAsync(bob, removal.RecipientDid, CancellationToken.None).ConfigureAwait(false);
        narrator.Step($"[operator] Purged recipient: {purged} message(s) that were waiting for that DID");

        // The operator's own code can put a message in a mailbox, and take it back out (for example
        // once it delivered it some other way).
        var notice = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(mediator.Did)
            .WithTo(bobPublic)
            .WithBody(new JsonObject { ["content"] = "Maintenance tonight" })
            .Build();
        var packed = await mediator.Client.PackEncryptedAsync(notice, new PackEncryptedOptions(Recipients: new[] { bobPublic }, From: mediator.Did)).ConfigureAwait(false);
        var delivered = await mediator.Queue.DeliverAsync(bobPublic, Encoding.UTF8.GetBytes(packed.Message), new PickupDeliveryOptions(AllowLive: false)).ConfigureAwait(false);
        narrator.Step($"[operator] queued a notice for Bob: {delivered.Outcome}");
        var removed = await mediator.Queue.RemoveAsync(delivered.GrantPeerDid!, delivered.MessageId!).ConfigureAwait(false);
        narrator.Step($"[operator] Removed by operator: {removed}");

        // Outside a generic host nothing purges on a schedule; here the hosted service does it every
        // PurgeInterval, and the operator can also run it now.
        var expired = await mediator.Queue.PurgeExpiredAsync().ConfigureAwait(false);
        narrator.Value("[operator] expired messages removed now", expired);

        // A peer the operator trusts can be granted directly. Its mediate-request is then answered
        // with that grant, and the policy is not asked again.
        var provisioned = await store.GrantAsync(new MediationGrant(carol.Did, mediator.Identity.RoutingDids), CancellationToken.None).ConfigureAwait(false);
        narrator.Step($"[operator] pre-provisioned a grant for Carol: {provisioned is not null}");
        if (!await carol.RequestMediationAsync().ConfigureAwait(false))
            throw new InvalidOperationException("Carol's pre-provisioned grant was not returned.");
        var carolRevoked = await store.RevokeAsync(carol.Did, CancellationToken.None).ConfigureAwait(false);
        narrator.Step($"[operator] Carol's grant revoked: {carolRevoked}");

        // Ending Bob's mediation: revoke the grant and drop everything held for him, in one call.
        await SendAndWaitUntilStoredAsync(narrator, mediator, alice, bobPublic, "See you soon", show: false).ConfigureAwait(false);
        var revocation = await mediator.Queue.RevokeAndPurgeAsync(bob).ConfigureAwait(false);
        narrator.Step($"[operator] Bob's mediation ended: Revoked = {revocation.Revoked}, Purged = {revocation.Purged}");
        narrator.Value("[operator] grants left", mediator.MediationStore.GrantCount);

        // What the mediator's guards saw during the run.
        var guard = mediator.ReplayGuard;
        narrator.Value("[operator] replay guard: message ids remembered / peers", $"{guard.Count} / {guard.PeerCount}");
        narrator.Value("[operator] replay guard: refused while full / refused for a clock behind", $"{guard.SaturationCount} / {guard.ClockBehindRefusalCount}");
        var sessions = mediator.Sessions;
        narrator.Value("[operator] sessions: opened / ended / evicted / refused", $"{sessions.Registered} / {sessions.Unregistered} / {sessions.Evicted} / {sessions.Refused}");
        narrator.Value("[operator] session notifications dropped", sessions.DroppedNotifications);
        narrator.Step("[mediator] shutting down (the container disposes the session registry it built)");
    }

    /// <summary>Wait for an event, failing loudly instead of hanging (FR-DX-02).</summary>
    private static async Task AwaitOrFailAsync(Task task, string what)
    {
        try
        {
            await task.WaitAsync(HangGuard).ConfigureAwait(false);
        }
        catch (TimeoutException ex) when (!task.IsCompleted)
        {
            Observe(task);
            throw new TimeoutException($"Timed out waiting for {what}.", ex);
        }
    }

    /// <summary>Wait for a result, failing loudly instead of hanging (FR-DX-02).</summary>
    private static async Task<T> AwaitOrFailAsync<T>(Task<T> task, string what)
    {
        await AwaitOrFailAsync((Task)task, what).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }

    // A task given up on may still fail later (when its connection is disposed): observe that failure.
    private static void Observe(Task task)
        => _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
