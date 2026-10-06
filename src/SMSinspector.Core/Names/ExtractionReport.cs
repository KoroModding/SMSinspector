using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SMSinspector.Core.Names;

/// <summary>What the extractor read and set aside, so that nothing is dropped silently.</summary>
public sealed class ExtractionStats
{
    public Dictionary<AccessorShape, int> Shapes { get; } = [];

    public int AccessorLinked { get; set; }

    public int AccessorOnNamedMember { get; set; }

    public int AccessorInsideMember { get; set; }

    public int AccessorNoMember { get; set; }

    public int AccessorWithoutLayout { get; set; }

    public int BodyMatchingLinks { get; set; }

    public int BodyIndirectLinks { get; set; }

    public int BodiesNotOriginal { get; set; }

    public int BodiesTooLong { get; set; }

    public int BodiesWithoutLayout { get; set; }

    public int BodyReferencesNotFound { get; set; }

    public void CountShape(AccessorShape shape) => Shapes[shape] = Shapes.GetValueOrDefault(shape) + 1;
}

public sealed record ExtractionReport(NameSources Sources, ExtractionStats Stats, IReadOnlyList<UnknownMember> Members)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public int WithCandidates => Members.Count(m => m.Candidates.Count > 0);

    /// <summary>Members with a candidate from code or a decomp body, not only a sibling hint.</summary>
    public int WithMethodCandidates => Members.Count(m => m.Candidates.Count > 0 && m.Candidates[0].Level != LinkLevel.SiblingOffset);

    public int WithLevel(LinkLevel level) => Members.Count(m => m.Candidates.Count > 0 && m.Candidates[0].Level == level);

    public static string LevelText(LinkLevel level) => level switch
    {
        LinkLevel.DolAccessor => "dol accessor",
        LinkLevel.DecompBodyMatching => "decomp body, matching",
        LinkLevel.DecompBodyIndirect => "decomp body, indirect",
        LinkLevel.SiblingOffset => "sibling offset",
        _ => level.ToString(),
    };

    /// <summary>A few lines for the user interface.</summary>
    public string Summary()
    {
        var lines = new List<string>
        {
            $"Unknown members: {Members.Count:N0}, {WithCandidates:N0} with at least one candidate, {WithMethodCandidates:N0} beyond sibling hints.",
        };
        foreach (var level in Enum.GetValues<LinkLevel>())
        {
            lines.Add($"  best candidate {LevelText(level),-22} {WithLevel(level),6:N0}");
        }

        lines.Add($"Original methods: {Sources.Methods.Count:N0}.");
        lines.Add($"main.dol: {Sources.Executable.Message}");
        lines.Add($"Linker map: {Sources.MapMessage}");
        return string.Join(Environment.NewLine, lines);
    }

    public string ToText(string? decompCommit = null)
    {
        var text = new StringBuilder();
        text.AppendLine("SMSinspector name extractor report");
        text.AppendLine($"Decomp commit: {decompCommit ?? "unknown"}");
        text.AppendLine();
        text.AppendLine("Candidates are original names placed next to members the decomp still calls unkXX.");
        text.AppendLine("None of them is a certainty: check each one in game before naming the member.");
        text.AppendLine();

        text.AppendLine("== Sources ==");
        text.AppendLine($"symbols.txt and map: {Sources.Methods.Count:N0} original methods ({Sources.Methods.All.Count(m => m.IsUnused):N0} UNUSED).");
        text.AppendLine($"Linker map: {Sources.MapMessage}");
        text.AppendLine($"main.dol: {Sources.Executable.Message}");
        text.AppendLine($"Decomp sources: {Sources.SourceFileCount:N0} files, {Sources.Sources.Bodies.Count:N0} function bodies, {Sources.Sources.UnusedDeclarations.Count:N0} UNUSED declarations.");
        text.AppendLine($"PARAM_INIT: {Sources.ParamInitCount:N0} members named by the macro, {Sources.ParamInitUnknownCount:N0} of them still unkXX.");
        text.AppendLine();

        text.AppendLine("== Summary ==");
        text.AppendLine(Summary().Split(Environment.NewLine)[0]);
        foreach (var level in Enum.GetValues<LinkLevel>())
        {
            text.AppendLine($"  best candidate {LevelText(level),-22} {WithLevel(level),6:N0}");
        }

        text.AppendLine();
        text.AppendLine("== Not interpreted ==");
        if (Sources.Executable.IsUsable)
        {
            text.AppendLine("Original methods in main.dol, by code shape:");
            foreach (var (shape, count) in Stats.Shapes.OrderBy(s => s.Key))
            {
                text.AppendLine($"  {ShapeText(shape),-46} {count,6:N0}");
            }

            text.AppendLine($"Accessors linked to an unknown member: {Stats.AccessorLinked:N0}");
            text.AppendLine($"Accessors on a member that already has a name: {Stats.AccessorOnNamedMember:N0}");
            text.AppendLine($"Accessors inside a member (array element, struct field): {Stats.AccessorInsideMember:N0}");
            text.AppendLine($"Accessors on an offset with no member: {Stats.AccessorNoMember:N0}");
            text.AppendLine($"Accessors of a class without a layout: {Stats.AccessorWithoutLayout:N0}");
        }
        else
        {
            text.AppendLine("main.dol was not used, so no code was read.");
        }

        text.AppendLine($"Decomp bodies using unkXX under a name that is not original: {Stats.BodiesNotOriginal:N0}");
        text.AppendLine($"Decomp bodies with an original name but longer than {BodyAnalyzer.MaxShortStatements} statements: {Stats.BodiesTooLong:N0}");
        text.AppendLine($"Decomp bodies of a class without a layout: {Stats.BodiesWithoutLayout:N0}");
        text.AppendLine($"unkXX names in bodies not found in the class layout: {Stats.BodyReferencesNotFound:N0}");
        text.AppendLine();

        text.AppendLine("== Members with candidates ==");
        foreach (var member in Members.Where(m => m.Candidates.Count > 0)
                     .OrderBy(m => m.Candidates[0].Level)
                     .ThenBy(m => m.ClassName, StringComparer.Ordinal)
                     .ThenBy(m => m.Offset))
        {
            var size = member.Size is { } s ? $", 0x{s:X} bytes" : "";
            text.AppendLine($"{member.ClassName}::{member.Name} (+0x{member.Offset:X}, {member.TypeName}{size}) [{member.File}]");
            foreach (var candidate in member.Candidates)
            {
                var suggestion = candidate.Suggestion is { } name ? $"{name}  " : "";
                text.AppendLine($"  [{LevelText(candidate.Level)}] {suggestion}from {candidate.Source}");
                text.AppendLine($"      {candidate.Evidence}");
                if (candidate.Note is { } note)
                {
                    text.AppendLine($"      note: {note}");
                }
            }
        }

        return text.ToString();
    }

    public string ToJson(string? decompCommit = null)
    {
        var document = new
        {
            decompCommit,
            sources = new
            {
                originalMethods = Sources.Methods.Count,
                linkerMap = Sources.MapMessage,
                linkerMapUsed = Sources.MapUsed,
                executable = Sources.Executable.Message,
                executableStatus = Sources.Executable.Status,
                executableUsed = Sources.Executable.IsUsable,
                sourceFiles = Sources.SourceFileCount,
                functionBodies = Sources.Sources.Bodies.Count,
                paramInit = Sources.ParamInitCount,
                paramInitUnknown = Sources.ParamInitUnknownCount,
            },
            summary = new
            {
                unknownMembers = Members.Count,
                withCandidates = WithCandidates,
                withMethodCandidates = WithMethodCandidates,
                bestLevel = Enum.GetValues<LinkLevel>().ToDictionary(LevelText, WithLevel),
            },
            notInterpreted = new
            {
                codeShapes = Stats.Shapes.ToDictionary(s => ShapeText(s.Key), s => s.Value),
                Stats.AccessorOnNamedMember,
                Stats.AccessorInsideMember,
                Stats.AccessorNoMember,
                Stats.AccessorWithoutLayout,
                Stats.BodiesNotOriginal,
                Stats.BodiesTooLong,
                Stats.BodiesWithoutLayout,
                Stats.BodyReferencesNotFound,
            },
            members = Members.Where(m => m.Candidates.Count > 0).Select(m => new
            {
                className = m.ClassName,
                member = m.Name,
                offset = $"0x{m.Offset:X}",
                type = m.TypeName,
                size = m.Size,
                file = m.File,
                candidates = m.Candidates.Select(c => new
                {
                    level = LevelText(c.Level),
                    suggestion = c.Suggestion,
                    source = c.Source,
                    evidence = c.Evidence,
                    note = c.Note,
                }),
            }),
        };

        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public static string ShapeText(AccessorShape shape) => shape switch
    {
        AccessorShape.Load => "load from d(r3), blr (accessor)",
        AccessorShape.Store => "store of the first argument to d(r3), blr (accessor)",
        AccessorShape.AddressOf => "addi r3, r3, d, blr (address of a member)",
        AccessorShape.OtherTwoInstructions => "other two-instruction body",
        AccessorShape.Empty => "blr only (empty)",
        AccessorShape.Longer => "longer than two instructions",
        AccessorShape.Unreadable => "size unknown or code outside the DOL",
        _ => shape.ToString().ToLower(CultureInfo.InvariantCulture),
    };
}
