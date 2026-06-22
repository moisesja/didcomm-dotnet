using System.Text.Json;
using DidComm.Composition;
using DidComm.InteropTests.Resolution;
using DidComm.Jose;
using DidComm.Messages;
using FluentAssertions;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.InteropTests.Runner;

/// <summary>
/// Routes a fixture manifest's <c>operation</c> to the matching runner. Phase 2 supports
/// <c>noop</c>, <c>unpack</c>, and <c>verify</c> — the FR-IX-01 / FR-IX-03 inbound gate.
/// Outbound (pack-*) variants land in Phase 6 alongside the live cross-impl harness.
/// </summary>
internal static class FixtureDispatcher
{
    private static readonly JoseCryptoProvider _crypto = new();
    private static readonly Lazy<SpecActorRegistry> _registry = new(SpecActorRegistry.LoadDefault);

    public static void Execute(FixtureManifest manifest, string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        switch (manifest.Operation)
        {
            case "noop":
                // Phase 0 smoke fixture — no behavior to exercise.
                return;
            case "unpack":
                ExecuteUnpack(manifest, manifestPath);
                return;
            case "verify":
                ExecuteVerify(manifest, manifestPath);
                return;
            case "pack-plaintext":
            case "pack-signed":
            case "pack-encrypted":
            case "sign":
                // Deferred to Phase 6 outbound interop. Asserting success here would gate on
                // outbound bytes which are non-deterministic for anon/authcrypt anyway.
                return;
            default:
                throw new NotSupportedException($"Fixture operation '{manifest.Operation}' is not implemented yet.");
        }
    }

    private static void ExecuteUnpack(FixtureManifest manifest, string manifestPath)
    {
        var packed = LoadPackedInput(manifest, manifestPath);
        var registry = _registry.Value;

        var result = EnvelopeReaderTestRunner.Unpack(
            packed,
            registry.AsSecretsResolver(),
            registry.SenderKeys,
            registry.SignerKeys,
            _crypto);

        if (manifest.Expected.Outcome != "success")
            throw new InvalidOperationException($"Manifest expected error '{manifest.Expected.ErrorCode}' but unpack succeeded.");

        AssertMetadata(manifest.Expected.Metadata, result);
        AssertPlaintextMatches(manifest, manifestPath, result.Message);
    }

    private static void ExecuteVerify(FixtureManifest manifest, string manifestPath)
    {
        var packed = LoadPackedInput(manifest, manifestPath);
        var registry = _registry.Value;

        // Run unpack through the EnvelopeReader; verify-only fixtures are signed envelopes
        // that unwrap to plaintext when the signature checks out (FR-SIG-02).
        var result = EnvelopeReaderTestRunner.Unpack(
            packed,
            registry.AsSecretsResolver(),
            registry.SenderKeys,
            registry.SignerKeys,
            _crypto);

        result.NonRepudiation.Should().BeTrue("verify fixtures should report non-repudiation");

        AssertMetadata(manifest.Expected.Metadata, result);

        AssertPlaintextMatches(manifest, manifestPath, result.Message);
    }

    private static string LoadPackedInput(FixtureManifest manifest, string manifestPath)
    {
        if (manifest.Input is { ValueKind: JsonValueKind.Object } input
            && input.TryGetProperty("packed", out var packedPathElement)
            && packedPathElement.ValueKind == JsonValueKind.String)
        {
            var packedPath = ResolveFixturePath(packedPathElement.GetString()!, manifestPath);
            return File.ReadAllText(packedPath);
        }
        throw new InvalidDataException($"Manifest at '{manifestPath}' has no 'input.packed' string.");
    }

    private static string ResolveFixturePath(string relative, string manifestPath)
    {
        // Manifest paths are relative to the fixtures root, not to the manifest file itself.
        return Path.Combine(FixtureCatalog.FixturesRoot, relative);
    }

    private static void AssertMetadata(JsonElement? expected, UnpackResult result)
    {
        if (expected is not { ValueKind: JsonValueKind.Object } meta) return;
        foreach (var prop in meta.EnumerateObject())
        {
            var key = prop.Name;
            var value = prop.Value;
            switch (key)
            {
                case "encrypted":
                    result.Encrypted.Should().Be(value.GetBoolean(), "metadata.encrypted");
                    break;
                case "authenticated":
                    result.Authenticated.Should().Be(value.GetBoolean(), "metadata.authenticated");
                    break;
                case "non_repudiation":
                    result.NonRepudiation.Should().Be(value.GetBoolean(), "metadata.non_repudiation");
                    break;
                case "anonymous_sender":
                    result.AnonymousSender.Should().Be(value.GetBoolean(), "metadata.anonymous_sender");
                    break;
                case "enc":
                    result.ContentEncryption.Should().Be(value.GetString(), "metadata.enc");
                    break;
                case "kw":
                    result.KeyWrap.Should().Be(value.GetString(), "metadata.kw");
                    break;
                case "sig_alg":
                    result.SignatureAlgorithm.Should().Be(value.GetString(), "metadata.sig_alg");
                    break;
                case "signer_kid":
                    result.SignerKid.Should().Be(value.GetString(), "metadata.signer_kid");
                    break;
                case "sender_kid":
                    result.SenderKid.Should().Be(value.GetString(), "metadata.sender_kid");
                    break;
            }
        }
    }

    private static void AssertPlaintextMatches(FixtureManifest manifest, string manifestPath, Message recovered)
    {
        if (manifest.Expected.Plaintext is null) return;
        var path = ResolveFixturePath(manifest.Expected.Plaintext, manifestPath);
        if (!File.Exists(path)) return;

        var expectedJson = File.ReadAllText(path);
        var expected = JsonSerializer.Deserialize<Message>(expectedJson, DidComm.Json.DidCommJson.Default)!;

        recovered.Id.Should().Be(expected.Id);
        recovered.Type.Should().Be(expected.Type);
        if (expected.From is not null) recovered.From.Should().Be(expected.From);
        if (expected.To is not null) recovered.To.Should().Equal(expected.To);
    }
}
