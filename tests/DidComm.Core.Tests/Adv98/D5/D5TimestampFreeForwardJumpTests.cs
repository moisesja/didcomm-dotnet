using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Adv98.D5;

/// <summary>
/// #98 red team, D5 hypothesis 3: the #105 "purged-at" refusal closes W-1 (a host clock that jumps
/// forward past an entry's retention and comes back re-admitting a captured request) for
/// timestamped messages only. With <see cref="ReplayProtectionOptions.RequireCreatedTime"/> off,
/// a timestamp-free entry is forgotten after such a jump and the same request is admitted again
/// at the restored time. These tests characterize today's behaviour; the stamped case is the
/// control that shows the jump itself is handled.
/// </summary>
public sealed class D5TimestampFreeForwardJumpTests
{
    private const string Alice = "did:example:alice";
    private static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static InMemoryReplayGuard Guard() => new(new ReplayProtectionOptions { RequireCreatedTime = false });

    [Fact]
    public void A_timestamp_free_request_is_admitted_again_after_a_forward_jump_past_Window_and_back()
    {
        var guard = Guard();
        var window = TimeSpan.FromMinutes(5);

        var first = guard.TryAdmit(Alice, "captured-1", null, T);
        // The host clock jumps forward past the entry's retention; any request at that reading purges.
        var atJump = guard.TryAdmit(Alice, "other", null, T + window + TimeSpan.FromSeconds(1));
        // The clock returns, two seconds after the first admission: the captured request again.
        var replay = guard.TryAdmit(Alice, "captured-1", null, T + TimeSpan.FromSeconds(2));

        first.Should().Be(ReplayAdmission.Admitted);
        atJump.Should().Be(ReplayAdmission.Admitted);
        // Today's behaviour for a timestamp-free entry: the purge at the jumped reading forgot it, so
        // the replay is admitted again 2 s after the original by the restored clock (D5-F2).
        replay.Should().Be(ReplayAdmission.Admitted);
    }

    [Fact]
    public void Control_the_same_jump_refuses_a_stamped_replay()
    {
        var guard = Guard();
        var window = TimeSpan.FromMinutes(5);
        var skew = TimeSpan.FromSeconds(60);
        var created = T.ToUnixTimeSeconds();

        var first = guard.TryAdmit(Alice, "captured-1", created, T);
        var atJump = guard.TryAdmit(Alice, "other", null, T + window + skew + TimeSpan.FromSeconds(1));
        var replay = guard.TryAdmit(Alice, "captured-1", created, T + TimeSpan.FromSeconds(2));

        first.Should().Be(ReplayAdmission.Admitted);
        atJump.Should().Be(ReplayAdmission.Admitted);
        replay.Should().Be(ReplayAdmission.TooOld);
    }

    [Fact]
    public void Without_a_jump_a_timestamp_free_replay_inside_Window_is_a_duplicate()
    {
        var guard = Guard();

        var first = guard.TryAdmit(Alice, "captured-1", null, T);
        var replay = guard.TryAdmit(Alice, "captured-1", null, T + TimeSpan.FromMinutes(5));

        first.Should().Be(ReplayAdmission.Admitted);
        replay.Should().Be(ReplayAdmission.Duplicate);
    }
}
