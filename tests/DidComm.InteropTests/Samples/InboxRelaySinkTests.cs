using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Samples.MediatorAgent;
using DidComm.TestSupport;
using DidComm.Threading;
using DidComm.Transports.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.Samples;

/// <summary>
/// The failure paths of sample 04's <see cref="InboxRelaySink"/>, which the sample's happy-path run
/// never reaches: a relay to an inbox that never answers is given up after
/// <see cref="InboxRelaySink.RelayTimeout"/> (the sink runs on the forward handler's one pump, so an
/// unbounded wait would stall every relay behind it); every failure, of any exception type, is
/// recorded as <see cref="RelayOutcome.Failed"/> and rethrown; and the host's shutdown is not a
/// failed relay. Also its label: each onward payload is sent under its own envelope's media type
/// (<see cref="RelayMediaType"/>), because a receiver checks the declared type against the envelope
/// (transports.md) and this library's endpoints answer 415 on a mismatch; a plaintext payload a
/// sender wrapped must reach the next hop's inbox.
/// </summary>
public sealed class InboxRelaySinkTests
{
    private const string Bob = "did:example:bob";
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task An_inbox_that_never_answers_is_given_up_after_RelayTimeout_and_recorded_Failed()
    {
        // A listener that is never accepted from: the OS completes the connection and the request is
        // sent, but no answer ever comes.
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            await using var relay = BuildRelay();
            var sink = new InboxRelaySink(relay.Client);
            sink.TryAddInbox(Bob, InboxAt(silent)).Should().BeTrue();

            var act = () => sink.AcceptAsync(Forward(), relay.Context(), CancellationToken.None).WaitAsync(HangGuard);

            (await act.Should().ThrowAsync<TimeoutException>()).Which.Message.Should().Contain("did not finish within");
            sink.Records.TryRead(out var record).Should().BeTrue();
            record!.Outcome.Should().Be(RelayOutcome.Failed);
            record.Error.Should().Be(nameof(TimeoutException));
        }
        finally
        {
            silent.Stop();
        }
    }

    [Fact]
    public async Task A_refused_connection_is_recorded_Failed_and_rethrown_whatever_its_exception_type()
    {
        await using var relay = BuildRelay();
        var sink = new InboxRelaySink(relay.Client);
        sink.TryAddInbox(Bob, new Uri($"http://127.0.0.1:{ClosedLoopbackPort()}/inbox")).Should().BeTrue();

        var act = () => sink.AcceptAsync(Forward(), relay.Context(), CancellationToken.None).WaitAsync(HangGuard);

        // Today the HTTP transport reports a refused connection as HttpRequestException, not
        // TransportException (HttpTransportConnectFailureContractTests): the sink records it anyway.
        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        thrown.Should().NotBeOfType<TimeoutException>();
        sink.Records.TryRead(out var record).Should().BeTrue();
        record!.Outcome.Should().Be(RelayOutcome.Failed);
        record.Error.Should().Be(thrown.GetType().Name);
    }

    [Fact]
    public async Task Shutdown_during_a_relay_is_not_a_failed_relay()
    {
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            await using var relay = BuildRelay();
            var sink = new InboxRelaySink(relay.Client);
            sink.TryAddInbox(Bob, InboxAt(silent)).Should().BeTrue();
            using var shutdown = new CancellationTokenSource();

            var relaying = sink.AcceptAsync(Forward(), relay.Context(), shutdown.Token);
            // The relay has connected (and is waiting for an answer): now the host shuts down.
            using var connection = await silent.AcceptTcpClientAsync().WaitAsync(HangGuard);
            await shutdown.CancelAsync();

            await ((Func<Task>)(() => relaying.WaitAsync(HangGuard))).Should().ThrowAsync<OperationCanceledException>();
            sink.Records.TryRead(out _).Should().BeFalse("a shutdown is counted by the forward handler, not recorded as a failed relay");
        }
        finally
        {
            silent.Stop();
        }
    }

    [Fact]
    public async Task A_plaintext_payload_relayed_by_the_sample_relay_reaches_a_MapDidCommEndpoint_inbox()
    {
        var received = new ConcurrentQueue<UnpackResult>();
        await using var inbox = await PlainInbox.StartAsync(received);
        await using var relay = BuildRelay();
        var sink = new InboxRelaySink(relay.Client);
        sink.TryAddInbox(Bob, inbox.Uri).Should().BeTrue();
        var onward = Encoding.UTF8.GetBytes(
            """{"id":"relay-plain-1","typ":"application/didcomm-plain+json","type":"https://didcomm.org/basicmessage/2.0/message","body":{"content":"plain"}}""");

        await sink.AcceptAsync(new ForwardProcessingResult(Bob, onward, ExpiresTime: null, Delay: null), relay.Context(), CancellationToken.None)
            .WaitAsync(HangGuard);

        sink.Records.TryRead(out var record).Should().BeTrue("the relay records every forward it handles");
        record!.Outcome.Should().Be(RelayOutcome.Relayed);
        record.HttpStatus.Should().Be(202);
        received.Should().ContainSingle().Which.Message.Id.Should().Be("relay-plain-1");
    }

    [Theory]
    [InlineData("""{"protected":"e30","recipients":[{"header":{"kid":"did:example:x#k"},"encrypted_key":"AA"}],"iv":"AA","ciphertext":"AA","tag":"AA"}""", MediaTypes.Encrypted)]
    [InlineData("""{"payload":"e30","signatures":[{"protected":"e30","signature":"AA"}]}""", MediaTypes.Signed)]
    [InlineData("""{"payload":"e30","protected":"e30","signature":"AA"}""", MediaTypes.Signed)]
    [InlineData("""{"id":"1","type":"https://didcomm.org/basicmessage/2.0/message","body":{}}""", MediaTypes.Plaintext)]
    [InlineData("""{"id":"1","type":"t","body":{"ciphertext":"AA"}}""", MediaTypes.Plaintext)]
    public void The_relay_labels_a_payload_by_its_own_envelope(string payload, string expected)
        => RelayMediaType.Of(Encoding.UTF8.GetBytes(payload)).Should().Be(expected);

    private static Uri InboxAt(TcpListener listener)
        => new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/inbox");

    // #98: SendPackedAsync takes only a DIDComm message (UTF-8 JSON, an object): the smallest one.
    private static ForwardProcessingResult Forward() => new(Bob, "{}"u8.ToArray(), ExpiresTime: null, Delay: null);

    // A loopback port nothing listens on: bind one, read it, release it.
    private static int ClosedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>The mediator's facade as sample 04 builds it: HTTP, no retries, 127.0.0.1 allowed.</summary>
    private static RelayHost BuildRelay()
    {
        var services = new ServiceCollection();
        services.AddDidComm(b => b
            .UseNetDidResolver()
            .UseSecretsResolver(new InMemorySecretsResolver())
            .UseHttpTransport(o =>
            {
                o.AllowedSchemes = new[] { "http", "https" };
                o.MaxRetryAttempts = 0;
            })
            .Configure(o => o.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1")));
        return new RelayHost(services.BuildServiceProvider());
    }

    /// <summary>A plain <c>MapDidCommEndpoint</c> inbox on a loopback port, recording what it receives.</summary>
    private sealed class PlainInbox(WebApplication app, Uri uri) : IAsyncDisposable
    {
        public Uri Uri => uri;

        public static async Task<PlainInbox> StartAsync(ConcurrentQueue<UnpackResult> received)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(new InMemorySecretsResolver()));
            var app = builder.Build();
            app.MapDidCommEndpoint("/inbox", (unpacked, _) =>
            {
                received.Enqueue(unpacked);
                return Task.CompletedTask;
            });
            await app.StartAsync();
            return new PlainInbox(app, new Uri(app.Urls.First().TrimEnd('/') + "/inbox"));
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class RelayHost(ServiceProvider services) : IAsyncDisposable
    {
        public DidCommClient Client { get; } = services.GetRequiredService<DidCommClient>();

        /// <summary>A forward's context, already acknowledged on the wire.</summary>
        public ProtocolContext Context()
        {
            var forward = new MessageBuilder()
                .WithType(ForwardConstants.ForwardTypeUri)
                .WithBody(new JsonObject())
                .Build();
            var received = new UnpackResult(
                forward, Array.Empty<EnvelopeKind>(), Encrypted: true, Authenticated: false, NonRepudiation: false,
                AnonymousSender: true, ContentEncryption: null, KeyWrap: null, SignatureAlgorithm: null, SignerKid: null,
                SenderKid: null, RecipientKid: null, AllRecipientKids: Array.Empty<string>(), FromPrior: null);
            return new ProtocolContext(
                received, new DidComm.Threading.ThreadState(forward.Id), Client,
                services.GetRequiredService<IOptions<DidCommOptions>>().Value, new InMemoryThreadStateStore());
        }

        public ValueTask DisposeAsync() => services.DisposeAsync();
    }
}
