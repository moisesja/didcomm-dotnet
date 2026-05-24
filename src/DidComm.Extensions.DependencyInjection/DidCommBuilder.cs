using DidComm.Facade;
using DidComm.Resolution;
using DidComm.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetDid.Extensions.DependencyInjection;

namespace DidComm.Extensions.DependencyInjection;

/// <summary>
/// Configures DIDComm services on an <see cref="IServiceCollection"/>. Returned by
/// <see cref="DidCommServiceCollectionExtensions.AddDidComm"/>; each method registers a piece
/// of the facade graph and returns <c>this</c> so calls can chain.
/// </summary>
public sealed class DidCommBuilder
{
    /// <summary>The underlying service collection — exposed so consumers can register supporting services without leaving the builder.</summary>
    public IServiceCollection Services { get; }

    internal DidCommBuilder(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
    }

    /// <summary>
    /// Register a NetDid-backed <c>IDidResolver</c> and the corresponding
    /// <see cref="IDidKeyService"/> adapter. By default registers <c>did:key</c> and
    /// <c>did:peer</c> — consumers can layer on more methods via <paramref name="configure"/>.
    /// </summary>
    /// <param name="configure">Optional callback that extends the underlying <see cref="NetDidBuilder"/> (e.g. adding webvh, enabling caching).</param>
    public DidCommBuilder UseNetDidResolver(Action<NetDidBuilder>? configure = null)
    {
        Services.AddNetDid(b =>
        {
            b.AddDidKey();
            b.AddDidPeer();
            configure?.Invoke(b);
        });
        Services.TryAddSingleton<IDidKeyService, NetDidKeyService>();
        return this;
    }

    /// <summary>
    /// Register a <typeparamref name="T"/> as the <see cref="ISecretsResolver"/> singleton.
    /// Required — FR-SEC-02 fails fast if no resolver is registered.
    /// </summary>
    /// <typeparam name="T">A concrete <see cref="ISecretsResolver"/> implementation.</typeparam>
    public DidCommBuilder UseSecretsResolver<T>() where T : class, ISecretsResolver
    {
        Services.AddSingleton<ISecretsResolver, T>();
        return this;
    }

    /// <summary>
    /// Register an already-constructed <see cref="ISecretsResolver"/> instance as a singleton.
    /// </summary>
    /// <param name="instance">The resolver to register.</param>
    public DidCommBuilder UseSecretsResolver(ISecretsResolver instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        Services.AddSingleton(instance);
        return this;
    }

    /// <summary>Tweak <see cref="DidCommOptions"/> (FR-API-05 / FR-API-06 knobs).</summary>
    /// <param name="configure">Configuration callback.</param>
    public DidCommBuilder Configure(Action<DidCommOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.AddOptions<DidCommOptions>().Configure(configure);
        return this;
    }
}
