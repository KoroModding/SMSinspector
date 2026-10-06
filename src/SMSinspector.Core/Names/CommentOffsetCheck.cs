using SMSinspector.Core.Layouts;
using SMSinspector.Core.Live;
using SMSinspector.Core.Symbols;

namespace SMSinspector.Core.Names;

/// <summary>
/// Switches for two rules of <see cref="CommentOffsetCheck"/>, to compare runs with and without them.
/// </summary>
/// <param name="StructureCopies">Structures are copied a word at a time: an aligned word inside one proves nothing.</param>
/// <param name="DependencyOrder">A class is settled only after its bases and member types.</param>
public sealed record CommentCheckOptions(bool StructureCopies = true, bool DependencyOrder = true)
{
    public static CommentCheckOptions Default { get; } = new();
}

/// <summary>The verdicts of one run of <see cref="CommentOffsetCheck"/>.</summary>
public sealed record CommentCheckResult(IReadOnlyList<CommentCheck> Comments, IReadOnlyList<CascadeCheck> Cascades);

/// <summary>
/// Settles offset comments that contradict the computation, with the game's code. When a
/// comment disagrees with the sizes of the members before it, there are two layouts: the
/// comments', and the computation's from that member on. Every load and store that reaches
/// the class through a pointer known to point at it is placed on both. It counts as evidence
/// only when it fits one and not the other: an <c>lfs</c> on a float in one and on a pointer
/// or a <c>u16</c> in the other. An <c>addi</c> taking a member's address counts when an array
/// or a class member starts there in one layout and not the other. Accesses that fit both are
/// kept as non-discriminating. A strict accessor counts when its name gives the member (from
/// the decomp's body, or getFoo for mFoo) and its offset is where one layout puts it only.
/// </summary>
/// <remarks>
/// <para>
/// The pointer is <c>this</c> (r3 on entry and its copies, as in <see cref="PalOnlyCodeCheck.Accesses"/>)
/// in a method of the class or of a class derived from it, or the first parameter of a free
/// function declared to take a pointer to the class. Symbols do not say which member functions
/// are static, so every evidence names its function for the reader to check.
/// </para>
/// <para>
/// A verdict needs evidence from two distinct functions and none against; one function makes
/// it probable, which is shown but does not move offsets. A class holding a member whose type
/// stays unverified is then tested on that member's size alone (a <see cref="CascadeCheck"/>).
/// </para>
/// </remarks>
public static class CommentOffsetCheck
{
    private const int MaxRounds = 32;

    // Arrays of scalars longer than this stay one row instead of one row per element.
    private const uint ExpandedArrayLimit = 64;

    /// <summary>Checks every class and hands the verdicts to the engine, replacing earlier ones.</summary>
    /// <param name="options">The default runs every rule; switches exist to compare runs without some of them.</param>
    public static CommentCheckResult Apply(LayoutEngine engine, NameSources sources, CommentCheckOptions? options = null)
    {
        options ??= CommentCheckOptions.Default;
        var context = new Context(engine, sources, options);
        var settledComments = new List<CommentCheck>();
        var settledCascades = new List<CascadeCheck>();
        List<CommentCheck> openComments = [];
        List<CascadeCheck> openCascades = [];

        for (var round = 1; round <= MaxRounds; round++)
        {
            // Settled verdicts shape the layouts; conflicts are looked for on top of them.
            engine.SetCascadeChecks(settledCascades);
            engine.SetCommentChecks(settledComments);
            var conflicted = context.Conflicted();

            // A class is settled only once its bases and member types are: until then its layout is not final.
            var waiting = options.DependencyOrder ? context.Waiting(conflicted) : [];
            var comments = context.CheckConflicts(conflicted);
            var newComments = comments.Where(c => c.IsSettled && !waiting.ContainsKey(c.ClassName)).ToList();
            openComments = comments.Except(newComments).Select(c => waiting.TryGetValue(c.ClassName, out var on) && c.IsSettled
                ? c with { Verdict = CommentVerdict.Unverified, Remark = $"the evidence leans one way, but {on} is not settled" }
                : c).ToList();
            settledComments.AddRange(newComments);
            if (newComments.Count > 0 && options.DependencyOrder)
            {
                continue;
            }

            // Open verdicts withhold sizes, which is what cascade checks look for.
            engine.SetCommentChecks([.. settledComments, .. openComments]);
            var cascades = context.CheckCascades(settledCascades.Select(c => c.ClassName).ToHashSet(StringComparer.Ordinal));
            var newCascades = cascades.Where(c => c.IsSettled).ToList();
            openCascades = cascades.Where(c => !c.IsSettled).ToList();
            settledCascades.AddRange(newCascades);
            if (newCascades.Count == 0 && newComments.Count == 0)
            {
                break;
            }
        }

        var allComments = settledComments.Concat(openComments).OrderBy(c => c.ClassName, StringComparer.Ordinal).ToList();
        var allCascades = settledCascades.Concat(openCascades).OrderBy(c => c.ClassName, StringComparer.Ordinal).ToList();
        engine.SetCommentChecks(allComments);
        engine.SetCascadeChecks(allCascades);
        return new CommentCheckResult(allComments, allCascades);
    }

    /// <summary>A function that reaches a class, and where that class starts in what it points at.</summary>
    private sealed record Reach(string Name, uint Address, uint Size, uint Origin, OriginalMethod? Method);

    private sealed class Context(LayoutEngine engine, NameSources sources, CommentCheckOptions options)
    {
        private readonly List<IGrouping<string, OriginalMethod>> _methods = sources.Executable.IsUsable
            ? sources.Methods.All.Where(m => m.Address is not null && m.Size is not null && m.Mangled is not null).GroupBy(m => m.ClassName).ToList()
            : [];

        private readonly ILookup<(string ClassName, string Name), FunctionBody> _bodies = sources.Sources.Bodies.ToLookup(b => (b.ClassName, b.Name));

        private readonly Dictionary<string, string?> _pointedClass = new(StringComparer.Ordinal);

        /// <summary>Classes with an offset comment conflict and no settled verdict yet.</summary>
        public List<ClassLayout> Conflicted() =>
            PalLayouts().Where(l => l.CommentConflicts.Count > 0 && l.CommentCheck is null && l.CascadeCheck?.IsSettled != true).ToList();

        /// <summary>For each conflicted class, a base or member type of it that is conflicted too.</summary>
        public Dictionary<string, string> Waiting(List<ClassLayout> conflicted)
        {
            var names = conflicted.Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
            var waiting = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var layout in conflicted)
            {
                if (Dependencies(layout).FirstOrDefault(d => names.Contains(d.Name)) is { Name: not null } dependency)
                {
                    waiting[layout.Name] = $"{dependency.Kind} {dependency.Name}";
                }
            }

            return waiting;
        }

        /// <summary>Bases and member types stored inline, recursively: what the layout of a class is built from.</summary>
        private IEnumerable<(string Kind, string Name)> Dependencies(ClassLayout layout)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { layout.Name };
            var pending = new Stack<ClassLayout>([layout]);
            while (pending.TryPop(out var current))
            {
                foreach (var baseLayout in current.Bases.Select(b => b.Layout))
                {
                    if (seen.Add(baseLayout.Name))
                    {
                        yield return ("base", baseLayout.Name);
                        pending.Push(baseLayout);
                    }
                }

                foreach (var field in current.Fields)
                {
                    if (field.Member is { Kind: MemberKind.Data } member && !member.Type.IsPointerLike
                        && engine.GetMemberTypeLayout(current, field, VersionMask.Pal) is { } type && seen.Add(type.Name))
                    {
                        yield return ("member type", type.Name);
                        pending.Push(type);
                    }
                }
            }
        }

        public List<CommentCheck> CheckConflicts(List<ClassLayout> conflicted)
        {
            var results = new List<CommentCheck>();
            foreach (var layout in conflicted)
            {
                var conflict = layout.CommentConflicts[0];
                if (layout.IsVersionAffected)
                {
                    results.Add(new CommentCheck(conflict, CommentVerdict.Unverified, [], [], "the PAL layout has version blocks, not checked"));
                }
                else if (!sources.Executable.IsUsable)
                {
                    results.Add(new CommentCheck(conflict, CommentVerdict.Unverified, [], [], "main.dol not available"));
                }
                else
                {
                    var computed = engine.GetComputedLayout(layout)!;
                    var evidence = Gather(layout.Name, layout, computed, conflict, conflict.FirstOffset);
                    var (verdict, remark) = HypothesisCheck.Decide(evidence);
                    results.Add(new CommentCheck(conflict, verdict, evidence, Tally(layout, computed, conflict.Member, evidence), remark));
                }
            }

            return results;
        }

        /// <summary>Classes holding, as a base or as their own member, a class whose verdict is open.</summary>
        public List<CascadeCheck> CheckCascades(HashSet<string> alreadySettled)
        {
            var results = new List<CascadeCheck>();
            if (!sources.Executable.IsUsable)
            {
                return results;
            }

            foreach (var layout in PalLayouts().Where(l => !alreadySettled.Contains(l.Name)))
            {
                var firstOwn = layout.Fields.FirstOrDefault(f => f.Member is { Kind: MemberKind.Data })?.Member;
                var openBase = layout.Bases.Select(b => engine.GetLayout(b.Layout.Name, VersionMask.Pal) ?? b.Layout)
                    .FirstOrDefault(b => b.CommentCheck is { IsSettled: false });
                if (openBase is not null && firstOwn is not null)
                {
                    if (Cascade(layout, openBase, firstOwn, isBase: true) is { } check)
                    {
                        results.Add(check);
                    }

                    continue;
                }

                foreach (var field in layout.Fields)
                {
                    if (field.Member is not { Kind: MemberKind.Data } member || member.Type.IsPointerLike || member.Type.Dims.Count > 0
                        || engine.GetMemberTypeLayout(layout, field, VersionMask.Pal) is not { CommentCheck: { IsSettled: false } } held
                        || held.Name == layout.Name)
                    {
                        continue;
                    }

                    if (Cascade(layout, held, member, isBase: false) is { } check)
                    {
                        results.Add(check);
                    }

                    break;
                }
            }

            return results;
        }

        /// <param name="from">The member tested, or for a base the class's first own member.</param>
        private CascadeCheck? Cascade(ClassLayout layout, ClassLayout held, MemberDecl from, bool isBase)
        {
            var byComments = engine.GetLayout(held.Decl, VersionMask.Jp);
            var byComputation = engine.GetComputedLayout(held);
            if ((isBase ? byComments?.NonVirtualSize : byComments?.Size) is not { } sizeC
                || (isBase ? byComputation?.NonVirtualSize : byComputation?.Size) is not { } sizeK)
            {
                return null;
            }

            if (sizeC == sizeK)
            {
                return new CascadeCheck(layout.Name, from, isBase, held.Name, sizeC, sizeK, CommentVerdict.Unverified, [], [], "same size by the comments and the computation");
            }

            var underComments = engine.ComputeWithMemberSize(layout, from, held.Name, sizeC);
            var underComputation = engine.ComputeWithMemberSize(layout, from, held.Name, sizeK);
            var start = isBase
                ? underComments.Bases.FirstOrDefault(b => b.Layout.Name == held.Name)?.Offset
                : underComments.Fields.First(f => ReferenceEquals(f.Member, from)).Offset;
            if (start is not { } partOffset)
            {
                return null;
            }

            // Accesses inside the part tell nothing about its size; only what comes after it does.
            var evidence = Gather(layout.Name, underComments, underComputation, null, partOffset + Math.Min(sizeC, sizeK));
            var (verdict, remark) = HypothesisCheck.Decide(evidence);
            return new CascadeCheck(layout.Name, from, isBase, held.Name, sizeC, sizeK, verdict, evidence, Tally(underComments, underComputation, from, evidence), remark);
        }

        private IEnumerable<ClassLayout> PalLayouts() => engine.Catalog.AllClasses
            .Where(d => !d.IsTemplate && d.SpecializationArgs is null)
            .Select(d => engine.GetLayout(d, VersionMask.Pal))
            .OfType<ClassLayout>()
            .DistinctBy(l => l.Name)
            .OrderBy(l => l.Name, StringComparer.Ordinal)
            .ToList();

        /// <param name="clipAt">For an own conflict: members before it end where the comment says the next starts.</param>
        private List<OffsetEvidence> Gather(string subject, ClassLayout underComments, ClassLayout underComputation, CommentConflict? clipAt, uint regionStart)
        {
            var commentPlaces = new Places(engine, underComments, clipAt, options.StructureCopies);
            var computedPlaces = new Places(engine, underComputation, null, options.StructureCopies);

            // Past the smaller end, a derived class's own members could be mistaken for ours.
            var end = Math.Min(commentPlaces.End, computedPlaces.End);
            var image = sources.Executable.Image!;
            uint? Read(uint a) => image.TryReadWord(a, out var word) ? word : null;

            var evidence = new Dictionary<uint, OffsetEvidence>();
            foreach (var reach in Reaching(subject))
            {
                if (reach.Method is { } method && Accessor(method, Read) is { } accessor && accessor.Code.Offset >= reach.Origin)
                {
                    var offset = accessor.Code.Offset - reach.Origin;
                    var atComments = Find(underComments, accessor.Member);
                    var atComputation = Find(underComputation, accessor.Member);
                    if (atComments != atComputation && (offset == atComments || offset == atComputation))
                    {
                        var supports = offset == atComputation ? EvidenceSupport.Computed : EvidenceSupport.Comment;
                        evidence[method.Address!.Value] = new OffsetEvidence(supports, true, reach.Name, reach.Address, method.Address!.Value,
                            accessor.Code.Instruction, offset, $"{accessor.Member} at 0x{atComputation:X}", $"{accessor.Member} at 0x{atComments:X}",
                            accessor.Member, accessor.Member);
                    }
                }

                foreach (var access in PalOnlyCodeCheck.Accesses(reach.Address, reach.Size, Read, withAddresses: true))
                {
                    if (access.Offset < reach.Origin || evidence.ContainsKey(access.Address))
                    {
                        continue;
                    }

                    var offset = access.Offset - reach.Origin;
                    if (offset < regionStart || offset >= end)
                    {
                        continue;
                    }

                    var computed = computedPlaces.Fit(offset, access);
                    var comment = commentPlaces.Fit(offset, access);
                    EvidenceSupport? supports = (computed.Fit, comment.Fit) switch
                    {
                        (Fit.Exact, Fit.Misfit) => EvidenceSupport.Computed,
                        (Fit.Misfit, Fit.Exact) => EvidenceSupport.Comment,
                        (Fit.Exact, Fit.Exact) => EvidenceSupport.Both,
                        _ => null,
                    };
                    if (supports is { } side)
                    {
                        evidence[access.Address] = new OffsetEvidence(side, false, reach.Name, reach.Address, access.Address, access.Instruction, offset,
                            computed.What, comment.What, computed.Member, comment.Member);
                    }
                }
            }

            return evidence.Values.OrderBy(e => e.Address).ToList();
        }

        /// <summary>Methods of the class and of classes derived from it, then free functions taking a pointer to it.</summary>
        private IEnumerable<Reach> Reaching(string subject)
        {
            foreach (var group in _methods)
            {
                if (Origin(engine.GetLayout(group.Key, VersionMask.Pal), subject) is not { } origin)
                {
                    continue;
                }

                foreach (var method in group)
                {
                    yield return new Reach(CodeWarriorDemangler.Demangle(method.Mangled!), method.Address!.Value, method.Size!.Value, origin, method);
                }
            }

            foreach (var function in sources.FreeFunctions ?? [])
            {
                if (PointedClass(function.FirstParameterType) == subject)
                {
                    yield return new Reach($"{function.Name} (first parameter {function.FirstParameterType}*, {function.Source})", function.Address, function.Size, 0, null);
                }
            }
        }

        private string? PointedClass(string typeName)
        {
            if (!_pointedClass.TryGetValue(typeName, out var name))
            {
                name = HeaderParser.TryParseType(typeName, out var type) ? engine.GetLayout(type, VersionMask.Pal)?.Name : null;
                _pointedClass[typeName] = name;
            }

            return name;
        }

        /// <summary>A strict accessor and the member its name gives: the decomp body's, or mFoo for getFoo.</summary>
        private (AccessorCode Code, string Member)? Accessor(OriginalMethod method, Func<uint, uint?> read)
        {
            var code = AccessorDecoder.Decode(method.Address!.Value, method.Size, read);
            if (!code.IsAccessor)
            {
                return null;
            }

            var candidates = _bodies[(method.ClassName, method.Name)].ToList();
            var body = candidates.FirstOrDefault(b => b.IsConst == method.IsConst) ?? (candidates.Count == 1 ? candidates[0] : null);
            var member = (body is null ? null : BodyAnalyzer.SingleMember(body)) ?? NameExtractor.SuggestMemberName(method.Name);
            return member is null ? null : (code, member);
        }

        /// <summary>Evidence per member of the class, from <paramref name="first"/> on, as both layouts place them.</summary>
        private static List<MemberTally> Tally(ClassLayout underComments, ClassLayout underComputation, MemberDecl first, IReadOnlyList<OffsetEvidence> evidence)
        {
            var computedOffsets = new Dictionary<MemberDecl, uint?>(ReferenceEqualityComparer.Instance);
            foreach (var field in underComputation.Fields.Where(f => f.Member is { Kind: MemberKind.Data }))
            {
                computedOffsets[field.Member!] = field.Offset;
            }

            var tallies = new List<MemberTally>();
            var started = false;
            foreach (var field in underComments.Fields)
            {
                if (field.Member is not { Kind: MemberKind.Data } member)
                {
                    continue;
                }

                started |= ReferenceEquals(member, first);
                if (!started)
                {
                    continue;
                }

                var forComputed = evidence.Where(e => e.Supports == EvidenceSupport.Computed && e.ComputedMember == member.Name).ToList();
                var forComment = evidence.Where(e => e.Supports == EvidenceSupport.Comment && e.CommentMember == member.Name).ToList();
                tallies.Add(new MemberTally(member.Name, field.Offset, computedOffsets.GetValueOrDefault(member), forComputed.Count, forComment.Count,
                    forComputed.Select(e => e.FunctionAddress).Distinct().Count(), forComment.Select(e => e.FunctionAddress).Distinct().Count()));
            }

            return tallies;
        }
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

    private enum Fit
    {
        Exact,
        Misfit,
        Unknown,
    }

    /// <summary>The rows of one layout, indexed by the bytes they cover, and where arrays and inline classes start.</summary>
    private sealed class Places
    {
        private readonly List<FieldRow>?[] _byByte;
        private readonly HashSet<uint> _aggregateStarts = [];
        private readonly List<(uint Start, uint Size)> _aggregates = [];
        private readonly bool _structureCopies;

        /// <param name="clipAt">
        /// For the comments' layout of an own conflict: the members before the conflict end where
        /// the comment says the next one starts, which is what the comment claims.
        /// </param>
        public Places(LayoutEngine engine, ClassLayout layout, CommentConflict? clipAt, bool structureCopies)
        {
            _structureCopies = structureCopies;
            var fields = ObjectFields.Build(engine, layout);
            End = fields.ReadSize;
            _byByte = new List<FieldRow>?[End];
            foreach (var row in fields.Rows)
            {
                var limit = clipAt is not null && row.Offset < clipAt.CommentOffset && row.Kind != RowKind.Gap ? clipAt.CommentOffset : End;
                Collect([row], limit);
            }
        }

        public uint End { get; }

        public (Fit Fit, string What, string? Member) Fit(uint offset, ThisAccess access)
        {
            if (offset >= End || _byByte[offset] is not { } covering)
            {
                return (Names.CommentOffsetCheck.Fit.Misfit, "nothing", null);
            }

            var member = Root(covering[0]);
            if (access.Width == 0)
            {
                // A member's address: an array or an inline class starting here fits; a scalar's address proves nothing.
                if (_aggregateStarts.Contains(offset))
                {
                    return (Names.CommentOffsetCheck.Fit.Exact, $"start of {AggregateAt(covering, offset)}", member);
                }

                return covering.Any(r => r.Offset == offset && r.Kind != RowKind.Gap)
                    ? (Names.CommentOffsetCheck.Fit.Unknown, Describe(covering[0], offset), member)
                    : (Names.CommentOffsetCheck.Fit.Misfit, Describe(covering[0], offset), member);
            }

            foreach (var row in covering)
            {
                if (Matches(row, offset, access))
                {
                    return (Names.CommentOffsetCheck.Fit.Exact, Describe(row, offset), Root(row));
                }
            }

            // Structures are copied word by word: a word at the start of a 4-byte structure fits it, and an
            // aligned word inside a larger one may be part of a copy, so it proves nothing.
            if (_structureCopies && access is { Width: 4, IsFloat: false })
            {
                if (_aggregates.Any(a => a.Start == offset && a.Size == 4))
                {
                    return (Names.CommentOffsetCheck.Fit.Exact, $"start of {AggregateAt(covering, offset)} (4-byte structure)", member);
                }

                if (_aggregates.Any(a => offset >= a.Start && offset + 4 <= a.Start + a.Size && (offset - a.Start) % 4 == 0))
                {
                    return (Names.CommentOffsetCheck.Fit.Unknown, Describe(covering[0], offset), member);
                }
            }

            // A member whose type is unknown could be anything, and so could bytes the header leaves
            // undocumented (undeclared members); only alignment padding is never read.
            return covering.Any(r => r.Kind == RowKind.Gap ? !r.IsPadding : r.Type is DataType.Opaque or DataType.Composite)
                ? (Names.CommentOffsetCheck.Fit.Unknown, Describe(covering[0], offset), member)
                : (Names.CommentOffsetCheck.Fit.Misfit, Describe(covering[0], offset), member);
        }

        private static string AggregateAt(List<FieldRow> covering, uint offset) =>
            covering.Select(r => r.Path).OrderBy(p => p.Length).First() is var path && path.LastIndexOf('.') is var dot and > 0 ? path[..dot] : path;

        private static string? Root(FieldRow row) =>
            row.Kind == RowKind.Gap ? null : row.Path.Split('.', '[')[0];

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
                if (row.Offset is not { } start || start >= limit)
                {
                    continue;
                }

                if (row.Type is DataType.Composite or DataType.ArrayOf && row.Kind != RowKind.Gap)
                {
                    _aggregateStarts.Add(start);
                    _aggregates.Add((start, Math.Min(row.Size, limit - start)));
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
