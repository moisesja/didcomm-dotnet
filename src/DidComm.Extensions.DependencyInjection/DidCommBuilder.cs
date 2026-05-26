using DidComm.Facade;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Threading;
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
        // Phase 6.1: every facade graph gets a process-local thread-state store by default so
        // FR-I18N-02 (thread-scoped accept-lang) and FR-PROTO-10 (cascade guard) have a
        // singleton seam to write to. Consumers can replace with a distributed store via
        // Services.Replace(...) when they scale horizontally.
        Services.TryAddSingleton<IThreadStateStore, InMemoryThreadStateStore>();
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
        // Phase 4: the same net-did resolver chain also feeds the routing layer
        // (FR-ROUTE-03/04). NetDidServiceEndpointResolver depends on DidCommOptions for the
        // DD-10 bare-string tolerance toggle — resolved at construction.
        Services.TryAddSingleton<IServiceEndpointResolver>(sp => new NetDidServiceEndpointResolver(
            sp.GetRequiredService<NetDid.Core.IDidResolver>(),
            sp.GetRequiredService<IDidKeyService>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DidCommOptions>>().Value));
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
