using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using DidComm.Extensions.DependencyInjection;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Transports.Proxy;

/// <summary>
/// Builds the real HTTP transport the way a host does (<c>AddDidComm</c> + <c>UseHttpTransport</c>),
/// so its named client's own handler (the connect callback, the proxy settings) is what runs. It
/// names no <c>TrustedProxy</c>, so the tests that use only this file compile against the code
/// before the proxy change too (that is how they are shown to fail there).
/// </summary>
internal static class HttpTransportHarness
{
    /// <summary>Bounds every wait; with a correct product each awaited event is certain.</summary>
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>TEST-NET-3: the guard treats it as public; only the stand-in ever answers for it.</summary>
    public const string PublicHost = "203.0.113.10";

    /// <summary>
    /// A provider with the HTTP transport. Defaults: http and https allowed, no retries, a short
    /// back-off (so a retry a test does not expect costs no time); <paramref name="configure"/> runs after them.
    /// </summary>
    /// <param name="configure">Transport options for the case.</param>
    /// <param name="more">Further registrations (core options, the named client), after <c>UseHttpTransport</c>.</param>
    public static ServiceProvider Build(Action<HttpTransportOptions>? configure = null, Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            // Only the transport is exercised; these satisfy AddDidComm's eager registration check.
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.UseHttpTransport(o =>
            {
                o.AllowedSchemes = new[] { "http", "https" };
                o.MaxRetryAttempts = 0;
                o.RetryBaseDelay = TimeSpan.FromMilliseconds(10);
                configure?.Invoke(o);
            });
        });
        more?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Count every attempt the transport hands to the named client: a handler in front of the
    /// primary one sees each Polly attempt and each followed 307 exactly once.
    /// </summary>
    /// <param name="services">The services to add the counter to.</param>
    /// <param name="counter">The shared count.</param>
    public static void CountAttempts(IServiceCollection services, AttemptCounter counter) =>
        services.AddHttpClient(HttpDidCommTransport.HttpClientName)
            .AddHttpMessageHandler(() => new CountingHandler(counter));

    /// <summary>POST one byte to <paramref name="endpoint"/> through the transport, bounded by the hang guard.</summary>
    /// <param name="transport">The transport.</param>
    /// <param name="endpoint">The destination.</param>
    public static Task<TransportResult> SendAsync(IDidCommTransport transport, string endpoint) =>
        transport.SendAsync(
                new TransportRequest(new Uri(endpoint), new byte[] { 1 }, "application/didcomm-encrypted+json"),
                CancellationToken.None)
            .WaitAsync(HangGuard);

    /// <summary>Send and return what the send threw (failing the test when it did not throw).</summary>
    /// <param name="transport">The transport.</param>
    /// <param name="endpoint">The destination.</param>
    public static async Task<Exception> SendExpectingFailureAsync(IDidCommTransport transport, string endpoint)
    {
        try
        {
            var result = await SendAsync(transport, endpoint);
            throw new Xunit.Sdk.XunitException($"Expected the send to '{endpoint}' to fail, but it returned Accepted={result.Accepted} ({result.HttpStatusCode}).");
        }
        catch (TimeoutException ex)
        {
            throw new Xunit.Sdk.XunitException($"The send to '{endpoint}' did not finish within the hang guard: {ex.Message}");
        }
        catch (Xunit.Sdk.XunitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>The exception and every inner exception, outermost first.</summary>
    /// <param name="ex">The outermost exception.</param>
    public static IEnumerable<Exception> Chain(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            yield return e;
    }

    /// <summary>Every message in the chain, joined, for "mentions" / "never mentions" assertions.</summary>
    /// <param name="ex">The outermost exception.</param>
    public static string Describe(Exception ex) => string.Join(" | ", Chain(ex).Select(e => $"{e.GetType().Name}: {e.Message}"));

    /// <summary>A loopback port nothing listens on (bound once to get a free one, then released).</summary>
    public static int ClosedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>The shared attempt count.</summary>
    internal sealed class AttemptCounter
    {
        private int _count;

        /// <summary>Attempts seen so far.</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>Count one attempt.</summary>
        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class CountingHandler(AttemptCounter counter) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            counter.Increment();
            return base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>A theory that is skipped where this machine cannot listen on the IPv6 loopback <c>::1</c>.</summary>
internal sealed class IPv6LoopbackTheoryAttribute : TheoryAttribute
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    });

    public IPv6LoopbackTheoryAttribute()
    {
        if (!Available.Value)
            Skip = "This machine has no IPv6 loopback (::1).";
    }
}
