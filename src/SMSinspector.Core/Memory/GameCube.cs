namespace SMSinspector.Core.Memory;

/// <summary>
/// Fixed facts about the GameCube address space as the game sees it.
/// </summary>
public static class GameCube
{
    /// <summary>Start of cached MEM1, where the game's pointers live.</summary>
    public const uint Mem1Base = 0x80000000;

    /// <summary>Size of MEM1 on retail hardware: 24 MiB.</summary>
    public const uint Mem1Size = 0x01800000;

    /// <summary>One past the last cached MEM1 address.</summary>
    public const uint Mem1End = Mem1Base + Mem1Size;

    /// <summary>True when <paramref name="address"/> is inside cached MEM1.</summary>
    public static bool IsMem1Address(uint address) => address is >= Mem1Base and < Mem1End;

    /// <summary>
    /// True when the whole range starting at <paramref name="address"/> and spanning
    /// <paramref name="length"/> bytes is inside cached MEM1.
    /// </summary>
    public static bool IsMem1Range(uint address, int length) =>
        length >= 0 && IsMem1Address(address) && (ulong)address + (ulong)length <= Mem1End;

    /// <summary>Offset of a MEM1 address from the start of MEM1.</summary>
    public static uint ToMem1Offset(uint address) => address - Mem1Base;
}
