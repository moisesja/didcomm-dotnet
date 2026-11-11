using DidComm.Facade;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Protocols.Empty;
using DidComm.Protocols.ProblemReport;
using DidComm.Protocols.Routing;
using DidComm.Protocols.Trace;
using DidComm.Protocols.TrustPing;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Threading;
using DidComm.Transports.ReturnRoute;
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
/// <example>
/// Every builder method is exercised in <c>samples/02-Cookbook</c> (section A) and <c>samples/08-Extensibility</c>.
/// </example>
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
        // Guarded (DependencyCycleGuard): a dependency cycle through this factory — a handler that
        // depends back on the registry or the dispatcher — fails fast instead of deadlocking.
        Services.TryAddSingleton<ProtocolHandlerRegistry>(DependencyCycleGuard.Guard(typeof(ProtocolHandlerRegistry), sp =>
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
        }));
        // Explicit factory rather than TryAddSingleton<ProtocolDispatcher>(): the default greediest-
        // constructor selection would only pick the observer/correlator-aware ctor when TraceOptions
        // also happens to be registered (i.e. EnableTracing was called), silently dropping them
        // otherwise. The factory always calls the full ctor, resolving the optional pieces explicitly,
        // so FR-PROTO-12 observers AND FR-PROTO-05a inline correlators (e.g. DiscoverFeaturesClient)
        // are wired regardless of tracing.
        Services.TryAddSingleton<ProtocolDispatcher>(DependencyCycleGuard.Guard(typeof(ProtocolDispatcher), sp => new ProtocolDispatcher(
            sp.GetRequiredService<ProtocolHandlerRegistry>(),
            sp.GetRequiredService<IThreadStateStore>(),
            sp.GetService<ILogger<ProtocolDispatcher>>(),
            sp.GetService<TraceOptions>(),
            sp.GetServices<IProtocolObserver>(),
            sp.GetServices<IInboundCorrelator>())));
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
        // that single T instance instead of constructing a duplicate. Guarded: every walk of the
        // handlers passes through it, so a cycle back into the handlers (a service T depends on
        // that takes IEnumerable<IProtocolHandler>) fails fast instead of deadlocking.
        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IProtocolHandler, T>(DependencyCycleGuard.Guard(typeof(T), sp => sp.GetRequiredService<T>())));
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
        // `disclose` inline, synchronously, so it is not dropped behind unsolicited traffic in the
        // best-effort observer queue. The bounded inline phase only checks headers/trust and claims
        // the pending operation; body parsing happens on the requester continuation.
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
    /// Register the mediator role for Routing Protocol 2.0 (FR-ROUTE-09/10/11): the
    /// <see cref="ForwardHandler"/> as an <see cref="IProtocolHandler"/>, the
    /// <see cref="ForwardProcessor"/> it drives, and defaults for its collaborators. NOT part of
    /// <see cref="AddBuiltInProtocols"/> — an ordinary agent is not a mediator. Once registered,
    /// Discover Features discloses <c>routing/2.0</c> with <c>roles: ["mediator"]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every registration is <c>TryAdd</c>, so a host (or the mediator service of #95) registers
    /// its own <see cref="IForwardSink"/>, <see cref="IForwardRecipientRegistry"/> or
    /// <see cref="IRecipientRoutingKeyProvider"/> BEFORE calling this method and it wins. The
    /// defaults: the recipient registry is an empty <see cref="InMemoryForwardRecipientRegistry"/>
    /// — nothing is relayed until the host resolves it and registers recipients (or a Coordinate
    /// Mediation grant store replaces <see cref="IForwardRecipientRegistry"/>); the sink is
    /// <see cref="NullForwardSink"/>, which drops every forward after logging its id; the routing
    /// keys are <see cref="ForwardProcessorOptions.ExtraRecipientRoutingKeys"/> applied to every
    /// recipient. <paramref name="options"/> is validated here so an invalid combination fails at
    /// startup, not on the first forward.
    /// </para>
    /// <para>
    /// <strong>DI graph (no cycle).</strong> The <see cref="ProtocolHandlerRegistry"/> factory walks
    /// <c>IEnumerable&lt;IProtocolHandler&gt;</c> → <see cref="ForwardHandler"/> →
    /// <see cref="ForwardProcessor"/> → <see cref="DidCommClient"/> (secrets, key service, endpoint
    /// resolver, transport router, options, crypto provider) + <see cref="IDidKeyService"/> +
    /// <see cref="IRecipientRoutingKeyProvider"/> (→ <see cref="ForwardProcessorOptions"/>) +
    /// <see cref="IForwardSink"/> (<see cref="NullForwardSink"/> → logger) +
    /// <see cref="IForwardRecipientRegistry"/> (→ <see cref="InMemoryForwardRecipientRegistry"/>, no
    /// dependencies) + logger. None of these reaches <see cref="ProtocolHandlerRegistry"/>,
    /// <see cref="ProtocolDispatcher"/> or <see cref="IFeatureProvider"/>. An <see cref="IForwardSink"/>
    /// implementation MUST keep it that way: never take the registry or the dispatcher in its
    /// constructor — inject <see cref="IServiceProvider"/> and resolve at call time — or the first
    /// resolution of the registry fails: the container cannot see the cycle (both ends are
    /// factories) and, left alone, would park the resolving thread forever; the registry, dispatcher
    /// and handler factories registered here detect being re-entered and throw
    /// <see cref="InvalidOperationException"/> instead, so the first inbound request fails rather
    /// than hangs. This method also refuses, at registration, a host-registered
    /// <see cref="IForwardSink"/>, <see cref="IForwardRecipientRegistry"/> or
    /// <see cref="IRecipientRoutingKeyProvider"/> whose implementation type takes the registry,
    /// the dispatcher or a protocol handler in a public constructor. A collaborator registered
    /// through a factory, or one that closes the cycle through a further dependency, cannot be
    /// inspected here; its cycle surfaces as that exception on first resolution.
    /// </para>
    /// <para>
    /// The container disposes the singleton <see cref="ForwardHandler"/> with the provider, which
    /// stops its pump within the shutdown grace.
    /// </para>
    /// </remarks>
    /// <param name="options">Pass-through / rewrap / extra routing keys; <c>null</c> for the defaults (pass-through, no extras). A positional record, so an instance rather than a callback.</param>
    /// <exception cref="ArgumentException">When <paramref name="options"/> combines rewrap mode with extra routing keys (<see cref="ForwardProcessorOptions.Validate"/>).</exception>
    /// <exception cref="InvalidOperationException">When a host-registered <see cref="IForwardSink"/>, <see cref="IForwardRecipientRegistry"/> or <see cref="IRecipientRoutingKeyProvider"/> implementation type takes <see cref="ProtocolHandlerRegistry"/>, <see cref="ProtocolDispatcher"/> or an <see cref="IProtocolHandler"/> in a public constructor — the dependency cycle that would otherwise fail the first resolution.</exception>
    public DidCommBuilder AddRoutingMediator(ForwardProcessorOptions? options = null)
    {
        var resolved = options ?? new ForwardProcessorOptions();
        resolved.Validate(); // fail fast at registration, not on first dispatch inside the registry factory
        RejectRegistryCycle(typeof(IForwardSink));
        RejectRegistryCycle(typeof(IForwardRecipientRegistry));
        RejectRegistryCycle(typeof(IRecipientRoutingKeyProvider));
        Services.TryAddSingleton(resolved);
        Services.TryAddSingleton<IRecipientRoutingKeyProvider>(sp =>
            new StaticRecipientRoutingKeyProvider(sp.GetRequiredService<ForwardProcessorOptions>().ExtraRecipientRoutingKeys));
        Services.TryAddSingleton<InMemoryForwardRecipientRegistry>();
        Services.TryAddSingleton<IForwardRecipientRegistry>(sp => sp.GetRequiredService<InMemoryForwardRecipientRegistry>());
        Services.TryAddSingleton<IForwardSink, NullForwardSink>();
        Services.TryAddSingleton(sp => new ForwardProcessor(
            sp.GetRequiredService<DidCommClient>(),
            sp.GetRequiredService<IDidKeyService>(),
            sp.GetRequiredService<ForwardProcessorOptions>(),
            sp.GetRequiredService<IRecipientRoutingKeyProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DidCommOptions>>().Value));
        return AddProtocol<ForwardHandler>();
    }

    /// <summary>
    /// Refuse a <see cref="ForwardHandler"/> collaborator whose public constructor would close the
    /// L-017 cycle: the registry factory realizes <see cref="ForwardHandler"/>, which realizes the
    /// collaborator, which would need the registry (or the dispatcher, which needs the registry; or
    /// a handler, which the registry is realizing) — a cycle the container never diagnoses, refused
    /// here at registration rather than on the first resolution.
    /// </summary>
    /// <param name="collaborator">The collaborator service type registered before <see cref="AddRoutingMediator"/>.</param>
    private void RejectRegistryCycle(Type collaborator)
    {
        foreach (var descriptor in Services)
        {
            if (descriptor.ServiceType != collaborator || descriptor.IsKeyedService)
                continue;
            if (descriptor.ImplementationType is not { } implementation)
                continue; // an instance cannot cycle; a factory cannot be inspected

            foreach (var constructor in implementation.GetConstructors())
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    if (!ClosesRegistryCycle(parameter.ParameterType))
                        continue;
                    throw new InvalidOperationException(
                        $"{implementation.FullName}, registered as {collaborator.Name}, takes {parameter.ParameterType.Name} in a constructor. " +
                        "ForwardHandler is built inside the ProtocolHandlerRegistry factory, so a collaborator that needs the registry, the " +
                        "dispatcher or a protocol handler at construction closes a dependency cycle the container cannot detect, and the first " +
                        "resolution fails. Inject IServiceProvider and resolve it when AcceptAsync (or the lookup) runs instead.");
                }
            }
        }
    }

    private static bool ClosesRegistryCycle(Type parameterType)
    {
        if (parameterType == typeof(ProtocolHandlerRegistry) || parameterType == typeof(ProtocolDispatcher))
            return true;
        if (typeof(IProtocolHandler).IsAssignableFrom(parameterType))
            return true;
        return parameterType.IsGenericType
            && parameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            && typeof(IProtocolHandler).IsAssignableFrom(parameterType.GetGenericArguments()[0]);
    }

    /// <summary>
    /// Register the built-in <see cref="IInboundFrameConsumer"/> (PRD §8a, FR-RR-07): the messages a
    /// peer pushes on a return-route session socket are unpacked, accepted only when authcrypted by
    /// the session's peer to its local DID, admitted under FR-RR-08 replay protection, and dispatched
    /// to the protocol handlers with <see cref="ProtocolContext.Session"/> <c>null</c>. Pair it with
    /// <c>UseWebSocketTransport(o => o.ReceiveInbound = true)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>TryAdd</c>: an <see cref="IInboundFrameConsumer"/> the host registered first wins. Nothing
    /// is started in the background: each session socket's own receive loop calls the consumer, one
    /// message at a time, so a message is dispatched exactly when its socket is read.
    /// </para>
    /// <para>
    /// A handler's reply to a pushed message is never sent automatically (it is logged at Debug):
    /// reply with an explicit <see cref="DidCommClient.SendAsync"/>. A handler must not wait for
    /// another pushed message on the same session, because that session reads its next message only
    /// after the handler returned. Handlers never re-check replay admission.
    /// </para>
    /// <para>
    /// <strong>DI graph (no cycle, L-017).</strong> <see cref="DidCommClient"/> → transport router →
    /// transports → the WebSocket transport → <see cref="IInboundFrameConsumer"/> → (the service
    /// provider, a logger). The consumer resolves the facade, the dispatcher, the replay guard and the
    /// options when a message arrives, never at construction. A host consumer must keep it that way:
    /// see <see cref="IInboundFrameConsumer"/>.
    /// </para>
    /// </remarks>
    public DidCommBuilder UseInboundDispatch()
    {
        Services.TryAddSingleton<IInboundFrameConsumer>(sp => new InboundDispatchConsumer(sp, sp.GetService<ILogger<InboundDispatchConsumer>>()));
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
