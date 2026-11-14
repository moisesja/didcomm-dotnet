using System.IO;
using AwesomeAssertions;
using Mono.Cecil;
using Xunit;

namespace DidComm.InteropTests.DxCoverage;

/// <summary>
/// The FR-DX-01 scanner and C# 14 extension members (#96 design §6.4). An <c>extension(T)</c> block
/// compiles to a plain static implementation method on the enclosing static class — what call
/// sites reference in IL — plus a compiler grouping type (<c>&lt;G&gt;$…</c>, with a nested
/// <c>&lt;M&gt;$…</c> marker) that carries skeleton copies of the members. The gate must count the
/// static class and the implementation method, and nothing from the grouping type. This pins both
/// halves, with the grouping type proven present (so the "nothing surfaces" half is not vacuous).
/// </summary>
public sealed class ExtensionMemberSurfaceTests
{
    private const string ExtensionsType = "DidComm.Mediator.Client.MediationServiceBuilderExtensions";
    private const string ForMediationId = "DidComm.Mediator!" + ExtensionsType + "::ForMediation(DidComm.Mediator.Coordination.MediationGrant)";

    [Fact]
    public void The_extension_block_surfaces_as_the_static_class_and_its_implementation_method_only()
    {
        var surface = CoverageComputation.Instance.Surface.Select(m => m.Id).ToList();

        surface.Should().Contain("DidComm.Mediator!" + ExtensionsType);
        surface.Should().Contain(ForMediationId, "the implementation method is what DidCommServiceBuilder.ForMediation(grant) call sites reference");
        surface.Should().NotContain(id => id.Contains("<G>$", StringComparison.Ordinal) || id.Contains("<M>$", StringComparison.Ordinal) || id.Contains("<Extension>$", StringComparison.Ordinal),
            "the compiler's grouping and marker types are not demonstrable surface");
    }

    [Fact]
    public void The_compiler_grouping_type_exists_and_the_implementation_method_is_not_compiler_generated()
    {
        using var module = ModuleDefinition.ReadModule(Path.Combine(AppContext.BaseDirectory, "DidComm.Mediator.dll"));
        var extensions = module.GetType(ExtensionsType);
        extensions.Should().NotBeNull();

        extensions!.NestedTypes.Should().Contain(t => t.Name.StartsWith("<G>$", StringComparison.Ordinal),
            "C# 14 emits the grouping type the scanner skips by its '<' prefix; if the emission changes, revisit the scanner's exemption");

        var implementation = extensions.Methods.Single(m => m.Name == "ForMediation");
        implementation.IsStatic.Should().BeTrue();
        implementation.IsPublic.Should().BeTrue();
        implementation.CustomAttributes.Should().NotContain(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute",
            "a [CompilerGenerated] implementation method would be exempted silently and the gate would never ask for its demonstration");
    }
}
