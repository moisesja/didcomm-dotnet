using DidComm.Jose;

namespace DidComm.Protocols.Routing;

/// <summary>
/// An <see cref="IRecipientRoutingKeyProvider"/> that returns the same fixed key list for every
/// recipient — the behaviour of <see cref="ForwardProcessorOptions.ExtraRecipientRoutingKeys"/>,
/// which the three-argument <see cref="ForwardProcessor"/> constructor and
/// <c>AddRoutingMediator</c> wrap in this provider by default.
/// </summary>
public sealed class StaticRecipientRoutingKeyProvider : IRecipientRoutingKeyProvider
{
    private readonly IReadOnlyList<Jwk> _keys;

    /// <summary>Create a provider that answers every lookup with <paramref name="keys"/>.</summary>
    /// <param name="keys">The routing keys, outer to inner; <c>null</c> or empty means "no extra wraps".</param>
    public StaticRecipientRoutingKeyProvider(IReadOnlyList<Jwk>? keys)
        => _keys = keys ?? Array.Empty<Jwk>();

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<Jwk>> GetAsync(string recipientDid, CancellationToken ct)
        => ValueTask.FromResult(_keys);
}
