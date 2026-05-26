namespace DidComm.Protocols.DiscoverFeatures;

/// <summary>
/// Default <see cref="IFeatureProvider"/> for <c>feature-type = constraint</c>: advertises
/// the receiver's <c>max_receive_bytes</c> from the active
/// <see cref="DidComm.Facade.DidCommOptions"/>. Per FR-PROTO-05 the spec defines this exact
/// constraint id so peers can negotiate payload size before they over-send and trip the 413
/// path (FR-API-06).
/// </summary>
/// <remarks>
/// Only responds to queries whose <see cref="FeatureQuery.Match"/> resolves to the
/// <c>max_receive_bytes</c> identifier (either exact or a wildcard that covers it). Other
/// constraints — there are no others defined in the spec today — return nothing; consumers
/// that publish their own constraints register additional <see cref="IFeatureProvider"/>s
/// for <c>feature-type = constraint</c> with a higher specificity match-id.
/// </remarks>
internal sealed class MaxReceiveBytesConstraintProvider : IFeatureProvider
{
    /// <inheritdoc />
    public string FeatureType => DiscoverFeatures.FeatureTypeConstraint;

    /// <inheritdoc />
    public IEnumerable<FeatureDisclosure> Disclose(string match, ProtocolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!FeatureMatch.Matches(match, DiscoverFeatures.ConstraintMaxReceiveBytes))
            yield break;

        yield return new FeatureDisclosure
        {
            FeatureType = DiscoverFeatures.FeatureTypeConstraint,
            Id = DiscoverFeatures.ConstraintMaxReceiveBytes,
            Value = context.Options.MaxReceiveBytes,
        };
    }
}
