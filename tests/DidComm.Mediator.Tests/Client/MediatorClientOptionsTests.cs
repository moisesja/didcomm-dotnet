using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Mediator.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="MediatorClientOptions"/>: the defaults and ranges of #96 design §2, the copy the
/// clients keep (L-060), and <c>AddMediatorClient()</c> refusing bad settings at registration. The
/// A0 stub commit's tests of what it implemented outright; engineer A extends them.
/// </summary>
public sealed class MediatorClientOptionsTests
{
    [Fact]
    public void Defaults_are_the_documented_ones_and_valid()
    {
        var options = new MediatorClientOptions();

        options.RequestTimeout.Should().Be(TimeSpan.FromSeconds(30));
        options.MaxPendingRequests.Should().Be(64);
        options.CoordinateMediationVersion.Should().Be(CoordinateMediationVersion.Negotiate);
        options.DedupeCapacity.Should().Be(4096);
        options.LivePollInterval.Should().Be(TimeSpan.FromMinutes(1));
        options.UnpackTimeout.Should().Be(TimeSpan.FromSeconds(30));
        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    public static TheoryData<Action<MediatorClientOptions>> InRange => new()
    {
        o => o.RequestTimeout = TimeSpan.FromTicks(1),
        o => o.RequestTimeout = TimeSpan.FromMinutes(10),
        o => o.MaxPendingRequests = 1,
        o => o.MaxPendingRequests = 4096,
        o => o.CoordinateMediationVersion = CoordinateMediationVersion.V3,
        o => o.CoordinateMediationVersion = CoordinateMediationVersion.V2,
        o => o.DedupeCapacity = 0,
        o => o.DedupeCapacity = 1_000_000,
        o => o.LivePollInterval = TimeSpan.FromSeconds(1),
        o => o.LivePollInterval = TimeSpan.FromHours(24),
        o => o.UnpackTimeout = TimeSpan.FromTicks(1),
        o => o.UnpackTimeout = TimeSpan.FromMinutes(10),
    };

    [Theory]
    [MemberData(nameof(InRange))]
    public void Values_at_the_edges_of_each_range_are_accepted(Action<MediatorClientOptions> set)
    {
        var options = new MediatorClientOptions();
        set(options);

        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    public static TheoryData<Action<MediatorClientOptions>, string> OutOfRange => new()
    {
        { o => o.RequestTimeout = TimeSpan.Zero, nameof(MediatorClientOptions.RequestTimeout) },
        { o => o.RequestTimeout = TimeSpan.FromSeconds(-1), nameof(MediatorClientOptions.RequestTimeout) },
        { o => o.RequestTimeout = Timeout.InfiniteTimeSpan, nameof(MediatorClientOptions.RequestTimeout) },
        { o => o.RequestTimeout = TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(1), nameof(MediatorClientOptions.RequestTimeout) },
        { o => o.MaxPendingRequests = 0, nameof(MediatorClientOptions.MaxPendingRequests) },
        { o => o.MaxPendingRequests = 4097, nameof(MediatorClientOptions.MaxPendingRequests) },
        { o => o.CoordinateMediationVersion = (CoordinateMediationVersion)3, nameof(MediatorClientOptions.CoordinateMediationVersion) },
        { o => o.CoordinateMediationVersion = (CoordinateMediationVersion)(-1), nameof(MediatorClientOptions.CoordinateMediationVersion) },
        { o => o.DedupeCapacity = -1, nameof(MediatorClientOptions.DedupeCapacity) },
        { o => o.DedupeCapacity = 1_000_001, nameof(MediatorClientOptions.DedupeCapacity) },
        { o => o.LivePollInterval = TimeSpan.FromMilliseconds(999), nameof(MediatorClientOptions.LivePollInterval) },
        { o => o.LivePollInterval = TimeSpan.FromHours(24) + TimeSpan.FromTicks(1), nameof(MediatorClientOptions.LivePollInterval) },
        { o => o.UnpackTimeout = TimeSpan.Zero, nameof(MediatorClientOptions.UnpackTimeout) },
        { o => o.UnpackTimeout = TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(1), nameof(MediatorClientOptions.UnpackTimeout) },
    };

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void A_value_outside_its_range_is_refused_naming_the_setting(Action<MediatorClientOptions> set, string setting)
    {
        var options = new MediatorClientOptions();
        set(options);

        options.Invoking(o => o.Validate()).Should().Throw<ArgumentOutOfRangeException>().WithParameterName(setting);
    }

    [Fact]
    public void The_validated_copy_is_independent_of_the_original()
    {
        var options = new MediatorClientOptions { RequestTimeout = TimeSpan.FromSeconds(5), DedupeCapacity = 0 };

        var copy = options.ValidatedCopy();
        options.RequestTimeout = TimeSpan.FromSeconds(9);

        copy.Should().NotBeSameAs(options);
        copy.RequestTimeout.Should().Be(TimeSpan.FromSeconds(5));
        copy.DedupeCapacity.Should().Be(0);
    }

    [Fact]
    public void AddMediatorClient_refuses_a_bad_setting_at_registration_and_registers_nothing()
    {
        var services = new ServiceCollection();
        var before = 0;
        var act = () => services.AddDidComm(b =>
        {
            before = b.Services.Count;
            b.AddMediatorClient(o => o.MaxPendingRequests = 0);
        });

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(nameof(MediatorClientOptions.MaxPendingRequests));
        services.Should().HaveCount(before, "the settings are checked before anything is added");
    }

    [Fact]
    public void AddMediatorClient_refuses_a_null_builder()
    {
        var act = () => MediatorClientBuilderExtensions.AddMediatorClient(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("builder");
    }
}
