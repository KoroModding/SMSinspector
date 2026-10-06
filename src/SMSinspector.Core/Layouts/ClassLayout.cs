namespace SMSinspector.Core.Layouts;

public enum OffsetSource
{
    /// <summary>Taken from the header's offset comment.</summary>
    Comment,

    /// <summary>Computed from the sizes and alignments of the preceding members.</summary>
    Computed,

    /// <summary>Not known: a preceding size could not be determined.</summary>
    Unknown,
}

/// <summary>One field of a class as laid out for one version. Offsets are relative to the class.</summary>
/// <param name="Member">The declaration, or null for the vtable pointer the compiler adds.</param>
/// <param name="Identity">The field's name with where it comes from: the header line, or the class for a hidden pointer.</param>
/// <param name="CommentOffset">The offset comment as written in the header, if any.</param>
/// <param name="CommentVerified">True when the computed offset reproduced a comment written for this version.</param>
/// <param name="BitOffset">For a bit-field: its first bit inside the storage unit, counted from the most significant bit.</param>
/// <param name="BitWidth">For a bit-field: its width in bits.</param>
/// <remarks>
/// Hidden pointers the compiler adds have no declaration: the vtable pointer ("vtable")
/// and, for each direct virtual base, a pointer to it ("vbase ...").
/// </remarks>
public sealed record FieldLayout(
    MemberDecl? Member,
    SourcedName Identity,
    string TypeName,
    uint? Offset,
    uint? Size,
    uint Align,
    OffsetSource Source,
    uint? CommentOffset,
    bool CommentVerified,
    int? BitOffset = null,
    int? BitWidth = null)
{
    public string Name => Identity.Value;

    public bool IsVtablePointer => Member is null && Name == "vtable";

    public bool IsHidden => Member is null;
}

public sealed record BaseLayout(ClassLayout Layout, uint? Offset);

public enum IssueKind
{
    /// <summary>The computed offset is lower than the comment: bytes the headers do not account for.</summary>
    Gap,

    /// <summary>The computed offset is higher than the comment: a member overlaps the previous one.</summary>
    Overlap,

    /// <summary>Offset comments go backwards.</summary>
    OutOfOrder,

    UnknownType,
    UnknownBase,
    VtableMismatch,

    /// <summary>A PAL offset could not be derived safely; later offsets are withheld.</summary>
    PalUnverified,
}

public sealed record LayoutIssue(IssueKind Kind, string Member, string Message);

/// <summary>The layout of one class (or template instance) for one game version.</summary>
public sealed class ClassLayout
{
    /// <summary>The class name with where it comes from: the header line of its definition.</summary>
    public required SourcedName Identity { get; init; }

    public string Name => Identity.Value;

    public required ClassDecl Decl { get; init; }

    public required VersionMask Version { get; init; }

    public List<BaseLayout> Bases { get; } = [];

    public List<FieldLayout> Fields { get; } = [];

    public List<LayoutIssue> Issues { get; } = [];

    /// <summary>Size of a complete object, virtual bases included.</summary>
    public uint? Size { get; set; }

    /// <summary>
    /// Size without virtual bases: what the class takes when it is a base of another class.
    /// Virtual bases are shared and placed once, at the end of the complete object.
    /// </summary>
    public uint? NonVirtualSize { get; set; }

    /// <summary>Virtual bases anywhere in the hierarchy, each once, in placement order.</summary>
    public List<ClassLayout> VirtualBases { get; } = [];

    public uint Align { get; set; } = 1;

    public bool HasVptr { get; set; }

    public uint? VptrOffset { get; set; }

    /// <summary>Where the bases end and the class's own members start.</summary>
    public uint? BasesEnd { get; set; }

    /// <summary>Offset comments written for this version that the computation could check.</summary>
    public int CommentsChecked { get; set; }

    public int CommentsMatched { get; set; }

    /// <summary>
    /// True when this version's layout differs from the other one because of version blocks
    /// in the class, its bases or its member types. Only meaningful for PAL.
    /// </summary>
    public bool IsVersionAffected { get; set; }

    /// <summary>For PAL: the offset after which PAL offsets could not be verified, if any.</summary>
    public uint? PalUnverifiedAfter { get; set; }

    /// <summary>Why the PAL offsets after <see cref="PalUnverifiedAfter"/> are withheld.</summary>
    public string? PalUnverifiedReason { get; set; }

    /// <summary>For PAL: a hint that the layout is wrong somewhere, with its reason; offsets are kept.</summary>
    public PalSuspect? PalSuspect { get; set; }

    /// <summary>Offset comments that contradict the sizes of the members before them, in member order.</summary>
    public List<CommentConflict> CommentConflicts { get; } = [];

    /// <summary>What main.dol said about the first comment conflict, when it was checked.</summary>
    public CommentCheck? CommentCheck { get; set; }

    /// <summary>For a template instance: the types its parameters stand for, to resolve members typed with them.</summary>
    internal IReadOnlyDictionary<string, TypeSpec> BoundTypes { get; init; } = new Dictionary<string, TypeSpec>();

    /// <summary>For a template instance: the values of its non-type parameters.</summary>
    internal IReadOnlyDictionary<string, long> BoundValues { get; init; } = new Dictionary<string, long>();

    public override string ToString() => $"{Name} ({Version})";
}

/// <summary>A field with its offset from the start of the outermost object, after flattening bases.</summary>
public sealed record FlatField(ClassLayout Owner, FieldLayout Field, uint? AbsoluteOffset);

public static class ClassLayoutExtensions
{
    /// <summary>
    /// Every field of the object in memory order: base classes first (recursively), then the
    /// class's own fields. This is how a TBlah view shows TNameRef's fields first.
    /// </summary>
    public static List<FlatField> Flatten(this ClassLayout layout)
    {
        var result = new List<FlatField>();
        Flatten(layout, 0, result);
        return result.OrderBy(f => f.AbsoluteOffset ?? uint.MaxValue).ToList();
    }

    private static void Flatten(ClassLayout layout, uint? origin, List<FlatField> result)
    {
        foreach (var baseLayout in layout.Bases)
        {
            Flatten(baseLayout.Layout, origin + baseLayout.Offset, result);
        }

        foreach (var field in layout.Fields)
        {
            // An inherited vtable pointer is listed once, by the class that introduced it.
            result.Add(new FlatField(layout, field, origin + field.Offset));
        }
    }
}

/// <summary>
/// A hint, weaker than a <see cref="PalContradiction"/>, that the PAL layout of a class is
/// wrong between two offsets. Shown with the layout and listed in the report; offsets are kept.
/// </summary>
public sealed record PalSuspect(string ClassName, uint FirstOffset, uint LastOffset, string Reason);

/// <summary>
/// Evidence that the PAL layout of a class is wrong from <paramref name="FirstOffset"/> on
/// (relative to the class), for example game code that reads a member at another offset.
/// </summary>
public sealed record PalContradiction(string ClassName, uint FirstOffset, string Reason);
