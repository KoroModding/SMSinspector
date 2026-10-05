using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace SMSinspector.Core.Memory;

/// <summary>
/// The start of MEM1, where the disc header of the running game is copied at boot.
/// It identifies the game and proves that a host memory region really is MEM1.
/// </summary>
public static class Mem1Header
{
    /// <summary>Bytes to read from the start of MEM1 to validate it.</summary>
    public const int Length = 0x20;

    /// <summary>Offset of the 6 ASCII bytes of the game ID (for example "GMSP01").</summary>
    public const int GameIdOffset = 0x00;

    public const int GameIdLength = 6;

    /// <summary>Offset of the disc revision byte.</summary>
    public const int RevisionOffset = 0x07;

    /// <summary>Offset of the GameCube disc magic word.</summary>
    public const int MagicOffset = 0x1C;

    public const uint Magic = 0xC2339F3D;

    /// <summary>
    /// Validates the magic word and reads the game ID. Fails when the magic is wrong
    /// or the game ID is not 6 printable ASCII characters.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> header, [NotNullWhen(true)] out string? gameId, out byte revision)
    {
        gameId = null;
        revision = 0;

        if (header.Length < Length
            || BinaryPrimitives.ReadUInt32BigEndian(header[MagicOffset..]) != Magic)
        {
            return false;
        }

        var idBytes = header.Slice(GameIdOffset, GameIdLength);
        foreach (var b in idBytes)
        {
            if (b is < 0x21 or > 0x7E)
            {
                return false;
            }
        }

        gameId = Encoding.ASCII.GetString(idBytes);
        revision = header[RevisionOffset];
        return true;
    }
}
