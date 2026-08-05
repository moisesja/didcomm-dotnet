using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DidComm.InteropTests.DxCoverage;

/// <summary>
/// Rewrites a copy of an assembly so that executing selected methods leaves an observable trace,
/// for the execution half of the FR-DX-01 gate (<see cref="PublicApiExecutionTests"/>).
///
/// <para><b>Mechanism.</b> A static probe type (<see cref="ProbeTypeFullName"/>) holding one
/// <c>bool[]</c> is injected into the assembly. Every selected method body gets a four-instruction
/// prologue — <c>ldsfld Hits; ldc.i4 index; ldc.i4.1; stelem.i1</c> — so the first executed
/// instruction of the method records its hit. The write is idempotent and thread-safe (a racing
/// <c>true</c> store), allocation-free, and immune to JIT inlining: the probe is part of the IL,
/// so it runs wherever the body runs. The caller keeps the returned index → member-id map and
/// reads the array back by reflection after driving the instrumented code.</para>
/// </summary>
internal static class ExecutionInstrumenter
{
    /// <summary>Namespace-qualified name of the injected probe type.</summary>
    internal const string ProbeTypeFullName = "DidComm.DxCoverage.Injected.ExecutionProbe";

    /// <summary>Name of the probe's <c>bool[]</c> hit field (index-aligned with the returned map).</summary>
    internal const string ProbeHitsFieldName = "Hits";

    /// <summary>
    /// Instruments <paramref name="sourcePath"/> into <paramref name="targetPath"/>.
    /// <paramref name="idsRecordedBy"/> maps a method to the canonical member ids its execution
    /// demonstrates (see <see cref="MemberId"/>); an empty result leaves the method untouched.
    /// Returns the hit-index → member-ids map, aligned with the injected <c>bool[]</c>.
    /// </summary>
    internal static List<string[]> Instrument(
        string sourcePath,
        string targetPath,
        ReaderParameters readerParameters,
        Func<MethodDefinition, string[]> idsRecordedBy)
    {
        using var module = ModuleDefinition.ReadModule(sourcePath, readerParameters);

        var map = new List<string[]>();
        var targets = new List<MethodDefinition>();
        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue; // abstract / extern / runtime-managed: nothing executes here directly

                var ids = idsRecordedBy(method);
                if (ids.Length == 0)
                    continue;

                targets.Add(method);
                map.Add(ids);
            }
        }

        var boolArray = new ArrayType(module.TypeSystem.Boolean);
        var probeType = new TypeDefinition(
            "DidComm.DxCoverage.Injected",
            "ExecutionProbe",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class,
            module.TypeSystem.Object);
        var hitsField = new FieldDefinition(
            ProbeHitsFieldName, FieldAttributes.Public | FieldAttributes.Static, boolArray);
        probeType.Fields.Add(hitsField);

        // .cctor allocates the array; ldsfld in any probe prologue guarantees it ran first.
        var cctor = new MethodDefinition(
            ".cctor",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig
            | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        var il = cctor.Body.GetILProcessor();
        il.Emit(OpCodes.Ldc_I4, targets.Count);
        il.Emit(OpCodes.Newarr, module.TypeSystem.Boolean);
        il.Emit(OpCodes.Stsfld, hitsField);
        il.Emit(OpCodes.Ret);
        probeType.Methods.Add(cctor);
        module.Types.Add(probeType);

        for (var index = 0; index < targets.Count; index++)
        {
            var body = targets[index].Body;
            var processor = body.GetILProcessor();
            var first = body.Instructions[0];

            // Inserting before the original first instruction keeps every branch target and
            // exception-handler range valid (they still point at the original instruction; a loop
            // that jumps back merely skips the probe, which only needs to fire once).
            processor.InsertBefore(first, Instruction.Create(OpCodes.Ldsfld, hitsField));
            processor.InsertBefore(first, Instruction.Create(OpCodes.Ldc_I4, index));
            processor.InsertBefore(first, Instruction.Create(OpCodes.Ldc_I4_1));
            processor.InsertBefore(first, Instruction.Create(OpCodes.Stelem_I1));
            body.MaxStackSize = Math.Max(body.MaxStackSize, 3);
        }

        module.Write(targetPath);
        return map;
    }
}
