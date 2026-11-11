using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Transports.ReturnRoute;

/// <summary>Validation of <see cref="ReturnRouteSessionOptions"/> and <see cref="ReplayProtectionOptions"/>: each error names the property only.</summary>
public sealed class ReturnRouteOptionsTests
{
    [Fact]
    public void Session_defaults_validate()
    {
        var o = new ReturnRouteSessionOptions();
        o.MaxSessionsPerPeer.Should().Be(8);
        o.MaxSessions.Should().Be(4096);
        o.MaxPendingPushes.Should().Be(16);
        var act = () => o.Validate();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0, 10, 1, "MaxSessionsPerPeer")]
    [InlineData(-1, 10, 1, "MaxSessionsPerPeer")]
    [InlineData(1, 0, 1, "MaxSessions")]
    [InlineData(1, 1, 0, "MaxPendingPushes")]
    [InlineData(5, 4, 1, "MaxSessionsPerPeer")]
    public void Session_bounds_are_validated_naming_the_property(int perPeer, int total, int pushes, string property)
    {
        var o = new ReturnRouteSessionOptions { MaxSessionsPerPeer = perPeer, MaxSessions = total, MaxPendingPushes = pushes };
        var act = () => o.Validate();
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(property);
    }

    [Fact]
    public void Session_options_are_validated_by_the_registry_ctor()
    {
        var act = () => new InMemoryReturnRouteSessionRegistry(new ReturnRouteSessionOptions { MaxSessions = 0 });
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("MaxSessions");
    }

    [Fact]
    public void Replay_defaults_validate()
    {
        var o = new ReplayProtectionOptions();
        o.Window.Should().Be(TimeSpan.FromMinutes(5));
        o.RequireCreatedTime.Should().BeTrue();
        o.MaxEntries.Should().Be(32_768);
        o.MaxEntriesPerPeer.Should().Be(1024);
        o.MaxMessageIdLength.Should().Be(512);
        var act = () => o.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Replay_window_must_be_positive_and_bounded()
    {
        foreach (var window in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds((double)int.MaxValue + 1) })
        {
            var o = new ReplayProtectionOptions { Window = window };
            var act = () => o.Validate();
            act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Window");
        }
    }

    [Theory]
    [InlineData(0, 1, 1, "MaxEntries")]
    [InlineData(10, 0, 1, "MaxEntriesPerPeer")]
    [InlineData(10, 11, 1, "MaxEntriesPerPeer")]
    [InlineData(10, 1, 0, "MaxMessageIdLength")]
    public void Replay_budgets_are_validated_naming_the_property(int max, int perPeer, int idLength, string property)
    {
        var o = new ReplayProtectionOptions { MaxEntries = max, MaxEntriesPerPeer = perPeer, MaxMessageIdLength = idLength };
        var act = () => o.Validate();
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(property);
    }

    [Fact]
    public void Replay_options_are_validated_by_the_guard_ctor()
    {
        var act = () => new InMemoryReplayGuard(new ReplayProtectionOptions { MaxEntries = 0 }, new DidComm.Facade.DidCommOptions());
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("MaxEntries");
    }
}
