using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #100 OD-8 red team, on real Kestrel with the manual clock: many clients each send one message
/// whose handling is slow and abort at once. Each connection gets one finish, timed by one close-grace
/// timer from its drop; at the bound every finish is cancelled, every connection ends, nothing stays
/// registered and nothing is logged at Error.
/// </summary>
public sealed class WebSocketMessageFinishRedTeamTests
{
    private static readonly TimeSpan Guard = DeadlineKestrelHost.Guard;
    private static readonly TimeSpan Grace = WebSocketReturnRouteSession.DefaultCloseGrace;

    [Theory]
    [InlineData(DeadlineEndpoint.Mediator)]
    [InlineData(DeadlineEndpoint.Dispatch)]
    public async Task A_connect_send_abort_flood_gets_one_bounded_finish_per_connection_and_nothing_outlives_the_bound(DeadlineEndpoint endpoint)
    {
        const int clients = 16;
        var holder = new MultiHoldHandler();
        var (host, _, _) = await WebSocketMessageFinishTests.StartAsync(o =>
            o.Builder = b => b.Services.AddSingleton<IProtocolHandler>(holder));
        await using var _host = host;

        var sockets = new List<ClientWebSocket>();
        for (var i = 0; i < clients; i++)
        {
            var socket = await host.ConnectAsync(endpoint);
            sockets.Add(socket);
            await DeadlinePeer.SendAsync(socket, await host.PackAsync(Hold(host)), endOfMessage: true);
        }
        (await ReturnRouteTestHost.WaitUntilAsync(() => holder.Entered == clients, Guard)).Should().BeTrue("every message is being handled");

        var mark = host.Time.Mark();
        foreach (var socket in sockets)
            socket.Abort();
        // One close-grace timer per connection: each server loop saw its end with the message in hand.
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.TimersCreatedSince(mark) >= clients, Guard)).Should().BeTrue();
        host.TimersCreatedSince(mark).Should().Be(clients, "one finish per connection, nothing else timed");
        holder.Cancelled.Should().Be(0, "nothing is cancelled before the bound");

        host.Time.Advance(Grace - DeadlineKestrelHost.Tick);
        holder.Cancelled.Should().Be(0);
        host.Time.Advance(DeadlineKestrelHost.Tick);

        (await ReturnRouteTestHost.WaitUntilAsync(() => holder.Cancelled == clients, Guard)).Should().BeTrue("the bound ends every finish");
        await host.WaitEndedWebSocketsAsync(clients);
        host.Registry.Count.Should().Be(0);
        holder.Entered.Should().Be(clients, "nothing more was read from any connection");
        WebSocketMessageFinishTests.Loud(host).Should().BeEmpty("a peer that leaves is not an application error");
        host.TimersCreatedSince(mark).Should().Be(clients, "no timer was created after the bound");
    }

    private static Message Hold(DeadlineKestrelHost host)
        => new MessageBuilder()
            .WithType(MultiHoldHandler.Piuri + "/hold")
            .WithFrom(host.Alice.Did)
            .WithTo(host.Did)
            .WithCreatedTime(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .Build();

    /// <summary>Holds every message until its token is cancelled; counts entries and cancellations.</summary>
    private sealed class MultiHoldHandler : IProtocolHandler
    {
        public const string Piuri = "https://didcomm.org/test-od8-flood/1.0";

        private int _entered;
        private int _cancelled;

        public string ProtocolUri => Piuri;

        public int Entered => Volatile.Read(ref _entered);

        public int Cancelled => Volatile.Read(ref _cancelled);

        public async Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _entered);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }
            return null;
        }
    }
}
