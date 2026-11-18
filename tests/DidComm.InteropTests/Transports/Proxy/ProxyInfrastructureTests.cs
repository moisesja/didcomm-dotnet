using System.Net;
using System.Net.Http;
using AwesomeAssertions;
using DidComm.InteropTests.Samples;
using Xunit;

namespace DidComm.InteropTests.Transports.Proxy;

/// <summary>
/// The process-proxy scope really sets a process-wide proxy (from <see cref="HttpClient.DefaultProxy"/>
/// and from each variable) and restores the previous one, so the proxy tests built on it cannot pass
/// vacuously.
/// </summary>
[Collection(nameof(DefaultProxyCollection))]
public sealed class ProxyInfrastructureTests
{
    [Theory]
    [InlineData("HTTP_PROXY", "http://203.0.113.10/")]
    [InlineData("HTTPS_PROXY", "https://203.0.113.10/")]
    [InlineData("ALL_PROXY", "http://203.0.113.10/")]
    [InlineData("ALL_PROXY", "https://203.0.113.10/")]
    public void A_proxy_variable_becomes_the_process_proxy_and_is_restored(string variable, string destination)
    {
        var proxy = new Uri("http://127.0.0.1:3128");
        var before = HttpClient.DefaultProxy;
        var variableBefore = Environment.GetEnvironmentVariable(variable);
        using (ProcessProxyScope.FromEnvironment(variable, proxy))
        {
            ProcessProxyScope.AssertProxies(new Uri(destination), proxy);
        }
        HttpClient.DefaultProxy.Should().BeSameAs(before);
        Environment.GetEnvironmentVariable(variable).Should().Be(variableBefore, "the variable is restored");
    }

    [Fact]
    public void DefaultProxy_scope_sets_and_restores()
    {
        var proxy = new Uri("http://127.0.0.1:3128");
        var before = HttpClient.DefaultProxy;
        using (ProcessProxyScope.FromDefaultProxy(proxy))
            ProcessProxyScope.AssertProxies(new Uri("https://203.0.113.10/"), proxy);
        HttpClient.DefaultProxy.Should().BeSameAs(before);
    }
}
