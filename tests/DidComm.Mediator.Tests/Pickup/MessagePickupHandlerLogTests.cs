using System.Globalization;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.Logging;
using Xunit;
using ProblemReports = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// What the pickup role writes to the log, over one run that takes every branch of the request
/// paths — drops, ignored messages, answers, trims, purges and each kind of fault: request message
/// ids (shortened when long), exception type names, enum names and counts, and nothing else. No
/// DID, no <c>recipient_did</c>, no id of a queued message, no thread id and no exception text at
/// any level. The same run with a logging provider that throws on every call has the same outcomes.
/// </summary>
public sealed class MessagePickupHandlerLogTests
{
    /// <summary>What one run sent and what must never show up in a log line because of it.</summary>
    private sealed class Run
    {
        /// <summary>What a peer could observe, step by step, with every id and DID left out.</summary>
        public List<string> Transcript { get; } = new();

        /// <summary>The ids of the requests that were sent: the one thing of a message a line may carry.</summary>
        public HashSet<string> RequestIds { get; } = new(StringComparer.Ordinal);

        /// <summary>Thread ids, recipient_did values and other wire values that are not DIDs by their look.</summary>
        public List<string> Secrets { get; } = new();

        private int _threads;

        public string NewThread()
        {
            var thread = "thread-secret-" + Interlocked.Increment(ref _threads).ToString(CultureInfo.InvariantCulture);
            Secrets.Add(thread);
            return thread;
        }
    }

    private static string Describe(DispatchOutcome outcome)
    {
        if (outcome.Reply is null)
            return "no reply (" + outcome.Result + ")";
        if (MessagePickup.TryParseStatus(outcome.Reply, out var status))
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"status count={status.MessageCount} bytes={status.TotalBytes} live={status.LiveDelivery} filtered={status.RecipientDid is not null} times={status.LongestWaitedSeconds is not null} threaded={outcome.Reply.Thid is not null}");
        }
        return outcome.Reply.Type == ProblemReports.MessageType
            ? "problem-report " + ProblemReports.ReadCode(outcome.Reply)
            : "unexpected " + outcome.Reply.Type;
    }

    /// <summary>
    /// One of everything on the request paths. Every step records what came back, how many
    /// deliveries were pushed and how many messages the store still holds.
    /// </summary>
    private static async Task<Run> RunEveryBranchAsync(MessagePickupHandlerHarness h)
    {
        var run = new Run();
        var alice = h.Alice;
        var bob = h.Bob;
        var second = await h.NewDidAsync();
        var gone = await h.NewDidAsync();
        await h.EnrolAsync(alice, alice, second, gone);
        await h.EnrolAsync(bob, bob);
        var a1 = await h.QueueAsync(alice, 10, 1);
        var a2 = await h.QueueAsync(second, 20, 2);
        await h.QueueAsync(gone, 30, 3);
        await h.QueueAsync(alice, 40, 4);
        await h.QueueAsync(bob, 5, 5);
        await h.Mediation.Inner.UpdateRecipientsAsync(alice, new[] { new RecipientUpdate(gone, RecipientUpdateAction.Remove) }, default);
        h.Clock.Advance(TimeSpan.FromSeconds(42));
        var http = h.Http(alice);
        var socket = h.Live(alice);
        var bobHttp = h.Http(bob);
        var longId = new string('i', 300);
        run.Secrets.Add(longId);
        Exception Boom() => new HandlerBoomException(HandlerBoomException.Marker + " " + alice + " " + a1);

        async Task StepAsync(string label, Message message, IReturnRouteSession? session = null, HandlerEnvelope shape = HandlerEnvelope.Authcrypt, Action<DidComm.Facade.UnpackResult>? tamper = null, Action? arm = null)
        {
            run.RequestIds.Add(message.Id);
            var pushedBefore = http.Sent.Count + socket.Sent.Count + bobHttp.Sent.Count;
            string result;
            try
            {
                var received = await h.Fixture.ReceiveAsync(message, h.Mediator, shape);
                tamper?.Invoke(received);
                arm?.Invoke();
                result = Describe(await h.Fixture.Host.Dispatcher.DispatchAsync(received, h.Fixture.Host.Client, h.Fixture.Host.Options, Task.CompletedTask, session, CancellationToken.None));
            }
            catch (Exception ex)
            {
                result = "threw " + ex.GetType().Name;
            }
            finally
            {
                h.Mediation.OnGetGrant = null;
                h.Mediation.OnFindGrantByRecipientDid = null;
                h.Store.OnGetStatus = null;
                h.Store.OnLease = null;
                h.Store.OnAcknowledge = null;
                h.Store.OnRelease = null;
                h.Store.OnPurgeRecipient = null;
                h.ClockFault = null;
                http.OnSend = null;
                socket.OnSend = null;
            }
            var pushed = http.Sent.Count + socket.Sent.Count + bobHttp.Sent.Count - pushedBefore;
            run.Transcript.Add(string.Create(CultureInfo.InvariantCulture, $"{label}: {result}; pushed={pushed}; stored={h.Store.Inner.MessageCount}"));
        }

        Message Threaded(Message message)
        {
            message.Thid = run.NewThread();
            return message;
        }

        // -- dropped: no authcrypt layer (FR-PICK-01) -----------------------------------------
        await StepAsync("anoncrypt status-request", h.StatusRequest(alice), http, HandlerEnvelope.Anoncrypt);
        await StepAsync("signed-only delivery-request with a long id", h.Raw("delivery-request", alice, new JsonObject { ["limit"] = 10 }, id: longId, thid: run.NewThread()), http, HandlerEnvelope.SignedOnly);
        await StepAsync("plaintext messages-received", h.MessagesReceived(alice, new[] { a1, a2 }), http, HandlerEnvelope.Plaintext);
        await StepAsync("anoncrypt(sign) live-delivery-change", h.LiveChange(alice, true), socket, HandlerEnvelope.AnoncryptOfSigned);

        // -- ignored: not a request of the mediator role --------------------------------------
        await StepAsync("a status sent to the mediator", MessagePickup.CreateStatus(alice, h.Mediator, new PickupStatus(3, alice), run.NewThread()), http);
        await StepAsync("an unknown name with a long id", h.Raw("no-such-message", alice, new JsonObject { ["recipient_did"] = bob }, id: longId), http);

        // -- answered -------------------------------------------------------------------------
        await StepAsync("status-request", Threaded(h.StatusRequest(alice)), http);
        await StepAsync("status-request for a registered DID", Threaded(h.StatusRequest(alice, second)), http);
        await StepAsync("status-request for another grant's DID", h.StatusRequest(alice, bob), http);
        run.Secrets.Add("not-a-did-secret");
        await StepAsync("status-request for something that is not a DID", h.StatusRequest(alice, "not-a-did-secret"), http);
        await StepAsync("status-request for a DID URL", h.StatusRequest(alice, second + "#fragment-secret"), http);
        run.Secrets.Add("fragment-secret");
        await StepAsync("status-request without a grant", h.StatusRequest(await h.NewDidAsync()), null);
        await StepAsync("live-delivery-change{true} over HTTP", Threaded(h.LiveChange(alice, true)), http);
        await StepAsync("live-delivery-change{true} over a socket", Threaded(h.LiveChange(alice, true)), socket);
        await StepAsync("delivery-request without a session", h.DeliveryRequest(alice, 10), null);
        await StepAsync("delivery-request with limit 0", h.DeliveryRequest(alice, 0, second), http);

        // -- faults on a delivery, before anything is delivered --------------------------------
        await StepAsync("delivery: the grant cannot be read", h.DeliveryRequest(alice, 10), http, arm: () => h.Mediation.OnGetGrant = (_, _) => throw Boom());
        await StepAsync("delivery: the lease fails", h.DeliveryRequest(alice, 10), http, arm: () => h.Store.OnLease = (_, _) => throw Boom());
        await StepAsync("delivery: the registration check fails", Threaded(h.DeliveryRequest(alice, 10)), http, arm: () => h.Mediation.OnFindGrantByRecipientDid = (_, _) => throw Boom());
        await StepAsync("delivery: the purge fails", h.DeliveryRequest(alice, 10), http, arm: () => h.Store.OnPurgeRecipient = (_, _, _) => throw Boom());
        await StepAsync("delivery: the store hands back another grant's record", h.DeliveryRequest(alice, 10), http,
            arm: () => h.Store.OnLease = async (request, ct) => (await h.Store.Inner.LeaseAsync(request, ct)).Append(new QueuedMessage("foreign-secret-id", bob, bob, PickupFixture.Payload(3), request.Now)).ToArray());
        run.Secrets.Add("foreign-secret-id");
        await StepAsync("delivery: the clock fails", h.DeliveryRequest(alice, 10), http, arm: () => h.ClockFault = Boom);
        await StepAsync("delivery: the session throws", h.DeliveryRequest(alice, 10, second), http, arm: () => http.OnSend = (_, _) => throw Boom());
        await StepAsync("delivery: the session does not take it", Threaded(h.DeliveryRequest(alice, 10, second)), http, arm: () => http.OnSend = (_, _) => ReturnRouteSendResult.NotSent);
        await StepAsync("delivery: nothing fits the byte budget", h.DeliveryRequest(alice, 10, second), socket, arm: () => socket.OnSend = (_, _) => ReturnRouteSendResult.TooLarge);
        await StepAsync("delivery: nothing fits, again", h.DeliveryRequest(alice, 10, second), socket, arm: () => socket.OnSend = (_, _) => ReturnRouteSendResult.TooLarge);
        await StepAsync("delivery: the trim cannot release", h.DeliveryRequest(alice, 10, second), http, arm: () =>
        {
            http.OnSend = (_, _) => ReturnRouteSendResult.TooLarge;
            h.Store.OnRelease = (_, _, _) => throw Boom();
        });
        // The leases that could not be released end by their timeout.
        h.Clock.Advance(TimeSpan.FromSeconds(61));

        // -- a delivery that works: the removed DID's message is purged on the way --------------
        await StepAsync("delivery-request", Threaded(h.DeliveryRequest(alice, 10)), http);
        await StepAsync("messages-received", Threaded(h.MessagesReceived(alice, new[] { a1, "unknown-secret-id" })), socket);
        run.Secrets.Add("unknown-secret-id");
        await StepAsync("live-delivery-change{false}", h.LiveChange(alice, false), socket);

        // -- faults on the other three requests -------------------------------------------------
        await StepAsync("status: the grant cannot be read", h.StatusRequest(alice), http, arm: () => h.Mediation.OnGetGrant = (_, _) => throw Boom());
        await StepAsync("status: the registration cannot be read", h.StatusRequest(alice, second), http, arm: () => h.Mediation.OnFindGrantByRecipientDid = (_, _) => throw Boom());
        await StepAsync("status: the store reports a negative count", h.StatusRequest(alice), http, arm: () => h.Store.OnGetStatus = (_, _, _, _) => new ValueTask<QueueStatus>(new QueueStatus(-1, -1, null, null)));
        await StepAsync("acknowledgement: the store fails", h.MessagesReceived(alice, new[] { a2 }), http, arm: () => h.Store.OnAcknowledge = (_, _, _) => throw Boom());
        await StepAsync("acknowledgement: the status cannot be read", Threaded(h.MessagesReceived(alice, new[] { a2 })), http, arm: () => h.Store.OnGetStatus = (_, _, _, _) => throw Boom());
        await StepAsync("live change: the status cannot be read", h.LiveChange(alice, true), socket, arm: () => h.Store.OnGetStatus = (_, _, _, _) => throw Boom());
        await StepAsync("status after the failed live change", h.StatusRequest(alice), socket);

        // -- a reply that cannot be built: a thread id no message may carry, slipped in after unpack
        const string unusableThread = "thread secret with spaces";
        run.Secrets.Add(unusableThread);
        await StepAsync("status-request whose thread cannot be echoed", h.StatusRequest(alice), http, tamper: received => received.Message.Thid = unusableThread);

        // -- malformed, from an authenticated peer: thrown, not logged by the role ---------------
        await StepAsync("malformed delivery-request", h.Raw("delivery-request", alice, new JsonObject { ["limit"] = "limit-secret" }), http);
        run.Secrets.Add("limit-secret");
        await StepAsync("too many ids", h.MessagesReceived(alice, Enumerable.Range(0, 101).Select(i => "id-secret-" + i).ToArray()), http);
        run.Secrets.Add("id-secret-");
        await StepAsync("a thread id over the bound", h.Raw("status-request", alice, new JsonObject(), thid: new string('T', 513)), http);
        run.Secrets.Add(new string('T', 64));

        // -- another peer, to finish: its own queue, untouched by all of the above ---------------
        await StepAsync("the other peer's delivery-request", h.DeliveryRequest(bob, 10), bobHttp);

        return run;
    }

    [Fact]
    public async Task No_pickup_log_line_at_any_level_carries_a_did_a_queue_id_a_thread_id_or_exception_text()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();

        var run = await RunEveryBranchAsync(h);

        var lines = h.Fixture.PickupLogs;
        var queueIds = h.Store.Enqueued.Select(e => e.Message.Id).ToArray();
        queueIds.Should().HaveCount(5);

        // The run really took every branch that logs.
        foreach (var fragment in new[]
        {
            "a delivery is at most",                              // start-up
            "dropped: no authcrypt sender (FR-PICK-01)",          // unauthenticated
            "ignored: not a request the mediator role answers",   // status, unknown name
            "status request",                                     // faults, by request
            "delivery request",
            "acknowledgement",
            "live-delivery change",
            "the reply could not be built",
            "the session did not take the delivery",
            "does not fit this peer's envelope",
            "could not be released",
            "queued message(s) removed",
        })
        {
            lines.Should().Contain(e => e.Formatted.Contains(fragment), fragment);
        }
        foreach (var level in new[] { LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error })
            lines.Should().Contain(e => e.Level == level, level.ToString());

        // What a line may carry in its values: a request id (or its replacement), an exception
        // type name, an enum name, a count. And nowhere, formatted or structured: a DID, a
        // recipient_did, a queue id, a thread id, exception text.
        var typeNames = new[] { nameof(HandlerBoomException), nameof(InvalidOperationException), "MalformedMessageException" };
        foreach (var line in lines)
        {
            line.Exception.Should().BeNull("the exception object is never handed to the logger: its text is host or wire data");
            var everything = line.Everything;
            everything.Should().NotContain("did:", "no DID and no recipient_did reaches a log line");
            everything.Should().NotContain(HandlerBoomException.Marker, "exception text is never logged, only the type name");
            foreach (var id in queueIds)
                everything.Should().NotContain(id, "the id of a queued message is never logged");
            foreach (var secret in run.Secrets)
                everything.Should().NotContain(secret, "no thread id or other wire value is logged");

            foreach (var (name, value) in line.State)
            {
                if (name == "{OriginalFormat}")
                    continue;
                var allowed = value switch
                {
                    int or long or bool => true,
                    string text when name == "MessageId" => run.RequestIds.Contains(text) || text == "(id of 300 characters)",
                    string text when name == "ExceptionType" => typeNames.Contains(text),
                    string text when name == "Outcome" => Enum.TryParse<EnqueueOutcome>(text, out _),
                    _ => false,
                };
                allowed.Should().BeTrue($"{name} = {value} in \"{line.State["{OriginalFormat}"]}\" must be a request id, a type name, an enum name or a count");
            }
        }

        lines.Should().Contain(e => e.Formatted.Contains("(id of 300 characters)"), "an over-long request id is replaced, not written");
        lines.Where(e => e.Level == LogLevel.Error).Select(e => e.State["ExceptionType"]).Should()
            .Contain(nameof(HandlerBoomException)).And.Contain(nameof(InvalidOperationException)).And.Contain("MalformedMessageException");
        run.Transcript.Should().NotContain(step => step.Contains("unexpected"));
    }

    [Fact]
    public async Task With_a_logging_provider_that_throws_on_every_call_every_flow_has_the_same_outcome()
    {
        await using var logging = await MessagePickupHandlerHarness.CreateAsync();
        await using var silent = await MessagePickupHandlerHarness.CreateAsync(throwingLogger: true);

        var expected = (await RunEveryBranchAsync(logging)).Transcript;
        var actual = (await RunEveryBranchAsync(silent)).Transcript;

        actual.Should().Equal(expected);
        silent.Fixture.Logs.Entries.Should().BeEmpty("the provider threw on every call; nothing was captured");

        // And the run is the one the log test reads: these are its landmarks.
        expected.Should().Contain("anoncrypt status-request: no reply (NoReply); pushed=0; stored=5");
        expected.Should().Contain("live-delivery-change{true} over HTTP: problem-report e.m.live-mode-not-supported; pushed=0; stored=5");
        expected.Should().Contain("delivery: the registration check fails: no reply (NoReply); pushed=0; stored=5");
        expected.Should().Contain("delivery-request: no reply (NoReply); pushed=1; stored=4");
        expected.Should().Contain("messages-received: status count=2 bytes=60 live=True filtered=False times=True threaded=True; pushed=0; stored=3");
        expected.Should().Contain("acknowledgement: the status cannot be read: no reply (NoReply); pushed=0; stored=2", "the acknowledgement itself was applied");
        expected.Should().Contain("status-request whose thread cannot be echoed: no reply (NoReply); pushed=0; stored=2");
        expected.Should().Contain("malformed delivery-request: threw MalformedMessageException; pushed=0; stored=2");
        expected.Should().Contain("the other peer's delivery-request: no reply (NoReply); pushed=1; stored=2");
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    [InlineData(4096, false)]
    public async Task A_request_id_is_written_up_to_128_characters_and_replaced_beyond_on_every_kind_of_line(int length, bool written)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        _ = h.Fixture.Queue;
        var id = new string('i', length);
        var logged = h.Fixture.PickupLogs.Count;

        // A drop, an ignored message, and a fault.
        (await h.SendAsync(h.Raw("status-request", h.Alice, new JsonObject(), id: id), shape: HandlerEnvelope.Anoncrypt)).ShouldBeNoReply();
        (await h.SendAsync(h.Raw("status", h.Alice, new JsonObject { ["message_count"] = 0 }, id: id, thid: "t"))).ShouldBeNoReply();
        h.Store.OnGetStatus = (_, _, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker);
        (await h.SendAsync(h.Raw("status-request", h.Alice, new JsonObject(), id: id, thid: "t"))).ShouldBeNoReply();

        var lines = h.Fixture.PickupLogs.Skip(logged).ToArray();
        lines.Select(e => e.Level).Should().Equal(LogLevel.Warning, LogLevel.Debug, LogLevel.Error);
        var expected = written ? id : $"(id of {length} characters)";
        lines.Should().OnlyContain(e => Equals(e.State["MessageId"], expected));
        if (!written)
            lines.Should().OnlyContain(e => !e.Everything.Contains("iiiiiiiiii"));
    }
}
