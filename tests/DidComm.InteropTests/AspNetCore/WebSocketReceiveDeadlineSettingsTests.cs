using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.InteropTests.Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// <c>DidCommReceiveOptions.MessageReceiveTimeout</c> as a setting: its default (60 s), its rule
/// (greater than zero and at most <see cref="int.MaxValue"/> milliseconds, or
/// <see cref="Timeout.InfiniteTimeSpan"/>, checked when it is set), and what each value does on the
/// endpoints: the default and a configured value are the deadline, infinite creates no timer at all,
/// and without a <see cref="TimeProvider"/> in the container the system clock times it.
/// </summary>
[Collection(QaSerialTiming.Name)]
public sealed class WebSocketReceiveDeadlineSettingsTests
{
    private static readonly TimeSpan Guard = DeadlineKestrelHost.Guard;

    public static TheoryData<DeadlineEndpoint> Endpoints => new()
    {
        DeadlineEndpoint.Dispatch,
        DeadlineEndpoint.Callback,
        DeadlineEndpoint.Mediator,
    };

    public static TheoryData<TimeSpan> ValidValues => new()
    {
        TimeSpan.FromTicks(1),
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMilliseconds(int.MaxValue),
        Timeout.InfiniteTimeSpan,
    };

    public static TheoryData<TimeSpan> InvalidValues => new()
    {
        TimeSpan.Zero,
        TimeSpan.FromTicks(-1),
        TimeSpan.FromMilliseconds(-2),
        TimeSpan.FromSeconds(-1),
        TimeSpan.MinValue,
        TimeSpan.FromMilliseconds(int.MaxValue) + TimeSpan.FromTicks(1),
        TimeSpan.MaxValue,
    };

    [Fact]
    public void The_default_is_60_seconds()
        => new DidCommReceiveOptions().MessageReceiveTimeout.Should().Be(TimeSpan.FromSeconds(60));

    [Theory]
    [MemberData(nameof(ValidValues))]
    public void A_value_greater_than_zero_and_at_most_Int32_MaxValue_milliseconds_or_infinite_is_accepted(TimeSpan value)
    {
        var options = new DidCommReceiveOptions { MessageReceiveTimeout = value };
        options.MessageReceiveTimeout.Should().Be(value);
    }

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public void Any_other_value_is_refused_when_it_is_set_and_the_setting_keeps_its_value(TimeSpan value)
    {
        var options = new DidCommReceiveOptions();
        var act = () => options.MessageReceiveTimeout = value;

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(nameof(DidCommReceiveOptions.MessageReceiveTimeout));
        options.MessageReceiveTimeout.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void A_refused_value_set_through_Configure_fails_when_the_options_are_built()
    {
        var services = new ServiceCollection();
        services.AddOptions<DidCommReceiveOptions>().Configure(o => o.MessageReceiveTimeout = TimeSpan.Zero);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DidCommReceiveOptions>>();

        var act = () => options.Value;

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(nameof(DidCommReceiveOptions.MessageReceiveTimeout));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task With_nothing_configured_a_silent_connection_is_closed_with_1008_after_60_seconds(DeadlineEndpoint endpoint)
    {
        await using var host = await DeadlineKestrelHost.StartAsync(o => o.Receive = null);
        var limit = TimeSpan.FromSeconds(60);
        var mark = host.Time.Mark();
        using var ws = await host.ConnectAsync(endpoint);
        await host.Time.WaitForTimerAsync(mark, limit, Guard);
        var fired = host.Time.TimersFired;
        var closing = DeadlinePeer.ReceiveCloseAsync(ws);

        host.Time.Advance(limit - DeadlineKestrelHost.Tick);
        host.Time.TimersFired.Should().Be(fired);
        host.Time.Advance(DeadlineKestrelHost.Tick);
        var (type, status, _) = await closing.WaitAsync(Guard);

        type.Should().Be(WebSocketMessageType.Close);
        status.Should().Be(WebSocketCloseStatus.PolicyViolation);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task With_an_infinite_limit_no_timer_is_ever_created_and_a_silent_connection_and_a_fragmented_message_are_untimed(DeadlineEndpoint endpoint)
    {
        await using var host = await DeadlineKestrelHost.StartAsync(o => o.Receive = r => r.MessageReceiveTimeout = Timeout.InfiniteTimeSpan);
        var first = host.Probe();
        var fragmented = host.Probe();
        var firstBytes = await host.PackAsync(first);
        var fragmentedBytes = await host.PackAsync(fragmented);
        var mark = host.Time.Mark();
        var fired = host.Time.TimersFired;
        using var ws = await host.ConnectAsync(endpoint);

        host.Time.Advance(TimeSpan.FromHours(1));
        await DeadlinePeer.SendAsync(ws, firstBytes, endOfMessage: true);
        await host.WaitDispatchedAsync(endpoint, first.Id);
        await DeadlinePeer.SendAsync(ws, fragmentedBytes.AsMemory(0, 100), endOfMessage: false);
        host.Time.Advance(TimeSpan.FromHours(1));
        await DeadlinePeer.SendAsync(ws, fragmentedBytes.AsMemory(100), endOfMessage: true);
        await host.WaitDispatchedAsync(endpoint, fragmented.Id);

        host.TimersCreatedSince(mark).Should().Be(0, "an infinite limit starts no timer, at the accept or at a message's first part");
        host.Time.TimersFired.Should().Be(fired);
        ws.State.Should().Be(WebSocketState.Open);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Without_a_TimeProvider_in_the_container_the_system_clock_times_the_limit()
    {
        await using var host = await DeadlineKestrelHost.StartAsync(o =>
        {
            o.RegisterClock = false;
            o.Receive = r => r.MessageReceiveTimeout = TimeSpan.FromMilliseconds(500);
        });
        using var ws = await host.ConnectAsync(DeadlineEndpoint.Dispatch);

        // Nothing moves the manual clock: only the system clock can end this connection.
        var (type, status, _) = await DeadlinePeer.ReceiveCloseAsync(ws).WaitAsync(Guard);

        type.Should().Be(WebSocketMessageType.Close);
        status.Should().Be(WebSocketCloseStatus.PolicyViolation);
        host.Time.Mark().Should().Be(0, "the manual clock is not the container's, so it never made a timer");
    }
}
