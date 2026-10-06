namespace SMSinspector.Core;

public enum ProvenanceKind
{
    /// <summary>Declared in a decomp header: file and line.</summary>
    Header,

    /// <summary>Added by the compiler (vtable or virtual base pointer) to a class declared in a header.</summary>
    Compiler,

    /// <summary>Read from the game's executable: address and decoded instructions.</summary>
    Executable,

    /// <summary>A function body in the decomp: file, line and function.</summary>
    DecompBody,

    /// <summary>Named by another class at the same offset: that member's header line.</summary>
    Sibling,

    /// <summary>A symbol of symbols.txt: the file, the mangled name and its address.</summary>
    Symbol,
}

/// <summary>
/// Where a name comes from, precise enough for the user to go and check it (plan 5.8).
/// Every factory demands the facts its kind needs, so an empty provenance cannot be built.
/// </summary>
public sealed record Provenance
{
    private Provenance(ProvenanceKind kind, string file, int? line, uint? address, string detail)
    {
        Kind = kind;
        File = file;
        Line = line;
        Address = address;
        Detail = detail;
    }

    public ProvenanceKind Kind { get; }

    /// <summary>Path relative to the decomp clone, or the executable's path.</summary>
    public string File { get; }

    public int? Line { get; }

    /// <summary>Game address, for <see cref="ProvenanceKind.Executable"/>.</summary>
    public uint? Address { get; }

    /// <summary>What was read there: the declaration, the instructions, the function.</summary>
    public string Detail { get; }

    public static Provenance Header(string file, int line, string detail = "") =>
        new(ProvenanceKind.Header, Required(file), Positive(line), null, detail);

    public static Provenance Compiler(string file, int line, string detail) =>
        new(ProvenanceKind.Compiler, Required(file), Positive(line), null, Required(detail));

    public static Provenance Executable(string file, uint address, string instructions) =>
        new(ProvenanceKind.Executable, Required(file), null, address, Required(instructions));

    public static Provenance DecompBody(string file, int line, string function) =>
        new(ProvenanceKind.DecompBody, Required(file), Positive(line), null, Required(function));

    /// <summary>A name read from a symbol, for example a class from its <c>__vt__</c> symbol.</summary>
    public static Provenance Symbol(string file, string mangledName, uint address) =>
        new(ProvenanceKind.Symbol, Required(file), null, address, Required(mangledName));

    /// <summary>A name borrowed from a sibling's member: that member's header line.</summary>
    public static Provenance Sibling(Provenance member, string detail) =>
        member is { Kind: ProvenanceKind.Header, Line: { } line }
            ? new(ProvenanceKind.Sibling, member.File, line, null, Required(detail))
            : throw new ArgumentException("A sibling name comes from a header line.", nameof(member));

    public override string ToString()
    {
        var place = Address is { } address ? $"{File} at 0x{address:X8}" : Line is { } line ? $"{File}:{line}" : File;
        return Detail.Length == 0 ? place : $"{place} ({Detail})";
    }

    private static string Required(string value) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A provenance needs this value.", nameof(value)) : value;

    private static int Positive(int line) =>
        line > 0 ? line : throw new ArgumentOutOfRangeException(nameof(line), line, "Line numbers start at 1.");
}

/// <summary>A name the tool shows, always paired with where it comes from.</summary>
public sealed record SourcedName
{
    public SourcedName(string value, Provenance source)
    {
        Value = string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A name cannot be empty.", nameof(value)) : value;
        Source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public string Value { get; }

    public Provenance Source { get; }

    public override string ToString() => Value;
}
