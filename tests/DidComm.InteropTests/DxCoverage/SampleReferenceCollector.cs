using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DidComm.InteropTests.DxCoverage;

/// <summary>
/// Every reference a sample assembly makes into the shipped assemblies, keyed by the same
/// canonical ids <see cref="MemberId"/> produces, so the FR-DX-01 gate can intersect the two sides.
/// </summary>
public sealed class SampleReferences
{
    /// <summary>Ids of shipped types the sample references anywhere in its metadata or IL.</summary>
    public HashSet<string> Types { get; } = [];

    /// <summary>Ids of shipped methods/ctors the sample calls, takes delegates of, or constructs with.</summary>
    public HashSet<string> Methods { get; } = [];

    /// <summary>Ids of shipped fields the sample loads or stores.</summary>
    public HashSet<string> Fields { get; } = [];

    /// <summary>Merges <paramref name="other"/> into this set (used to union all samples).</summary>
    public void UnionWith(SampleReferences other)
    {
        Types.UnionWith(other.Types);
        Methods.UnionWith(other.Methods);
        Fields.UnionWith(other.Fields);
    }
}

/// <summary>
/// Walks a sample assembly's complete metadata — not just call instructions: base types,
/// interfaces, generic arguments, attributes (including <c>typeof</c> arguments), field/property/
/// event/parameter/return types, local-variable signatures, catch clauses, and every IL operand —
/// and records each reference that lands in a shipped assembly. Compiler-generated types (lambda
/// display classes, async state machines) are walked too: that is where sample code's real calls
/// live.
/// </summary>
public static class SampleReferenceCollector
{
    /// <summary>Collects all shipped-assembly references made by <paramref name="module"/>.</summary>
    public static SampleReferences Collect(ModuleDefinition module, IReadOnlySet<string> shippedAssemblies)
    {
        var refs = new SampleReferences();
        AddAttributes(module.Assembly.CustomAttributes, refs, shippedAssemblies);
        AddAttributes(module.CustomAttributes, refs, shippedAssemblies);
        foreach (var type in module.GetTypes())
            CollectType(type, refs, shippedAssemblies);
        return refs;
    }

    private static void CollectType(TypeDefinition type, SampleReferences refs, IReadOnlySet<string> shipped)
    {
        AddType(type.BaseType, refs, shipped);
        foreach (var implementation in type.Interfaces)
            AddType(implementation.InterfaceType, refs, shipped);
        AddAttributes(type.CustomAttributes, refs, shipped);
        foreach (var genericParameter in type.GenericParameters)
        foreach (var constraint in genericParameter.Constraints)
            AddType(constraint.ConstraintType, refs, shipped);

        foreach (var field in type.Fields)
        {
            AddType(field.FieldType, refs, shipped);
            AddAttributes(field.CustomAttributes, refs, shipped);
        }

        foreach (var property in type.Properties)
        {
            AddType(property.PropertyType, refs, shipped);
            AddAttributes(property.CustomAttributes, refs, shipped);
        }

        foreach (var @event in type.Events)
        {
            AddType(@event.EventType, refs, shipped);
            AddAttributes(@event.CustomAttributes, refs, shipped);
        }

        foreach (var method in type.Methods)
            CollectMethod(method, refs, shipped);
    }

    private static void CollectMethod(MethodDefinition method, SampleReferences refs, IReadOnlySet<string> shipped)
    {
        // Implementing a shipped interface member (or overriding a shipped virtual) in a sample IS
        // its demonstration — consumer-implemented contracts like ISecretsResolver or
        // IProtocolHandler leave no MemberRef in IL, but the implementation is the usage.
        foreach (var implementedId in PublicSurfaceScanner.RootIdsOf(method, shipped))
            refs.Methods.Add(implementedId);

        AddType(method.ReturnType, refs, shipped);
        AddAttributes(method.CustomAttributes, refs, shipped);
        AddAttributes(method.MethodReturnType.CustomAttributes, refs, shipped);
        foreach (var parameter in method.Parameters)
        {
            AddType(parameter.ParameterType, refs, shipped);
            AddAttributes(parameter.CustomAttributes, refs, shipped);
        }

        foreach (var genericParameter in method.GenericParameters)
        foreach (var constraint in genericParameter.Constraints)
            AddType(constraint.ConstraintType, refs, shipped);

        if (!method.HasBody)
            return;

        foreach (var variable in method.Body.Variables)
            AddType(variable.VariableType, refs, shipped);
        foreach (var handler in method.Body.ExceptionHandlers)
            AddType(handler.CatchType, refs, shipped);

        foreach (var instruction in method.Body.Instructions)
        {
            switch (instruction.Operand)
            {
                case MethodReference methodRef:
                    AddMethod(methodRef, refs, shipped);
                    break;
                case FieldReference fieldRef:
                    AddField(fieldRef, refs, shipped);
                    break;
                case TypeReference typeRef:
                    AddType(typeRef, refs, shipped);
                    break;
            }
        }
    }

    private static void AddAttributes(
        IEnumerable<CustomAttribute> attributes, SampleReferences refs, IReadOnlySet<string> shipped)
    {
        foreach (var attribute in attributes)
        {
            AddMethod(attribute.Constructor, refs, shipped);
            foreach (var argument in attribute.ConstructorArguments)
                AddAttributeArgument(argument, refs, shipped);
            foreach (var named in attribute.Fields.Concat(attribute.Properties))
                AddAttributeArgument(named.Argument, refs, shipped);
        }
    }

    private static void AddAttributeArgument(
        CustomAttributeArgument argument, SampleReferences refs, IReadOnlySet<string> shipped)
    {
        switch (argument.Value)
        {
            case TypeReference typeRef: // typeof(...) argument
                AddType(typeRef, refs, shipped);
                break;
            case CustomAttributeArgument nested:
                AddAttributeArgument(nested, refs, shipped);
                break;
            case CustomAttributeArgument[] array:
                foreach (var element in array)
                    AddAttributeArgument(element, refs, shipped);
                break;
        }
    }

    private static void AddMethod(MethodReference? reference, SampleReferences refs, IReadOnlySet<string> shipped)
    {
        if (reference is null)
            return;
        if (reference is GenericInstanceMethod generic)
        {
            foreach (var argument in generic.GenericArguments)
                AddType(argument, refs, shipped);
        }

        // A MemberRef's signature blob carries TypeRefs to its parameter and return types — the
        // sample's metadata references them even though no separate IL operand does (this is how
        // an enum argument, IL-inlined to an int, still counts for its enum type).
        AddType(reference.DeclaringType, refs, shipped);
        AddType(reference.ReturnType, refs, shipped);
        foreach (var parameter in reference.Parameters)
            AddType(parameter.ParameterType, refs, shipped);

        MethodDefinition? definition;
        try
        {
            definition = reference.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            definition = null;
        }

        if (definition is not null && shipped.Contains(definition.Module.Assembly.Name.Name))
            refs.Methods.Add(MemberId.ForMethod(definition));
    }

    private static void AddField(FieldReference reference, SampleReferences refs, IReadOnlySet<string> shipped)
    {
        AddType(reference.DeclaringType, refs, shipped);
        AddType(reference.FieldType, refs, shipped);
        FieldDefinition? definition;
        try
        {
            definition = reference.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            definition = null;
        }

        if (definition is not null && shipped.Contains(definition.Module.Assembly.Name.Name))
            refs.Fields.Add(MemberId.ForField(definition));
    }

    private static void AddType(TypeReference? reference, SampleReferences refs, IReadOnlySet<string> shipped)
    {
        while (reference is TypeSpecification specification)
        {
            if (reference is GenericInstanceType generic)
            {
                foreach (var argument in generic.GenericArguments)
                    AddType(argument, refs, shipped);
            }

            reference = specification.ElementType;
        }

        if (reference is null or GenericParameter)
            return;

        TypeDefinition? definition;
        try
        {
            definition = reference.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            definition = null;
        }

        if (definition is null || !shipped.Contains(definition.Module.Assembly.Name.Name))
            return;

        refs.Types.Add(MemberId.ForType(definition));
        AddType(definition.DeclaringType, refs, shipped); // a nested-type reference demonstrates its outer type too
    }
}
