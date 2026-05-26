namespace DidComm.Protocols.DiscoverFeatures;

/// <summary>
/// Default <see cref="IFeatureProvider"/> for <c>feature-type = protocol</c>: surfaces every
/// PIURI registered in <see cref="ProtocolHandlerRegistry"/>. Registered automatically when
/// the consumer calls <c>AddBuiltInProtocols()</c>; replace via
/// <c>Services.Replace(...)</c> for custom discovery policies (e.g. hiding internal protocols).
/// </summary>
/// <remarks>
/// Takes <see cref="IServiceProvider"/> rather than <see cref="ProtocolHandlerRegistry"/>
/// directly to dodge a DI-graph cycle: the registry's factory walks every
/// <see cref="IProtocolHandler"/>, including <see cref="DiscoverFeaturesHandler"/>, whose ctor
/// depends on every <see cref="IFeatureProvider"/> (including this one). Resolving the
/// registry lazily at call time means our ctor doesn't transitively force its construction.
/// </remarks>
internal sealed class ProtocolFeatureProvider : IFeatureProvider
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>Construct the provider, deferring registry resolution to call time.</summary>
    /// <param name="serviceProvider">The DI service provider — used to lazily resolve <see cref="ProtocolHandlerRegistry"/>.</param>
    public ProtocolFeatureProvider(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public string FeatureType => DiscoverFeatures.FeatureTypeProtocol;

    /// <inheritdoc />
    public IEnumerable<FeatureDisclosure> Disclose(string match, ProtocolContext context)
    {
        // Avoid the `Microsoft.Extensions.DependencyInjection` package dependency in Core by
        // using the BCL's IServiceProvider.GetService method directly. Cast + null-check.
        var registry = _serviceProvider.GetService(typeof(ProtocolHandlerRegistry)) as ProtocolHandlerRegistry
            ?? throw new InvalidOperationException(
                "ProtocolHandlerRegistry is not registered in the DI graph (FR-PROTO-03).");
        foreach (var handler in registry.All)
        {
            if (FeatureMatch.Matches(match, handler.ProtocolUri))
                yield return new FeatureDisclosure
                {
                    FeatureType = DiscoverFeatures.FeatureTypeProtocol,
                    Id = handler.ProtocolUri,
                };
        }
    }
}
