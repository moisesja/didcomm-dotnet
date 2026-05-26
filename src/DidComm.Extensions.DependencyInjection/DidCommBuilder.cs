using DidComm.Facade;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Protocols.Empty;
using DidComm.Protocols.TrustPing;
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
        // Phase 6.2a: the protocol handler registry + dispatcher are part of every facade
        // graph (FR-PROTO-03). The registry is built via a DI factory that walks every
        // registered IProtocolHandler — so AddProtocol<T>() only needs to add the handler to
        // the IServiceCollection, and first resolution of the singleton picks up everything
        // registered before that point.
        Services.TryAddSingleton<ProtocolHandlerRegistry>(sp =>
        {
            var registry = new ProtocolHandlerRegistry();
            foreach (var handler in sp.GetServices<IProtocolHandler>())
                registry.Register(handler);
            return registry;
        });
        Services.TryAddSingleton<ProtocolDispatcher>();
    }

    /// <summary>
    /// Register an <see cref="IProtocolHandler"/> singleton (FR-PROTO-03). Pulled into the
    /// shared <see cref="ProtocolHandlerRegistry"/> the first time the registry is resolved.
    /// Idempotent in the DI graph: calling <c>AddProtocol&lt;T&gt;()</c> twice still produces
    /// exactly one <typeparamref name="T"/> instance and exactly one
    /// <see cref="IProtocolHandler"/> entry.
    /// </summary>
    /// <typeparam name="T">A concrete <see cref="IProtocolHandler"/> with a public ctor resolvable from DI.</typeparam>
    public DidCommBuilder AddProtocol<T>() where T : class, IProtocolHandler
    {
        Services.TryAddSingleton<T>();
        // TryAddEnumerable de-dupes by ImplementationType; the typed factory below sets
        // ImplementationType = typeof(T), so repeat AddProtocol<T>() calls are no-ops. The
        // factory forwards to the singleton T registered above so the IEnumerable entry shares
        // that single T instance instead of constructing a duplicate.
        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IProtocolHandler, T>(sp => sp.GetRequiredService<T>()));
        return this;
    }

    /// <summary>
    /// Register the spec-defined built-in protocol handlers: Trust Ping 2.0 (FR-PROTO-04),
    /// Empty 1.0 (FR-PROTO-06), and Discover Features 2.0 (FR-PROTO-05) with its default
    /// <see cref="IFeatureProvider"/>s for <c>protocol</c> (reflects the registry) and
    /// <c>constraint</c> (advertises <c>max_receive_bytes</c>). Phase 6.2c will add
    /// Report Problem (always-on) and Trace (off by default).
    /// </summary>
    public DidCommBuilder AddBuiltInProtocols()
    {
        AddProtocol<TrustPingHandler>();
        AddProtocol<EmptyHandler>();
        AddProtocol<DiscoverFeaturesHandler>();
        // Default Discover Features providers — consumers add more (goal-codes, headers,
        // custom constraints) via AddFeatureProvider<T>(). TryAddEnumerable de-dupes by
        // ImplementationType so re-registration is a no-op.
        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IFeatureProvider, ProtocolFeatureProvider>());
        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IFeatureProvider, MaxReceiveBytesConstraintProvider>());
        return this;
    }

    /// <summary>
    /// Register an additional <see cref="IFeatureProvider"/> for Discover Features 2.0
    /// (FR-PROTO-05). Useful for advertising goal-codes, custom headers, or app-specific
    /// constraints. Idempotent in the DI graph: repeat registration of the same type is a no-op.
    /// </summary>
    /// <typeparam name="T">A concrete <see cref="IFeatureProvider"/> resolvable from DI.</typeparam>
    public DidCommBuilder AddFeatureProvider<T>() where T : class, IFeatureProvider
    {
        Services.TryAddSingleton<T>();
        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IFeatureProvider, T>(sp => sp.GetRequiredService<T>()));
        return this;
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
