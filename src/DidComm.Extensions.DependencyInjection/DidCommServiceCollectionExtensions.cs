using DidComm.Crypto;
using DidComm.Facade;
using DidComm.Resolution;
using DidComm.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DidComm.Extensions.DependencyInjection;

/// <summary>
/// Public entry point: <c>services.AddDidComm(b => …)</c> wires the
/// <see cref="DidCommClient"/> singleton plus its dependencies (FR-API-08). The configuration
/// callback uses <see cref="DidCommBuilder"/>.
/// </summary>
public static class DidCommServiceCollectionExtensions
{
    /// <summary>
    /// Add DIDComm services. Throws <see cref="InvalidOperationException"/> on completion if
    /// the consumer did not register both an <see cref="ISecretsResolver"/> (via
    /// <see cref="DidCommBuilder.UseSecretsResolver{T}"/>) and an <see cref="IDidKeyService"/>
    /// (via <see cref="DidCommBuilder.UseNetDidResolver"/> or by calling
    /// <c>Services.AddSingleton&lt;IDidKeyService&gt;</c> directly) — FR-SEC-02 fail-fast.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Builder callback.</param>
    public static IServiceCollection AddDidComm(
        this IServiceCollection services,
        Action<DidCommBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new DidCommBuilder(services);
        configure(builder);

        services.AddOptions<DidCommOptions>();
        services.TryAddSingleton<DefaultCryptoProvider>();
        services.AddSingleton(sp => new DidCommClient(
            sp.GetRequiredService<ISecretsResolver>(),
            sp.GetRequiredService<IDidKeyService>(),
            sp.GetRequiredService<IOptions<DidCommOptions>>().Value,
            sp.GetRequiredService<DefaultCryptoProvider>()));

        if (!IsRegistered<ISecretsResolver>(services))
        {
            throw new InvalidOperationException(
                "AddDidComm: no ISecretsResolver was registered. Call builder.UseSecretsResolver<T>() — FR-SEC-02 (see docs/didcomm-dotnet_PRD.md §6.3).");
        }
        if (!IsRegistered<IDidKeyService>(services))
        {
            throw new InvalidOperationException(
                "AddDidComm: no IDidKeyService was registered. Call builder.UseNetDidResolver() or register a custom adapter (see PRD §6.1).");
        }

        return services;
    }

    private static bool IsRegistered<T>(IServiceCollection services)
    {
        foreach (var sd in services)
        {
            if (sd.ServiceType == typeof(T))
                return true;
        }
        return false;
    }
}
