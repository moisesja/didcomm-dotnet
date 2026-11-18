using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Samples.MediatedAgent.Wallet;
using DidComm.Samples.Shared;
using Xunit;

namespace DidComm.InteropTests.Samples;

/// <summary>
/// The test changes <see cref="HttpClient.DefaultProxy"/>, which every handler in the process that
/// uses the default proxy reads, so its collection runs alone, after the parallel ones.
/// </summary>
[CollectionDefinition(nameof(DefaultProxyCollection), DisableParallelization = true)]
public sealed class DefaultProxyCollection;

/// <summary>
/// PR #123 review (P2): sample 11's wallet fetches the mediator's invitation from a URL that came
/// from outside (a QR code), so the fetch connects only where the outbound-endpoint guard allows.
/// Through a proxy the client would connect to the proxy instead, the guard would check the proxy's
/// address, and the proxy would be asked to reach the forbidden host. The fetch must therefore
/// never use a configured proxy.
/// </summary>
[Collection(nameof(DefaultProxyCollection))]
public sealed class MediatedAgentWalletProxyTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Invitation_fetch_to_a_forbidden_host_is_refused_by_the_guard_and_never_reaches_a_configured_proxy()
    {
        // A stand-in proxy on loopback (an address the wallet's policy allows): it counts the
        // connections it accepts and closes each at once, so a client that does use it fails fast.
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var port = ((IPEndPoint)proxy.LocalEndpoint).Port;
        var accepted = 0;
        using var stopAccepting = new CancellationTokenSource();
        var acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await proxy.AcceptTcpClientAsync(stopAccepting.Token);
                    Interlocked.Increment(ref accepted); // before the close the client would see
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        var previous = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy(new Uri($"http://127.0.0.1:{port}"));
        try
        {
            await using var wallet = await RecipientWallet.CreateAsync("bob", new Narrator(TextWriter.Null));

            // 10.0.0.1 is a private address: the wallet's policy refuses it (it opens 127.0.0.1 only).
            var fetch = () => wallet.ReadInvitationAsync("https://10.0.0.1/oob?_oobid=mediator");
            var thrown = await fetch.Should().ThrowAsync<Exception>().WaitAsync(HangGuard);

            Chain(thrown.Which).OfType<TransportException>().Should().NotBeEmpty(
                "the guard refuses the invitation's host itself, at connect time");
            Volatile.Read(ref accepted).Should().Be(0,
                "a forbidden destination must never be handed to a proxy: the fetch connects directly or not at all");
        }
        finally
        {
            HttpClient.DefaultProxy = previous;
            await stopAccepting.CancelAsync();
            proxy.Stop();
            await acceptLoop.WaitAsync(HangGuard);
        }
    }

    private static IEnumerable<Exception> Chain(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            yield return ex;
    }
}
