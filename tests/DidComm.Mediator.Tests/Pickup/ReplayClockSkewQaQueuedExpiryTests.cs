using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static DidComm.Mediator.Pickup.PickupDeliveryOutcome;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// #105 QA pass, kept as regressions: queued-payload expiry (FR-ROUTE-09, <c>PickupCoordinator.ExpiryInstant</c> and
/// <c>MessagePickupQueue.DeliverAsync</c>) follows <c>DidCommOptions.ExpiresClockSkew</c> and never
/// <c>ReplayProtectionOptions.ClockSkew</c>, at queue time and while queued. Each case first proves the
/// replay skew it set is live in the same container (the guard admits exactly up to it).
/// </summary>
public sealed class ReplayClockSkewQaQueuedExpiryTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 3600)]
    [InlineData(30, 0)]
    [InlineData(30, 3600)]
    public async Task Queued_payload_expiry_follows_ExpiresClockSkew_whatever_ClockSkew(int expiresSkewSeconds, int replaySkewSeconds)
    {
        await using var fixture = PickupFixture.Create(
            didComm: o => o.ExpiresClockSkew = TimeSpan.FromSeconds(expiresSkewSeconds),
            services: s => s.AddOptions<ReplayProtectionOptions>().Configure(o => o.ClockSkew = TimeSpan.FromSeconds(replaySkewSeconds)));
        var start = fixture.Clock.Now;
        var now = start.ToUnixTimeSeconds();
        var guard = fixture.Host.Services.GetRequiredService<IInboundReplayGuard>();
        guard.TryAdmit("did:example:qa", "at-skew", now + replaySkewSeconds, start).Should().Be(ReplayAdmission.Admitted, "the replay skew reached this container");
        guard.TryAdmit("did:example:qa", "past-skew", now + replaySkewSeconds + 1, start).Should().Be(ReplayAdmission.FutureDated);
        var (mediator, peer) = await fixture.EnrolledAsync();

        // At queue time: expired once now − ExpiresClockSkew > expires_time.
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(32, 1), new PickupDeliveryOptions(ExpiresTime: now - expiresSkewSeconds))).Outcome.Should().Be(Queued);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(32, 2), new PickupDeliveryOptions(ExpiresTime: now - expiresSkewSeconds - 1))).Outcome.Should().Be(Expired);

        // While queued: good until the whole second expires + ExpiresClockSkew, gone one second later.
        var shortLived = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(50, 3), new PickupDeliveryOptions(ExpiresTime: now + 10));
        shortLived.Outcome.Should().Be(Queued);

        fixture.Clock.Now = start + TimeSpan.FromSeconds(10 + expiresSkewSeconds) + TimeSpan.FromMilliseconds(999);
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator)).MessageCount.Should().Be(1, "the short-lived message is still good; the queue-time one expired");

        fixture.Clock.Now = start + TimeSpan.FromSeconds(10 + expiresSkewSeconds + 1);
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator)).MessageCount.Should().Be(0);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().BeEmpty();
    }
}
