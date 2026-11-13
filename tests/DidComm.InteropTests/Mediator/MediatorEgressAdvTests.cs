using System.Collections.Concurrent;
using System.Net;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator;
using DidComm.Mediator.Pipeline;
using DidComm.Protocols.Routing;
using DidComm.Transports.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// ADV-2 (#95): the mediator's egress and its single forward pump attacked over the real endpoints
/// (TestServer, the real HTTP transport, the real did:peer resolver). The HTTP transport here runs
/// with the library's default retry and circuit-breaker settings, which the #95 acceptance suites
/// turn off (<see cref="MediatorTestServer.ConfigureHttp"/>); only the retry back-off is zero so no
/// test sleeps (the back-off changes when the attempts are made, not how many are made within the
/// 30 s push deadline: 1 + 2 + 4 s with ±50 % jitter is at most 10.5 s).
/// </summary>
public sealed class MediatorEgressAdvTests
{
    private static readonly TimeSpan Wait = MediatorTestServer.Timeout;

    // ---- the single forward pump ------------------------------------------------------------

    // ADV-2 DEFECT 1 (P2, fixed by #95 AD-1): a stranger's forward addressed to the mediator itself
    // used to be opened on the single forward pump, after the 202, so an attacker-chosen DID
    // resolution held up every other relay. Layers addressed to the mediator are now opened inline,
    // on the sender's own request: the stranger's POST is what waits for the DID it chose.
    [Fact]
    public async Task A_strangers_forward_addressed_to_the_mediator_cannot_hold_up_other_senders_relays()
    {
        var resolver = new GatedDidResolverHandle();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.PostConfigureServices = s => GatedDidResolverHandle.Install(s, resolver));
        Task<(HttpStatusCode Status, string? ContentType, string Body)>? strangersPost = null;
        try
        {
            var bob = await mediator.NewPeerAsync();
            await mediator.EnrolAsync(bob);
            var alice = await mediator.NewSenderAsync();
            var stranger = await mediator.NewSenderAsync(); // never enrolled, never registered

            // The stranger's DID stands for one whose resolution is slow (a did:web / did:webvh host
            // that answers after the resolver's own timeout, or never): its resolution parks here.
            resolver.Gate(stranger.Did);
            var signed = await stranger.Client.PackSignedAsync(stranger.Note(mediator.Did), stranger.Did);
            var (selfAddressed, _) = await stranger.ForwardAsync(mediator.Did, next: mediator.Did, signed);
            strangersPost = Task.Run(() => mediator.PostAsync(selfAddressed)); // its own request thread: the signer lookup blocks it
            await resolver.Entered.WaitAsync(Wait);

            // An ordinary relay from someone else, for an enrolled recipient.
            var note = alice.Note(bob.Did);
            var (forward, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(note, bob.Did));
            (await mediator.PostAsync(forward)).Status.Should().Be(HttpStatusCode.Accepted);

            // Property: Bob's message is queued whatever the stranger's forward is waiting for.
            await MediatorTestServer.WaitUntilAsync(() => mediator.Store.MessageCount == 1, "a stranger's forward must not hold every other relay behind a DID resolution it chose");
            strangersPost.IsCompleted.Should().BeFalse("the stranger's own request pays for opening what it sent");
        }
        finally
        {
            resolver.Release();
        }

        (await strangersPost!.WaitAsync(Wait)).Status.Should().Be(HttpStatusCode.Accepted, "once its DID resolves, the stranger still gets the uniform 202 and nothing more");
        await mediator.FlushAsync();
        mediator.Store.MessageCount.Should().Be(1, "the stranger's forward stored nothing");
    }

    [Fact]
    public async Task A_strangers_self_addressed_forward_never_stores_anything_and_ends_with_one_outcome()
    {
        // What does hold: with a resolver that answers, the signed message inside is opened, found
        // not to be a forward, and dropped; one outcome, nothing stored, nothing pushed.
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var stranger = await mediator.NewSenderAsync();

        var signed = await stranger.Client.PackSignedAsync(stranger.Note(mediator.Did), stranger.Did);
        var (selfAddressed, _) = await stranger.ForwardAsync(mediator.Did, next: mediator.Did, signed);
        (await mediator.PostAsync(selfAddressed)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedMalformed);
        mediator.Store.MessageCount.Should().Be(0);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    // ---- direct push through the real HTTP transport ----------------------------------------

    [Fact(Skip = "ADV-2 DEFECT 2 (P3): 'one attempt per message' (PRD FR-PICK-09, MediatorOptions.DirectPush, docs/codebase-architecture.md) is false with the HTTP transport's defaults: the transport's Polly retry re-sends a push answered 5xx/408 MaxRetryAttempts (3) more times, 4 POSTs per message; with 307 hops (MaxRedirectHops 5, each hop retried) up to 24. main has no direct push.")]
    public async Task One_accepted_message_costs_its_recipients_endpoint_one_request_with_the_default_transport()
    {
        await using var fixture = await EgressFixture.StartAsync();
        var (mediator, inbox, alice) = (fixture.Mediator, fixture.Inbox, fixture.Alice);
        var bob = await fixture.EnrolWithEndpointAsync("/bob");
        inbox.Answer = (_, _) => (StatusCodes.Status503ServiceUnavailable, null);

        await alice.SendViaMediatorAsync(mediator, bob);
        await mediator.FlushAsync();
        await mediator.PushesIdleAsync();

        mediator.PushOutcomes.Should().Equal(PushOutcomes.Failed);
        mediator.Store.MessageCount.Should().Be(1, "a failed push leaves the message queued");
        inbox.Count("/bob").Should().Be(1, "the documented contract is one push attempt per message");
    }

    [Fact(Skip = "ADV-2 DEFECT 3 (P2): the HTTP transport has one circuit breaker for every endpoint, so an enrolled peer whose own push endpoint answers 5xx (2 messages with the default retries, 5 without) opens it for 30 s and every other recipient's push fails without a request (Push-Failed, BrokenCircuitException), as does every other HTTP send of the host; the peer can keep it open by sending itself a message every 30 s. main has no direct push (no remote party drives egress).")]
    public async Task A_failing_recipient_endpoint_does_not_stop_pushes_to_other_recipients()
    {
        await using var fixture = await EgressFixture.StartAsync();
        var (mediator, inbox, alice) = (fixture.Mediator, fixture.Inbox, fixture.Alice);
        var mallory = await fixture.EnrolWithEndpointAsync("/mallory");
        var bob = await fixture.EnrolWithEndpointAsync("/bob");
        inbox.Answer = (path, _) => path == "/mallory" ? (StatusCodes.Status503ServiceUnavailable, null) : (StatusCodes.Status202Accepted, null);

        // Mallory sends herself a few messages through the mediator (anyone could send them to her).
        for (var i = 0; i < 5; i++)
            await alice.SendViaMediatorAsync(mediator, mallory);
        await mediator.FlushAsync();
        await mediator.PushesIdleAsync();
        var malloryRequests = inbox.Count("/mallory");

        await alice.SendViaMediatorAsync(mediator, bob);
        await mediator.FlushAsync();
        await mediator.PushesIdleAsync();

        malloryRequests.Should().BeGreaterThan(0);
        inbox.Count("/bob").Should().Be(1, "Bob's endpoint is healthy; Mallory's failures must not decide whether Bob gets pushes");
        mediator.PushOutcome(PushOutcomes.Pushed).Should().Be(1);
    }

    [Fact(Skip = "ADV-2 DEFECT 4 (P3): a 307 from the directory's endpoint makes the mediator POST the stored payload to a host the directory never named (any public URL in Location, re-pinned by the SSRF guard), count it Pushed and remove the queued copy; IRecipientEndpointDirectory says the endpoint 'always comes from here'. main has no direct push.")]
    public async Task A_push_reaches_only_the_endpoint_the_directory_named()
    {
        await using var fixture = await EgressFixture.StartAsync();
        var (mediator, inbox, alice) = (fixture.Mediator, fixture.Inbox, fixture.Alice);
        await using var elsewhere = await AdvInbox.StartAsync(mediator.Network, "m2.test");
        var bob = await fixture.EnrolWithEndpointAsync("/bob");
        inbox.Answer = (_, _) => (StatusCodes.Status307TemporaryRedirect, "http://m2.test/not-in-the-directory");

        await alice.SendViaMediatorAsync(mediator, bob);
        await mediator.FlushAsync();
        await mediator.PushesIdleAsync();

        inbox.Count("/bob").Should().Be(1);
        elsewhere.Received.Should().BeEmpty("only the directory decides where a recipient's messages are pushed");
        mediator.Store.MessageCount.Should().Be(1, "a push that did not reach the directory's endpoint is not a delivery");
    }

    [Fact(Skip = "ADV-2 DEFECT 6 (P3): HttpDidCommTransport logs the 2xx at Debug outside any guard (HttpDidCommTransport.cs:117; main:107 the same), so a logging provider that throws turns every delivered push into Push-Failed: the lease is given back and the message, already delivered, is delivered again by pickup. Every other mediator log call is guarded (MediatorLog.Write, SafeLog). main has no direct push.")]
    public async Task A_throwing_logging_provider_does_not_turn_a_delivered_push_into_a_failure_and_a_second_delivery()
    {
        await using var fixture = await EgressFixture.StartAsync(builder => builder.Logging.AddProvider(new ThrowingLoggerProvider(typeof(HttpDidCommTransport).FullName!)));
        var (mediator, inbox, alice) = (fixture.Mediator, fixture.Inbox, fixture.Alice);
        var bob = await fixture.EnrolWithEndpointAsync("/bob");

        await alice.SendViaMediatorAsync(mediator, bob);
        await mediator.FlushAsync();
        await mediator.PushesIdleAsync();

        inbox.Count("/bob").Should().Be(1);
        mediator.PushOutcomes.Should().Equal(PushOutcomes.Pushed);
        mediator.Store.MessageCount.Should().Be(0, "the endpoint accepted the push; no second copy waits for pickup");
    }

    [Fact]
    public async Task A_sender_can_hide_a_recipients_full_quota_for_at_most_the_delay_cap()
    {
        // FR-PICK-10 / FR-ROUTE-10 (owner-accepted residual, bounded): delayed messages count against
        // the quota unseen, for at most MaxForwardDelay; a hint past the cap, or the most negative
        // one, never holds longer.
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.Pickup.MaxQueueMessages = 3);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var stranger = await mediator.NewSenderAsync();
        var alice = await mediator.NewSenderAsync();

        foreach (var hint in new[] { long.MaxValue, 3_600_001L, long.MinValue })
        {
            var (packed, _) = await stranger.ForwardAsync(mediator.Did, bob.Did, await stranger.AuthcryptAsync(stranger.Note(bob.Did), bob.Did),
                forward => (forward.AdditionalHeaders ??= new Dictionary<string, System.Text.Json.JsonElement>())["delay_milli"] = System.Text.Json.JsonSerializer.SerializeToElement(hint));
            (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        }
        await mediator.FlushAsync();
        (await mediator.StatusAsync(bob)).MessageCount.Should().BeInRange(0, 1, "only the random draw of the negative hint can already be due");
        mediator.Store.MessageCount.Should().Be(3, "the quota is full");

        await alice.SendViaMediatorAsync(mediator, bob.Did);
        await mediator.FlushAsync();
        mediator.Outcome(ForwardOutcomes.DroppedQuota).Should().Be(1, "while the quota is held, Bob's other mail is refused");

        mediator.Clock.Advance(TimeSpan.FromHours(1));
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(3, "at the cap every held message is due and visible");
        (await mediator.FetchAsync(bob)).Should().HaveCount(3);
    }

    [Fact]
    public async Task A_replayed_captured_forward_is_bounded_by_the_recipients_quota_when_nothing_is_pushed()
    {
        // FR-RR-08 exempts forward from replay admission (documented, owner-accepted): the number of
        // copies a captured forward can park is the grant's quota, no more.
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.Pickup.MaxQueueMessages = 5);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var (captured, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did));

        for (var i = 0; i < 8; i++)
            (await mediator.PostAsync(captured)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();

        mediator.Store.MessageCount.Should().Be(5);
        mediator.Outcome(ForwardOutcomes.Queued).Should().Be(5);
        mediator.Outcome(ForwardOutcomes.DroppedQuota).Should().Be(3);
    }

    // ---- fixture -----------------------------------------------------------------------------

    /// <summary>A mediator with direct push on, the HTTP transport at its defaults (no back-off), a scripted inbox on inbox.test and a directory.</summary>
    private sealed class EgressFixture : IAsyncDisposable
    {
        public required MediatorTestServer Mediator { get; init; }
        public required AdvInbox Inbox { get; init; }
        public required TestEndpointDirectory Directory { get; init; }
        public required MediatorSender Alice { get; init; }
        public static async Task<EgressFixture> StartAsync(Action<WebApplicationBuilder>? builder = null)
        {
            var network = new MediatorNetwork();
            var inbox = await AdvInbox.StartAsync(network, InboxServer.Host);
            var directory = new TestEndpointDirectory();
            var server = await MediatorTestServer.StartAsync(o =>
            {
                o.Network = network;
                o.Mediator = m => m.DirectPush = true;
                o.ConfigureServices = services => services.AddSingleton<IRecipientEndpointDirectory>(directory);
                o.ConfigureBuilder = builder;
                o.PostConfigureServices = services => services.Configure<HttpTransportOptions>(http =>
                {
                    var defaults = new HttpTransportOptions();
                    http.MaxRetryAttempts = defaults.MaxRetryAttempts;
                    http.CircuitBreakerFailureThreshold = defaults.CircuitBreakerFailureThreshold;
                    http.CircuitBreakerOpenDuration = defaults.CircuitBreakerOpenDuration;
                    http.MaxRedirectHops = defaults.MaxRedirectHops;
                    http.RetryBaseDelay = TimeSpan.Zero;
                });
            });
            var alice = await server.NewSenderAsync();
            return new EgressFixture { Mediator = server, Inbox = inbox, Directory = directory, Alice = alice };
        }

        /// <summary>A peer enrolled with its own DID as recipient, whose push endpoint is <c>http://inbox.test{path}</c>.</summary>
        public async Task<string> EnrolWithEndpointAsync(string path)
        {
            var peer = await Mediator.NewPeerAsync();
            await Mediator.EnrolAsync(peer);
            Directory.Map(peer.Did, Inbox.Endpoint(path));
            return peer.Did;
        }

        public async ValueTask DisposeAsync()
        {
            await Mediator.DisposeAsync();
            await Inbox.DisposeAsync();
        }
    }
}

internal static class AdvSenderExtensions
{
    /// <summary>A note from <paramref name="sender"/> to <paramref name="recipientDid"/>, authcrypted, wrapped in a forward to the mediator and posted.</summary>
    public static async Task SendViaMediatorAsync(this MediatorSender sender, MediatorTestServer mediator, string recipientDid)
    {
        var (forward, _) = await sender.ForwardAsync(mediator.Did, recipientDid, await sender.AuthcryptAsync(sender.Note(recipientDid), recipientDid));
        (await mediator.PostAsync(forward)).Status.Should().Be(HttpStatusCode.Accepted);
    }
}

/// <summary>A logging provider whose loggers for one category throw on every write (a broken host sink).</summary>
internal sealed class ThrowingLoggerProvider(string category) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Throwing(categoryName == category);

    public void Dispose()
    {
    }

    private sealed class Throwing(bool throws) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (throws)
                throw new InvalidOperationException("ADV-2: the host's log sink is broken.");
        }
    }
}

/// <summary>An HTTP endpoint on the test network that answers each POST as scripted (status and an optional Location) and counts requests per path.</summary>
internal sealed class AdvInbox : IAsyncDisposable
{
    private readonly ConcurrentQueue<(string Path, byte[] Body)> _received = new();
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    private AdvInbox(WebApplication app, string host)
    {
        App = app;
        Host = host;
    }

    public WebApplication App { get; }

    public string Host { get; }

    /// <summary>(path, nth request to that path) → (status, Location or null). Default: 202.</summary>
    public Func<string, int, (int Status, string? Location)> Answer { get; set; } = (_, _) => (StatusCodes.Status202Accepted, null);

    public IReadOnlyCollection<(string Path, byte[] Body)> Received => _received;

    public int Count(string path) => _counts.GetValueOrDefault(path);

    public Uri Endpoint(string path) => new($"http://{Host}{path}");

    public static async Task<AdvInbox> StartAsync(MediatorNetwork network, string host)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(o => o.BaseAddress = new Uri($"http://{host}/"));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        AdvInbox? inbox = null;
        app.MapPost("/{**path}", async (HttpContext context) =>
        {
            using var ms = new MemoryStream();
            await context.Request.Body.CopyToAsync(ms);
            var path = context.Request.Path.Value ?? string.Empty;
            var n = inbox!._counts.AddOrUpdate(path, 1, static (_, c) => c + 1);
            inbox._received.Enqueue((path, ms.ToArray()));
            var (status, location) = inbox.Answer(path, n);
            if (location is not null)
                context.Response.Headers.Location = location;
            return Results.StatusCode(status);
        });
        inbox = new AdvInbox(app, host);
        await app.StartAsync();
        network.Add(app.GetTestServer());
        return inbox;
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

/// <summary>
/// Wraps the container's net-did <see cref="IDidResolver"/>: the resolution of one chosen DID parks
/// until released (a networked DID method whose host is slow); everything else passes through.
/// </summary>
internal sealed class GatedDidResolverHandle
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile string? _gated;
    private int _calls;

    public Task Entered => _entered.Task;

    /// <summary>How many resolutions of the gated DID started.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>The token the first gated resolution was handed.</summary>
    public CancellationToken FirstToken { get; private set; }

    public void Gate(string did) => _gated = did;

    public void Release() => _release.TrySetResult();

    /// <summary>Replace the registered <see cref="IDidResolver"/> with a gated wrapper around it.</summary>
    public static void Install(IServiceCollection services, GatedDidResolverHandle handle)
    {
        var original = services.Last(d => d.ServiceType == typeof(IDidResolver) && !d.IsKeyedService);
        services.Remove(original);
        services.AddSingleton<IDidResolver>(sp => new Gated(Build(original, sp), handle));
    }

    private static IDidResolver Build(ServiceDescriptor descriptor, IServiceProvider sp)
        => descriptor.ImplementationInstance as IDidResolver
           ?? (descriptor.ImplementationFactory is { } factory
               ? (IDidResolver)factory(sp)
               : (IDidResolver)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!));

    private sealed class Gated(IDidResolver inner, GatedDidResolverHandle handle) : IDidResolver
    {
        public bool CanResolve(string did) => inner.CanResolve(did);

        public async Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
        {
            if (string.Equals(did, handle._gated, StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref handle._calls) == 1)
                    handle.FirstToken = ct;
                handle._entered.TrySetResult();
                await handle._release.Task.ConfigureAwait(false);
            }
            return await inner.ResolveAsync(did, options, ct).ConfigureAwait(false);
        }
    }
}
