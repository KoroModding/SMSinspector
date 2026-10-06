using System.Text.RegularExpressions;

namespace SMSinspector.Core.Layouts;

/// <summary>
/// Computes class layouts from the parsed headers, for JP and PAL.
/// </summary>
/// <remarks>
/// <para>
/// JP: offset comments in the decomp describe the JP build. The engine walks each class,
/// computing every offset from the sizes and alignments of what precedes it, and compares
/// with the comment. The comment always wins as the placed offset; the comparison tells
/// how far the size model can be trusted.
/// </para>
/// <para>
/// PAL: a class untouched by version blocks (in itself, its bases or its member types)
/// keeps the JP offsets. Otherwise the engine walks it again with the PAL members.
/// Comments inside PAL-only blocks are PAL offsets and are checked. A member present in
/// both versions moves by the shift accumulated so far, which is exact as long as the
/// shift keeps its alignment; when it does not, the offset is recomputed from the
/// preceding sizes, but only if the JP walk verified that same position. Anything less
/// certain is withheld and the class says from which offset.
/// </para>
/// </remarks>
public sealed partial class LayoutEngine(TypeCatalog catalog)
{
    private static readonly Dictionary<string, (uint Size, uint Align)> Builtins = new(StringComparer.Ordinal)
    {
        ["char"] = (1, 1), ["signed char"] = (1, 1), ["unsigned char"] = (1, 1), ["bool"] = (1, 1),
        ["short"] = (2, 2), ["unsigned short"] = (2, 2), ["wchar_t"] = (2, 2),
        ["int"] = (4, 4), ["unsigned int"] = (4, 4), ["long"] = (4, 4), ["unsigned long"] = (4, 4),
        ["long long"] = (8, 8), ["unsigned long long"] = (8, 8), ["__int64"] = (8, 8), ["unsigned __int64"] = (8, 8),
        ["float"] = (4, 4), ["double"] = (8, 8), ["long double"] = (8, 8),
    };

    private const uint PointerSize = 4;

    private readonly Dictionary<(string Key, VersionMask Version), ClassLayout?> _cache = [];
    private readonly HashSet<(string Key, VersionMask Version)> _inProgress = [];
    private readonly Dictionary<string, PalContradiction> _palContradictions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PalSuspect> _palSuspects = new(StringComparer.Ordinal);

    public TypeCatalog Catalog => catalog;

    /// <summary>Evidence from outside the headers that a class's PAL layout is wrong from some offset on.</summary>
    public IReadOnlyCollection<PalContradiction> PalContradictions => _palContradictions.Values;

    /// <summary>Hints that a class's PAL layout is wrong somewhere; they mark the layout, offsets stay.</summary>
    public IReadOnlyCollection<PalSuspect> PalSuspects => _palSuspects.Values;

    /// <summary>Replaces the PAL suspects; the layouts concerned are computed again on the next request.</summary>
    public void SetPalSuspects(IEnumerable<PalSuspect> suspects)
    {
        foreach (var name in _palSuspects.Keys)
        {
            _cache.Remove((name, VersionMask.Pal));
        }

        _palSuspects.Clear();
        foreach (var suspect in suspects)
        {
            _palSuspects[suspect.ClassName] = suspect;
            _cache.Remove((suspect.ClassName, VersionMask.Pal));
        }
    }

    /// <summary>
    /// Replaces the PAL contradictions. Each one withholds the PAL offsets of its class from
    /// <see cref="PalContradiction.FirstOffset"/> on, with its reason; the layouts concerned
    /// are computed again on the next request.
    /// </summary>
    public void SetPalContradictions(IEnumerable<PalContradiction> contradictions)
    {
        foreach (var name in _palContradictions.Keys)
        {
            _cache.Remove((name, VersionMask.Pal));
        }

        _palContradictions.Clear();
        foreach (var contradiction in contradictions)
        {
            // Several contradictions in one class: the earliest offset wins.
            if (!_palContradictions.TryGetValue(contradiction.ClassName, out var known) || contradiction.FirstOffset < known.FirstOffset)
            {
                _palContradictions[contradiction.ClassName] = contradiction;
            }

            _cache.Remove((contradiction.ClassName, VersionMask.Pal));
        }
    }

    /// <summary>Layout of a non-template class by qualified name, or null if unknown or absent from this version.</summary>
    public ClassLayout? GetLayout(string qualifiedName, VersionMask version)
    {
        var decl = PickDefinition(catalog.FindClass(qualifiedName), version);
        return decl is null ? null : GetLayout(decl, version, Bindings.Empty);
    }

    public ClassLayout? GetLayout(ClassDecl decl, VersionMask version) => GetLayout(decl, version, Bindings.Empty);

    /// <summary>
    /// Layout of the class a member is declared with, looked up from the class that declares
    /// it, so nested types resolve ("TNode_" inside a list). Pointers and arrays are stripped:
    /// for <c>TFoo* mFoo[4]</c> this is TFoo. Null for a type that is not a class or depends on
    /// a template parameter.
    /// </summary>
    public ClassLayout? GetMemberTypeLayout(ClassLayout owner, FieldLayout field, VersionMask version) =>
        field.Member is { } member && !member.Type.IsFunctionPointer
            ? ResolveClassLayout(member.Type with { PointerDepth = 0, IsReference = false, ArrayDims = null, IsConst = false }, owner.Decl.QualifiedName, Bindings.Empty, version)
            : null;

    /// <summary>Layout of a class type, template instances included: "TBox&lt;float&gt;" and "TBox&lt;f32&gt;" give the same one.</summary>
    public ClassLayout? GetLayout(TypeSpec type, VersionMask version) =>
        type.IsPointerLike || type.Dims.Count > 0 ? null : ResolveClassLayout(type, "", Bindings.Empty, version);

    /// <summary>Size and alignment of a type as written in <paramref name="scope"/>.</summary>
    public bool TrySize(TypeSpec type, string scope, VersionMask version, out uint size, out uint align, out string? problem) =>
        TrySize(type, scope, Bindings.Empty, version, out size, out align, out problem);

    private sealed record Bindings(IReadOnlyDictionary<string, TypeSpec> Types, IReadOnlyDictionary<string, long> Values)
    {
        public static readonly Bindings Empty = new(new Dictionary<string, TypeSpec>(), new Dictionary<string, long>());

        public bool IsEmpty => Types.Count == 0 && Values.Count == 0;
    }

    private ClassLayout? GetLayout(ClassDecl decl, VersionMask version, Bindings bindings, string? displayName = null)
    {
        if ((decl.Versions & version) == 0)
        {
            return null;
        }

        var name = displayName ?? decl.QualifiedName;
        var key = (name, version);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (!_inProgress.Add(key))
        {
            return null;
        }

        try
        {
            ClassLayout layout;
            if (version == VersionMask.Pal && decl.Versions.HasFlag(VersionMask.Jp) && !IsAffected(decl, bindings))
            {
                // Untouched by version blocks: PAL is the JP layout as it is.
                var jp = GetLayout(decl, VersionMask.Jp, bindings, displayName);
                layout = jp is null ? Walk(decl, version, bindings, name, null) : CopyForPal(jp);
            }
            else if (version == VersionMask.Pal)
            {
                var jp = decl.Versions.HasFlag(VersionMask.Jp) ? GetLayout(decl, VersionMask.Jp, bindings, displayName) : null;
                layout = Walk(decl, version, bindings, name, jp);
                layout.IsVersionAffected = true;
            }
            else
            {
                layout = Walk(decl, version, bindings, name, null);
            }

            if (version == VersionMask.Pal && _palContradictions.TryGetValue(name, out var contradiction))
            {
                ApplyContradiction(layout, contradiction);
            }

            if (version == VersionMask.Pal && _palSuspects.TryGetValue(name, out var suspect))
            {
                layout.PalSuspect = suspect;
            }

            _cache[key] = layout;
            return layout;
        }
        finally
        {
            _inProgress.Remove(key);
        }
    }

    private static void ApplyContradiction(ClassLayout layout, PalContradiction contradiction)
    {
        var first = layout.Fields.FirstOrDefault(f => f.Offset >= contradiction.FirstOffset);
        var lastTrusted = layout.Fields.Where(f => f.Offset < contradiction.FirstOffset).Max(f => f.Offset) ?? layout.BasesEnd ?? 0;
        var reason = contradiction.Reason;

        for (var i = 0; i < layout.Fields.Count; i++)
        {
            if (layout.Fields[i].Offset >= contradiction.FirstOffset)
            {
                layout.Fields[i] = layout.Fields[i] with { Offset = null, Source = OffsetSource.Unknown, CommentVerified = false };
            }
        }

        // An earlier withholding by the engine stays; the new evidence is still recorded.
        if (layout.PalUnverifiedAfter is null || lastTrusted < layout.PalUnverifiedAfter)
        {
            layout.PalUnverifiedAfter = lastTrusted;
            layout.PalUnverifiedReason = reason;
        }

        layout.Size = null;
        layout.NonVirtualSize = null;
        layout.Issues.Add(new LayoutIssue(IssueKind.PalUnverified, first?.Name ?? "", $"PAL offsets unverified after 0x{lastTrusted:X}: {reason}"));
    }

    private static ClassLayout CopyForPal(ClassLayout jp)
    {
        var copy = new ClassLayout
        {
            Identity = jp.Identity,
            Decl = jp.Decl,
            Version = VersionMask.Pal,
            Size = jp.Size,
            NonVirtualSize = jp.NonVirtualSize,
            Align = jp.Align,
            HasVptr = jp.HasVptr,
            VptrOffset = jp.VptrOffset,
            BasesEnd = jp.BasesEnd,
            CommentsChecked = jp.CommentsChecked,
            CommentsMatched = jp.CommentsMatched,
            BoundTypes = jp.BoundTypes,
            BoundValues = jp.BoundValues,
        };
        copy.Bases.AddRange(jp.Bases);
        copy.VirtualBases.AddRange(jp.VirtualBases);
        copy.Fields.AddRange(jp.Fields);
        copy.Issues.AddRange(jp.Issues);
        return copy;
    }

    /// <summary>
    /// Whether the PAL layout can differ from JP: version-specific members, or a base or
    /// member type whose size differs between the versions.
    /// </summary>
    private bool IsAffected(ClassDecl decl, Bindings bindings)
    {
        if (decl.Members.Any(m => m.Versions != VersionMask.Both))
        {
            return true;
        }

        foreach (var spec in decl.Bases)
        {
            if (SizeDiffers(spec.Type, decl.Scope, bindings))
            {
                return true;
            }
        }

        return decl.Members.Any(m => m.Kind == MemberKind.Data && SizeDiffers(m.Type, decl.QualifiedName, bindings));
    }

    private bool SizeDiffers(TypeSpec type, string scope, Bindings bindings)
    {
        if (type.IsPointerLike)
        {
            return false;
        }

        var jp = TrySize(type, scope, bindings, VersionMask.Jp, out var jpSize, out _, out _);
        var pal = TrySize(type, scope, bindings, VersionMask.Pal, out var palSize, out _, out _);
        return jp != pal || jpSize != palSize;
    }

    private ClassLayout Walk(ClassDecl decl, VersionMask version, Bindings bindings, string name, ClassLayout? jp)
    {
        var layout = new ClassLayout
        {
            Identity = new SourcedName(name, Provenance.Header(decl.File, decl.Line, name == decl.QualifiedName ? "" : $"template instance {name}")),
            Decl = decl,
            Version = version,
            BoundTypes = bindings.Types,
            BoundValues = bindings.Values,
        };
        var isPal = version == VersionMask.Pal;
        var isUnion = decl.Kind == ClassKind.Union;
        // Keyed by reference: two identical declaration lines are still two members.
        var jpFields = new Dictionary<MemberDecl, FieldLayout>(ReferenceEqualityComparer.Instance);
        foreach (var field in jp?.Fields ?? [])
        {
            if (field.Member is not null)
            {
                jpFields[field.Member] = field;
            }
        }

        uint? cursor = 0;
        uint align = 1;
        uint unionSize = 0;

        var directVirtualBases = new List<ClassLayout>();
        foreach (var spec in decl.Bases)
        {
            var baseLayout = ResolveClassLayout(spec.Type, decl.Scope, bindings, version);
            if (baseLayout is null)
            {
                layout.Issues.Add(new LayoutIssue(IssueKind.UnknownBase, spec.Type.BaseName, $"Base {spec.Type.BaseName} could not be laid out."));
                cursor = null;
                continue;
            }

            foreach (var inherited in baseLayout.VirtualBases)
            {
                AddVirtualBase(inherited);
            }

            if (spec.IsVirtual)
            {
                // Laid out once at the end of the complete object; this class keeps a pointer to it.
                directVirtualBases.Add(baseLayout);
                AddVirtualBase(baseLayout);
                continue;
            }

            var baseOffset = cursor is { } c ? AlignUp(c, baseLayout.Align) : (uint?)null;
            layout.Bases.Add(new BaseLayout(baseLayout, baseOffset));
            align = Math.Max(align, baseLayout.Align);
            if (baseLayout.HasVptr && !layout.HasVptr)
            {
                layout.HasVptr = true;
                layout.VptrOffset = baseOffset + baseLayout.VptrOffset;
            }

            // An empty base takes no room in the derived class.
            var baseSize = IsEmpty(baseLayout) ? 0 : baseLayout.NonVirtualSize;
            cursor = baseOffset + baseSize;
        }

        foreach (var virtualBase in directVirtualBases)
        {
            var offset = cursor is { } c ? AlignUp(c, PointerSize) : (uint?)null;
            layout.Fields.Add(new FieldLayout(null, Hidden($"vbase {virtualBase.Name}", $"pointer to the virtual base {virtualBase.Name} of {name}"), "void*", offset, PointerSize, PointerSize,
                offset is null ? OffsetSource.Unknown : OffsetSource.Computed, null, false));
            align = Math.Max(align, PointerSize);
            cursor = offset + PointerSize;
        }

        layout.BasesEnd = cursor;

        // PAL: how far PAL positions are ahead of JP ones, while no version difference intervenes.
        long? shift = isPal && jp is not null && cursor is { } palStart && jp.BasesEnd is { } jpStart ? palStart - (long)jpStart : null;
        if (isPal && jp is not null && shift is null && layout.Bases.All(b => b.Layout.Version == VersionMask.Pal && !b.Layout.IsVersionAffected))
        {
            shift = 0;
        }

        var needsOwnVptr = decl.DeclaresVirtual && !layout.HasVptr;
        var markerIndex = decl.Members.FindIndex(m => m.Kind == MemberKind.VtableMarker && (m.Versions & version) != 0);
        var vptrIndex = needsOwnVptr ? (markerIndex >= 0 ? markerIndex : decl.FirstVirtualAfterMembers ?? decl.Members.Count) : -1;
        var withheld = false;
        var lastTrusted = cursor;

        // The storage unit consecutive bit-fields of the same size share.
        (uint Start, uint Size, int Used)? bitUnit = null;

        for (var i = 0; i <= decl.Members.Count; i++)
        {
            if (i == vptrIndex)
            {
                PlaceVptr();
            }

            if (i == decl.Members.Count)
            {
                break;
            }

            var member = decl.Members[i];
            if ((member.Versions & version) == 0)
            {
                // A member of the other version only: from here PAL and JP diverge.
                shift = null;
                continue;
            }

            switch (member.Kind)
            {
                case MemberKind.VtableMarker:
                    if (IsNativeComment(member, version) && layout.VptrOffset is { } vptr && vptr != member.CommentOffset)
                    {
                        layout.Issues.Add(new LayoutIssue(IssueKind.VtableMismatch, "vtable",
                            $"Vtable pointer computed at 0x{vptr:X}, comment says 0x{member.CommentOffset:X}."));
                    }

                    continue;
                case MemberKind.OtherMarker:
                    continue;
            }

            PlaceMember(member);
        }

        if (isUnion)
        {
            cursor = unionSize;
        }

        foreach (var virtualBase in layout.VirtualBases)
        {
            align = Math.Max(align, virtualBase.Align);
        }

        layout.Align = align;
        layout.NonVirtualSize = cursor is { } end ? AlignUp(Math.Max(end, IsEmptyDecl(decl, layout) ? 1u : 0u), align) : null;

        // The complete object ends with each virtual base, once.
        var complete = layout.NonVirtualSize;
        foreach (var virtualBase in layout.VirtualBases)
        {
            complete = complete is { } c && virtualBase.NonVirtualSize is { } vbSize ? AlignUp(c, virtualBase.Align) + vbSize : null;
        }

        layout.Size = complete is { } total ? AlignUp(total, align) : null;

        if (version == VersionMask.Jp || jp is null)
        {
            CheckCommentOrder(layout);
        }

        return layout;

        void AddVirtualBase(ClassLayout virtualBase)
        {
            if (!layout.VirtualBases.Any(v => v.Name == virtualBase.Name))
            {
                layout.VirtualBases.Add(virtualBase);
            }
        }

        void PlaceVptr()
        {
            var marker = markerIndex >= 0 ? decl.Members[markerIndex] : null;
            var markerNative = marker is not null && IsNativeComment(marker, version);
            uint? offset = cursor is { } c ? AlignUp(c, PointerSize) : null;
            if (offset is null && markerNative)
            {
                offset = marker!.CommentOffset;
            }

            if (offset is null && isPal && jp?.VptrOffset is { } jpVptr && shift is { } s && s % PointerSize == 0)
            {
                offset = (uint)(jpVptr + s);
            }

            layout.HasVptr = true;
            layout.VptrOffset = offset;
            layout.Fields.Add(new FieldLayout(null, Hidden("vtable", $"vtable pointer of {name}"), "void*", offset, PointerSize, PointerSize,
                offset is null ? OffsetSource.Unknown : OffsetSource.Computed, marker?.CommentOffset,
                markerNative && cursor is not null && offset == marker!.CommentOffset));
            align = Math.Max(align, PointerSize);
            cursor = offset + PointerSize;
        }

        // An anonymous union or struct is a member without a name; it gets a label, not a game name.
        SourcedName Declared(MemberDecl member) => member.Name.Length > 0
            ? new(member.Name, Provenance.Header(decl.File, member.Line))
            : new("(anonymous)", Provenance.Header(decl.File, member.Line, $"anonymous {member.Type}"));

        SourcedName Hidden(string fieldName, string detail) => new(fieldName, Provenance.Compiler(decl.File, decl.Line, detail));

        void PlaceBitField(MemberDecl member)
        {
            var sized = TrySize(member.Type, decl.QualifiedName, bindings, version, out var size, out var unitAlign, out _) && size is 1 or 2 or 4 or 8;
            var width = Evaluate(member.BitWidth!, decl.QualifiedName, bindings, version);
            if (!sized || width is not { } bits || bits < 0 || bits > size * 8)
            {
                layout.Issues.Add(new LayoutIssue(IssueKind.UnknownType, member.Name, $"Bit-field {member.Name} : {member.BitWidth} could not be laid out."));
                layout.Fields.Add(new FieldLayout(member, Declared(member), member.Type.ToString(), null, null, 1, OffsetSource.Unknown, member.CommentOffset, false));
                cursor = null;
                bitUnit = null;
                return;
            }

            if (bits == 0)
            {
                // ": 0" closes the current storage unit.
                bitUnit = null;
                return;
            }

            uint? offset;
            int bitOffset;
            if (bitUnit is { } unit && unit.Size == size && unit.Used + bits <= size * 8)
            {
                offset = unit.Start;
                bitOffset = unit.Used;
                bitUnit = unit with { Used = unit.Used + (int)bits };
            }
            else if (cursor is { } c)
            {
                offset = AlignUp(c, unitAlign);
                bitOffset = 0;
                bitUnit = (offset.Value, size, (int)bits);
                cursor = offset + size;
            }
            else
            {
                offset = null;
                bitOffset = 0;
            }

            var verified = false;
            if (IsNativeComment(member, version) && offset is { } placed)
            {
                layout.CommentsChecked++;
                verified = placed == member.CommentOffset;
                if (verified)
                {
                    layout.CommentsMatched++;
                }
                else
                {
                    layout.Issues.Add(new LayoutIssue(member.CommentOffset > placed ? IssueKind.Gap : IssueKind.Overlap, member.Name,
                        $"Bit-field comment 0x{member.CommentOffset:X} differs from the computed storage unit at 0x{placed:X}."));
                }
            }

            align = Math.Max(align, unitAlign);
            layout.Fields.Add(new FieldLayout(member, Declared(member), member.Type.ToString(), offset, size, unitAlign,
                offset is null ? OffsetSource.Unknown : OffsetSource.Computed, member.CommentOffset, verified, bitOffset, (int)bits));
        }

        void PlaceMember(MemberDecl member)
        {
            if (member.IsBitField && !isUnion)
            {
                PlaceBitField(member);
                return;
            }

            bitUnit = null;
            var sized = !member.IsBitField & TrySize(member.Type, decl.QualifiedName, bindings, version, out var size, out var memberAlign, out var problem);
            if (member.AlignAttribute is { } forced && forced > memberAlign)
            {
                memberAlign = forced;
            }

            if (!sized)
            {
                layout.Issues.Add(new LayoutIssue(IssueKind.UnknownType, member.Name,
                    member.IsBitField ? "Bit-fields in unions are not laid out." : problem ?? $"Unknown type {member.Type}."));
            }

            uint? computed = isUnion ? 0 : cursor is { } c && sized ? AlignUp(c, memberAlign) : null;
            var comment = member.CommentOffset;
            var native = IsNativeComment(member, version);
            uint? offset;
            var source = OffsetSource.Computed;
            var verified = false;

            if (native)
            {
                if (computed is { } expected && !isUnion)
                {
                    layout.CommentsChecked++;
                    if (expected == comment)
                    {
                        layout.CommentsMatched++;
                        verified = true;
                    }
                    else
                    {
                        var kind = comment > expected ? IssueKind.Gap : IssueKind.Overlap;
                        layout.Issues.Add(new LayoutIssue(kind, member.Name, kind == IssueKind.Gap
                            ? $"Comment 0x{comment:X} is 0x{comment - expected:X} bytes after the computed 0x{expected:X}."
                            : $"Comment 0x{comment:X} is 0x{expected - comment:X} bytes before the computed 0x{expected:X}."));
                    }
                }

                offset = comment;
                source = OffsetSource.Comment;
                if (isPal && member.Versions != VersionMask.Both)
                {
                    shift = null;
                }
            }
            else if (isPal && jp is not null && member.Versions == VersionMask.Both)
            {
                offset = PalOffset(member, computed, sized, memberAlign);
                if (offset is not null && offset == comment)
                {
                    // Unmoved: the JP comment holds for PAL too, as verified as it was in JP.
                    source = OffsetSource.Comment;
                    verified = jpFields.GetValueOrDefault(member)?.CommentVerified ?? false;
                }
            }
            else
            {
                offset = computed;
                if (offset is null)
                {
                    source = OffsetSource.Unknown;
                }
                else if (isPal && member.Versions == VersionMask.Pal && withheld)
                {
                    offset = null;
                    source = OffsetSource.Unknown;
                }
            }

            if (offset is null)
            {
                source = OffsetSource.Unknown;
            }

            layout.Fields.Add(new FieldLayout(member, Declared(member), member.Type.ToString(), offset, sized ? size : null,
                sized ? memberAlign : 1, source, comment, verified));

            if (sized)
            {
                align = Math.Max(align, memberAlign);
            }

            if (isUnion)
            {
                unionSize = Math.Max(unionSize, sized ? size : 0);
                return;
            }

            cursor = offset is { } placed && sized ? placed + size : null;
            if (offset is not null)
            {
                lastTrusted = offset;
            }

            if (isPal && sized && SizeDiffers(member.Type, decl.QualifiedName, bindings))
            {
                shift = null;
            }
        }

        uint? PalOffset(MemberDecl member, uint? computed, bool sized, uint memberAlign)
        {
            var jpField = jpFields.GetValueOrDefault(member);
            var jpOffset = jpField?.Offset;

            if (withheld || jpOffset is null)
            {
                return Withhold(member);
            }

            // No divergence since the last placed member, and the shift keeps this member's alignment.
            if (shift is { } s && (sized ? s % memberAlign == 0 : s % 8 == 0))
            {
                return (uint)(jpOffset + s);
            }

            // After a divergence: recompute, if the JP walk proved the same position.
            if (computed is { } expected && jpField!.CommentVerified)
            {
                shift = expected - (long)jpOffset;
                return expected;
            }

            return Withhold(member);
        }

        uint? Withhold(MemberDecl member)
        {
            if (!withheld)
            {
                withheld = true;
                layout.PalUnverifiedAfter = lastTrusted;
                layout.PalUnverifiedReason = $"{member.Name} cannot be placed safely.";
                layout.Issues.Add(new LayoutIssue(IssueKind.PalUnverified, member.Name,
                    $"PAL offsets unverified after 0x{lastTrusted:X}: {member.Name} cannot be placed safely."));
            }

            return null;
        }
    }

    /// <summary>
    /// A comment describes the version it was written for: members outside version blocks
    /// and in JP-only blocks carry JP offsets, members in PAL-only blocks carry PAL offsets.
    /// </summary>
    private static bool IsNativeComment(MemberDecl member, VersionMask version) =>
        member.CommentOffset is not null && (version == VersionMask.Jp
            ? member.Versions != VersionMask.Pal
            : member.Versions == VersionMask.Pal);

    private static bool IsEmpty(ClassLayout layout) =>
        !layout.HasVptr && layout.Fields.Count == 0 && layout.VirtualBases.Count == 0 && layout.Bases.All(b => IsEmpty(b.Layout));

    private static bool IsEmptyDecl(ClassDecl decl, ClassLayout layout) => IsEmpty(layout);

    private static void CheckCommentOrder(ClassLayout layout)
    {
        uint? previous = null;
        string? previousName = null;
        foreach (var field in layout.Fields)
        {
            if (field.Member is null || field.Source != OffsetSource.Comment || field.Offset is not { } offset || layout.Decl.Kind == ClassKind.Union)
            {
                continue;
            }

            if (previous is { } p && offset < p)
            {
                layout.Issues.Add(new LayoutIssue(IssueKind.OutOfOrder, field.Name,
                    $"Comment 0x{offset:X} comes after {previousName} at 0x{p:X}."));
            }

            previous = offset;
            previousName = field.Name;
        }
    }

    private bool TrySize(TypeSpec type, string scope, Bindings bindings, VersionMask version, out uint size, out uint align, out string? problem)
    {
        size = 0;
        align = 1;
        problem = null;

        long count = 1;
        foreach (var dim in type.Dims)
        {
            // "T data[];" at the end of a struct takes no room.
            if (dim.Length == 0)
            {
                count = 0;
                continue;
            }

            if (Evaluate(dim, scope, bindings, version) is not { } value || value < 0)
            {
                problem = $"Array size '{dim}' of {type} could not be evaluated.";
                return false;
            }

            count *= value;
        }

        if (!TrySizeElement(type.WithoutDims(), scope, bindings, version, out var elementSize, out align, out problem, 0))
        {
            return false;
        }

        size = (uint)(elementSize * count);
        return true;
    }

    private bool TrySizeElement(TypeSpec type, string scope, Bindings bindings, VersionMask version, out uint size, out uint align, out string? problem, int depth)
    {
        size = 0;
        align = 1;
        problem = null;

        if (depth > 32)
        {
            problem = $"Type {type} nests too deeply.";
            return false;
        }

        if (type.IsPointerLike)
        {
            size = align = PointerSize;
            return true;
        }

        if (bindings.Types.TryGetValue(type.Name, out var bound) && type.TemplateArgs.Count == 0)
        {
            return TrySize(Compose(type, bound), "", Bindings.Empty, version, out size, out align, out problem);
        }

        if (Builtins.TryGetValue(type.Name, out var builtin))
        {
            (size, align) = builtin;
            return true;
        }

        if (type.Name == "void")
        {
            problem = "void has no size.";
            return false;
        }

        switch (catalog.Resolve(type.Name, scope))
        {
            case ResolvedName.Typedef typedef:
            {
                var target = Compose(type, typedef.Declaration.Target);
                return TrySize(target, typedef.Declaration.Scope, Bindings.Empty, version, out size, out align, out problem);
            }

            case ResolvedName.Enum enumDecl:
                if (enumDecl.Declaration.UnderlyingType is { } underlying)
                {
                    return TrySizeElement(underlying, enumDecl.Declaration.Scope, Bindings.Empty, version, out size, out align, out problem, depth + 1);
                }

                // Metrowerks makes enums int-sized by default.
                size = align = 4;
                return true;

            case ResolvedName.Class cls:
            {
                var layout = ResolveInstance(cls, type.TemplateArgs, scope, bindings, version);
                if (layout?.Size is not { } classSize)
                {
                    problem = layout is null ? $"Class {type.BaseName} could not be laid out." : $"Size of {type.BaseName} is unknown.";
                    return false;
                }

                size = classSize;
                align = layout.Align;
                return true;
            }

            default:
                problem = $"Unknown type {type.BaseName}.";
                return false;
        }
    }

    /// <summary>Applies the pointers and arrays written at the use site to the type a name stands for.</summary>
    private static TypeSpec Compose(TypeSpec use, TypeSpec target)
    {
        if (use.PointerDepth > 0 || use.IsReference || use.IsFunctionPointer)
        {
            // A pointer to whatever the name stands for: its own dimensions belong to the pointee.
            return target with { PointerDepth = target.PointerDepth + use.PointerDepth, IsReference = use.IsReference, ArrayDims = use.ArrayDims };
        }

        var dims = use.Dims.Concat(target.Dims).ToList();
        return target with { ArrayDims = dims.Count > 0 ? dims : null };
    }

    private ClassLayout? ResolveClassLayout(TypeSpec type, string scope, Bindings bindings, VersionMask version)
    {
        if (bindings.Types.TryGetValue(type.Name, out var bound) && type.TemplateArgs.Count == 0)
        {
            return ResolveClassLayout(bound, "", Bindings.Empty, version);
        }

        return catalog.Resolve(type.Name, scope) switch
        {
            ResolvedName.Class cls => ResolveInstance(cls, type.TemplateArgs, scope, bindings, version),
            ResolvedName.Typedef typedef when !typedef.Declaration.Target.IsPointerLike =>
                ResolveClassLayout(typedef.Declaration.Target, typedef.Declaration.Scope, Bindings.Empty, version),
            _ => null,
        };
    }

    private ClassLayout? ResolveInstance(ResolvedName.Class cls, IReadOnlyList<TemplateArg> args, string scope, Bindings bindings, VersionMask version)
    {
        if (args.Count == 0)
        {
            var decl = PickDefinition(cls.Declarations, version);
            return decl is null ? null : GetLayout(decl, version, Bindings.Empty);
        }

        var canonical = args.Select(a => CanonicalArg(a, scope, bindings, version)).ToList();

        // Fill omitted arguments from the primary template's defaults, which may refer to earlier parameters.
        var template = cls.Declarations.FirstOrDefault(d => d.IsTemplate && (d.Versions & version) != 0);
        if (template is not null && canonical.Count < template.TemplateParams.Count)
        {
            for (var i = canonical.Count; i < template.TemplateParams.Count; i++)
            {
                if (i >= template.TemplateParamDefaults.Count || template.TemplateParamDefaults[i] is not { } fallback)
                {
                    break;
                }

                var earlier = new Dictionary<string, TypeSpec>(StringComparer.Ordinal);
                for (var j = 0; j < i; j++)
                {
                    if (canonical[j].Type is { } t)
                    {
                        earlier[template.TemplateParams[j]] = t;
                    }
                }

                canonical.Add(CanonicalArg(fallback, template.Scope, new Bindings(earlier, new Dictionary<string, long>()), version));
            }
        }

        var key = string.Join(", ", canonical.Select(a => a.Text));

        // An explicit specialization for exactly these arguments wins over the primary template.
        foreach (var candidate in cls.Declarations)
        {
            if (candidate.SpecializationArgs is { } specArgs && specArgs.Count == canonical.Count
                && (candidate.Versions & version) != 0
                && string.Join(", ", specArgs.Select(a => CanonicalArg(a, candidate.Scope, Bindings.Empty, version).Text)) == key)
            {
                return GetLayout(candidate, version, Bindings.Empty, $"{cls.QualifiedName}<{key}>");
            }
        }

        var primary = cls.Declarations.FirstOrDefault(d => d.IsTemplate && (d.Versions & version) != 0);
        if (primary is null || primary.TemplateParams.Count < canonical.Count)
        {
            return null;
        }

        var types = new Dictionary<string, TypeSpec>(StringComparer.Ordinal);
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < canonical.Count; i++)
        {
            if (canonical[i].Type is { } t)
            {
                types[primary.TemplateParams[i]] = t;
            }
            else if (long.TryParse(canonical[i].Text, out var v))
            {
                values[primary.TemplateParams[i]] = v;
            }
        }

        return GetLayout(primary, version, new Bindings(types, values), $"{cls.QualifiedName}<{key}>");
    }

    /// <summary>
    /// Rewrites a template argument with fully qualified, typedef-free names so that
    /// "TVec3&lt;f32&gt;" and "TVec3&lt;float&gt;" meet on the same instance.
    /// </summary>
    private TemplateArg CanonicalArg(TemplateArg arg, string scope, Bindings bindings, VersionMask version)
    {
        if (arg.Type is null)
        {
            return Evaluate(arg.Text, scope, bindings, version) is { } value
                ? new TemplateArg(null, value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : arg;
        }

        var type = Canonical(arg.Type, scope, bindings, 0);
        return new TemplateArg(type, type.ToString());
    }

    private TypeSpec Canonical(TypeSpec type, string scope, Bindings bindings, int depth)
    {
        if (depth > 16)
        {
            return type;
        }

        if (bindings.Types.TryGetValue(type.Name, out var bound) && type.TemplateArgs.Count == 0)
        {
            return Compose(type, bound) with { IsConst = type.IsConst || bound.IsConst };
        }

        if (Builtins.ContainsKey(type.Name) || type.Name == "void")
        {
            return type;
        }

        switch (catalog.Resolve(type.Name, scope))
        {
            case ResolvedName.Typedef typedef:
                return Canonical(Compose(type, typedef.Declaration.Target), typedef.Declaration.Scope, Bindings.Empty, depth + 1)
                    with { IsConst = type.IsConst || typedef.Declaration.Target.IsConst };
            case ResolvedName.Class cls:
                return type with
                {
                    Name = cls.QualifiedName,
                    TemplateArgs = type.TemplateArgs.Select(a => a.Type is { } t
                        ? new TemplateArg(Canonical(t, scope, bindings, depth + 1), Canonical(t, scope, bindings, depth + 1).ToString())
                        : a).ToList(),
                };
            case ResolvedName.Enum enumDecl:
                return type with { Name = enumDecl.Declaration.QualifiedName };
            default:
                return type;
        }
    }

    private long? Evaluate(string expression, string scope, Bindings bindings, VersionMask version)
    {
        var text = expression;
        if (!bindings.IsEmpty)
        {
            text = IdentifierPattern().Replace(text, m => bindings.Values.TryGetValue(m.Value, out var v)
                ? v.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : m.Value);
        }

        return catalog.Evaluate(text, scope, version);
    }

    /// <summary>Among same-named declarations, the one defined for this version (not a template).</summary>
    private static ClassDecl? PickDefinition(IReadOnlyList<ClassDecl> decls, VersionMask version) =>
        decls.Where(d => (d.Versions & version) != 0 && d.SpecializationArgs is null && !d.IsTemplate)
            .OrderByDescending(d => d.Members.Count + d.Bases.Count)
            .FirstOrDefault();

    private static uint AlignUp(uint value, uint align) => align <= 1 ? value : (value + align - 1) / align * align;

    [GeneratedRegex(@"\b[A-Za-z_]\w*\b")]
    private static partial Regex IdentifierPattern();
}
