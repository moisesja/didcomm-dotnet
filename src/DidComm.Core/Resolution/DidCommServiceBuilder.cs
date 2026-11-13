using System.Text.Json;
using DidComm.Consistency;
using NetDid.Core.Model;

namespace DidComm.Resolution;

/// <summary>
/// Builds the <c>DIDCommMessaging</c> service entry a DID document publishes so that others can
/// reach it (FR-ROUTE-03): a <c>serviceEndpoint</c> in the object form the DIDComm v2.1 spec defines
/// — <c>uri</c>, then optional <c>routingKeys</c> and <c>accept</c>. Put the result in the
/// <c>service</c> list of a DID you mint (for <c>did:peer:2</c>, in the creation options, where it
/// becomes part of the DID string).
/// </summary>
/// <remarks>
/// <para>
/// Start with <see cref="ForEndpoint"/> when the DID is reached directly at a transport address,
/// or with <see cref="ForMediator"/> when it is reached through a mediator: the mediator's DID is
/// the <c>uri</c>, and senders wrap one forward for the mediator's own keys (FR-ROUTE-04). Then set
/// what differs from the defaults — the entry id (<c>#didcomm</c>), extra routing keys (none) and
/// the accepted profiles (<c>["didcomm/v2"]</c>) — and call <see cref="Build"/>.
/// </para>
/// <para>
/// Every value is checked when it is given, so <see cref="Build"/> only ever produces an entry that
/// senders of this library can use. A refusal is an <see cref="ArgumentException"/> whose message
/// never repeats the value. A builder is not thread-safe; <see cref="Build"/> can be called more
/// than once and returns a new entry each time.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Reached directly:
/// var direct = DidCommServiceBuilder.ForEndpoint(new Uri("https://agent.example/didcomm")).Build();
///
/// // Reached through a mediator, with one more routing key after it:
/// var mediated = DidCommServiceBuilder.ForMediator(mediatorDid)
///     .WithRoutingKeys("did:example:relay#key-x25519-1")
///     .Build();
/// </code>
/// </example>
public sealed class DidCommServiceBuilder
{
    private const string ServiceType = "DIDCommMessaging";
    private const string DefaultId = "#didcomm";
    private static readonly string[] DefaultAccept = ["didcomm/v2"];

    private readonly string _uri;
    private readonly bool _viaMediator;
    private string _id = DefaultId;
    private string[] _routingKeys = [];
    private string[] _accept = DefaultAccept;

    private DidCommServiceBuilder(string uri, bool viaMediator)
    {
        _uri = uri;
        _viaMediator = viaMediator;
    }

    /// <summary>
    /// Start an entry for a DID reached directly at <paramref name="endpoint"/>. The address is kept
    /// in its canonical form (<see cref="Uri.AbsoluteUri"/>).
    /// </summary>
    /// <param name="endpoint">An absolute <c>http</c>, <c>https</c>, <c>ws</c> or <c>wss</c> address with no user information (<c>user@</c>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="endpoint"/> is relative, has another scheme, or carries user information.</exception>
    public static DidCommServiceBuilder ForEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri)
            throw new ArgumentException("The endpoint must be an absolute URI.", nameof(endpoint));
        if (endpoint.Scheme is not ("http" or "https" or "ws" or "wss"))
            throw new ArgumentException("The endpoint must use the http, https, ws or wss scheme.", nameof(endpoint));
        if (!string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("The endpoint must not carry user information.", nameof(endpoint));
        return new DidCommServiceBuilder(endpoint.AbsoluteUri, viaMediator: false);
    }

    /// <summary>
    /// Start an entry for a DID reached through the mediator <paramref name="mediatorDid"/>
    /// (FR-ROUTE-04): the mediator's DID is the <c>uri</c>, and a sender looks up the mediator's own
    /// service and wraps one forward for the mediator's keys. That forward counts toward the longest
    /// route <see cref="WithRoutingKeys"/> accepts.
    /// </summary>
    /// <param name="mediatorDid">The mediator's DID: a bare DID of at most 2048 characters (no path, query or fragment).</param>
    /// <exception cref="ArgumentNullException"><paramref name="mediatorDid"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="mediatorDid"/> is not a bare DID, or is longer than 2048 characters.</exception>
    public static DidCommServiceBuilder ForMediator(string mediatorDid)
    {
        ArgumentNullException.ThrowIfNull(mediatorDid);
        if (!IsBareDid(mediatorDid))
            throw new ArgumentException("The mediator must be named by a bare DID of at most 2048 characters (no path, query or fragment).", nameof(mediatorDid));
        return new DidCommServiceBuilder(mediatorDid, viaMediator: true);
    }

    /// <summary>
    /// Set the entry's <c>id</c>. Default <c>#didcomm</c>.
    /// </summary>
    /// <param name="id">A relative fragment (<c>#name</c>) or an absolute DID URL ending in a fragment (<c>did:example:123#name</c>). The fragment is not empty and uses only the characters a URI fragment may contain.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is neither form, or is over the identifier length limits.</exception>
    public DidCommServiceBuilder WithId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!IsServiceId(id))
            throw new ArgumentException("The id must be a fragment ('#name') or a DID URL ending in a fragment, within the identifier length limits.", nameof(id));
        _id = id;
        return this;
    }

    /// <summary>
    /// Set the entry's <c>routingKeys</c>, in the order given (FR-ROUTE-02): a sender wraps one
    /// forward per key, the first key outermost. Each key is a DID (the mediator's keys, as for
    /// <see cref="ForMediator"/>) or a DID URL naming one <c>keyAgreement</c> key
    /// (<c>did:example:m#key-1</c>). No arguments removes them.
    /// </summary>
    /// <param name="routingKeys">The routing keys, outermost first.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routingKeys"/> or one of its entries is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">An entry is neither a bare DID nor a DID followed by a non-empty fragment, or is over the identifier length limits; or the route would need more than 8 forward layers, counting the mediator's layer when the entry was started with <see cref="ForMediator"/>.</exception>
    public DidCommServiceBuilder WithRoutingKeys(params string[] routingKeys)
    {
        ArgumentNullException.ThrowIfNull(routingKeys);
        // The bound first, from the count alone, before any entry is looked at (L-055).
        var layers = routingKeys.Length + (_viaMediator ? 1 : 0);
        if (layers > MediatorEndpointExpander.MaxForwardLayers)
        {
            throw new ArgumentException(
                $"The route would need {layers} forward layers, more than the maximum of {MediatorEndpointExpander.MaxForwardLayers} senders accept.",
                nameof(routingKeys));
        }

        var copy = new string[routingKeys.Length];
        for (var i = 0; i < routingKeys.Length; i++)
        {
            var key = routingKeys[i] ?? throw new ArgumentNullException(nameof(routingKeys), $"Routing key {i} is null.");
            if (!IsRoutingKey(key))
                throw new ArgumentException($"Routing key {i} must be a DID or a DID followed by a fragment, within the identifier length limits.", nameof(routingKeys));
            copy[i] = key;
        }
        _routingKeys = copy;
        return this;
    }

    /// <summary>
    /// Set the entry's <c>accept</c> list: the DIDComm profiles the DID takes, in order of
    /// preference. Default <c>["didcomm/v2"]</c>. No arguments leaves <c>accept</c> out, which senders
    /// read as "no preference".
    /// </summary>
    /// <param name="profiles">The profiles, e.g. <c>didcomm/v2</c>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profiles"/> or one of its entries is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">An entry is empty or white space.</exception>
    public DidCommServiceBuilder WithAccept(params string[] profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var copy = new string[profiles.Length];
        for (var i = 0; i < profiles.Length; i++)
        {
            var profile = profiles[i] ?? throw new ArgumentNullException(nameof(profiles), $"Profile {i} is null.");
            if (string.IsNullOrWhiteSpace(profile))
                throw new ArgumentException($"Profile {i} is empty.", nameof(profiles));
            copy[i] = profile;
        }
        _accept = copy;
        return this;
    }

    /// <summary>
    /// Build the service entry: <c>id</c>, <c>type</c> <c>DIDCommMessaging</c>, and a
    /// <c>serviceEndpoint</c> object with <c>uri</c>, then <c>routingKeys</c> when there are any,
    /// then <c>accept</c> when it is not empty.
    /// </summary>
    /// <returns>A new entry, independent of this builder.</returns>
    public Service Build()
    {
        var endpoint = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["uri"] = JsonSerializer.SerializeToElement(_uri),
        };
        if (_routingKeys.Length > 0)
            endpoint["routingKeys"] = JsonSerializer.SerializeToElement(_routingKeys);
        if (_accept.Length > 0)
            endpoint["accept"] = JsonSerializer.SerializeToElement(_accept);

        return new Service
        {
            Id = _id,
            Type = ServiceType,
            ServiceEndpoint = ServiceEndpointValue.FromMap(endpoint),
        };
    }

    // A DID and nothing more: within the DID bound (checked first, so an over-long value is never
    // parsed) and equal to its own DID subject (PRD §4.3).
    private static bool IsBareDid(string value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= IdentifierLimits.MaxDidLength
           && string.Equals(DidSubject.DidSubjectOf(value), value, StringComparison.Ordinal);

    // A routing key the sender's route expansion accepts: a bare DID, or a bare DID + '#' + fragment.
    private static bool IsRoutingKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdentifierLimits.IsWithinKeyIdLimits(value))
            return false;
        var hash = value.IndexOf('#');
        return hash < 0
            ? IsBareDid(value)
            : IsBareDid(value[..hash]) && IsFragment(value.AsSpan(hash + 1));
    }

    // "#fragment", or a DID URL (a DID, optionally with path or query) + "#fragment".
    private static bool IsServiceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdentifierLimits.IsWithinKeyIdLimits(value))
            return false;
        var hash = value.IndexOf('#');
        if (hash < 0 || !IsFragment(value.AsSpan(hash + 1)))
            return false;
        return hash == 0 || DidSubject.DidSubjectOf(value[..hash]) is not null;
    }

    // RFC 3986 fragment: *( pchar / "/" / "?" ), pchar = unreserved / pct-encoded / sub-delims / ":" / "@".
    // Not empty here: an empty fragment names nothing.
    private static bool IsFragment(ReadOnlySpan<char> fragment)
    {
        if (fragment.IsEmpty)
            return false;
        for (var i = 0; i < fragment.Length; i++)
        {
            var c = fragment[i];
            if (char.IsAsciiLetterOrDigit(c) || "-._~!$&'()*+,;=:@/?".Contains(c))
                continue;
            if (c == '%' && i + 2 < fragment.Length && char.IsAsciiHexDigit(fragment[i + 1]) && char.IsAsciiHexDigit(fragment[i + 2]))
            {
                i += 2;
                continue;
            }
            return false;
        }
        return true;
    }
}
