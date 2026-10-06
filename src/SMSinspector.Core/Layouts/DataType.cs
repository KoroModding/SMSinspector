namespace SMSinspector.Core.Layouts;

public enum ScalarKind
{
    Signed,
    Unsigned,
    Float,
    Bool,
}

/// <summary>
/// What the bytes of a field hold, with typedefs, template parameters and enums resolved:
/// what a value reader needs to decode it.
/// </summary>
public abstract record DataType(uint Size)
{
    /// <summary>An integer, float or bool of 1, 2, 4 or 8 bytes.</summary>
    public sealed record Scalar(ScalarKind Kind, uint Size) : DataType(Size);

    /// <summary>A pointer or reference. <paramref name="Target"/> is null for function pointers.</summary>
    public sealed record Pointer(TypeSpec? Target) : DataType(4);

    public sealed record Enumeration(EnumDecl Declaration, uint Size) : DataType(Size);

    /// <summary>A class or struct stored inline, such as a vector member.</summary>
    public sealed record Composite(ClassLayout Layout, uint Size) : DataType(Size);

    public sealed record ArrayOf(DataType Element, uint Count) : DataType(Element.Size * Count);

    /// <summary>A type that could not be resolved: shown as raw bytes.</summary>
    public sealed record Opaque(uint Size) : DataType(Size);
}
