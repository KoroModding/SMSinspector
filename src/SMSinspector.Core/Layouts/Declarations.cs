using System.Text;

namespace SMSinspector.Core.Layouts;

/// <summary>A template argument: a type, or a constant expression kept as text.</summary>
public sealed record TemplateArg(TypeSpec? Type, string Text)
{
    public override string ToString() => Type?.ToString() ?? Text;
}

/// <summary>
/// A type as written in a declaration: a name (possibly qualified, possibly a builtin
/// such as "unsigned char"), template arguments, pointer and reference levels, and
/// array dimensions as expressions.
/// </summary>
public sealed record TypeSpec(
    string Name,
    IReadOnlyList<TemplateArg> TemplateArgs,
    int PointerDepth = 0,
    bool IsReference = false,
    bool IsFunctionPointer = false,
    IReadOnlyList<string>? ArrayDims = null,
    bool IsConst = false)
{
    public IReadOnlyList<string> Dims => ArrayDims ?? [];

    /// <summary>Pointers, references and function pointers all take 4 bytes on the GameCube.</summary>
    public bool IsPointerLike => PointerDepth > 0 || IsReference || IsFunctionPointer;

    /// <summary>The name with template arguments, without pointers or arrays: "JGeometry::TVec3&lt;f32&gt;".</summary>
    public string BaseName => TemplateArgs.Count == 0 ? Name : $"{Name}<{string.Join(", ", TemplateArgs)}>";

    public TypeSpec WithoutDims() => this with { ArrayDims = null };

    public override string ToString()
    {
        var text = new StringBuilder();
        if (IsConst)
        {
            text.Append("const ");
        }

        text.Append(BaseName);
        if (IsFunctionPointer)
        {
            text.Append(" (*)(...)");
        }
        else
        {
            text.Append('*', PointerDepth);
            if (IsReference)
            {
                text.Append('&');
            }
        }

        foreach (var dim in Dims)
        {
            text.Append('[').Append(dim).Append(']');
        }

        return text.ToString();
    }
}

public enum ClassKind
{
    Class,
    Struct,
    Union,
}

public enum MemberKind
{
    /// <summary>A data member.</summary>
    Data,

    /// <summary>A comment-only line such as <c>/* 0x18 */ // vt</c>, marking the vtable pointer.</summary>
    VtableMarker,

    /// <summary>Any other comment-only offset line, such as <c>/* 0x04 */ // JKRThread</c> for a base class.</summary>
    OtherMarker,
}

public sealed record MemberDecl(
    string Name,
    TypeSpec Type,
    uint? CommentOffset,
    VersionMask Versions,
    int Line,
    MemberKind Kind = MemberKind.Data,
    string? MarkerText = null,
    string? BitWidth = null,
    uint? AlignAttribute = null)
{
    public bool IsBitField => BitWidth is not null;
}

public sealed record BaseSpec(TypeSpec Type, bool IsVirtual);

/// <summary>A class, struct or union definition found in a header.</summary>
public sealed class ClassDecl
{
    public required string Name { get; init; }

    /// <summary>Fully qualified name, for example "JDrama::TNameRef" or "TOuter::TInner".</summary>
    public required string QualifiedName { get; init; }

    /// <summary>The enclosing namespaces and classes, used to resolve the names it uses.</summary>
    public required string Scope { get; init; }

    public required ClassKind Kind { get; init; }

    public required string File { get; init; }

    public required int Line { get; init; }

    public required VersionMask Versions { get; init; }

    public IReadOnlyList<string> TemplateParams { get; init; } = [];

    /// <summary>Default arguments of the template parameters, null where there is none.</summary>
    public IReadOnlyList<TemplateArg?> TemplateParamDefaults { get; init; } = [];

    /// <summary>For an explicit specialization such as <c>template &lt;&gt; struct TVec3&lt;f32&gt;</c>: its arguments.</summary>
    public IReadOnlyList<TemplateArg>? SpecializationArgs { get; init; }

    public List<BaseSpec> Bases { get; } = [];

    public List<MemberDecl> Members { get; } = [];

    /// <summary>
    /// Whether the class declares virtual functions itself. When no base is polymorphic,
    /// the compiler places the vtable pointer where the first virtual function is declared.
    /// </summary>
    public bool DeclaresVirtual { get; set; }

    /// <summary>Number of data members declared before the first virtual function.</summary>
    public int? FirstVirtualAfterMembers { get; set; }

    public bool IsAnonymous { get; init; }

    public bool IsTemplate => TemplateParams.Count > 0 && SpecializationArgs is null;

    public override string ToString() => QualifiedName;
}

public sealed record Enumerator(string Name, string? Expression, VersionMask Versions);

public sealed record EnumDecl(string QualifiedName, string Scope, TypeSpec? UnderlyingType, IReadOnlyList<Enumerator> Enumerators, string File, int Line);

public sealed record TypedefDecl(string QualifiedName, string Scope, TypeSpec Target, VersionMask Versions, string File, int Line);

/// <summary>A named integer constant usable in array sizes: <c>static const int N = 4;</c> in a class.</summary>
public sealed record ConstantDecl(string QualifiedName, string Expression, VersionMask Versions);

/// <summary>Everything the parser found in one header.</summary>
public sealed class HeaderFile
{
    public required string Path { get; init; }

    public List<ClassDecl> Classes { get; } = [];

    public List<EnumDecl> Enums { get; } = [];

    public List<TypedefDecl> Typedefs { get; } = [];

    public List<ConstantDecl> Constants { get; } = [];

    public List<MacroDefinition> Macros { get; } = [];

    /// <summary>Statements in class bodies that looked like data members but could not be read.</summary>
    public List<string> Problems { get; } = [];
}
