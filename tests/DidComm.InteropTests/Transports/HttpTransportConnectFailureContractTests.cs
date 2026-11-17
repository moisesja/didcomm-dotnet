using System.Net;
using System.Net.Sockets;
using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// The contract <see cref="IDidCommTransport.SendAsync"/> documents: a transport-level failure surfaces
/// as <see cref="TransportException"/>. Today <c>HttpDidCommTransport</c> lets a failure to connect
/// escape <see cref="ITransportRouter.SendAsync"/> as <see cref="System.Net.Http.HttpRequestException"/>
/// instead — both a refused connection and the connect-time SSRF refusal, where the guard's
/// <see cref="TransportException"/> is only the inner exception (see
/// <c>HttpTransportPolicyInheritanceTests</c>, which pins today's behaviour). Recorded in #97 for a
/// follow-up; these reproductions fail until the transport wraps connect failures.
/// </summary>
public sealed class HttpTransportConnectFailureContractTests
{
    private const string KnownGap =
        "Known gap, not fixed in #97: HttpDidCommTransport lets connect failures (connection refused, the connect-time SSRF refusal) "
        + "escape ITransportRouter.SendAsync as HttpRequestException instead of the documented TransportException. "
        + "A pre-existing contract gap, recorded in #97 for a follow-up.";

    [Theory(Skip = KnownGap)]
    [InlineData(true)]  // the default policy: the connect-time SSRF guard refuses the loopback address
    [InlineData(false)] // no SSRF block: the connection itself is refused (nothing listens on the port)
    public async Task Connect_failure_surfaces_as_TransportException(bool blockPrivateNetworks)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            // AddDidComm's registration check needs a resolver and a secrets resolver; only the
            // transport router is used here.
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.UseHttpTransport(o =>
            {
                o.AllowedSchemes = new[] { "http", "https" };
                o.MaxRetryAttempts = 0;
            });
        });
        services.Configure<DidCommOptions>(o => o.OutboundEndpointPolicy.BlockPrivateNetworks = blockPrivateNetworks);
        await using var sp = services.BuildServiceProvider();
        var router = sp.GetRequiredService<ITransportRouter>();

        var request = new TransportRequest(
            new Uri($"http://127.0.0.1:{ClosedLoopbackPort()}/didcomm"), new byte[] { 1 }, MediaTypes.Encrypted);

        await ((Func<Task>)(() => router.SendAsync(request, CancellationToken.None)))
            .Should().ThrowExactlyAsync<TransportException>();
    }

    // A loopback port nothing listens on: bind one, read it, release it.
    private static int ClosedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
