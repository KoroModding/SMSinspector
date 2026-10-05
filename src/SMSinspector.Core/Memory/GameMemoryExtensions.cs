using System.Buffers.Binary;

namespace SMSinspector.Core.Memory;

/// <summary>
/// Big-endian typed reads on top of <see cref="IGameMemory"/>. The Try forms never
/// allocate; the plain forms throw <see cref="GameMemoryReadException"/> on failure.
/// </summary>
public static class GameMemoryExtensions
{
    public static bool TryReadU8(this IGameMemory memory, uint address, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        var ok = memory.TryRead(address, buffer);
        value = ok ? buffer[0] : default;
        return ok;
    }

    public static bool TryReadS8(this IGameMemory memory, uint address, out sbyte value)
    {
        var ok = memory.TryReadU8(address, out var raw);
        value = unchecked((sbyte)raw);
        return ok;
    }

    public static bool TryReadU16(this IGameMemory memory, uint address, out ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        var ok = memory.TryRead(address, buffer);
        value = ok ? BinaryPrimitives.ReadUInt16BigEndian(buffer) : default;
        return ok;
    }

    public static bool TryReadS16(this IGameMemory memory, uint address, out short value)
    {
        Span<byte> buffer = stackalloc byte[2];
        var ok = memory.TryRead(address, buffer);
        value = ok ? BinaryPrimitives.ReadInt16BigEndian(buffer) : default;
        return ok;
    }

    public static bool TryReadU32(this IGameMemory memory, uint address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        var ok = memory.TryRead(address, buffer);
        value = ok ? BinaryPrimitives.ReadUInt32BigEndian(buffer) : default;
        return ok;
    }

    public static bool TryReadS32(this IGameMemory memory, uint address, out int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        var ok = memory.TryRead(address, buffer);
        value = ok ? BinaryPrimitives.ReadInt32BigEndian(buffer) : default;
        return ok;
    }

    public static bool TryReadF32(this IGameMemory memory, uint address, out float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        var ok = memory.TryRead(address, buffer);
        value = ok ? BinaryPrimitives.ReadSingleBigEndian(buffer) : default;
        return ok;
    }

    public static bool TryReadF64(this IGameMemory memory, uint address, out double value)
    {
        Span<byte> buffer = stackalloc byte[8];
        var ok = memory.TryRead(address, buffer);
        value = ok ? BinaryPrimitives.ReadDoubleBigEndian(buffer) : default;
        return ok;
    }

    /// <summary>
    /// Reads a 32-bit value and accepts it only if it points inside MEM1.
    /// Null pointers and garbage both return false.
    /// </summary>
    public static bool TryReadPointer(this IGameMemory memory, uint address, out uint pointer) =>
        memory.TryReadU32(address, out pointer) && GameCube.IsMem1Address(pointer);

    public static void Read(this IGameMemory memory, uint address, Span<byte> destination)
    {
        if (!memory.TryRead(address, destination))
        {
            throw new GameMemoryReadException(address, destination.Length);
        }
    }

    public static byte ReadU8(this IGameMemory memory, uint address) =>
        memory.TryReadU8(address, out var v) ? v : throw new GameMemoryReadException(address, 1);

    public static ushort ReadU16(this IGameMemory memory, uint address) =>
        memory.TryReadU16(address, out var v) ? v : throw new GameMemoryReadException(address, 2);

    public static short ReadS16(this IGameMemory memory, uint address) =>
        memory.TryReadS16(address, out var v) ? v : throw new GameMemoryReadException(address, 2);

    public static uint ReadU32(this IGameMemory memory, uint address) =>
        memory.TryReadU32(address, out var v) ? v : throw new GameMemoryReadException(address, 4);

    public static int ReadS32(this IGameMemory memory, uint address) =>
        memory.TryReadS32(address, out var v) ? v : throw new GameMemoryReadException(address, 4);

    public static float ReadF32(this IGameMemory memory, uint address) =>
        memory.TryReadF32(address, out var v) ? v : throw new GameMemoryReadException(address, 4);

    public static double ReadF64(this IGameMemory memory, uint address) =>
        memory.TryReadF64(address, out var v) ? v : throw new GameMemoryReadException(address, 8);
}

public sealed class GameMemoryReadException(uint address, int length)
    : Exception($"Could not read {length} byte(s) at 0x{address:X8}.")
{
    public uint Address { get; } = address;

    public int Length { get; } = length;
}
