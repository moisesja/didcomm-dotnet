using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Protocols.Routing;
using DidComm.Transports.ReturnRoute;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary><see cref="MediatorOptions"/>: defaults, the pipeline ranges, the cross-role rule, and the projection views (JC-8).</summary>
public sealed class MediatorOptionsTests
{
    [Fact]
    public void Defaults_are_the_documented_values_and_pass_validation()
    {
        var options = new MediatorOptions();

        options.RewrapMode.Should().Be(RewrapMode.PassThrough);
        options.MaxForwardDelay.Should().Be(TimeSpan.FromHours(1));
        options.PurgeInterval.Should().Be(TimeSpan.FromMinutes(5));
        options.DirectPush.Should().BeFalse();
        options.DirectPushConcurrency.Should().Be(4);
        options.DirectPushTimeout.Should().Be(TimeSpan.FromSeconds(30));
        options.Pickup.MaxMessageAge.Should().Be(TimeSpan.FromDays(30));
        options.CoordinateMediation.RoutingDids.Should().BeEmpty();
        options.CoordinateMediation.MaxGrants.Should().Be(1_000);
        options.CoordinateMediation.MaxRecipients.Should().Be(10_000);
        options.Replay.ClockSkew.Should().Be(TimeSpan.FromSeconds(60));

        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    public static TheoryData<string, Action<MediatorOptions>> OutOfRange => new()
    {
        { nameof(MediatorOptions.MaxForwardDelay), o => o.MaxForwardDelay = TimeSpan.FromTicks(-1) },
        { nameof(MediatorOptions.MaxForwardDelay), o => o.MaxForwardDelay = TimeSpan.FromMilliseconds(int.MaxValue) + TimeSpan.FromTicks(1) },
        { nameof(MediatorOptions.PurgeInterval), o => o.PurgeInterval = TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1) },
        { nameof(MediatorOptions.PurgeInterval), o => o.PurgeInterval = TimeSpan.FromHours(24) + TimeSpan.FromTicks(1) },
        { nameof(MediatorOptions.DirectPushConcurrency), o => o.DirectPushConcurrency = 0 },
        { nameof(MediatorOptions.DirectPushConcurrency), o => o.DirectPushConcurrency = 65 },
        { nameof(MediatorOptions.DirectPushTimeout), o => o.DirectPushTimeout = TimeSpan.Zero },
        { nameof(MediatorOptions.DirectPushTimeout), o => o.DirectPushTimeout = TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1) },
        { nameof(MediatorOptions.RewrapMode), o => o.RewrapMode = (RewrapMode)42 },
    };

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void A_pipeline_setting_out_of_range_is_refused_by_name(string setting, Action<MediatorOptions> configure)
    {
        var options = new MediatorOptions { Pickup = { MaxMessageAge = null } };
        configure(options);

        options.Invoking(o => o.Validate()).Should().Throw<ArgumentException>().Which.ParamName.Should().Be(setting);
    }

    public static TheoryData<Action<MediatorOptions>> AtTheBounds => new()
    {
        o => o.MaxForwardDelay = TimeSpan.Zero,
        o => { o.MaxForwardDelay = TimeSpan.FromMilliseconds(int.MaxValue); o.Pickup.MaxMessageAge = null; },
        o => o.PurgeInterval = TimeSpan.FromSeconds(1),
        o => o.PurgeInterval = TimeSpan.FromHours(24),
        o => o.DirectPushConcurrency = 1,
        o => o.DirectPushConcurrency = 64,
        o => o.DirectPushTimeout = TimeSpan.FromTicks(1),
        o => o.DirectPushTimeout = TimeSpan.FromMinutes(5),
        o => o.RewrapMode = RewrapMode.ReanoncryptToNext,
    };

    [Theory]
    [MemberData(nameof(AtTheBounds))]
    public void Values_at_the_bounds_are_accepted(Action<MediatorOptions> configure)
    {
        var options = new MediatorOptions();
        configure(options);

        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(59, 60)]
    public void A_mailbox_age_limit_not_longer_than_the_delay_cap_is_refused(int ageMinutes, int delayMinutes)
    {
        var options = new MediatorOptions
        {
            MaxForwardDelay = TimeSpan.FromMinutes(delayMinutes),
            Pickup = { MaxMessageAge = TimeSpan.FromMinutes(ageMinutes) },
        };

        var refusal = options.Invoking(o => o.Validate()).Should().Throw<ArgumentException>().Which;
        refusal.ParamName.Should().Be("Pickup.MaxMessageAge");
        refusal.Message.Should().NotContain(ageMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":");
    }

    [Fact]
    public void A_mailbox_age_limit_one_tick_longer_than_the_delay_cap_or_off_is_accepted()
    {
        new MediatorOptions { MaxForwardDelay = TimeSpan.FromHours(1), Pickup = { MaxMessageAge = TimeSpan.FromHours(1) + TimeSpan.FromTicks(1) } }
            .Invoking(o => o.Validate()).Should().NotThrow();
        new MediatorOptions { MaxForwardDelay = TimeSpan.FromDays(20), Pickup = { MaxMessageAge = null } }
            .Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Fact]
    public void Each_role_reports_its_own_settings_with_its_own_exception_type()
    {
        new MediatorOptions { CoordinateMediation = { MaxGrants = 0 } }.Invoking(o => o.Validate()).Should().Throw<ArgumentException>();
        new MediatorOptions { Pickup = { MaxQueueMessages = 0 } }.Invoking(o => o.Validate()).Should().Throw<ArgumentException>();
        new MediatorOptions { Replay = { MaxEntries = 0 } }.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>();
        new MediatorOptions { Sessions = { MaxSessions = 0 } }.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_routing_did_that_is_not_a_bare_did_is_refused_without_quoting_it()
    {
        const string didUrl = "did:example:mediator-secret#key-1";
        var options = new MediatorOptions { CoordinateMediation = { RoutingDids = { didUrl } } };

        options.Invoking(o => o.Validate()).Should().Throw<ArgumentException>().Which.Message.Should().NotContain("mediator-secret");
    }

    [Fact]
    public void A_projecting_view_writes_through_to_the_given_instance_only()
    {
        var pickup = new MessagePickupOptions();
        var replay = new ReplayProtectionOptions();
        Action<MediatorOptions> configure = o =>
        {
            o.Pickup.MaxQueueMessages = 7;
            o.Replay.MaxEntries = 99;
            o.CoordinateMediation.RoutingDids.Add("did:example:m");
        };

        configure(MediatorOptions.Projecting(pickup: pickup));

        pickup.MaxQueueMessages.Should().Be(7);
        replay.MaxEntries.Should().Be(new ReplayProtectionOptions().MaxEntries, "only the slot given is the real instance; the others are scratch");

        configure(MediatorOptions.Projecting(replay: replay));

        replay.MaxEntries.Should().Be(99);
        pickup.MaxQueueMessages.Should().Be(7);
    }

    [Fact]
    public void A_projecting_view_leaves_untouched_properties_as_they_were()
    {
        var replay = new ReplayProtectionOptions { Window = TimeSpan.FromMinutes(10), MaxEntries = 1_000 };

        ((Action<MediatorOptions>)(o => o.Replay.MaxEntries = 2_000))(MediatorOptions.Projecting(replay: replay));

        replay.Window.Should().Be(TimeSpan.FromMinutes(10), "the delegate did not assign it, so an earlier host setting survives");
        replay.MaxEntries.Should().Be(2_000);
    }

    [Fact]
    public void The_effective_view_joins_the_role_options_in_effect_with_the_pipeline_settings()
    {
        var pipeline = new MediatorOptions
        {
            RewrapMode = RewrapMode.ReanoncryptToNext,
            MaxForwardDelay = TimeSpan.FromMinutes(3),
            PurgeInterval = TimeSpan.FromSeconds(7),
            DirectPush = true,
            DirectPushConcurrency = 9,
            DirectPushTimeout = TimeSpan.FromSeconds(11),
        };
        var cm = new CoordinateMediationOptions();
        var pickup = new MessagePickupOptions { MaxMessageAge = TimeSpan.FromMinutes(2) };
        var replay = new ReplayProtectionOptions();
        var sessions = new ReturnRouteSessionOptions();

        var effective = MediatorOptions.Effective(pipeline, cm, pickup, replay, sessions);

        effective.CoordinateMediation.Should().BeSameAs(cm);
        effective.Pickup.Should().BeSameAs(pickup);
        effective.Replay.Should().BeSameAs(replay);
        effective.Sessions.Should().BeSameAs(sessions);
        effective.RewrapMode.Should().Be(RewrapMode.ReanoncryptToNext);
        effective.MaxForwardDelay.Should().Be(TimeSpan.FromMinutes(3));
        effective.PurgeInterval.Should().Be(TimeSpan.FromSeconds(7));
        effective.DirectPush.Should().BeTrue();
        effective.DirectPushConcurrency.Should().Be(9);
        effective.DirectPushTimeout.Should().Be(TimeSpan.FromSeconds(11));
        effective.Invoking(o => o.Validate()).Should().Throw<ArgumentException>().Which.ParamName.Should().Be("Pickup.MaxMessageAge",
            "the cross-role rule is checked on the values in effect");
    }
}
