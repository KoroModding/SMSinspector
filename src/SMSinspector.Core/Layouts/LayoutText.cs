using System.Text;

namespace SMSinspector.Core.Layouts;

/// <summary>Plain-text rendering of a class layout, for diagnostics and reports.</summary>
public static class LayoutText
{
    public static string Describe(ClassLayout layout)
    {
        var text = new StringBuilder();
        var version = layout.Version == VersionMask.Pal ? "PAL" : "JP";
        text.AppendLine($"{layout.Name} ({version}), size {Hex(layout.Size)}, {layout.Decl.File}:{layout.Decl.Line}");
        text.AppendLine($"{"offset",-8} {"size",-6} {"type",-34} {"name",-30} note");

        uint? previousEnd = 0;
        ClassLayout? previousOwner = null;
        foreach (var flat in layout.Flatten())
        {
            var field = flat.Field;
            var offset = flat.AbsoluteOffset;

            if (offset is { } start && previousEnd is { } end && start > end && field.BitOffset is null or 0)
            {
                // Bytes that an alignment explains (the next field's, or the end of a base class)
                // are padding; anything more is undocumented.
                var isPadding = AlignUp(end, field.Align) == start
                    || (previousOwner is not null && !ReferenceEquals(previousOwner, flat.Owner) && AlignUp(end, previousOwner.Align) == start);
                text.AppendLine($"{Hex(end),-8} {Hex(start - end),-6} {(isPadding ? "(padding)" : "(gap)"),-34}");
            }

            previousOwner = flat.Owner;

            text.AppendLine($"{Hex(offset),-8} {Hex(field.Size),-6} {Clip(field.TypeName, 34),-34} {Clip(field.Name, 30),-30} {Note(layout, flat)}");

            if (offset is { } placed && field.Size is { } size)
            {
                previousEnd = Math.Max(previousEnd ?? 0, placed + size);
            }
            else if (offset is null)
            {
                previousEnd = null;
            }
        }

        if (layout.PalUnverifiedAfter is { } after)
        {
            text.AppendLine(layout.PalUnverifiedReason is { } reason
                ? $"PAL offsets unverified after 0x{after:X}: {reason}"
                : $"PAL offsets unverified after 0x{after:X}.");
        }

        if (layout.PalSuspect is { } suspect)
        {
            text.AppendLine($"PAL suspect at 0x{suspect.FirstOffset:X}..0x{suspect.LastOffset:X}: {suspect.Reason}");
        }

        if (layout.CommentCheck is { } check)
        {
            text.AppendLine($"Offset comment check: {check.Summary()}");
        }

        foreach (var issue in layout.Issues.Where(i => i.Kind is not IssueKind.PalUnverified))
        {
            text.AppendLine($"note: {issue.Kind} {issue.Member}: {issue.Message}");
        }

        return text.ToString();
    }

    private static string Note(ClassLayout layout, FlatField flat)
    {
        var field = flat.Field;
        var parts = new List<string>();
        if (!ReferenceEquals(flat.Owner, layout))
        {
            parts.Add(flat.Owner.Name);
        }

        if (field.BitWidth is { } width)
        {
            parts.Add($"bits {field.BitOffset}..{field.BitOffset + width - 1}");
        }

        if (field.Member?.Versions == VersionMask.Pal)
        {
            parts.Add("PAL only");
        }

        parts.Add(field.Source switch
        {
            OffsetSource.Comment when field.CommentVerified => "comment, verified",
            OffsetSource.Comment => "comment",
            OffsetSource.Computed when field.CommentOffset is { } c && field.Offset != c && flat.Owner.Version == VersionMask.Pal => $"moved from JP 0x{c:X}",
            OffsetSource.Computed => "computed",
            _ => "unknown",
        });

        return string.Join(", ", parts);
    }

    private static uint AlignUp(uint value, uint align) => align <= 1 ? value : (value + align - 1) / align * align;

    private static string Clip(string text, int width) => text.Length <= width ? text : text[..(width - 3)] + "...";

    private static string Hex(uint? value) => value is { } v ? $"0x{v:X}" : "?";
}
