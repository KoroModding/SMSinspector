using SMSinspector.Core.Layouts;
using SMSinspector.Core.Memory;
using SMSinspector.Core.Names;

namespace SMSinspector.Core.Live;

/// <summary>
/// The field grid of one class: every row in memory order, bases first, with the bytes no
/// member covers shown as gaps, and how many bytes one read of an object takes.
/// </summary>
public sealed class ObjectFields
{
    private ObjectFields(ClassLayout layout, IReadOnlyList<FieldRow> rows, uint readSize)
    {
        Layout = layout;
        Rows = rows;
        ReadSize = readSize;
    }

    public ClassLayout Layout { get; }

    /// <summary>Top-level rows; rows whose offset is withheld come last.</summary>
    public IReadOnlyList<FieldRow> Rows { get; }

    /// <summary>
    /// Bytes read per object: the class size, or the end of the last placed member when the
    /// size is unknown (withheld PAL offsets are not read).
    /// </summary>
    public uint ReadSize { get; }

    public static ObjectFields Build(LayoutEngine engine, ClassLayout layout)
    {
        var builder = new Builder(engine, layout.Version);
        var rows = builder.Rows(layout, 0, 0, "", null, out var end);
        return new ObjectFields(layout, rows, end);
    }

    /// <summary>Reads one object with a single memory read. <paramref name="destination"/> must hold <see cref="ReadSize"/> bytes.</summary>
    public bool TryRead(IGameMemory memory, uint address, Span<byte> destination) =>
        destination.Length >= ReadSize && memory.TryRead(address, destination[..(int)ReadSize]);

    private sealed class Builder(LayoutEngine engine, VersionMask version)
    {
        private const uint GapLine = 16;

        private readonly Dictionary<EnumDecl, IReadOnlyDictionary<long, string>> _enumNames = new(ReferenceEqualityComparer.Instance);

        public List<FieldRow> Rows(ClassLayout layout, uint origin, int depth, string prefix, string? inheritedNote, out uint end)
        {
            var placed = new List<FieldRow>();
            var withheld = new List<FieldRow>();
            foreach (var flat in layout.Flatten())
            {
                var row = Row(flat, origin, depth, prefix, inheritedNote);
                (row.Offset is null ? withheld : placed).Add(row);
            }

            end = layout.Size is { } size ? origin + size : placed.Select(r => r.Offset!.Value + r.Size).DefaultIfEmpty(origin).Max();

            var result = new List<FieldRow>(placed.Count + withheld.Count);
            var cursor = origin;
            foreach (var row in placed)
            {
                AddGap(result, cursor, Math.Min(row.Offset!.Value, end), row.Align, depth, prefix, inheritedNote);
                result.Add(row);
                cursor = Math.Max(cursor, row.Offset!.Value + row.Size);
            }

            AddGap(result, cursor, end, layout.Align, depth, prefix, inheritedNote);
            result.AddRange(withheld);
            end -= origin;
            return result;
        }

        private FieldRow Row(FlatField flat, uint origin, int depth, string prefix, string? inheritedNote)
        {
            var (owner, field) = (flat.Owner, flat.Field);
            var type = engine.DescribeField(owner, field);
            var spec = engine.DeclaredType(owner, field);
            var size = field.Size ?? type.Size;
            return new FieldRow
            {
                Kind = RowKind.Field,
                Name = field.Identity,
                Label = field.Name,
                Path = prefix.Length == 0 ? field.Name : $"{prefix}.{field.Name}",
                Offset = origin + flat.AbsoluteOffset,
                Size = size,
                TypeName = spec?.ToString() ?? field.TypeName,
                Type = type,
                Align = field.Align,
                BitOffset = field.BitOffset,
                BitWidth = field.BitWidth,
                Depth = depth,
                Note = Note(owner, field, flat.AbsoluteOffset, size) ?? inheritedNote,
                HasUnknownName = field.Member is not null && NameExtractor.IsUnknownName(field.Name),
                EnumNames = type is DataType.Enumeration e ? EnumNames(e.Declaration) : null,
                Spec = spec,
                Expand = Expand,
            };
        }

        private IReadOnlyList<FieldRow> Expand(FieldRow row) => row.Type switch
        {
            DataType.Composite composite => Rows(composite.Layout, row.Offset!.Value, row.Depth + 1, row.Path, row.Note, out _),
            DataType.ArrayOf array => Elements(row, array),
            _ => [],
        };

        private List<FieldRow> Elements(FieldRow row, DataType.ArrayOf array)
        {
            var spec = row.Spec is { Dims.Count: > 0 } s ? s with { ArrayDims = s.Dims.Count > 1 ? s.Dims.Skip(1).ToList() : null } : null;
            var typeName = spec?.ToString() ?? Name(array.Element);
            var enumNames = array.Element is DataType.Enumeration e ? EnumNames(e.Declaration) : null;
            var elements = new List<FieldRow>((int)array.Count);
            for (uint i = 0; i < array.Count; i++)
            {
                elements.Add(new FieldRow
                {
                    Kind = RowKind.Element,
                    Name = row.Name,
                    Label = $"[{i}]",
                    Path = $"{row.Path}[{i}]",
                    Offset = row.Offset + i * array.Element.Size,
                    Size = array.Element.Size,
                    TypeName = typeName,
                    Type = array.Element,
                    Depth = row.Depth + 1,
                    Note = row.Note,
                    HasUnknownName = row.HasUnknownName,
                    EnumNames = enumNames,
                    Spec = spec,
                    Expand = Expand,
                });
            }

            return elements;
        }

        /// <param name="nextAlign">The alignment of what follows: a gap it explains is padding.</param>
        private static void AddGap(List<FieldRow> rows, uint from, uint to, uint nextAlign, int depth, string prefix, string? note)
        {
            var isPadding = nextAlign > 1 && to - from < nextAlign && to % nextAlign == 0;
            // Split on 16-byte lines of the object, as a hex view would.
            for (var at = from; at < to;)
            {
                var next = Math.Min(to, (at / GapLine + 1) * GapLine);
                rows.Add(new FieldRow
                {
                    Kind = RowKind.Gap,
                    Label = "(gap)",
                    Path = prefix.Length == 0 ? $"(gap 0x{at:X})" : $"{prefix}.(gap 0x{at:X})",
                    Offset = at,
                    Size = next - at,
                    TypeName = $"{next - at} bytes",
                    Type = new DataType.Opaque(next - at),
                    Depth = depth,
                    Note = note,
                    IsPadding = isPadding,
                });
                at = next;
            }
        }

        private static string? Note(ClassLayout owner, FieldLayout field, uint? absolute, uint size)
        {
            if (owner.PalSuspect is { } suspect && field.Offset is { } offset
                && offset <= suspect.LastOffset && offset + Math.Max(size, 1) > suspect.FirstOffset)
            {
                return $"PAL suspect 0x{suspect.FirstOffset:X}..0x{suspect.LastOffset:X}: {suspect.Reason}";
            }

            if (owner.CascadeCheck is { IsSettled: true } cascade && field.Offset is { } placed
                && owner.Fields.FirstOrDefault(f => ReferenceEquals(f.Member, cascade.Member))?.Offset is { } memberOffset && placed >= memberOffset)
            {
                return ReferenceEquals(field.Member, cascade.Member) && !cascade.IsBase ? cascade.RowNote() : cascade.AfterNote();
            }

            if (owner.CommentCheck is { IsSettled: true } check && field.Offset >= check.Conflict.FirstOffset)
            {
                return check.RowNote();
            }

            if (absolute is null)
            {
                return owner.PalUnverifiedAfter is { } after
                    ? $"PAL offsets unverified after 0x{after:X}: {owner.PalUnverifiedReason}"
                    : "Offset unknown: the size of a preceding member could not be determined.";
            }

            return null;
        }

        private IReadOnlyDictionary<long, string> EnumNames(EnumDecl decl)
        {
            if (_enumNames.TryGetValue(decl, out var known))
            {
                return known;
            }

            var names = new Dictionary<long, string>();
            foreach (var item in decl.Enumerators)
            {
                if ((item.Versions & version) != 0
                    && engine.Catalog.ResolveConstant(TypeCatalog.Combine(decl.QualifiedName, item.Name), "", version) is { } value)
                {
                    names.TryAdd(value, item.Name);
                }
            }

            _enumNames[decl] = names;
            return names;
        }

        /// <summary>A type name for an element whose declaration gives none, such as an array typedef.</summary>
        private static string Name(DataType type) => type switch
        {
            DataType.Scalar { Kind: ScalarKind.Float } f => f.Size == 4 ? "float" : "double",
            DataType.Scalar { Kind: ScalarKind.Bool } => "bool",
            DataType.Scalar s => $"{(s.Kind == ScalarKind.Signed ? "signed" : "unsigned")} {s.Size * 8}-bit",
            DataType.Pointer { Target: { } target } => $"{target}*",
            DataType.Pointer => "function pointer",
            DataType.Enumeration e => e.Declaration.QualifiedName,
            DataType.Composite c => c.Layout.Name,
            DataType.ArrayOf a => $"{Name(a.Element)}[{a.Count}]",
            _ => $"{type.Size} bytes",
        };
    }
}
