using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.InteropTests.Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// <c>DidCommReceiveOptions.MessageReceiveTimeout</c> read from the host's configuration (the
/// <c>appsettings.json</c> way: <c>AddOptions&lt;DidCommReceiveOptions&gt;().Bind(section)</c>), on all
/// three WebSocket endpoints, the mediator's included: a time-span string is the limit, the string
/// of <see cref="Timeout.InfiniteTimeSpan"/> turns it off, and a refused value fails when the options
/// are first used. Also how the endpoints answer a peer that closes in the middle of a message: a
/// normal close, never the time limit's 1008.
/// </summary>
/// <remarks>
/// Same Kestrel host and manual clock as <see cref="WebSocketReceiveDeadlineTests"/>: before every
/// clock move the test waits for the specific deadline timer, then asserts how many timers the move
/// fired. Real time is only a hang guard.
/// </remarks>
[Collection(QaSerialTiming.Name)]
public sealed class WebSocketReceiveDeadlineBindingTests
{
    private const string Section = "DidComm:Receive";
    private static readonly TimeSpan Tick = DeadlineKestrelHost.Tick;
    private static readonly TimeSpan Guard = DeadlineKestrelHost.Guard;

    public static TheoryData<DeadlineEndpoint> Endpoints => new()
    {
        DeadlineEndpoint.Dispatch,
        DeadlineEndpoint.Callback,
        DeadlineEndpoint.Mediator,
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_limit_bound_from_configuration_closes_a_silent_connection_with_1008_at_that_limit(DeadlineEndpoint endpoint)
    {
        var limit = TimeSpan.FromSeconds(37);
        await using var host = await DeadlineKestrelHost.StartAsync(o => BindFromConfiguration(o, "00:00:37"));
        var mark = host.Time.Mark();
        using var ws = await host.ConnectAsync(endpoint);
        await host.Time.WaitForTimerAsync(mark, limit, Guard); // the configured value, not the 60 s default
        var fired = host.Time.TimersFired;
        var closing = DeadlinePeer.ReceiveCloseAsync(ws);

        host.Time.Advance(limit - Tick);
        host.Time.TimersFired.Should().Be(fired, "nothing is due before the configured limit");
        host.Time.Advance(Tick);
        host.Time.TimersFired.Should().Be(fired + 1, "the deadline bound from configuration fired");
        var (type, status, reason) = await closing.WaitAsync(Guard);

        type.Should().Be(WebSocketMessageType.Close);
        status.Should().Be(WebSocketCloseStatus.PolicyViolation);
        reason.Should().Be("message receive timeout");
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task The_infinite_value_bound_from_configuration_starts_no_timer_for_a_silent_connection_or_a_fragmented_message(DeadlineEndpoint endpoint)
    {
        // TimeSpan's text for Timeout.InfiniteTimeSpan (minus one millisecond).
        await using var host = await DeadlineKestrelHost.StartAsync(o => BindFromConfiguration(o, Timeout.InfiniteTimeSpan.ToString()));
        Timeout.InfiniteTimeSpan.ToString().Should().Be("-00:00:00.0010000", "precondition: the string a host writes in its configuration");
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

        host.TimersCreatedSince(mark).Should().Be(0, "the bound infinite value starts no timer, at the accept or at a message's first part");
        host.Time.TimersFired.Should().Be(fired);
        ws.State.Should().Be(WebSocketState.Open);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("25.00:00:00")]
    public void A_refused_value_bound_from_configuration_fails_when_the_options_are_first_used(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [Section + ":MessageReceiveTimeout"] = value })
            .Build();
        var services = new ServiceCollection();
        services.AddOptions<DidCommReceiveOptions>().Bind(configuration.GetSection(Section));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DidCommReceiveOptions>>();

        var thrown = Record.Exception(() => options.Value);

        thrown.Should().NotBeNull("a value the setter refuses is never silently replaced by a default");
        var refusal = Chain(thrown!).OfType<ArgumentOutOfRangeException>().FirstOrDefault();
        refusal.Should().NotBeNull("the setter's refusal is what fails the binding ({0})", thrown!.GetType().Name);
        refusal!.ParamName.Should().Be(nameof(DidCommReceiveOptions.MessageReceiveTimeout));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_close_frame_in_the_middle_of_a_message_is_answered_with_a_normal_close_and_stops_that_messages_timer(DeadlineEndpoint endpoint)
    {
        await using var host = await DeadlineKestrelHost.StartAsync();
        await using var peer = await host.ConnectRawAsync(endpoint);
        var first = host.Probe();
        await peer.SendFrameAsync(RawFrame.Text, fin: true, await host.PackAsync(first));
        await host.WaitDispatchedAsync(endpoint, first.Id);

        var mark = host.Time.Mark();
        var fired = host.Time.TimersFired;
        await peer.SendFrameAsync(RawFrame.Text, fin: false, "{\"id\":"u8.ToArray());
        await peer.PingAsync("first part");
        await host.Time.WaitForTimerAsync(mark, DeadlineKestrelHost.Limit, Guard); // the message's time runs

        await peer.SendFrameAsync(RawFrame.Close, fin: true, new byte[] { 0x03, 0xE8 }); // 1000, no reason
        var close = await peer.ReadCloseAsync(Guard);

        close.Should().NotBeNull("the server answers the peer's close");
        close!.CloseCode.Should().Be((int)WebSocketCloseStatus.NormalClosure, "a peer that closes mid-message is answered normally, not as a timed-out message");
        host.Time.ScheduledTimersSince(mark).Should().Be(0, "the message's timer stopped before the server answered the close");
        host.Time.Advance(DeadlineKestrelHost.Limit);
        host.Time.TimersFired.Should().Be(fired, "nothing is left to fire");
        host.TimeoutLines().Should().BeEmpty();
        host.DispatchedCount(endpoint).Should().Be(1, "the unfinished message is dropped");
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>No code-level setting: <c>MessageReceiveTimeout</c> comes from the host's configuration as <paramref name="value"/>.</summary>
    private static void BindFromConfiguration(DeadlineKestrelHostOptions options, string value)
    {
        options.Receive = null;
        options.Builder = b =>
        {
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [Section + ":MessageReceiveTimeout"] = value });
            b.Services.AddOptions<DidCommReceiveOptions>().Bind(b.Configuration.GetSection(Section));
        };
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
            yield return e;
    }
}
