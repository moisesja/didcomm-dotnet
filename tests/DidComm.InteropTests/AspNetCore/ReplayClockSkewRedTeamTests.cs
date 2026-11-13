using System.Net;
using AwesomeAssertions;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #105 red-team finding RT-1 at the endpoint, kept as a regression: <c>InboundReplayGate</c> reads
/// <c>DidCommOptions.Now()</c> before the guard takes its lock, so a request can carry an older `now`
/// into the lock than one that already purged. Before the fix a captured request then re-executed its
/// handler (on <c>main</c> too). The guard decorator below only parks the replay between "clock read"
/// and "lock taken" — where a pre-empted or lock-contended thread already sits.
/// </summary>
public sealed class ReplayClockSkewRedTeamTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(0, 1)]    // ClockSkew 0 (main's default): a one-second inversion
    [InlineData(60, 61)]  // the new default: a 61-second inversion
    public async Task A_replay_whose_clock_read_precedes_another_requests_purge_does_not_run_the_handler_again(int skewSeconds, int inversionSeconds)
    {
        var parking = new ParkingReplayGuard(new InMemoryReplayGuard(new ReplayProtectionOptions { ClockSkew = TimeSpan.FromSeconds(skewSeconds) }));
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
            o.ConfigureServices = services => services.AddSingleton<IInboundReplayGuard>(parking));

        var original = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var captured = await host.AuthcryptAsync(host.Alice, original);
        (await host.PostAsync(captured)).Status.Should().Be(HttpStatusCode.Accepted);
        host.Recorder.Invocations.Should().Be(1, "the original delivery");

        // The replay is presented at created + Window (still inside the window) and parks after its clock read.
        host.Clock.Advance(TimeSpan.FromSeconds(300));
        parking.ParkNext(original.Id);
        var replay = host.PostAsync(captured);
        parking.Arrived.Wait(Timeout).Should().BeTrue();

        // Meanwhile another in-scope request reads a later clock and purges the original's entry.
        host.Clock.Advance(TimeSpan.FromSeconds(inversionSeconds));
        var other = host.Stamped(host.RecordingMessage(host.Carol).WithReturnRoute(ReturnRouteMode.All)).Build();
        (await host.PostAsync(await host.AuthcryptAsync(host.Carol, other))).Status.Should().Be(HttpStatusCode.Accepted);
        host.Recorder.Invocations.Should().Be(2);

        parking.Release.Set();
        (await replay).Status.Should().Be(HttpStatusCode.Accepted);

        parking.Answers.Should().Equal(ReplayAdmission.TooOld);
        host.Recorder.Seen.Count(r => r.Message.Id == original.Id)
            .Should().Be(1, "a captured request never re-executes its handler");
        host.DropLines(original.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.TooOld));
        host.LeakingLogLines().Should().BeEmpty();
    }

    /// <summary>Forwards to the real guard; holds one designated call after its `now` was read.</summary>
    private sealed class ParkingReplayGuard : IInboundReplayGuard
    {
        private readonly IInboundReplayGuard _inner;
        private string? _parkId;

        public ParkingReplayGuard(IInboundReplayGuard inner) => _inner = inner;

        public ManualResetEventSlim Arrived { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public List<ReplayAdmission> Answers { get; } = new();

        public void ParkNext(string messageId) => Volatile.Write(ref _parkId, messageId);

        public ReplayAdmission TryAdmit(string peerDidSubject, string messageId, long? createdTimeEpochSeconds, DateTimeOffset now)
        {
            if (Interlocked.CompareExchange(ref _parkId, null, messageId) == messageId)
            {
                Arrived.Set();
                Release.Wait(Timeout);
                var answer = _inner.TryAdmit(peerDidSubject, messageId, createdTimeEpochSeconds, now);
                lock (Answers)
                    Answers.Add(answer);
                return answer;
            }
            return _inner.TryAdmit(peerDidSubject, messageId, createdTimeEpochSeconds, now);
        }
    }
}
