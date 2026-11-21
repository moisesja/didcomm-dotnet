using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// How the 4.0 client matches answers to its requests (#100 R-1; DIDComm v2.1 threading.md L9: ids
/// are compared ignoring case; L17: every later message of a thread carries its <c>thid</c>). The
/// mediator is the real <c>AddMediator()</c> host, with its real <see cref="MessagePickup40Handler"/>
/// behind <see cref="RewritingPickup40Handler"/>, which writes each answer's thread its own way; the
/// client is the real one over its real channel, each request id taken from <see cref="LetterIds"/>
/// (a random UUID v4 may hold no letter, and re-casing it would change nothing). An answer whose
/// <c>thid</c> is the request id upper-cased completes the request over HTTP and over a WebSocket; a
/// 4.0 answer without <c>thid</c> completes nothing (owner answer "2", #98).
/// </summary>
public sealed class PickupV4ThreadCaseEndToEndTests
{
    public static TheoryData<bool> Transports => new() { false, true };

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Answers_whose_thid_is_the_request_id_upper_cased_complete_status_fetch_and_acknowledgement(bool webSocket)
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.PostConfigureServices = RewritingPickup40Handler.Register(thid => thid?.ToUpperInvariant()));
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var note = await PickupE2E.SendAsync(mediator, alice, recipient, "re-cased");
        await PickupE2E.PostUnopenableAsync(mediator, alice, recipient);
        var pickup = WithLetterIds(harness);
        var over = webSocket ? harness.WebSocketEndpoint : null;
        var rewriter = mediator.App.Services.GetServices<IProtocolHandler>().OfType<RewritingPickup40Handler>().Single();

        var status = await PickupE2E.Within(pickup.GetStatusAsync(mediator.Did, bob.Did, serviceEndpointOverride: over));
        var batch = await PickupE2E.Within(pickup.FetchAsync(mediator.Did, bob.Did, limit: 10, serviceEndpointOverride: over));
        var failed = batch.Items.Single(i => i.Error is not null);
        var afterAck = await PickupE2E.Within(pickup.AckAsync(mediator.Did, bob.Did, new[] { failed.Id }, over));

        rewriter.Rewritten.Should().HaveCount(3, "status-request, delivery-request and the status-request after the acknowledgement were answered");
        rewriter.Rewritten.Should().OnlyContain(r => r.Written != r.Original && string.Equals(r.Written, r.Original, StringComparison.OrdinalIgnoreCase),
            "precondition: every answer's thread is its request's with the letters re-cased");
        rewriter.Rewritten.Select(r => r.Original).Should().OnlyContain(id => id.StartsWith("request-abcdef-", StringComparison.Ordinal), "precondition: the request ids are the pinned ones");
        status.MessageCount.Should().Be(2);
        PickupE2E.ShouldBeAlicesNote(batch.Items.Single(i => i.Error is null), alice, recipient, note);
        afterAck.MessageCount.Should().Be(0);
        harness.Channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Over_HTTP_a_4_0_answer_without_thid_completes_nothing_and_nothing_is_acknowledged()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.PostConfigureServices = RewritingPickup40Handler.Register(_ => null));
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        await PickupE2E.SendAsync(mediator, alice, recipient, "thread-less");

        var status = await Record.ExceptionAsync(() => PickupE2E.Within(harness.Pickup.GetStatusAsync(mediator.Did, bob.Did)));
        var fetch = await Record.ExceptionAsync(() => PickupE2E.Within(harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10)));

        status.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("no answer in the HTTP response");
        fetch.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("no answer in the HTTP response");
        mediator.App.Services.GetServices<IProtocolHandler>().OfType<RewritingPickup40Handler>().Single().Rewritten.Should().HaveCount(2, "the mediator did answer both, without a thread");
        mediator.Store.MessageCount.Should().Be(1, "the delivery was not taken as the answer: nothing in it was acknowledged");
        harness.Channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Over_a_WebSocket_a_4_0_status_without_thid_completes_nothing_and_the_request_times_out()
    {
        var time = new QaManualTimeProvider();
        var clientSeen = new PickupWireObserver();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.PostConfigureServices = RewritingPickup40Handler.Register(_ => null));
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket: true, o =>
        {
            o.Time = time;
            o.ConfigureServices = s => s.AddSingleton<IProtocolObserver>(clientSeen);
        });
        var (bob, _) = await PickupE2E.EnrolAsync(mediator, harness);
        var rewriter = mediator.App.Services.GetServices<IProtocolHandler>().OfType<RewritingPickup40Handler>().Single();
        var timeout = harness.Channel.Options.RequestTimeout;

        var mark = time.Mark();
        var call = harness.Pickup.GetStatusAsync(mediator.Did, bob.Did, serviceEndpointOverride: harness.WebSocketEndpoint);
        await time.WaitForTimerAsync(mark, timeout, MediatorTestServer.Timeout);
        // The thread-less status reached the client and was handed to its dispatcher (the request's
        // matcher had looked at it first and passed): it completed nothing.
        await clientSeen.WaitForAsync(mediator.Did, r => r.Kind == "4.0/status" && r.Thid is null, "the thread-less status reached the client's dispatcher");
        rewriter.Rewritten.Should().ContainSingle().Which.Written.Should().BeNull("precondition: the mediator answered without a thread");
        call.IsCompleted.Should().BeFalse("a reply without thid is not part of the request's thread (v2.1 threading L17)");
        time.Advance(timeout);
        var thrown = await Record.ExceptionAsync(() => PickupE2E.Within(call));

        thrown.Should().BeOfType<TimeoutException>();
        harness.Channel.PendingCount.Should().Be(0);
    }

    private static PickupClient WithLetterIds(MediatorClientHarness harness)
        => new(new LetterIdExchange(harness.Channel, new LetterIds("request")), logger: null);
}

/// <summary>
/// A 4.0 mediator role that answers as this library's does — it is <see cref="MessagePickup40Handler"/>
/// — and then writes the thread of each answer its own way (<c>thid</c>, and <c>pthid</c> on a
/// problem report): as a peer that re-cases ids, or one that leaves the thread out. A
/// <c>delivery</c> is sent through the request's return-route session rather than returned, so the
/// session the handler sees is wrapped too, and a message sent through it with a thread gets the
/// same treatment. Registered after <c>AddMediator()</c>, so it is the handler the dispatcher picks
/// for the 4.0 protocol. It records each answer's thread as the handler wrote it (the request's
/// thread) and as it was sent.
/// </summary>
internal sealed class RewritingPickup40Handler(MessagePickup40Handler inner, Func<string?, string?> threadOf) : IProtocolHandler, IProtocolHandlerRoles
{
    private readonly object _gate = new();
    private readonly List<(string Original, string? Written)> _rewritten = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReturnRouteSession, RewritingSession> _sessions = new();

    public string ProtocolUri => inner.ProtocolUri;

    public IReadOnlyList<string> Roles => inner.Roles;

    /// <summary>Each answer's thread as the handler wrote it, and as it was sent.</summary>
    public IReadOnlyList<(string Original, string? Written)> Rewritten
    {
        get
        {
            lock (_gate)
                return _rewritten.ToArray();
        }
    }

    /// <summary>A <c>PostConfigureServices</c> that registers the handler over the container's own 4.0 handler.</summary>
    public static Action<IServiceCollection> Register(Func<string?, string?> threadOf)
        => services => services.AddSingleton<IProtocolHandler>(sp => new RewritingPickup40Handler(sp.GetRequiredService<MessagePickup40Handler>(), threadOf));

    public async Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        // One wrapper per session, so the mediator's per-session state is keyed as without it.
        var wrapped = context.Session is { } session
            ? context with { Session = _sessions.GetValue(session, s => new RewritingSession(s, Rewrite)) }
            : context;
        var reply = await inner.HandleAsync(message, wrapped, ct).ConfigureAwait(false);
        if (reply is not null)
            Rewrite(reply);
        return reply;
    }

    private void Rewrite(Message answer)
    {
        string original;
        string? written;
        if (answer.Pthid is { } pthid)
            (original, written) = (pthid, answer.Pthid = threadOf(pthid));
        else if (answer.Thid is { } thid)
            (original, written) = (thid, answer.Thid = threadOf(thid));
        else
            return; // a live push answers no request
        lock (_gate)
            _rewritten.Add((original, written));
    }

    // The request's session, with the thread of what is sent through it rewritten.
    private sealed class RewritingSession(IReturnRouteSession inner, Action<Message> rewrite) : IReturnRouteSession
    {
        public string PeerDid => inner.PeerDid;
        public string LocalDid => inner.LocalDid;
        public ReturnRouteMode Mode => inner.Mode;
        public string? Thread => inner.Thread;
        public ReturnRouteTransport Transport => inner.Transport;
        public bool IsLive => inner.IsLive;
        public CancellationToken Closed => inner.Closed;

        public ValueTask<bool> TrySendAsync(Message message, CancellationToken ct)
        {
            rewrite(message);
            return inner.TrySendAsync(message, ct);
        }

        public ValueTask<ReturnRouteSendResult> TrySendAsync(Message message, int maxPackedBytes, CancellationToken ct)
        {
            rewrite(message);
            return inner.TrySendAsync(message, maxPackedBytes, ct);
        }
    }
}
