using Mono.Cecil;

namespace DidComm.InteropTests.DxCoverage;

/// <summary>What kind of public surface item a <see cref="SurfaceMember"/> describes.</summary>
public enum SurfaceKind
{
    /// <summary>A public (or nested-public/protected) type.</summary>
    Type,

    /// <summary>A public/protected method or constructor.</summary>
    Method,

    /// <summary>A public/protected property (covered through either accessor).</summary>
    Property,

    /// <summary>A public/protected event (covered through add/remove).</summary>
    Event,

    /// <summary>A public/protected non-const field.</summary>
    Field,
}

/// <summary>
/// One demonstrable item of a shipped package's public surface (FR-DX-01). <see cref="Id"/> is the
/// canonical identity string both sides of the coverage computation speak (see
/// <see cref="MemberId"/>); <see cref="AccessorIds"/> carries the method ids a property/event is
/// covered through; <see cref="RootIds"/> carries the interface members this member implements and
/// the virtual/abstract base members it overrides — coverage of a root covers the member.
/// </summary>
public sealed record SurfaceMember(
    string Id,
    SurfaceKind Kind,
    string DeclaringTypeId,
    IReadOnlyList<string> AccessorIds,
    IReadOnlyList<string> RootIds);

/// <summary>Counts of members structurally exempt from the FR-DX-01 gate (see the test's rules).</summary>
public sealed class SurfaceExemptions
{
    /// <summary>Enum members and <c>const</c> fields — IL inlines the literal, so use is undetectable.</summary>
    public int EnumMembersAndConsts;

    /// <summary>Members carrying <c>[CompilerGenerated]</c> plus record plumbing by name.</summary>
    public int CompilerGeneratedAndRecordPlumbing;

    /// <summary>Members carrying <c>[Obsolete]</c> — deprecated surface is not demonstrated.</summary>
    public int Obsolete;

    /// <summary>Property/event accessor methods — counted through their property/event, not alone.</summary>
    public int AccessorsCountedThroughOwner;

    /// <summary>
    /// Overrides of <c>System.Object</c> members (<c>Equals(object)</c> / <c>GetHashCode</c> /
    /// <c>ToString</c>): C# call sites always emit the <c>System.Object</c> MemberRef (or a
    /// <c>constrained.</c> callvirt), so use of the override is undetectable in IL.
    /// </summary>
    public int ObjectOverrides;
}

/// <summary>Builds the canonical member-identity strings used across the coverage computation.</summary>
public static class MemberId
{
    /// <summary>Id of a type: <c>Assembly!Namespace.Type</c> (nested types use <c>/</c>).</summary>
    public static string ForType(TypeDefinition type)
        => $"{type.Module.Assembly.Name.Name}!{type.FullName}";

    /// <summary>Id of a method/ctor: <c>Assembly!Type::Name(paramType,paramType)</c>.</summary>
    public static string ForMethod(MethodDefinition method)
        => $"{ForType(method.DeclaringType)}::{method.Name}({string.Join(",", method.Parameters.Select(p => p.ParameterType.FullName))})";

    /// <summary>Id of a field: <c>Assembly!Type::Name</c>.</summary>
    public static string ForField(FieldDefinition field)
        => $"{ForType(field.DeclaringType)}::{field.Name}";

    /// <summary>Id of a property: <c>Assembly!Type::prop:Name</c>.</summary>
    public static string ForProperty(PropertyDefinition property)
        => $"{ForType(property.DeclaringType)}::prop:{property.Name}";

    /// <summary>Id of an event: <c>Assembly!Type::event:Name</c>.</summary>
    public static string ForEvent(EventDefinition @event)
        => $"{ForType(@event.DeclaringType)}::event:{@event.Name}";
}

/// <summary>
/// Enumerates the demonstrable public surface of a shipped assembly: public types, public/protected
/// methods, constructors, properties, events and fields — minus the structural exemptions the
/// FR-DX-01 gate documents (enum members/consts, compiler-generated + record plumbing, obsolete
/// members, accessors counted through their property).
/// </summary>
public static class PublicSurfaceScanner
{
    /// <summary>
    /// Record plumbing the C# compiler synthesizes; undemonstrable as distinct API. Synthesized
    /// members normally carry <c>[CompilerGenerated]</c>, but the exemption is name-based as well
    /// so the gate does not hinge on a compiler emission detail.
    /// </summary>
    private static readonly string[] RecordPlumbingNames =
        ["PrintMembers", "Deconstruct", "<Clone>$", "op_Equality", "op_Inequality", "Equals", "GetHashCode", "ToString"];

    /// <summary>Scans <paramref name="module"/> and returns its demonstrable public surface.</summary>
    /// <param name="module">A shipped assembly's main module.</param>
    /// <param name="shippedAssemblies">Names of all shipped assemblies (roots outside them are ignored).</param>
    /// <param name="exemptions">Accumulates structural-exemption counts for the gate's report.</param>
    public static List<SurfaceMember> Scan(
        ModuleDefinition module, IReadOnlySet<string> shippedAssemblies, SurfaceExemptions exemptions)
    {
        var surface = new List<SurfaceMember>();
        foreach (var type in module.GetTypes())
        {
            if (!IsPublicSurfaceType(type))
                continue;

            var typeId = MemberId.ForType(type);
            surface.Add(new SurfaceMember(typeId, SurfaceKind.Type, typeId, [], []));

            // Enum members are const fields (IL inlines them); a delegate's Invoke/ctor plumbing is
            // runtime-managed. The type itself is still demonstrable surface, its members are not.
            if (type.IsEnum)
            {
                exemptions.EnumMembersAndConsts += type.Fields.Count(f => f.IsLiteral);
                continue;
            }

            if (IsDelegate(type))
                continue;

            var isRecord = IsRecord(type);
            var accessorMethods = new HashSet<MethodDefinition>();

            foreach (var property in type.Properties)
                CollectProperty(type, property, isRecord, shippedAssemblies, surface, accessorMethods, exemptions);
            foreach (var @event in type.Events)
                CollectEvent(@event, shippedAssemblies, surface, accessorMethods, exemptions);
            foreach (var method in type.Methods)
                CollectMethod(type, method, isRecord, shippedAssemblies, surface, accessorMethods, exemptions);
            foreach (var field in type.Fields)
                CollectField(field, surface, exemptions);
        }

        return surface;
    }

    private static void CollectProperty(
        TypeDefinition type,
        PropertyDefinition property,
        bool isRecord,
        IReadOnlySet<string> shippedAssemblies,
        List<SurfaceMember> surface,
        HashSet<MethodDefinition> accessorMethods,
        SurfaceExemptions exemptions)
    {
        var accessors = new[] { property.GetMethod, property.SetMethod }
            .Where(a => a is not null && IsAccessible(a)).Select(a => a!).ToList();
        if (accessors.Count == 0)
            return;

        foreach (var accessor in accessors)
            accessorMethods.Add(accessor);
        exemptions.AccessorsCountedThroughOwner += accessors.Count;

        // EqualityContract is record plumbing; [CompilerGenerated]/[Obsolete] properties are exempt.
        if (IsCompilerGenerated(property.CustomAttributes) || (isRecord && property.Name == "EqualityContract"))
        {
            exemptions.CompilerGeneratedAndRecordPlumbing++;
            return;
        }

        if (IsObsolete(property.CustomAttributes))
        {
            exemptions.Obsolete++;
            return;
        }

        surface.Add(new SurfaceMember(
            MemberId.ForProperty(property),
            SurfaceKind.Property,
            MemberId.ForType(type),
            accessors.Select(MemberId.ForMethod).ToList(),
            accessors.SelectMany(a => RootIdsOf(a, shippedAssemblies)).Distinct().ToList()));
    }

    private static void CollectEvent(
        EventDefinition @event,
        IReadOnlySet<string> shippedAssemblies,
        List<SurfaceMember> surface,
        HashSet<MethodDefinition> accessorMethods,
        SurfaceExemptions exemptions)
    {
        var accessors = new[] { @event.AddMethod, @event.RemoveMethod }
            .Where(a => a is not null && IsAccessible(a)).Select(a => a!).ToList();
        if (accessors.Count == 0)
            return;

        foreach (var accessor in accessors)
            accessorMethods.Add(accessor);
        exemptions.AccessorsCountedThroughOwner += accessors.Count;

        if (IsObsolete(@event.CustomAttributes))
        {
            exemptions.Obsolete++;
            return;
        }

        surface.Add(new SurfaceMember(
            MemberId.ForEvent(@event),
            SurfaceKind.Event,
            MemberId.ForType(@event.DeclaringType),
            accessors.Select(MemberId.ForMethod).ToList(),
            accessors.SelectMany(a => RootIdsOf(a, shippedAssemblies)).Distinct().ToList()));
    }

    private static void CollectMethod(
        TypeDefinition type,
        MethodDefinition method,
        bool isRecord,
        IReadOnlySet<string> shippedAssemblies,
        List<SurfaceMember> surface,
        HashSet<MethodDefinition> accessorMethods,
        SurfaceExemptions exemptions)
    {
        if (!IsAccessible(method) || method.IsConstructor && method.IsStatic)
            return;
        if (accessorMethods.Contains(method))
            return; // counted through its property/event above

        if (IsCompilerGenerated(method.CustomAttributes)
            || method.Name.StartsWith('<')
            || (isRecord && RecordPlumbingNames.Contains(method.Name)))
        {
            exemptions.CompilerGeneratedAndRecordPlumbing++;
            return;
        }

        if (IsObsolete(method.CustomAttributes))
        {
            exemptions.Obsolete++;
            return;
        }

        if (IsSystemObjectOverride(method))
        {
            exemptions.ObjectOverrides++;
            return;
        }

        surface.Add(new SurfaceMember(
            MemberId.ForMethod(method),
            SurfaceKind.Method,
            MemberId.ForType(type),
            [],
            RootIdsOf(method, shippedAssemblies).ToList()));
    }

    private static void CollectField(FieldDefinition field, List<SurfaceMember> surface, SurfaceExemptions exemptions)
    {
        if (!(field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly))
            return;

        if (field.IsLiteral)
        {
            exemptions.EnumMembersAndConsts++; // const: IL inlines the literal — use is undetectable
            return;
        }

        if (IsCompilerGenerated(field.CustomAttributes) || field.Name.StartsWith('<'))
        {
            exemptions.CompilerGeneratedAndRecordPlumbing++;
            return;
        }

        if (IsObsolete(field.CustomAttributes))
        {
            exemptions.Obsolete++;
            return;
        }

        surface.Add(new SurfaceMember(
            MemberId.ForField(field), SurfaceKind.Field, MemberId.ForType(field.DeclaringType), [], []));
    }

    /// <summary>
    /// The interface members <paramref name="method"/> implements (explicitly via
    /// <c>.override</c> or implicitly by signature) and the virtual/abstract base members it
    /// overrides — restricted to the shipped assemblies. Used in both directions: coverage of a
    /// root covers a shipped implementation, and a sample method implementing a shipped interface
    /// member demonstrates that interface member.
    /// </summary>
    public static IEnumerable<string> RootIdsOf(MethodDefinition method, IReadOnlySet<string> shippedAssemblies)
    {
        foreach (var explicitTarget in method.Overrides)
        {
            if (TryResolve(explicitTarget) is { } target && InShipped(target.DeclaringType, shippedAssemblies))
                yield return MemberId.ForMethod(target);
        }

        if (!method.IsVirtual)
            yield break;

        // Overridden virtual/abstract member up the base chain.
        for (var baseRef = method.DeclaringType.BaseType; baseRef is not null;)
        {
            var baseDef = TryResolve(baseRef);
            if (baseDef is null)
                break;
            if (InShipped(baseDef, shippedAssemblies))
            {
                var overridden = baseDef.Methods.FirstOrDefault(m => m.IsVirtual && SignatureMatches(m, method));
                if (overridden is not null)
                    yield return MemberId.ForMethod(overridden);
            }

            baseRef = baseDef.BaseType;
        }

        // Implicit interface implementation, across the whole interface closure of the type.
        foreach (var interfaceRef in InterfaceClosure(method.DeclaringType))
        {
            var interfaceDef = TryResolve(interfaceRef);
            if (interfaceDef is null || !InShipped(interfaceDef, shippedAssemblies))
                continue;
            var declared = interfaceDef.Methods.FirstOrDefault(m => SignatureMatches(m, method));
            if (declared is not null)
                yield return MemberId.ForMethod(declared);
        }
    }

    private static IEnumerable<TypeReference> InterfaceClosure(TypeDefinition type)
    {
        for (TypeDefinition? current = type; current is not null; current = current.BaseType is { } b ? TryResolve(b) : null)
        {
            foreach (var implementation in current.Interfaces)
            {
                yield return implementation.InterfaceType;
                if (TryResolve(implementation.InterfaceType) is { } def)
                {
                    foreach (var inherited in def.Interfaces)
                        yield return inherited.InterfaceType;
                }
            }
        }
    }

    /// <summary>
    /// True for an override of <c>Equals(object)</c>, <c>GetHashCode()</c> or <c>ToString()</c>:
    /// every C# call site binds these through <c>System.Object</c>, so the override itself can
    /// never appear as a MemberRef in sample IL — a structural exemption, like enum members.
    /// </summary>
    private static bool IsSystemObjectOverride(MethodDefinition method)
    {
        if (!method.IsVirtual || method.IsNewSlot)
            return false;
        return method.Name switch
        {
            "ToString" or "GetHashCode" => method.Parameters.Count == 0,
            "Equals" => method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == "System.Object",
            _ => false,
        };
    }

    private static bool SignatureMatches(MethodDefinition candidate, MethodDefinition method)
        => candidate.Name == method.Name
           && candidate.Parameters.Count == method.Parameters.Count
           && candidate.GenericParameters.Count == method.GenericParameters.Count
           && candidate.Parameters.Zip(method.Parameters)
               .All(pair => pair.First.ParameterType.FullName == pair.Second.ParameterType.FullName);

    private static bool InShipped(TypeDefinition type, IReadOnlySet<string> shippedAssemblies)
        => shippedAssemblies.Contains(type.Module.Assembly.Name.Name);

    private static TypeDefinition? TryResolve(TypeReference reference)
    {
        try
        {
            return reference.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    private static MethodDefinition? TryResolve(MethodReference reference)
    {
        try
        {
            return reference.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    private static bool IsPublicSurfaceType(TypeDefinition type)
    {
        if (IsCompilerGenerated(type.CustomAttributes) || type.Name.StartsWith('<'))
            return false;
        for (var current = type; ; current = current.DeclaringType)
        {
            if (current.DeclaringType is null)
                return current.IsPublic;
            if (!(current.IsNestedPublic || current.IsNestedFamily || current.IsNestedFamilyOrAssembly))
                return false;
        }
    }

    private static bool IsAccessible(MethodDefinition method)
        => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static bool IsRecord(TypeDefinition type)
        => type.Methods.Any(m => m.Name == "<Clone>$")
           || type.Properties.Any(p => p.Name == "EqualityContract");

    private static bool IsDelegate(TypeDefinition type)
        => type.BaseType?.FullName == "System.MulticastDelegate";

    private static bool IsCompilerGenerated(IEnumerable<CustomAttribute> attributes)
        => attributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");

    private static bool IsObsolete(IEnumerable<CustomAttribute> attributes)
        => attributes.Any(a => a.AttributeType.FullName == "System.ObsoleteAttribute");
}
