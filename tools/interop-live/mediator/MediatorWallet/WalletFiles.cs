using System.Text.Json.Nodes;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;

namespace MediatorWallet;

/// <summary>
/// An identity file in the harness's <c>{did, secrets: [private JWKs]}</c> schema, the one
/// <c>InteropCli mint</c> writes and the python counterpart reads.
/// </summary>
internal sealed record IdentityFile(string Did, IReadOnlyList<Jwk> Secrets)
{
    public static IdentityFile Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException($"Identity file did not parse: {path}");
        var did = root["did"]?.GetValue<string>() ?? throw new InvalidDataException($"Identity file has no 'did': {path}");
        var secrets = new List<Jwk>();
        foreach (var entry in root["secrets"]?.AsArray() ?? throw new InvalidDataException($"Identity file has no 'secrets': {path}"))
        {
            var jwk = entry?.AsObject() ?? throw new InvalidDataException($"Identity file has a null secret: {path}");
            secrets.Add(new Jwk
            {
                Kid = jwk["kid"]?.GetValue<string>(),
                Kty = jwk["kty"]?.GetValue<string>() ?? "OKP",
                Crv = jwk["crv"]?.GetValue<string>(),
                X = jwk["x"]?.GetValue<string>(),
                Y = jwk["y"]?.GetValue<string>(),
                D = jwk["d"]?.GetValue<string>(),
            });
        }
        return new IdentityFile(did, secrets);
    }

    public void Save(string path)
    {
        var secrets = new JsonArray();
        foreach (var jwk in Secrets)
        {
            var node = new JsonObject { ["kid"] = jwk.Kid, ["kty"] = jwk.Kty, ["crv"] = jwk.Crv, ["x"] = jwk.X };
            if (jwk.Y is not null)
                node["y"] = jwk.Y;
            if (jwk.D is not null)
                node["d"] = jwk.D;
            secrets.Add(node);
        }
        File.WriteAllText(path, new JsonObject { ["did"] = Did, ["secrets"] = secrets }.ToJsonString(CommandResult.Json) + "\n");
    }

    public void AddTo(InMemorySecretsResolver resolver)
    {
        foreach (var jwk in Secrets)
            resolver.Add(jwk);
    }

    /// <summary>Mint a did:peer:2 (X25519 + Ed25519, as sample 11's wallet does), with the service entry when one is given.</summary>
    public static async Task<IdentityFile> MintAsync(IServiceProvider services, NetDid.Core.Model.Service? service)
    {
        var manager = services.GetRequiredService<IDidManager>();
        var keys = services.GetRequiredService<IKeyGenerator>();
        var crypto = services.GetRequiredService<ICryptoProvider>();
        var identity = service is null
            ? await PeerIdentityFactory.CreateAsync(manager, keys, crypto).ConfigureAwait(false)
            : await PeerIdentityFactory.CreateWithServiceEntryAsync(manager, keys, crypto, service).ConfigureAwait(false);
        return new IdentityFile(identity.Did, identity.Privates);
    }
}

/// <summary>
/// <c>state.json</c>, written by <c>enrol</c> and read by the other commands:
/// <c>{mediatorDid, walletDid, walletIdentity, routedDid, routedIdentity, extraRoutedDids,
/// extraRoutedIdentities, httpEndpoint, wsEndpoint, endpointOverride, negotiatedVersion,
/// grant:{routingDids}}</c>. Identity entries are absolute paths of identity files.
/// </summary>
internal sealed record WalletState(
    string MediatorDid,
    string WalletDid,
    string WalletIdentity,
    string RoutedDid,
    string RoutedIdentity,
    IReadOnlyList<string> ExtraRoutedDids,
    IReadOnlyList<string> ExtraRoutedIdentities,
    string? HttpEndpoint,
    string? WsEndpoint,
    string? EndpointOverride,
    string? NegotiatedVersion,
    IReadOnlyList<string> RoutingDids)
{
    public static WalletState Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException($"State file did not parse: {path}");
        static string Text(JsonObject o, string name) => o[name]?.GetValue<string>() ?? throw new InvalidDataException($"State file has no '{name}'.");
        static IReadOnlyList<string> List(JsonNode? node) => node?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [];
        return new WalletState(
            Text(root, "mediatorDid"),
            Text(root, "walletDid"),
            Text(root, "walletIdentity"),
            Text(root, "routedDid"),
            Text(root, "routedIdentity"),
            List(root["extraRoutedDids"]),
            List(root["extraRoutedIdentities"]),
            root["httpEndpoint"]?.GetValue<string>(),
            root["wsEndpoint"]?.GetValue<string>(),
            root["endpointOverride"]?.GetValue<string>(),
            root["negotiatedVersion"]?.GetValue<string>(),
            List(root["grant"]?["routingDids"]));
    }

    public void Save(string path)
    {
        static JsonArray Array(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)i).ToArray());
        var root = new JsonObject
        {
            ["mediatorDid"] = MediatorDid,
            ["walletDid"] = WalletDid,
            ["walletIdentity"] = WalletIdentity,
            ["routedDid"] = RoutedDid,
            ["routedIdentity"] = RoutedIdentity,
            ["extraRoutedDids"] = Array(ExtraRoutedDids),
            ["extraRoutedIdentities"] = Array(ExtraRoutedIdentities),
            ["httpEndpoint"] = HttpEndpoint,
            ["wsEndpoint"] = WsEndpoint,
            ["endpointOverride"] = EndpointOverride,
            ["negotiatedVersion"] = NegotiatedVersion,
            ["grant"] = new JsonObject { ["routingDids"] = Array(RoutingDids) },
        };
        File.WriteAllText(path, root.ToJsonString(CommandResult.Json) + "\n");
    }

    /// <summary>Every secret the wallet holds: its own DID and each routed DID (a delivered message is encrypted to a routed DID).</summary>
    public InMemorySecretsResolver LoadSecrets()
    {
        var secrets = new InMemorySecretsResolver();
        IdentityFile.Load(WalletIdentity).AddTo(secrets);
        IdentityFile.Load(RoutedIdentity).AddTo(secrets);
        foreach (var path in ExtraRoutedIdentities)
            IdentityFile.Load(path).AddTo(secrets);
        return secrets;
    }

    /// <summary>The address calls use: the override when enrol was given one, else the mediator DID's first endpoint (<c>null</c>: let the client resolve it).</summary>
    public Uri? Override => EndpointOverride is null ? null : new Uri(EndpointOverride);
}
