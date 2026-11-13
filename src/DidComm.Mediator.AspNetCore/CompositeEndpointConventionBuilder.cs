using Microsoft.AspNetCore.Builder;

namespace DidComm.Mediator.AspNetCore;

/// <summary>
/// The builder <c>MapDidCommMediator</c> returns: every convention added to it (rate limiting, host
/// filters, authorization, metadata) is added to each of the mediator's endpoints, in the order the
/// host adds them.
/// </summary>
internal sealed class CompositeEndpointConventionBuilder : IEndpointConventionBuilder
{
    private readonly IEndpointConventionBuilder[] _builders;

    /// <summary>Wrap the builders of the endpoints mapped together.</summary>
    /// <param name="builders">The endpoints' own convention builders.</param>
    internal CompositeEndpointConventionBuilder(IEnumerable<IEndpointConventionBuilder> builders)
    {
        ArgumentNullException.ThrowIfNull(builders);
        _builders = builders.ToArray();
        if (_builders.Length == 0 || Array.Exists(_builders, b => b is null))
            throw new ArgumentException("At least one endpoint builder is required, and none may be null.", nameof(builders));
    }

    /// <summary>The endpoints' own builders, in mapping order.</summary>
    internal IReadOnlyList<IEndpointConventionBuilder> Builders => _builders;

    /// <inheritdoc />
    public void Add(Action<EndpointBuilder> convention)
    {
        ArgumentNullException.ThrowIfNull(convention);
        foreach (var builder in _builders)
            builder.Add(convention);
    }

    /// <inheritdoc />
    public void Finally(Action<EndpointBuilder> finallyConvention)
    {
        ArgumentNullException.ThrowIfNull(finallyConvention);
        foreach (var builder in _builders)
            builder.Finally(finallyConvention);
    }
}
