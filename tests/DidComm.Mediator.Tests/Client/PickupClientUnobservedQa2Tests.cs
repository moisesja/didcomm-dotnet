using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// QA-2 (#96): the pickup client over the real <see cref="MediatorClientChannel"/> (fake send seam)
/// must not leave faulted tasks nobody observes. The project observes abandoned work on purpose
/// (<c>GuardedDeadline.ObserveAbandoned</c>, the unpack limiter's continuation), and the interop
/// suite has a test that fails when a <c>DidComm.</c> exception reaches
/// <see cref="TaskScheduler.UnobservedTaskException"/>.
/// </summary>
public sealed class PickupClientUnobservedQa2Tests
{
    /// <summary>
    /// <b>Q2-3</b> (#96 design §4.1 step 6 and §3.2 "PendingRequest copies DF's PendingQuery terminal
    /// CAS"; the project's rule that abandoned or failed work is observed, e.g.
    /// <c>GuardedDeadline.ObserveAbandoned</c>; <c>ReturnRouteClientWebSocketTests.Receiver_eviction_ends_the_loop_without_unobserved_exceptions…</c>).
    /// Over HTTP, a response that carries no answer fails the request at once:
    /// <c>pending.TryFail(noAnswer)</c> faults the pending request's <c>TaskCompletionSource</c> and the
    /// exchange then <c>throw</c>s the same exception directly, never awaiting that task. When the task
    /// is collected its exception is raised on <see cref="TaskScheduler.UnobservedTaskException"/> —
    /// once per HTTP request the mediator did not answer (a 202, an answer refused by the identity
    /// rule or one that does not correlate), from <c>GetStatusAsync</c>, <c>FetchAsync</c>,
    /// <c>AckAsync</c>, and the fetch's own acknowledgement alike. In the full interop run this is
    /// what makes the pre-existing <c>Receiver_eviction_ends_the_loop_without_unobserved_exceptions…</c>
    /// test fail intermittently (its filter matches the <c>DidComm.Mediator.Client</c> stack).
    /// </summary>
    [Fact]
    public async Task Q2_3_An_HTTP_request_the_mediator_did_not_answer_leaves_no_unobserved_task_exception()
    {
        await using var world = await ClientWorld.CreateAsync();
        var send = new FakeSend(); // accepted with nothing returned: an HTTP 202
        var seen = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var inner in e.Exception.Flatten().InnerExceptions)
                seen.Enqueue(inner);
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await FailThreeTimesAsync(world, send);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            var ours = send.Calls.Select(c => c.Message.Id).ToArray();
            ours.Should().HaveCount(3);
            seen.Where(e => ours.Any(id => e.Message.Contains(id, StringComparison.Ordinal)))
                .Should().BeEmpty("a request that failed for lack of an answer leaves no faulted task behind");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    /// <summary>
    /// <b>Q2-3</b>, second path: disposing the container while a request is still being sent fails
    /// the pending request (<c>TryFail(Disposed())</c>) and the exchange throws a fresh
    /// <see cref="ObjectDisposedException"/> from the send's catch, never awaiting the faulted
    /// <c>TaskCompletionSource</c> — whose exception then reaches
    /// <see cref="TaskScheduler.UnobservedTaskException"/>.
    /// </summary>
    [Fact]
    public async Task Q2_3_Disposing_while_a_request_is_being_sent_leaves_no_unobserved_task_exception()
    {
        await using var world = await ClientWorld.CreateAsync();
        var seen = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var inner in e.Exception.Flatten().InnerExceptions)
                seen.Enqueue(inner);
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await DisposeDuringSendAsync(world);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            // The disposal's exception stored in the pending request and never thrown (no stack trace).
            seen.OfType<ObjectDisposedException>().Where(e => e.StackTrace is null && e.ObjectName == nameof(MediatorClientChannel))
                .Should().BeEmpty("a request failed by the disposal leaves no faulted task behind");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    // In its own frame, so nothing of the exchange stays reachable from the test method.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task DisposeDuringSendAsync(ClientWorld world)
    {
        var send = new FakeSend
        {
            Respond = (_, _, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith<DidComm.Transports.SendResult>(_ => throw new OperationCanceledException(ct), TaskScheduler.Default),
        };
        var channel = ClientChannels.Create(world, send);
        var client = new PickupClient(channel, logger: null);
        var status = client.GetStatusAsync(world.Mediator.Did, world.Local.Did, serviceEndpointOverride: ClientChannels.Https);
        await send.FirstCall.WaitAsync(PickupTest.Timeout);
        channel.Dispose();
        await FluentActions.Awaiting(() => status.WaitAsync(PickupTest.Timeout)).Should().ThrowAsync<ObjectDisposedException>();
    }

    // In its own frame, so nothing of the exchanges stays reachable from the test method.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task FailThreeTimesAsync(ClientWorld world, FakeSend send)
    {
        var channel = ClientChannels.Create(world, send);
        var client = new PickupClient(channel, logger: null);
        for (var i = 0; i < 3; i++)
        {
            await FluentActions.Awaiting(() => client.GetStatusAsync(world.Mediator.Did, world.Local.Did, serviceEndpointOverride: ClientChannels.Https))
                .Should().ThrowAsync<TimeoutException>();
        }
    }
}
