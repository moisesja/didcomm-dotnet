using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace DidComm.Protocols;

/// <summary>
/// Registry that maps inbound <c>type</c> URIs to <see cref="IProtocolHandler"/>s per FR-PROTO-03.
/// Thread-safe; intended as a singleton.
/// </summary>
/// <remarks>
/// <para>
/// When multiple handlers share the same major version (FR-PROTO-02 floor), <see cref="TryResolve"/>
/// returns the handler whose registered minor is closest-but-not-greater than the inbound minor.
/// This mirrors the spec rule "interoperate at the older minor".
/// </para>
/// <para>
/// Handler registration is additive and idempotent: registering the same PIURI twice replaces
/// the prior entry so DI callbacks can be re-applied safely.
/// </para>
/// </remarks>
public sealed class ProtocolHandlerRegistry
{
    private readonly ConcurrentDictionary<string, IProtocolHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Register a handler. Replaces any existing handler at the same PIURI.</summary>
    /// <param name="handler">The handler to register.</param>
    public void Register(IProtocolHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!ProtocolIdentifier.TryParse(handler.ProtocolUri, out _))
            throw new ArgumentException(
                $"Handler.ProtocolUri is not a valid PIURI per FR-PROTO-01: '{handler.ProtocolUri}'.", nameof(handler));
        _handlers[handler.ProtocolUri] = handler;
    }

    /// <summary>
    /// Look up a handler whose registered PIURI matches the inbound message's type per
    /// FR-PROTO-01/02. The match is case- and punctuation-insensitive on the protocol name and
    /// requires the same major; the older minor wins (interop floor).
    /// </summary>
    /// <param name="messageType">The inbound <c>Message.Type</c> string (an MTURI).</param>
    /// <param name="handler">Resolved handler on success; <c>null</c> otherwise.</param>
    /// <returns><c>true</c> when a compatible handler exists.</returns>
    public bool TryResolve(string? messageType, [NotNullWhen(true)] out IProtocolHandler? handler)
    {
        handler = null;
        if (!MessageTypeUri.TryParse(messageType, out var mturi)) return false;

        // TryParse, not Parse: the MTURI docUri group (`.+?`) tolerates a trailing '/' that the
        // stricter PIURI group (`.+?[^/]`) rejects, so a crafted double-slash `type` (e.g.
        // "https://didcomm.org//x/1.0/m") parses as an MTURI but its derived PIURI does not.
        // A malformed inbound type must resolve to "no handler", not throw on the dispatch path.
        if (!ProtocolIdentifier.TryParse(mturi!.ProtocolIdentifier, out var inboundPiuri)) return false;

        IProtocolHandler? best = null;
        ProtocolVersion bestVersion = default;
        foreach (var candidate in _handlers.Values)
        {
            if (!ProtocolIdentifier.TryParse(candidate.ProtocolUri, out var candidatePiuri)) continue;
            if (!candidatePiuri!.MatchesProtocolAndMajor(inboundPiuri)) continue;
            // Older-minor-wins: keep the largest registered minor that does not exceed the
            // inbound's minor (so a 2.0 handler serves a 2.1 inbound, and a 2.1 handler serves
            // a 2.1 inbound but is bypassed for a 2.0 inbound in favor of the 2.0 entry).
            if (candidatePiuri.Version.Minor > inboundPiuri.Version.Minor) continue;
            if (best is null || candidatePiuri.Version.Minor > bestVersion.Minor)
            {
                best = candidate;
                bestVersion = candidatePiuri.Version;
            }
        }

        handler = best;
        return handler is not null;
    }

    /// <summary>Enumerate every registered handler (used by DiscoverFeatures in Phase 6.2b).</summary>
    public IReadOnlyCollection<IProtocolHandler> All => _handlers.Values.ToArray();
}
