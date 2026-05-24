using System.Text.Json;
using NetDid.Core.Model;

namespace DidComm.Resolution;

/// <summary>
/// Pure projection from net-did's <see cref="Service"/> entries onto the DIDComm
/// <see cref="DidCommServiceInfo"/> shape (PRD §8 / FR-ROUTE-03). Lives behind
/// <see cref="IServiceEndpointResolver"/> implementations so the JSON-element wrangling has
/// exactly one home.
/// </summary>
/// <remarks>
/// <para>
/// Per the v2.1 §Service Endpoint spec, a <c>DIDCommMessaging</c> service's
/// <c>serviceEndpoint</c> takes one of two **conformant** shapes:
/// </para>
/// <list type="bullet">
///   <item><description>A single object with at minimum <c>uri</c>, plus optional <c>accept</c> and <c>routingKeys</c> arrays.</description></item>
///   <item><description>An array of such objects (preference order = sender preference).</description></item>
/// </list>
/// <para>
/// A bare-string <c>serviceEndpoint</c> is a DD-10 compatibility tolerance, not the canonical
/// shape. The parser refuses it by default; opt in by passing
/// <c>allowBareStringServiceEndpoint = true</c>.
/// </para>
/// </remarks>
internal static class ServiceEndpointParser
{
    /// <summary>The DID-Core service type that DIDComm v2.x recognises.</summary>
    public const string DidCommMessagingType = "DIDCommMessaging";

    /// <summary>
    /// Project every <c>DIDCommMessaging</c> service entry in <paramref name="services"/>
    /// onto a flat, preference-ordered list of <see cref="DidCommServiceInfo"/>. Service
    /// entries of other types are silently skipped (a DID Document MAY publish many service
    /// types for different protocols).
    /// </summary>
    /// <param name="services">The full <c>service</c> array from a resolved DID Document. <c>null</c> is treated as empty.</param>
    /// <param name="allowBareStringServiceEndpoint">When <c>true</c>, the DD-10 bare-string tolerance is honored.</param>
    /// <returns>An ordered enumeration of <see cref="DidCommServiceInfo"/>; possibly empty.</returns>
    public static IReadOnlyList<DidCommServiceInfo> Parse(
        IEnumerable<Service>? services,
        bool allowBareStringServiceEndpoint = false)
    {
        if (services is null)
            return Array.Empty<DidCommServiceInfo>();

        var infos = new List<DidCommServiceInfo>();
        foreach (var service in services)
        {
            if (service is null) continue;
            if (!string.Equals(service.Type, DidCommMessagingType, StringComparison.Ordinal))
                continue;

            CollectFromEndpoint(service.ServiceEndpoint, infos, allowBareStringServiceEndpoint);
        }
        return infos;
    }

    private static void CollectFromEndpoint(
        ServiceEndpointValue? endpoint,
        List<DidCommServiceInfo> sink,
        bool allowBareString)
    {
        if (endpoint is null) return;

        if (endpoint.IsUri)
        {
            if (!allowBareString)
                return; // DD-10 tolerance is off; skip this entry rather than throw — the document MAY publish other usable shapes.

            var uri = endpoint.Uri;
            if (!string.IsNullOrEmpty(uri))
                sink.Add(new DidCommServiceInfo(uri, Array.Empty<string>(), Array.Empty<string>()));
            return;
        }

        if (endpoint.IsMap)
        {
            var projected = ProjectMap(endpoint.Map!);
            if (projected is not null)
                sink.Add(projected);
            return;
        }

        if (endpoint.IsSet)
        {
            foreach (var child in endpoint.Set!)
                CollectFromEndpoint(child, sink, allowBareString);
        }
    }

    private static DidCommServiceInfo? ProjectMap(IReadOnlyDictionary<string, JsonElement> map)
    {
        if (!map.TryGetValue("uri", out var uriElement)
            || uriElement.ValueKind != JsonValueKind.String)
        {
            return null; // FR-ROUTE-03: uri is REQUIRED inside the object form; entries without it are unusable.
        }

        var uri = uriElement.GetString();
        if (string.IsNullOrEmpty(uri))
            return null;

        var routingKeys = ReadStringArray(map, "routingKeys");
        var accept = ReadStringArray(map, "accept");
        return new DidCommServiceInfo(uri, routingKeys, accept);
    }

    private static IReadOnlyList<string> ReadStringArray(
        IReadOnlyDictionary<string, JsonElement> map,
        string key)
    {
        if (!map.TryGetValue(key, out var element)) return Array.Empty<string>();
        if (element.ValueKind != JsonValueKind.Array) return Array.Empty<string>();

        var list = new List<string>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s))
                    list.Add(s);
            }
        }
        return list;
    }
}
