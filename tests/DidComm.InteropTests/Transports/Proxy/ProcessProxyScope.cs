using System.Net;
using System.Net.Http;
using System.Reflection;

namespace DidComm.InteropTests.Transports.Proxy;

/// <summary>
/// Sets a process-wide proxy for the length of one test and restores everything after: either
/// <see cref="HttpClient.DefaultProxy"/> directly, or one of the proxy environment variables
/// (<c>HTTP_PROXY</c>, <c>HTTPS_PROXY</c>, <c>ALL_PROXY</c>). Use it only in the non-parallel
/// <c>DefaultProxyCollection</c>: every handler in the process that uses the default proxy sees it.
/// </summary>
/// <remarks>
/// The runtime reads the proxy variables once, when <see cref="HttpClient.DefaultProxy"/> is first
/// used, and caches the result, so setting a variable later changes nothing. To make the variable
/// count, the scope rebuilds the system proxy the way the runtime does on first use
/// (<c>System.Net.Http.SystemProxyInfo.ConstructSystemProxy</c>, found by reflection; the scope
/// fails loudly if it is gone) and assigns it through the public setter. Every test then asserts
/// <see cref="AssertProxies"/> before it sends, so a case cannot pass because the proxy silently did
/// not apply.
/// </remarks>
internal sealed class ProcessProxyScope : IDisposable
{
    /// <summary>Every proxy variable the runtime reads, in both spellings.</summary>
    private static readonly string[] ProxyVariables =
    {
        "http_proxy", "HTTP_PROXY", "https_proxy", "HTTPS_PROXY", "all_proxy", "ALL_PROXY", "no_proxy", "NO_PROXY",
    };

    private readonly IWebProxy _previousDefault;
    private readonly Dictionary<string, string?> _previousVariables = new(StringComparer.Ordinal);

    private ProcessProxyScope()
    {
        _previousDefault = HttpClient.DefaultProxy;
        foreach (var name in ProxyVariables)
            _previousVariables[name] = Environment.GetEnvironmentVariable(name);
    }

    /// <summary>Set <see cref="HttpClient.DefaultProxy"/> to <paramref name="proxy"/>.</summary>
    /// <param name="proxy">The process-wide proxy.</param>
    public static ProcessProxyScope FromDefaultProxy(Uri proxy)
    {
        var scope = new ProcessProxyScope();
        HttpClient.DefaultProxy = new WebProxy(proxy);
        return scope;
    }

    /// <summary>
    /// Clear every proxy variable, set <paramref name="variable"/> to <paramref name="proxy"/>, and
    /// rebuild <see cref="HttpClient.DefaultProxy"/> from the environment.
    /// </summary>
    /// <param name="variable">One of <c>HTTP_PROXY</c>, <c>HTTPS_PROXY</c>, <c>ALL_PROXY</c>.</param>
    /// <param name="proxy">The proxy address the variable names.</param>
    public static ProcessProxyScope FromEnvironment(string variable, Uri proxy)
    {
        var scope = new ProcessProxyScope();
        foreach (var name in ProxyVariables)
            Environment.SetEnvironmentVariable(name, null);
        Environment.SetEnvironmentVariable(variable, proxy.ToString());
        HttpClient.DefaultProxy = ConstructSystemProxy();
        return scope;
    }

    /// <summary>
    /// Throw unless the process-wide proxy sends <paramref name="destination"/> to
    /// <paramref name="proxy"/>. For <c>ws</c>/<c>wss</c>, pass the <c>http</c>/<c>https</c> form,
    /// which is what the handshake asks the proxy about.
    /// </summary>
    /// <param name="destination">The destination a send will target.</param>
    /// <param name="proxy">The stand-in's address.</param>
    public static void AssertProxies(Uri destination, Uri proxy)
    {
        var current = HttpClient.DefaultProxy;
        if (current.IsBypassed(destination) || current.GetProxy(destination) is not { } chosen
            || !string.Equals(chosen.Authority, proxy.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Precondition failed: the process proxy does not send '{destination}' to '{proxy}'. The test would prove nothing.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var (name, value) in _previousVariables)
            Environment.SetEnvironmentVariable(name, value);
        HttpClient.DefaultProxy = _previousDefault;
    }

    private static IWebProxy ConstructSystemProxy()
    {
        var type = typeof(HttpClient).Assembly.GetType("System.Net.Http.SystemProxyInfo", throwOnError: true)!;
        var construct = type.GetMethod("ConstructSystemProxy", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("System.Net.Http.SystemProxyInfo.ConstructSystemProxy is gone; rebuild this scope for the new runtime.");
        return (IWebProxy)construct.Invoke(null, null)!;
    }
}
