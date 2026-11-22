using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.Messages;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// <c>WebSocketTransportOptions.MessageReceiveTimeout</c> on a transport registered with
/// <c>UseWebSocketTransport</c>, the way a host configures it: not set (the 60 s default), bound from
/// configuration as a time-span string, or bound as the string of <see cref="Timeout.InfiniteTimeSpan"/>
/// (no limit). The limit is timed on the container's clock (a hand-moved one here): before each move
/// the test waits for the specific timer, then asserts how many timers the move fired.
/// </summary>
public sealed class WebSocketSessionReceiveDeadlineBindingTests
{
    private const string Section = "DidComm:WebSocket";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);
    private static readonly Uri Endpoint = new("ws://agents.r.us/ws");

    public static TheoryData<string?, TimeSpan> FiniteLimits => new()
    {
        { null, TimeSpan.FromSeconds(60) },          // nothing configured: the default
        { "00:00:37", TimeSpan.FromSeconds(37) },    // bound from configuration
    };

    [Theory]
    [MemberData(nameof(FiniteLimits))]
    public async Task A_stalled_push_is_closed_with_1008_at_the_default_or_configured_limit_on_the_containers_clock(string? configured, TimeSpan limit)
    {
        await using var t = Registered.Create(configured);
        var socket = await t.OpenSessionAsync();

        var mark = t.Clock.Mark();
        socket.PushPart(Encoding.UTF8.GetBytes("first"), endOfMessage: false);
        await t.Clock.WaitForTimerAsync(mark, limit, Guard);
        t.Clock.Advance(limit - Tick);
        t.Clock.TimersFired.Should().Be(0, "nothing is due before the limit");
        socket.CloseOutputStatus.Should().BeNull();
        t.Clock.Advance(Tick);
        t.Clock.TimersFired.Should().Be(1, "the message's deadline fired at the limit");

        (await ReturnRouteTestHost.WaitUntilAsync(() => socket.CloseOutputStatus is not null, Guard)).Should().BeTrue("the transport closes the stalled session socket");
        socket.CloseOutputStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
        socket.CloseOutputDescription.Should().Be("message receive timeout");
    }

    [Fact]
    public async Task The_infinite_value_bound_from_configuration_starts_no_timer_and_a_push_in_parts_is_read_whole()
    {
        await using var t = Registered.Create(Timeout.InfiniteTimeSpan.ToString());
        Timeout.InfiniteTimeSpan.ToString().Should().Be("-00:00:00.0010000", "precondition: the string a host writes in its configuration");
        var socket = await t.OpenSessionAsync();

        var mark = t.Clock.Mark();
        socket.PushPart(Encoding.UTF8.GetBytes("first "), endOfMessage: false);
        socket.PushPart(Encoding.UTF8.GetBytes("and last"), endOfMessage: true);
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.InboundMessagesDropped == 1, Guard)).Should().BeTrue("the reader read the whole message");

        t.Clock.Mark().Should().Be(mark, "no limit, no timer");
        t.Clock.Advance(TimeSpan.FromDays(365));
        t.Clock.TimersFired.Should().Be(0);
        socket.CloseOutputStatus.Should().BeNull("the socket is never closed for time");
        t.Transport.ActiveSessionSocketCount.Should().Be(1);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("25.00:00:00")]
    public void A_refused_value_bound_from_configuration_fails_when_the_transport_is_built(string value)
    {
        var configuration = Configuration(value);
        var services = new ServiceCollection();
        services.AddOptions<WebSocketTransportOptions>().Bind(configuration.GetSection(Section));
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.UseWebSocketTransport();
        });
        using var provider = services.BuildServiceProvider();

        var thrown = Record.Exception(() => provider.GetRequiredService<WebSocketDidCommTransport>());

        thrown.Should().NotBeNull("a value the setter refuses is never silently replaced by a default");
        var refusal = Chain(thrown!).OfType<ArgumentOutOfRangeException>().FirstOrDefault();
        refusal.Should().NotBeNull("the setter's refusal is what fails the binding ({0})", thrown!.GetType().Name);
        refusal!.ParamName.Should().Be(nameof(WebSocketTransportOptions.MessageReceiveTimeout));
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static IConfiguration Configuration(string? messageReceiveTimeout)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(messageReceiveTimeout is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { [Section + ":MessageReceiveTimeout"] = messageReceiveTimeout })
            .Build();

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
            yield return e;
    }

    /// <summary>
    /// A container with the manual clock as its <see cref="TimeProvider"/>, the transport's options
    /// bound from configuration (when a value is given), and <c>UseWebSocketTransport</c> over scripted
    /// sockets that never answer a close. ReceiveInbound is off: pushed messages are read and counted.
    /// </summary>
    private sealed class Registered : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private Registered(ServiceProvider provider, ScriptedSocketFactory sockets, QaManualTimeProvider clock)
        {
            _provider = provider;
            Sockets = sockets;
            Clock = clock;
            Transport = provider.GetRequiredService<WebSocketDidCommTransport>();
        }

        public QaManualTimeProvider Clock { get; }

        public ScriptedSocketFactory Sockets { get; }

        public WebSocketDidCommTransport Transport { get; }

        public static Registered Create(string? configured)
        {
            var clock = new QaManualTimeProvider();
            var sockets = new ScriptedSocketFactory { OnCreate = s => s.AnswerCloseOutput = false };
            var configuration = Configuration(configured);
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(clock);
            services.AddOptions<WebSocketTransportOptions>().Bind(configuration.GetSection(Section));
            services.AddDidComm(b =>
            {
                b.UseNetDidResolver();
                b.UseSecretsResolver(new InMemorySecretsResolver());
                b.UseWebSocketTransport(o =>
                {
                    o.AllowedSchemes = new[] { "ws" };
                    o.MaxReconnectAttempts = 0;
                    o.WebSocketFactory = sockets.Create;
                    o.Connect = (_, _, _) => Task.CompletedTask;
                });
            });
            return new Registered(services.BuildServiceProvider(), sockets, clock);
        }

        /// <summary>Open the session socket with an <c>all</c> send and return its scripted socket.</summary>
        public async Task<ScriptedSessionWebSocket> OpenSessionAsync()
        {
            var request = new TransportRequest(Endpoint, Encoding.UTF8.GetBytes("open"), MediaTypes.Encrypted)
            {
                ReturnRoute = ReturnRouteMode.All,
                LocalDid = "did:example:alice",
                PeerDid = "did:example:bob",
            };
            await Transport.SendAsync(request, default).WaitAsync(Guard);
            Transport.ActiveSessionSocketCount.Should().Be(1, "the send opened a session socket");
            return Sockets.Sockets.Single();
        }

        public async ValueTask DisposeAsync()
        {
            // The peers never answer a close on their own; dispose waits for a session socket's.
            Sockets.AnswerClosesFromNowOn();
            await _provider.DisposeAsync();
        }
    }
}
