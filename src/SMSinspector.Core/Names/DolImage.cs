using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace SMSinspector.Core.Names;

/// <summary>
/// A GameCube executable (.dol): up to 7 text and 11 data sections, each copied from a
/// file offset to a load address. The 0x100-byte header holds, in order, the file offsets,
/// the load addresses and the sizes of all 18 sections, then the BSS and the entry point.
/// </summary>
public sealed class DolImage
{
    public const int HeaderSize = 0x100;
    public const int SectionCount = 18;
    public const int TextSectionCount = 7;

    private readonly byte[] _bytes;
    private readonly DolSection[] _sections;

    private DolImage(byte[] bytes, DolSection[] sections)
    {
        _bytes = bytes;
        _sections = sections;
    }

    public IReadOnlyList<DolSection> Sections => _sections;

    public static bool TryParse(byte[] bytes, [NotNullWhen(true)] out DolImage? image, out string error)
    {
        image = null;
        if (bytes.Length < HeaderSize)
        {
            error = "The file is smaller than a DOL header.";
            return false;
        }

        var sections = new List<DolSection>();
        for (var i = 0; i < SectionCount; i++)
        {
            var fileOffset = ReadWord(bytes, i * 4);
            var address = ReadWord(bytes, 0x48 + i * 4);
            var size = ReadWord(bytes, 0x90 + i * 4);
            if (size == 0)
            {
                continue;
            }

            if ((ulong)fileOffset + size > (ulong)bytes.Length)
            {
                error = $"Section {i} runs past the end of the file.";
                return false;
            }

            sections.Add(new DolSection(i < TextSectionCount, fileOffset, address, size));
        }

        image = new DolImage(bytes, [.. sections]);
        error = "";
        return true;
    }

    /// <summary>Reads a big-endian word at a game address. Fails outside the loaded sections.</summary>
    public bool TryReadWord(uint address, out uint value)
    {
        foreach (var section in _sections)
        {
            if (address >= section.Address && address - section.Address <= section.Size - 4)
            {
                value = ReadWord(_bytes, (int)(section.FileOffset + (address - section.Address)));
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static uint ReadWord(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
}

public sealed record DolSection(bool IsText, uint FileOffset, uint Address, uint Size);
