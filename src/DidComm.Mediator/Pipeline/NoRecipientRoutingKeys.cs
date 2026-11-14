using DidComm.Protocols.Routing;
using Jwk = DataProofsDotnet.Jose.Jwk;

namespace DidComm.Mediator.Pipeline;

/// <summary>
/// The <see cref="IRecipientRoutingKeyProvider"/> <c>AddMediator()</c> registers when the host
/// registers none: no extra routing keys for any recipient. A type of its own so that the mediator
/// can tell, when it starts, whether the provider in effect is its default or the host's (a host
/// provider cannot be combined with <see cref="RewrapMode.ReanoncryptToNext"/>, which never consults it).
/// </summary>
internal sealed class NoRecipientRoutingKeys : IRecipientRoutingKeyProvider
{
    private static readonly IReadOnlyList<Jwk> None = Array.Empty<Jwk>();

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<Jwk>> GetAsync(string recipientDid, CancellationToken ct)
        => ValueTask.FromResult(None);
}
