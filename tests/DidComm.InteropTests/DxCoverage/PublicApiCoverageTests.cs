using System.IO;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.InteropTests.DxCoverage;

/// <summary>
/// The FR-DX-01 developer-experience gate: every public type and member of the six shipped
/// packages must be exercised by at least one sample under <c>samples/</c>, and the §14.4
/// API → sample coverage matrix must hold (FR-DX-09).
///
/// <para><b>How coverage is measured.</b> The shipped assemblies' public surface (public types;
/// public/protected methods, constructors, properties, events, fields) is enumerated with
/// Mono.Cecil, and every sample assembly's complete IL + metadata is walked for references into
/// them — type refs, member refs, generic arguments, attributes, base types, signatures,
/// local-variable types. A method also counts as covered when it implements an interface member
/// that is itself covered, or overrides a covered virtual/abstract member; a property is covered
/// through either accessor, an event through add/remove.</para>
///
/// <para><b>Structural exemptions</b> (documented as <see cref="StructuralExemptions"/>): enum
/// members and <c>const</c> fields (IL inlines the literal — use is undetectable), compiler-
/// generated members and record plumbing (<c>PrintMembers</c>, <c>Deconstruct</c>,
/// <c>EqualityContract</c>, <c>&lt;Clone&gt;$</c>, <c>op_Equality</c>/<c>op_Inequality</c>,
/// synthesized <c>Equals</c>/<c>GetHashCode</c>/<c>ToString</c> on records), <c>[Obsolete]</c>
/// members, and property accessors (counted through their property). A short, justified allowlist
/// (<c>coverage-allowlist.json</c>) covers members that are demonstrated but invisible in IL —
/// e.g. constructors invoked only reflectively by DI.</para>
/// </summary>
public sealed class PublicApiCoverageTests
{
    /// <summary>The structural exemptions the gate applies, spelled out for the report (FR-DX-01).</summary>
    private const string StructuralExemptions =
        "enum members + const fields (IL-inlined literals), compiler-generated members + record " +
        "plumbing (PrintMembers/Deconstruct/EqualityContract/<Clone>$/operators/synthesized " +
        "Equals-GetHashCode-ToString), [Obsolete] members, accessors counted through their property, " +
        "System.Object overrides (Equals(object)/GetHashCode/ToString — call sites bind through System.Object)";

    private readonly ITestOutputHelper _output;

    public PublicApiCoverageTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// FR-DX-01: the report must list 0 undemonstrated public members. On failure, every gap is
    /// printed assembly-qualified and sorted — the actionable list of members needing a sample.
    /// </summary>
    [Fact]
    public void EveryPublicMember_IsDemonstratedByASample()
    {
        var coverage = CoverageComputation.Instance;
        var allowlist = LoadAllowlist();

        // Allowlist hygiene first: an entry must name a real surface member (else it is stale)
        // and that member must actually be undemonstrated (else the entry should be deleted).
        var surfaceIds = coverage.Surface.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var uncoveredIds = coverage.Uncovered.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (member, reason) in allowlist)
        {
            reason.Should().NotBeNullOrWhiteSpace($"allowlist entry '{member}' needs a one-line justification");
            surfaceIds.Should().Contain(member,
                $"allowlist entry '{member}' no longer names a public surface member — delete the stale entry");
            uncoveredIds.Should().Contain(member,
                $"allowlist entry '{member}' is now demonstrated by a sample — delete the redundant entry");
        }

        var gaps = coverage.Uncovered.Where(m => !allowlist.ContainsKey(m.Id)).ToList();

        _output.WriteLine($"Public surface scanned      : {coverage.Surface.Count} members across {CoverageComputation.ShippedAssemblies.Count} assemblies");
        _output.WriteLine($"Covered by direct reference : {coverage.CoveredDirectly.Count}");
        _output.WriteLine($"Covered via interface/override mapping : {coverage.CoveredViaMapping.Count}");
        _output.WriteLine($"Allowlisted (justified)     : {allowlist.Count}");
        _output.WriteLine($"Structurally exempt         : {StructuralExemptions}");
        var e = coverage.Exemptions;
        _output.WriteLine($"  enum members/consts={e.EnumMembersAndConsts}, compiler-generated/record plumbing={e.CompilerGeneratedAndRecordPlumbing}, obsolete={e.Obsolete}, accessors={e.AccessorsCountedThroughOwner}, object-overrides={e.ObjectOverrides}");
        _output.WriteLine($"Undemonstrated              : {gaps.Count}");

        if (gaps.Count > 0)
        {
            var report = new StringBuilder();
            report.AppendLine($"{gaps.Count} public member(s) are not demonstrated by any sample (FR-DX-01).");
            report.AppendLine("Add a demonstration to the closest-fit sample, or (last resort) a justified allowlist entry:");
            foreach (var gap in gaps)
                report.AppendLine($"  [{gap.Kind}] {gap.Id}");
            Assert.Fail(report.ToString());
        }
    }

    /// <summary>
    /// FR-DX-09: the §14.4 API → sample coverage matrix, asserted row by row — every named member
    /// must be referenced by at least one of the row's named sample assemblies.
    ///
    /// <para><b>Mapping from the PRD's illustrative names to the real members.</b> §14.4 predates
    /// the implementation, so: the facade is <c>DidCommClient</c> (the PRD's illustrative
    /// <c>DidComm</c>); pack takes <c>PackEncryptedOptions</c> and unpack a plain <c>string</c>
    /// (no <c>Pack*Params</c>/<c>UnpackParams</c>); unpack metadata lives on <c>UnpackResult</c>
    /// itself (no separate <c>UnpackMetadata</c>); the builder is <c>new MessageBuilder()</c> (no
    /// static <c>Message.Builder</c>); threading's <c>WithAck</c> exists as written and
    /// <c>Message.Empty</c> is the Empty-1.0 builder factory (asserted in the Protocols row);
    /// rotation is <c>FromPriorBuilder.BuildAsync</c> (not <c>CreateSignedAsync</c>) +
    /// <c>MessageBuilder.WithFromPrior</c> + <c>UnpackResult.FromPrior</c> (claims record, not
    /// <c>FromPriorIssuer</c>); DI transport hooks are the per-transport
    /// <c>UseHttpTransport</c>/<c>UseWebSocketTransport</c> (no generic <c>UseTransport</c>);
    /// <c>NetDidKeyService</c> lives in <c>DidComm.Core</c>'s <c>DidComm.Resolution</c> namespace
    /// (the <c>DidComm.Adapters.NetDid</c> package carries the keystore bridge); the Secrets row's
    /// <c>Secret</c> type does not exist — private keys are JWKs (<c>DataProofsDotnet.Jose.Jwk</c>,
    /// outside the shipped surface), so the row asserts <c>ISecretsResolver</c> + the bridge;
    /// routing's forward flag is <c>PackEncryptedOptions.Forward</c>, demonstrated by the cookbook
    /// (added to that row's samples — noted drift from the PRD's "04" column); the Protocols row's
    /// <c>Empty</c> maps to <c>Message.Empty()</c> (the const-only <c>EmptyProtocol</c> holder is
    /// IL-invisible); profiles/i18n are <c>ProfileNegotiator</c> +
    /// <c>WithLang</c>/<c>WithAcceptLang</c>, and "accept selection" is
    /// <c>ProfileNegotiator.Choose</c>.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(CoverageMatrixRows))]
    public void CoverageMatrix_Section14_4_RowHolds(string group, string[] memberProbes, string[] sampleAssemblies)
    {
        var coverage = CoverageComputation.Instance;
        var rowRefs = new SampleReferences();
        foreach (var assembly in sampleAssemblies)
        {
            coverage.SampleRefsByAssembly.Should().ContainKey(assembly,
                $"§14.4 row '{group}' names sample assembly '{assembly}', which must exist in the test output");
            rowRefs.UnionWith(coverage.SampleRefsByAssembly[assembly]);
        }

        foreach (var probe in memberProbes)
        {
            IsReferenced(rowRefs, probe).Should().BeTrue(
                $"§14.4 row '{group}': '{probe}' must be demonstrated by at least one of [{string.Join(", ", sampleAssemblies)}]");
        }
    }

    /// <summary>
    /// The §14.4 matrix as data: group, real-member probes (a type full name, or
    /// <c>Type::Method</c>), and the demonstrating sample assemblies from the row's
    /// "Demonstrated in" column (01 → Quickstart, 02 → Cookbook, 03 → EnvelopesAndMessages,
    /// 04 → MediatorAgent, 05 → WebSocketChat, 06 → OutOfBand, 07 → ProblemsAndProtocols,
    /// 08 → Extensibility, 09 → NetDidIntegration, 10 → ProfilesAndI18n).
    /// </summary>
    public static TheoryData<string, string[], string[]> CoverageMatrixRows()
    {
        var quickstart = "DidComm.Samples.Quickstart";
        var cookbook = "DidComm.Samples.Cookbook";
        var envelopes = "DidComm.Samples.EnvelopesAndMessages";
        var mediator = "DidComm.Samples.MediatorAgent";
        var wsChat = "DidComm.Samples.WebSocketChat";
        var oob = "DidComm.Samples.OutOfBand";
        var problems = "DidComm.Samples.ProblemsAndProtocols";
        var extensibility = "DidComm.Samples.Extensibility";
        var netdid = "DidComm.Samples.NetDidIntegration";
        var profiles = "DidComm.Samples.ProfilesAndI18n";

        return new TheoryData<string, string[], string[]>
        {
            {
                "Facade",
                [
                    "DidComm.Facade.DidCommClient::PackEncryptedAsync",
                    "DidComm.Facade.DidCommClient::PackSignedAsync",
                    "DidComm.Facade.DidCommClient::PackPlaintextAsync",
                    "DidComm.Facade.DidCommClient::UnpackAsync",
                    "DidComm.Facade.DidCommClient::SendAsync",
                ],
                [quickstart, cookbook, envelopes]
            },
            {
                "Params/results",
                [
                    "DidComm.Facade.PackEncryptedOptions",
                    "DidComm.Facade.PackEncryptedResult",
                    "DidComm.Facade.UnpackResult",
                    "DidComm.Transports.SendOptions",
                    "DidComm.Transports.SendResult",
                ],
                [cookbook, envelopes]
            },
            {
                "Message model",
                [
                    "DidComm.Messages.Message",
                    "DidComm.Messages.MessageBuilder",
                    "DidComm.Messages.Attachment",
                    "DidComm.Messages.AttachmentData",
                    "DidComm.Facade.ContentEncryptionAlgorithm",
                ],
                [cookbook, envelopes]
            },
            {
                "Threading/ACKs",
                [
                    "DidComm.Messages.MessageBuilder::WithThid",
                    "DidComm.Messages.MessageBuilder::WithPthid",
                    "DidComm.Messages.MessageBuilder::WithPleaseAck",
                    "DidComm.Messages.MessageBuilder::WithAck",
                    "DidComm.Threading.ThreadState",
                ],
                [envelopes, problems]
            },
            {
                "Rotation",
                [
                    "DidComm.Protocols.Rotation.FromPriorBuilder::BuildAsync",
                    "DidComm.Messages.MessageBuilder::WithFromPrior",
                    "DidComm.Facade.UnpackResult::get_FromPrior",
                ],
                [envelopes]
            },
            {
                "DI",
                [
                    "DidComm.Extensions.DependencyInjection.DidCommServiceCollectionExtensions::AddDidComm",
                    "DidComm.Extensions.DependencyInjection.DidCommBuilder::UseNetDidResolver",
                    "DidComm.Extensions.DependencyInjection.DidCommBuilder::UseSecretsResolver",
                    "DidComm.Extensions.DependencyInjection.DidCommBuilder::AddProtocol",
                    "DidComm.Extensions.DependencyInjection.DidCommBuilder::Configure",
                ],
                [cookbook, extensibility]
            },
            {
                "Resolution",
                [
                    "DidComm.Resolution.IDidKeyService",
                    "DidComm.Resolution.NetDidKeyService",
                    "DidComm.Extensions.DependencyInjection.DidCommBuilder::UseNetDidResolver",
                    "DidComm.Exceptions.UnsupportedDidMethodException",
                ],
                [netdid]
            },
            {
                "Secrets",
                [
                    "DidComm.Secrets.ISecretsResolver",
                    "DidComm.Adapters.NetDid.NetDidKeyStoreSecretsResolver",
                ],
                [extensibility]
            },
            {
                "Transports",
                [
                    "DidComm.Transports.IDidCommTransport",
                    "DidComm.Transports.Http.HttpDidCommBuilderExtensions::UseHttpTransport",
                    "DidComm.AspNetCore.DidCommEndpointRouteBuilderExtensions::MapDidCommEndpoint",
                    "DidComm.Transports.WebSocket.WebSocketDidCommBuilderExtensions::UseWebSocketTransport",
                    "DidComm.AspNetCore.DidCommEndpointRouteBuilderExtensions::MapDidCommWebSocket",
                ],
                [mediator, wsChat, extensibility]
            },
            {
                // §14.4 names sample 04 alone; the Forward flag itself is demonstrated by the
                // cookbook (04 rides SendAsync's automatic wrapping), so the cookbook joins the row.
                "Routing",
                [
                    "DidComm.Facade.PackEncryptedOptions::get_Forward",
                    "DidComm.Protocols.Routing.ForwardProcessor",
                    "DidComm.Protocols.Routing.ForwardMessage::TryParse",
                ],
                [mediator, cookbook]
            },
            {
                "Protocols",
                [
                    "DidComm.Protocols.IProtocolHandler",
                    "DidComm.Protocols.ProtocolContext",
                    "DidComm.Protocols.TrustPing.TrustPing",
                    "DidComm.Protocols.DiscoverFeatures.DiscoverFeatures",
                    "DidComm.Protocols.ProblemReport.ProblemReport",
                    "DidComm.Protocols.OutOfBand.OutOfBand",
                    "DidComm.Messages.Message::Empty",
                ],
                [wsChat, oob, problems]
            },
            {
                "Profiles/i18n",
                [
                    "DidComm.Profiles.ProfileNegotiator",
                    "DidComm.Profiles.ProfileNegotiator::Choose",
                    "DidComm.Messages.MessageBuilder::WithLang",
                    "DidComm.Messages.MessageBuilder::WithAcceptLang",
                ],
                [profiles]
            },
            {
                "Exceptions",
                [
                    "DidComm.Exceptions.DidCommException",
                    "DidComm.Exceptions.MalformedMessageException",
                    "DidComm.Exceptions.ConsistencyException",
                    "DidComm.Exceptions.CryptoException",
                    "DidComm.Exceptions.DidResolutionException",
                    "DidComm.Exceptions.SecretNotFoundException",
                    "DidComm.Exceptions.TransportException",
                    "DidComm.Exceptions.ProtocolException",
                    "DidComm.Exceptions.UnsupportedDidMethodException",
                ],
                [envelopes, problems, netdid]
            },
        };
    }

    /// <summary>True when the row's samples reference the probed type or member.</summary>
    private static bool IsReferenced(SampleReferences refs, string probe)
    {
        var separator = probe.IndexOf("::", StringComparison.Ordinal);
        if (separator < 0)
            return refs.Types.Any(id => id.EndsWith("!" + probe, StringComparison.Ordinal));

        var type = probe[..separator];
        var member = probe[(separator + 2)..];
        var methodPrefix = "!" + type + "::" + member + "(";
        var fieldId = "!" + type + "::" + member;
        return refs.Methods.Any(id => id.Contains(methodPrefix, StringComparison.Ordinal))
               || refs.Fields.Any(id => id.EndsWith(fieldId, StringComparison.Ordinal));
    }

    private static Dictionary<string, string> LoadAllowlist()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "DxCoverage", "coverage-allowlist.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
            entries.Add(entry.GetProperty("member").GetString()!, entry.GetProperty("reason").GetString()!);
        return entries;
    }
}
