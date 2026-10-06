using System.Text.RegularExpressions;
using SMSinspector.Core.Layouts;

namespace SMSinspector.Core.Names;

/// <summary>How strong a link between an original name and an unknown member is, strongest first.</summary>
public enum LinkLevel
{
    /// <summary>The game's code: a two-instruction accessor reads or writes the member.</summary>
    DolAccessor,

    /// <summary>A decomp body that is a single <c>return unkXX;</c> or <c>unkXX = argument;</c>.</summary>
    DecompBodyMatching,

    /// <summary>A short decomp body that uses the member some other way.</summary>
    DecompBodyIndirect,

    /// <summary>A sibling class names its member at the same offset. A hint only.</summary>
    SiblingOffset,
}

/// <summary>An original name put forward for an unknown member, with where it comes from.</summary>
/// <param name="Origin">Where to check it: the address and instructions in main.dol, the decomp body, or the sibling's header line.</param>
/// <param name="Suggestion">The member name the method suggests ("mSpeed"), or null when it suggests none.</param>
/// <param name="Source">The method or sibling member the link comes from.</param>
/// <param name="Evidence">What was read: the decoded instruction, the body, or the sibling classes.</param>
/// <param name="Note">A caveat, for example an instruction width that differs from the member size.</param>
public sealed record Candidate(LinkLevel Level, Provenance Origin, string? Suggestion, string Source, string Evidence, string? Note = null)
{
    public Provenance Origin { get; } = Origin ?? throw new ArgumentNullException(nameof(Origin));

    /// <summary>The suggestion as a name paired with its provenance, the form the UI shows (plan 5.8).</summary>
    public SourcedName? SuggestedName => Suggestion is null ? null : new SourcedName(Suggestion, Origin);
}

/// <summary>A member still called <c>unkXX</c> or <c>field_0xXX</c>, with its candidates, strongest first.</summary>
public sealed record UnknownMember(string ClassName, string Name, string TypeName, uint Offset, uint? Size, string File, IReadOnlyList<Candidate> Candidates);

/// <summary>Everything the extractor read, for one game version.</summary>
public sealed record NameSources(
    OriginalMethods Methods,
    ScannedSource Sources,
    ExecutableLookup Executable,
    string MapMessage,
    bool MapUsed,
    int ParamInitCount,
    int ParamInitUnknownCount,
    int SourceFileCount,
    IReadOnlySet<string>? OtherVersionFunctions = null,
    IReadOnlyList<FreeFunction>? FreeFunctions = null);

/// <summary>
/// Puts original names next to members the decomp still calls <c>unkXX</c> (plan 5.7). It
/// never renames anything: each candidate carries its source and level, and the user
/// checks it in game before naming the member.
/// </summary>
public static partial class NameExtractor
{
    private static readonly string[] AccessorPrefixes = ["check", "reset", "clear", "get", "set", "inc", "dec", "off", "on", "is"];

    public static bool IsUnknownName(string name) => UnknownName().IsMatch(name);

    /// <summary>"getSpeed" gives "mSpeed"; a name without an accessor prefix gives null.</summary>
    public static string? SuggestMemberName(string methodName)
    {
        foreach (var prefix in AccessorPrefixes)
        {
            if (methodName.Length > prefix.Length
                && methodName.StartsWith(prefix, StringComparison.Ordinal)
                && char.IsAsciiLetterUpper(methodName[prefix.Length]))
            {
                return "m" + methodName[prefix.Length..];
            }
        }

        return null;
    }

    /// <summary>
    /// Runs the extractor. The main.dol check (<see cref="DolLayoutCheck"/>) runs first and
    /// hands its contradictions to <paramref name="engine"/>, so no candidate lands on an
    /// offset the game's code contradicts.
    /// </summary>
    public static ExtractionReport Run(LayoutEngine engine, NameSources sources, VersionMask version = VersionMask.Pal)
    {
        var layoutCheck = version == VersionMask.Pal ? DolLayoutCheck.Apply(engine, sources) : LayoutCheckResult.NotRun;
        var stats = new ExtractionStats();
        var context = new Context(engine, version, stats);

        foreach (var decl in engine.Catalog.AllClasses.Where(c => !c.IsTemplate && c.SpecializationArgs is null))
        {
            context.Layout(decl.QualifiedName);
        }

        LinkDolAccessors(context, sources, stats);
        LinkDecompBodies(context, sources, stats);
        LinkSiblings(context);

        var members = context.Unknown.Values
            .Select(u => u.ToRecord())
            .OrderBy(m => m.ClassName, StringComparer.Ordinal)
            .ThenBy(m => m.Offset)
            .ToList();

        return new ExtractionReport(sources, stats, members, layoutCheck);
    }

    private static void LinkDolAccessors(Context context, NameSources sources, ExtractionStats stats)
    {
        if (!sources.Executable.IsUsable)
        {
            return;
        }

        var image = sources.Executable.Image!;
        foreach (var method in sources.Methods.All)
        {
            if (method.Address is not { } address || method.Mangled is null || IsConstructorOrDestructor(method.ClassName, method.Name))
            {
                continue;
            }

            var code = AccessorDecoder.Decode(address, method.Size, a => image.TryReadWord(a, out var word) ? word : null);
            stats.CountShape(code.Shape);
            if (!code.IsAccessor)
            {
                continue;
            }

            if (context.Layout(method.ClassName) is not { } layout)
            {
                stats.AccessorWithoutLayout++;
                continue;
            }

            var flat = layout.Flatten();
            var atOffset = flat.Where(f => f.AbsoluteOffset == code.Offset && f.Field.Member?.Kind == MemberKind.Data).ToList();
            var unknown = atOffset.FirstOrDefault(f => IsUnknownName(f.Field.Name));
            if (unknown is null)
            {
                if (atOffset.Count > 0)
                {
                    stats.AccessorOnNamedMember++;
                }
                else if (flat.Any(f => f.AbsoluteOffset < code.Offset && f.Field.Size is { } size && code.Offset < f.AbsoluteOffset + size))
                {
                    stats.AccessorInsideMember++;
                }
                else
                {
                    stats.AccessorNoMember++;
                }

                continue;
            }

            string? note = null;
            if (unknown.Field.Size is { } memberSize && memberSize != code.Width)
            {
                note = $"the instruction moves {code.Width} bytes, the member is {memberSize} bytes";
            }

            stats.AccessorLinked++;
            var instructions = $"{code.Instruction}; blr";
            context.Add(unknown.Owner.Name, unknown.Field, new Candidate(
                LinkLevel.DolAccessor,
                Provenance.Executable(sources.Executable.ShownPath, address, instructions),
                SuggestMemberName(method.Name),
                $"{method.Mangled}, size 0x{method.Size:X}",
                $"0x{address:X8}: {instructions}",
                note));
        }
    }

    private static void LinkDecompBodies(Context context, NameSources sources, ExtractionStats stats)
    {
        foreach (var body in sources.Sources.Bodies)
        {
            if (IsConstructorOrDestructor(body.ClassName, body.Name))
            {
                continue;
            }

            var references = BodyAnalyzer.UnknownReferences(body.Body);
            if (references.Count == 0)
            {
                continue;
            }

            if (!sources.Methods.Contains(body.ClassName, body.Name))
            {
                stats.BodiesNotOriginal++;
                continue;
            }

            var shape = BodyAnalyzer.Classify(body);
            if (shape == BodyShape.Long)
            {
                stats.BodiesTooLong++;
                continue;
            }

            if (context.Layout(body.ClassName) is not { } layout)
            {
                stats.BodiesWithoutLayout++;
                continue;
            }

            var flat = layout.Flatten();
            var level = shape == BodyShape.Matching ? LinkLevel.DecompBodyMatching : LinkLevel.DecompBodyIndirect;
            var evidence = BodyAnalyzer.Format(body.Body);
            foreach (var name in references)
            {
                var field = flat.FirstOrDefault(f => f.Field.Name == name && f.Field.Member?.Kind == MemberKind.Data && f.Owner.Name == layout.Name)
                    ?? flat.FirstOrDefault(f => f.Field.Name == name && f.Field.Member?.Kind == MemberKind.Data);
                if (field is null)
                {
                    stats.BodyReferencesNotFound++;
                    continue;
                }

                if (level == LinkLevel.DecompBodyMatching)
                {
                    stats.BodyMatchingLinks++;
                }
                else
                {
                    stats.BodyIndirectLinks++;
                }

                var overloads = sources.Methods.Find(body.ClassName, body.Name);
                var method = overloads.FirstOrDefault(m => m.IsConst == body.IsConst) ?? overloads[0];
                context.Add(field.Owner.Name, field.Field, new Candidate(
                    level,
                    Provenance.DecompBody(body.File, body.Line, $"{body.ClassName}::{body.Name}"),
                    SuggestMemberName(body.Name),
                    method.Mangled is { } mangled ? $"{mangled} ({OriginText(method.Origin)})" : $"{body.ClassName}::{body.Name} ({OriginText(method.Origin)})",
                    $"{body.File}:{body.Line}: {{ {evidence} }}"));
            }
        }
    }

    /// <summary>
    /// For each unknown member declared by a class, the names its siblings (classes with the
    /// same first base) give at the same offset, to members they declare themselves, of the
    /// same size.
    /// </summary>
    private static void LinkSiblings(Context context)
    {
        var byBase = context.Layouts.Values
            .OfType<ClassLayout>()
            .Where(l => l.Bases.Count > 0)
            .GroupBy(l => l.Bases[0].Layout.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var unknown in context.AllDeclaredUnknowns().ToList())
        {
            var layout = unknown.Layout;
            if (layout.Bases.Count == 0 || !byBase.TryGetValue(layout.Bases[0].Layout.Name, out var siblings))
            {
                continue;
            }

            var names = new SortedDictionary<string, List<(string Class, FieldLayout Field)>>(StringComparer.Ordinal);
            foreach (var sibling in siblings)
            {
                if (sibling == layout)
                {
                    continue;
                }

                foreach (var field in sibling.Fields)
                {
                    if (field.Member?.Kind == MemberKind.Data && field.Offset == unknown.Field.Offset && field.Size == unknown.Field.Size
                        && !IsUnknownName(field.Name))
                    {
                        if (!names.TryGetValue(field.Name, out var classes))
                        {
                            names[field.Name] = classes = [];
                        }

                        classes.Add((sibling.Name, field));
                    }
                }
            }

            foreach (var (name, siblingFields) in names)
            {
                var classes = siblingFields.Select(s => s.Class).ToList();
                var shown = classes.Count <= 3 ? string.Join(", ", classes) : $"{string.Join(", ", classes.Take(3))} and {classes.Count - 3} more";
                var first = siblingFields[0];
                context.Add(layout.Name, unknown.Field, new Candidate(
                    LinkLevel.SiblingOffset,
                    Provenance.Sibling(first.Field.Identity.Source, $"{first.Class}::{name}"),
                    name,
                    $"{name} in {classes.Count} sibling class{(classes.Count == 1 ? "" : "es")}",
                    $"same base {layout.Bases[0].Layout.Name}, same offset and size: {shown}"));
            }
        }
    }

    /// <summary>Constructors and destructors set members up; they say nothing about one member's name.</summary>
    private static bool IsConstructorOrDestructor(string className, string name)
    {
        var lastScope = className.LastIndexOf("::", StringComparison.Ordinal);
        var shortName = lastScope < 0 ? className : className[(lastScope + 2)..];
        var templateStart = shortName.IndexOf('<');
        if (templateStart >= 0)
        {
            shortName = shortName[..templateStart];
        }

        return name == shortName || name.StartsWith('~');
    }

    internal static string OriginText(NameOrigin origin)
    {
        var parts = new List<string>();
        if (origin.HasFlag(NameOrigin.Symbols))
        {
            parts.Add("symbols.txt");
        }

        if (origin.HasFlag(NameOrigin.Map))
        {
            parts.Add("linker map");
        }

        if (origin.HasFlag(NameOrigin.UnusedComment))
        {
            parts.Add("UNUSED comment");
        }

        return string.Join(", ", parts);
    }

    [GeneratedRegex("^(?:unk[0-9A-Fa-f]+|field_0x[0-9A-Fa-f]+)$")]
    private static partial Regex UnknownName();

    private sealed class PendingMember(ClassLayout layout, FieldLayout field, string file)
    {
        public ClassLayout Layout { get; } = layout;

        public FieldLayout Field { get; } = field;

        public List<Candidate> Candidates { get; } = [];

        public UnknownMember ToRecord()
        {
            var ranked = Candidates
                .Distinct()
                .OrderBy(c => c.Level)
                .ThenBy(c => c.Note is not null)
                .ThenBy(c => c.Source, StringComparer.Ordinal)
                .ToList();
            return new UnknownMember(Layout.Name, Field.Name, Field.TypeName, Field.Offset ?? 0, Field.Size, file, ranked);
        }
    }

    private sealed class Context(LayoutEngine engine, VersionMask version, ExtractionStats stats)
    {
        public Dictionary<string, ClassLayout?> Layouts { get; } = new(StringComparer.Ordinal);

        public Dictionary<(string Class, string Member), PendingMember> Unknown { get; } = [];

        public ClassLayout? Layout(string qualifiedName)
        {
            if (!Layouts.TryGetValue(qualifiedName, out var layout))
            {
                layout = engine.Catalog.FindClass(qualifiedName).Count > 0 ? engine.GetLayout(qualifiedName, version) : null;
                Layouts[qualifiedName] = layout;
                if (layout is not null)
                {
                    foreach (var field in layout.Fields)
                    {
                        if (field.Member?.Kind != MemberKind.Data || !IsUnknownName(field.Name))
                        {
                            continue;
                        }

                        if (field.Offset is null)
                        {
                            stats.UnknownWithoutOffset++;
                        }
                        else
                        {
                            Unknown.TryAdd((layout.Name, field.Name), new PendingMember(layout, field, layout.Decl.File));
                        }
                    }
                }
            }

            return layout;
        }

        public IEnumerable<PendingMember> AllDeclaredUnknowns() => Unknown.Values;

        public void Add(string className, FieldLayout field, Candidate candidate)
        {
            if (!Unknown.TryGetValue((className, field.Name), out var pending))
            {
                return;
            }

            pending.Candidates.Add(candidate);
        }
    }
}
