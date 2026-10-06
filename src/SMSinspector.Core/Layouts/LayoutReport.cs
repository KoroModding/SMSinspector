using System.Globalization;
using System.Text;

namespace SMSinspector.Core.Layouts;

/// <summary>
/// The validation report over every class of the decomp: how many JP offset comments
/// the computation reproduces, where it disagrees, which classes PAL changes and how,
/// and what could not be read. Useful to the decomp project on its own.
/// </summary>
public sealed class LayoutReport
{
    public required int HeaderCount { get; init; }

    public required int ClassCount { get; init; }

    public required int SizedClassCount { get; init; }

    public required int JpCommentsChecked { get; init; }

    public required int JpCommentsMatched { get; init; }

    /// <summary>JP comments that could not be checked because a preceding size is unknown.</summary>
    public required int JpCommentsUnchecked { get; init; }

    public required IReadOnlyList<ClassLayout> JpLayoutsWithIssues { get; init; }

    public required IReadOnlyList<(ClassLayout Jp, ClassLayout Pal)> PalAffected { get; init; }

    public required IReadOnlyList<(string File, string Problem)> ParseProblems { get; init; }

    /// <summary>PAL layouts contradicted by evidence from outside the headers, such as the game's code.</summary>
    public IReadOnlyList<PalContradiction> PalContradictions { get; init; } = [];

    /// <summary>PAL layouts the game's code makes suspect, with the evidence; offsets are kept.</summary>
    public IReadOnlyList<PalSuspect> PalSuspects { get; init; } = [];

    /// <summary>Offset comments that contradict the computation, with what main.dol says about each.</summary>
    public IReadOnlyList<CommentCheck> CommentChecks { get; init; } = [];

    /// <summary>Classes holding a member of an unverified type, tested on that member's size.</summary>
    public IReadOnlyList<CascadeCheck> CascadeChecks { get; init; } = [];

    public double JpMatchRate => JpCommentsChecked == 0 ? 1 : (double)JpCommentsMatched / JpCommentsChecked;

    public static LayoutReport Build(TypeCatalog catalog, LayoutEngine engine)
    {
        var classes = catalog.AllClasses.Where(c => !c.IsTemplate && c.SpecializationArgs is null).ToList();
        int laidOut = 0, sized = 0, checkedCount = 0, matched = 0, unchecked_ = 0;
        var withIssues = new List<ClassLayout>();
        var affected = new List<(ClassLayout, ClassLayout)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var decl in classes)
        {
            if (!seen.Add(decl.QualifiedName + "|" + decl.File))
            {
                continue;
            }

            var jp = engine.GetLayout(decl, VersionMask.Jp);
            if (jp is not null)
            {
                laidOut++;
                sized += jp.Size is null ? 0 : 1;
                checkedCount += jp.CommentsChecked;
                matched += jp.CommentsMatched;
                unchecked_ += jp.Fields.Count(f => f.Member is not null && f.CommentOffset is not null
                    && f.Member.Versions != VersionMask.Pal && f.Source == OffsetSource.Comment && !f.CommentVerified
                    && !jp.Issues.Any(i => i.Member == f.Name && i.Kind is IssueKind.Gap or IssueKind.Overlap));
                if (jp.Issues.Count > 0)
                {
                    withIssues.Add(jp);
                }
            }

            var pal = engine.GetLayout(decl, VersionMask.Pal);
            if (pal is not null && pal.IsVersionAffected)
            {
                affected.Add((jp ?? pal, pal));
                if (jp is null)
                {
                    laidOut++;
                }
            }
        }

        return new LayoutReport
        {
            HeaderCount = catalog.Headers.Count,
            ClassCount = laidOut,
            SizedClassCount = sized,
            JpCommentsChecked = checkedCount,
            JpCommentsMatched = matched,
            JpCommentsUnchecked = unchecked_,
            JpLayoutsWithIssues = withIssues.OrderBy(l => l.Decl.File, StringComparer.Ordinal).ThenBy(l => l.Decl.Line).ToList(),
            PalAffected = affected.OrderBy(a => a.Item2.Decl.File, StringComparer.Ordinal).ThenBy(a => a.Item2.Decl.Line).ToList(),
            ParseProblems = catalog.Headers.SelectMany(h => h.Problems.Select(p => (h.Path, p))).ToList(),
            PalContradictions = engine.PalContradictions.OrderBy(c => c.ClassName, StringComparer.Ordinal).ToList(),
            PalSuspects = engine.PalSuspects.OrderBy(c => c.ClassName, StringComparer.Ordinal).ToList(),
            CommentChecks = engine.CommentChecks.OrderBy(c => c.ClassName, StringComparer.Ordinal).ToList(),
            CascadeChecks = engine.CascadeChecks.OrderBy(c => c.ClassName, StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>A few lines for the user interface.</summary>
    public string Summary()
    {
        var unverified = PalAffected.Count(a => a.Pal.PalUnverifiedAfter is not null);
        var lines = new List<string>
        {
            $"{ClassCount:N0} classes from {HeaderCount:N0} headers, {SizedClassCount:N0} with a known size.",
            $"JP offset comments reproduced: {JpCommentsMatched:N0} of {JpCommentsChecked:N0} checked ({JpMatchRate.ToString("P1", CultureInfo.InvariantCulture)}).",
            $"Classes with remarks: {JpLayoutsWithIssues.Count:N0}.",
            $"Classes whose PAL layout differs: {PalAffected.Count:N0}" + (unverified > 0 ? $", {unverified} with unverified PAL offsets." : "."),
            $"Unreadable statements: {ParseProblems.Count:N0}.",
        };
        if (PalContradictions.Count > 0)
        {
            lines.Add($"PAL layouts contradicted by main.dol: {string.Join(", ", PalContradictions.Select(c => c.ClassName))}.");
        }

        if (PalSuspects.Count > 0)
        {
            lines.Add($"PAL suspects (PAL-only code that does not fit the layout): {string.Join(", ", PalSuspects.Select(c => c.ClassName))}.");
        }

        if (CommentChecks.Count > 0)
        {
            int Count(CommentVerdict verdict) => CommentChecks.Count(c => c.Verdict == verdict);
            lines.Add($"Offset comments contradicting the computation: {CommentChecks.Count} classes; main.dol confirms the computation for "
                + $"{Count(CommentVerdict.ComputationConfirmed)} ({Count(CommentVerdict.ComputationProbable)} more probable), the comments for "
                + $"{Count(CommentVerdict.CommentConfirmed)} ({Count(CommentVerdict.CommentProbable)} more probable), "
                + $"{Count(CommentVerdict.Mixed)} with evidence both ways, {Count(CommentVerdict.Unverified)} unverified.");
        }

        if (CascadeChecks.Count > 0)
        {
            lines.Add($"Classes holding a member of an unverified type: {CascadeChecks.Count}; member size settled for {CascadeChecks.Count(c => c.IsSettled)}.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Where the evidence falls, member by member, when it goes both ways.</summary>
    private static void AppendTally(StringBuilder text, HypothesisCheck check)
    {
        if (check.Verdict != CommentVerdict.Mixed)
        {
            return;
        }

        text.AppendLine($"    {"member",-28} {"comments",-9} {"computed",-9} for computation    for comments");
        foreach (var member in check.Members)
        {
            static string Hex(uint? value) => value is { } v ? $"0x{v:X}" : "?";
            text.AppendLine($"    {member.Member,-28} {Hex(member.CommentOffset),-9} {Hex(member.ComputedOffset),-9} "
                + $"{$"{member.ForComputed} ({member.FunctionsForComputed} fn)",-18} {member.ForComment} ({member.FunctionsForComment} fn)");
        }
    }

    public string ToText(string? decompCommit = null)
    {
        var text = new StringBuilder();
        text.AppendLine("SMSinspector layout report");
        if (decompCommit is not null)
        {
            text.AppendLine($"Decomp commit: {decompCommit}");
        }

        text.AppendLine();
        text.AppendLine(Summary());

        if (PalContradictions.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== PAL layouts contradicted by main.dol");
            text.AppendLine("Game code reads or writes a member at another offset than the layout gives; PAL offsets are withheld from there on.");
            foreach (var contradiction in PalContradictions)
            {
                text.AppendLine($"  {contradiction.ClassName} from 0x{contradiction.FirstOffset:X}: {contradiction.Reason}");
            }
        }

        if (PalSuspects.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== PAL suspects");
            text.AppendLine("PAL-only methods (in the PAL symbols, not the JP ones) access their object with widths the layout does not fit. A hint: offsets are kept.");
            foreach (var suspect in PalSuspects)
            {
                text.AppendLine($"  {suspect.ClassName} at 0x{suspect.FirstOffset:X}..0x{suspect.LastOffset:X}: {suspect.Reason}");
            }
        }

        if (CommentChecks.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== Offset comments against the computation");
            text.AppendLine("An offset comment disagrees with the sizes of the members before it. Each load or store through this in the class's methods counts when it fits one layout and not the other. Confirmed computations are used; unverified classes have their PAL offsets withheld from the conflict on.");
            foreach (var check in CommentChecks)
            {
                text.AppendLine($"  {check.Summary()}");
                AppendTally(text, check);
            }
        }

        if (CascadeChecks.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== Members of unverified types");
            text.AppendLine("A class holds a member whose type's own offsets are unverified. The class is tested on that member's size, by the type's comments and by the computation, with accesses through the class only. When it settles, the member takes that size; its type stays unverified.");
            foreach (var check in CascadeChecks)
            {
                text.AppendLine($"  {check.Summary()}");
                AppendTally(text, check);
            }
        }

        text.AppendLine();
        text.AppendLine("== PAL differences");
        text.AppendLine("Offsets in PAL-only blocks are PAL offsets and are checked; other members move with the version blocks before them.");
        foreach (var (jp, pal) in PalAffected)
        {
            text.AppendLine();
            text.AppendLine($"{pal.Name} ({pal.Decl.File}:{pal.Decl.Line})");
            var jpSize = ReferenceEquals(jp, pal) ? "absent in JP" : Hex(jp.Size);
            text.AppendLine($"  size JP {jpSize}, PAL {Hex(pal.Size)}; PAL comments reproduced {pal.CommentsMatched}/{pal.CommentsChecked}");
            if (pal.PalUnverifiedAfter is { } after)
            {
                text.AppendLine($"  PAL offsets unverified after 0x{after:X}");
            }

            var jpOffsets = jp.Fields.Where(f => f.Member is not null)
                .ToDictionary(f => (object)f.Member!, f => f.Offset, ReferenceEqualityComparer.Instance);
            foreach (var field in pal.Fields)
            {
                var jpOffset = field.Member is not null && !ReferenceEquals(jp, pal) && jpOffsets.TryGetValue(field.Member, out var o) ? o : null;
                if (field.Member?.Versions == VersionMask.Pal)
                {
                    text.AppendLine($"  {field.Name,-28} PAL only at {Hex(field.Offset)}");
                }
                else if (jpOffset != field.Offset)
                {
                    text.AppendLine($"  {field.Name,-28} JP {Hex(jpOffset)} -> PAL {Hex(field.Offset)}");
                }
            }
        }

        text.AppendLine();
        text.AppendLine("== JP remarks");
        text.AppendLine("Gap: the comment is past the computed offset (bytes the header does not account for).");
        text.AppendLine("Overlap: the comment is before the computed offset (a member would overlap the previous one).");
        text.AppendLine("Either can be a wrong comment, a wrong type, or a size this tool gets wrong.");
        foreach (var layout in JpLayoutsWithIssues)
        {
            text.AppendLine();
            text.AppendLine($"{layout.Name} ({layout.Decl.File}:{layout.Decl.Line}), {layout.CommentsMatched}/{layout.CommentsChecked} comments reproduced");
            foreach (var issue in layout.Issues)
            {
                text.AppendLine($"  {issue.Kind,-14} {issue.Member}: {issue.Message}");
            }
        }

        if (ParseProblems.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== Unreadable statements");
            foreach (var (file, problem) in ParseProblems)
            {
                text.AppendLine($"  {file}: {problem}");
            }
        }

        return text.ToString();
    }

    private static string Hex(uint? value) => value is { } v ? $"0x{v:X}" : "?";
}
