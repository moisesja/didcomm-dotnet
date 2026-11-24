using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Facade;
using DidComm.InteropTests.Mediator;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PlainPath = DidComm.InteropTests.AspNetCore.RewrappedReceiveTests.PlainPath;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #110, red-team pass 3 (3c) on O-3 (an opened message's own envelope must be in an accepted
/// media type): what was attacked and held. A host that lists only the encrypted media type, in any
/// letter case, accepts every encrypted composition inside a re-wrapped delivery (the honest
/// mediator case) and hands the consumer exactly the envelope it judged; and on the mediator's
/// endpoints the gate never touches a forward for the next hop, also one inside a layer addressed
/// to the mediator itself.
/// </summary>
public sealed class RewrappedReceiveAdv110Pass3Tests
{
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(15);

    public static TheoryData<PlainPath, EnvelopeShape, string> EncryptedInners()
    {
        var data = new TheoryData<PlainPath, EnvelopeShape, string>();
        foreach (var path in Enum.GetValues<PlainPath>())
        {
            foreach (var shape in new[] { EnvelopeShape.Anoncrypt, EnvelopeShape.AnoncryptOfSigned, EnvelopeShape.AnoncryptOfAuthcrypt, EnvelopeShape.AuthcryptOfSigned })
            {
                data.Add(path, shape, DidCommMediaTypes.Encrypted);
                data.Add(path, shape, DidCommMediaTypes.Encrypted.ToUpperInvariant());
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EncryptedInners))]
    public async Task An_encrypted_only_host_accepts_every_encrypted_composition_inside_a_rewrap_and_the_consumer_sees_the_envelope_judged(PlainPath path, EnvelopeShape shape, string listed)
    {
        await using var r = await Receivers.StartAsync(listed);
        var host = r.Host;
        var message = host.RecordingMessage(host.Alice).Build();
        var (packed, _) = await host.PackAsync(host.Alice, message, shape);
        var direct = await r.Host.App.Services.GetRequiredService<DidCommClient>().UnpackAsync(packed);

        var status = await r.DeliverAsync(path, await RewrapAsync(host, packed));

        if (status is { } s)
            s.Should().Be(HttpStatusCode.Accepted, path.ToString());
        var seen = r.Seen(message.Id).Should().ContainSingle().Subject;
        seen.Stack[0].Should().Be(EnvelopeKind.Encrypted);
        seen.Stack.Should().Equal(direct.Stack, "the consumer gets the inner message's own envelope, as if it had arrived directly");
        seen.Authenticated.Should().Be(direct.Authenticated);
        seen.NonRepudiation.Should().Be(direct.NonRepudiation);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task On_the_mediators_endpoint_an_encrypted_only_list_never_touches_a_forward_for_the_next_hop(bool insideASelfAddressedLayer)
    {
        await using var mediator = await MediatorTestServer.StartAsync(o =>
            o.PostConfigureServices = s => s.Configure<DidCommReceiveOptions>(x => x.AcceptedMediaTypes = new[] { DidCommMediaTypes.Encrypted }));
        var peer = await mediator.NewPeerAsync();
        var bob = await mediator.NewRecipientAsync();
        await mediator.EnrolAsync(peer, bob.Did);
        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(bob.Did);
        var (forward, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(note, bob.Did));
        var packed = insideASelfAddressedLayer ? (await alice.ForwardAsync(mediator.Did, mediator.Did, forward)).Packed : forward;

        await Qa.PostForwardAsync(mediator, packed);
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    private static async Task<string> RewrapAsync(ReturnRouteTestHost host, string inner)
        => await host.AnoncryptAsync(ForwardMessage.Create(host.ServerDid, host.ServerDid, new[] { inner }));

    /// <summary>The five plain receive paths on a host with one accepted entry; a WebSocket delivery is followed by an authcrypted fence (accepted under any list holding the encrypted type).</summary>
    private sealed class Receivers : IAsyncDisposable
    {
        private readonly ConcurrentQueue<UnpackResult> _seen = new();

        public ReturnRouteTestHost Host { get; private set; } = null!;

        public IReadOnlyList<UnpackResult> Seen(string id) => _seen.Where(u => u.Message.Id == id).ToList();

        public static async Task<Receivers> StartAsync(string listed)
        {
            var r = new Receivers();
            r.Host = await ReturnRouteTestHost.StartAsync(o =>
            {
                o.Callback = (unpacked, _, _) =>
                {
                    r._seen.Enqueue(unpacked);
                    return Task.FromResult<Message?>(null);
                };
                o.MapMore = app =>
                {
                    app.MapDidCommEndpoint(PlainReceivers.PathOf(PlainPath.HttpCallback), (unpacked, _) =>
                    {
                        r._seen.Enqueue(unpacked);
                        return Task.CompletedTask;
                    });
                    app.MapDidCommWebSocket(PlainReceivers.PathOf(PlainPath.WebSocketCallback), (unpacked, _) =>
                    {
                        r._seen.Enqueue(unpacked);
                        return Task.CompletedTask;
                    });
                };
                o.PostConfigureServices = s => s.Configure<DidCommReceiveOptions>(x => x.AcceptedMediaTypes = new[] { listed });
            });
            r.Host.Recorder.Behavior = (_, context) =>
            {
                r._seen.Enqueue(context.Received);
                return (Array.Empty<Message>(), null);
            };
            return r;
        }

        public async Task<HttpStatusCode?> DeliverAsync(PlainPath path, string packed)
        {
            if (path is PlainPath.HttpCallback or PlainPath.HttpRegistry or PlainPath.HttpSessionCallback)
                return (await Host.PostAsync(packed, PlainReceivers.PathOf(path))).Status;
            using var socket = await Host.ConnectAsync(PlainReceivers.PathOf(path));
            await ReturnRouteTestHost.SendAsync(socket, packed);
            var fence = Host.RecordingMessage(Host.Alice, "fence").Build();
            await ReturnRouteTestHost.SendAsync(socket, await Host.AuthcryptAsync(Host.Alice, fence));
            (await ReturnRouteTestHost.WaitUntilAsync(() => Seen(fence.Id).Count > 0, Hang)).Should().BeTrue("the fence was handled");
            using var close = new CancellationTokenSource(Hang);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, close.Token);
            return null;
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}
