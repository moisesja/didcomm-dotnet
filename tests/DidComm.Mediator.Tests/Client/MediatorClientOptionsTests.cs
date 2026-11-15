using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        options.UnpackTimeout.Should().Be(TimeSpan.FromSeconds(20));
        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Fact]
    public void The_default_delivery_budget_is_well_inside_this_librarys_default_mediator_lease()
    {
        // Owner decision 2-A: the messages a fetch gives up on must still be set aside when the next
        // fetch comes, so the budget is a third of the lease, leaving room for the request and the
        // acknowledgement (each bounded by RequestTimeout, which a responsive mediator never needs).
        var lease = new DidComm.Mediator.Pickup.MessagePickupOptions().LeaseTimeout;

        new MediatorClientOptions().UnpackTimeout.Should().BeLessThanOrEqualTo(lease / 3);
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

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void The_non_DI_clients_refuse_a_bad_setting(Action<MediatorClientOptions> set, string setting)
    {
        using var sp = Container();
        var client = sp.GetRequiredService<DidCommClient>();
        var options = new MediatorClientOptions();
        set(options);

        FluentActions.Invoking(() => new MediationClient(client, options)).Should().Throw<ArgumentOutOfRangeException>().WithParameterName(setting);
        FluentActions.Invoking(() => new PickupClient(client, options)).Should().Throw<ArgumentOutOfRangeException>().WithParameterName(setting);
    }

    [Fact]
    public void The_non_DI_clients_use_the_defaults_without_options()
    {
        using var sp = Container();

        var channel = (MediatorClientChannel)new PickupClient(sp.GetRequiredService<DidCommClient>()).Exchange;

        channel.Options.RequestTimeout.Should().Be(TimeSpan.FromSeconds(30));
        channel.Duplicates.Capacity.Should().Be(4096);
        channel.Unpacker.Budget.Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void The_channel_keeps_a_copy_so_later_changes_change_nothing()
    {
        using var sp = Container();
        var options = new MediatorClientOptions { DedupeCapacity = 7, UnpackTimeout = TimeSpan.FromSeconds(3), MaxPendingRequests = 2 };

        var channel = new MediatorClientChannel(sp.GetRequiredService<DidCommClient>(), options, receivesInbound: false, TimeProvider.System, loggerFactory: null);
        options.DedupeCapacity = 0;
        options.UnpackTimeout = TimeSpan.FromSeconds(9);
        options.MaxPendingRequests = 4000;

        channel.Options.Should().NotBeSameAs(options);
        channel.Options.MaxPendingRequests.Should().Be(2);
        channel.Duplicates.Capacity.Should().Be(7);
        channel.Unpacker.Budget.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void The_container_settings_are_copied_when_the_channel_is_built()
    {
        using var sp = Container(b => b.AddMediatorClient(o => o.LivePollInterval = TimeSpan.FromSeconds(5)));
        var channel = sp.GetRequiredService<MediatorClientChannel>();

        sp.GetRequiredService<IOptions<MediatorClientOptions>>().Value.LivePollInterval = TimeSpan.FromHours(2);

        channel.Options.LivePollInterval.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_delegate_runs_once_at_registration_and_once_when_the_container_builds_the_settings()
    {
        var runs = 0;
        using var sp = Container(b => b.AddMediatorClient(o =>
        {
            runs++;
            o.DedupeCapacity = 0;
        }));
        runs.Should().Be(1, "the probe at registration");

        sp.GetRequiredService<MediatorClientChannel>().Options.DedupeCapacity.Should().Be(0);

        runs.Should().Be(2);
    }

    [Fact]
    public void A_delegate_that_throws_at_registration_propagates_and_registers_nothing()
    {
        var services = new ServiceCollection();
        var before = 0;
        var act = () => services.AddDidComm(b =>
        {
            before = b.Services.Count;
            b.AddMediatorClient(_ => throw new FormatException("bad settings"));
        });

        act.Should().Throw<FormatException>();
        services.Should().HaveCount(before);
    }

    [Fact]
    public void Settings_that_only_fail_together_with_a_later_call_fail_at_that_call()
    {
        var services = new ServiceCollection();
        var act = () => services.AddDidComm(b => b
            .AddMediatorClient(o => o.RequestTimeout = TimeSpan.FromSeconds(5))
            .AddMediatorClient(o => o.UnpackTimeout = TimeSpan.Zero));

        // Each call validates its own delegate on a fresh probe; the second one is out of range by itself.
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(nameof(MediatorClientOptions.UnpackTimeout));
    }

    private static ServiceProvider Container(Action<DidCommBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            configure?.Invoke(b);
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddMediatorClient_refuses_a_null_builder()
    {
        var act = () => MediatorClientBuilderExtensions.AddMediatorClient(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("builder");
    }
}
