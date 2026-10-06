using SMSinspector.Core.Layouts;

namespace SMSinspector.Core.Live;

public enum RowKind
{
    /// <summary>A member, or a member of a class stored inline in another one.</summary>
    Field,

    /// <summary>One element of an array member.</summary>
    Element,

    /// <summary>Bytes no member covers, shown raw.</summary>
    Gap,
}

/// <summary>
/// One line of an object's field grid. Rows describe the class, not one object: they are
/// built once per class and decoded against the bytes of each object read.
/// </summary>
public sealed class FieldRow
{
    private IReadOnlyList<FieldRow>? _children;

    public required RowKind Kind { get; init; }

    /// <summary>The member this row shows, with its header line. Null for a gap.</summary>
    public SourcedName? Name { get; init; }

    /// <summary>What the grid prints: the member name, <c>[3]</c> for an element, <c>(gap)</c>.</summary>
    public required string Label { get; init; }

    /// <summary>The row's path from the object, such as <c>mPos.x</c> or <c>mItems[2]</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Offset from the start of the object; null when the layout withholds it.</summary>
    public required uint? Offset { get; init; }

    public required uint Size { get; init; }

    public required string TypeName { get; init; }

    public required DataType Type { get; init; }

    /// <summary>For a bit-field: its first bit, counted from the most significant bit of its storage unit.</summary>
    public int? BitOffset { get; init; }

    public int? BitWidth { get; init; }

    public int Depth { get; init; }

    /// <summary>Why the value may be wrong whatever it holds: a PAL suspect range, or a withheld offset.</summary>
    public string? Note { get; init; }

    /// <summary>A member the decomp has not named yet (<c>unkXX</c>); the weaker rules skip it.</summary>
    public bool HasUnknownName { get; init; }

    /// <summary>For an enum: the enumerator name of each value.</summary>
    public IReadOnlyDictionary<long, string>? EnumNames { get; init; }

    internal TypeSpec? Spec { get; init; }

    internal Func<FieldRow, IReadOnlyList<FieldRow>>? Expand { get; init; }

    public bool IsBitField => BitWidth is not null;

    public bool HasChildren => Expand is not null && Offset is not null && !IsBitField && Type is DataType.Composite or DataType.ArrayOf { Count: > 0 };

    /// <summary>Members of an inline class or elements of an array, built on first use.</summary>
    public IReadOnlyList<FieldRow> Children => _children ??= HasChildren ? Expand!(this) : [];

    public override string ToString() => Offset is { } offset ? $"0x{offset:X} {Path}" : $"? {Path}";
}
