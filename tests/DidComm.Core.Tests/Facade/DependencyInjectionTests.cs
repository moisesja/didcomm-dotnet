using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Secrets;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Tests.Facade;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddDidComm_ResolvesDidCommClient_WhenAllRegistrationsPresent()
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver<EmptyResolver>();
        });

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<DidCommClient>();

        client.Should().NotBeNull();
    }

    [Fact]
    public void AddDidComm_FailsFast_WhenSecretsResolverMissing_FrSec02()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddDidComm(b => b.UseNetDidResolver());

        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains("ISecretsResolver"));
    }

    [Fact]
    public void AddDidComm_FailsFast_WhenKeyServiceMissing()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddDidComm(b => b.UseSecretsResolver<EmptyResolver>());

        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains("IDidKeyService"));
    }

    [Fact]
    public void Configure_AppliesOptions()
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver<EmptyResolver>();
            b.Configure(o => o.MaxReceiveBytes = 4096);
        });

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DidCommOptions>>();

        options.Value.MaxReceiveBytes.Should().Be(4096);
    }

    [Fact]
    public void UseSecretsResolver_Instance_RegistersTheSpecificInstance()
    {
        var instance = new EmptyResolver();
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(instance);
        });

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ISecretsResolver>().Should().BeSameAs(instance);
    }

    private sealed class EmptyResolver : ISecretsResolver
    {
        public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default) => Task.FromResult<Jwk?>(null);
        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
