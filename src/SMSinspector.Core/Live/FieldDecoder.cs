using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SMSinspector.Core.Layouts;

namespace SMSinspector.Core.Live;

/// <summary>A decoded value as the grid shows it, with the plausibility flag it raised, if any.</summary>
public readonly record struct FieldValue(string Text, PlausibilityFlag? Flag);

/// <summary>Turns the bytes of one object into the values of its rows, big-endian.</summary>
public static class FieldDecoder
{
    private const int MaxInlineItems = 8;

    /// <param name="objectBytes">The object's bytes from its first byte on, as read by <see cref="ObjectFields.TryRead"/>.</param>
    public static FieldValue Decode(FieldRow row, ReadOnlySpan<byte> objectBytes, PlausibilitySettings settings)
    {
        if (row.Offset is not { } offset)
        {
            return new("offset unknown", null);
        }

        if ((ulong)offset + row.Size > (ulong)objectBytes.Length)
        {
            return new("not read", null);
        }

        var bytes = objectBytes.Slice((int)offset, (int)row.Size);
        if (row.Kind == RowKind.Gap)
        {
            return new(Convert.ToHexString(bytes), null);
        }

        if (row.IsBitField)
        {
            return DecodeBitField(row, bytes);
        }

        switch (row.Type)
        {
            case DataType.Scalar scalar when bytes.Length == scalar.Size:
                return DecodeScalar(row, scalar, bytes, settings);

            case DataType.Pointer:
            {
                var value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
                return new(value == 0 ? "null" : $"0x{value:X8}", Plausibility.CheckPointer(value, settings));
            }

            case DataType.Enumeration when bytes.Length is 1 or 2 or 4 or 8:
            {
                var value = ReadSigned(bytes);
                var name = row.EnumNames is { } names && names.TryGetValue(value, out var found) ? $" ({found})" : "";
                return new($"{value}{name}", null);
            }

            case DataType.ArrayOf { Element: DataType.Scalar { Size: 1, Kind: ScalarKind.Signed } } when row.TypeName.StartsWith("char", StringComparison.Ordinal):
                return new(Text(bytes), null);

            case DataType.Composite or DataType.ArrayOf:
                return Summary(row, objectBytes, settings);

            default:
                return new(Convert.ToHexString(bytes), null);
        }
    }

    private static FieldValue DecodeScalar(FieldRow row, DataType.Scalar scalar, ReadOnlySpan<byte> bytes, PlausibilitySettings settings)
    {
        switch (scalar.Kind)
        {
            case ScalarKind.Float when scalar.Size == 4:
            {
                var value = BinaryPrimitives.ReadSingleBigEndian(bytes);
                return new(value.ToString("R", CultureInfo.InvariantCulture), Plausibility.CheckFloat(value, settings));
            }

            case ScalarKind.Float:
                return new(BinaryPrimitives.ReadDoubleBigEndian(bytes).ToString("R", CultureInfo.InvariantCulture), null);

            case ScalarKind.Bool:
            {
                var value = ReadUnsigned(bytes);
                return new(value switch { 0 => "false", 1 => "true", _ => value.ToString(CultureInfo.InvariantCulture) }, Plausibility.CheckBool(value));
            }

            case ScalarKind.Unsigned:
            {
                var value = ReadUnsigned(bytes);
                var text = value < 10 ? value.ToString(CultureInfo.InvariantCulture) : $"{value} (0x{value.ToString($"X{bytes.Length * 2}", CultureInfo.InvariantCulture)})";
                return new(text, !row.HasUnknownName && row.Name is { } name && Plausibility.IsCounterName(name.Value) ? Plausibility.CheckUnsigned(value, scalar.Size) : null);
            }

            default:
                return new(ReadSigned(bytes).ToString(CultureInfo.InvariantCulture), null);
        }
    }

    private static FieldValue DecodeBitField(FieldRow row, ReadOnlySpan<byte> unit)
    {
        if (unit.Length is not (1 or 2 or 4 or 8) || row.BitOffset is not { } first || row.BitWidth is not { } width || width == 0)
        {
            return new(Convert.ToHexString(unit), null);
        }

        // Bits are numbered from the most significant one, as Metrowerks lays them out.
        var shift = unit.Length * 8 - first - width;
        var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        var raw = (ReadUnsigned(unit) >> shift) & mask;
        if (row.Type is DataType.Scalar { Kind: ScalarKind.Signed } && width < 64 && (raw >> (width - 1) & 1) != 0)
        {
            return new(((long)raw - (1L << width)).ToString(CultureInfo.InvariantCulture), null);
        }

        return new(raw.ToString(CultureInfo.InvariantCulture), null);
    }

    /// <summary>A one-line view of an inline class or array: its first few values, and the first flag among them.</summary>
    private static FieldValue Summary(FieldRow row, ReadOnlySpan<byte> objectBytes, PlausibilitySettings settings)
    {
        var children = row.Children;
        var text = new StringBuilder(row.Type is DataType.ArrayOf ? "[" : "(");
        PlausibilityFlag? flag = null;
        var shown = 0;
        foreach (var child in children)
        {
            if (child.Kind == RowKind.Gap)
            {
                continue;
            }

            if (shown == MaxInlineItems || child.HasChildren)
            {
                text.Append(shown == 0 ? "" : ", ").Append("...");
                break;
            }

            var value = Decode(child, objectBytes, settings);
            text.Append(shown == 0 ? "" : ", ").Append(value.Text);
            flag ??= value.Flag;
            shown++;
        }

        // Flags further in the array still count: one bad element is enough to look.
        if (flag is null)
        {
            foreach (var child in children)
            {
                if (child.Kind != RowKind.Gap && !child.HasChildren && Decode(child, objectBytes, settings).Flag is { } later)
                {
                    flag = later;
                    break;
                }
            }
        }

        return new(text.Append(row.Type is DataType.ArrayOf ? "]" : ")").ToString(), flag);
    }

    private static string Text(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        var text = new StringBuilder("\"");
        foreach (var b in end < 0 ? bytes : bytes[..end])
        {
            text.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }

        return text.Append('"').ToString();
    }

    private static ulong ReadUnsigned(ReadOnlySpan<byte> bytes) => bytes.Length switch
    {
        1 => bytes[0],
        2 => BinaryPrimitives.ReadUInt16BigEndian(bytes),
        4 => BinaryPrimitives.ReadUInt32BigEndian(bytes),
        8 => BinaryPrimitives.ReadUInt64BigEndian(bytes),
        _ => 0,
    };

    private static long ReadSigned(ReadOnlySpan<byte> bytes) => bytes.Length switch
    {
        1 => (sbyte)bytes[0],
        2 => BinaryPrimitives.ReadInt16BigEndian(bytes),
        4 => BinaryPrimitives.ReadInt32BigEndian(bytes),
        8 => BinaryPrimitives.ReadInt64BigEndian(bytes),
        _ => 0,
    };
}
