using System.Buffers.Binary;

namespace SMSinspector.Core.Tests.Fakes;

/// <summary>Builds small DOL files and PowerPC instructions for the tests. Nothing here comes from a game.</summary>
internal static class TestDol
{
    public const uint Blr = 0x4E800020;

    /// <summary>A DOL with one text section holding <paramref name="words"/> at <paramref name="address"/>.</summary>
    public static byte[] Build(uint address, params uint[] words)
    {
        var bytes = new byte[0x100 + words.Length * 4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x00), 0x100);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x48), address);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x90), (uint)words.Length * 4);
        for (var i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x100 + i * 4), words[i]);
        }

        return bytes;
    }

    /// <summary>A D-form instruction: opcode, target or source register, base register, displacement.</summary>
    public static uint DForm(uint opcode, uint register, uint baseRegister, short displacement) =>
        opcode << 26 | register << 21 | baseRegister << 16 | (ushort)displacement;

    public static uint Lwz(uint rt, short d, uint ra = 3) => DForm(32, rt, ra, d);

    public static uint Lhz(uint rt, short d, uint ra = 3) => DForm(40, rt, ra, d);

    public static uint Lbz(uint rt, short d, uint ra = 3) => DForm(34, rt, ra, d);

    public static uint Lfs(uint frt, short d, uint ra = 3) => DForm(48, frt, ra, d);

    public static uint Stw(uint rs, short d, uint ra = 3) => DForm(36, rs, ra, d);

    public static uint Stfs(uint frs, short d, uint ra = 3) => DForm(52, frs, ra, d);

    public static uint Addi(uint rt, uint ra, short value) => DForm(14, rt, ra, value);
}
