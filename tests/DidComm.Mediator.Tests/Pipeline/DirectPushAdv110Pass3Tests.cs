using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// Red-team pass 3a (#110, direct push): the pass-2 fixes in the push's endpoint check before the take
/// (ADV-110-2a-1, 2a-2: <see cref="IDirectPushSender.CheckTargetAsync"/>), the yield rule
/// (ADV-110-2b-1: a run yields unless it delivered nothing) and live-on waking any peer with the
/// directory's widened contract (ADV-110-2b-2, 2b-3). Seam-driven: the outbound policy's lookup seam,
/// the scripted directory and sender of <see cref="DirectWorld"/>, the manual clock; no sleeps.
/// </summary>
public sealed class DirectPushAdv110Pass3Tests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PushTimeout = new MediatorOptions().PushTimeout;
    private static readonly TimeSpan Pause = PushDispatcher.CooldownPushTimeouts * PushTimeout;

    // ---- the endpoint check before the take (fix for ADV-110-2a-2) ------------------------------------------

    // ADV-110-3a-1, the documented residual (owner decision OD-7 on #110: "Document it"). On a host whose
    // own connection code replaced the library's (a connect callback of its own that checks nothing, or a
    // primary handler of its own: both pass the start-up check), the check before a push is a name check
    // only: it looks the name up on its own, and the connection looks the same name up again and connects
    // wherever that second answer points. The host's connection code must check addresses at connect
    // itself (MediatorOptions.DirectPush, DirectPushSender, D-7 / residual 5). Modelled
    // deterministically: the lookup seam gives the check a public answer for "localhost" (the first
    // answer); the connection resolves it for real (the second) and the POST reaches the loopback. The
    // default UseHttpTransport handler (the control below) checks the connection when it opens: 0 POSTs.
    [Theory]
    [InlineData("a connect callback of the host's own (socket options)")]
    [InlineData("a primary handler of the host's own")]
    public async Task ADV_110_3a_1_On_a_host_with_its_own_connection_code_the_check_before_a_push_is_a_name_check_and_the_name_is_looked_up_again_at_connect(string row)
        => (await PushToReboundLocalhostAsync(row)).Should().Be(1, "the check passed the name, and the connection went where the second lookup pointed: the host's connection code must check addresses at connect (OD-7)");

    // The control for ADV-110-3a-1: with the default UseHttpTransport handler the connect callback looks
    // the name up again and pins the connection to a vetted address, so the rebound answer is refused
    // when the connection opens. (Could not break.)
    [Fact]
    public async Task Control_With_the_default_handler_a_name_rebound_to_the_loopback_after_the_check_is_refused_at_connect()
        => (await PushToReboundLocalhostAsync("the default UseHttpTransport handler")).Should().Be(0);

    // Every spelling of a loopback, private, link-local or NAT64/6to4/mapped address, and the names the
    // platform resolves to the loopback (localhost, *.localhost, with or without the trailing dot), are
    // refused by the check before the take with the real resolver, on a host whose connect callback checks
    // nothing (the configuration where that check is the only one). (Could not break.)
    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST.")]
    [InlineData("inbox.localhost")]
    [InlineData("inbox.localhost.")]
    [InlineData("127.1")]
    [InlineData("0x7f.1")]
    [InlineData("0177.0.0.1")]
    [InlineData("2130706433")]
    [InlineData("127。0。0。1")]
    [InlineData("0")]
    [InlineData("[::]")]
    [InlineData("[::1]")]
    [InlineData("[::ffff:7f00:1]")]
    [InlineData("[0:0:0:0:0:ffff:127.0.0.1]")]
    [InlineData("[::ffff:0:7f00:1]")]
    [InlineData("[::127.0.0.1]")]
    [InlineData("[64:ff9b::a9fe:a9fe]")]
    [InlineData("[64:ff9b:1::1]")]
    [InlineData("[2002:a9fe:a9fe::]")]
    [InlineData("[2001:0::1]")]
    [InlineData("[fe80::1%25lo0]")]
    [InlineData("[fd00::1]")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("224.0.0.1")]
    public async Task Every_spelling_of_a_private_address_is_refused_before_the_take_with_the_real_resolver(string host)
    {
        await using var container = Container(o => o.AllowedSchemes = new[] { "http", "https" }, more: s =>
            s.AddHttpClient(HttpDidCommTransport.HttpClientName).UseSocketsHttpHandler((h, _) => h.ConnectCallback = PlainConnectAsync));
        var sender = DirectPushSender.Create(container);

        var check = await sender.CheckTargetAsync(new Uri($"http://{host}:8080/didcomm"), CancellationToken.None);

        check.Verdict.Should().Be(PushTarget.Refused, "{0} names a private or reserved address", host);
    }

    // ---- what an abandoned lookup holds (fix for ADV-110-2a-1) ----------------------------------------------

    // ADV-110-3a-2 (Low). A lookup that ignores cancellation is abandoned at the push deadline
    // (OutboundEndpointGuard.ValidateAsync waits with WaitAsync): the operation settles, the recipient
    // pauses, and the lookup runs on until the system's resolver gives up, counted nowhere. The docs
    // now say so (the coordinator's call): such lookups are
    // outside the D-4 count, and since each worker starts at most one push per PushTimeout, at most about
    // PushConcurrency × ceil(resolver timeout ÷ PushTimeout) run at once. This pins that bound, on the
    // manual clock: six recipients whose lookups each end 2.5 × PushTimeout after they start, one worker;
    // at most ceil(2.5) = 3 run at once (six were abandoned in all), while at most one operation is ever
    // unsettled. Each lookup ends in a timer callback that runs inside the clock's Advance, so the count
    // drops before the next deadline fires (deterministic).
    [Fact]
    public async Task ADV_110_3a_2_Lookups_abandoned_at_the_push_deadline_are_outside_the_unsettled_count_and_at_most_PushConcurrency_times_ceil_resolver_timeout_over_PushTimeout_run_at_once()
    {
        const int Recipients = 6;
        var resolverTimeout = TimeSpan.FromTicks(PushTimeout.Ticks * 5 / 2);
        var started = new SemaphoreSlim(0);
        var outstanding = 0;
        var maxOutstanding = 0;
        TimeProvider? clock = null;
        var policy = new OutboundEndpointPolicy
        {
            ResolveDnsAsync = (_, _) =>
            {
                var now = Interlocked.Increment(ref outstanding);
                int seen;
                while ((seen = Volatile.Read(ref maxOutstanding)) < now && Interlocked.CompareExchange(ref maxOutstanding, now, seen) != seen)
                {
                }
                // Ignores its token; gives up when the system's resolver would, on the push workers' clock.
                var gaveUp = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                ITimer? timer = null;
                timer = clock!.CreateTimer(_ =>
                {
                    Interlocked.Decrement(ref outstanding);
                    gaveUp.TrySetException(new SocketException((int)SocketError.TryAgain));
                    timer?.Dispose();
                }, null, resolverTimeout, Timeout.InfiniteTimeSpan);
                started.Release();
                return gaveUp.Task;
            },
        };
        await using var container = Container(o => o.AllowedSchemes = new[] { "http", "https" },
            more: s => s.Configure<DidCommOptions>(o => o.OutboundEndpointPolicy = policy));
        var sender = DirectPushSender.Create(container);
        var endpoints = new DirectEndpoints();
        await using var world = await LivePushWorld.StartAsync(o => o.PushConcurrency = 1, s =>
            DirectPushRouting.Register(s, new DirectPushRoute(endpoints, new Lazy<IDirectPushSender>(() => sender))));
        var fixture = world.Fixture;
        clock = fixture.Time;
        var grants = new List<LivePushGrant>();
        for (var i = 0; i < Recipients; i++)
        {
            var grant = await world.GrantAsync($"adv3a-abandon-{i}");
            endpoints.Set(grant.Peer, new Uri($"http://inbox-{i}.stall.recipient.example/didcomm"));
            await world.StoreAsync(grant, (byte)i);
            grants.Add(grant);
        }

        var mark = fixture.Time.Mark();
        foreach (var grant in grants)
            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        var maxUnsettled = 0;
        for (var push = 0; push < Recipients; push++)
        {
            (await started.WaitAsync(Wait)).Should().BeTrue("push {0}: the check looks the name up", push);
            await fixture.Time.WaitForTimerAsync(PushTimeout, mark, Wait); // this push's deadline
            maxUnsettled = Math.Max(maxUnsettled, fixture.Dispatcher.Unsettled);
            mark = fixture.Time.Mark();
            fixture.Time.Advance(PushTimeout); // the deadline (and any lookup that gives up before it)
            await fixture.WaitForPushOutcomesAsync(push + 1, Wait);
        }
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "every push settled at its deadline");

        fixture.PushOutcomeLog.Should().HaveCount(Recipients).And.OnlyContain(o => o == PushOutcomes.Failed, "precondition: each lookup reached the deadline");
        var bound = 1 * (int)Math.Ceiling(resolverTimeout / PushTimeout);
        Volatile.Read(ref maxOutstanding).Should().Be(bound, "at most PushConcurrency × ceil(resolver timeout ÷ PushTimeout) abandoned lookups run at once ({0} were abandoned in all)", Recipients);
        maxUnsettled.Should().BeLessThanOrEqualTo(1, "the operations settle at their deadlines: the lookups they abandoned are outside the D-4 count");

        // Once the resolver has given up on every one, none is left.
        fixture.Time.Advance(resolverTimeout);
        Volatile.Read(ref outstanding).Should().Be(0);
    }

    // ---- the yield rule (fix for ADV-110-2b-1) --------------------------------------------------------------

    // ADV-110-3a-3 (Low). With a store that loses the push mark, the docs said a 4xx-refused record "is
    // sent up to MaxDeliveryBatch times per wake-up". Since a run that delivered anything yields, a
    // delivery ahead of the refused record buys it a second run: the first run delivers and then re-sends
    // the refused record (it comes straight back without its mark) MaxDeliveryBatch − 1 times, yields,
    // and the second run sends it MaxDeliveryBatch times more and, having delivered nothing, does not
    // yield. The docs now say 2 × MaxDeliveryBatch − 1 (the coordinator's call); this pins it, at batch 1
    // and 10 (the default). Not a loop.
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task ADV_110_3a_3_With_a_store_that_drops_the_mark_a_refused_record_is_sent_at_most_twice_MaxDeliveryBatch_less_one_times_per_wake_up(int batch)
    {
        var store = new MarkDroppingStore(new InMemoryMessageStore());
        await using var direct = await DirectWorld.StartAsync(o => o.Pickup.MaxDeliveryBatch = batch, s => s.AddSingleton<IMessageStore>(store));
        var fixture = direct.Fixture;
        fixture.Dispatcher.PerRun.Should().Be(batch);
        var grant = await direct.GrantAsync("adv3a-mark-dropping-yield-" + batch);
        const byte Delivered = 1, Refused = 2;
        direct.Sends.Refuse(Refused);
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, Delivered));
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, Refused));

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(2 * batch, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the wake-up's runs ended");

        var refusedPosts = direct.Sends.Seeds.Count(s => s == Refused);
        refusedPosts.Should().Be(2 * batch - 1, "IMessageStore: a refused record is sent up to 2 × MaxDeliveryBatch − 1 ({0}) times per wake-up, a delivery ahead of it included", 2 * batch - 1);
        direct.Sends.Seeds.Count(s => s == Delivered).Should().Be(1);
        fixture.PushOutcomeLog.Should().HaveCount(2 * batch, "and then the wake-up ends: no loop");
    }

    // A conforming store, a grant that alternates a delivery and a refusal forever (the attacker's endpoint
    // refuses every other message), next to a healthy grant, PushConcurrency 1: each refusal-and-delivery
    // run yields, so the alternating grant takes its turn like any busy grant; the healthy grant's single
    // message goes in the first turn after the alternating grant's first run, and every record is POSTed
    // once. (Could not break: Q-4 holds under the new yield rule.)
    [Fact]
    public async Task A_grant_that_alternates_deliveries_and_refusals_yields_in_turn_and_each_record_is_posted_once()
    {
        await using var direct = await DirectWorld.StartAsync(o => o.PushConcurrency = 1);
        var fixture = direct.Fixture;
        var batch = fixture.Dispatcher.PerRun;
        var alternating = await direct.GrantAsync("adv3a-alternating");
        var healthy = await direct.GrantAsync("adv3a-healthy");
        const int Messages = 60;
        const byte First = 10, HealthySeed = 200;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        direct.Sends.OnAnswer = async (_, payload, _) =>
        {
            var seed = StandInJwe.SeedOf(payload.Span);
            if (seed == First)
            {
                entered.TrySetResult();
                await gate.Task; // the healthy grant is woken while the alternating grant's first run is under way
            }
            return seed != HealthySeed && (seed - First) % 2 == 1 ? DirectSendResult.Refused : DirectSendResult.Delivered;
        };
        for (var i = 0; i < Messages; i++)
            await fixture.StoreAsync(alternating.Recipient, PushFixture.Payload(64, (byte)(First + i)));
        await fixture.StoreAsync(healthy.Recipient, PushFixture.Payload(64, HealthySeed));

        fixture.Dispatcher.Wake(alternating.Peer).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);
        fixture.Dispatcher.Wake(healthy.Peer).Should().BeTrue();
        gate.TrySetResult();
        await fixture.WaitForPushOutcomesAsync(Messages + 1, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        var seeds = direct.Sends.Seeds.ToList();
        seeds.GroupBy(s => s).Should().OnlyContain(g => g.Count() == 1, "each record is posted once");
        seeds.Should().HaveCount(Messages + 1);
        seeds.IndexOf(HealthySeed).Should().Be(batch, "the healthy grant goes right after the alternating grant's first run (grants in turn)");
    }

    // ---- live-on wakes any peer; the directory may be asked about strangers (2b-2, 2b-3) ---------------------

    // A DID with no grant turns live delivery on N times (fresh DIDs, each session gone before the worker
    // runs, as an HTTP request's session is). Measured: one directory call per live-on (N), 0 POSTs, one
    // Push-Skipped-NoEndpoint per live-on, and no dispatcher entry left when the directory answers null.
    // Linear in what the stranger sends (one authenticated message per call), no amplification, so no
    // bound beyond the inbound rate is needed for that answer. With a directory that answers "anything"
    // for a stranger (the contract allows it) and an answer the check refuses (a placeholder whose name
    // does not resolve), each stranger DID starts a direct pause and its entry (the DID) is kept for the
    // pause: N entries, bounded only by the stranger's inbound rate × the pause. (Could not break: pinned.)
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_on_by_strangers_asks_the_directory_once_each_sends_nothing_and_keeps_entries_only_for_refused_answers(bool directoryAnswersARefusedPlaceholder)
    {
        const int Strangers = 8;
        var refusing = new RefusingCheckSender();
        var endpoints = new DirectEndpoints();
        await using var world = await LivePushWorld.StartAsync(o => o.PushConcurrency = 1, s =>
            DirectPushRouting.Register(s, new DirectPushRoute(endpoints, new Lazy<IDirectPushSender>(() => refusing))));
        var fixture = world.Fixture;

        // Park the only worker on an enrolled recipient's push, so every stranger's session is gone before
        // its wake-up runs.
        var parker = await world.GrantAsync("adv3a-parker");
        endpoints.Set(parker.Peer, DirectWorld.EndpointFor("parker"));
        await world.StoreAsync(parker, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        refusing.OnSend = async ct =>
        {
            entered.TrySetResult();
            await gate.Task.WaitAsync(ct);
        };
        fixture.Dispatcher.Wake(parker.Peer).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);
        if (directoryAnswersARefusedPlaceholder)
        {
            endpoints.OnFind = (did, _) => ValueTask.FromResult<Uri?>(did == parker.Peer ? DirectWorld.EndpointFor("parker") : new Uri("https://unregistered.invalid/inbox"));
        }

        var strangers = new List<string>();
        var registry = fixture.Services.GetRequiredService<IReturnRouteSessionRegistry>();
        for (var i = 0; i < Strangers; i++)
        {
            var peer = await world.Peers.NewAsync();
            strangers.Add(peer.Did);
            var session = new LiveTestSession(peer.Did, world.Mediator.Did);
            await TurnLiveOnAsync(fixture, session);
            registry.Unregister(session); // gone before the wake-up runs
        }
        gate.TrySetResult();
        await fixture.WaitForPushOutcomesAsync(1 + Strangers, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "every operation settled");

        endpoints.Asked.Count(strangers.Contains).Should().Be(Strangers, "one directory call per live-on of a DID with no grant");
        refusing.Sent.Should().Be(1, "nothing is sent for a DID with no grant (the parker's one send only)");
        if (directoryAnswersARefusedPlaceholder)
        {
            fixture.PushOutcomeLog.Skip(1).Should().Equal(Enumerable.Repeat(PushOutcomes.Failed, Strangers), "a refused endpoint counts Push-Failed and pauses");
            fixture.Dispatcher.GrantEntries.Should().Be(Strangers, "each stranger's DID is kept for its pause");
            fixture.Dispatcher.RetainedCharacters.Should().Be(strangers.Sum(d => (long)d.Length));
            fixture.Time.Advance(Pause);
            var healthy = await world.GrantAsync("adv3a-after-pause");
            endpoints.Set(healthy.Peer, DirectWorld.EndpointFor("after"));
            endpoints.OnFind = null;
            refusing.OnSend = null;
            await world.StoreAsync(healthy, 2);
            fixture.Dispatcher.Wake(healthy.Peer).Should().BeTrue();
            await fixture.WaitForPushOutcomesAsync(2 + Strangers, Wait);
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
            fixture.Dispatcher.GrantEntries.Should().Be(0, "the spent pauses go at the next worker finish");
        }
        else
        {
            fixture.PushOutcomeLog.Skip(1).Should().Equal(Enumerable.Repeat(PushOutcomes.SkippedNoEndpoint, Strangers));
            fixture.Dispatcher.GrantEntries.Should().Be(0, "no entry is kept for a stranger the directory has no endpoint for");
        }
    }

    // ---- support ----------------------------------------------------------------------------------------------

    /// <summary>
    /// One push to <c>http://localhost:{port}/</c> whose check before the take got a public answer for the
    /// name (the lookup seam), with the host connection path <paramref name="row"/> names; returns the POSTs
    /// that reached the loopback.
    /// </summary>
    private static async Task<int> PushToReboundLocalhostAsync(string row)
    {
        await using var loopback = await LoopbackInbox.StartAsync();
        var lookups = 0;
        var policy = new OutboundEndpointPolicy
        {
            // The rebinding name's first answer: a public address (never connected to).
            ResolveDnsAsync = (_, _) =>
            {
                Interlocked.Increment(ref lookups);
                return Task.FromResult(new[] { IPAddress.Parse("93.184.215.14") });
            },
        };
        await using var container = Container(o => o.AllowedSchemes = new[] { "http", "https" }, more: s =>
        {
            s.Configure<DidCommOptions>(o => o.OutboundEndpointPolicy = policy);
            var named = s.AddHttpClient(HttpDidCommTransport.HttpClientName);
            if (row.StartsWith("a connect callback", StringComparison.Ordinal))
                named.UseSocketsHttpHandler((h, _) => h.ConnectCallback = PlainConnectAsync);
            else if (row.StartsWith("a primary handler", StringComparison.Ordinal))
                named.ConfigurePrimaryHttpMessageHandler(() => new HostOwnHandler());
        });
        var sender = DirectPushSender.Create(container);
        var endpoints = new DirectEndpoints();
        await using var world = await LivePushWorld.StartAsync(null, s =>
            DirectPushRouting.Register(s, new DirectPushRoute(endpoints, new Lazy<IDirectPushSender>(() => sender))));
        var fixture = world.Fixture;
        var grant = await world.GrantAsync("adv3a-rebind");
        endpoints.Set(grant.Peer, new Uri($"http://localhost:{loopback.Port}/didcomm"));
        await world.StoreAsync(grant, 1);

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(1, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the push settled");

        // One lookup per push: the one that took the record, and (after a 2xx) the next, which found nothing.
        Volatile.Read(ref lookups).Should().BeGreaterThanOrEqualTo(1, "precondition: the check before the take looked the name up and got the public answer");
        return loopback.Posts;
    }

    private static async Task TurnLiveOnAsync(PushFixture fixture, IReturnRouteSession session)
    {
        fixture.Services.GetRequiredService<IReturnRouteSessionRegistry>().Register(session);
        var client = fixture.Services.GetRequiredService<DidCommClient>();
        var change = MessagePickup.CreateLiveDeliveryChange(session.PeerDid, session.LocalDid, liveDelivery: true);
        var packed = await client.PackEncryptedAsync(change, new PackEncryptedOptions(Recipients: new[] { session.LocalDid }, From: session.PeerDid));
        await fixture.Services.GetRequiredService<ProtocolDispatcher>().DispatchAsync(await client.UnpackAsync(packed.Message), client, fixture.Options, Task.CompletedTask, session, CancellationToken.None);
    }

    /// <summary>A sender whose check refuses every endpoint but the parker's (as the real check refuses a name that does not resolve); counts its sends.</summary>
    private sealed class RefusingCheckSender : IDirectPushSender
    {
        private int _sent;

        public int Sent => Volatile.Read(ref _sent);

        public Func<CancellationToken, Task>? OnSend { get; set; }

        public ValueTask<PushTargetCheck> CheckTargetAsync(Uri? endpoint, CancellationToken ct)
            => ValueTask.FromResult(endpoint is null
                ? new PushTargetCheck(PushTarget.NoEndpoint, DirectPushSender.ReasonNoEndpoint)
                : endpoint.Host.EndsWith(".invalid", StringComparison.Ordinal)
                    ? new PushTargetCheck(PushTarget.Refused, DirectPushSender.ReasonGuard)
                    : new PushTargetCheck(PushTarget.Accepted, string.Empty));

        public async Task<DirectSendResult> SendAsync(Uri endpoint, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Interlocked.Increment(ref _sent);
            if (OnSend is { } hook)
                await hook(ct);
            return DirectSendResult.Delivered;
        }
    }

    /// <summary>A host's own connect callback (for socket options, say): it connects where it is asked, with no address check.</summary>
    private static async ValueTask<Stream> PlainConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>A host's own primary handler that is neither an <see cref="HttpClientHandler"/> nor a <see cref="SocketsHttpHandler"/>; it follows no redirect and uses no proxy.</summary>
    private sealed class HostOwnHandler : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _inner = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _inner.SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>A plain HTTP/1.1 server on the IPv4 loopback that answers every request 202 and counts the POSTs.</summary>
    private sealed class LoopbackInbox : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;
        private int _posts;

        private LoopbackInbox(TcpListener listener)
        {
            _listener = listener;
            _serving = Task.Run(ServeAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Posts => Volatile.Read(ref _posts);

        public static Task<LoopbackInbox> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new LoopbackInbox(listener));
        }

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read >= 4 && System.Text.Encoding.ASCII.GetString(buffer, 0, 4) == "POST")
                        Interlocked.Increment(ref _posts);
                    await stream.WriteAsync("HTTP/1.1 202 Accepted\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
            {
                // Stopped.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await Task.WhenAny(_serving, Task.Delay(Wait));
            _stop.Dispose();
        }
    }

    /// <summary>A container the way a mediator host builds one: <c>AddDidComm</c> with <c>UseHttpTransport</c>, then <paramref name="more"/>.</summary>
    private static ServiceProvider Container(Action<HttpTransportOptions> http, Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.UseHttpTransport(http);
        });
        more?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
