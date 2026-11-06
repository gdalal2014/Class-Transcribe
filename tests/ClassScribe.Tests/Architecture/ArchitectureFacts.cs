using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ClassScribe.Tests;

/// <summary>
/// One built assembly as the architecture test sees it (architecture 2.4): its name and informational version, the assemblies
/// it references, and its top-level types with what each of them uses.
/// </summary>
/// <param name="Name">The assembly's name, such as ClassScribe.Core, or ClassScribe for the exe.</param>
/// <param name="InformationalVersion">The informational version stamped into it, such as 0.4.0+f5f2fb0..., or empty.</param>
/// <param name="References">The names of the assemblies it references.</param>
/// <param name="Types">Its top-level types, each with its nested types folded in.</param>
public sealed record AssemblyFacts(string Name, string InformationalVersion, IReadOnlyList<string> References, IReadOnlyList<TypeFacts> Types);

/// <summary>
/// A top-level type with its nested types folded in: its name, whether the compiler or a generator made it, whether it or a
/// nested type declares a P/Invoke or is a COM import, and every type and member it refers to.
/// </summary>
/// <param name="FullName">The namespace and the name, such as ClassScribe.Core.Durations; the name alone in the global namespace.</param>
/// <param name="Namespace">The namespace, or null in the global namespace.</param>
/// <param name="IsGenerated">True for a type marked CompilerGenerated or GeneratedCode, or whose name begins with an angle bracket.</param>
/// <param name="DeclaresPInvoke">True when it or a nested type declares a method with a P/Invoke import.</param>
/// <param name="IsComImport">True when it or a nested type is a COM import.</param>
/// <param name="Uses">Every type and member it refers to outside its own assembly, from its signatures, attributes, base types and IL.</param>
public sealed record TypeFacts(string FullName, string? Namespace, bool IsGenerated, bool DeclaresPInvoke, bool IsComImport, IReadOnlyList<TypeUse> Uses);

/// <summary>
/// One reference from a type to a type of another assembly: the type by its full name (a nested type with a plus sign, a
/// generic type without its arity), the member when one is named (a method, a constructor as .ctor, a field, a property's
/// accessor as get_Name or set_Name), and the member's parameter types by their full names.
/// </summary>
/// <param name="Type">The full name of the type referred to.</param>
/// <param name="Member">The member's name, or null for the type alone.</param>
/// <param name="Parameters">The parameter types of a method or constructor, empty for a field or a type.</param>
public sealed record TypeUse(string Type, string? Member, IReadOnlyList<string> Parameters)
{
    /// <summary>The use in one line: the type, the member and the parameters, as a test names it.</summary>
    public override string ToString() => Member is null ? Type : $"{Type}.{Member}({string.Join(", ", Parameters)})";
}

/// <summary>
/// Reads the facts of a built assembly with System.Reflection.Metadata, which is part of the shared framework (architecture
/// 2.4): the assembly references, the type and member references of every signature, attribute and base type, and the
/// operands of each method's IL. It loads nothing and runs nothing: the file is read as bytes.
/// </summary>
public static class ArchitectureFacts
{
    /// <summary>Reads the facts of the assembly at <paramref name="path"/>.</summary>
    public static AssemblyFacts Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var assembly = metadata.GetAssemblyDefinition();
        var references = metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        var reader = new Reader(pe, metadata);
        var types = new List<TypeFacts>();
        foreach (var handle in metadata.TypeDefinitions)
        {
            if (!metadata.GetTypeDefinition(handle).GetDeclaringType().IsNil) continue;
            types.Add(reader.FactsOf(handle));
        }

        return new AssemblyFacts(metadata.GetString(assembly.Name), reader.InformationalVersion(assembly), references, types);
    }

    /// <summary>Reads one assembly's metadata and IL, type by type.</summary>
    private sealed class Reader(PEReader pe, MetadataReader metadata)
    {
        private static readonly Dictionary<int, OperandType> Operands = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opcode => (int)(ushort)opcode.Value, opcode => opcode.OperandType);

        private readonly TypeNames names = new(metadata);
        private HashSet<string> seen = [];
        private List<TypeUse> uses = [];

        /// <summary>The informational version the assembly carries, or an empty string.</summary>
        public string InformationalVersion(AssemblyDefinition assembly)
        {
            foreach (var handle in assembly.GetCustomAttributes())
            {
                var attribute = metadata.GetCustomAttribute(handle);
                if (AttributeTypeOf(attribute) != "System.Reflection.AssemblyInformationalVersionAttribute") continue;
                var blob = metadata.GetBlobReader(attribute.Value);
                blob.ReadUInt16();
                return blob.ReadSerializedString() ?? "";
            }

            return "";
        }

        /// <summary>The facts of one top-level type, its nested types folded in.</summary>
        public TypeFacts FactsOf(TypeDefinitionHandle handle)
        {
            seen = [];
            uses = [];
            names.Seen = Add;
            var type = metadata.GetTypeDefinition(handle);
            var generated = false;
            var pinvoke = false;
            var comImport = false;
            Collect(handle, top: true, ref generated, ref pinvoke, ref comImport);
            var ns = metadata.GetString(type.Namespace);
            var name = metadata.GetString(type.Name);
            var fullName = ns.Length == 0 ? name : $"{ns}.{name}";
            return new TypeFacts(fullName, ns.Length == 0 ? null : ns, generated || name.StartsWith('<'), pinvoke, comImport, uses);
        }

        /// <summary>Walks one type definition and its nested types: attributes, base types, fields, methods, properties, events and IL.</summary>
        private void Collect(TypeDefinitionHandle handle, bool top, ref bool generated, ref bool pinvoke, ref bool comImport)
        {
            var type = metadata.GetTypeDefinition(handle);
            if ((type.Attributes & TypeAttributes.Import) != 0) comImport = true;
            foreach (var attributeHandle in type.GetCustomAttributes())
            {
                var attributeType = Attribute(attributeHandle);
                if (top && attributeType is "System.Runtime.CompilerServices.CompilerGeneratedAttribute" or "System.CodeDom.Compiler.GeneratedCodeAttribute") generated = true;
            }

            if (!type.BaseType.IsNil) AddType(type.BaseType);
            foreach (var implementation in type.GetInterfaceImplementations()) AddType(metadata.GetInterfaceImplementation(implementation).Interface);
            foreach (var fieldHandle in type.GetFields())
            {
                var field = metadata.GetFieldDefinition(fieldHandle);
                field.DecodeSignature(names, null);
                foreach (var attributeHandle in field.GetCustomAttributes()) Attribute(attributeHandle);
            }

            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0) pinvoke = true;
                method.DecodeSignature(names, null);
                foreach (var attributeHandle in method.GetCustomAttributes()) Attribute(attributeHandle);
                foreach (var parameterHandle in method.GetParameters())
                {
                    foreach (var attributeHandle in metadata.GetParameter(parameterHandle).GetCustomAttributes()) Attribute(attributeHandle);
                }

                if (method.RelativeVirtualAddress != 0) ReadBody(pe.GetMethodBody(method.RelativeVirtualAddress));
            }

            foreach (var propertyHandle in type.GetProperties())
            {
                var property = metadata.GetPropertyDefinition(propertyHandle);
                property.DecodeSignature(names, null);
                foreach (var attributeHandle in property.GetCustomAttributes()) Attribute(attributeHandle);
            }

            foreach (var eventHandle in type.GetEvents())
            {
                var declared = metadata.GetEventDefinition(eventHandle);
                AddType(declared.Type);
                foreach (var attributeHandle in declared.GetCustomAttributes()) Attribute(attributeHandle);
            }

            foreach (var nested in type.GetNestedTypes()) Collect(nested, top: false, ref generated, ref pinvoke, ref comImport);
        }

        /// <summary>Notes a custom attribute's constructor as a use of its type and gives that type's full name.</summary>
        private string? Attribute(CustomAttributeHandle handle)
        {
            var attribute = metadata.GetCustomAttribute(handle);
            var typeName = AttributeTypeOf(attribute);
            if (typeName is not null) Add(new TypeUse(typeName, ".ctor", []));
            return typeName;
        }

        /// <summary>The full name of an attribute's type, or null when its constructor is this assembly's own.</summary>
        private string? AttributeTypeOf(CustomAttribute attribute) => attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => ParentName(metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent),
            HandleKind.MethodDefinition => names.Definition(metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()),
            _ => null,
        };

        /// <summary>Reads a method body's IL and notes every type and member its operands name, and the types of its locals.</summary>
        private void ReadBody(MethodBodyBlock body)
        {
            if (!body.LocalSignature.IsNil) metadata.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(names, null);
            var il = body.GetILReader();
            while (il.RemainingBytes > 0)
            {
                int code = il.ReadByte();
                if (code == 0xFE) code = 0xFE00 | il.ReadByte();
                if (!Operands.TryGetValue(code, out var operand)) throw new InvalidOperationException($"Unknown IL opcode 0x{code:X}.");
                switch (operand)
                {
                    case OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok or OperandType.InlineSig:
                        AddToken(MetadataTokens.EntityHandle(il.ReadInt32()));
                        break;
                    case OperandType.InlineSwitch:
                        var count = il.ReadInt32();
                        il.Offset += 4 * count;
                        break;
                    case OperandType.InlineI8 or OperandType.InlineR:
                        il.Offset += 8;
                        break;
                    case OperandType.InlineBrTarget or OperandType.InlineI or OperandType.InlineString or OperandType.ShortInlineR:
                        il.Offset += 4;
                        break;
                    case OperandType.InlineVar:
                        il.Offset += 2;
                        break;
                    case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                        il.Offset += 1;
                        break;
                    default:
                        break;
                }
            }
        }

        /// <summary>Notes what an IL operand token names: a member of another assembly, a type, or a generic method instance.</summary>
        private void AddToken(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.MemberReference:
                    var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
                    var parent = ParentName(member.Parent);
                    if (parent is null) break;
                    var parameters = member.GetKind() == MemberReferenceKind.Method
                        ? member.DecodeMethodSignature(names, null).ParameterTypes.ToList()
                        : (IReadOnlyList<string>)[];
                    if (member.GetKind() == MemberReferenceKind.Field) member.DecodeFieldSignature(names, null);
                    Add(new TypeUse(parent, metadata.GetString(member.Name), parameters));
                    break;
                case HandleKind.MethodSpecification:
                    var specification = metadata.GetMethodSpecification((MethodSpecificationHandle)handle);
                    specification.DecodeSignature(names, null);
                    AddToken(specification.Method);
                    break;
                case HandleKind.TypeReference or HandleKind.TypeSpecification:
                    AddType(handle);
                    break;
                case HandleKind.StandaloneSignature:
                    metadata.GetStandaloneSignature((StandaloneSignatureHandle)handle).DecodeMethodSignature(names, null);
                    break;
                default:
                    break;
            }
        }

        /// <summary>Notes a type a handle names: a reference to another assembly's type, or a constructed type, whose parts the decoder notes.</summary>
        private void AddType(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeReference:
                    Add(new TypeUse(names.Reference((TypeReferenceHandle)handle), null, []));
                    break;
                case HandleKind.TypeSpecification:
                    metadata.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(names, null);
                    break;
                default:
                    break;
            }
        }

        /// <summary>The full name of a member reference's parent type without its type arguments, or null for this assembly's own.</summary>
        private string? ParentName(EntityHandle parent) => parent.Kind switch
        {
            HandleKind.TypeReference => names.Reference((TypeReferenceHandle)parent),
            HandleKind.TypeSpecification => Open(metadata.GetTypeSpecification((TypeSpecificationHandle)parent).DecodeSignature(names, null)),
            _ => null,
        };

        /// <summary>A constructed type's name without its type arguments: List&lt;T&gt; as System.Collections.Generic.List.</summary>
        private static string Open(string name)
        {
            var angle = name.IndexOf('<');
            return angle < 0 ? name : name[..angle];
        }

        /// <summary>Notes a type seen in a signature, as the decoder reports it.</summary>
        private void Add(string typeName) => Add(new TypeUse(typeName, null, []));

        /// <summary>Notes a use once.</summary>
        private void Add(TypeUse use)
        {
            if (seen.Add(use.ToString())) uses.Add(use);
        }
    }

    /// <summary>
    /// Names the types a signature decoder meets, as full names, and reports every type of another assembly it meets to
    /// <see cref="Seen"/>, so that a type named only in a signature counts as used.
    /// </summary>
    private sealed class TypeNames(MetadataReader metadata) : ISignatureTypeProvider<string, object?>
    {
        /// <summary>Called with the full name of every type of another assembly the decoder meets.</summary>
        public Action<string>? Seen { get; set; }

        /// <summary>The full name of a type reference, nested types joined with a plus sign and the arity removed.</summary>
        public string Reference(TypeReferenceHandle handle)
        {
            var reference = metadata.GetTypeReference(handle);
            var name = Strip(metadata.GetString(reference.Name));
            if (reference.ResolutionScope.Kind == HandleKind.TypeReference) return $"{Reference((TypeReferenceHandle)reference.ResolutionScope)}+{name}";
            var ns = metadata.GetString(reference.Namespace);
            return ns.Length == 0 ? name : $"{ns}.{name}";
        }

        /// <summary>The full name of a type defined in this assembly.</summary>
        public string Definition(TypeDefinitionHandle handle)
        {
            var definition = metadata.GetTypeDefinition(handle);
            var name = Strip(metadata.GetString(definition.Name));
            var declaring = definition.GetDeclaringType();
            if (!declaring.IsNil) return $"{Definition(declaring)}+{name}";
            var ns = metadata.GetString(definition.Namespace);
            return ns.Length == 0 ? name : $"{ns}.{name}";
        }

        /// <summary>A generic type's name without its arity: List`1 as List.</summary>
        private static string Strip(string name)
        {
            var tick = name.IndexOf('`');
            return tick < 0 ? name : name[..tick];
        }

        /// <inheritdoc />
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => $"System.{typeCode}";

        /// <inheritdoc />
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Definition(handle);

        /// <inheritdoc />
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var name = Reference(handle);
            Seen?.Invoke(name);
            return name;
        }

        /// <inheritdoc />
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        /// <inheritdoc />
        public string GetSZArrayType(string elementType) => $"{elementType}[]";

        /// <inheritdoc />
        public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', Math.Max(0, shape.Rank - 1))}]";

        /// <inheritdoc />
        public string GetByReferenceType(string elementType) => $"{elementType}&";

        /// <inheritdoc />
        public string GetPointerType(string elementType) => $"{elementType}*";

        /// <inheritdoc />
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => $"{genericType}<{string.Join(",", typeArguments)}>";

        /// <inheritdoc />
        public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";

        /// <inheritdoc />
        public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";

        /// <inheritdoc />
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        /// <inheritdoc />
        public string GetPinnedType(string elementType) => elementType;

        /// <inheritdoc />
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    }
}
