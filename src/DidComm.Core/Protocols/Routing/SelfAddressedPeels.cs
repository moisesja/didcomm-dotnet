using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using DidComm.Facade;

namespace DidComm.Protocols.Routing;

/// <summary>
/// Each forward layer addressed to this agent is opened once per request. An endpoint with the
/// routing role opens such layers before dispatch (<see cref="DidCommClient.OpenSelfAddressedMessageAsync"/>,
/// owner decision OD-6 on #110). When the chain ends in a forward for the next hop, or in a layer
/// that does not open, the endpoint leaves the forward to the forward handler as it arrived. The
/// handler's peel (<see cref="ForwardProcessor.PeelAsync(UnpackResult, CancellationToken)"/>) then
/// takes what the endpoint recorded here instead of opening the same layer again. So every class of
/// forward costs what it cost before the endpoint looked ahead: one restricted opening per layer,
/// whichever way the chain ends.
/// </summary>
/// <remarks>
/// An entry is keyed by the layer's own unpack result, by reference, and is held weakly. Only the
/// request that unpacked a layer holds that object, so only that request finds the entry, and the
/// entry goes when the layer does. It is taken at most once, and only by a peel through the same
/// <see cref="DidCommClient"/> that recorded it. Otherwise the layer is opened again, as before.
/// </remarks>
internal static class SelfAddressedPeels
{
    private static readonly ConditionalWeakTable<UnpackResult, Outcome> Recorded = new();

    /// <summary>Record that <paramref name="layer"/> opened to <paramref name="inner"/>.</summary>
    /// <param name="client">The facade that opened it.</param>
    /// <param name="layer">The layer opened (a forward addressed to this agent).</param>
    /// <param name="inner">What the restricted opening found inside.</param>
    internal static void Opened(DidCommClient client, UnpackResult layer, UnpackResult inner)
        => Recorded.AddOrUpdate(layer, new Outcome(client, inner, Refusal: null));

    /// <summary>Record that the restricted opening of <paramref name="layer"/> refused it.</summary>
    /// <param name="client">The facade that tried to open it.</param>
    /// <param name="layer">The layer (a forward addressed to this agent).</param>
    /// <param name="refusal">What the opening threw (never a cancellation of the request).</param>
    internal static void Refused(DidCommClient client, UnpackResult layer, Exception refusal)
        => Recorded.AddOrUpdate(layer, new Outcome(client, Inner: null, ExceptionDispatchInfo.Capture(refusal)));

    /// <summary>
    /// Take the recorded opening of <paramref name="layer"/> by <paramref name="client"/>, if there is
    /// one: what was found inside, or the recorded refusal thrown again.
    /// </summary>
    /// <param name="client">The facade the peel runs through.</param>
    /// <param name="layer">The layer to open.</param>
    /// <param name="inner">What the recorded opening found inside.</param>
    /// <returns><c>true</c> when an opening was recorded and taken; <c>false</c> when the layer must be opened.</returns>
    internal static bool TryTake(DidCommClient client, UnpackResult layer, out UnpackResult inner)
    {
        if (!Recorded.TryGetValue(layer, out var outcome) || !ReferenceEquals(outcome.Client, client))
        {
            inner = null!;
            return false;
        }
        Recorded.Remove(layer);
        outcome.Refusal?.Throw();
        inner = outcome.Inner!;
        return true;
    }

    /// <summary>One recorded opening: what was found inside, or what refused it.</summary>
    /// <param name="Client">The facade that opened the layer.</param>
    /// <param name="Inner">What was found inside, when it opened.</param>
    /// <param name="Refusal">What refused it, when it did not.</param>
    private sealed record Outcome(DidCommClient Client, UnpackResult? Inner, ExceptionDispatchInfo? Refusal);
}
