using System.Collections.Concurrent;
using DidComm.Consistency;
using DidComm.Facade;
using DidComm.Secrets;
using AwesomeAssertions;
using Microsoft.IdentityModel.Tokens;
using NetDid.Core;
using NetDid.Core.Model;

namespace DidComm.Tests.Facade;

/// <summary>
/// Shared pieces for the identifier length-bound tests (FR-API-09, #80): builders for hostile
/// identifiers, the assertions every hostile case makes, and spies that record each value reaching
/// the DID resolver and the secrets resolver.
/// </summary>
internal static class LengthCap
{
    /// <summary>The key-id bound: kid, skid, from_prior kid, forward next.</summary>
    public const int KeyIdCap = IdentifierLimits.MaxKeyIdLength;

    /// <summary>The DID bound: from, to, from_prior iss and sub.</summary>
    public const int DidCap = IdentifierLimits.MaxDidLength;

    /// <summary>
    /// The filler every long identifier is padded with. No short identifier in these tests contains
    /// a run of it this long, so finding it in an exception message means the value was quoted.
    /// </summary>
    public static readonly string Run = new('k', 64);

    /// <summary>
    /// A key id of exactly <paramref name="length"/> characters: the short <paramref name="did"/>,
    /// then '#', then a long fragment. The DID part stays resolvable, so a served document can
    /// publish the long kid and, without the bound, the receiver would parse it and resolve the DID.
    /// </summary>
    public static string KidOfLength(string did, int length)
        => did + "#" + new string('k', length - did.Length - 1);

    /// <summary>A bare DID of exactly <paramref name="length"/> characters.</summary>
    public static string DidOfLength(int length)
        => "did:example:" + new string('k', length - "did:example:".Length);

    /// <summary>Assert that no message in the exception chain quotes the long identifier.</summary>
    public static void ShouldNotQuoteTheValue(this Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            e.Message.Should().NotContain(Run,
                "an over-long identifier must never be copied into an exception message (FR-API-09)");
        }
    }

    /// <summary>
    /// Assert the envelope is under the default receive limit. Otherwise the FR-API-06 size gate
    /// would refuse it first, and the test would prove nothing about the identifier bound.
    /// </summary>
    public static string ShouldFitTheReceiveLimit(this string packed)
    {
        Encoding.UTF8.GetByteCount(packed).Should().BeLessThan(new DidCommOptions().MaxReceiveBytes,
            "the envelope must pass the receive size gate so the identifier bound is what refuses it");
        return packed;
    }

    /// <summary>A DID document publishing the given keys under the given relationships, each controlled by <paramref name="did"/>.</summary>
    public static DidDocument Doc(string did, Jwk? authentication = null, Jwk? keyAgreement = null)
    {
        var methods = new List<VerificationMethod>();
        VerificationRelationshipEntry[]? auth = null, ka = null;
        if (authentication is not null)
        {
            var vm = Method(did, authentication);
            methods.Add(vm);
            auth = new[] { VerificationRelationshipEntry.FromEmbedded(vm) };
        }
        if (keyAgreement is not null)
        {
            var vm = Method(did, keyAgreement);
            methods.Add(vm);
            ka = new[] { VerificationRelationshipEntry.FromEmbedded(vm) };
        }
        return new DidDocument
        {
            Id = new Did(did),
            VerificationMethod = methods,
            Authentication = auth,
            KeyAgreement = ka,
        };
    }

    private static VerificationMethod Method(string did, Jwk key) => new()
    {
        Id = key.Kid!,
        Type = "JsonWebKey2020",
        Controller = new Did(did),
        PublicKeyJwk = new JsonWebKey { Kty = key.Kty, Crv = key.Crv, X = key.X, Y = key.Y },
    };
}

/// <summary>
/// Serves fixed DID documents and records every DID it is asked to resolve, so a test can show an
/// over-long value (or anything at all) never reached resolution.
/// </summary>
internal sealed class SpyDidResolver : IDidResolver
{
    private readonly Dictionary<string, DidDocument> _docs;
    private readonly ConcurrentQueue<string> _requested = new();

    public SpyDidResolver(params DidDocument[] docs)
        => _docs = docs.ToDictionary(d => d.Id.Value, StringComparer.Ordinal);

    /// <summary>Every DID passed to <see cref="ResolveAsync"/>, in call order.</summary>
    public IReadOnlyCollection<string> Requested => _requested;

    public bool CanResolve(string did) => _docs.ContainsKey(did);

    public Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
    {
        _requested.Enqueue(did);
        return Task.FromResult(_docs.TryGetValue(did, out var doc)
            ? new DidResolutionResult { DidDocument = doc, ResolutionMetadata = new DidResolutionMetadata() }
            : DidResolutionResult.NotFound(did));
    }
}

/// <summary>
/// An in-memory <see cref="ISecretsResolver"/> that records every kid it is asked about, through
/// either method, so a test can show an over-long kid never reached the consumer's keystore.
/// </summary>
internal sealed class SpySecretsResolver : ISecretsResolver
{
    private readonly Dictionary<string, Jwk> _byKid;
    private readonly ConcurrentQueue<string> _requested = new();

    public SpySecretsResolver(params Jwk[] privateJwks)
        => _byKid = privateJwks.ToDictionary(j => j.Kid!, StringComparer.Ordinal);

    /// <summary>Every kid passed to <see cref="FindAsync"/> or <see cref="FindPresentAsync"/>.</summary>
    public IReadOnlyCollection<string> Requested => _requested;

    public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default)
    {
        _requested.Enqueue(kid);
        return Task.FromResult(_byKid.GetValueOrDefault(kid));
    }

    public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
    {
        var asked = kids.ToList();
        foreach (var kid in asked)
            _requested.Enqueue(kid);
        return Task.FromResult<IReadOnlyList<string>>(asked.Where(_byKid.ContainsKey).ToArray());
    }
}
