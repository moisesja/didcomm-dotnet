using System.Net;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Protocols.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// #98 FX-1, adversarial finding D3-F3. A forward whose 202 is not committed (a client that pipelines
/// requests and never reads them keeps Kestrel's response from completing) used to keep its
/// reservation in the forward handler's one global budget (1 024 forwards, 64 MiB) for as long as the
/// connection lived, so a self-enrolled stranger could fill the budget and every other tenant's forward
/// was <c>Dropped-Saturated</c>. Now the mediator's HTTP endpoint gives every 202 the forward handler
/// produced a deadline (<see cref="Deadline"/>, on the container's <see cref="TimeProvider"/>): one that
/// is not committed by then cancels the acknowledgement — the forward is dropped as
/// <c>Dropped-Unacknowledged</c> and its reservation released — and aborts the request, so the sender
/// never holds a 202 for it. A 202 committed before the deadline wins and is relayed once.
/// The responses are held at <c>OnStarting</c> (<see cref="MediatorRequestControls.ResponseGate"/>), the
/// TestServer stand-in for a client that keeps the response from completing; time moves only by hand
/// (<see cref="QaManualTimeProvider"/>), so each step waits for the exact record it then reads.
/// </summary>
public sealed class MediatorAcknowledgementDeadlineTests
{
    /// <summary>The endpoint's deadline for a forward's 202.</summary>
    internal static readonly TimeSpan Deadline = DidComm.AspNetCore.DidCommEndpointRouteBuilderExtensions.ForwardResponseDeadline;

    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    [Fact]
    public async Task A_stranger_holding_1024_uncommitted_202s_loses_every_reservation_at_the_deadline_and_another_tenants_forward_is_queued()
    {
        Qa.RaiseThreadPoolFloor();
        var time = new QaManualTimeProvider();
        var responses = new DeadlineResponseTracker(HttpMethods.Post);
        await using var mediator = await StartAsync(time, responses);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var mallory = await mediator.NewSenderAsync();
        var malloryDid = await mediator.NewPeerAsync(); // self-enrolled under the default open enrolment
        await mediator.EnrolAsync(malloryDid);
        var (aliceForward, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did));
        var (small, _) = await mallory.ForwardAsync(mediator.Did, malloryDid.Did, Qa.JsonPayloadOfSize(64));
        var startedBefore = responses.Started;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.Controls.ResponseGate = gate;
        try
        {
            var held = Enumerable.Range(0, ForwardHandler.DefaultCapacity).Select(_ => Task.Run(() => mediator.PostAsync(small))).ToList();
            // A response reaches OnStarting only after the endpoint returned, so its deadline is armed by then.
            await MediatorTestServer.WaitUntilAsync(() => responses.Started == startedBefore + ForwardHandler.DefaultCapacity, "every 202 is held unread");
            mediator.Handler.Outstanding.Should().Be(ForwardHandler.DefaultCapacity, "Mallory holds the count bound");

            time.Advance(Deadline - Tick);
            mediator.Handler.Outstanding.Should().Be(ForwardHandler.DefaultCapacity, "one tick before the deadline nothing is released");
            responses.Aborted.Should().Be(0);

            time.Advance(Tick);
            await MediatorTestServer.WaitUntilAsync(() => mediator.Handler.Outstanding == 0, "the deadline releases every reservation");
            await MediatorTestServer.WaitUntilAsync(() => responses.Aborted == ForwardHandler.DefaultCapacity, "the deadline aborts every held request");
            var failures = await Task.WhenAll(held.Select(t => Record.ExceptionAsync(() => t.WaitAsync(MediatorTestServer.Timeout))));

            mediator.Controls.ResponseGate = null;
            var aliceResponse = await mediator.PostAsync(aliceForward);
            await mediator.FlushAsync();

            failures.Should().AllSatisfy(f => f.Should().NotBeNull().And.NotBeOfType<TimeoutException>(), "a sender whose 202 was not committed never receives one");
            aliceResponse.Status.Should().Be(HttpStatusCode.Accepted);
            mediator.Outcome(ForwardOutcomes.DroppedUnacknowledged).Should().Be(ForwardHandler.DefaultCapacity);
            mediator.Outcome(ForwardOutcomes.DroppedSaturated).Should().Be(0);
            mediator.Outcome(ForwardOutcomes.Queued).Should().Be(1, "Alice's forward to Bob is relayed once the stranger's reservations are gone");
            (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1);
            mediator.LeakingLogLines().Should().BeEmpty();
        }
        finally
        {
            mediator.Controls.ResponseGate = null;
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_stranger_filling_the_64_MiB_budget_with_uncommitted_202s_denies_other_tenants_only_until_the_deadline()
    {
        Qa.RaiseThreadPoolFloor();
        var time = new QaManualTimeProvider();
        var responses = new DeadlineResponseTracker(HttpMethods.Post);
        await using var mediator = await StartAsync(time, responses);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var mallory = await mediator.NewSenderAsync();
        var malloryDid = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(malloryDid);
        var (aliceForward, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did));
        // The largest forward that still fits MaxReceiveBytes (1 MiB) once anoncrypted (base64url ~ 4/3).
        var (big, _) = await mallory.ForwardAsync(mediator.Did, malloryDid.Did, Qa.JsonPayloadOfSize(700 * 1024));
        var startedBefore = responses.Started;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.Controls.ResponseGate = gate;
        try
        {
            var held = new List<Task<(HttpStatusCode Status, string? ContentType, string Body)>>();
            var admitted = 0;
            while (mediator.Outcome(ForwardOutcomes.DroppedSaturated) == 0)
            {
                var before = mediator.Handler.Outstanding;
                held.Add(Task.Run(() => mediator.PostAsync(big)));
                await MediatorTestServer.WaitUntilAsync(
                    () => mediator.Handler.Outstanding == before + 1 || mediator.Outcome(ForwardOutcomes.DroppedSaturated) > 0,
                    "each held forward is admitted or dropped on the request");
                if (mediator.Outcome(ForwardOutcomes.DroppedSaturated) == 0)
                    admitted++;
                admitted.Should().BeLessThan(200);
            }
            var bytesHeld = mediator.Handler.OutstandingBytes;
            // Top up the remainder (less than one big forward) with small ones until one is refused too.
            var (small, _) = await mallory.ForwardAsync(mediator.Did, malloryDid.Did, Qa.JsonPayloadOfSize(64));
            var topUps = 0;
            while (mediator.Outcome(ForwardOutcomes.DroppedSaturated) == 1)
            {
                var before = mediator.Handler.Outstanding;
                held.Add(Task.Run(() => mediator.PostAsync(small)));
                await MediatorTestServer.WaitUntilAsync(
                    () => mediator.Handler.Outstanding == before + 1 || mediator.Outcome(ForwardOutcomes.DroppedSaturated) > 1,
                    "each held top-up is admitted or dropped on the request");
                if (mediator.Outcome(ForwardOutcomes.DroppedSaturated) == 1)
                    topUps++;
                topUps.Should().BeLessThan(2000);
            }

            // Within the deadline the budget is still the stranger's: this is the window the deadline bounds.
            held.Add(Task.Run(() => mediator.PostAsync(aliceForward)));
            await MediatorTestServer.WaitUntilAsync(() => mediator.Outcome(ForwardOutcomes.DroppedSaturated) == 3, "Alice's forward is judged on the request");
            await MediatorTestServer.WaitUntilAsync(() => responses.Started == startedBefore + held.Count, "every 202 is held unread");

            time.Advance(Deadline);
            await MediatorTestServer.WaitUntilAsync(() => mediator.Handler.Outstanding == 0 && mediator.Handler.OutstandingBytes == 0, "the deadline releases every reservation");
            await MediatorTestServer.WaitUntilAsync(() => responses.Aborted == held.Count, "the deadline aborts every held request, the saturated ones too");
            var failures = await Task.WhenAll(held.Select(t => Record.ExceptionAsync(() => t.WaitAsync(MediatorTestServer.Timeout))));

            mediator.Controls.ResponseGate = null;
            var retried = await mediator.PostAsync(aliceForward);
            await mediator.FlushAsync();

            bytesHeld.Should().BeGreaterThan(ForwardHandler.DefaultByteBudget - (bytesHeld / admitted), "the stranger's held forwards nearly filled the byte budget");
            failures.Should().AllSatisfy(f => f.Should().NotBeNull().And.NotBeOfType<TimeoutException>());
            retried.Status.Should().Be(HttpStatusCode.Accepted);
            mediator.Outcome(ForwardOutcomes.DroppedUnacknowledged).Should().Be(admitted + topUps);
            mediator.Outcome(ForwardOutcomes.DroppedSaturated).Should().Be(3, "the stranger's two overflows and Alice's first try, all inside the window");
            mediator.Outcome(ForwardOutcomes.Queued).Should().Be(1, "Alice's forward sent again after the deadline is relayed");
            (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1);
        }
        finally
        {
            mediator.Controls.ResponseGate = null;
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_202_committed_one_tick_before_the_deadline_is_relayed_exactly_once_and_its_deadline_is_gone()
    {
        var time = new QaManualTimeProvider();
        var responses = new DeadlineResponseTracker(HttpMethods.Post);
        await using var mediator = await StartAsync(time, responses);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(bob.Did);
        var (packed, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(note, bob.Did));
        var timers = time.ScheduledTimers;
        var startedBefore = responses.Started;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.Controls.ResponseGate = gate;
        try
        {
            var post = Task.Run(() => mediator.PostAsync(packed));
            await MediatorTestServer.WaitUntilAsync(() => responses.Started == startedBefore + 1, "the 202 is held unread");
            time.ScheduledTimers.Should().Be(timers + 1, "the held 202 has its deadline");

            time.Advance(Deadline - Tick);
            gate.SetResult(); // the client reads its 202 one tick before the deadline
            var response = await post.WaitAsync(MediatorTestServer.Timeout);
            await MediatorTestServer.WaitUntilAsync(() => responses.Completed == startedBefore + 1, "the 202 is committed");
            await MediatorTestServer.WaitUntilAsync(() => time.ScheduledTimers == timers, "the commit disposes the deadline");
            time.Advance(Deadline); // far past where the deadline was
            await mediator.FlushAsync();

            response.Status.Should().Be(HttpStatusCode.Accepted);
            response.Body.Should().BeEmpty();
            responses.Aborted.Should().Be(0, "completion wins: nothing is aborted after the commit");
            mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
            var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle("relayed exactly once").Subject;
            (await MediatorRecipient.UnpackFromSenderAsync(bob, delivered.Payload, alice)).Message.Id.Should().Be(note.Id);
        }
        finally
        {
            mediator.Controls.ResponseGate = null;
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task An_unknown_next_and_a_registered_next_get_the_same_deadline_and_are_aborted_at_the_same_instant()
    {
        // D7: an unknown next is dropped inline and reserves nothing, a registered one waits for its
        // acknowledgement; the deadline must not tell them apart (the 202 of both is aborted at once).
        var time = new QaManualTimeProvider();
        var responses = new DeadlineResponseTracker(HttpMethods.Post);
        await using var mediator = await StartAsync(time, responses);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var carol = await mediator.NewPeerAsync(); // never enrolled
        var alice = await mediator.NewSenderAsync();
        var (toBob, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did));
        var (toCarol, _) = await alice.ForwardAsync(mediator.Did, carol.Did, await alice.AuthcryptAsync(alice.Note(carol.Did), carol.Did));
        var timers = time.ScheduledTimers;
        var startedBefore = responses.Started;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.Controls.ResponseGate = gate;
        try
        {
            // One after the other, so each dispatch sees exactly what the other left reserved.
            var registered = Task.Run(() => mediator.PostAsync(toBob));
            await MediatorTestServer.WaitUntilAsync(() => responses.Started == startedBefore + 1, "the registered next's 202 is held unread");
            var unknown = Task.Run(() => mediator.PostAsync(toCarol));
            await MediatorTestServer.WaitUntilAsync(() => responses.Started == startedBefore + 2, "both 202s are held unread");
            mediator.Handler.Outstanding.Should().Be(1, "only the registered next is reserved");
            mediator.Outcome(ForwardOutcomes.DroppedUnknown).Should().Be(1);
            time.ScheduledTimers.Should().Be(timers + 2, "both 202s have a deadline, registered or not");

            time.Advance(Deadline - Tick);
            responses.Aborted.Should().Be(0);
            time.Advance(Tick);
            await MediatorTestServer.WaitUntilAsync(() => responses.Aborted == 2, "both are aborted at the deadline");
            var registeredFailure = await Record.ExceptionAsync(() => registered.WaitAsync(MediatorTestServer.Timeout));
            var unknownFailure = await Record.ExceptionAsync(() => unknown.WaitAsync(MediatorTestServer.Timeout));
            await MediatorTestServer.WaitUntilAsync(() => mediator.Handler.Outstanding == 0, "the registered forward's reservation is released");

            registeredFailure.Should().NotBeNull().And.NotBeOfType<TimeoutException>();
            unknownFailure.Should().NotBeNull().And.NotBeOfType<TimeoutException>();
            unknownFailure!.GetType().Should().Be(registeredFailure!.GetType(), "the sender sees the same failure either way");
            mediator.Outcome(ForwardOutcomes.DroppedUnacknowledged).Should().Be(1);
            mediator.Outcome(ForwardOutcomes.DroppedUnknown).Should().Be(1);
            mediator.Store.MessageCount.Should().Be(0);
            mediator.LeakingLogLines().Should().BeEmpty();
        }
        finally
        {
            mediator.Controls.ResponseGate = null;
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_202_the_forward_handler_did_not_produce_has_no_deadline()
    {
        // A message the mediator has no handler for (an authcrypted basic message, no return_route) is
        // answered 202 exactly as before: nothing waits on its acknowledgement, so nothing bounds its commit.
        var time = new QaManualTimeProvider();
        var responses = new DeadlineResponseTracker(HttpMethods.Post);
        await using var mediator = await StartAsync(time, responses);
        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(mediator.Did);
        var packed = (await alice.Client.PackEncryptedAsync(note, new PackEncryptedOptions(Recipients: new[] { mediator.Did }, From: alice.Did, Forward: false))).Message;
        var timers = time.ScheduledTimers;
        var startedBefore = responses.Started;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.Controls.ResponseGate = gate;
        try
        {
            var post = Task.Run(() => mediator.PostAsync(packed));
            await MediatorTestServer.WaitUntilAsync(() => responses.Started == startedBefore + 1, "the 202 is held unread");
            time.ScheduledTimers.Should().Be(timers, "no deadline is armed");

            time.Advance(TimeSpan.FromHours(1));
            responses.Aborted.Should().Be(0);
            post.IsCompleted.Should().BeFalse("the response is still held, not aborted");

            gate.SetResult();
            var response = await post.WaitAsync(MediatorTestServer.Timeout);
            response.Status.Should().Be(HttpStatusCode.Accepted);
            mediator.Logs.Entries.Should().Contain(e => e.Contains("No protocol handler registered", StringComparison.Ordinal));
            mediator.Outcomes.Should().BeEmpty();
        }
        finally
        {
            mediator.Controls.ResponseGate = null;
            gate.TrySetResult();
        }
    }

    /// <summary>A mediator on <paramref name="time"/>, with <paramref name="responses"/> watching its POSTs; returns once the purge loop's timer is scheduled.</summary>
    internal static async Task<MediatorTestServer> StartAsync(QaManualTimeProvider time, DeadlineResponseTracker responses, Action<MediatorTestServerOptions>? configure = null)
    {
        var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.ConfigureServices = services => services.AddSingleton<TimeProvider>(time);
            o.ConfigurePipeline = app => app.Use(responses.Middleware);
            configure?.Invoke(o);
        });
        // The hosted purge loop's timer: the one timer that is always scheduled.
        await time.WaitForScheduledTimersAsync(1, MediatorTestServer.Timeout);
        return mediator;
    }
}

/// <summary>
/// Watches the responses of one HTTP method: how many reached <c>OnStarting</c> (the endpoint had
/// returned), how many of those are held (neither completed nor aborted), the most held at once, and
/// how each ended. Registered after the request-controls middleware, so its <c>OnStarting</c> runs
/// before the response gate's (callbacks run last-registered first).
/// </summary>
internal sealed class DeadlineResponseTracker
{
    private readonly string _method;
    private readonly long _minimumLength;
    private int _started;
    private int _held;
    private int _maxHeld;
    private int _aborted;
    private int _completed;

    /// <param name="method">The method to watch.</param>
    /// <param name="minimumLength">Only responses whose Content-Length is at least this are counted (0: all).</param>
    public DeadlineResponseTracker(string method, long minimumLength = 0)
    {
        _method = method;
        _minimumLength = minimumLength;
    }

    public int Started => Volatile.Read(ref _started);

    public int Held => Volatile.Read(ref _held);

    public int MaxHeld => Volatile.Read(ref _maxHeld);

    /// <summary>Counted responses whose request was aborted before they completed.</summary>
    public int Aborted => Volatile.Read(ref _aborted);

    /// <summary>Counted responses that completed without an abort first.</summary>
    public int Completed => Volatile.Read(ref _completed);

    public async Task Middleware(HttpContext context, RequestDelegate next)
    {
        if (!string.Equals(context.Request.Method, _method, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }
        var watch = new Watch(this);
        context.Response.OnStarting(static state =>
        {
            var (w, ctx) = ((Watch, HttpContext))state;
            if ((ctx.Response.ContentLength ?? 0) >= w.Owner._minimumLength)
                w.Start();
            return Task.CompletedTask;
        }, (watch, context));
        context.Response.OnCompleted(static state =>
        {
            ((Watch)state).End(aborted: false);
            return Task.CompletedTask;
        }, watch);
        // Kept past this middleware (never disposed here): the response is written after it returns.
        watch.Keep(context.RequestAborted.Register(static state => ((Watch)state!).End(aborted: true), watch));
        await next(context);
    }

    private sealed class Watch(DeadlineResponseTracker owner)
    {
        private int _state; // 0 not started, 1 held, 2 ended
        private CancellationTokenRegistration _kept;

        public DeadlineResponseTracker Owner => owner;

        public void Start()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                return;
            Interlocked.Increment(ref owner._started);
            var now = Interlocked.Increment(ref owner._held);
            int max;
            while ((max = Volatile.Read(ref owner._maxHeld)) < now && Interlocked.CompareExchange(ref owner._maxHeld, now, max) != max)
            {
            }
        }

        public void End(bool aborted)
        {
            if (Interlocked.CompareExchange(ref _state, 2, 1) != 1)
                return;
            Interlocked.Decrement(ref owner._held);
            if (aborted)
                Interlocked.Increment(ref owner._aborted);
            else
                Interlocked.Increment(ref owner._completed);
        }

        public void Keep(CancellationTokenRegistration registration) => _kept = registration;
    }
}
