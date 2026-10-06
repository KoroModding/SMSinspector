using SMSinspector.Core.Layouts;
using SMSinspector.Core.Live;
using SMSinspector.Core.Symbols;

namespace SMSinspector.Core.Names;

/// <summary>
/// Settles offset comments that contradict the computation, with the game's code. When a
/// comment disagrees with the sizes of the members before it, there are two layouts: the
/// comments', and the computation's from that member on. Every load and store through
/// <c>this</c> in a method of the class (or of a class derived from it) is placed on both. It
/// counts as evidence only when it fits one and not the other: an <c>lfs</c> on a float in one
/// and on a pointer in the other, a 16-bit store on a 16-bit member in one and on a pointer in
/// the other. An access that fits both is kept as non-discriminating. A strict accessor whose
/// name gives the member (from the decomp's body, or getFoo for mFoo) counts when its offset is
/// where one layout puts that member and not the other.
/// </summary>
/// <remarks>
/// <c>this</c> is r3 on entry and its copies, as in <see cref="PalOnlyCodeCheck.Accesses"/>.
/// A static member function would break that assumption; symbols do not say which functions
/// are static, so every evidence names its method for the reader to check.
/// </remarks>
public static class CommentOffsetCheck
{
    // Arrays of scalars longer than this stay one row instead of one row per element.
    private const uint ExpandedArrayLimit = 64;

    private const int MaxRounds = 8;

    /// <summary>
    /// Checks every class with a comment conflict and hands the verdicts to the engine,
    /// replacing earlier ones. A derived class can inherit a conflict from its base (its first
    /// member's comment assumes the base's size), so confirmed computations are applied and the
    /// remaining classes looked at again, until nothing new is confirmed.
    /// </summary>
    public static IReadOnlyList<CommentCheck> Apply(LayoutEngine engine, NameSources sources)
    {
        var confirmed = new List<CommentCheck>();
        engine.SetCommentChecks([]);
        for (var round = 1; ; round++)
        {
            var checks = CheckConflicted(engine, sources);
            var newlyConfirmed = checks.Where(c => c.Verdict == CommentVerdict.ComputationConfirmed).ToList();
            if (newlyConfirmed.Count == 0 || round == MaxRounds)
            {
                var all = confirmed.Concat(checks).OrderBy(c => c.ClassName, StringComparer.Ordinal).ToList();
                engine.SetCommentChecks(all);
                return all;
            }

            confirmed.AddRange(newlyConfirmed);
            engine.SetCommentChecks(confirmed);
        }
    }

    private static List<CommentCheck> CheckConflicted(LayoutEngine engine, NameSources sources)
    {
        var conflicted = engine.Catalog.AllClasses
            .Where(d => !d.IsTemplate && d.SpecializationArgs is null)
            .Select(d => engine.GetLayout(d, VersionMask.Pal))
            .OfType<ClassLayout>()
            .Where(l => l.CommentConflicts.Count > 0 && l.CommentCheck is null)
            .DistinctBy(l => l.Name)
            .OrderBy(l => l.Name, StringComparer.Ordinal)
            .ToList();

        var results = new List<CommentCheck>();
        if (conflicted.Count == 0)
        {
            return results;
        }

        var methods = sources.Executable.IsUsable
            ? sources.Methods.All.Where(m => m.Address is not null && m.Size is not null && m.Mangled is not null).GroupBy(m => m.ClassName).ToList()
            : [];
        var bodies = sources.Sources.Bodies.ToLookup(b => (b.ClassName, b.Name));

        foreach (var layout in conflicted)
        {
            var conflict = layout.CommentConflicts[0];
            if (layout.IsVersionAffected)
            {
                results.Add(new CommentCheck(conflict, CommentVerdict.Unverified, [], "the PAL layout has version blocks, not checked"));
            }
            else if (!sources.Executable.IsUsable)
            {
                results.Add(new CommentCheck(conflict, CommentVerdict.Unverified, [], "main.dol not available"));
            }
            else
            {
                results.Add(Check(engine, sources, layout, conflict, methods, bodies));
            }
        }

        return results;
    }

    private static CommentCheck Check(
        LayoutEngine engine,
        NameSources sources,
        ClassLayout layout,
        CommentConflict conflict,
        List<IGrouping<string, OriginalMethod>> methods,
        ILookup<(string ClassName, string Name), FunctionBody> bodies)
    {
        var computed = engine.GetComputedLayout(layout)!;
        var commentPlaces = new Places(engine, layout, conflict);
        var computedPlaces = new Places(engine, computed, null);

        // Past the smaller of the two ends, a derived class's own members could be mistaken for ours.
        var end = Math.Min(commentPlaces.End, computedPlaces.End);
        var image = sources.Executable.Image!;
        uint? Read(uint a) => image.TryReadWord(a, out var word) ? word : null;

        var evidence = new Dictionary<uint, OffsetEvidence>();
        foreach (var group in methods)
        {
            if (Origin(engine.GetLayout(group.Key, VersionMask.Pal), layout.Name) is not { } origin)
            {
                continue;
            }

            foreach (var method in group)
            {
                var address = method.Address!.Value;
                var name = CodeWarriorDemangler.Demangle(method.Mangled!);

                if (Accessor(method, bodies, Read) is { } accessor && accessor.Code.Offset >= origin)
                {
                    var offset = accessor.Code.Offset - origin;
                    var underComment = Find(layout, accessor.Member);
                    var underComputed = Find(computed, accessor.Member);
                    if (underComment != underComputed && (offset == underComment || offset == underComputed))
                    {
                        var supports = offset == underComputed ? EvidenceSupport.Computed : EvidenceSupport.Comment;
                        evidence[address] = new OffsetEvidence(supports, true, name, address, accessor.Code.Instruction, offset,
                            $"{accessor.Member} at 0x{underComputed:X}", $"{accessor.Member} at 0x{underComment:X}");
                    }
                }

                foreach (var access in PalOnlyCodeCheck.Accesses(address, method.Size!.Value, Read))
                {
                    if (access.Offset < origin || evidence.ContainsKey(access.Address))
                    {
                        continue;
                    }

                    var offset = access.Offset - origin;
                    if (offset < conflict.FirstOffset || offset >= end)
                    {
                        continue;
                    }

                    var (fitComputed, whatComputed) = computedPlaces.Fit(offset, access);
                    var (fitComment, whatComment) = commentPlaces.Fit(offset, access);
                    EvidenceSupport? supports = (fitComputed, fitComment) switch
                    {
                        (Fit.Exact, Fit.Misfit) => EvidenceSupport.Computed,
                        (Fit.Misfit, Fit.Exact) => EvidenceSupport.Comment,
                        (Fit.Exact, Fit.Exact) => EvidenceSupport.Both,
                        _ => null,
                    };
                    if (supports is { } side)
                    {
                        evidence[access.Address] = new OffsetEvidence(side, false, name, access.Address, access.Instruction, offset, whatComputed, whatComment);
                    }
                }
            }
        }

        var list = evidence.Values.OrderBy(e => e.Address).ToList();
        var forComputed = list.Count(e => e.Supports == EvidenceSupport.Computed);
        var forComment = list.Count(e => e.Supports == EvidenceSupport.Comment);
        var (verdict, remark) = (forComputed, forComment) switch
        {
            ( > 0, 0) => (CommentVerdict.ComputationConfirmed, (string?)null),
            (0, > 0) => (CommentVerdict.CommentConfirmed, null),
            (0, 0) => (CommentVerdict.Unverified, "no discriminating access found"),
            _ => (CommentVerdict.Unverified, "evidence both ways"),
        };
        return new CommentCheck(conflict, verdict, list, remark);
    }

    /// <summary>Where <paramref name="baseName"/> starts inside <paramref name="layout"/>, or null when it is not a base.</summary>
    private static uint? Origin(ClassLayout? layout, string baseName)
    {
        if (layout is null)
        {
            return null;
        }

        if (layout.Name == baseName)
        {
            return 0;
        }

        foreach (var baseLayout in layout.Bases)
        {
            if (baseLayout.Offset is { } offset && Origin(baseLayout.Layout, baseName) is { } inner)
            {
                return offset + inner;
            }
        }

        return null;
    }

    private static uint? Find(ClassLayout layout, string member) =>
        layout.Flatten().FirstOrDefault(f => f.Field.Name == member && f.Field.Member?.Kind == MemberKind.Data && ReferenceEquals(f.Owner, layout))?.AbsoluteOffset
        ?? layout.Flatten().FirstOrDefault(f => f.Field.Name == member && f.Field.Member?.Kind == MemberKind.Data)?.AbsoluteOffset;

    /// <summary>A strict accessor and the member its name gives: the decomp body's, or mFoo for getFoo.</summary>
    private static (AccessorCode Code, string Member)? Accessor(OriginalMethod method, ILookup<(string, string), FunctionBody> bodies, Func<uint, uint?> read)
    {
        var code = AccessorDecoder.Decode(method.Address!.Value, method.Size, read);
        if (!code.IsAccessor)
        {
            return null;
        }

        var candidates = bodies[(method.ClassName, method.Name)].ToList();
        var body = candidates.FirstOrDefault(b => b.IsConst == method.IsConst) ?? (candidates.Count == 1 ? candidates[0] : null);
        var member = (body is null ? null : BodyAnalyzer.SingleMember(body)) ?? NameExtractor.SuggestMemberName(method.Name);
        return member is null ? null : (code, member);
    }

    private enum Fit
    {
        Exact,
        Misfit,
        Unknown,
    }

    /// <summary>The leaf rows of one layout, indexed by the bytes they cover.</summary>
    private sealed class Places
    {
        private readonly List<FieldRow>?[] _byByte;

        /// <param name="conflict">
        /// For the comments' layout: the members before the conflict end where the comment says
        /// the next one starts, which is what the comment claims.
        /// </param>
        public Places(LayoutEngine engine, ClassLayout layout, CommentConflict? conflict)
        {
            var fields = ObjectFields.Build(engine, layout);
            End = fields.ReadSize;
            _byByte = new List<FieldRow>?[End];
            foreach (var row in fields.Rows)
            {
                var limit = conflict is not null && row.Offset < conflict.CommentOffset && row.Kind != RowKind.Gap ? conflict.CommentOffset : End;
                Collect([row], limit);
            }
        }

        public uint End { get; }

        public (Fit Fit, string What) Fit(uint offset, ThisAccess access)
        {
            if (offset >= End || _byByte[offset] is not { } covering)
            {
                return (Names.CommentOffsetCheck.Fit.Misfit, "nothing");
            }

            foreach (var row in covering)
            {
                if (Matches(row, offset, access))
                {
                    return (Names.CommentOffsetCheck.Fit.Exact, Describe(row, offset));
                }
            }

            return covering.Any(r => r.Type is DataType.Opaque or DataType.Composite)
                ? (Names.CommentOffsetCheck.Fit.Unknown, Describe(covering[0], offset))
                : (Names.CommentOffsetCheck.Fit.Misfit, Describe(covering[0], offset));
        }

        private static bool Matches(FieldRow row, uint offset, ThisAccess access)
        {
            if (row.Kind == RowKind.Gap)
            {
                return false;
            }

            var (type, start) = row.Type is DataType.ArrayOf array
                ? (array.Element, row.Offset!.Value + (offset - row.Offset!.Value) / Math.Max(array.Element.Size, 1) * array.Element.Size)
                : (row.Type, row.Offset!.Value);
            var size = row.Type is DataType.ArrayOf ? type.Size : row.Size;
            return start == offset && size == access.Width && type switch
            {
                // Integer moves copy floats too (structure copies), so only float moves tell.
                DataType.Scalar { Kind: ScalarKind.Float } => true,
                DataType.Scalar or DataType.Pointer or DataType.Enumeration => !access.IsFloat,
                _ => false,
            };
        }

        private static string Describe(FieldRow row, uint offset)
        {
            var at = offset == row.Offset ? "" : $" +0x{offset - row.Offset:X}";
            return row.Kind == RowKind.Gap ? $"padding at 0x{row.Offset:X}{at}" : $"{row.Path} ({row.TypeName}){at}";
        }

        private void Collect(IReadOnlyList<FieldRow> rows, uint limit)
        {
            foreach (var row in rows)
            {
                if (row.Offset is not { } start)
                {
                    continue;
                }

                var isLongScalarArray = row.Type is DataType.ArrayOf { Element: DataType.Scalar or DataType.Pointer or DataType.Enumeration, Count: > ExpandedArrayLimit };
                if (row.HasChildren && !isLongScalarArray)
                {
                    Collect(row.Children, limit);
                    continue;
                }

                for (var at = start; at < start + row.Size && at < Math.Min(End, limit); at++)
                {
                    (_byByte[at] ??= []).Add(row);
                }
            }
        }
    }
}
