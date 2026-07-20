using DidComm.Facade;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Protocols.Empty;
using DidComm.Protocols.ProblemReport;
using DidComm.Protocols.Trace;
using DidComm.Protocols.TrustPing;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
            var logger = sp.GetService<ILogger<ProtocolHandlerRegistry>>();
            // Detect duplicate-PIURI registrations and surface them as warnings: the registry
            // is last-write-wins by design (re-registration is idempotent for DI re-application),
            // but silently overriding a host's custom handler with a built-in is a documented
            // foot-gun (AddBuiltInProtocols after a custom AddProtocol). Logging here makes the
            // override observable without changing the documented semantics.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var handler in sp.GetServices<IProtocolHandler>())
            {
                if (!seen.Add(handler.ProtocolUri))
                {
                    logger?.LogWarning(
                        "Multiple IProtocolHandler registrations for PIURI '{Piuri}'. Last registration wins ({HandlerType}). If you registered a custom handler before calling AddBuiltInProtocols(), reorder so the custom registration comes after.",
                        handler.ProtocolUri, handler.GetType().FullName);
                }
                registry.Register(handler);
            }
            return registry;
        });
        // Explicit factory rather than TryAddSingleton<ProtocolDispatcher>(): the default greediest-
        // constructor selection would only pick the observer/correlator-aware ctor when TraceOptions
        // also happens to be registered (i.e. EnableTracing was called), silently dropping them
        // otherwise. The factory always calls the full ctor, resolving the optional pieces explicitly,
        // so FR-PROTO-12 observers AND FR-PROTO-05a inline correlators (e.g. DiscoverFeaturesClient)
        // are wired regardless of tracing.
        Services.TryAddSingleton<ProtocolDispatcher>(sp => new ProtocolDispatcher(
            sp.GetRequiredService<ProtocolHandlerRegistry>(),
            sp.GetRequiredService<IThreadStateStore>(),
            sp.GetService<ILogger<ProtocolDispatcher>>(),
            sp.GetService<TraceOptions>(),
            sp.GetServices<IProtocolObserver>(),
            sp.GetServices<IInboundCorrelator>()));
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
    /// Empty 1.0 (FR-PROTO-06), Discover Features 2.0 (FR-PROTO-05) with its default
    /// <see cref="IFeatureProvider"/>s and the initiator-side
    /// <see cref="DiscoverFeaturesClient"/> (FR-PROTO-05a), and Report Problem 2.0
    /// (FR-PROTO-07/08/10). Trace 2.0 is NOT registered here — it is off by default per
    /// FR-PROTO-11a; opt in via <see cref="EnableTracing"/>.
    /// </summary>
    public DidCommBuilder AddBuiltInProtocols()
    {
        AddProtocol<TrustPingHandler>();
        AddProtocol<EmptyHandler>();
        AddProtocol<DiscoverFeaturesHandler>();
        AddProtocol<ProblemReportHandler>();
        // FR-PROTO-05a: the Discover Features initiator (QueryFeaturesAsync). Registered as an
        // internal IInboundCorrelator (NOT a queued observer): the dispatcher hands it each inbound
        // `disclose` inline, synchronously, so a genuine authenticated response completes losslessly —
        // it is never dropped behind a flood of unsolicited traffic in the best-effort observer queue.
        // This is also why AddBuiltInProtocols registers no default IProtocolObserver: there is no
        // default firehose consumer for an attacker to exploit.
        Services.TryAddSingleton<DiscoverFeaturesClient>();
        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IInboundCorrelator, DiscoverFeaturesClient>(
                sp => sp.GetRequiredService<DiscoverFeaturesClient>()));
        // The FR-PROTO-10 cascade budget (#36) lives in a dedicated store registered as its OWN
        // singleton, so the budget persists even if the handler is (mis)registered non-singleton.
        Services.TryAddSingleton<CascadeBudgetStore>();
        // ProblemReportOptions is bound via the standard Options pattern; default ctor's
        // CascadeThreshold = 5 is the SICPA-python-matching default per locked decision.
        Services.AddOptions<ProblemReportOptions>();
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
    /// Opt in to Trace 2.0 (FR-PROTO-11). <strong>Off by default</strong> per FR-PROTO-11a;
    /// calling this method registers <see cref="TraceOptions"/>, validates the configured
    /// allowlist immediately (a non-empty <c>AllowedReportingUris</c> is REQUIRED when
    /// <c>Enabled = true</c>), and makes the options available for resolution.
    /// </summary>
    /// <remarks>
    /// Idempotent: first call wins. Subsequent calls on the same <see cref="IServiceCollection"/>
    /// keep the original <see cref="TraceOptions"/> instance — use <c>Services.Replace(...)</c>
    /// if you need to reconfigure.
    /// </remarks>
    /// <param name="configure">Configuration callback. Set <c>Enabled = true</c> and add entries to <c>AllowedReportingUris</c>.</param>
    /// <exception cref="InvalidOperationException">When the configured options fail <see cref="TraceOptions.Validate"/>.</exception>
    public DidCommBuilder EnableTracing(Action<TraceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new TraceOptions();
        configure(options);
        options.Validate();
        Services.TryAddSingleton(options);
        // Also expose the validated instance as IOptions<TraceOptions> for consumers that prefer
        // the Options pattern (ProblemReportOptions offers the same via AddOptions<T>()). Options.Create
        // wraps the SAME instance registered above, so both resolve to identical values; the container
        // prefers this closed IOptions<TraceOptions> over the open-generic IOptions<> fallback, so it
        // is order-independent. TryAdd keeps the "first call wins" idempotency this method documents.
        Services.TryAddSingleton<Microsoft.Extensions.Options.IOptions<TraceOptions>>(
            Microsoft.Extensions.Options.Options.Create(options));
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
    /// Register an <see cref="IProtocolObserver"/> — a read-only side channel the
    /// <see cref="ProtocolDispatcher"/> notifies for every completed inbound dispatch
    /// (FR-PROTO-12). Use this to observe traffic whose PIURI is owned by a built-in handler
    /// (e.g. reacting to inbound <c>report-problem</c>s) without replacing that handler.
    /// </summary>
    /// <remarks>
    /// Observers are host-trusted: they see decrypted inbound plaintext (scoped by their
    /// <see cref="IProtocolObserver.ProtocolUriFilter"/>), can only be registered here at
    /// composition time, and are enumerated in an Information log line at dispatcher
    /// construction. They receive defensive clones and cannot influence dispatch outcomes —
    /// see <see cref="IProtocolObserver"/> for the full trust model. Idempotent in the DI
    /// graph: repeat registration of the same type is a no-op.
    /// </remarks>
    /// <typeparam name="T">A concrete <see cref="IProtocolObserver"/> resolvable from DI.</typeparam>
    public DidCommBuilder AddProtocolObserver<T>() where T : class, IProtocolObserver
    {
        Services.TryAddSingleton<T>();
        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IProtocolObserver, T>(sp => sp.GetRequiredService<T>()));
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
    /// Required — FR-SEC-02 fails fast if no resolver is registered. When <typeparamref name="T"/>
    /// also implements <see cref="IOpaqueKeyResolver"/> (e.g. a keystore-backed resolver), the same
    /// singleton is surfaced as that capability too, so the facade routes signing / ECDH through the
    /// non-extractable handles (FR-SEC-06).
    /// </summary>
    /// <typeparam name="T">A concrete <see cref="ISecretsResolver"/> implementation.</typeparam>
    public DidCommBuilder UseSecretsResolver<T>() where T : class, ISecretsResolver
    {
        Services.AddSingleton<ISecretsResolver, T>();
        if (typeof(IOpaqueKeyResolver).IsAssignableFrom(typeof(T)))
            Services.AddSingleton(sp => (IOpaqueKeyResolver)sp.GetRequiredService<ISecretsResolver>());
        return this;
    }

    /// <summary>
    /// Register an already-constructed <see cref="ISecretsResolver"/> instance as a singleton. When the
    /// instance also implements <see cref="IOpaqueKeyResolver"/>, it is surfaced as that capability too
    /// (non-extractable signing / ECDH; FR-SEC-06).
    /// </summary>
    /// <param name="instance">The resolver to register.</param>
    public DidCommBuilder UseSecretsResolver(ISecretsResolver instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        Services.AddSingleton(instance);
        if (instance is IOpaqueKeyResolver opaque)
            Services.AddSingleton(opaque);
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
