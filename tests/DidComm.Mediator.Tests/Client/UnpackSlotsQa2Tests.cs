using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// QA-2 for #111: the reservations of host unpack calls (I-1, I-2, D-4, D-7), what a host work item
/// keeps reachable (I-4) and when host code may start (I-3), checked over permutations of how many
/// deliveries open together, which partitions they fall in and how each delivery ends. Every timing
/// is driven by the injected <see cref="ManualTimeProvider"/>, gates and rendezvous on host calls and
/// timers (L-065); real time is only a hang guard.
/// </summary>
public sealed class UnpackSlotsQa2Tests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Unreachable = TimeSpan.FromSeconds(3);
    private const int C = UnpackReservations.MaxReserved;
    private const int Four = DeliveredMessageUnpacker.FetchConcurrency;
    private const string M1 = FakePickupExchange.M;
    private const string M2 = "did:example:other-mediator";
    private const string Bob = FakePickupExchange.L;
    private const string Carol = "did:example:carol";

    // ---- I-1, I-2: deliveries opened together -------------------------------------------------

    /// <summary>The ways the messages of the deliveries are spread over partitions.</summary>
    public enum Layout
    {
        /// <summary>Each delivery from its own mailbox; anonymous messages.</summary>
        OwnMailboxEach,

        /// <summary>Each delivery through its own mediator, one mailbox; anonymous messages.</summary>
        OwnMediatorEach,

        /// <summary>One mailbox; anonymous messages.</summary>
        OneMailboxAnonymous,

        /// <summary>One mailbox; every message names the same sender.</summary>
        OneMailboxOneSender,

        /// <summary>One mailbox; each delivery names a sender of its own.</summary>
        OneMailboxSenderEach,
    }

    /// <summary>
    /// I-1, I-2 (ADV3-2 with 8, 16, C/4 and C/4 + 1 deliveries of four, opened at the same moment
    /// from as many threads): every host call ignores its token and never ends. However the deliveries are spread
    /// over mailboxes, mediators and senders, exactly the bound of the partitions they fall in is
    /// reserved, never more than C; every other message is refused at once, for the bound that is
    /// full, and never starts. Once the calls end, every reservation is given back.
    /// </summary>
    [Theory]
    [InlineData(8, Layout.OwnMailboxEach, 32, 0, 0)]
    [InlineData(16, Layout.OwnMailboxEach, 64, 0, 0)]
    [InlineData(C / Four, Layout.OwnMailboxEach, C, 0, 0)]
    [InlineData(C / Four + 1, Layout.OwnMailboxEach, C, 0, Four)]
    [InlineData(C / Four, Layout.OwnMediatorEach, C, 0, 0)]
    [InlineData(C / Four + 1, Layout.OwnMediatorEach, C, 0, Four)]
    [InlineData(8, Layout.OneMailboxAnonymous, 16, 16, 0)]
    [InlineData(16, Layout.OneMailboxAnonymous, 16, 48, 0)]
    [InlineData(8, Layout.OneMailboxOneSender, 4, 28, 0)]
    [InlineData(16, Layout.OneMailboxOneSender, 4, 60, 0)]
    [InlineData(8, Layout.OneMailboxSenderEach, 32, 0, 0)]
    [InlineData(16, Layout.OneMailboxSenderEach, 64, 0, 0)]
    [InlineData(C / Four, Layout.OneMailboxSenderEach, C, 0, 0)]
    [InlineData(C / Four + 1, Layout.OneMailboxSenderEach, C, 0, Four)]
    public async Task Deliveries_opened_together_reserve_exactly_the_bounds_of_their_partitions(int deliveries, Layout layout, int reserved, int partitionRefusals, int containerRefusals)
    {
        var time = new ManualTimeProvider();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            calls.Enter(TestJwe.LabelOf(packed), ct);
            return never.Task; // ignores its token
        }, Budget, time);
        try
        {
            var inputs = Enumerable.Range(0, deliveries).Select(d => DeliveryOf(layout, d)).ToArray();
            var batches = new Task<IReadOnlyList<DeliveredOutcome>>[deliveries];
            using (var start = new Barrier(deliveries))
            {
                var openers = Enumerable.Range(0, deliveries).Select(d => new Thread(() =>
                {
                    start.SignalAndWait(RealTime);
                    batches[d] = unpacker.UnpackAllAsync(inputs[d].Scope, inputs[d].Messages, $"request-{d}", default);
                })).ToArray();
                foreach (var opener in openers)
                    opener.Start();
                foreach (var opener in openers)
                    opener.Join(RealTime).Should().BeTrue();
            }

            // Every worker took its message before UnpackAllAsync returned (the host calls never end).
            unpacker.Reserved.Should().Be(reserved, "exactly the bounds of the partitions the deliveries fall in are reserved");
            await calls.WaitForAsync(reserved, RealTime);
            time.Advance(Budget);
            var outcomes = (await Task.WhenAll(batches).WaitAsync(RealTime)).SelectMany(o => o).ToArray();

            outcomes.Count(o => IsRefusal(o, "all mailboxes together")).Should().Be(containerRefusals, "refused for the container");
            outcomes.Count(o => IsRefusal(o, "for its sender")).Should().Be(partitionRefusals, "refused for the partition");
            outcomes.Count(o => o.Error is TimeoutException t && t.Message.Contains("ran out", StringComparison.Ordinal)).Should().Be(reserved, "every call that started was let go at the budget");
            calls.Count.Should().Be(reserved, "a refused message never starts");
            calls.MaxRunning.Should().BeLessThanOrEqualTo(C);
            unpacker.Reserved.Should().Be(reserved, "a call let go keeps its reservation until it ends");
        }
        finally
        {
            never.TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue("every reservation is given back when its call ends");
        unpacker.Reservations.Partitions.Should().Be(0);
    }

    private static (DeliveryScope Scope, DeliveredInput[] Messages) DeliveryOf(Layout layout, int d)
    {
        var scope = layout switch
        {
            Layout.OwnMailboxEach => new DeliveryScope(M1, $"did:example:mailbox-{d}"),
            Layout.OwnMediatorEach => new DeliveryScope($"did:example:mediator-{d}", Bob),
            _ => new DeliveryScope(M1, Bob),
        };
        var messages = Enumerable.Range(0, Four).Select(i =>
        {
            var label = $"m-{d}-{i}";
            var packed = layout switch
            {
                Layout.OneMailboxOneSender => TestJwe.Authcrypt("did:example:mallory#key-1", label),
                Layout.OneMailboxSenderEach => TestJwe.Authcrypt($"did:example:sender-{d}#key-1", label),
                _ => TestJwe.Anoncrypt(label),
            };
            return new DeliveredInput(label, TestJwe.Bytes(packed));
        }).ToArray();
        return (scope, messages);
    }

    // ---- ADV2-2: a resolver that ignores cancellation, across mailboxes and mediators -------------

    /// <summary>
    /// I-2 (ADV2-2) across mailboxes and mediators: Mallory's four lookups that ignore their tokens
    /// fill her partition in (M1, Bob) only. Her fifth message there is refused — whatever DID URL of
    /// hers it names, since they all resolve her DID — while her messages to Carol's mailbox and
    /// through another mediator open, and so do Alice's and an anonymous message in Bob's mailbox.
    /// Then sixteen anonymous lookups fill Bob's anonymous partition at M1 only: an anonymous message
    /// for Bob through another mediator, or for Carol, still opens.
    /// </summary>
    [Fact]
    public async Task One_sender_fills_only_its_own_partition_and_the_anonymous_partition_is_per_mediator_and_mailbox()
    {
        var (unpacker, time, calls, never) = WithJunkThatNeverEnds();
        var bobAtM1 = new DeliveryScope(M1, Bob);
        try
        {
            await FillAsync(unpacker, time, calls, bobAtM1, Enumerable.Range(0, 4).Select(i => Junk(TestJwe.Authcrypt("did:example:mallory#key-1", $"junk-m-{i}"))).ToArray());
            unpacker.Reserved.Should().Be(4);

            var refused = await Open(unpacker, bobAtM1,
                TestJwe.Authcrypt("did:example:mallory#key-2", "mallory-other-fragment"),
                TestJwe.Authcrypt("did:example:mallory?versionId=7#key-1", "mallory-query"),
                TestJwe.Authcrypt("did:example:mallory/some/path#key-1", "mallory-path"),
                TestJwe.Authcrypt("did:example:mallory", "mallory-bare"));
            refused.Should().OnlyContain(o => IsRefusal(o, "for its sender"), "every DID URL of Mallory's resolves her DID, whose partition is full");

            (await Open(unpacker, new DeliveryScope(M1, Carol), TestJwe.Authcrypt("did:example:mallory#key-1", "mallory-to-carol"))).Single().Succeeded.Should().BeTrue("Carol's mailbox has a partition for Mallory of its own");
            (await Open(unpacker, new DeliveryScope(M2, Bob), TestJwe.Authcrypt("did:example:mallory#key-1", "mallory-via-m2"))).Single().Succeeded.Should().BeTrue("another mediator's partitions are its own");
            (await Open(unpacker, bobAtM1, TestJwe.Authcrypt("did:example:alice#key-1", "alice"), TestJwe.Anoncrypt("anonymous"))).Should().OnlyContain(o => o.Succeeded, "another sender and the anonymous partition are not full");

            await FillAsync(unpacker, time, calls, bobAtM1, Enumerable.Range(0, 16).Select(i => Junk(TestJwe.Anoncrypt($"junk-a-{i}"))).ToArray());
            unpacker.Reserved.Should().Be(20);

            (await Open(unpacker, bobAtM1, TestJwe.Anoncrypt("anonymous-2"), TestJwe.EcdhEsWithSkid("did:example:alice#key-1", "es-with-skid"), TestJwe.AuthcryptApuOnly("did:example:alice#key-1", "apu-only")))
                .Should().OnlyContain(o => IsRefusal(o, "for its sender"), "anoncrypt, ECDH-ES with a skid and apu-only all share Bob's anonymous partition at M1");
            (await Open(unpacker, new DeliveryScope(M2, Bob), TestJwe.Anoncrypt("anonymous-via-m2"))).Single().Succeeded.Should().BeTrue("the anonymous partition is per mediator");
            (await Open(unpacker, new DeliveryScope(M1, Carol), TestJwe.Anoncrypt("anonymous-to-carol"))).Single().Succeeded.Should().BeTrue("the anonymous partition is per mailbox");
            (await Open(unpacker, bobAtM1, TestJwe.Authcrypt("did:example:alice#key-1", "alice-2"))).Single().Succeeded.Should().BeTrue("a named sender is not in the anonymous partition");

            calls.Labels.Should().NotContain(new[] { "mallory-other-fragment", "mallory-query", "mallory-path", "mallory-bare", "anonymous-2", "es-with-skid", "apu-only" }, "a refused message never starts");
            unpacker.Reserved.Should().Be(20, "the stalled lookups keep their reservations, and nothing else holds one");
        }
        finally
        {
            never.TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue();
        unpacker.Reservations.Partitions.Should().Be(0);
    }

    /// <summary>
    /// R-1 (the residual, pinned): C/4 + 1, C/4 + 4 and C/2 named senders, each with four lookups that ignore
    /// their tokens, opened at the same moment in one mailbox. Exactly C calls are reserved, never
    /// more; the rest are refused for the container. While those calls run, every partition is
    /// refused at once — a new sender, the anonymous partition, another mailbox, another mediator —
    /// with no host call. When one sender's four calls end, four messages of anyone open again; when
    /// all end, nothing is reserved.
    /// </summary>
    [Theory]
    [InlineData(C / Four + 1)]
    [InlineData(C / Four + 4)]
    [InlineData(C / 2)]
    public async Task Named_senders_enough_to_fill_the_container_hold_exactly_C_and_then_every_partition_is_refused_at_once(int senders)
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var lookups = new ConcurrentDictionary<string, TaskCompletionSource<DeliveredUnpack>>(StringComparer.Ordinal);
        TaskCompletionSource<DeliveredUnpack> LookupOf(string sender) => lookups.GetOrAdd(sender, _ => new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously));
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            calls.Enter(label, ct);
            return label.StartsWith("sender-", StringComparison.Ordinal) ? LookupOf(label[..label.LastIndexOf('-')]).Task : Task.FromResult(UnpackFakes.Opened(label));
        }, Budget, time);
        var bob = new DeliveryScope(M1, Bob);
        try
        {
            var batches = new Task<IReadOnlyList<DeliveredOutcome>>[senders];
            Parallel.For(0, senders, new ParallelOptions { MaxDegreeOfParallelism = senders }, s =>
                batches[s] = unpacker.UnpackAllAsync(bob,
                    Enumerable.Range(0, Four).Select(i => Junk(TestJwe.Authcrypt($"did:example:sender-{s}#key-1", $"sender-{s}-{i}"))).ToArray(),
                    $"request-{s}", default));
            unpacker.Reserved.Should().Be(C, "exactly C calls are reserved, never more");
            await calls.WaitForAsync(C, RealTime);
            time.Advance(Budget);
            var outcomes = (await Task.WhenAll(batches).WaitAsync(RealTime)).SelectMany(o => o).ToArray();
            outcomes.Count(o => IsRefusal(o, "all mailboxes together")).Should().Be(senders * Four - C);
            calls.Count.Should().Be(C);

            var everyPartition = new (DeliveryScope Scope, string Packed)[]
            {
                (bob, TestJwe.Authcrypt("did:example:newcomer#key-1", "newcomer")),
                (bob, TestJwe.Anoncrypt("anonymous")),
                (new DeliveryScope(M1, Carol), TestJwe.Authcrypt("did:example:alice#key-1", "alice-to-carol")),
                (new DeliveryScope(M2, Bob), TestJwe.Anoncrypt("anonymous-via-m2")),
            };
            foreach (var (scope, packed) in everyPartition)
            {
                var task = unpacker.UnpackAllAsync(scope, new[] { Junk(packed) }, "request-probe", default);
                task.IsCompleted.Should().BeTrue("a refusal is decided at once, with no host call or timer");
                IsRefusal((await task).Single(), "all mailboxes together").Should().BeTrue();
            }
            calls.Count.Should().Be(C, "no refused message started");
            unpacker.Reserved.Should().Be(C);

            // One sender's lookups end (as many as started for it, one to four): that many reservations
            // come back, and that many messages of anyone open.
            var first = calls.Labels.First();
            var sender = first[..first.LastIndexOf('-')];
            var released = calls.Labels.Count(l => l.StartsWith(sender + "-", StringComparison.Ordinal));
            LookupOf(sender).TrySetResult(UnpackFakes.Opened("late"));
            SpinWait.SpinUntil(() => unpacker.Reserved == C - released, RealTime).Should().BeTrue("{0} reservations came back", released);
            var anyone = new[] { TestJwe.Anoncrypt("after-1"), TestJwe.Authcrypt("did:example:newcomer#key-1", "after-2"), TestJwe.Anoncrypt("after-3"), TestJwe.Authcrypt("did:example:alice#key-1", "after-4") };
            (await Open(unpacker, bob, anyone.Take(released).ToArray())).Should().OnlyContain(o => o.Succeeded);
        }
        finally
        {
            foreach (var lookup in lookups.Values)
                lookup.TrySetCanceled();
            for (var s = 0; s < senders; s++)
                LookupOf($"sender-{s}").TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue();
        unpacker.Reservations.Partitions.Should().Be(0);
    }

    // ---- I-1: exactly-once release under stress ------------------------------------------------

    /// <summary>
    /// I-1 (exactly once): one set of reservations shared by an unpacker per round, over thousands of
    /// deliveries whose messages open, fail at once (as a faulted task or a throw before any task),
    /// fail after a second, run into the budget, are out of time before their turn, ignore their
    /// tokens and end later, or are refused; with callers that cancel and owners that shut down
    /// mid-delivery. A
    /// sampler reads the count throughout: it never goes below 0 or above C. After each round the
    /// reservations held are exactly the late calls still running, and at the end none are held.
    /// </summary>
    [Fact]
    public async Task Reservations_are_given_back_exactly_once_whatever_ends_each_call()
    {
        var time = new ManualTimeProvider();
        var reservations = new UnpackReservations();
        var lateRunning = 0;
        var late = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string, CancellationToken, Task<DeliveredUnpack>> host = (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            switch (label[0])
            {
                case 'o': return Task.FromResult(UnpackFakes.Opened(label));
                case 'x': throw new MalformedMessageException("thrown before any task"); // synchronous throw
                case 'f': return Task.FromException<DeliveredUnpack>(new CryptoException("JWE could not be decrypted."));
                case 'd': return FailAfter(TimeSpan.FromSeconds(1), time, ct);
                case 's': return UnpackFakes.StallUntilCancelled(ct);
                case 'l': return Late(late.Task);
                default: return Task.FromResult(UnpackFakes.Opened(label));
            }
        };
        async Task<DeliveredUnpack> Late(Task<DeliveredUnpack> until)
        {
            Interlocked.Increment(ref lateRunning);
            try
            {
                return await until.ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref lateRunning);
            }
        }

        var min = int.MaxValue;
        var max = int.MinValue;
        using var stop = new CancellationTokenSource();
        var sampler = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var now = reservations.Reserved;
                if (now < Volatile.Read(ref min))
                    Volatile.Write(ref min, now);
                if (now > Volatile.Read(ref max))
                    Volatile.Write(ref max, now);
            }
        }) { IsBackground = true };
        sampler.Start();

        var scopes = new[] { new DeliveryScope(M1, Bob), new DeliveryScope(M1, Carol), new DeliveryScope(M2, Bob) };
        var senders = new string?[] { null, "did:example:alice#key-1", "did:example:sam#key-1" };
        var kinds = new[] { 'o', 'x', 'f', 'd', 's', 'l' };
        var random = new Random(2111);
        var deliveries = 0;
        var ended = new Dictionary<string, int> { ["opened"] = 0, ["failed"] = 0, ["budget"] = 0, ["refused"] = 0, ["caller"] = 0, ["shutdown"] = 0 };
        try
        {
            for (var round = 0; round < 250; round++)
            {
                using var shutdown = new CancellationTokenSource();
                using var cancel = new CancellationTokenSource();
                var unpacker = new DeliveredMessageUnpacker(host, Budget, time, shutdown.Token, reservations);
                var batches = new List<Task<IReadOnlyList<DeliveredOutcome>>>();
                for (var d = 0; d < 8; d++)
                {
                    var messages = Enumerable.Range(0, random.Next(1, 11)).Select(_ =>
                    {
                        var label = $"{kinds[random.Next(kinds.Length)]}-{random.Next(1_000_000)}"; // new ids: nothing is held back
                        var sender = senders[random.Next(senders.Length)];
                        return new DeliveredInput(label, TestJwe.Bytes(sender is null ? TestJwe.Anoncrypt(label) : TestJwe.Authcrypt(sender, label)));
                    }).ToArray();
                    batches.Add(unpacker.UnpackAllAsync(scopes[random.Next(scopes.Length)], messages, "request", d % 4 == 3 ? cancel.Token : default));
                    deliveries++;
                }
                switch (round % 5)
                {
                    case 1:
                        cancel.Cancel();
                        break;
                    case 2:
                        shutdown.Cancel();
                        break;
                    case 3:
                        time.Advance(Budget / 4); // part of the budget passes first: the messages taken after it start late
                        break;
                }
                for (var step = 0; step < 6 && batches.Any(b => !b.IsCompleted); step++)
                {
                    time.Advance(TimeSpan.FromSeconds(1));
                    time.Advance(Budget);
                    await Task.Yield();
                }
                foreach (var batch in batches)
                {
                    await ((Task)batch).ContinueWith(static _ => { }, TaskScheduler.Default).WaitAsync(RealTime);
                    if (batch.IsCanceled)
                    {
                        ended[round % 5 == 2 ? "shutdown" : "caller"]++;
                        continue;
                    }
                    foreach (var outcome in await batch)
                    {
                        var kind = outcome.Succeeded ? "opened"
                            : IsRefusal(outcome, "capacity") ? "refused"
                            : outcome.Error is TimeoutException ? "budget"
                            : "failed";
                        ended[kind]++;
                    }
                }
                SpinWait.SpinUntil(() => reservations.Reserved == Volatile.Read(ref lateRunning), RealTime)
                    .Should().BeTrue("the reservations held are exactly the late calls still running (round {0}: {1} held, {2} running)", round, reservations.Reserved, Volatile.Read(ref lateRunning));
                if (round % 25 == 24)
                {
                    late.TrySetResult(UnpackFakes.Opened("late"));
                    SpinWait.SpinUntil(() => reservations.Reserved == 0, RealTime).Should().BeTrue("round {0}", round);
                    late = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }
        finally
        {
            late.TrySetResult(UnpackFakes.Opened("late"));
            SpinWait.SpinUntil(() => reservations.Reserved == 0, RealTime);
            stop.Cancel();
            sampler.Join(RealTime);
        }

        reservations.Reserved.Should().Be(0);
        reservations.Partitions.Should().Be(0);
        Volatile.Read(ref min).Should().BeGreaterThanOrEqualTo(0, "a reservation is never given back twice");
        Volatile.Read(ref max).Should().BeLessThanOrEqualTo(C);
        Volatile.Read(ref max).Should().BeGreaterThan(Four, "the run held many calls at once");
        deliveries.Should().Be(2000);
        ended.Should().OnlyContain(e => e.Value > 0, "every way a call ends happened: {0}", string.Join(", ", ended.Select(e => $"{e.Key} {e.Value}")));
    }

    private static async Task<DeliveredUnpack> FailAfter(TimeSpan after, TimeProvider time, CancellationToken ct)
    {
        await Task.Delay(after, time, ct).ConfigureAwait(false);
        throw new CryptoException("JWE could not be decrypted.");
    }

    // ---- I-4 (ADV4-2): what a host call let go keeps reachable ----------------------------------

    /// <summary>How a delivery ends while one of its host calls keeps running.</summary>
    public enum Ending
    {
        /// <summary>The budget runs out.</summary>
        Budget,

        /// <summary>The caller cancels.</summary>
        Caller,

        /// <summary>The owner shuts down.</summary>
        Shutdown,
    }

    /// <summary>How host code outlives the delivery it was opening a message of.</summary>
    public enum Outliving
    {
        /// <summary>Its call blocks its thread.</summary>
        BlocksItsThread,

        /// <summary>Its call's task never ends, and it ignores its token.</summary>
        NeverEndsAndIgnoresItsToken,

        /// <summary>Its call's task never ends, it ignores its token, and it keeps a registration it made on that token.</summary>
        KeepsARegistrationOnItsToken,
    }

    // What the host code of the kept call holds on to: nothing of the delivery, by itself.
    private sealed class HostState
    {
        public CancellationTokenRegistration Registration;
    }

    /// <summary>
    /// I-4 (ADV4-2) for every way a delivery ends and every kind of host call that outlives it — one
    /// that blocks its thread, one whose task never ends and ignores its token, and one that also
    /// keeps a registration it made on its token: while that call still runs (and keeps its
    /// reservation), the delivery's message list, another message's bytes and, when the delivery
    /// returned them, its outcomes are unreachable.
    /// </summary>
    [Theory]
    [InlineData(Ending.Budget, Outliving.BlocksItsThread)]
    [InlineData(Ending.Caller, Outliving.BlocksItsThread)]
    [InlineData(Ending.Shutdown, Outliving.BlocksItsThread)]
    [InlineData(Ending.Budget, Outliving.NeverEndsAndIgnoresItsToken)]
    [InlineData(Ending.Caller, Outliving.NeverEndsAndIgnoresItsToken)]
    [InlineData(Ending.Shutdown, Outliving.NeverEndsAndIgnoresItsToken)]
    [InlineData(Ending.Budget, Outliving.KeepsARegistrationOnItsToken)]
    [InlineData(Ending.Caller, Outliving.KeepsARegistrationOnItsToken)]
    [InlineData(Ending.Shutdown, Outliving.KeepsARegistrationOnItsToken)]
    public async Task A_host_call_that_outlives_its_delivery_keeps_nothing_of_the_delivery_reachable(Ending ending, Outliving outliving)
    {
        using var floor = new PoolFloor(parked: 1);
        var time = new ManualTimeProvider();
        using var gate = new ManualResetEventSlim(false);
        using var entered = new SemaphoreSlim(0);
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostState = new HostState();
        using var shutdown = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var unpacker = new DeliveredMessageUnpacker((text, ct) =>
        {
            if (!text.StartsWith("kept", StringComparison.Ordinal))
                return Task.FromResult(UnpackFakes.Opened(text));
            if (outliving == Outliving.KeepsARegistrationOnItsToken)
                hostState.Registration = ct.Register(static () => { });
            entered.Release();
            if (outliving != Outliving.BlocksItsThread)
                return never.Task;
            gate.Wait(RealTime); // blocks its thread (bounded only so a red run cannot hang)
            return Task.FromResult(UnpackFakes.Opened(text));
        }, Budget, time, shutdown.Token);
        WeakReference[] refs;
        bool kept;
        try
        {
            refs = await OpenAndEndAsync(unpacker, time, entered, ending, shutdown, caller);
            unpacker.Reserved.Should().Be(1, "the call still runs and keeps its reservation");
            SpinWait.SpinUntil(() => time.ScheduledTimers == 0 && unpacker.Opening == 0, RealTime).Should().BeTrue("the delivery left no timer behind");
            kept = refs.Any(r => !BecomesUnreachable(r));
        }
        finally
        {
            gate.Set();
            never.TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue();
        // The host code held its registration until here; the control runs without it.
        hostState.Registration.Dispose();
        hostState.Registration = default;
        refs.Should().OnlyContain(r => BecomesUnreachable(r), "the control: the test holds nothing of the delivery");
        kept.Should().BeFalse("a host call that outlives its delivery references the unpack step, its text and its token, and the token nothing of the delivery once it has ended ({0}, {1})", ending, outliving);
    }

    /// <summary>
    /// I-4 for a delivery that ends because every message has its outcome: host code whose call
    /// opened its message, and that still holds a registration it made on its token, keeps nothing of
    /// the delivery reachable either: not its message list, another message's bytes or its outcomes.
    /// </summary>
    [Fact]
    public async Task Host_code_that_keeps_a_registration_on_its_token_after_its_call_ended_keeps_nothing_of_the_delivery_reachable()
    {
        var time = new ManualTimeProvider();
        var hostState = new HostState();
        var unpacker = new DeliveredMessageUnpacker((text, ct) =>
        {
            if (text.StartsWith("kept", StringComparison.Ordinal))
                hostState.Registration = ct.Register(static () => { });
            return Task.FromResult(UnpackFakes.Opened(text));
        }, Budget, time);

        var refs = await OpenAndLetEndAsync(unpacker);
        SpinWait.SpinUntil(() => time.ScheduledTimers == 0 && unpacker.Opening == 0 && unpacker.Reserved == 0, RealTime).Should().BeTrue("the delivery left nothing behind");
        var kept = refs.Any(r => !BecomesUnreachable(r));

        // The host code held its registration until here; the control runs without it.
        hostState.Registration.Dispose();
        hostState.Registration = default;
        refs.Should().OnlyContain(r => BecomesUnreachable(r), "the control: the test holds nothing of the delivery");
        kept.Should().BeFalse("once a delivery has ended, the token host code holds references nothing of it");
    }

    // A delivery of five that all open at once; only weak references leave this frame.
    private static async Task<WeakReference[]> OpenAndLetEndAsync(DeliveredMessageUnpacker unpacker)
    {
        var (messages, other, _) = Start(unpacker);
        var outcomes = await unpacker.UnpackAllAsync(new DeliveryScope(M1, Bob), messages, "request-1", default).WaitAsync(RealTime);
        outcomes.Should().OnlyContain(o => o.Succeeded);
        return new[] { new WeakReference(messages), new WeakReference(other), new WeakReference(outcomes), new WeakReference(outcomes[1].Unpacked) };
    }

    // A delivery of five: "kept-0", whose call outlives the delivery, and four that open at once. It
    // ends the given way; only weak references leave this frame.
    private static async Task<WeakReference[]> OpenAndEndAsync(DeliveredMessageUnpacker unpacker, ManualTimeProvider time, SemaphoreSlim entered, Ending ending, CancellationTokenSource shutdown, CancellationTokenSource caller)
    {
        var (messages, other, settled) = Start(unpacker);
        var batch = unpacker.UnpackAllAsync(new DeliveryScope(M1, Bob), messages, "request-1", ending == Ending.Caller ? caller.Token : default);
        (await entered.WaitAsync(RealTime)).Should().BeTrue();
        SpinWait.SpinUntil(() => unpacker.Settled == settled + 4, RealTime).Should().BeTrue("the four others opened");
        var refs = new List<WeakReference> { new(messages), new(other) };
        switch (ending)
        {
            case Ending.Budget:
                time.Advance(Budget);
                break;
            case Ending.Caller:
                caller.Cancel();
                break;
            case Ending.Shutdown:
                shutdown.Cancel();
                break;
        }
        await ((Task)batch).ContinueWith(static _ => { }, TaskScheduler.Default).WaitAsync(RealTime);
        if (batch.IsCompletedSuccessfully)
        {
            var outcomes = batch.Result;
            outcomes[0].Succeeded.Should().BeFalse();
            outcomes.Skip(1).Should().OnlyContain(o => o.Succeeded);
            refs.Add(new WeakReference(outcomes));
            refs.Add(new WeakReference(outcomes[1].Unpacked));
        }
        else
        {
            batch.IsCanceled.Should().BeTrue();
        }
        return refs.ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DeliveredInput[] Messages, byte[] Other, long Settled) Start(DeliveredMessageUnpacker unpacker)
    {
        var other = Encoding.UTF8.GetBytes("honest-1");
        var messages = new[]
        {
            new DeliveredInput("q-0", Encoding.UTF8.GetBytes("kept-0")),
            new DeliveredInput("q-1", other),
            new DeliveredInput("q-2", Encoding.UTF8.GetBytes("honest-2")),
            new DeliveredInput("q-3", Encoding.UTF8.GetBytes("honest-3")),
            new DeliveredInput("q-4", Encoding.UTF8.GetBytes("honest-4")),
        };
        return (messages, other, unpacker.Settled);
    }

    // ---- I-3 (ADV4-3): no host code after its delivery ended ------------------------------------

    private static readonly AsyncLocal<string?> Marked = new(OnMarkedContextRestored);
    private static Action? _atNextRestore;

    // Runs when a thread switches to an execution context in which Marked is "target": for the
    // target's host work item, on its pool thread, after the task's own cancellation check and before
    // its delegate runs. The armed action runs once; nothing escapes (an exception here ends the
    // process).
    private static void OnMarkedContextRestored(AsyncLocalValueChangedArgs<string?> args)
    {
        if (!args.ThreadContextChanged || args.CurrentValue != "target")
            return;
        var action = Interlocked.Exchange(ref _atNextRestore, null);
        try
        {
            action?.Invoke();
        }
        catch
        {
            // Recorded by the action itself; never thrown into the runtime's context switch.
        }
    }

    /// <summary>When the delivery ends relative to the target's host work item.</summary>
    public enum Window
    {
        /// <summary>While its worker is starting it (it reads the clock for the start rule), before the work item exists.</summary>
        BeforeTheWorkItem,

        /// <summary>After the work item was queued and its task passed its own cancellation check, before the unpack runs.</summary>
        AfterTheWorkItemStarted,
    }

    /// <summary>
    /// I-3 (ADV4-3) for every way the target message's delivery ends — the budget, the caller, the
    /// shutdown — in both windows: while its worker is starting it, before its work item exists, and
    /// on the pool thread that runs its work item, after the task's own cancellation check and before
    /// the unpack is called. In every case its host code never starts, it is not remembered, and its
    /// reservation comes back. The clock's readings count from zero, so that the start reading of a
    /// call that never ran (never taken, so zero) is not below the budget's start: such a call is
    /// not remembered because its code did not run.
    /// </summary>
    [Theory]
    [InlineData(Ending.Budget, Window.BeforeTheWorkItem)]
    [InlineData(Ending.Caller, Window.BeforeTheWorkItem)]
    [InlineData(Ending.Shutdown, Window.BeforeTheWorkItem)]
    [InlineData(Ending.Budget, Window.AfterTheWorkItemStarted)]
    [InlineData(Ending.Caller, Window.AfterTheWorkItemStarted)]
    [InlineData(Ending.Shutdown, Window.AfterTheWorkItemStarted)]
    public async Task No_host_code_starts_once_its_delivery_has_ended(Ending ending, Window window)
    {
        var clock = new CountingTimeProvider(new ManualTimeProvider(), readingsFromZero: true);
        using var shutdown = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(async (text, ct) =>
        {
            using (calls.Enter(text, ct))
            {
                if (text == "m-0")
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct); // a lookup that honours its token
            }
            return UnpackFakes.Opened(text);
        }, Budget, clock, shutdown.Token);
        var ended = 0;
        Exception? endError = null;
        void End()
        {
            try
            {
                switch (ending)
                {
                    case Ending.Budget:
                        clock.Inner.Advance(Budget);
                        break;
                    case Ending.Caller:
                        caller.Cancel();
                        break;
                    case Ending.Shutdown:
                        shutdown.Cancel();
                        break;
                }
            }
            catch (Exception ex)
            {
                endError = ex;
            }
            Volatile.Write(ref ended, 1);
        }

        // The delivery's only timer is its budget. The first reading of the clock after it was
        // created is made by the worker that has just taken m-0, the target, for the start rule:
        // before it reserves and before its work item exists.
        var atTheTargetsTake = 0;
        clock.OnCreate = _ => Volatile.Write(ref atTheTargetsTake, 1);
        clock.OnRead = () =>
        {
            if (Interlocked.CompareExchange(ref atTheTargetsTake, 2, 1) != 1)
                return;
            if (window == Window.BeforeTheWorkItem)
                End();
            else
                Marked.Value = "target"; // flows into m-0's work item only
        };
        if (window == Window.AfterTheWorkItemStarted)
            Volatile.Write(ref _atNextRestore, End);
        Task<IReadOnlyList<DeliveredOutcome>> batch;
        try
        {
            batch = unpacker.UnpackAllAsync(new DeliveryScope(M1, Bob),
                Enumerable.Range(0, 5).Select(i => new DeliveredInput($"q-{i}", Encoding.UTF8.GetBytes($"m-{i}"))).ToArray(),
                "request-1", caller.Token);
        }
        finally
        {
            clock.OnCreate = null;
            clock.OnRead = null;
        }

        SpinWait.SpinUntil(() => Volatile.Read(ref ended) == 1, RealTime).Should().BeTrue("the ending ran in its window");
        await ((Task)batch).ContinueWith(static _ => { }, TaskScheduler.Default).WaitAsync(RealTime);
        if (ending == Ending.Budget)
            (await batch)[0].Error.Should().BeOfType<TimeoutException>();
        else
            batch.IsCanceled.Should().BeTrue();
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Opening == 0, RealTime).Should().BeTrue("every reservation came back");

        endError.Should().BeNull();
        Volatile.Read(ref atTheTargetsTake).Should().Be(2, "the window was reached at the target's take");
        calls.Labels.Should().NotContain("m-0", "the target's host code never starts once its delivery has ended ({0}, {1})", ending, window);
        unpacker.Memory.HeldBack(new DeliveryScope(M1, Bob), new[] { "q-0" }).Single().Should().BeFalse("a call that never ran is not remembered");
        Volatile.Read(ref _atNextRestore).Should().BeNull("the armed ending was used");
    }

    /// <summary>
    /// R-2 and D-13, pinned: messages that name a sender only in <c>apu</c> count against the
    /// mailbox's anonymous partition, not that sender's, although Core resolves that sender for them
    /// (<see cref="SenderPartitionCoreQa2Tests"/>). So with lookups that ignore their tokens, Sam's
    /// own partition holds four of them and the anonymous partition sixteen more: twenty lookups of
    /// Sam's DID run at once in one mailbox, and the twenty-first message naming Sam is refused.
    /// </summary>
    [Fact]
    public async Task Lookups_of_one_senders_DID_can_run_in_its_partition_and_the_anonymous_one_at_once()
    {
        var (unpacker, time, calls, never) = WithJunkThatNeverEnds();
        var bob = new DeliveryScope(M1, Bob);
        try
        {
            await FillAsync(unpacker, time, calls, bob, Enumerable.Range(0, 4).Select(i => Junk(TestJwe.Authcrypt("did:example:sam#key-1", $"junk-skid-{i}"))).ToArray());
            await FillAsync(unpacker, time, calls, bob, Enumerable.Range(0, 16).Select(i => Junk(TestJwe.AuthcryptApuOnly("did:example:sam#key-1", $"junk-apu-{i}"))).ToArray());

            unpacker.Reserved.Should().Be(20, "four in Sam's partition and sixteen in the anonymous one, all naming Sam");
            calls.Count.Should().Be(20);
            (await Open(unpacker, bob, TestJwe.Authcrypt("did:example:sam#key-1", "junk-skid-4"), TestJwe.AuthcryptApuOnly("did:example:sam#key-1", "junk-apu-16")))
                .Should().OnlyContain(o => IsRefusal(o, "for its sender"), "both partitions are full");
        }
        finally
        {
            never.TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue();
    }

    // ---- I-1: the pool threads a blocking resolver holds ------------------------------------------

    /// <summary>
    /// I-1, R-8 (the pool-thread bound): a resolver that blocks its thread and ignores its token,
    /// under eighty deliveries of ten messages spread over two mediators, ten mailboxes, four named
    /// senders and the anonymous partitions, opened from eight threads at once. At most C threads are
    /// ever held by it, exactly C once the partitions are full; every other message is refused at
    /// once. When the threads return, nothing is reserved.
    /// </summary>
    [Fact]
    public async Task A_resolver_that_blocks_its_thread_holds_at_most_C_pool_threads_across_every_partition()
    {
        using var floor = new PoolFloor(parked: C);
        var time = new ManualTimeProvider();
        using var gate = new ManualResetEventSlim(false);
        var blocked = new ConcurrentDictionary<int, int>();
        var maxBlocked = 0;
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            using (calls.Enter(TestJwe.LabelOf(packed), ct))
            {
                var thread = Environment.CurrentManagedThreadId;
                blocked[thread] = 1;
                var now = blocked.Count;
                int seen;
                while ((seen = Volatile.Read(ref maxBlocked)) < now && Interlocked.CompareExchange(ref maxBlocked, now, seen) != seen)
                {
                }
                gate.Wait(RealTime); // blocks its thread, ignores its token (bounded only so a red run cannot hang)
                blocked.TryRemove(thread, out _);
            }
            return Task.FromResult(UnpackFakes.Opened(TestJwe.LabelOf(packed)));
        }, Budget, time);
        var scopes = new[] { M1, M2 }.SelectMany(m => new[] { Bob, Carol, "did:example:dave", "did:example:erin", "did:example:frank" }.Select(l => new DeliveryScope(m, l))).ToArray();
        var senders = new string?[] { null, "did:example:alice#key-1", "did:example:sam#key-1", "did:example:mallory#key-1", "did:example:trent#key-1" };
        var batches = new Task<IReadOnlyList<DeliveredOutcome>>[80];
        try
        {
            Parallel.For(0, batches.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, d =>
                batches[d] = unpacker.UnpackAllAsync(scopes[d % scopes.Length],
                    Enumerable.Range(0, 10).Select(i =>
                    {
                        var label = $"m-{d}-{i}";
                        var sender = senders[(d + i) % senders.Length];
                        return new DeliveredInput(label, TestJwe.Bytes(sender is null ? TestJwe.Anoncrypt(label) : TestJwe.Authcrypt(sender, label)));
                    }).ToArray(),
                    $"request-{d}", default));
            // Per mailbox (eight deliveries): 4 senders × 4 + 16 anonymous = 32; ten mailboxes could take 320, C caps them at 256.
            await calls.WaitForAsync(C, RealTime);
            SpinWait.SpinUntil(() => blocked.Count == C, RealTime).Should().BeTrue("C calls are blocked on their threads");
            time.Advance(Budget);
            for (var i = 0; i < 4 && batches.Any(b => !b.IsCompleted); i++)
                time.Advance(Budget);
            var outcomes = (await Task.WhenAll(batches).WaitAsync(RealTime)).SelectMany(o => o).ToArray();

            unpacker.Reserved.Should().Be(C);
            calls.Count.Should().Be(C, "no other message started");
            outcomes.Should().HaveCount(800).And.OnlyContain(o => !o.Succeeded);
            outcomes.Count(o => o.Error is TimeoutException t && t.Message.Contains("capacity exhausted", StringComparison.Ordinal)).Should().BeGreaterThan(0);
        }
        finally
        {
            gate.Set();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue();
        Volatile.Read(ref maxBlocked).Should().Be(C, "a blocking resolver holds at most C pool threads");
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static bool IsRefusal(DeliveredOutcome outcome, string which)
        => outcome.Error is TimeoutException t && t.Message.Contains("capacity exhausted", StringComparison.Ordinal) && t.Message.Contains(which, StringComparison.Ordinal);

    private static DeliveredInput Junk(string packed) => new(TestJwe.LabelOf(packed), TestJwe.Bytes(packed));

    private static Task<IReadOnlyList<DeliveredOutcome>> Open(DeliveredMessageUnpacker unpacker, DeliveryScope scope, params string[] packed)
        => unpacker.UnpackAllAsync(scope, packed.Select(Junk).ToArray(), "request-open", default).WaitAsync(RealTime);

    // Open junk that never ends in deliveries of four and let the budgets go: its reservations stay.
    private static async Task FillAsync(DeliveredMessageUnpacker unpacker, ManualTimeProvider time, HostCallLog calls, DeliveryScope scope, DeliveredInput[] junk)
    {
        var before = calls.Count;
        var batches = junk.Chunk(Four).Select(chunk => unpacker.UnpackAllAsync(scope, chunk, "request-junk", default)).ToArray();
        await calls.WaitForAsync(before + junk.Length, RealTime);
        time.Advance(Budget);
        (await Task.WhenAll(batches).WaitAsync(RealTime)).SelectMany(o => o).Should().OnlyContain(o => !o.Succeeded);
    }

    // An unpacker whose "junk" lookups never end and ignore their token, and whose others open at once.
    private static (DeliveredMessageUnpacker Unpacker, ManualTimeProvider Time, HostCallLog Calls, TaskCompletionSource<DeliveredUnpack> Never) WithJunkThatNeverEnds()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            calls.Enter(label, ct);
            return label.StartsWith("junk", StringComparison.Ordinal) ? never.Task : Task.FromResult(UnpackFakes.Opened(label));
        }, Budget, time);
        return (unpacker, time, calls, never);
    }

    // Whether nothing holds the target any more: collected within a bounded wait.
    private static bool BecomesUnreachable(WeakReference weak)
        => SpinWait.SpinUntil(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return !weak.IsAlive;
        }, Unreachable);
}
