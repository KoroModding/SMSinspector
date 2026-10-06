namespace SMSinspector.Core.Layouts;

public sealed partial class LayoutEngine
{
    /// <summary>
    /// What a field of <paramref name="owner"/> holds, resolved from the class that declares
    /// it, so template parameters and nested types resolve. Hidden pointers the compiler adds
    /// are pointers; a type that cannot be resolved is <see cref="DataType.Opaque"/>.
    /// </summary>
    public DataType DescribeField(ClassLayout owner, FieldLayout field)
    {
        if (field.Member is not { } member)
        {
            return new DataType.Pointer(null);
        }

        var bindings = new Bindings(owner.BoundTypes, owner.BoundValues);
        return Describe(member.Type, owner.Decl.QualifiedName, bindings, owner.Version, 0) ?? new DataType.Opaque(field.Size ?? 0);
    }

    /// <summary>
    /// The field's type as declared, with a template parameter replaced by the type it stands
    /// for: <c>T x</c> in <c>TVec&lt;f32&gt;</c> reads <c>f32</c>. Null for a hidden pointer.
    /// </summary>
    public TypeSpec? DeclaredType(ClassLayout owner, FieldLayout field)
    {
        if (field.Member is not { } member)
        {
            return null;
        }

        var type = member.Type;
        return type.TemplateArgs.Count == 0 && owner.BoundTypes.TryGetValue(type.Name, out var bound) ? Compose(type, bound) : type;
    }

    private DataType? Describe(TypeSpec type, string scope, Bindings bindings, VersionMask version, int depth)
    {
        if (depth > 32)
        {
            return null;
        }

        if (type.Dims.Count > 0)
        {
            long count = 1;
            foreach (var dim in type.Dims)
            {
                if (dim.Length == 0)
                {
                    count = 0;
                }
                else if (Evaluate(dim, scope, bindings, version) is { } value && value >= 0)
                {
                    count *= value;
                }
                else
                {
                    return null;
                }
            }

            return Describe(type.WithoutDims(), scope, bindings, version, depth + 1) is { } element && count <= uint.MaxValue
                ? new DataType.ArrayOf(element, (uint)count)
                : null;
        }

        if (type.IsFunctionPointer)
        {
            return new DataType.Pointer(null);
        }

        if (type.IsPointerLike)
        {
            var target = type.IsReference ? type with { IsReference = false } : type with { PointerDepth = type.PointerDepth - 1 };
            return new DataType.Pointer(Canonical(target, scope, bindings, 0));
        }

        if (bindings.Types.TryGetValue(type.Name, out var bound) && type.TemplateArgs.Count == 0)
        {
            return Describe(Compose(type, bound), "", Bindings.Empty, version, depth + 1);
        }

        if (Builtins.TryGetValue(type.Name, out var builtin))
        {
            return new DataType.Scalar(BuiltinKind(type.Name), builtin.Size);
        }

        switch (catalog.Resolve(type.Name, scope))
        {
            case ResolvedName.Typedef typedef:
                return Describe(Compose(type, typedef.Declaration.Target), typedef.Declaration.Scope, Bindings.Empty, version, depth + 1);

            case ResolvedName.Enum enumDecl:
                return TrySizeElement(type, scope, bindings, version, out var enumSize, out _, out _, 0)
                    ? new DataType.Enumeration(enumDecl.Declaration, enumSize)
                    : null;

            case ResolvedName.Class cls:
                var layout = ResolveInstance(cls, type.TemplateArgs, scope, bindings, version);
                return layout?.Size is { } size ? new DataType.Composite(layout, size) : null;

            default:
                return null;
        }
    }

    // The game is built with "-char signed", so a plain char is signed.
    private static ScalarKind BuiltinKind(string name) => name switch
    {
        "float" or "double" or "long double" => ScalarKind.Float,
        "bool" => ScalarKind.Bool,
        "wchar_t" => ScalarKind.Unsigned,
        _ when name.StartsWith("unsigned", StringComparison.Ordinal) => ScalarKind.Unsigned,
        _ => ScalarKind.Signed,
    };
}
