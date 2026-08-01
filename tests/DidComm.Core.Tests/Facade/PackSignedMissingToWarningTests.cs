using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;

namespace DidComm.Tests.Facade;

/// <summary>
/// FR-SIG-05 (SHOULD) — a standalone signed message SHOULD contain <c>to</c>. The facade packs
/// a <c>to</c>-less message anyway (SHOULD, not MUST) but emits a structured warning; with a
/// <c>to</c> present nothing is logged and the packed output is unchanged.
/// </summary>
public sealed class PackSignedMissingToWarningTests
{
    private const string SignerDid = "did:example:alice";
    private const string SignerKid = "did:example:alice#auth-1";

    [Fact]
    public async Task PackSigned_WithoutTo_EmitsFrSig05Warning_AndStillPacks()
    {
        var (client, log) = NewClient();
        var message = new MessageBuilder()
            .WithType("http://example.com/p/1.0/m")
            .WithFrom(SignerDid)
            .Build(); // no To

        var packed = await client.PackSignedAsync(message, SignerDid);

        packed.Should().NotBeNullOrEmpty("the SHOULD warns, it must not refuse");
        var entry = log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject;
        entry.Message.Should().Contain("FR-SIG-05").And.Contain(message.Id);
    }

    [Fact]
    public async Task PackSigned_WithEmptyToList_EmitsFrSig05Warning()
    {
        var (client, log) = NewClient();
        var message = new MessageBuilder()
            .WithType("http://example.com/p/1.0/m")
            .WithFrom(SignerDid)
            .Build();
        message.To = new List<string>(); // present but empty — still not a non-empty 'to'

        await client.PackSignedAsync(message, SignerDid);

        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task PackSigned_WithTo_EmitsNoWarning()
    {
        var (client, log) = NewClient();
        var message = new MessageBuilder()
            .WithType("http://example.com/p/1.0/m")
            .WithFrom(SignerDid)
            .WithTo("did:example:bob")
            .Build();

        var packed = await client.PackSignedAsync(message, SignerDid);

        packed.Should().NotBeNullOrEmpty();
        log.Entries.Should().BeEmpty("a message that satisfies the SHOULD produces no advisory");
    }

    [Fact]
    public async Task PackSigned_WithoutLogger_KeepsWorkingIdentically()
    {
        // The pre-existing constructor shape (no logger) must behave exactly as before: the
        // NullLogger default swallows the advisory and the pack succeeds.
        var key = TestKeyMaterial.Generate(KeyType.Ed25519, SignerKid);
        var client = new DidCommClient(
            new DictionarySecretsLookup(new[] { key.PrivateJwk }),
            KeyService(key),
            new DidCommOptions());
        var message = new MessageBuilder().WithType("http://example.com/p/1.0/m").Build();

        var packed = await client.PackSignedAsync(message, SignerDid);

        packed.Should().NotBeNullOrEmpty();
    }

    private static (DidCommClient Client, ListLogger Log) NewClient()
    {
        var key = TestKeyMaterial.Generate(KeyType.Ed25519, SignerKid);
        var log = new ListLogger();
        var client = new DidCommClient(
            new DictionarySecretsLookup(new[] { key.PrivateJwk }),
            KeyService(key),
            new DidCommOptions(),
            log);
        return (client, log);
    }

    private static NetDidKeyService KeyService(TestKeyMaterial authKey)
        => new(new SingleDocResolver(Doc(SignerDid, authKey.PublicJwk)));

    private static DidDocument Doc(string did, DataProofsDotnet.Jose.Jwk authenticationKey)
    {
        var vm = new VerificationMethod
        {
            Id = authenticationKey.Kid!,
            Type = "JsonWebKey2020",
            Controller = new Did(did),
            PublicKeyJwk = new JsonWebKey
            {
                Kty = authenticationKey.Kty,
                Crv = authenticationKey.Crv,
                X = authenticationKey.X,
            },
        };
        return new DidDocument
        {
            Id = new Did(did),
            VerificationMethod = new[] { vm },
            Authentication = new[] { VerificationRelationshipEntry.FromEmbedded(vm) },
        };
    }

    private sealed class SingleDocResolver : IDidResolver
    {
        private readonly DidDocument _doc;
        public SingleDocResolver(DidDocument doc) => _doc = doc;

        public bool CanResolve(string did) => string.Equals(did, _doc.Id.Value, StringComparison.Ordinal);

        public Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(CanResolve(did)
                ? new DidResolutionResult { DidDocument = _doc, ResolutionMetadata = new DidResolutionMetadata() }
                : DidResolutionResult.NotFound(did));
    }

    /// <summary>Captures rendered log entries for assertion.</summary>
    private sealed class ListLogger : ILogger<DidCommClient>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
